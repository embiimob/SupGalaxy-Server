using System.Globalization;
using System.Text.Json.Nodes;

namespace SupGalaxyServer.Rules;

/// <summary>
/// Server-side game rules for every active world. In peer-to-peer SupGalaxy these rules run in the hosting
/// player's browser (the <c>isHost</c> code paths in js/web-rtc.js, js/main.js and js/world-generation.js). Players
/// cannot be trusted to run them (they may run a modified client), so on a dedicated server they run here, for all
/// worlds at once, and clients only send requests:
///
///  * Block breaking (block_hit / request_block_break): mining damage per tool and laser color, block strength and
///    hit accumulation (block_damaged), chunk ownership, drops (add_to_inventory, 10% tree seed bonus on leaves),
///    seaweed turning into water, blue laser 3x3x2 blasts, magician / calligraphy stone removal.
///  * Block placing (request_block_place): valid block, empty target, door clearance, chunk ownership and the
///    edit-based ownership claims (new / renew / expired / pending), inventory decrement, tree seed planting.
///  * Doors (request_block_toggle).
///  * Chunk ownership: home spawn chunks (calculateSpawnPoint(user@world)) belong to their player forever; edited
///    chunks are claimed for a year and can be taken over during the first 30 days.
///  * Tree seeds grow into trees after five minutes.
///  * Fish spawn commands (fish_spawn_request / fish_spawn_remove).
///  * PvP melee (player_hit), lava damage, and volcano eruptions near players.
///
/// Block lookups use a C# port of SupGalaxy's terrain generator (<see cref="TerrainGenerator"/>, bit-identical to
/// js/worker.js) overlaid with the saved world edits in <see cref="WorldStateStore"/>.
/// </summary>
public sealed class WorldRules
{
    public const long MaturityPeriodMs = 30L * 24 * 60 * 60 * 1000;     // IPFS_MATURITY_PERIOD
    public const long MaxOwnershipPeriodMs = 365L * 24 * 60 * 60 * 1000; // IPFS_MAX_OWNERSHIP_PERIOD
    public const long TreeGrowthMs = 300_000;
    public const long LavaDamageIntervalMs = 500;
    public const long VolcanoIntervalMs = 10_000;
    public const long VolcanoCooldownMs = 60_000;
    public const double VolcanoPlayerRange = 256;
    public const int VolcanoScanRadiusChunks = 8;
    public const int VolcanoScanBudget = 48;
    public const double PvpRange = 6;
    public const long PvpCooldownMs = 200;

    /// <summary>Max distance between a player and a block it edits by hand. Generous to allow for movement lag.</summary>
    public const double HandReach = 16;

    /// <summary>Max distance for laser hits (lasers travel, but not across the map).</summary>
    public const double LaserReach = 192;

    /// <summary>Max distance for a fish spawn request (fish_spawn_request in js/web-rtc.js).</summary>
    public const double FishReach = 8;

    /// <summary>Largest player_damage a client (mob simulation) may send. Highest in SupGalaxy is 15 (elite lasers).</summary>
    public const double MaxClientDamage = 15;

    /// <summary>Largest add_to_inventory count a client (mob loot) may send. Highest elite drop is 12.</summary>
    public const int MaxClientLootCount = 12;

    private const int BatchSize = 25;
    private const long DamageForgetMs = 5 * 60_000;

    private readonly WorldStateStore _store;
    private readonly PlayerRegistry _players;
    private readonly Func<long> _clock;
    private readonly Func<double> _random;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<(string World, string Key), (double Hits, long Touched)> _damage = new();
    private readonly Dictionary<string, VolcanoInfo> _volcanoes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _volcanoScanned = new(StringComparer.Ordinal);
    private readonly Dictionary<(string World, string ChunkKey), long> _volcanoLastEvent = new();
    private long _lastVolcanoTick;
    private long _lastDamagePrune;

    public WorldRules(WorldStateStore store, PlayerRegistry players, Action<string>? log = null,
        Func<long>? clock = null, Func<double>? random = null, int terrainCacheChunks = 1024)
    {
        _store = store;
        _players = players;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _random = random ?? Random.Shared.NextDouble;
        Terrain = new TerrainCache(terrainCacheChunks);
    }

