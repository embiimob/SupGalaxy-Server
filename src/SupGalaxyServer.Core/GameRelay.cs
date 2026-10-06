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
    private readonly object _authorityGate = new();
    private readonly Dictionary<string, string> _authorities = new(StringComparer.Ordinal);

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

        // Identity enforcement: a player can only speak for itself. A world authority legitimately sends
        // results on behalf of other players (e.g. block_break with username = breaker), so it is exempt.
        bool modified = false;
        if (!isAuthority && msg.ContainsKey("username") && Str(msg, "username") != from.Username)
        {
            msg["username"] = from.Username;
            modified = true;
        }

        var world = Str(msg, "world");

        switch (type)
        {
            case "i_am_alive":
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

        if (modified) raw = msg.ToJsonString();

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

        Broadcast(from, raw, WorldScopedTypes.Contains(type) ? world ?? from.World : null, DroppableTypes.Contains(type));
    }

    private void Broadcast(PlayerSession from, string raw, string? world, bool droppable)
    {
        foreach (var p in _players.Connected())
        {
            if (ReferenceEquals(p, from)) continue;
            if (world != null && p.World != null && p.World != world) continue;
            if (droppable && p.Transport is { } t && t.BufferedAmount > CongestedThreshold) continue;
            p.Send(raw);
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
        var payload = _worlds.BuildSyncPayload(world);
        if (payload == null) return Task.CompletedTask;

        var chunks = new List<string>();
        for (int i = 0; i < payload.Length; i += SyncChunkSize)
            chunks.Add(payload.Substring(i, Math.Min(SyncChunkSize, payload.Length - i)));
        var transactionId = $"world_sync_{p.Username}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        return Task.Run(async () =>
        {
            if (!p.Send(Json(new JsonObject { ["type"] = "world_sync_start", ["world"] = world, ["total"] = chunks.Count, ["transactionId"] = transactionId })))
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
            _log?.Invoke($"Sent saved state of world '{world}' to {p.Username} ({chunks.Count} chunk(s)).");
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
