using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupGalaxyServer;

/// <summary>
/// The "always on host". Every player has exactly one WebRTC data channel to this server (star topology),
/// and this class decides who receives each SupGalaxy data channel message:
///
///  * Plain relay   - chat, avatars, scores, etc. go to every other connected player.
///  * World scoped  - movement / combat / mob / block traffic only goes to players in the same world.
///  * Unicast       - any message with a "to" field is delivered to that one player only (used for
///                    proximity voice/video signaling between players and for authority replies).
///  * Authority     - SupGalaxy's game rules (chunk ownership, block health, drops, PvP damage, fish) live in
///                    the JavaScript host code. The server picks one connected player per world (the player who
///                    has been in the server the longest) as that world's "authority" and forwards rule-requests
///                    (request_block_place, block_hit, ...) to that player only. The authority's client runs its
///                    existing isHost code path and its results flow back through the server to everyone.
///  * Persisted     - block_change / batch_block_change / block_place / block_break and stone messages are
///                    applied to <see cref="WorldStateStore"/> so the world survives restarts and new players
///                    receive it with world_sync_start / world_sync_chunk when they enter a world.
///  * Imports       - Chunk Keyword / IPFS world updates a client discovered (ipfs_chunk_from_client_start/_chunk)
///                    are re-assembled per sender and transaction, validated, merged into <see cref="WorldStateStore"/>
///                    and fanned out as ipfs_chunk_update_start/_chunk to the other players in that world.
///
/// World-state updates and world sync snapshots are ordered by one gate: a snapshot is taken atomically with
/// starting the sync, and updates applied after it are held back per player until the snapshot has been sent.
///
/// See <c>ClientIntegrationNotes.cs</c> for the exact protocol the SupGalaxy client must follow.
/// </summary>
public sealed class GameRelay
{
    public const int ProtocolVersion = 1;

    /// <summary>Same chunk size SupGalaxy uses in sendWorldStateAsync.</summary>
    public const int SyncChunkSize = 131072;

    private const ulong SyncHighWaterMark = 1024 * 1024;
    private const ulong CongestedThreshold = 512 * 1024;

    /// <summary>Only delivered to players currently in the message's world.</summary>
    internal static readonly HashSet<string> WorldScopedTypes = new(StringComparer.Ordinal)
    {
        "player_move", "laser_fired", "laser_fired_batch", "item_dropped", "item_picked_up",
        "block_change", "block_damaged", "block_place", "block_break",
        "mob_update", "mob_update_batch", "mob_state_batch", "mob_spawn", "mob_despawn", "mob_hit", "mob_kill",
        "elite_mob_attack", "boulder_update", "volcano_event", "fish_spawn_command", "fish_spawn_remove",
        "player_attack", "player_death", "player_respawn",
    };

    /// <summary>High-frequency updates that are skipped for a player whose channel is backed up (newer ones follow).</summary>
    internal static readonly HashSet<string> DroppableTypes = new(StringComparer.Ordinal)
    {
        "player_move", "mob_update", "mob_update_batch", "boulder_update", "state_update",
    };

    /// <summary>Requests that need SupGalaxy's host game rules; routed to the world authority only.</summary>
    internal static readonly HashSet<string> AuthorityRequestTypes = new(StringComparer.Ordinal)
    {
        "request_block_place", "request_block_break", "request_block_toggle", "block_hit",
        "fish_spawn_request", "player_hit",
    };

    /// <summary>Messages only the server may originate. Dropped when a client sends them.</summary>
    internal static readonly HashSet<string> ServerOnlyTypes = new(StringComparer.Ordinal)
    {
        "new_player", "remove_peer", "world_sync", "world_sync_start", "world_sync_chunk",
        // Media renegotiation must happen peer-to-peer (see p2p_signal), the server connection is data only.
        "renegotiation_offer", "renegotiation_answer",
    };

    private readonly PlayerRegistry _players;
    private readonly WorldStateStore _worlds;
    private readonly ServerSettings _settings;
    private readonly Action<string>? _log;
    /// <summary>Messages that change persisted world state. Applied and delivered under <see cref="_stateGate"/>.</summary>
    internal static readonly HashSet<string> StateTypes = new(StringComparer.Ordinal)
    {
        "block_change", "batch_block_change", "block_place", "block_break",
        "magician_stone_placed", "calligraphy_stone_placed", "magician_stone_removed", "calligraphy_stone_removed",
    };

