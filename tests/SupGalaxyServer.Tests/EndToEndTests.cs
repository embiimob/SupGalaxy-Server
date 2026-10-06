using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SupGalaxyServer.Tests;

/// <summary>Minimal stand-in for the SupGalaxy browser client: offer -> POST /connect -> answer -> "game" channel.</summary>
internal sealed class RtcTestClient : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private RTCDataChannel? _dc;

    public ConcurrentQueue<JsonObject> Received { get; } = new();

    private RtcTestClient() => _pc = new RTCPeerConnection(new RTCConfiguration());

    public string? AnswerSdp { get; private set; }

    public static async Task<(RtcTestClient Client, HttpResponseMessage Response)> ConnectAsync(HttpClient http, string user, string world, bool withAudio = false)
    {
        var client = new RtcTestClient();
        if (withAudio)
        {
            // SupGalaxy adds its microphone track to every peer connection it creates.
            client._pc.addTrack(new MediaStreamTrack(SDPWellKnownMediaFormatsEnum.PCMU));
        }
        var dc = await client._pc.createDataChannel("game");
        client._dc = dc;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dc.onopen += () => opened.TrySetResult();
        dc.onmessage += (_, _, data) => client.Received.Enqueue((JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(data))!);

        var offer = client._pc.createOffer();
        await client._pc.setLocalDescription(offer);

        var response = await http.PostAsJsonAsync("/connect", new
        {
            world,
            user,
            offer = new { type = "offer", sdp = offer.sdp },
            iceCandidates = Array.Empty<object>(),
        });
        if (!response.IsSuccessStatusCode) return (client, response);

        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        client.AnswerSdp = (string)body["answer"]!["sdp"]!;
        var result = client._pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = (string)body["answer"]!["sdp"]!,
        });
        Assert.Equal(SetDescriptionResultEnum.OK, result);
        foreach (var c in body["iceCandidates"]!.AsArray())
        {
            client._pc.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = (string)c!["candidate"]!,
                sdpMid = (string?)c["sdpMid"] ?? "0",
                sdpMLineIndex = (ushort)((int?)c["sdpMLineIndex"] ?? 0),
            });
        }

        if (await Task.WhenAny(opened.Task, Task.Delay(15000)) != opened.Task)
            Assert.Fail($"Data channel for {user} did not open.");
        return (client, response);
    }

    // Same escaping as the browser's JSON.stringify.
    public void Send(object message) => _dc!.send(System.Text.Json.JsonSerializer.Serialize(message, WireFormat.Options));

    public async Task<JsonObject> WaitFor(string type, Func<JsonObject, bool>? predicate = null, int timeoutMs = 10000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            var match = Received.FirstOrDefault(m => (string?)m["type"] == type && (predicate == null || predicate(m)));
            if (match != null) return match;
            await Task.Delay(20);
        }
        throw new TimeoutException($"No '{type}' message received.");
    }

    public void Dispose() => _pc.close();
}