    public TerrainCache Terrain { get; }

    // ------------------------------------------------------------------------------------------------------------
    //  World queries
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Port of getBlockAt: saved edit if any, otherwise generated terrain. Out of height range is air.</summary>
    public int GetBlock(string world, long x, long y, long z)
    {
        if (y < 0 || y >= TerrainGenerator.MaxHeight) return BlockCatalog.Air;
        if (_store.GetBlock(world, x, y, z) is { } edited) return (int)Math.Clamp(edited, int.MinValue, int.MaxValue);
        long wx = JsRandom.ModWrap(x, TerrainGenerator.MapSize), wz = JsRandom.ModWrap(z, TerrainGenerator.MapSize);
        var chunk = Terrain.Get(TerrainGenerator.MakeChunkKey(world, wx / TerrainGenerator.ChunkSize, wz / TerrainGenerator.ChunkSize));
        return chunk.Get((int)(wx % TerrainGenerator.ChunkSize), (int)y, (int)(wz % TerrainGenerator.ChunkSize));
    }

    public int GetBlock(string world, double x, double y, double z) =>
        GetBlock(world, (long)Math.Floor(x), (long)Math.Floor(y), (long)Math.Floor(z));

    /// <summary>Port of calculateSpawnPoint(user + "@" + world) without the height: the player's home spawn chunk.</summary>
    public static string HomeChunkKey(string world, string username)
    {
        var rnd = JsRandom.MakeSeededRandom(username + "@" + world);
        long x = (long)Math.Floor(rnd.Next() * TerrainGenerator.MapSize);
        long z = (long)Math.Floor(rnd.Next() * TerrainGenerator.MapSize);
        return TerrainGenerator.MakeChunkKey(world, x / TerrainGenerator.ChunkSize, z / TerrainGenerator.ChunkSize);
    }

    /// <summary>Port of isChunkMutationAllowed.</summary>
    public bool IsMutationAllowed(string world, string chunkKey, string username)
    {
        var home = _store.GetHomeOwner(world, chunkKey);
        if (home != null) return home == username;
        var claim = _store.GetClaim(world, chunkKey);
        if (claim == null) return true;
        long now = _clock();
        if (now - claim.ClaimDate <= MaturityPeriodMs) return true;
        if (now > claim.ExpiryDate) return true;
        return claim.Username == username;
    }

    /// <summary>Port of getChunkOwnerName.</summary>
    public string? GetChunkOwner(string world, string chunkKey) =>
        _store.GetHomeOwner(world, chunkKey) ?? _store.GetClaim(world, chunkKey)?.Username;

