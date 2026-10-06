using System.Text.Json.Nodes;

namespace SupGalaxyServer.Tests;

public class GameRelayTests : RelayTestBase
{
    [Fact]
    public void Join_IntroducesPlayersToEachOther()
    {
        var (_, a) = Join("alice");
        var (_, b) = Join("bob");

        Assert.Contains(a.OfType("new_player"), m => (string?)m["username"] == "bob");
        Assert.Contains(b.OfType("new_player"), m => (string?)m["username"] == "alice");
        var welcome = Assert.Single(b.OfType("server_welcome"));
        Assert.Equal("TestServer", (string?)welcome["serverName"]);
        Assert.Equal("alice", (string?)welcome["players"]![0]);
    }

    [Fact]
    public void Chat_IsRelayedToEveryoneButSender()
    {
        var (alice, a) = Join("alice", "alpha");
        var (_, b) = Join("bob", "beta");
        a.Clear();

        Relay.HandleMessage(alice, """{"type":"chat","username":"alice","message":"hi"}""");

        Assert.Single(b.OfType("chat"));
        Assert.Empty(a.OfType("chat"));
    }

    [Fact]
    public void PlayerMove_IsScopedToWorld_AndUpdatesPosition()
    {
        var (alice, _) = Join("alice", "alpha");
        var (_, b) = Join("bob", "alpha");
        var (_, c) = Join("carol", "beta");

        Relay.HandleMessage(alice, """{"type":"player_move","username":"alice","world":"alpha","x":1.5,"y":20,"z":-3}""");

        Assert.Single(b.OfType("player_move"));
        Assert.Empty(c.OfType("player_move"));
        Assert.Equal(1.5, alice.X);
        Assert.Equal(-3, alice.Z);
    }

    [Fact]
    public void PlayerMove_DroppedForCongestedReceiver()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");
        b.BufferedAmount = 10 * 1024 * 1024;

        Relay.HandleMessage(alice, """{"type":"player_move","username":"alice","world":"alpha","x":1,"y":2,"z":3}""");
        Relay.HandleMessage(alice, """{"type":"chat","username":"alice","message":"still delivered"}""");

        Assert.Empty(b.OfType("player_move"));
        Assert.Single(b.OfType("chat"));
    }

    [Fact]
    public void SpoofedUsername_IsRewritten()
    {
        Join("alice");
        var (bob, _) = Join("bob");
        var (_, c) = Join("carol");

        Relay.HandleMessage(bob, """{"type":"chat","username":"alice","message":"I am alice"}""");

        Assert.Equal("bob", (string?)Assert.Single(c.OfType("chat"))["username"]);
    }

    [Fact]
    public void ServerOnlyMessages_FromClients_AreDropped()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");
        b.Clear();

        Relay.HandleMessage(alice, """{"type":"remove_peer","username":"bob"}""");
        Relay.HandleMessage(alice, """{"type":"server_kick","reason":"x"}""");
        Relay.HandleMessage(alice, """{"type":"new_player","username":"ghost"}""");
        Relay.HandleMessage(alice, "not json");

        Assert.Empty(b.Sent);
    }

    [Fact]
    public void PlayerLeaving_IsAnnounced()
    {
        var (alice, _) = Join("alice");
        var (_, b) = Join("bob");

        Players.Remove(alice);
        alice.State = PlayerState.Disconnected;
        Relay.OnPlayerLeft(alice);

        Assert.Contains(b.OfType("remove_peer"), m => (string?)m["username"] == "alice");
    }

    [Fact]
    public void P2pSignal_IsDeliveredOnlyToTarget()
    {
        var (_, a) = Join("alice");
        var (bob, _) = Join("bob");
        var (_, c) = Join("carol");

        Relay.HandleMessage(bob, """{"type":"p2p_signal","to":"alice","signal":{"type":"offer","sdp":"v=0"}}""");

        Assert.Single(a.OfType("p2p_signal"));
        Assert.Empty(c.OfType("p2p_signal"));
    }

    [Fact]
    public async Task EnteringAWorldWithSavedEdits_StreamsWorldSync()
    {
        Worlds.ApplyBlock("alpha", 5, 6, 7, 12, null);

        var (_, a) = Join("alice", "alpha");

        await Eventually(() => a.OfType("world_sync_chunk").Count == 1);
        var start = Assert.Single(a.OfType("world_sync_start"));
        Assert.Equal(1, (int)start["total"]!);
        var chunk = a.OfType("world_sync_chunk")[0];
        var payload = JsonNode.Parse((string)chunk["chunk"]!)!;
        var delta = payload["chunkDeltas"]![0]!;
        Assert.Equal("alpha:0:0", (string?)delta[0]);
        Assert.Equal(12, (int)delta[1]![0]!["b"]!);
    }

    [Fact]
    public void RateLimit_DropsExcessMessages()
    {
        Settings.MaxMessagesPerSecond = 5;
        var (_, a) = Join("alice");
        var (bob, _) = Join("bob");

        for (int i = 0; i < 20; i++) Relay.HandleMessage(bob, """{"type":"chat","message":"spam"}""");

        Assert.Equal(5, a.OfType("chat").Count);
    }
}
