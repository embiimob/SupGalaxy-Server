using System.Text.Json;
using System.Text.Json.Nodes;
using SupGalaxyServer.Rules;

namespace SupGalaxyServer;

/// <summary>
/// The "always on host". Every player has exactly one WebRTC data channel to this server (star topology),
/// and this class decides what happens to each SupGalaxy data channel message:
///
///  * Game rules    - requests (request_block_place, request_block_break, block_hit, request_block_toggle,
///                    fish_spawn_request, fish_spawn_remove, player_hit) are evaluated by the server itself in
///                    <see cref="WorldRules"/>, for every world. Players are never trusted with the rules, and the
///                    authoritative results (block_change, block_break, add_to_inventory, ...) can only come from
///                    the server: clients that send them are ignored.
///  * Plain relay   - chat, avatars, scores, etc. go to every other connected player.
///  * World scoped  - movement / combat / mob traffic only goes to players in the same world.
///  * Unicast       - any message with a "to" field is delivered to that one player only (proximity voice/video
///                    signaling, mob attacks and loot from the player simulating the mob).
///  * Mobs          - mob AI runs on the client that spawned the mob; <see cref="MobRegistry"/> makes sure only that
///                    player can move, kill or despawn it.
///  * Persisted     - every world edit made by the rules lives in <see cref="WorldStateStore"/> so the world survives
///                    restarts, and new players receive it with world_sync_start / world_sync_chunk.
///
/// See <c>ClientIntegrationNotes.cs</c> for the exact protocol the SupGalaxy client must follow.
/// </summary>
public sealed class GameRelay
{
    public const int ProtocolVersion = 2;

    /// <summary>Same chunk size SupGalaxy uses in sendWorldStateAsync.</summary>
    public const int SyncChunkSize = 131072;

    private const ulong SyncHighWaterMark = 1024 * 1024;
    private const ulong CongestedThreshold = 512 * 1024;

    /// <summary>Only delivered to players currently in the message's world.</summary>
    internal static readonly HashSet<string> WorldScopedTypes = new(StringComparer.Ordinal)
    {
        "player_move", "laser_fired", "laser_fired_batch", "item_dropped", "item_picked_up",
        "mob_update", "mob_update_batch", "mob_state_batch", "mob_spawn", "mob_despawn", "mob_hit", "mob_kill",
        "elite_mob_attack", "boulder_update", "player_attack", "player_death", "player_respawn",
        "wolf_tame_request", "wolf_tame_result", "flower_consumed",
    };

    /// <summary>High-frequency updates that are skipped for a player whose channel is backed up (newer ones follow).</summary>
    internal static readonly HashSet<string> DroppableTypes = new(StringComparer.Ordinal)
    {
        "player_move", "mob_update", "mob_update_batch", "boulder_update", "state_update",
    };

    /// <summary>
    /// Messages only the server may originate. Dropped when a client sends them. This includes every result of the
    /// game rules, so a modified client cannot change the world, inventories or chunk ownership by sending them.
    /// </summary>
    internal static readonly HashSet<string> ServerOnlyTypes = new(StringComparer.Ordinal)
    {
        "new_player", "remove_peer", "world_sync", "world_sync_start", "world_sync_chunk",
        // Media renegotiation must happen peer-to-peer (see p2p_signal), the server connection is data only.
        "renegotiation_offer", "renegotiation_answer",
        // Results of the game rules (Rules/WorldRules.cs).
        "block_change", "batch_block_change", "block_place", "block_break", "block_damaged", "block_action_denied",
        "remove_from_inventory", "alert", "fish_spawn_command", "volcano_event",
        "magician_stone_removed", "calligraphy_stone_removed", "magician_stones_sync", "calligraphy_stones_sync",
    };

    private readonly PlayerRegistry _players;
    private readonly WorldStateStore _worlds;
    private readonly ServerSettings _settings;
    private readonly Action<string>? _log;

    public GameRelay(PlayerRegistry players, WorldStateStore worlds, ServerSettings settings, Action<string>? log = null,
        WorldRules? rules = null)
    {
        _players = players;
        _worlds = worlds;
        _settings = settings;
        _log = log;
        Rules = rules ?? new WorldRules(worlds, players, log);
    }

    public string ServerName => _settings.ServerName;

    /// <summary>The server-side game rules for all worlds.</summary>
    public WorldRules Rules { get; }

    public MobRegistry Mobs { get; } = new();

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

