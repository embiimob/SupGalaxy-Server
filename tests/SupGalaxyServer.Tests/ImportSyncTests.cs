using System.Text.Json.Nodes;

namespace SupGalaxyServer.Tests;

/// <summary>Chunk Keyword / IPFS imports (ipfs_chunk_from_client_*) and gap-free world sync.</summary>
public class ImportSyncTests : RelayTestBase
{
    // 2025-10-01T00:00:00Z in ms, after SupGalaxy's IPFS epoch (2025-09-21).
    private const double T1 = 1759276800000;
    private const double T2 = T1 + 86_400_000;

    private static string Payload(string world = "alpha", int blockId = 42, int x = 1) => new JsonObject
    {
        ["deltas"] = new JsonArray(
            new JsonObject
            {
                ["chunk"] = "#" + WorldStateStore.MakeChunkKey(world, 2, 3),
                ["changes"] = new JsonArray(new JsonObject { ["x"] = x, ["y"] = 10, ["z"] = 4, ["b"] = blockId }),
            },
            new JsonObject
            {
                ["chunk"] = WorldStateStore.MakeChunkKey("otherwld", 0, 0),
                ["changes"] = new JsonArray(new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["b"] = 9 }),
            }),
        ["foreignBlockOrigins"] = new JsonArray(new JsonArray("33,10,52", "seedworld")),
        ["magicianStones"] = new JsonObject { ["33,11,52"] = new JsonObject { ["x"] = 33, ["y"] = 11, ["z"] = 52, ["url"] = "https://img" } },
        ["calligraphyStones"] = new JsonObject { ["34,11,52"] = new JsonObject { ["x"] = 34, ["y"] = 11, ["z"] = 52, ["text"] = "hi" } },
        ["chests"] = new JsonObject { ["35,10,52"] = new JsonObject { ["x"] = 35, ["y"] = 10, ["z"] = 52, ["rotation"] = 0, ["items"] = new JsonArray() } },
    }.ToJsonString();

    private static List<string> Split(string s, int size)
    {
        var list = new List<string>();
        for (int i = 0; i < s.Length; i += size) list.Add(s.Substring(i, Math.Min(size, s.Length - i)));
        return list;
    }

    private static string Start(string tx, int total, string? world = "alpha", double timestamp = T1)
    {
        var o = new JsonObject
        {
            ["type"] = "ipfs_chunk_from_client_start", ["total"] = total, ["fromAddress"] = "addr1",
            ["timestamp"] = timestamp, ["transactionId"] = tx,
        };
        if (world != null) o["world"] = world;
        return o.ToJsonString();
    }

    private static string Chunk(string tx, int index, string chunk, int total) => new JsonObject
    {
        ["type"] = "ipfs_chunk_from_client_chunk", ["transactionId"] = tx, ["index"] = index, ["chunk"] = chunk, ["total"] = total,
    }.ToJsonString();

    private void SendImport(PlayerSession from, string tx, string payload, int chunkSize = 64, string? world = "alpha", double timestamp = T1, bool reverse = false)
    {
        var parts = Split(payload, chunkSize);
        Relay.HandleMessage(from, Start(tx, parts.Count, world, timestamp));
        var order = Enumerable.Range(0, parts.Count);
        if (reverse) order = order.Reverse();
        foreach (var i in order) Relay.HandleMessage(from, Chunk(tx, i, parts[i], parts.Count));
    }

    private static long Wx(long x) => 2 * 16 + x;
    private const long Wz = 3 * 16 + 4;

    private static JsonNode Reassemble(FakeTransport t, string startType, string chunkType)
    {
        var start = Assert.Single(t.OfType(startType));
        var chunks = t.OfType(chunkType).Where(c => (string?)c["transactionId"] == (string?)start["transactionId"])
            .OrderBy(c => (int)c["index"]!).Select(c => (string)c["chunk"]!).ToList();
        Assert.Equal((int)start["total"]!, chunks.Count);
        return JsonNode.Parse(string.Concat(chunks))!;
    }

    private async Task<JsonNode> WorldSyncPayload(FakeTransport t)
    {
        await Eventually(() => t.OfType("world_sync_start").Count == 1
                               && t.OfType("world_sync_chunk").Count == (int)t.OfType("world_sync_start")[0]["total"]!);
        return Reassemble(t, "world_sync_start", "world_sync_chunk");
    }

    [Fact]
    public void CompleteImport_IsPersisted_AndRelayedOnlyToSameWorld()
    {
        var (alice, a) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        var (_, c) = Join("carol", "beta");
        var (_, d) = Join("dave", "alphabet"); // same 8 char chunk-key prefix, different world

        SendImport(alice, "tx1", Payload());

        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.Null(Worlds.GetBlock("alpha", 0, 0, 0)); // chunk of another world ignored
        Assert.Equal("seedworld", Worlds.GetForeignOrigin("alpha", 33, 10, 52));
        Assert.True(Worlds.HasStone(StoneKind.Magician, "alpha", "33,11,52"));
        Assert.True(Worlds.HasStone(StoneKind.Calligraphy, "alpha", "34,11,52"));
        Assert.True(Worlds.HasChest("alpha", "35,10,52"));
        Assert.True(Worlds.IsImportProcessed("alpha", "tx1"));

        var update = Reassemble(b, "ipfs_chunk_update_start", "ipfs_chunk_update_chunk");
        var start = b.OfType("ipfs_chunk_update_start")[0];
        Assert.Equal("alice", (string?)start["username"]);
        Assert.Equal("alpha", (string?)start["world"]);
        Assert.Equal("addr1", (string?)start["fromAddress"]);
        Assert.Equal(T1, (double)start["timestamp"]!);
        var deltas = update["deltas"]!.AsArray();
        Assert.Equal("alpha:2:3", (string?)Assert.Single(deltas)!["chunk"]);
        Assert.Equal(42, (int)deltas[0]!["changes"]![0]!["b"]!);
        Assert.NotNull(update["chests"]!["35,10,52"]);

        foreach (var t in new[] { a, c, d })
        {
            Assert.Empty(t.OfType("ipfs_chunk_update_start"));
            Assert.Empty(t.OfType("ipfs_chunk_update_chunk"));
        }
        // The raw client transfer is never relayed.
        foreach (var t in new[] { b, c, d }) Assert.Empty(t.OfType("ipfs_chunk_from_client_chunk"));
        Assert.Equal(0, Relay.PendingImportCount);
    }

    [Fact]
    public void ImportWithoutWorld_UsesSendersWorld()
    {
        var (alice, _) = Join("alice", "alpha");
        SendImport(alice, "tx1", Payload(), world: null);
        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
    }

    [Fact]
    public void ImportForOtherWorld_IsNotAppliedToSendersWorld()
    {
        var (alice, _) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        var (_, c) = Join("carol", "beta");

        SendImport(alice, "tx1", Payload("beta"), world: "beta");

        Assert.Equal(42, Worlds.GetBlock("beta", Wx(1), 10, Wz));
        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.Empty(b.OfType("ipfs_chunk_update_start"));
        Assert.Single(c.OfType("ipfs_chunk_update_start"));
    }

    [Fact]
    public async Task LaterEntrant_ReceivesImportInInitialWorldSync()
    {
        var (alice, _) = Join("alice", "alpha");
        SendImport(alice, "tx1", Payload());

        var (_, e) = Join("erin", "alpha");
        var payload = await WorldSyncPayload(e);

        var delta = Assert.Single(payload["chunkDeltas"]!.AsArray())!;
        Assert.Equal("alpha:2:3", (string?)delta[0]);
        Assert.Equal(42, (int)delta[1]![0]!["b"]!);
        Assert.Equal("seedworld", (string?)payload["foreignBlockOrigins"]![0]![1]);
        Assert.NotNull(payload["magicianStones"]!["33,11,52"]);
        Assert.NotNull(payload["calligraphyStones"]!["34,11,52"]);
        Assert.NotNull(payload["chests"]!["35,10,52"]);
        Assert.Contains("tx1", payload["processedIds"]!.AsArray().Select(n => (string?)n));
        Assert.True((long)e.OfType("world_sync_start")[0]["revision"]! > 0);
    }

    [Fact]
    public async Task ImportSurvivesRestart()
    {
        using var dir = new TempDir();
        var store = new WorldStateStore(dir.Path);
        var relay = new GameRelay(Players, store, Settings);
        var alice = new PlayerSession("alice", "alpha", null) { Transport = new FakeTransport() };
        Players.TryAdd(alice);
        relay.OnPlayerJoined(alice);
        var parts = Split(Payload(), 100);
        relay.HandleMessage(alice, Start("tx1", parts.Count));
        for (int i = 0; i < parts.Count; i++) relay.HandleMessage(alice, Chunk("tx1", i, parts[i], parts.Count));
        Assert.Equal(1, store.Save());

        var restored = new WorldStateStore(dir.Path);
        Assert.Equal(1, restored.Load());
        Assert.Equal(42, restored.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.Equal("seedworld", restored.GetForeignOrigin("alpha", 33, 10, 52));
        Assert.True(restored.HasChest("alpha", "35,10,52"));
        Assert.True(restored.IsImportProcessed("alpha", "tx1"));
        Assert.True(restored.GetRevision("alpha") > 0);

        // A newcomer on the restarted server receives it, and the same transaction is not merged twice.
        var players = new PlayerRegistry();
        var relay2 = new GameRelay(players, restored, Settings);
        var t = new FakeTransport();
        var bob = new PlayerSession("bob", "alpha", null) { Transport = t };
        players.TryAdd(bob);
        relay2.OnPlayerJoined(bob);
        var payload = await WorldSyncPayload(t);
        Assert.Equal(42, (int)payload["chunkDeltas"]![0]![1]![0]!["b"]!);

        var revision = restored.GetRevision("alpha");
        relay2.HandleMessage(bob, Start("tx1", parts.Count));
        for (int i = 0; i < parts.Count; i++) relay2.HandleMessage(bob, Chunk("tx1", i, parts[i], parts.Count));
        Assert.Equal(revision, restored.GetRevision("alpha"));
    }

    [Fact]
    public void DuplicateAndOutOfOrderChunks_AreHandled_AndRepeatedDeliveryIsIdempotent()
    {
        var (alice, _) = Join("alice", "alpha");
        var (bob, b) = Join("bob", "alpha");
        var parts = Split(Payload(), 50);
        Assert.True(parts.Count > 3);

        Relay.HandleMessage(alice, Start("tx1", parts.Count));
        Relay.HandleMessage(alice, Start("tx1", parts.Count)); // duplicate start
        for (int i = parts.Count - 1; i >= 1; i--)
        {
            Relay.HandleMessage(alice, Chunk("tx1", i, parts[i], parts.Count));
            Relay.HandleMessage(alice, Chunk("tx1", i, parts[i], parts.Count)); // duplicate chunk
        }
        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 10, Wz)); // incomplete: nothing applied yet
        Relay.HandleMessage(alice, Chunk("tx1", 0, parts[0], parts.Count));
        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        var revision = Worlds.GetRevision("alpha");

        // Delivered again (by the sender, or by another player who imported the same keyword transaction).
        SendImport(alice, "tx1", Payload());
        SendImport(bob, "tx1", Payload(blockId: 77));

        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.Equal(revision, Worlds.GetRevision("alpha"));
        Assert.Single(b.OfType("ipfs_chunk_update_start"));
        Assert.Equal(0, Relay.PendingImportCount);
    }

    [Fact]
    public void ConflictingImports_FollowMonotonicIpfsDates()
    {
        var (alice, _) = Join("alice", "alpha");

        SendImport(alice, "new", Payload(blockId: 50), timestamp: T2);
        SendImport(alice, "old", Payload(blockId: 40), timestamp: T1, reverse: true);
        Assert.Equal(50, Worlds.GetBlock("alpha", Wx(1), 10, Wz)); // older import does not overwrite

        SendImport(alice, "same", Payload(blockId: 60), timestamp: T2);
        Assert.Equal(60, Worlds.GetBlock("alpha", Wx(1), 10, Wz)); // equal date is accepted (SupGalaxy rule)

        SendImport(alice, "nodate", Payload(blockId: 70, x: 5), timestamp: 0);
        Assert.Null(Worlds.GetBlock("alpha", Wx(5), 10, Wz)); // missing date: blocks rejected like the client
        Assert.True(Worlds.IsImportProcessed("alpha", "nodate"));
    }

    [Fact]
    public void MalformedImports_DoNotChangeState()
    {
        var (alice, a) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        Relay.HandleMessage(alice, """{"type":"block_change","world":"alpha","wx":1,"wy":1,"wz":1,"bid":3}""");
        var revision = Worlds.GetRevision("alpha");

        // Structurally malformed payloads are rejected as a whole.
        string[] bad =
        {
            "{ not json",
            "42",
            """{"deltas":{}}""",
            """{"deltas":[],"foreignBlockOrigins":{}}""",
            """{"deltas":[],"magicianStones":[]}""",
        };
        for (int i = 0; i < bad.Length; i++) SendImport(alice, "bad" + i, bad[i]);

        // Malformed transfer framing.
        Relay.HandleMessage(alice, Start("", 1));
        Relay.HandleMessage(alice, Start("negative", -1));
        Relay.HandleMessage(alice, Start("frac", 1).Replace("\"total\":1", "\"total\":1.5"));
        Relay.HandleMessage(alice, Start("idx", 2));
        Relay.HandleMessage(alice, Chunk("idx", 5, "x", 2));
        Relay.HandleMessage(alice, Start("tot", 2));
        Relay.HandleMessage(alice, Chunk("tot", 0, "[", 3));
        Relay.HandleMessage(alice, Start("conflict", 2));
        Relay.HandleMessage(alice, Chunk("conflict", 0, "[", 2));
        Relay.HandleMessage(alice, Chunk("conflict", 0, "{", 2));
        Relay.HandleMessage(alice, Chunk("conflict", 1, "]", 2));
        Relay.HandleMessage(alice, Chunk("orphan", 0, "[]", 1));

        Assert.Equal(revision, Worlds.GetRevision("alpha"));
        Assert.Equal(3, Worlds.GetBlock("alpha", 1, 1, 1));
        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 1, 3 * 16 + 1));
        Assert.Empty(b.OfType("ipfs_chunk_update_start"));
        Assert.Equal(0, Relay.PendingImportCount);
        Assert.False(Worlds.IsImportProcessed("alpha", "bad0"));

        var results = a.OfType("server_import_result");
        Assert.Contains(results, r => (string?)r["transactionId"] == "bad0" && !(bool)r["ok"]! && (string?)r["reason"] == "malformed_payload");
        Assert.Contains(results, r => (string?)r["transactionId"] == "conflict" && !(bool)r["ok"]! && (bool)r["retry"]!);
        Assert.Contains(results, r => (string?)r["transactionId"] == "idx" && (string?)r["reason"] == "malformed_chunk");
    }

    [Fact]
    public void InvalidEntries_AreSkipped_RestOfImportIsApplied()
    {
        var (alice, a) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");

        // Shapes seen in real SupGalaxy save sessions: stone/chest markers at fractional positions, y outside the
        // world, chunk keys built from a full (>8 char) world name, null metadata fields.
        var payload = """
            {"deltas":[
              {"chunk":"alpha:2:3","changes":[{"x":1,"y":10,"z":4,"b":42},{"x":1.5,"y":1,"z":1,"b":127},{"x":16,"y":1,"z":1,"b":1},
                                              {"x":2,"y":-1,"z":1,"b":1},{"x":2,"y":256,"z":1,"b":1},{"x":2,"y":1,"z":1,"b":"5"},{"x":2,"y":1,"z":1}]},
              {"chunk":"averylongworldname:1:1","changes":[{"x":0,"y":0,"z":0,"b":1}]},
              {"chunk":"bad key","changes":[]},
              {"chunk":"alpha:99999:3","changes":[]},
              "not an object",
              {"chunk":"alpha:2:4","changes":[{"x":3,"y":20,"z":5,"b":7}]}],
             "foreignBlockOrigins":[["33,10,52","seedworld"],["not a key","s"],["1,2"]],
             "magicianStones":{"k":"not an object","33,11,52":{"x":33,"y":11,"z":52,"url":"u"},"bad":{"x":"a","y":1,"z":1}},
             "calligraphyStones":null,
             "chests":{"gone":null}}
            """;
        SendImport(alice, "mixed", payload);

        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.Equal(7, Worlds.GetBlock("alpha", Wx(3), 20, 4 * 16 + 5));
        Assert.Null(Worlds.GetBlock("alpha", Wx(2), 1, 3 * 16 + 1));
        Assert.Equal("seedworld", Worlds.GetForeignOrigin("alpha", 33, 10, 52));
        Assert.True(Worlds.HasStone(StoneKind.Magician, "alpha", "33,11,52"));
        Assert.False(Worlds.HasStone(StoneKind.Magician, "alpha", "k"));
        Assert.True(Worlds.IsImportProcessed("alpha", "mixed"));

        var relayed = Reassemble(b, "ipfs_chunk_update_start", "ipfs_chunk_update_chunk");
        Assert.Equal(2, relayed["deltas"]!.AsArray().Count);
        var result = Assert.Single(a.OfType("server_import_result"));
        Assert.True((bool)result["ok"]!);
        Assert.Equal(2, (int)result["blocks"]!);
    }

    [Fact]
    public void ProcessedTransactionId_IsNotRelayed_SoOthersApplyTheServerFanOut()
    {
        var (alice, _) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");

        // SupGalaxy sends processed_transaction_id right after it starts streaming the import.
        var parts = Split(Payload(), 64);
        Relay.HandleMessage(alice, Start("tx1", parts.Count));
        Relay.HandleMessage(alice, """{"type":"processed_transaction_id","transactionId":"tx1"}""");
        Relay.HandleMessage(alice, """{"type":"sync_processed_transaction","transactionId":"tx1"}""");
        for (int i = 0; i < parts.Count; i++) Relay.HandleMessage(alice, Chunk("tx1", i, parts[i], parts.Count));

        Assert.Empty(b.OfType("processed_transaction_id"));
        Assert.Empty(b.OfType("sync_processed_transaction"));
        Assert.Single(b.OfType("ipfs_chunk_update_start"));
    }

    [Fact]
    public void ManyConcurrentImportsFromOnePlayer_AreAllMerged()
    {
        var (alice, a) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        const int n = 40;
        var parts = Enumerable.Range(0, n).Select(i => Split(Payload(blockId: 100 + i, x: i % 16), 64)).ToList();

        // Interleaved, like SupGalaxy's concurrent sendChunksAsync calls on one data channel.
        for (int i = 0; i < n; i++) Relay.HandleMessage(alice, Start("tx" + i, parts[i].Count));
        for (int c = 0; c < parts.Max(p => p.Count); c++)
            for (int i = 0; i < n; i++)
                if (c < parts[i].Count) Relay.HandleMessage(alice, Chunk("tx" + i, c, parts[i][c], parts[i].Count));

        for (int i = 0; i < n; i++) Assert.True(Worlds.IsImportProcessed("alpha", "tx" + i));
        Assert.Equal(0, Relay.PendingImportCount);
        Assert.Equal(n, b.OfType("ipfs_chunk_update_start").Count);
        Assert.Equal(n, a.OfType("server_import_result").Count(r => (bool)r["ok"]!));
    }

    [Fact]
    public void PendingCharBudget_RejectsWithRetry()
    {
        Settings.MaxImportSize = 2048;
        Settings.MaxPendingImportCharsPerPlayer = 2048;
        var (alice, a) = Join("alice", "alpha");

        Relay.HandleMessage(alice, Start("one", 2));
        Relay.HandleMessage(alice, Chunk("one", 0, new string('x', 1500), 2));
        Relay.HandleMessage(alice, Start("two", 2));
        Relay.HandleMessage(alice, Chunk("two", 0, new string('y', 1000), 2));

        var r = Assert.Single(a.OfType("server_import_result"));
        Assert.Equal("two", (string?)r["transactionId"]);
        Assert.Equal("busy", (string?)r["reason"]);
        Assert.True((bool)r["retry"]!);
        Assert.Equal(1, Relay.PendingImportCount);
    }

    [Fact]
    public void OversizedImport_IsRejected()
    {
        Settings.MaxImportSize = 2048;
        var (alice, _) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");

        var big = Payload().Replace("\"text\":\"hi\"", "\"text\":\"" + new string('x', 4000) + "\"");
        SendImport(alice, "big", big, chunkSize: 1000);
        Relay.HandleMessage(alice, Start("toomany", 100_000));

        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.False(Worlds.IsImportProcessed("alpha", "big"));
        Assert.Empty(b.OfType("ipfs_chunk_update_start"));
        Assert.Equal(0, Relay.PendingImportCount);
    }

    [Fact]
    public void IncompleteAndTimedOutImports_AreDiscarded()
    {
        long now = 1_000_000;
        Relay.Clock = () => now;
        Settings.ImportTimeoutSeconds = 30;
        var (alice, a) = Join("alice", "alpha");
        var parts = Split(Payload(), 64);

        Relay.HandleMessage(alice, Start("slow", parts.Count));
        for (int i = 0; i < parts.Count - 1; i++) Relay.HandleMessage(alice, Chunk("slow", i, parts[i], parts.Count));
        Assert.Equal(1, Relay.PendingImportCount);
        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 10, Wz));

        now += 31_000;
        Assert.Equal(1, Relay.ExpireStaleImports());
        Assert.Contains(a.OfType("server_import_result"), r => (string?)r["transactionId"] == "slow" && (string?)r["reason"] == "timeout" && (bool)r["retry"]!);
        Relay.HandleMessage(alice, Chunk("slow", parts.Count - 1, parts[^1], parts.Count)); // late last chunk
        Assert.Null(Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        Assert.False(Worlds.IsImportProcessed("alpha", "slow"));

        // Too many unfinished transfers per player.
        for (int i = 0; i < Settings.MaxPendingImportsPerPlayer + 2; i++) Relay.HandleMessage(alice, Start("p" + i, 2));
        Assert.Equal(Settings.MaxPendingImportsPerPlayer, Relay.PendingImportCount);

        // A retry after the stalled transfers time out still works.
        now += 31_000;
        SendImport(alice, "slow", Payload());
        Assert.Equal(42, Worlds.GetBlock("alpha", Wx(1), 10, Wz));

        // Leaving discards the sender's unfinished transfers.
        Players.Remove(alice);
        Relay.OnPlayerLeft(alice);
        Assert.Equal(0, Relay.PendingImportCount);
    }

    [Fact]
    public async Task UpdatesDuringInitialSync_AreDeliveredAfterTheSnapshot()
    {
        var (alice, _) = Join("alice", "alpha");
        Relay.HandleMessage(alice, """{"type":"block_change","world":"alpha","wx":1,"wy":1,"wz":1,"bid":3}""");

        // Bob's channel is backed up, so his snapshot stalls after world_sync_start.
        var t = new FakeTransport { BufferedAmount = 10 * 1024 * 1024 };
        var bob = new PlayerSession("bob", "alpha", "127.0.0.1") { Transport = t };
        Assert.True(Players.TryAdd(bob));
        Relay.OnPlayerJoined(bob);
        await Eventually(() => t.OfType("world_sync_start").Count == 1);

        Relay.HandleMessage(alice, """{"type":"block_change","world":"alpha","wx":1,"wy":1,"wz":1,"bid":4}""");
        SendImport(alice, "tx1", Payload());
        Relay.HandleMessage(alice, """{"type":"player_move","username":"alice","world":"alpha","x":1,"y":2,"z":3}""");
        Relay.HandleMessage(alice, """{"type":"chat","username":"alice","message":"hi"}""");

        await Task.Delay(100);
        Assert.Empty(t.OfType("world_sync_chunk"));
        Assert.Empty(t.OfType("block_change"));
        Assert.Empty(t.OfType("ipfs_chunk_update_start"));
        Assert.Single(t.OfType("chat")); // non-state traffic is not held back

        t.BufferedAmount = 0;
        await Eventually(() => t.OfType("ipfs_chunk_update_chunk").Count > 0
                               && t.OfType("ipfs_chunk_update_chunk").Count == (int)t.OfType("ipfs_chunk_update_start")[0]["total"]!);

        var types = t.Messages.Select(m => (string)m["type"]!).ToList();
        int lastSync = types.LastIndexOf("world_sync_chunk");
        Assert.True(lastSync >= 0);
        Assert.True(types.IndexOf("block_change") > lastSync);
        Assert.True(types.IndexOf("ipfs_chunk_update_start") > types.IndexOf("block_change"));

        // The snapshot reflects the state before the held back edit, which then follows it.
        var snapshot = Reassemble(t, "world_sync_start", "world_sync_chunk");
        Assert.Equal(3, (int)snapshot["chunkDeltas"]![0]![1]![0]!["b"]!);
        Assert.Equal(4, (int)Assert.Single(t.OfType("block_change"))["bid"]!);
        Assert.Equal(42, (int)Reassemble(t, "ipfs_chunk_update_start", "ipfs_chunk_update_chunk")["deltas"]![0]!["changes"]![0]!["b"]!);
    }

    [Fact]
    public void LegacyImportPayloadArray_IsAccepted()
    {
        var (alice, _) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        SendImport(alice, "arr", """[{"chunk":"alpha:2:3","changes":[{"x":1,"y":10,"z":4,"b":5}],"ownershipNeutral":true}]""");

        Assert.Equal(5, Worlds.GetBlock("alpha", Wx(1), 10, Wz));
        var relayed = Reassemble(b, "ipfs_chunk_update_start", "ipfs_chunk_update_chunk");
        Assert.True((bool)relayed[0]!["ownershipNeutral"]!);
    }
}
