using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace SupGalaxyServer;

/// <summary>
/// The SupGalaxy dedicated server: HTTP(S) signaling listener on the main port, one WebRTC peer connection
/// per player on a port from the player range, the <see cref="GameRelay"/>, periodic incremental saves and
/// the admin operations (kick / block / unblock / reset) used by the GUI and the headless console.
/// </summary>
public sealed class SupGalaxyServerHost : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private WebApplication? _app;
    private Timer? _saveTimer;
    private Timer? _healthTimer;
    private CancellationTokenSource? _cts;

    /// <summary>Players must send something (SupGalaxy sends i_am_alive every 10s) within this time.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Time allowed between the answer being returned and the data channel opening.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time spent gathering server ICE candidates before returning the answer.</summary>
    public TimeSpan IceGatherTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    public SupGalaxyServerHost(ServerSettings settings, string dataDirectory)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(settings));

        Settings = settings.Clone();
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        BlockList = new BlockList(Path.Combine(dataDirectory, "blocked.json"));
        Worlds = new WorldStateStore(Path.Combine(dataDirectory, "session"));
        Players = new PlayerRegistry();
        Ports = new PortAllocator(Settings.PlayerPortStart, Settings.PlayerPortEnd, evenOnly: true);
        Relay = new GameRelay(Players, Worlds, Settings, WriteLog);
        Relay.AuthorityChanged += (_, _) => PlayersChanged?.Invoke();
        Relay.WorldImported += _ => ScheduleImportSave();
    }

    private int _importSaveQueued;

    /// <summary>Imported discoveries are saved within a few seconds instead of waiting for the next incremental save.</summary>
    private void ScheduleImportSave()
    {
        if (Interlocked.Exchange(ref _importSaveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Interlocked.Exchange(ref _importSaveQueued, 0);
            SaveNowSafe("Import save");
        });
    }

    public ServerSettings Settings { get; }
    public string DataDirectory { get; }
    public BlockList BlockList { get; }
    public WorldStateStore Worlds { get; }
    public PlayerRegistry Players { get; }
    public PortAllocator Ports { get; }
    public GameRelay Relay { get; }

    public bool IsRunning { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public IReadOnlyList<string> ListeningUrls { get; private set; } = Array.Empty<string>();

    /// <summary>Human readable log lines (thread pool threads - marshal to the UI thread before touching controls).</summary>
    public event Action<string>? Log;

    /// <summary>Raised when a player connects, disconnects or a world authority changes.</summary>
    public event Action? PlayersChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (IsRunning || _app != null) throw new InvalidOperationException("Server is already running.");
        }

        int loaded = Worlds.Load();
        if (loaded > 0) WriteLog($"Restored {loaded} world(s) from the last save ({Worlds.Directory}).");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddCors();
        builder.WebHost.ConfigureKestrel(ConfigureKestrel);

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // Chrome Private Network Access: allow public https pages (the SupGalaxy site) to reach a
            // server on 127.0.0.1 / a LAN address.
            if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            await next(ctx);
        });
        app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

        app.MapGet("/", () => Results.Json(InfoObject()));
        app.MapGet("/info", () => Results.Json(InfoObject()));
        app.MapGet("/health", () => Results.Json(new { status = "ok", players = Players.Count }));
        app.MapPost("/connect", (Delegate)HandleConnectAsync);
        app.MapGet("/world-sync/{token}", (Delegate)HandleWorldSyncDownloadAsync);

        _cts = new CancellationTokenSource();
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lifecycleGate)
        {
            _app = app;
            IsRunning = true;
            StartedAtUtc = DateTime.UtcNow;
        }

        ListeningUrls = BuildListeningUrls();
        var interval = TimeSpan.FromMinutes(Settings.SaveIntervalMinutes);
        _saveTimer = new Timer(_ => SaveNowSafe("Incremental save"), null, interval, interval);
        _healthTimer = new Timer(_ => CheckPlayerHealth(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        WriteLog($"Server '{Settings.ServerName}' listening on {string.Join(", ", ListeningUrls)}; " +
                 $"player ports {Settings.PlayerPortStart}-{Settings.PlayerPortEnd} ({Ports.Capacity} slots).");
    }

    public async Task StopAsync()
    {
        WebApplication? app;
        lock (_lifecycleGate)
        {
            app = _app;
            _app = null;
            if (app == null) return;
            IsRunning = false;
        }

        _saveTimer?.Dispose();
        _healthTimer?.Dispose();
        _cts?.Cancel();

        foreach (var p in Players.All())
        {
            p.Send(GameRelay.Json(new JsonObject { ["type"] = "server_kick", ["reason"] = "Server shutting down." }));
            Disconnect(p, "server stopped", notifyOthers: false);
        }

        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        SaveNowSafe("Shutdown save");
        WriteLog("Server stopped.");
        PlayersChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Writes every world changed since the last save. Returns the number of world files written.</summary>
    public int SaveNow() => Worlds.Save();

    /// <summary>
    /// Throws away the saved session (all world edits) both in memory and on disk, so the server starts fresh.
    /// Connected players stay connected. The block list and settings are kept.
    /// </summary>
    public void ResetSavedSession()
    {
        Worlds.Reset();
        foreach (var p in Players.All())
            lock (p.SyncedWorlds) p.SyncedWorlds.Clear();
        WriteLog("Saved session discarded. The server is starting fresh.");
    }

    /// <summary>Disconnects a player. Returns false if no such player is connected.</summary>
    public bool Kick(string username, string reason = "Kicked by the server administrator.")
    {
        var p = Players.Get(username);
        if (p == null) return false;
        p.Send(GameRelay.Json(new JsonObject { ["type"] = "server_kick", ["reason"] = reason }));
        WriteLog($"Kicked {p.Username}: {reason}");
        // Give the kick notice a moment to flush before closing the connection.
        _ = Task.Delay(250).ContinueWith(_ => Disconnect(p, "kicked"), TaskScheduler.Default);
        return true;
    }

    /// <summary>Blocks a user name (case-insensitive) and kicks it if connected.</summary>
    public bool Block(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var added = BlockList.Block(username);
        if (added) WriteLog($"Blocked user name '{username.Trim()}'.");
        Kick(username.Trim(), "You have been blocked from this server.");
        return added;
    }

    public bool Unblock(string username)
    {
        var removed = BlockList.Unblock(username);
        if (removed) WriteLog($"Unblocked user name '{username.Trim()}'.");
        return removed;
    }

    /// <summary>Alphabetical, searchable snapshot of all players.</summary>
    public PlayerInfo[] GetPlayers(string? filter = null) =>
        Players.Search(filter).Select(p => p.ToInfo(Relay.IsAuthority(p))).ToArray();

    private object InfoObject() => new
    {
        name = Settings.ServerName,
        software = "SupGalaxy-Server",
        protocol = GameRelay.ProtocolVersion,
        players = Players.Count,
        maxPlayers = Ports.Capacity,
        playerPortStart = Settings.PlayerPortStart,
        playerPortEnd = Settings.PlayerPortEnd,
        connect = "POST /connect",
        features = SupportedFeatures(),
    };

    private string[] SupportedFeatures() =>
        Settings.EnableHttpWorldSync ? new[] { GameRelay.HttpWorldSyncFeature } : Array.Empty<string>();

    private void ConfigureKestrel(KestrelServerOptions k)
    {
        k.Limits.MaxRequestBodySize = 256 * 1024;
        k.AddServerHeader = false;

        void Configure(ListenOptions lo)
        {
            if (!string.IsNullOrWhiteSpace(Settings.CertificatePath))
                lo.UseHttps(Settings.CertificatePath, Settings.CertificatePassword);
        }

        var bind = IPAddress.Parse(Settings.BindAddress);
        if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any))
        {
            // Every interface, which includes 127.0.0.1 for local testing.
            k.ListenAnyIP(Settings.SignalingPort, Configure);
        }
        else
        {
            k.Listen(bind, Settings.SignalingPort, Configure);
            // Always keep a loopback listener so the server can be tested on 127.0.0.1.
            if (!IPAddress.IsLoopback(bind)) k.Listen(IPAddress.Loopback, Settings.SignalingPort, Configure);
        }
    }

    private IReadOnlyList<string> BuildListeningUrls()
    {
        var scheme = string.IsNullOrWhiteSpace(Settings.CertificatePath) ? "http" : "https";
        var urls = new List<string> { $"{scheme}://127.0.0.1:{Settings.SignalingPort}" };
        var bind = IPAddress.Parse(Settings.BindAddress);
        if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any))
            urls.Add($"{scheme}://0.0.0.0:{Settings.SignalingPort} (all interfaces)");
        else if (!IPAddress.IsLoopback(bind))
            urls.Add($"{scheme}://{bind}:{Settings.SignalingPort}");
        return urls;
    }

    private async Task<IResult> HandleConnectAsync(HttpContext ctx)
    {
        ConnectRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ConnectRequest>(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            request = null;
        }

        var response = request == null
            ? ConnectResponse.Fail(400, "bad_request", "Body must be JSON: { world, user, offer: { type, sdp }, iceCandidates }.", Settings.ServerName)
            : await ConnectAsync(request, ctx.Connection.RemoteIpAddress?.ToString(), ctx.RequestAborted).ConfigureAwait(false);
        return Results.Json(response, statusCode: response.StatusCode);
    }

    /// <summary>
    /// Serves a world snapshot offered with world_sync_http. The token is a single player's unguessable capability;
    /// the body is the same JSON as the joined world_sync_chunk pieces, gzip compressed.
    /// </summary>
    private async Task HandleWorldSyncDownloadAsync(HttpContext ctx, string token)
    {
        var snapshot = Relay.BeginHttpSyncDownload(token);
        if (snapshot == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (gzip, _) = await snapshot.ConfigureAwait(false);
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var acceptsGzip = ctx.Request.Headers.AcceptEncoding.Any(v => v?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true);
        try
        {
            if (acceptsGzip)
            {
                ctx.Response.Headers.ContentEncoding = "gzip";
                ctx.Response.Headers.Vary = "Accept-Encoding";
                ctx.Response.ContentLength = gzip.Length;
                await ctx.Response.Body.WriteAsync(gzip, ctx.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await using var unzip = new System.IO.Compression.GZipStream(new MemoryStream(gzip), System.IO.Compression.CompressionMode.Decompress);
                await unzip.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // Client went away; it reports world_sync_http_failed or the offer times out and falls back.
        }
    }

    /// <summary>Validates the player, creates the server side peer connection and returns the WebRTC answer.</summary>
    public async Task<ConnectResponse> ConnectAsync(ConnectRequest request, string? remoteAddress, CancellationToken cancellationToken = default)
    {
        var name = Settings.ServerName;
        if (!IsRunning) return ConnectResponse.Fail(503, "server_full", "Server is not running.", name);

        var user = request.User;
        var usernameError = PlayerRegistry.ValidateUsername(user, name);
        if (usernameError != null) return ConnectResponse.Fail(400, "bad_request", usernameError, name);
        if (request.World is { Length: > 256 }) return ConnectResponse.Fail(400, "bad_request", "World name is too long.", name);
        if (request.Offer?.Sdp is not { Length: > 0 } sdp || !string.Equals(request.Offer.Type, "offer", StringComparison.OrdinalIgnoreCase))
            return ConnectResponse.Fail(400, "bad_request", "An SDP offer is required.", name);

        if (BlockList.IsBlocked(user!))
        {
            WriteLog($"Rejected blocked user '{user}' from {remoteAddress}.");
            return ConnectResponse.Fail(403, "blocked", "You are blocked from this server.", name);
        }

        var accepted = SupportedFeatures().Where(f => request.Features?.Contains(f, StringComparer.Ordinal) == true).ToList();
        var session = new PlayerSession(user!, request.World, remoteAddress) { Features = accepted.ToHashSet(StringComparer.Ordinal) };
        if (!Players.TryAdd(session))
        {
            WriteLog($"Rejected duplicate user name '{user}' from {remoteAddress}.");
            return ConnectResponse.Fail(409, "name_in_use", $"The user name '{user}' is already connected.", name);
        }

        RTCPeerConnection? pc = null;
        try
        {
            pc = CreatePeerConnection(session);
            if (pc == null)
            {
                Players.Remove(session);
                return ConnectResponse.Fail(503, "server_full", "All player slots are in use.", name);
            }

            var transport = new WebRtcPlayerTransport(pc);
            session.Transport = transport;
            var candidates = new List<IceCandidate>();
            var gathered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pc.onicecandidate += c =>
            {
                if (c == null) return;
                var parsed = JsonSerializer.Deserialize<IceCandidate>(c.toJSON());
                if (parsed != null) lock (candidates) candidates.Add(parsed);
            };
            pc.onicegatheringstatechange += s =>
            {
                if (s == RTCIceGatheringState.complete) gathered.TrySetResult();
            };
            pc.oniceconnectionstatechange += s => session.IceState = s.ToString();
            pc.onconnectionstatechange += s =>
            {
                if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                    Disconnect(session, $"connection {s}");
            };
            pc.ondatachannel += dc =>
            {
                if (!transport.Attach(dc)) return;
                dc.onmessage += (_, _, data) => Relay.HandleMessage(session, Encoding.UTF8.GetString(data));
                dc.onclose += () => Disconnect(session, "data channel closed");
                if (dc.readyState == RTCDataChannelState.open) OnChannelOpen(session);
                else dc.onopen += () => OnChannelOpen(session);
            };

            var setResult = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
            if (setResult != SetDescriptionResultEnum.OK)
            {
                Disconnect(session, "invalid offer", notifyOthers: false);
                return ConnectResponse.Fail(400, "bad_request", $"Offer rejected: {setResult}.", name);
            }

            foreach (var c in request.IceCandidates ?? new List<IceCandidate>())
            {
                if (string.IsNullOrWhiteSpace(c.Candidate)) continue;
                try
                {
                    pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = c.Candidate,
                        sdpMid = c.SdpMid ?? "0",
                        sdpMLineIndex = (ushort)Math.Clamp(c.SdpMLineIndex ?? 0, 0, ushort.MaxValue),
                        usernameFragment = c.UsernameFragment,
                    });
                }
                catch (Exception)
                {
                    // Unresolvable candidates (e.g. browser mDNS .local names) are expected; ICE still
                    // succeeds through the browser's checks against our candidates (peer reflexive).
                }
            }

            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer).ConfigureAwait(false);
            if (pc.iceGatheringState != RTCIceGatheringState.complete)
                await Task.WhenAny(gathered.Task, Task.Delay(IceGatherTimeout, cancellationToken)).ConfigureAwait(false);

            List<IceCandidate> outCandidates;
            lock (candidates) outCandidates = candidates.ToList();
            if (!string.IsNullOrWhiteSpace(Settings.PublicAddress))
            {
                outCandidates.Insert(0, new IceCandidate
                {
                    Candidate = $"candidate:1985 1 udp 2130706431 {Settings.PublicAddress} {session.Port} typ host generation 0",
                    SdpMid = FirstMid(answer.sdp) ?? "0",
                    SdpMLineIndex = 0,
                });
            }

            ScheduleConnectTimeout(session);
            WriteLog($"{session.Username} negotiating from {remoteAddress} (world '{session.World}', port {session.Port}).");
            PlayersChanged?.Invoke();

            return new ConnectResponse
            {
                Ok = true,
                ServerName = name,
                User = session.Username,
                World = session.World,
                Port = session.Port,
                Answer = new SessionDescription { Type = "answer", Sdp = answer.sdp },
                IceCandidates = outCandidates,
                Features = request.Features == null ? null : accepted,
            };
        }
        catch (Exception e)
        {
            WriteLog($"Negotiation with '{user}' failed: {e.Message}");
            if (pc != null) Disconnect(session, "negotiation failed", notifyOthers: false);
            else Players.Remove(session);
            return ConnectResponse.Fail(500, "negotiation_failed", "WebRTC negotiation failed.", name);
        }
    }

    /// <summary>Creates a peer connection bound to a free port of the player range. Null when the range is exhausted.</summary>
    private RTCPeerConnection? CreatePeerConnection(PlayerSession session)
    {
        var config = new RTCConfiguration { iceServers = ParseIceServers(Settings.IceServers) };
        var bind = IPAddress.Parse(Settings.BindAddress);
        if (!bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any)) config.X_BindAddress = bind;

        for (int attempt = 0; attempt < Math.Min(Ports.Capacity, 20); attempt++)
        {
            var port = Ports.Rent();
            if (port == null) return null;
            try
            {
                var pc = new RTCPeerConnection(config, port.Value);
                session.Port = port.Value;
                return pc;
            }
            catch (Exception e) when (e is SocketException or ApplicationException)
            {
                // Port taken by another process; skip it for a while and try the next one.
                Ports.Quarantine(port.Value);
            }
        }
        return null;
    }

    private static List<RTCIceServer> ParseIceServers(IEnumerable<string> entries)
    {
        // Format: "stun:host:port" or "turn:host:port|username|credential".
        var list = new List<RTCIceServer>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var parts = entry.Split('|');
            var server = new RTCIceServer { urls = parts[0].Trim() };
            if (parts.Length >= 3)
            {
                server.username = parts[1];
                server.credential = parts[2];
                server.credentialType = RTCIceCredentialType.password;
            }
            list.Add(server);
        }
        return list;
    }

    private static string? FirstMid(string sdp)
    {
        foreach (var line in sdp.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("a=mid:", StringComparison.Ordinal)) return l[6..];
        }
        return null;
    }

    private void OnChannelOpen(PlayerSession session)
    {
        if (session.IsClosed || session.State != PlayerState.Connecting) return;
        lock (session)
        {
            if (session.State != PlayerState.Connecting) return;
            session.LastSeenUtc = DateTime.UtcNow;
            Relay.OnPlayerJoined(session);
        }
        WriteLog($"{session.Username} connected (world '{session.World}', port {session.Port}). Players online: {Players.Connected().Length}.");
        PlayersChanged?.Invoke();
    }

    private void ScheduleConnectTimeout(PlayerSession session)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Delay(ConnectTimeout, token).ContinueWith(t =>
        {
            if (!t.IsCanceled && session.State == PlayerState.Connecting)
                Disconnect(session, "connection timed out", notifyOthers: false);
        }, TaskScheduler.Default);
    }

    private void CheckPlayerHealth()
    {
        var now = DateTime.UtcNow;
        foreach (var p in Players.Connected())
        {
            if (now - p.LastSeenUtc > IdleTimeout) Disconnect(p, "timed out (no messages)");
        }
        Relay.ExpireStaleImports();
        Relay.ExpireHttpSyncs();
    }

    /// <summary>Idempotently tears down a session, frees its name and port and tells the other players.</summary>
    internal void Disconnect(PlayerSession session, string reason, bool notifyOthers = true)
    {
        if (!session.MarkClosed()) return;
        bool wasConnected = session.State == PlayerState.Connected;
        session.State = PlayerState.Disconnected;
        Players.Remove(session);
        try { session.Transport?.Close(); } catch (Exception) { /* best effort */ }
        if (session.Port != 0) Ports.Release(session.Port);
        if (wasConnected && notifyOthers) Relay.OnPlayerLeft(session);
        WriteLog($"{session.Username} disconnected: {reason}.");
        PlayersChanged?.Invoke();
    }

    private void SaveNowSafe(string label)
    {
        try
        {
            var written = Worlds.Save();
            WriteLog($"{label}: {written} changed world(s) written.");
        }
        catch (Exception e)
        {
            WriteLog($"{label} failed: {e.Message}");
        }
    }

    private void WriteLog(string message)
    {
        try
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        catch (Exception)
        {
            // Never let a faulty log subscriber break the server.
        }
    }
}