    public const string ImportStartType = "ipfs_chunk_from_client_start";
    public const string ImportChunkType = "ipfs_chunk_from_client_chunk";
    public const string ImportUpdateStartType = "ipfs_chunk_update_start";
    public const string ImportUpdateChunkType = "ipfs_chunk_update_chunk";
    public const string ImportResultType = "server_import_result";
    private const int MaxTransactionIdLength = 256;
    private const int MaxWorldNameLength = 256;

    private readonly object _authorityGate = new();
    private readonly object _stateGate = new();

    private sealed class ImportTransfer
    {
        public required string Sender { get; init; }
        public required string TransactionId { get; init; }
        public required string World { get; init; }
        public required string?[] Chunks { get; init; }
        public string? FromAddress { get; init; }
        public double? Timestamp { get; init; }
        public int Received;
        public long Chars;
        public long LastActivity;
    }

    private readonly object _importGate = new();
    private readonly Dictionary<(string Sender, string TransactionId), ImportTransfer> _imports = new();

    /// <summary>Monotonic clock in milliseconds, replaceable by tests.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Raised after an imported world update was merged into the store (world name).</summary>
    public event Action<string>? WorldImported;
    private readonly Dictionary<string, string> _authorities = new(StringComparer.Ordinal);

    // Recently relayed player messages. SupGalaxy's host code re-forwards messages it receives to its peers;
    // when the world authority does that through the server it would echo a duplicate to everyone, so the
    // authority's copy of a message the server relayed moments ago is dropped.
    private static readonly long EchoWindowMs = 3000;
    private readonly object _echoGate = new();
    private readonly Dictionary<string, (long Tick, string Sender)> _recent = new(StringComparer.Ordinal);
    private long _lastEchoPrune;

    public GameRelay(PlayerRegistry players, WorldStateStore worlds, ServerSettings settings, Action<string>? log = null)
    {
        _players = players;
        _worlds = worlds;
        _settings = settings;
        _log = log;
    }

    public string ServerName => _settings.ServerName;

    /// <summary>Raised whenever a world's authority changes (world, new authority or null).</summary>
    public event Action<string, string?>? AuthorityChanged;

    public string? GetAuthority(string world)
    {
        lock (_authorityGate) return _authorities.TryGetValue(world, out var a) ? a : null;
    }