    // ------------------------------------------------------------------------------------------------------------
    //  Players
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Called when a player enters a world: protects its home chunk and sends the world's fish spawns.</summary>
    public void OnPlayerEnteredWorld(PlayerSession p, string world)
    {
        if (_store.RegisterHome(world, p.Username, HomeChunkKey(world, p.Username)))
            _log?.Invoke($"Home chunk of {p.Username} in '{world}' is {HomeChunkKey(world, p.Username)}.");
        foreach (var command in _store.GetSpawnCommands(world))
        {
            p.Send(new JsonObject
            {
                ["type"] = "fish_spawn_command",
                ["world"] = world,
                ["command"] = command,
                ["fishType"] = command["type"]?.DeepClone(),
                ["originSeed"] = command["originSeed"]?.DeepClone(),
            }.ToJsonString());
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Requests from players
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>block_hit / request_block_break (removeBlockAt and applyBlueLaserDamage).</summary>
    public void HandleBlockHit(PlayerSession from, JsonObject msg)
    {
        if (ResolveWorld(from, msg) is not { } world || !TryInt(msg, "x", out var x) || !TryInt(msg, "y", out var y) || !TryInt(msg, "z", out var z))
            return;
        bool isBlue = msg["isBlue"] is JsonValue b && b.TryGetValue<bool>(out var blue) && blue;
        var laserColor = isBlue ? "blue" : GameRelay.Str(msg, "laserColor") is "red" or "green" ? GameRelay.Str(msg, "laserColor") : null;
        if (!InReach(from, x, y, z, laserColor != null ? LaserReach : HandReach)) return;

        lock (_gate)
        {
            if (isBlue)
            {
                var changes = new List<JsonObject>();
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = 0; dy < 2; dy++)
                    BreakBlock(from, world, x + dx, y - dy, z + dz, null, "blue", changes);
                BroadcastBatch(world, changes);
            }
            else
            {
                BreakBlock(from, world, x, y, z, GameRelay.Long(msg, "toolId"), laserColor, null);
            }
        }
    }

    /// <summary>request_block_place.</summary>
    public void HandlePlace(PlayerSession from, JsonObject msg)
    {
        if (ResolveWorld(from, msg) is not { } world || !TryInt(msg, "x", out var x) || !TryInt(msg, "y", out var y)
            || !TryInt(msg, "z", out var z) || !TryInt(msg, "blockId", out var blockId))
            return;
        var originSeed = Truncate(GameRelay.Str(msg, "originSeed"));
        var chunkKey = WorldStateStore.ChunkKeyForBlock(world, x, z);

        lock (_gate)
        {
            var def = BlockCatalog.Get(blockId);
            string? problem = null;
            if (def == null || def.ItemOnly) problem = "Unknown block";
            else if (y < 0 || y >= TerrainGenerator.MaxHeight) problem = "Out of world";
            else if (!InReach(from, x, y, z, HandReach)) problem = "Too far away";
            else if (!BlockCatalog.IsReplaceable(GetBlock(world, x, y, z))) problem = "Space is occupied";
            else if (def.Model == "door_closed" && !(y + 1 < TerrainGenerator.MaxHeight && GetBlock(world, x, y + 1, z) == BlockCatalog.Air))
                problem = "No room for the door";

            if (problem != null || !IsMutationAllowed(world, chunkKey, from.Username))
            {
                var owner = GetChunkOwner(world, chunkKey);
                from.Send(new JsonObject
                {
                    ["type"] = "block_action_denied",
                    ["x"] = x, ["y"] = y, ["z"] = z,
                    ["reason"] = problem ?? (owner != null ? $"Chunk owned by {owner}" : "Unknown ownership"),
                    ["chunkKey"] = chunkKey,
                }.ToJsonString());
                return;
            }

            long now = _clock();
            int previous = GetBlock(world, x, y, z);
            _store.ApplyBlock(world, x, y, z, blockId, originSeed);
            UpdateClaimOnEdit(world, chunkKey, from.Username, now);
            if (blockId == BlockCatalog.TreeSeed)
                _store.AddTreeSeed(world, Key(x, y, z), new TreeSeed(x, y, z, originSeed ?? world, now));

            from.Send(WithOptional(new JsonObject
            {
                ["type"] = "remove_from_inventory",
                ["blockId"] = GameRelay.Long(msg, "inventoryBlockId") is { } inv and > 0 ? inv : blockId,
                ["count"] = 1,
            }, "originSeed", originSeed).ToJsonString());

            // block_change makes the placer's unmodified client apply the block too: SupGalaxy ignores messages
            // that carry its own username, and block_place carries the placer's name for everyone else's effects.
            BroadcastWorld(world, BlockChange(world, x, y, z, blockId, previous, originSeed));
            BroadcastWorld(world, WithOptional(new JsonObject
            {
                ["type"] = "block_place",
                ["x"] = x, ["y"] = y, ["z"] = z,
                ["blockId"] = blockId,
                ["username"] = from.Username,
                ["world"] = world,
            }, "originSeed", originSeed));
        }
    }

    /// <summary>request_block_toggle (doors).</summary>
    public void HandleToggle(PlayerSession from, JsonObject msg)
    {
        if (ResolveWorld(from, msg) is not { } world || !TryInt(msg, "x", out var x) || !TryInt(msg, "y", out var y)
            || !TryInt(msg, "z", out var z) || !TryInt(msg, "blockId", out var target))
            return;
        if (!InReach(from, x, y, z, HandReach)) return;

        lock (_gate)
        {
            int current = GetBlock(world, x, y, z);
            var door = BlockCatalog.Get(current);
            bool isDoorToggle = door != null && (door.OpenId == target || door.ClosedId == target);
            var targetDef = BlockCatalog.Get(target);
            bool clearance = targetDef?.Model != "door_closed" || (y + 1 < TerrainGenerator.MaxHeight && GetBlock(world, x, y + 1, z) == BlockCatalog.Air);
            if (!isDoorToggle || !clearance || !IsMutationAllowed(world, WorldStateStore.ChunkKeyForBlock(world, x, z), from.Username)) return;

            _store.ApplyBlock(world, x, y, z, target, null);
            BroadcastWorld(world, BlockChange(world, x, y, z, target, current, null));
        }
    }

    /// <summary>fish_spawn_request (addFishSpawnCommand on the host).</summary>
    public void HandleFishSpawnRequest(PlayerSession from, JsonObject msg)
    {
        var fishType = GameRelay.Str(msg, "fishType");
        if (fishType is not ("fish_rare" or "fish_school") || ResolveWorld(from, msg) is not { } world
            || !TryInt(msg, "x", out var x) || !TryInt(msg, "y", out var y) || !TryInt(msg, "z", out var z))
            return;
        if (!from.HasPosition || JsRandom.Hypot(from.X - x, from.Y - y, from.Z - z) > FishReach) return;

        lock (_gate)
        {
            int block = GetBlock(world, x, y, z);
            if (block != BlockCatalog.Water && block != BlockCatalog.Seaweed) return;
            if (!IsMutationAllowed(world, WorldStateStore.ChunkKeyForBlock(world, x, z), from.Username)) return;

            long wx = JsRandom.ModWrap(x, TerrainGenerator.MapSize), wz = JsRandom.ModWrap(z, TerrainGenerator.MapSize);
            var originSeed = Truncate(GameRelay.Str(msg, "originSeed")) ?? Truncate(world)!;
            var command = new JsonObject
            {
                ["x"] = wx, ["y"] = y, ["z"] = wz,
                ["type"] = fishType,
                ["originSeed"] = originSeed,
                ["owner"] = from.Username,
                // The requesting player's client simulates the fish (isMobAuthority uses spawner).
                ["spawner"] = from.Username,
            };
            if (!_store.TryAddSpawnCommand(world, Key(wx, y, wz), command)) return;

            BroadcastWorld(world, new JsonObject
            {
                ["type"] = "fish_spawn_command",
                ["world"] = world,
                ["command"] = command.DeepClone(),
                ["fishType"] = fishType,
                ["originSeed"] = originSeed,
                ["requestedBy"] = from.Username,
            });
        }
    }

    /// <summary>fish_spawn_remove (canRemoveFishSpawnCommand).</summary>
    public void HandleFishSpawnRemove(PlayerSession from, JsonObject msg)
    {
        if (ResolveWorld(from, msg) is not { } world || GameRelay.Str(msg, "key") is not { } key) return;
        lock (_gate)
        {
            var command = _store.GetSpawnCommand(world, key);
            if (command == null || GameRelay.Long(command, "x") is not { } cx || GameRelay.Long(command, "z") is not { } cz) return;
            if (!IsMutationAllowed(world, WorldStateStore.ChunkKeyForBlock(world, cx, cz), from.Username)) return;
            _store.RemoveSpawnCommand(world, key);
            BroadcastWorld(world, new JsonObject { ["type"] = "fish_spawn_remove", ["world"] = world, ["key"] = key }, except: from);
        }
    }

    /// <summary>player_hit (handlePlayerHit): melee PvP with server-side range check and knockback.</summary>
    public void HandlePlayerHit(PlayerSession from, JsonObject msg)
    {
        if (GameRelay.Str(msg, "target") is not { } targetName || from.World == null || !from.HasPosition) return;
        var target = _players.Get(targetName);
        if (target == null || ReferenceEquals(target, from) || target.State != PlayerState.Connected
            || target.World != from.World || !target.HasPosition)
            return;

        long now = _clock();
        lock (_gate)
        {
            if (now - from.LastPvpHitMs < PvpCooldownMs) return;
            if (JsRandom.Hypot(from.X - target.X, from.Y - target.Y, from.Z - target.Z) >= PvpRange) return;
            from.LastPvpHitMs = now;
        }

        double dx = target.X - from.X, dz = target.Z - from.Z, len = JsRandom.Hypot(dx, dz);
        double kx = len > 0 ? dx / len * 5 : 0, kz = len > 0 ? dz / len * 5 : 0;
        target.Send(new JsonObject
        {
            ["type"] = "player_damage",
            ["damage"] = BlockCatalog.PickaxeMultiplier(GameRelay.Long(msg, "toolId")),
            ["attacker"] = from.Username,
            ["kx"] = kx,
            ["kz"] = kz,
        }.ToJsonString());
    }

    /// <summary>
    /// Checks a player_damage sent by a client. Mob AI runs on the spawning player's client, so mob attacks still
    /// arrive from clients; the server bounds them (target in the same world, damage 0-15, no fake "lava" source,
    /// at most one hit per target every 100 ms).
    /// </summary>
    public bool IsAcceptableClientDamage(PlayerSession from, PlayerSession target, JsonObject msg)
    {
        if (target.World == null || target.World != from.World) return false;
        if (GameRelay.Num(msg, "damage") is not { } damage || damage <= 0 || damage > MaxClientDamage) return false;
        if (string.Equals(GameRelay.Str(msg, "attacker"), "lava", StringComparison.OrdinalIgnoreCase)) return false;
        if (GameRelay.Num(msg, "kx") is { } kx && Math.Abs(kx) > 10) return false;
        if (GameRelay.Num(msg, "kz") is { } kz && Math.Abs(kz) > 10) return false;
        long now = _clock();
        lock (from.RuleGate)
        {
            if (from.LastClientDamageMs.TryGetValue(target.Username, out var last) && now - last < 100) return false;
            from.LastClientDamageMs[target.Username] = now;
        }
        return true;
    }

    /// <summary>Checks an add_to_inventory a client sends (mob loot, see onEliteMobDeath / fish catches).</summary>
    public bool IsAcceptableClientLoot(PlayerSession from, PlayerSession target, JsonObject msg)
    {
        if (target.World == null || target.World != from.World) return false;
        if (!TryInt(msg, "blockId", out var id) || BlockCatalog.Get(id) == null) return false;
        return TryInt(msg, "count", out var count) && count is >= 1 and <= MaxClientLootCount;
    }

    /// <summary>A magician / calligraphy stone may only be configured on a matching block the sender may edit.</summary>
    public bool CanConfigureStone(PlayerSession from, string world, StoneKind kind, long x, long y, long z)
    {
        int expected = kind == StoneKind.Magician ? BlockCatalog.MagicianStone : BlockCatalog.CalligraphyStone;
        return GetBlock(world, x, y, z) == expected && IsMutationAllowed(world, WorldStateStore.ChunkKeyForBlock(world, x, z), from.Username);
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Timed rules
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Runs tree growth, lava damage and volcano events. Call a few times per second.</summary>
    public void Tick()
    {
        long now = _clock();
        GrowTrees(now);
        ApplyLavaDamage(now);
        if (now - _lastVolcanoTick >= VolcanoIntervalMs)
        {
            _lastVolcanoTick = now;
            ManageVolcanoes(now);
        }
        if (now - _lastDamagePrune > DamageForgetMs)
        {
            _lastDamagePrune = now;
            lock (_gate)
                foreach (var key in _damage.Where(kv => now - kv.Value.Touched > DamageForgetMs).Select(kv => kv.Key).ToList())
                    _damage.Remove(key);
        }
    }

    /// <summary>Port of manageTreeSeeds, for every world.</summary>
    private void GrowTrees(long now)
    {
        foreach (var (world, key, seed) in _store.TreeSeedsPlantedBefore(now - TreeGrowthMs))
        {
            lock (_gate)
            {
                _store.RemoveTreeSeed(world, key);
                // The seed may have been broken or replaced since it was planted.
                if (GetBlock(world, seed.X, seed.Y, seed.Z) != BlockCatalog.TreeSeed) continue;

                var changes = new List<JsonObject>();
                var origin = seed.OriginSeed;
                var rnd = JsRandom.MakeSeededRandom(origin + "_tree_" + seed.X + "_" + seed.Y + "_" + seed.Z);
                int height = 5 + (int)Math.Floor(rnd.Next() * 6);
                int canopy = 2 + (int)Math.Floor(rnd.Next() * 2);

                SetBlock(world, seed.X, seed.Y, seed.Z, BlockCatalog.Air, null, changes, clearOrigin: true);
                for (int i = 0; i < height; i++)
                    SetBlock(world, seed.X, seed.Y + i, seed.Z, 7, origin, changes);
                for (int dy = -canopy; dy <= canopy; dy++)
                for (int dx = -canopy; dx <= canopy; dx++)
                for (int dz = -canopy; dz <= canopy; dz++)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d <= canopy + 0.5 * rnd.Next())
                    {
                        long rx = seed.X + dx, ry = seed.Y + height + dy, rz = seed.Z + dz;
                        if (GetBlock(world, rx, ry, rz) == BlockCatalog.Air) SetBlock(world, rx, ry, rz, BlockCatalog.Leaves, origin, changes);
                    }
                }
                BroadcastBatch(world, changes);
                _log?.Invoke($"A tree grew in '{world}' at {key}.");
            }
        }
    }

