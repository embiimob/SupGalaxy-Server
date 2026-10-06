using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SupGalaxyServer;

/// <summary>
/// Fast world sync: instead of streaming a world's saved state as hundreds of 64K data-channel messages (SCTP over
/// the server's WebRTC stack is slow), clients that announce the "http_world_sync" feature in POST /connect get a
/// world_sync_http offer and download the same JSON as one gzip compressed HTTP response from the server's main port.
///
/// Gap-free like the data-channel path: the snapshot and BeginSync are taken atomically under the state gate, and
/// live world updates are held back for the player until it reports world_sync_http_done (applied) or the offer is
/// abandoned, in which case the world is streamed over the data channel instead.
/// </summary>
public sealed partial class GameRelay
{
    public const string HttpWorldSyncFeature = "http_world_sync";
    public const string HttpSyncOfferType = "world_sync_http";
    public const string HttpSyncDoneType = "world_sync_http_done";
    public const string HttpSyncFailedType = "world_sync_http_failed";
    public const string HttpSyncPathPrefix = "/world-sync/";
    private const int MaxHttpSyncsPerPlayer = 8;

    private sealed class HttpSyncOffer
    {
        public required string Token { get; init; }
        public required string TransactionId { get; init; }
        public required string World { get; init; }
        public required PlayerSession Player { get; init; }
        public required Task<(byte[] Gzip, long RawBytes)> Snapshot { get; init; }
        public long CreatedAt;
        public bool Offered;
        public bool Fetched;
    }

    private readonly object _httpSyncGate = new();
    private readonly Dictionary<string, HttpSyncOffer> _httpSyncs = new(StringComparer.Ordinal);

    // Latest compressed snapshot per world, shared by everyone joining while the world does not change.
    private readonly Dictionary<string, (long Generation, long Revision, Task<(byte[] Gzip, long RawBytes)> Snapshot)> _snapshotCache =
        new(StringComparer.Ordinal);

    internal int PendingHttpSyncCount
    {
        get { lock (_httpSyncGate) return _httpSyncs.Count; }
    }

    private Task OfferHttpSync(PlayerSession p, string world)
    {
        lock (_httpSyncGate)
        {
            // A misbehaving client re-requesting sync in a loop must not pin unbounded snapshots.
            if (_httpSyncs.Values.Count(o => ReferenceEquals(o.Player, p)) >= MaxHttpSyncsPerPlayer)
                return SendWorldSync(p, world, allowHttp: false);
        }

        Task<(byte[] Gzip, long RawBytes)>? snapshot = null;
        long revision;
        lock (_stateGate)
        {
            var generation = _worlds.Generation;
            revision = _worlds.GetRevision(world);
            lock (_httpSyncGate)
            {
                if (_snapshotCache.TryGetValue(world, out var cached) && cached.Generation == generation && cached.Revision == revision)
                    snapshot = cached.Snapshot;
            }
            if (snapshot == null)
            {
                var payload = _worlds.BuildSyncPayload(world, out revision);
                if (payload == null) return Task.CompletedTask;
                snapshot = Task.Run(() => Compress(payload));
                lock (_httpSyncGate) _snapshotCache[world] = (generation, revision, snapshot);
            }
            p.BeginSync();
        }

        var offer = new HttpSyncOffer
        {
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            TransactionId = $"world_sync_{p.Username}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{RandomNumberGenerator.GetInt32(1_000_000)}",
            World = world,
            Player = p,
            Snapshot = snapshot,
            CreatedAt = Clock(),
        };
        lock (_httpSyncGate) _httpSyncs[offer.Token] = offer;

        return snapshot.ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                if (TakeHttpSync(offer.Token)) FinishHttpSync(offer, fallback: true, "could not be prepared");
                return;
            }
            var (gzip, raw) = t.Result;
            lock (_httpSyncGate)
            {
                if (!_httpSyncs.ContainsKey(offer.Token)) return;
                offer.CreatedAt = Clock();
                offer.Offered = true;
            }
            var sent = p.Send(Json(new JsonObject
            {
                ["type"] = HttpSyncOfferType,
                ["world"] = world,
                ["transactionId"] = offer.TransactionId,
                ["revision"] = revision,
                ["path"] = HttpSyncPathPrefix + offer.Token,
                ["bytes"] = raw,
                ["compressedBytes"] = gzip.Length,
                ["fetchTimeoutSeconds"] = _settings.HttpWorldSyncFetchTimeoutSeconds,
            }));
            if (!sent && TakeHttpSync(offer.Token)) FinishHttpSync(offer, fallback: true, "offer could not be sent");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Called by the HTTP endpoint. Returns the compressed snapshot for a valid token, or null. The token stays valid
    /// (retries are allowed) until the player reports done / failed, disconnects or the offer expires.
    /// </summary>
    internal Task<(byte[] Gzip, long RawBytes)>? BeginHttpSyncDownload(string token)
    {
        lock (_httpSyncGate)
        {
            if (!_httpSyncs.TryGetValue(token, out var offer) || !offer.Offered || offer.Player.IsClosed) return null;
            offer.Fetched = true;
            return offer.Snapshot;
        }
    }