public class EndToEndTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private SupGalaxyServerHost _host = null!;
    private HttpClient _http = null!;
    private readonly List<string> _log = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public EndToEndTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        var basePort = 41000 + Random.Shared.Next(0, 2000) * 10;
        var settings = new ServerSettings
        {
            ServerName = "E2EServer",
            SignalingPort = FreeTcpPort(),
            PlayerPortStart = basePort,
            PlayerPortEnd = basePort + 9,
            IceServers = new List<string>(),
        };
        _host = new SupGalaxyServerHost(settings, _dir.Path);
        _host.Log += line => { lock (_log) _log.Add(line); };
        await _host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.SignalingPort}") };
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        lock (_log) foreach (var line in _log) _output.WriteLine(line);
        _http.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task HealthAndInfo_RespondOnLoopback()
    {
        var health = await _http.GetFromJsonAsync<JsonObject>("/health");
        Assert.Equal("ok", (string?)health!["status"]);
        var info = await _http.GetFromJsonAsync<JsonObject>("/info");
        Assert.Equal("E2EServer", (string?)info!["name"]);
        Assert.Equal(5, (int)info["maxPlayers"]!);
    }

    [Fact]
    public async Task Cors_AllowsBrowserOrigins()
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/connect");
        req.Headers.Add("Origin", "https://supgalaxy.example");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Private-Network", "true");
        var res = await _http.SendAsync(req);
        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("true", res.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
    }

    [Fact]
    public async Task TwoPlayers_ConnectOverWebRtc_AndRelayMessages()
    {
        var (alice, r1) = await RtcTestClient.ConnectAsync(_http, "alice", "alpha");
        using var _a = alice;
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        await alice.WaitFor("server_welcome");
        await alice.WaitFor("server_authority", m => (string?)m["username"] == "alice");

        var (bob, r2) = await RtcTestClient.ConnectAsync(_http, "bob", "alpha");
        using var _b = bob;
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        await alice.WaitFor("new_player", m => (string?)m["username"] == "bob");
        await bob.WaitFor("new_player", m => (string?)m["username"] == "alice");

        bob.Send(new { type = "chat", username = "bob", message = "hello alice" });
        var chat = await alice.WaitFor("chat");
        Assert.Equal("hello alice", (string?)chat["message"]);

        // Block placement by the authority is persisted and later synced to new players.
        alice.Send(new { type = "block_change", world = "alpha", wx = 3, wy = 10, wz = 4, bid = 42, username = "alice" });
        await bob.WaitFor("block_change");
        Assert.Equal(42, _host.Worlds.GetBlock("alpha", 3, 10, 4));

        var players = _host.GetPlayers();
        Assert.Equal(new[] { "alice", "bob" }, players.Select(p => p.Username));
        Assert.All(players, p => Assert.InRange(p.Port, _host.Settings.PlayerPortStart, _host.Settings.PlayerPortEnd));
        Assert.True(players.Single(p => p.Username == "alice").IsWorldAuthority);

        var (carol, r3) = await RtcTestClient.ConnectAsync(_http, "carol", "alpha");
        using var _c = carol;
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        var sync = await carol.WaitFor("world_sync_chunk");
        Assert.Contains("\"b\":42", (string)sync["chunk"]!);
    }

    [Fact]
    public async Task OfferWithAudioTrack_StillConnects()
    {
        var (dave, r) = await RtcTestClient.ConnectAsync(_http, "dave", "alpha", withAudio: true);
        using var _d = dave;
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await dave.WaitFor("server_welcome");
        Assert.Contains("a=candidate", dave.AnswerSdp);
    }

    [Fact]
    public async Task DuplicateName_IsRejected_AndKickFreesTheName()
    {
        var (alice, _) = await RtcTestClient.ConnectAsync(_http, "alice", "alpha");
        using var _a = alice;
        await alice.WaitFor("server_welcome");

        var (dup, r) = await RtcTestClient.ConnectAsync(_http, "ALICE", "alpha");
        dup.Dispose();
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        Assert.True(_host.Kick("alice"));
        await alice.WaitFor("server_kick");
        await RelayTestBase.Eventually(() => _host.Players.Count == 0);
        Assert.Equal(0, _host.Ports.InUseCount);

        var (again, r2) = await RtcTestClient.ConnectAsync(_http, "alice", "alpha");
        using var _again = again;
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Fact]
    public async Task BlockedName_IsRejected_UntilUnblocked()
    {
        _host.Block("Mallory");

        var (c1, r1) = await RtcTestClient.ConnectAsync(_http, "mallory", "alpha");
        c1.Dispose();
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);

        _host.Unblock("mallory");
        var (c2, r2) = await RtcTestClient.ConnectAsync(_http, "mallory", "alpha");
        using var _c2 = c2;
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Fact]
    public async Task InvalidRequests_AreRejected()
    {
        var bad = await _http.PostAsync("/connect", new StringContent("nope", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var noOffer = await _http.PostAsJsonAsync("/connect", new { user = "x", world = "w" });
        Assert.Equal(HttpStatusCode.BadRequest, noOffer.StatusCode);

        var reserved = await _http.PostAsJsonAsync("/connect", new { user = "E2EServer", world = "w", offer = new { type = "offer", sdp = "v=0" } });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
    }

    [Fact]
    public async Task StopAndRestart_RestoresSavedWorld()
    {
        var (alice, _) = await RtcTestClient.ConnectAsync(_http, "alice", "alpha");
        using (alice)
        {
            await alice.WaitFor("server_welcome");
            alice.Send(new { type = "block_change", world = "alpha", wx = 1, wy = 2, wz = 3, bid = 77 });
            await RelayTestBase.Eventually(() => _host.Worlds.GetBlock("alpha", 1, 2, 3) == 77);
        }

        await _host.StopAsync();
        var restarted = new SupGalaxyServerHost(_host.Settings, _dir.Path);
        await restarted.StartAsync();
        try
        {
            Assert.Equal(77, restarted.Worlds.GetBlock("alpha", 1, 2, 3));
        }
        finally
        {
            await restarted.StopAsync();
        }
        _host = restarted;
    }

    [Fact]
    public async Task LargeImport_OverWebRtc_IsMergedSharedAndSyncedToLateJoiner()
    {
        var (alice, _) = await RtcTestClient.ConnectAsync(_http, "alice", "Ender");
        using var _a = alice;
        await alice.WaitFor("server_welcome");
        var (bob, _) = await RtcTestClient.ConnectAsync(_http, "bob", "Ender");
        using var _b = bob;
        await bob.WaitFor("server_welcome");

        // A city-sized save session: many chunks, sent like SupGalaxy's applyChunkUpdates (128K char pieces).
        var deltas = new JsonArray();
        for (int c = 0; c < 400; c++)
        {
            var changes = new JsonArray();
            for (int i = 0; i < 64; i++) changes.Add(new JsonObject { ["x"] = i % 16, ["y"] = 60 + i / 16, ["z"] = (i * 7) % 16, ["b"] = 5 + i % 3 });
            deltas.Add(new JsonObject { ["chunk"] = $"Ender:{c % 20}:{c / 20}", ["changes"] = changes });
        }
        var payload = new JsonObject { ["deltas"] = deltas, ["foreignBlockOrigins"] = null, ["magicianStones"] = null }.ToJsonString();
        const int size = 131072;
        var parts = new List<string>();
        for (int i = 0; i < payload.Length; i += size) parts.Add(payload.Substring(i, Math.Min(size, payload.Length - i)));
        Assert.True(parts.Count > 5);

        alice.Send(new { type = "ipfs_chunk_from_client_start", total = parts.Count, fromAddress = "addr", timestamp = 1790000000000L, world = "Ender", transactionId = "city" });
        for (int i = 0; i < parts.Count; i++)
            alice.Send(new { type = "ipfs_chunk_from_client_chunk", transactionId = "city", index = i, chunk = parts[i], total = parts.Count });

        var ack = await alice.WaitFor("server_import_result", timeoutMs: 20000);
        Assert.True((bool)ack["ok"]!, ack.ToJsonString());
        Assert.True(_host.Worlds.IsImportProcessed("Ender", "city"));
        await RelayTestBase.Eventually(() => bob.Received.Count(m => (string?)m["type"] == "ipfs_chunk_update_chunk") == parts.Count, 20000);

        var (carol, _) = await RtcTestClient.ConnectAsync(_http, "carol", "Ender");
        using var _c = carol;
        var start = await carol.WaitFor("world_sync_start");
        int total = (int)start["total"]!;
        await RelayTestBase.Eventually(() => carol.Received.Count(m => (string?)m["type"] == "world_sync_chunk") == total, 20000);
        var sync = JsonNode.Parse(string.Concat(carol.Received.Where(m => (string?)m["type"] == "world_sync_chunk")
            .OrderBy(m => (int)m["index"]!).Select(m => (string)m["chunk"]!)))!;
        Assert.Equal(400, sync["chunkDeltas"]!.AsArray().Count);
        Assert.Contains("city", sync["processedIds"]!.AsArray().Select(n => (string?)n));
    }
}
