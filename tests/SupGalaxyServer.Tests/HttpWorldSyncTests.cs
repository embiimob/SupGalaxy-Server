using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace SupGalaxyServer.Tests;

public class HttpWorldSyncTests : RelayTestBase
{
    private (PlayerSession Session, FakeTransport Transport) JoinFast(string name, string world = "alpha")
    {
        var t = new FakeTransport();
        var s = new PlayerSession(name, world, "127.0.0.1")
        {
            Transport = t,
            Features = new HashSet<string> { GameRelay.HttpWorldSyncFeature },
        };
        Assert.True(Players.TryAdd(s));
        Relay.OnPlayerJoined(s);
        return (s, t);
    }

    private static string Token(JsonObject offer) => ((string)offer["path"]!)[GameRelay.HttpSyncPathPrefix.Length..];

    private static string Unzip(byte[] gzip)
    {
        using var gz = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task<JsonObject> WaitOffer(FakeTransport t)
    {
        await Eventually(() => t.OfType(GameRelay.HttpSyncOfferType).Count > 0);
        return t.OfType(GameRelay.HttpSyncOfferType).Last();
    }

    private void Edit(PlayerSession who, int bid) =>
        Relay.HandleMessage(who, $$"""{"type":"block_change","world":"alpha","wx":1,"wy":2,"wz":3,"bid":{{bid}}}""");

    [Fact]
    public async Task FastClient_GetsHttpOffer_WithSameSnapshotAsDataChannelSync()
    {
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var expected = Worlds.BuildSyncPayload("alpha");

        var (_, b) = JoinFast("bob");
        var offer = await WaitOffer(b);
        Assert.Empty(b.OfType("world_sync_start"));
        Assert.Equal("alpha", (string?)offer["world"]);
        Assert.Equal(Worlds.GetRevision("alpha"), (long)offer["revision"]!);

        var download = Relay.BeginHttpSyncDownload(Token(offer));
        Assert.NotNull(download);
        var (gzip, raw) = await download!;
        Assert.Equal(expected, Unzip(gzip));
        Assert.Equal(Encoding.UTF8.GetByteCount(expected!), raw);
        Assert.Equal(raw, (long)offer["bytes"]!);
    }

    [Fact]
    public async Task LegacyClient_StillGetsDataChannelSync()
    {
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (_, b) = Join("bob");
        await Eventually(() => b.OfType("world_sync_chunk").Count > 0);
        Assert.Empty(b.OfType(GameRelay.HttpSyncOfferType));
    }

    [Fact]
    public async Task UpdatesDuringHttpSync_AreHeldUntilDone_ThenDelivered()
    {
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (bob, b) = JoinFast("bob");
        var offer = await WaitOffer(b);

        Edit(alice, 43);
        Assert.Empty(b.OfType("block_change"));

        await Relay.BeginHttpSyncDownload(Token(offer))!;
        Relay.HandleMessage(bob, $$"""{"type":"{{GameRelay.HttpSyncDoneType}}","transactionId":"{{offer["transactionId"]}}"}""");
        var change = Assert.Single(b.OfType("block_change"));
        Assert.Equal(43, (int)change["bid"]!);
        Assert.Equal(0, Relay.PendingHttpSyncCount);
        Assert.Null(Relay.BeginHttpSyncDownload(Token(offer)));

        Edit(alice, 44);
        Assert.Equal(2, b.OfType("block_change").Count);
    }

    [Fact]
    public async Task ClientFailure_FallsBackToDataChannel_WithoutGap()
    {
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (bob, b) = JoinFast("bob");
        var offer = await WaitOffer(b);
        Edit(alice, 43);

        Relay.HandleMessage(bob, $$"""{"type":"{{GameRelay.HttpSyncFailedType}}","transactionId":"{{offer["transactionId"]}}"}""");
        await Eventually(() => b.OfType("block_change").Count == 1);
        var messages = b.Messages.Select(m => (string?)m["type"]).ToList();
        Assert.True(messages.IndexOf("world_sync_chunk") < messages.IndexOf("block_change"));
        Assert.Contains("\"b\":43", string.Concat(b.OfType("world_sync_chunk").Select(m => (string)m["chunk"]!)));
    }

    [Fact]
    public async Task UnfetchedOffer_TimesOut_AndFallsBack()
    {
        long now = 1_000_000;
        Relay.Clock = () => now;
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (_, b) = JoinFast("bob");
        await WaitOffer(b);

        Assert.Equal(0, Relay.ExpireHttpSyncs());
        now += Settings.HttpWorldSyncFetchTimeoutSeconds * 1000L + 1;
        Assert.Equal(1, Relay.ExpireHttpSyncs());
        await Eventually(() => b.OfType("world_sync_chunk").Count > 0);
        Assert.Equal(0, Relay.PendingHttpSyncCount);
    }

    [Fact]
    public async Task FetchedButUnconfirmedOffer_TimesOut_AndFallsBack()
    {
        long now = 1_000_000;
        Relay.Clock = () => now;
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (_, b) = JoinFast("bob");
        var offer = await WaitOffer(b);
        await Relay.BeginHttpSyncDownload(Token(offer))!;

        now += Settings.HttpWorldSyncFetchTimeoutSeconds * 1000L + 1;
        Assert.Equal(0, Relay.ExpireHttpSyncs());
        now += Settings.HttpWorldSyncTimeoutSeconds * 1000L;
        Assert.Equal(1, Relay.ExpireHttpSyncs());
        await Eventually(() => b.OfType("world_sync_chunk").Count > 0);
    }

    [Fact]
    public async Task Tokens_AreUnguessable_PerPlayer_AndDroppedOnDisconnect()
    {
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (bob, b) = JoinFast("bob");
        var (carol, c) = JoinFast("carol");
        var ob = await WaitOffer(b);
        var oc = await WaitOffer(c);
        Assert.NotEqual(Token(ob), Token(oc));
        Assert.Equal(64, Token(ob).Length);
        Assert.Null(Relay.BeginHttpSyncDownload("nope"));

        // Another player cannot complete bob's sync.
        Relay.HandleMessage(carol, $$"""{"type":"{{GameRelay.HttpSyncDoneType}}","transactionId":"{{ob["transactionId"]}}"}""");
        Assert.Equal(2, Relay.PendingHttpSyncCount);

        Players.Remove(bob);
        bob.State = PlayerState.Disconnected;
        Relay.OnPlayerLeft(bob);
        Assert.Null(Relay.BeginHttpSyncDownload(Token(ob)));
        Assert.NotNull(Relay.BeginHttpSyncDownload(Token(oc)));
    }

    [Fact]
    public async Task ClientCannotForgeOffer_AndUnchangedWorldReusesSnapshot()
    {
        var (alice, a) = Join("alice");
        Edit(alice, 42);
        var (_, b) = JoinFast("bob");
        var (_, c) = JoinFast("carol");
        var ob = await WaitOffer(b);
        var oc = await WaitOffer(c);
        var gb = (await Relay.BeginHttpSyncDownload(Token(ob))!).Gzip;
        var gc = (await Relay.BeginHttpSyncDownload(Token(oc))!).Gzip;
        Assert.Same(gb, gc);

        Relay.HandleMessage(alice, $$"""{"type":"{{GameRelay.HttpSyncOfferType}}","path":"/evil"}""");
        Assert.Single(b.OfType(GameRelay.HttpSyncOfferType));
    }

    [Fact]
    public async Task Disabled_UsesDataChannel()
    {
        Settings.EnableHttpWorldSync = false;
        var (alice, _) = Join("alice");
        Edit(alice, 42);
        var (_, b) = JoinFast("bob");
        await Eventually(() => b.OfType("world_sync_start").Count == 1);
        Assert.Empty(b.OfType(GameRelay.HttpSyncOfferType));
    }
}
