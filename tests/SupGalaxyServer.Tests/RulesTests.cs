using System.Text.Json.Nodes;
using SupGalaxyServer.Rules;

namespace SupGalaxyServer.Tests;

/// <summary>The server runs SupGalaxy's game rules itself; clients only send requests.</summary>
public class RulesTests : RelayTestBase
{
    private const long Day = 24L * 60 * 60 * 1000;

    // High in the sky over the "alpha" world: air in generated terrain, and nobody's home chunk.
    private const int X = 100, Y = 200, Z = 100;

    private void Send(PlayerSession p, string json) => Relay.HandleMessage(p, json);

    private void MoveTo(PlayerSession p, double x, double y, double z) =>
        Send(p, $$"""{"type":"player_move","username":"{{p.Username}}","world":"{{p.World}}","x":{{x}},"y":{{y}},"z":{{z}}}""");

    [Fact]
    public void GetBlock_UsesGeneratedTerrain_OverlaidWithEdits()
    {
        int surface = Enumerable.Range(0, 256).Last(y => Relay.Rules.GetBlock("alpha", 5L, y, 5L) != BlockCatalog.Air);
        Assert.Equal(BlockCatalog.Bedrock, Relay.Rules.GetBlock("alpha", 5L, 0, 5L));
        Assert.Equal(BlockCatalog.Air, Relay.Rules.GetBlock("alpha", 5L, surface + 1, 5L));

        Worlds.ApplyBlock("alpha", 5, surface, 5, BlockCatalog.Air, null);
        Assert.Equal(BlockCatalog.Air, Relay.Rules.GetBlock("alpha", 5L, surface, 5L));
        // Coordinates wrap around the map like SupGalaxy's getBlockAt.
        Assert.Equal(Relay.Rules.GetBlock("alpha", 7L, surface - 1, 9L), Relay.Rules.GetBlock("alpha", 7L + 16384, surface - 1, 9L - 16384));
    }

    [Fact]
    public void HomeChunk_MatchesCalculateSpawnPoint()
    {
        // calculateSpawnPoint("alice@alpha") in the browser gives x=9678, z=11923.
        Assert.Equal("alpha:604:745", WorldRules.HomeChunkKey("alpha", "alice"));
    }