    public bool IsAuthority(PlayerSession p)
    {
        lock (_authorityGate)
        {
            foreach (var a in _authorities.Values)
                if (string.Equals(a, p.Username, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Called once the player's data channel is open.</summary>
    public void OnPlayerJoined(PlayerSession p)
    {
        p.State = PlayerState.Connected;
        var others = _players.Connected().Where(o => !ReferenceEquals(o, p)).ToArray();

        p.Send(Json(new JsonObject
        {
            ["type"] = "server_welcome",
            ["serverName"] = ServerName,
            ["username"] = p.Username,
            ["protocol"] = ProtocolVersion,
            ["players"] = new JsonArray(others.Select(o => (JsonNode)JsonValue.Create(o.Username)!).ToArray()),
        }));

        // Same introductions the in-game host performs in setupDataChannel's onopen.
        var announce = Json(new JsonObject { ["type"] = "new_player", ["username"] = p.Username });
        foreach (var o in others)
        {
            p.Send(Json(new JsonObject { ["type"] = "new_player", ["username"] = o.Username }));
            o.Send(announce);
        }

        if (p.World != null)
        {
            var world = p.World;
            p.World = null;
            EnterWorld(p, world);
        }
    }

    /// <summary>Called after the player has been removed from the registry.</summary>
    public void OnPlayerLeft(PlayerSession p)
    {
        var msg = Json(new JsonObject { ["type"] = "remove_peer", ["username"] = p.Username });
        foreach (var o in _players.Connected())
            if (!ReferenceEquals(o, p)) o.Send(msg);

        List<string> worlds;
        lock (_authorityGate)
        {
            worlds = _authorities.Where(kv => string.Equals(kv.Value, p.Username, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
        }
        if (p.World != null && !worlds.Contains(p.World)) worlds.Add(p.World);
        foreach (var w in worlds) RecomputeAuthority(w, null);

        lock (_importGate)
        {
            foreach (var key in _imports.Keys.Where(k => k.Sender == p.Username.ToLowerInvariant()).ToList())
                _imports.Remove(key);
        }
    }

    public void HandleMessage(PlayerSession from, string raw)
    {
        if (from.State != PlayerState.Connected || raw.Length > _settings.MaxMessageSize) return;
        from.CountIn(raw.Length);

        bool isAuthority = IsAuthority(from);
        if (!from.TryConsumeRate(isAuthority ? _settings.MaxMessagesPerSecond * 4 : _settings.MaxMessagesPerSecond)) return;

        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (msg == null) return;

        var type = Str(msg, "type");
        if (type == null || ServerOnlyTypes.Contains(type) || type.StartsWith("server_", StringComparison.Ordinal)) return;

        // Imports are re-assembled and fanned out by the server; the raw client messages are never relayed.
        if (type == ImportStartType)
        {
            HandleImportStart(from, msg);
            return;
        }
        if (type == ImportChunkType)
        {
            HandleImportChunk(from, msg);
            return;
        }

        // Identity enforcement: a player can only speak for itself. A world authority legitimately sends
        // results on behalf of other players (e.g. block_break with username = breaker), so it is exempt.
        bool modified = false;
        if (!isAuthority && msg.ContainsKey("username") && Str(msg, "username") != from.Username)
        {
            msg["username"] = from.Username;
            modified = true;
        }

        // Compared in canonical form because the authority's JS re-serializes what it forwards.
        var canonical = msg.ToJsonString();
        if (isAuthority)
        {
            if (IsEcho(from, canonical)) return;
        }
        else
        {
            RememberRelayed(from, canonical);
        }

        if (StateTypes.Contains(type))
        {
            lock (_stateGate) Dispatch(from, msg, type, raw, canonical, modified, state: true);
        }
        else
        {
            Dispatch(from, msg, type, raw, canonical, modified, state: false);
        }
    }

    private void Dispatch(PlayerSession from, JsonObject msg, string type, string raw, string canonical, bool modified, bool state)
    {
        var world = Str(msg, "world");

        switch (type)
        {
            case "i_am_alive":
                return;

            // P2P bookkeeping for imports. Relaying it made other players mark the transaction as processed before
            // the server's ipfs_chunk_update_* fan-out arrived, so they dropped it. The server tracks merged
            // transactions itself and sends them as processedIds in world sync.
            case "processed_transaction_id":
            case "sync_processed_transaction":
                return;

            case "request_world_sync":
            {
                var w = world ?? from.World;
                if (w != null)
                {
                    lock (from.SyncedWorlds) from.SyncedWorlds.Add(w);
                    SendWorldSync(from, w);
                }
                return;
            }

            case "player_move":
                if (Num(msg, "x") is { } x) from.X = x;
                if (Num(msg, "y") is { } y) from.Y = y;
                if (Num(msg, "z") is { } z) from.Z = z;
                if (world != null && world != from.World) EnterWorld(from, world);
                break;

            case "block_change":
                PersistBlockChange(msg);
                break;

            case "batch_block_change":
                if (msg["messages"] is JsonArray batch)
                    foreach (var m in batch.OfType<JsonObject>()) PersistBlockChange(m);
                break;

            case "block_place":
                if (world != null && Long(msg, "x") is { } px && Long(msg, "y") is { } py && Long(msg, "z") is { } pz && Long(msg, "blockId") is { } pb)
                    _worlds.ApplyBlock(world, px, py, pz, pb, Str(msg, "originSeed"));
                break;

            case "block_break":
                if (world != null && Long(msg, "x") is { } bx && Long(msg, "y") is { } by && Long(msg, "z") is { } bz)
                    _worlds.ApplyBlock(world, bx, by, bz, Long(msg, "replacementBlockId") ?? 0, null, clearOrigin: Str(msg, "originSeed") != null);
                break;

            case "magician_stone_placed":
            case "calligraphy_stone_placed":
                if (from.World != null && msg["stoneData"] is JsonObject stone
                    && Num(stone, "x") is { } sx && Num(stone, "y") is { } sy && Num(stone, "z") is { } sz)
                {
                    var kind = type.StartsWith("magician", StringComparison.Ordinal) ? StoneKind.Magician : StoneKind.Calligraphy;
                    _worlds.SetStone(kind, world ?? from.World, $"{FormatJsNumber(sx)},{FormatJsNumber(sy)},{FormatJsNumber(sz)}", stone);
                }
                break;

            case "magician_stone_removed":
            case "calligraphy_stone_removed":
                if ((world ?? from.World) is { } sw && Str(msg, "key") is { } key)
                {
                    var kind = type.StartsWith("magician", StringComparison.Ordinal) ? StoneKind.Magician : StoneKind.Calligraphy;
                    _worlds.RemoveStone(kind, sw, key);
                }
                break;
        }

        if (modified) raw = canonical;

        // Unicast (p2p_signal for proximity voice/video, authority replies such as remove_from_inventory).
        var to = Str(msg, "to");
        if (to != null)
        {
            var target = _players.Get(to);
            if (target != null && !ReferenceEquals(target, from) && target.State == PlayerState.Connected) target.Send(raw);
            return;
        }

        if (AuthorityRequestTypes.Contains(type))
        {
            var w = world ?? from.World;
            var authority = w == null ? null : GetAuthority(w);
            if (authority != null && !string.Equals(authority, from.Username, StringComparison.OrdinalIgnoreCase))
                _players.Get(authority)?.Send(raw);
            return;
        }

        Broadcast(from, raw, WorldScopedTypes.Contains(type) ? world ?? from.World : null, DroppableTypes.Contains(type), state);
    }

    private void Broadcast(PlayerSession from, string raw, string? world, bool droppable, bool state)
    {
        foreach (var p in _players.Connected())
        {
            if (ReferenceEquals(p, from)) continue;
            if (world != null && p.World != null && p.World != world) continue;
            if (droppable && p.Transport is { } t && t.BufferedAmount > CongestedThreshold) continue;
            if (state) p.SendStateUpdate(raw);
            else p.Send(raw);
        }
    }

    private void HandleImportStart(PlayerSession from, JsonObject msg)
    {
        var tx = Str(msg, "transactionId");
        var world = Str(msg, "world") ?? from.World;
        var total = Long(msg, "total");
        long maxChunks = Math.Max(1, _settings.MaxImportSize / 1024);
        if (string.IsNullOrEmpty(tx) || tx.Length > MaxTransactionIdLength || string.IsNullOrEmpty(world) || world.Length > MaxWorldNameLength
            || total is not { } t || t < 1 || t > maxChunks || Num(msg, "total") != t)
        {
            _log?.Invoke($"Rejected world import start from {from.Username}: invalid transactionId, world or total.");
            if (!string.IsNullOrEmpty(tx) && tx.Length <= MaxTransactionIdLength)
                SendImportResult(from, tx, world, ok: false, "invalid_start", retry: false);
            return;
        }
        if (_worlds.IsImportProcessed(world, tx))
        {
            // Already merged: repeated delivery is a no-op.
            SendImportResult(from, tx, world, ok: true, "duplicate", retry: false);
            return;
        }

        var now = Clock();
        var key = (from.Username.ToLowerInvariant(), tx);
        lock (_importGate)
        {
            ExpireImportsLocked(now);
            if (_imports.ContainsKey(key)) return; // Duplicate start.
            var mine = _imports.Values.Where(v => v.Sender == key.Item1).ToList();
            if (mine.Count >= _settings.MaxPendingImportsPerPlayer)
            {
                _log?.Invoke($"Rejected world import {tx} from {from.Username}: too many unfinished imports ({mine.Count}).");
                SendImportResult(from, tx, world, ok: false, "busy", retry: true);
                return;
            }
            _imports[key] = new ImportTransfer
            {
                Sender = key.Item1,
                TransactionId = tx,
                World = world,
                Chunks = new string?[t],
                FromAddress = Str(msg, "fromAddress"),
                Timestamp = Num(msg, "timestamp"),
                LastActivity = now,
            };
        }
    }

    private void HandleImportChunk(PlayerSession from, JsonObject msg)
    {
        var tx = Str(msg, "transactionId");
        if (tx == null) return;
        var key = (from.Username.ToLowerInvariant(), tx);
        var now = Clock();
        ImportTransfer? complete = null;
        string? rejected = null;
        bool retry = false;
        string? world = null;
        lock (_importGate)
        {
            ExpireImportsLocked(now);
            if (!_imports.TryGetValue(key, out var transfer)) return; // Unknown, expired, rejected or already merged.
            world = transfer.World;

            var chunk = Str(msg, "chunk");
            var index = Long(msg, "index");
            var total = msg.ContainsKey("total") ? Long(msg, "total") : transfer.Chunks.Length;
            if (chunk == null || index is not { } i || Num(msg, "index") != i || i < 0 || i >= transfer.Chunks.Length || total != transfer.Chunks.Length)
            {
                _imports.Remove(key);
                _log?.Invoke($"Rejected world import {tx} from {from.Username}: malformed chunk.");
                rejected = "malformed_chunk";
            }
            else if (transfer.Chunks[i] is { } existing)
            {
                if (existing == chunk) return; // Duplicate chunk.
                _imports.Remove(key);
                _log?.Invoke($"Rejected world import {tx} from {from.Username}: conflicting copies of chunk {i}.");
                rejected = "conflicting_chunk";
                retry = true;
            }
            else if (transfer.Chars + chunk.Length > _settings.MaxImportSize)
            {
                _imports.Remove(key);
                _log?.Invoke($"Rejected world import {tx} from {from.Username}: larger than {_settings.MaxImportSize} characters.");
                rejected = "too_large";
            }
            else
            {
                long pending = _imports.Values.Where(v => v.Sender == key.Item1).Sum(v => v.Chars);
                if (pending + chunk.Length > _settings.MaxPendingImportCharsPerPlayer)
                {
                    _imports.Remove(key);
                    _log?.Invoke($"Rejected world import {tx} from {from.Username}: unfinished imports exceed {_settings.MaxPendingImportCharsPerPlayer} characters.");
                    rejected = "busy";
                    retry = true;
                }
                else
                {
                    transfer.Chunks[i] = chunk;
                    transfer.Chars += chunk.Length;
                    transfer.Received++;
                    transfer.LastActivity = now;
                    if (transfer.Received == transfer.Chunks.Length)
                    {
                        _imports.Remove(key);
                        complete = transfer;
                    }
                }
            }
        }
        if (rejected != null) SendImportResult(from, tx, world, ok: false, rejected, retry);
        if (complete != null) CompleteImport(from, complete);
    }

    private void CompleteImport(PlayerSession from, ImportTransfer transfer)
    {
        var payload = string.Concat(transfer.Chunks);
        var import = WorldImport.Parse(transfer.World, payload, out var error);
        if (import == null)
        {
            _log?.Invoke($"Rejected world import {transfer.TransactionId} from {from.Username}: {error}.");
            SendImportResult(from, transfer.TransactionId, transfer.World, ok: false, "malformed_payload", retry: false);
            return;
        }
        if (import.ForeignWorldChunks > 0 || import.SkippedEntries > 0)
            _log?.Invoke($"World import {transfer.TransactionId} from {from.Username}: ignored {import.ForeignWorldChunks} chunk(s) of other worlds and {import.SkippedEntries} invalid entr(y/ies).");

        int recipients = 0, blocks = 0;
        lock (_stateGate)
        {
            var applied = _worlds.ApplyImport(transfer.World, transfer.TransactionId, WorldImport.ComputeTruncatedDate(transfer.Timestamp), import);
            if (applied == null)
            {
                SendImportResult(from, transfer.TransactionId, transfer.World, ok: true, "duplicate", retry: false);
                return;
            }
            blocks = applied.Chunks.Sum(c => c.Changes.Count);
            if (!applied.IsEmpty)
            {
                var json = applied.ToJson();
                var messages = new List<string>();
                var start = new JsonObject
                {
                    ["type"] = ImportUpdateStartType,
                    ["world"] = transfer.World,
                    ["username"] = from.Username,
                    ["transactionId"] = transfer.TransactionId,
                    ["fromAddress"] = transfer.FromAddress,
                    ["timestamp"] = transfer.Timestamp,
                };
                var chunks = new List<string>();
                for (int i = 0; i < json.Length; i += SyncChunkSize)
                    chunks.Add(json.Substring(i, Math.Min(SyncChunkSize, json.Length - i)));
                start["total"] = chunks.Count;
                messages.Add(Json(start));
                for (int i = 0; i < chunks.Count; i++)
                {
                    messages.Add(Json(new JsonObject
                    {
                        ["type"] = ImportUpdateChunkType,
                        ["world"] = transfer.World,
                        ["username"] = from.Username,
                        ["transactionId"] = transfer.TransactionId,
                        ["index"] = i,
                        ["chunk"] = chunks[i],
                        ["total"] = chunks.Count,
                    }));
                }
                foreach (var p in _players.Connected())
                {
                    if (ReferenceEquals(p, from) || p.World != transfer.World) continue;
                    foreach (var m in messages) p.SendStateUpdate(m);
                    recipients++;
                }
            }
        }
        _log?.Invoke($"Merged world import {transfer.TransactionId} from {from.Username} into '{transfer.World}' ({transfer.Chunks.Length} chunk(s), {blocks} block(s), sent to {recipients} player(s)).");
        SendImportResult(from, transfer.TransactionId, transfer.World, ok: true, null, retry: false, blocks);
        WorldImported?.Invoke(transfer.World);
    }

    /// <summary>
    /// Tells the importing client what happened to its transfer: server_import_result
    /// { transactionId, world, ok, reason?, retry, blocks? }. retry=true means the transfer was dropped for a
    /// temporary reason (busy, timed out, conflicting chunk) and can be sent again.
    /// </summary>
    private void SendImportResult(PlayerSession to, string tx, string? world, bool ok, string? reason, bool retry, int? blocks = null)
    {
        var o = new JsonObject { ["type"] = ImportResultType, ["transactionId"] = tx, ["world"] = world, ["ok"] = ok, ["retry"] = retry };
        if (reason != null) o["reason"] = reason;
        if (blocks != null) o["blocks"] = blocks;
        to.Send(Json(o));
    }

    /// <summary>Discards unfinished import transfers that have been idle longer than the import timeout.</summary>
    public int ExpireStaleImports()
    {
        lock (_importGate) return ExpireImportsLocked(Clock());
    }

    internal int PendingImportCount
    {
        get { lock (_importGate) return _imports.Count; }
    }

    private int ExpireImportsLocked(long now)
    {
        long timeoutMs = _settings.ImportTimeoutSeconds * 1000L;
        var stale = _imports.Where(kv => now - kv.Value.LastActivity > timeoutMs).ToList();
        foreach (var (k, v) in stale)
        {
            _log?.Invoke($"Discarded unfinished world import {k.TransactionId} from {k.Sender}: timed out ({v.Received}/{v.Chunks.Length} chunk(s) received).");
            _imports.Remove(k);
            if (_players.Get(k.Sender) is { State: PlayerState.Connected } p)
                SendImportResult(p, k.TransactionId, v.World, ok: false, "timeout", retry: true);
        }
        return stale.Count;
    }

    private void RememberRelayed(PlayerSession from, string raw)
    {
        long now = Environment.TickCount64;
        lock (_echoGate)
        {
            _recent[raw] = (now, from.Username);
            if (now - _lastEchoPrune > EchoWindowMs)
            {
                _lastEchoPrune = now;
                foreach (var key in _recent.Where(kv => now - kv.Value.Tick > EchoWindowMs).Select(kv => kv.Key).ToList())
                    _recent.Remove(key);
            }
        }
    }

    private bool IsEcho(PlayerSession from, string raw)
    {
        lock (_echoGate)
        {
            return _recent.TryGetValue(raw, out var seen)
                   && Environment.TickCount64 - seen.Tick <= EchoWindowMs
                   && !string.Equals(seen.Sender, from.Username, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void PersistBlockChange(JsonObject m)
    {
        if (Str(m, "world") is { } w && Long(m, "wx") is { } wx && Long(m, "wy") is { } wy && Long(m, "wz") is { } wz && Long(m, "bid") is { } bid)
            _worlds.ApplyBlock(w, wx, wy, wz, bid, Str(m, "originSeed"));
    }

    private void EnterWorld(PlayerSession p, string world)
    {
        var old = p.World;
        p.World = world;
        bool first;
        lock (p.SyncedWorlds) first = p.SyncedWorlds.Add(world);
        if (first) SendWorldSync(p, world);
        if (old != null && old != world) RecomputeAuthority(old, null);
        RecomputeAuthority(world, p);
    }

    /// <summary>
    /// Picks the longest-connected player in the world as its authority. When the authority changes every
    /// player in the world is told; otherwise only <paramref name="newcomer"/> is told who the authority is.
    /// </summary>
    private void RecomputeAuthority(string world, PlayerSession? newcomer)
    {
        var inWorld = _players.Connected().Where(p => p.World == world).OrderBy(p => p.JoinSequence).ToArray();
        string? next;
        bool changed;
        lock (_authorityGate)
        {
            _authorities.TryGetValue(world, out var current);
            var keep = current != null && inWorld.Any(p => string.Equals(p.Username, current, StringComparison.OrdinalIgnoreCase));
            next = keep ? current : inWorld.FirstOrDefault()?.Username;
            changed = !string.Equals(current, next, StringComparison.Ordinal);
            if (next == null) _authorities.Remove(world);
            else _authorities[world] = next;
        }

        if (next == null)
        {
            if (changed) AuthorityChanged?.Invoke(world, null);
            return;
        }

        var msg = Json(new JsonObject { ["type"] = "server_authority", ["world"] = world, ["username"] = next });
        if (changed)
        {
            _log?.Invoke($"World authority for '{world}' is now {next}.");
            foreach (var p in inWorld) p.Send(msg);
            AuthorityChanged?.Invoke(world, next);
        }
        else
        {
            newcomer?.Send(msg);
        }
    }

    /// <summary>Streams the saved state of a world with SupGalaxy's world_sync_start / world_sync_chunk messages.</summary>
    internal Task SendWorldSync(PlayerSession p, string world)
    {
        string? payload;
        long revision;
        // Taken atomically with BeginSync: every state update applied before this point is in the snapshot,
        // every later one is held back for this player until the snapshot has been streamed.
        lock (_stateGate)
        {
            payload = _worlds.BuildSyncPayload(world, out revision);
            if (payload == null) return Task.CompletedTask;
            p.BeginSync();
        }

        var chunks = new List<string>();
        for (int i = 0; i < payload.Length; i += SyncChunkSize)
            chunks.Add(payload.Substring(i, Math.Min(SyncChunkSize, payload.Length - i)));
        var transactionId = $"world_sync_{p.Username}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        return Task.Run(async () =>
        {
            bool sent = false;
            try
            {
                if (!p.Send(Json(new JsonObject
                    {
                        ["type"] = "world_sync_start", ["world"] = world, ["total"] = chunks.Count, ["transactionId"] = transactionId,
                        ["revision"] = revision,
                    })))
                    return;
                for (int i = 0; i < chunks.Count; i++)
                {
                    while (p.Transport is { IsOpen: true } t && t.BufferedAmount > SyncHighWaterMark && !p.IsClosed)
                        await Task.Delay(10).ConfigureAwait(false);
                    if (!p.Send(Json(new JsonObject
                        {
                            ["type"] = "world_sync_chunk",
                            ["world"] = world,
                            ["transactionId"] = transactionId,
                            ["index"] = i,
                            ["chunk"] = chunks[i],
                            ["total"] = chunks.Count,
                        })))
                        return;
                }
                sent = true;
                _log?.Invoke($"Sent saved state of world '{world}' (revision {revision}) to {p.Username} ({chunks.Count} chunk(s)).");
            }
            finally
            {
                // Held back updates follow the snapshot. If too many piled up they were dropped; resend a fresh snapshot.
                if (!p.EndSync() && sent && !p.IsClosed) _ = SendWorldSync(p, world);
            }
        });
    }

    internal static string Json(JsonObject o) => o.ToJsonString();

    private static string? Str(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        return null;
    }

    private static long? Long(JsonObject o, string name) =>
        Num(o, name) is { } d && d >= long.MinValue && d <= long.MaxValue ? (long)Math.Floor(d) : null;

    /// <summary>Formats a number the way JavaScript template strings do for the integer coordinates used in keys.</summary>
    private static string FormatJsNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