    /// <summary>Host lava check from the game loop: 1 damage every 500 ms while standing in lava.</summary>
    private void ApplyLavaDamage(long now)
    {
        foreach (var p in _players.Connected())
        {
            if (p.World is not { } world || !p.HasPosition || now - p.LastLavaDamageMs <= LavaDamageIntervalMs) continue;
            if (GetBlock(world, p.X, p.Y + 0.5, p.Z) != BlockCatalog.Lava) continue;
            p.LastLavaDamageMs = now;
            p.Send(new JsonObject { ["type"] = "player_damage", ["damage"] = 1, ["attacker"] = "lava" }.ToJsonString());
        }
    }

    /// <summary>Port of manageVolcanoes, for every world that has players.</summary>
    private void ManageVolcanoes(long now)
    {
        var byWorld = _players.Connected().Where(p => p.World != null && p.HasPosition).GroupBy(p => p.World!).ToList();
        int budget = VolcanoScanBudget;
        foreach (var group in byWorld)
        {
            var world = group.Key;
            if (TerrainGenerator.SelectArchetype(TerrainGenerator.SeedOf(world)).Name != "Vulcan") continue;

            // The browser host learns about volcanoes from the chunks it generates; the server scans around players.
            foreach (var p in group)
            {
                long pcx = JsRandom.ModWrap((long)Math.Floor(p.X), TerrainGenerator.MapSize) / TerrainGenerator.ChunkSize;
                long pcz = JsRandom.ModWrap((long)Math.Floor(p.Z), TerrainGenerator.MapSize) / TerrainGenerator.ChunkSize;
                // Rings outward from the player's chunk, so the nearest chunks are scanned first.
                for (long ring = 0; ring <= VolcanoScanRadiusChunks && budget > 0; ring++)
                for (long dx = -ring; dx <= ring && budget > 0; dx++)
                for (long dz = -ring; dz <= ring && budget > 0; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    var key = TerrainGenerator.MakeChunkKey(world, JsRandom.ModWrap(pcx + dx, TerrainGenerator.ChunksPerAxis),
                        JsRandom.ModWrap(pcz + dz, TerrainGenerator.ChunksPerAxis));
                    lock (_gate)
                        if (_volcanoScanned.Contains(key)) continue;
                    budget--;
                    var volcano = Terrain.Get(key).Volcano;
                    lock (_gate)
                    {
                        _volcanoScanned.Add(key);
                        if (volcano != null) _volcanoes[key] = volcano;
                    }
                }
            }

            var seedPrefix = TerrainGenerator.SeedOf(world) + ":";
            List<VolcanoInfo> volcanoes;
            lock (_gate) volcanoes = _volcanoes.Values.Where(v => v.ChunkKey.StartsWith(seedPrefix, StringComparison.Ordinal)).ToList();
            foreach (var v in volcanoes)
            {
                if (!group.Any(p => JsRandom.Hypot(v.X - p.X, v.Z - p.Z) < VolcanoPlayerRange)) continue;
                lock (_gate)
                {
                    if (_volcanoLastEvent.TryGetValue((world, v.ChunkKey), out var last) && now - last < VolcanoCooldownMs) continue;
                    var rnd = JsRandom.MakeSeededRandom(world + "_volcano_event_" + v.ChunkKey + "_" + (now / 60000).ToString(CultureInfo.InvariantCulture));
                    if (rnd.Next() >= 0.05) continue;
                    _volcanoLastEvent[(world, v.ChunkKey)] = now;
                    double a = rnd.Next();
                    var eventType = a < 0.33 ? "lava_eruption" : a < 0.66 ? "pebble_rain" : "boulder_eruption";
                    _log?.Invoke($"Volcano {eventType} in '{world}' at {v.ChunkKey}.");
                    BroadcastWorld(world, new JsonObject
                    {
                        ["type"] = "volcano_event",
                        ["world"] = world,
                        ["volcano"] = new JsonObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z },
                        ["eventType"] = eventType,
                        ["seed"] = world + "_event_" + now.ToString(CultureInfo.InvariantCulture),
                    });
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Port of removeBlockAt. When <paramref name="silentChanges"/> is not null (blue laser) the result is collected
    /// for a batch and no drops, crack updates or alerts are sent.
    /// </summary>
    private void BreakBlock(PlayerSession from, string world, long x, long y, long z, long? toolId, string? laserColor, List<JsonObject>? silentChanges)
    {
        bool silent = silentChanges != null;
        int block = GetBlock(world, x, y, z);
        if (block == BlockCatalog.Air || block == BlockCatalog.Water) return;

        double damage = BlockCatalog.MiningDamage(block, toolId, laserColor);
        if (!(damage > 0))
        {
            if (!silent) Alert(from, "Cannot break that block");
            return;
        }

        var chunkKey = WorldStateStore.ChunkKeyForBlock(world, x, z);
        if (!IsMutationAllowed(world, chunkKey, from.Username))
        {
            if (!silent) Alert(from, $"You cannot break this block. It is owned by {GetChunkOwner(world, chunkKey) ?? "another user"}.");
            return;
        }

        var key = Key(x, y, z);
        long now = _clock();
        _damage.TryGetValue((world, key), out var state);
        double hits = state.Hits + damage;
        if (hits < BlockCatalog.EffectiveStrength(block))
        {
            _damage[(world, key)] = (hits, now);
            if (!silent)
                BroadcastWorld(world, new JsonObject { ["type"] = "block_damaged", ["x"] = x, ["y"] = y, ["z"] = z, ["hits"] = hits });
            return;
        }

        _damage.Remove((world, key));
        var origin = _store.GetForeignOrigin(world, x, y, z);
        int replacement = block == BlockCatalog.Seaweed ? BlockCatalog.Water : BlockCatalog.Air;
        _store.ApplyBlock(world, x, y, z, replacement, null, clearOrigin: true);
        var change = BlockChange(world, x, y, z, replacement, block, null);

        if (silent)
        {
            silentChanges!.Add(change);
        }
        else
        {
            var def = BlockCatalog.Get(block);
            from.Send(WithOptional(new JsonObject { ["type"] = "add_to_inventory", ["blockId"] = def?.DropId ?? block, ["count"] = 1 }, "originSeed", origin).ToJsonString());
            if (block == BlockCatalog.Leaves && _random() < 0.1)
                from.Send(WithOptional(new JsonObject { ["type"] = "add_to_inventory", ["blockId"] = BlockCatalog.TreeSeed, ["count"] = 5 }, "originSeed", origin).ToJsonString());

            BroadcastWorld(world, change);
            BroadcastWorld(world, WithOptional(new JsonObject
            {
                ["type"] = "block_break",
                ["x"] = x, ["y"] = y, ["z"] = z,
                ["blockId"] = block,
                ["replacementBlockId"] = replacement,
                ["username"] = from.Username,
                ["world"] = world,
            }, "originSeed", origin));
        }

        if (block is BlockCatalog.MagicianStone or BlockCatalog.CalligraphyStone)
        {
            var kind = block == BlockCatalog.MagicianStone ? StoneKind.Magician : StoneKind.Calligraphy;
            if (_store.HasStone(kind, world, key))
            {
                _store.RemoveStone(kind, world, key);
                BroadcastWorld(world, new JsonObject
                {
                    ["type"] = kind == StoneKind.Magician ? "magician_stone_removed" : "calligraphy_stone_removed",
                    ["key"] = key,
                    ["world"] = world,
                });
            }
        }
    }

    /// <summary>Edit-based ownership from the request_block_place host handler.</summary>
    private void UpdateClaimOnEdit(string world, string chunkKey, string username, long now)
    {
        if (_store.GetHomeOwner(world, chunkKey) != null) return;
        var claim = _store.GetClaim(world, chunkKey);
        ChunkClaim? next = null;
        if (claim == null) next = new ChunkClaim(username, now, now + MaxOwnershipPeriodMs);
        else if (claim.Username == username) next = new ChunkClaim(username, claim.ClaimDate, now + MaxOwnershipPeriodMs);
        else if (now > claim.ExpiryDate) next = new ChunkClaim(username, now, now + MaxOwnershipPeriodMs);
        else if (now - claim.ClaimDate <= MaturityPeriodMs) next = new ChunkClaim(username, now, now + MaxOwnershipPeriodMs);
        if (next != null) _store.SetClaim(world, chunkKey, next);
    }

    private void SetBlock(string world, long x, long y, long z, int blockId, string? originSeed, List<JsonObject> changes, bool clearOrigin = false)
    {
        if (y < 0 || y >= TerrainGenerator.MaxHeight) return;
        int previous = GetBlock(world, x, y, z);
        if (previous == blockId && !clearOrigin) return;
        _store.ApplyBlock(world, x, y, z, blockId, originSeed, clearOrigin);
        if (previous != blockId) changes.Add(BlockChange(world, x, y, z, blockId, previous, originSeed));
    }

    private static JsonObject BlockChange(string world, long x, long y, long z, long bid, long prevBid, string? originSeed) => new()
    {
        ["type"] = "block_change",
        ["world"] = world,
        ["wx"] = x, ["wy"] = y, ["wz"] = z,
        ["bid"] = bid,
        ["prevBid"] = prevBid,
        ["originSeed"] = originSeed,
    };

    private void BroadcastBatch(string world, List<JsonObject> changes)
    {
        for (int i = 0; i < changes.Count; i += BatchSize)
        {
            var batch = new JsonArray(changes.Skip(i).Take(BatchSize).Select(c => (JsonNode)c).ToArray());
            BroadcastWorld(world, new JsonObject { ["type"] = "batch_block_change", ["world"] = world, ["messages"] = batch });
        }
    }

    private void BroadcastWorld(string world, JsonObject message, PlayerSession? except = null)
    {
        var raw = message.ToJsonString();
        foreach (var p in _players.Connected())
            if (p.World == world && !ReferenceEquals(p, except)) p.Send(raw);
    }

    private static void Alert(PlayerSession p, string message) =>
        p.Send(new JsonObject { ["type"] = "alert", ["message"] = message }.ToJsonString());

    /// <summary>A player may only act in the world it is in.</summary>
    private static string? ResolveWorld(PlayerSession from, JsonObject msg)
    {
        var world = GameRelay.Str(msg, "world");
        if (from.World == null) return null;
        return world == null || world == from.World ? from.World : null;
    }

    private static bool InReach(PlayerSession p, long x, long y, long z, double reach) =>
        !p.HasPosition || JsRandom.Hypot(p.X - (x + 0.5), p.Y - (y + 0.5), p.Z - (z + 0.5)) <= reach;

    /// <summary>Number.isInteger with a sane range.</summary>
    internal static bool TryInt(JsonObject msg, string name, out long value)
    {
        value = 0;
        if (GameRelay.Num(msg, name) is not { } d || d != Math.Floor(d) || Math.Abs(d) > 1e12) return false;
        value = (long)d;
        return true;
    }

    private static string? Truncate(string? s) => string.IsNullOrEmpty(s) ? null : s.Length > 128 ? s[..128] : s;

    private static string Key(long x, long y, long z) =>
        string.Create(CultureInfo.InvariantCulture, $"{x},{y},{z}");

    private static JsonObject WithOptional(JsonObject o, string name, string? value)
    {
        if (value != null) o[name] = value;
        return o;
    }
}