    [Fact]
    public void BlockHit_AccumulatesDamage_ThenBreaks_AndDropsToBreaker()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 3, "otherWorld"); // Dirt, strength 2
        var (alice, a) = Join("alice");
        var (_, b) = Join("bob");
        var (_, c) = Join("carol", "beta");

        Send(alice, $$"""{"type":"block_hit","username":"alice","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");
        Assert.Equal(1, (double)Assert.Single(b.OfType("block_damaged"))["hits"]!);
        Assert.Equal(3, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));

        Send(alice, $$"""{"type":"block_hit","username":"alice","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");
        Assert.Equal(BlockCatalog.Air, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Null(Worlds.GetForeignOrigin("alpha", X, Y, Z));

        var drop = Assert.Single(a.OfType("add_to_inventory"));
        Assert.Equal(3, (int)drop["blockId"]!);
        Assert.Equal("otherWorld", (string?)drop["originSeed"]);
        Assert.Empty(b.OfType("add_to_inventory"));

        var breakMsg = Assert.Single(b.OfType("block_break"));
        Assert.Equal("alice", (string?)breakMsg["username"]);
        // block_change (no username) lets the breaker's own client apply the change.
        Assert.Contains(a.OfType("block_change"), m => (int)m["bid"]! == 0 && (int)m["wx"]! == X);
        Assert.Empty(c.OfType("block_break"));
    }

    [Fact]
    public void BlockHit_RespectsToolsAndUnbreakableBlocks()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 4, null);       // Stone needs a pick
        Worlds.ApplyBlock("alpha", X + 1, Y, Z, 1, null);   // Bedrock
        var (alice, a) = Join("alice");

        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");
        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X + 1}},"y":{{Y}},"z":{{Z}},"toolId":175}""");
        Assert.Equal(2, a.OfType("alert").Count);

        // Iron pick does 2 damage per hit: stone (strength 4) breaks on the second hit.
        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"toolId":174}""");
        Assert.Equal(4, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"toolId":174}""");
        Assert.Equal(0, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Equal(1, Relay.Rules.GetBlock("alpha", (long)X + 1, Y, Z));
    }

    [Fact]
    public void BlockHit_OutOfReach_IsIgnored()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 2, null);
        var (alice, a) = Join("alice");
        MoveTo(alice, X + 100, Y, Z);
        a.Clear();

        Send(alice, $$"""{"type":"request_block_break","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");

        Assert.Equal(2, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Empty(a.Sent);
    }

    [Fact]
    public void BlueLaser_BreaksA3x3x2Area_InBatches()
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = 0; dy < 2; dy++)
            Worlds.ApplyBlock("alpha", X + dx, Y - dy, Z + dz, 8, null); // Leaves, strength 1
        var (alice, a) = Join("alice");
        var (_, b) = Join("bob");

        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"isBlue":true}""");

        var changes = b.OfType("batch_block_change").SelectMany(m => m["messages"]!.AsArray()).ToList();
        Assert.Equal(18, changes.Count);
        Assert.Single(b.OfType("batch_block_change"));
        Assert.Empty(a.OfType("add_to_inventory"));
        Assert.Equal(0, Relay.Rules.GetBlock("alpha", (long)X - 1, Y - 1, Z + 1));
    }

    [Fact]
    public void Leaves_SometimesDropTreeSeeds()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 8, null);
        var (alice, a) = Join("alice");
        NextRandom = 0.05;

        Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");

        Assert.Contains(a.OfType("add_to_inventory"), m => (int)m["blockId"]! == 135 && (int)m["count"]! == 5);
    }

    [Fact]
    public void Place_SetsBlock_ClaimsChunk_AndDecrementsInventory()
    {
        var (alice, a) = Join("alice");
        var (_, b) = Join("bob");

        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":100,"inventoryBlockId":100,"originSeed":"far"}""");

        Assert.Equal(100, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Equal("far", Worlds.GetForeignOrigin("alpha", X, Y, Z));
        Assert.Equal(100, (int)Assert.Single(a.OfType("remove_from_inventory"))["blockId"]!);
        Assert.Empty(b.OfType("remove_from_inventory"));
        Assert.Single(b.OfType("block_place"));
        Assert.Single(a.OfType("block_change"));
        var claim = Worlds.GetClaim("alpha", WorldStateStore.ChunkKeyForBlock("alpha", X, Z))!;
        Assert.Equal("alice", claim.Username);
        Assert.Equal(NowMs + WorldRules.MaxOwnershipPeriodMs, claim.ExpiryDate);
    }

    [Fact]
    public void Place_IntoOccupiedSpace_OrUnknownBlock_IsDenied()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 2, null);
        var (alice, a) = Join("alice");

        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":100}""");
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y + 1}},"z":{{Z}},"blockId":9999}""");
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y + 1}},"z":{{Z}},"blockId":174}""");

        Assert.Equal(3, a.OfType("block_action_denied").Count);
        Assert.Equal(2, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Equal(0, Relay.Rules.GetBlock("alpha", (long)X, Y + 1, Z));
        Assert.Empty(a.OfType("remove_from_inventory"));
    }

    [Fact]
    public void ChunkOwnership_PendingThenMatureThenExpired()
    {
        var (alice, _) = Join("alice");
        var (bob, b) = Join("bob");
        var chunk = WorldStateStore.ChunkKeyForBlock("alpha", X, Z);
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":100}""");

        // Mature claim (30 days - 1 year): only alice may edit.
        NowMs += 31 * Day;
        Send(bob, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y + 1}},"z":{{Z}},"blockId":100}""");
        Assert.Equal("Chunk owned by alice", (string?)Assert.Single(b.OfType("block_action_denied"))["reason"]);
        Send(bob, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");
        Assert.Contains("owned by alice", (string?)Assert.Single(b.OfType("alert"))["message"]);
        Assert.Equal(100, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));

        // Expired claim (> 1 year since the last edit): anyone may edit and take it over.
        NowMs += 400 * Day;
        Send(bob, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y + 1}},"z":{{Z}},"blockId":100}""");
        Assert.Equal("bob", Worlds.GetClaim("alpha", chunk)!.Username);

        // Pending claim (first 30 days): anyone may edit and take it over.
        NowMs += Day;
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y + 2}},"z":{{Z}},"blockId":100}""");
        Assert.Equal("alice", Worlds.GetClaim("alpha", chunk)!.Username);
    }

    [Fact]
    public void HomeChunk_OnlyEditableByItsPlayer()
    {
        var (alice, a) = Join("alice");
        var (bob, b) = Join("bob");
        // alpha:604:745 is alice's home chunk.
        long hx = 604 * 16 + 3, hz = 745 * 16 + 3;

        Send(bob, $$"""{"type":"request_block_place","world":"alpha","x":{{hx}},"y":{{Y}},"z":{{hz}},"blockId":100}""");
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{hx}},"y":{{Y + 1}},"z":{{hz}},"blockId":100}""");

        Assert.Equal("Chunk owned by alice", (string?)Assert.Single(b.OfType("block_action_denied"))["reason"]);
        Assert.Empty(a.OfType("block_action_denied"));
        Assert.Equal(0, Relay.Rules.GetBlock("alpha", hx, Y, hz));
        Assert.Equal(100, Relay.Rules.GetBlock("alpha", hx, Y + 1, hz));
        // Home chunks are never claimed by editing.
        Assert.Null(Worlds.GetClaim("alpha", "alpha:604:745"));
    }

    [Fact]
    public void Door_TogglesOnlyBetweenItsOwnStates()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 146, null); // Oak Door (closed) -> 147 open
        var (alice, a) = Join("alice");

        Send(alice, $$"""{"type":"request_block_toggle","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":2}""");
        Assert.Equal(146, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));

        Send(alice, $$"""{"type":"request_block_toggle","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":147}""");
        Assert.Equal(147, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
        Assert.Single(a.OfType("block_change"));

        // Closing needs a free block above the door.
        Worlds.ApplyBlock("alpha", X, Y + 1, Z, 2, null);
        Send(alice, $$"""{"type":"request_block_toggle","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":146}""");
        Assert.Equal(147, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
    }

    [Fact]
    public void TreeSeed_GrowsIntoTreeAfterFiveMinutes()
    {
        var (alice, a) = Join("alice");
        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":135,"originSeed":"seedX"}""");
        Assert.Equal(135, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));

        NowMs += WorldRules.TreeGrowthMs - 1;
        Relay.Rules.Tick();
        Assert.Equal(135, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));

        NowMs += 1;
        a.Clear();
        Relay.Rules.Tick();

        // makeSeededRandom("seedX_tree_100_200_100"): height 6, canopy 2.
        for (int i = 0; i < 6; i++) Assert.Equal(7, Relay.Rules.GetBlock("alpha", (long)X, Y + i, Z));
        Assert.Equal(8, Relay.Rules.GetBlock("alpha", (long)X, Y + 6 + 2, Z));
        Assert.Equal("seedX", Worlds.GetForeignOrigin("alpha", X, Y + 2, Z));
        Assert.NotEmpty(a.OfType("batch_block_change"));
        Assert.Empty(Worlds.TreeSeedsPlantedBefore(long.MaxValue));
    }

    [Fact]
    public void FishSpawn_RequiresWaterNearbyAndOwnership()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, BlockCatalog.Water, null);
        var (alice, a) = Join("alice");
        var (bob, b) = Join("bob");
        var (_, c) = Join("carol", "beta");
        MoveTo(alice, X, Y + 1, Z);
        MoveTo(bob, X + 50, Y, Z);

        Send(bob, $$"""{"type":"fish_spawn_request","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"fishType":"fish_rare"}""");
        Send(alice, $$"""{"type":"fish_spawn_request","world":"alpha","x":{{X}},"y":{{Y + 1}},"z":{{Z}},"fishType":"fish_rare"}""");
        Assert.Empty(b.OfType("fish_spawn_command"));

        Send(alice, $$"""{"type":"fish_spawn_request","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"fishType":"fish_rare","originSeed":"sea"}""");
        Send(alice, $$"""{"type":"fish_spawn_request","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"fishType":"fish_rare"}""");
        var cmd = Assert.Single(b.OfType("fish_spawn_command"));
        Assert.Equal("alice", (string?)cmd["requestedBy"]);
        Assert.Equal("alice", (string?)cmd["command"]!["spawner"]);
        Assert.Equal("sea", (string?)cmd["originSeed"]);
        Assert.Single(a.OfType("fish_spawn_command"));
        Assert.Empty(c.OfType("fish_spawn_command"));

        // Players who enter the world later receive the existing spawn commands.
        var (_, d) = Join("dave");
        Assert.Single(d.OfType("fish_spawn_command"));

        Send(alice, $$"""{"type":"fish_spawn_remove","world":"alpha","key":"{{X}},{{Y}},{{Z}}"}""");
        Assert.Single(b.OfType("fish_spawn_remove"));
        Assert.Empty(Worlds.GetSpawnCommands("alpha"));
    }

    [Fact]
    public void PlayerHit_UsesServerPositions()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");
        var bob = Players.Get("bob")!;
        MoveTo(alice, 0, 50, 0);
        MoveTo(bob, 3, 50, 4);

        Send(alice, """{"type":"player_hit","target":"bob","toolId":174}""");
        var dmg = Assert.Single(b.OfType("player_damage"));
        Assert.Equal(2, (double)dmg["damage"]!);
        Assert.Equal("alice", (string?)dmg["attacker"]);
        Assert.Equal(3.0, (double)dmg["kx"]!, 9);
        Assert.Equal(4.0, (double)dmg["kz"]!, 9);

        // Out of range (>= 6 blocks): no damage, whatever the client claims.
        NowMs += 1000;
        MoveTo(bob, 30, 50, 0);
        Send(alice, """{"type":"player_hit","target":"bob","toolId":175}""");
        Assert.Single(b.OfType("player_damage"));
    }

    [Fact]
    public void ClientPlayerDamage_IsBounded()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");

        Send(alice, """{"type":"player_damage","to":"bob","damage":2,"attacker":"whale"}""");
        NowMs += 1000;
        Send(alice, """{"type":"player_damage","to":"bob","damage":500,"attacker":"whale"}""");
        Send(alice, """{"type":"player_damage","to":"bob","damage":1,"attacker":"lava"}""");
        Send(alice, """{"type":"player_damage","damage":1,"attacker":"whale"}""");

        Assert.Equal(2, (double)Assert.Single(b.OfType("player_damage"))["damage"]!);
    }

    [Fact]
    public void LavaDamage_IsAppliedByTheServer()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, BlockCatalog.Lava, null);
        var (alice, a) = Join("alice");
        MoveTo(alice, X + 0.5, Y - 0.2, Z + 0.5);

        Relay.Rules.Tick();
        Relay.Rules.Tick();
        NowMs += WorldRules.LavaDamageIntervalMs + 1;
        Relay.Rules.Tick();

        Assert.Equal(2, a.OfType("player_damage").Count(m => (string?)m["attacker"] == "lava"));
    }

    [Fact]
    public void Volcano_EruptsForPlayersNearby()
    {
        // vega:479:934 has a caldera; makeSeededRandom picks a boulder_eruption in minute 28333408.
        NowMs = 28333408L * 60000 + 1;
        var (vic, v) = Join("vic", "vega");
        var (_, a) = Join("alice", "alpha");
        MoveTo(vic, 7671, 120, 14951);

        Relay.Rules.Tick();

        var e = Assert.Single(v.OfType("volcano_event"));
        Assert.Equal("boulder_eruption", (string?)e["eventType"]);
        Assert.Equal(7671.536789297659, (double)e["volcano"]!["x"]!, 9);
        Assert.Empty(a.OfType("volcano_event"));

        // One event per volcano per minute at most.
        NowMs += WorldRules.VolcanoIntervalMs;
        Relay.Rules.Tick();
        Assert.Single(v.OfType("volcano_event"));
    }

    [Fact]
    public void ForgedRuleResults_FromClients_AreDropped()
    {
        Worlds.ApplyBlock("alpha", X, Y, Z, 2, null);
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");
        b.Clear();

        Send(alice, $$"""{"type":"block_change","world":"alpha","wx":{{X}},"wy":{{Y}},"wz":{{Z}},"bid":0}""");
        Send(alice, $$"""{"type":"batch_block_change","messages":[{"type":"block_change","world":"alpha","wx":1,"wy":1,"wz":1,"bid":3}]}""");
        Send(alice, $$"""{"type":"block_break","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":2}""");
        Send(alice, """{"type":"remove_from_inventory","to":"bob","blockId":4,"count":1}""");
        Send(alice, """{"type":"add_to_inventory","to":"bob","blockId":4,"count":64}""");
        Send(alice, """{"type":"volcano_event","eventType":"lava_eruption"}""");
        Send(alice, """{"type":"fish_spawn_command","world":"alpha","command":{}}""");
        Send(alice, """{"type":"magician_stone_removed","key":"1,2,3"}""");

        Assert.Empty(b.Sent);
        Assert.Equal(2, Relay.Rules.GetBlock("alpha", (long)X, Y, Z));
    }

    [Fact]
    public void MobLoot_FromSimulatingClient_IsDeliveredToTarget()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");
        var (_, c) = Join("carol");

        Send(alice, """{"type":"add_to_inventory","to":"bob","blockId":137,"count":1,"originSeed":"alpha"}""");

        Assert.Single(b.OfType("add_to_inventory"));
        Assert.Empty(c.OfType("add_to_inventory"));
    }

    [Fact]
    public void Stones_RequireTheStoneBlock_AndAreRemovedWhenBroken()
    {
        var (alice, a) = Join("alice");
        var (_, b) = Join("bob");

        Send(alice, $$$"""{"type":"magician_stone_placed","stoneData":{"x":{{{X}}},"y":{{{Y}}},"z":{{{Z}}},"url":"https://x"}}""");
        Assert.False(Worlds.HasStone(StoneKind.Magician, "alpha", $"{X},{Y},{Z}"));

        Send(alice, $$"""{"type":"request_block_place","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}},"blockId":127}""");
        Send(alice, $$$"""{"type":"magician_stone_placed","stoneData":{"x":{{{X}}},"y":{{{Y}}},"z":{{{Z}}},"url":"https://x"}}""");
        Assert.True(Worlds.HasStone(StoneKind.Magician, "alpha", $"{X},{Y},{Z}"));
        Assert.Single(b.OfType("magician_stone_placed"));

        for (int i = 0; i < 3; i++)
            Send(alice, $$"""{"type":"block_hit","world":"alpha","x":{{X}},"y":{{Y}},"z":{{Z}}}""");

        Assert.False(Worlds.HasStone(StoneKind.Magician, "alpha", $"{X},{Y},{Z}"));
        Assert.Single(b.OfType("magician_stone_removed"));
    }

    [Fact]
    public void Mobs_OnlyTheirSimulatingPlayerMayControlThem()
    {
        var (alice, _) = Join("alice");
        var (bob, _) = Join("bob");
        var (_, c) = Join("carol");

        Send(alice, """{"type":"mob_spawn","id":7,"world":"alpha","x":1,"y":2,"z":3}""");
        Send(bob, """{"type":"mob_update","id":7,"world":"alpha","x":9}""");
        Send(bob, """{"type":"mob_kill","id":7,"world":"alpha"}""");
        Send(bob, """{"type":"mob_update_batch","world":"alpha","mobs":[{"id":7,"x":9},{"id":8,"x":1}]}""");

        Assert.Single(c.OfType("mob_spawn"));
        Assert.Empty(c.OfType("mob_update"));
        Assert.Empty(c.OfType("mob_kill"));
        var batch = Assert.Single(c.OfType("mob_update_batch"));
        Assert.Equal(8, (int)Assert.Single(batch["mobs"]!.AsArray())!["id"]!);

        // When the simulating player leaves, its mobs are despawned for everyone else.
        Players.Remove(alice);
        alice.State = PlayerState.Disconnected;
        Relay.OnPlayerLeft(alice);
        Assert.Contains(c.OfType("mob_despawn"), m => (int)m["id"]! == 7);
    }

    [Fact]
    public void RuleState_IsSavedAndRestored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sgs-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStateStore(dir);
            store.SetClaim("alpha", "alpha:1:2", new ChunkClaim("alice", 10, 20));
            store.RegisterHome("alpha", "alice", "alpha:604:745");
            store.AddTreeSeed("alpha", "1,2,3", new TreeSeed(1, 2, 3, "o", 99));
            store.TryAddSpawnCommand("alpha", "4,5,6", new JsonObject { ["x"] = 4, ["type"] = "fish_rare" });
            store.Save();

            var loaded = new WorldStateStore(dir);
            Assert.Equal(1, loaded.Load());
            Assert.Equal(new ChunkClaim("alice", 10, 20), loaded.GetClaim("alpha", "alpha:1:2"));
            Assert.Equal("alice", loaded.GetHomeOwner("alpha", "alpha:604:745"));
            Assert.Equal(new TreeSeed(1, 2, 3, "o", 99), Assert.Single(loaded.TreeSeedsPlantedBefore(100)).Seed);
            Assert.Equal("fish_rare", (string?)loaded.GetSpawnCommand("alpha", "4,5,6")!["type"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