    private void CompleteHttpSync(PlayerSession p, string? transactionId, bool failed)
    {
        HttpSyncOffer? offer;
        lock (_httpSyncGate)
        {
            offer = _httpSyncs.Values.FirstOrDefault(o => ReferenceEquals(o.Player, p) && o.TransactionId == transactionId);
            if (offer == null) return;
            _httpSyncs.Remove(offer.Token);
        }
        FinishHttpSync(offer, fallback: failed, failed ? "failed on the client" : null);
    }

    private bool TakeHttpSync(string token)
    {
        lock (_httpSyncGate) return _httpSyncs.Remove(token);
    }

    private void FinishHttpSync(HttpSyncOffer offer, bool fallback, string? reason)
    {
        var p = offer.Player;
        if (fallback)
        {
            _log?.Invoke($"HTTP world sync of '{offer.World}' to {p.Username} {reason}; sending it over the data channel.");
            // Start the data-channel snapshot (BeginSync) before ending this one, so held-back updates keep waiting
            // and are delivered after the fresher snapshot.
            if (!p.IsClosed) _ = SendWorldSync(p, offer.World, allowHttp: false);
            p.EndSync();
            return;
        }

        var elapsed = Clock() - offer.CreatedAt;
        var size = offer.Snapshot.IsCompletedSuccessfully ? offer.Snapshot.Result : default;
        _log?.Invoke($"Sent saved state of world '{offer.World}' to {p.Username} over HTTP " +
                     $"({size.RawBytes / 1024} KB, {size.Gzip?.Length / 1024 ?? 0} KB gzip, {elapsed} ms).");
        if (!p.EndSync() && !p.IsClosed) _ = SendWorldSync(p, offer.World);
    }

    /// <summary>Falls back to the data channel for offers that were not downloaded or acknowledged in time.</summary>
    public int ExpireHttpSyncs()
    {
        var now = Clock();
        var expired = new List<(HttpSyncOffer Offer, string Reason)>();
        lock (_httpSyncGate)
        {
            foreach (var o in _httpSyncs.Values.ToList())
            {
                if (o.Player.IsClosed)
                {
                    _httpSyncs.Remove(o.Token);
                    continue;
                }
                if (!o.Offered) continue;
                var age = now - o.CreatedAt;
                if (!o.Fetched && age > _settings.HttpWorldSyncFetchTimeoutSeconds * 1000L)
                    expired.Add((o, "was not downloaded in time"));
                else if (age > _settings.HttpWorldSyncTimeoutSeconds * 1000L)
                    expired.Add((o, "was not confirmed in time"));
            }
            foreach (var (o, _) in expired) _httpSyncs.Remove(o.Token);
        }
        foreach (var (o, reason) in expired) FinishHttpSync(o, fallback: true, reason);
        return expired.Count;
    }

    private void DropHttpSyncs(PlayerSession p)
    {
        lock (_httpSyncGate)
        {
            foreach (var o in _httpSyncs.Values.Where(o => ReferenceEquals(o.Player, p)).ToList())
                _httpSyncs.Remove(o.Token);
        }
    }

    private static (byte[] Gzip, long RawBytes) Compress(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var ms = new MemoryStream(bytes.Length / 8 + 1024);
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(bytes);
        return (ms.ToArray(), bytes.Length);
    }
}