        // Nobody simulates this player's mobs any more.
        foreach (var (world, id) in Mobs.ReleaseAll(p.Username))
        {
            var despawn = $"{{\"type\":\"mob_despawn\",\"id\":{id},\"world\":{JsonValue.Create(world)!.ToJsonString()}}}";
            foreach (var o in _players.Connected())
                if (!ReferenceEquals(o, p) && o.World == world) o.Send(despawn);
        }
    }

    public void HandleMessage(PlayerSession from, string raw)
    {
        if (from.State != PlayerState.Connected || raw.Length > _settings.MaxMessageSize) return;
        from.CountIn(raw.Length);
        if (!from.TryConsumeRate(_settings.MaxMessagesPerSecond)) return;

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

        // Identity enforcement: a player can only speak for itself.
        bool modified = false;
        if (msg.ContainsKey("username") && Str(msg, "username") != from.Username)
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

            // ---- game rules, evaluated by the server -------------------------------------------------------------
            case "request_block_break":
            case "block_hit":
                Rules.HandleBlockHit(from, msg);
                return;
            case "request_block_place":
                Rules.HandlePlace(from, msg);
                return;
            case "request_block_toggle":
                Rules.HandleToggle(from, msg);
                return;
            case "fish_spawn_request":
                Rules.HandleFishSpawnRequest(from, msg);
                return;
            case "fish_spawn_remove":
                Rules.HandleFishSpawnRemove(from, msg);
                return;
            case "player_hit":
                Rules.HandlePlayerHit(from, msg);
                return;

            case "player_move":
                if (world != null && world != from.World) EnterWorld(from, world);
                if (Num(msg, "x") is { } x && Num(msg, "y") is { } y && Num(msg, "z") is { } z)
                {
                    from.X = x;
                    from.Y = y;
                    from.Z = z;
                    from.HasPosition = true;
                }
                break;

            // ---- client messages the server bounds ---------------------------------------------------------------
            case "player_damage":
            case "add_to_inventory":
            {
                // Sent by the client that simulates a mob (mob attacks / loot). Must name one target.
                var target = Str(msg, "to") is { } toName ? _players.Get(toName) : null;
                if (target == null || ReferenceEquals(target, from) || target.State != PlayerState.Connected) return;
                bool ok = type == "player_damage" ? Rules.IsAcceptableClientDamage(from, target, msg) : Rules.IsAcceptableClientLoot(from, target, msg);
                if (!ok) return;
                break;
            }

            case "magician_stone_placed":
            case "calligraphy_stone_placed":
            {
                var kind = type.StartsWith("magician", StringComparison.Ordinal) ? StoneKind.Magician : StoneKind.Calligraphy;
                if (from.World is not { } sw || (world != null && world != sw) || msg["stoneData"] is not JsonObject stone
                    || !WorldRules.TryInt(stone, "x", out var sx) || !WorldRules.TryInt(stone, "y", out var sy) || !WorldRules.TryInt(stone, "z", out var sz)
                    || !Rules.CanConfigureStone(from, sw, kind, sx, sy, sz))
                    return;
                _worlds.SetStone(kind, sw, $"{sx},{sy},{sz}", stone);
                break;
            }

            case "mob_spawn":
            case "mob_update":
                if (from.World is not { } mw || MobRegistry.IdOf(msg["id"]) is not { } mid || !Mobs.TryClaim(world ?? mw, mid, from.Username)) return;
                break;

            case "mob_kill":
            case "mob_despawn":
            {
                if (from.World is not { } kw || MobRegistry.IdOf(msg["id"]) is not { } kid) return;
                if (Mobs.IsOwnedByOther(world ?? kw, kid, from.Username)) return;
                Mobs.Release(world ?? kw, kid);
                break;
            }

            case "mob_update_batch":
            case "mob_state_batch":
            {
                if (from.World is not { } bw || msg["mobs"] is not JsonArray list) return;
                var allowed = list.Where(m => m is JsonObject o && MobRegistry.IdOf(o["id"]) is { } id && Mobs.TryClaim(world ?? bw, id, from.Username))
                    .ToList();
                if (allowed.Count == 0) return;
                if (allowed.Count != list.Count)
                {
                    msg["mobs"] = new JsonArray(allowed.Select(m => m!.DeepClone()).ToArray());
                    modified = true;
                }
                break;
            }

            case "wolf_tame_result":
                // The wolf's simulating player hands it to its new owner.
                if (from.World is { } tw && MobRegistry.IdOf(msg["id"]) is { } wid && Str(msg, "owner") is { } owner
                    && msg["success"] is JsonValue sv && sv.TryGetValue<bool>(out var success) && success)
                {
                    if (Mobs.IsOwnedByOther(world ?? tw, wid, from.Username)) return;
                    Mobs.Transfer(world ?? tw, wid, owner);
                }
                break;
        }

        if (modified) raw = msg.ToJsonString();

        // Unicast (p2p_signal for proximity voice/video, mob damage and loot).
        var to = Str(msg, "to");
        if (to != null)
        {
            var target = _players.Get(to);
            if (target != null && !ReferenceEquals(target, from) && target.State == PlayerState.Connected) target.Send(raw);
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

    private void EnterWorld(PlayerSession p, string world)
    {
        p.World = world;
        p.HasPosition = false;
        bool first;
        lock (p.SyncedWorlds) first = p.SyncedWorlds.Add(world);
        if (first) SendWorldSync(p, world);
        Rules.OnPlayerEnteredWorld(p, world);
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

    internal static string? Str(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static double? Num(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        // Values built in code (not parsed) only convert to their own CLR type.
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    internal static long? Long(JsonObject o, string name) =>
        Num(o, name) is { } d && d >= long.MinValue && d <= long.MaxValue ? (long)Math.Floor(d) : null;
}
