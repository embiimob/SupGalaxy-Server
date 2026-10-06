using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SupGalaxyServer;

/// <summary>One block change inside an imported chunk (chunk-local x/z, like SupGalaxy chunkDeltas entries).</summary>
public readonly record struct ImportChange(long X, long Y, long Z, long B);

public sealed class ImportChunk
{
    public ImportChunk(string key, long cx, long cz, List<ImportChange> changes, bool? ownershipNeutral)
    {
        Key = key;
        Cx = cx;
        Cz = cz;
        Changes = changes;
        OwnershipNeutral = ownershipNeutral;
    }

    /// <summary>Normalized chunk key (leading '#' removed), e.g. "alpha:3:4".</summary>
    public string Key { get; }
    public long Cx { get; }
    public long Cz { get; }
    public List<ImportChange> Changes { get; }
    public bool? OwnershipNeutral { get; }
}

/// <summary>
/// A validated Chunk Keyword / IPFS world update, i.e. the payload SupGalaxy's applyChunkUpdates() receives after it
/// re-assembles ipfs_chunk_from_client_chunk / ipfs_chunk_update_chunk messages. Two shapes exist on the wire:
/// an array of { chunk, changes: [{x,y,z,b}], ownershipNeutral? } or an export object
/// { deltas: [...same...], foreignBlockOrigins: [[key, seed]], magicianStones: {}, calligraphyStones: {}, chests: {} }.
/// </summary>
public sealed class WorldImport
{
    private static readonly Regex ChunkKeyPattern = new(@"^(.{1,8}):(\d{1,5}):(\d{1,5})$", RegexOptions.CultureInvariant);
    private static readonly Regex BlockKeyPattern = new(@"^-?\d{1,9},-?\d{1,9},-?\d{1,9}$", RegexOptions.CultureInvariant);
    private const int MaxKeyLength = 128;
    private const long MaxBlockY = 1_000_000;
    private const long MaxBlockId = int.MaxValue;
    private static readonly long ChunksPerAxis = WorldStateStore.MapSize / WorldStateStore.ChunkSize;

    /// <summary>Seconds since 2025-09-21T00:00:00Z (SupGalaxy IPFS_EPOCH_2025_09_21).</summary>
    public static readonly long IpfsEpochSeconds = new DateTimeOffset(2025, 9, 21, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    public bool IsObjectForm { get; init; }
    public List<ImportChunk> Chunks { get; init; } = new();
    public List<KeyValuePair<string, string>> ForeignOrigins { get; init; } = new();
    public Dictionary<string, JsonNode> MagicianStones { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonNode> CalligraphyStones { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonNode> Chests { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Number of chunks in the payload that belong to another world and were ignored.</summary>
    public int ForeignWorldChunks { get; init; }

    public bool IsEmpty => Chunks.Count == 0 && ForeignOrigins.Count == 0 && MagicianStones.Count == 0
                           && CalligraphyStones.Count == 0 && Chests.Count == 0;

    /// <summary>Port of SupGalaxy computeIpfsTruncatedDate: seconds since the IPFS epoch, 0 when missing/invalid.</summary>
    public static long ComputeTruncatedDate(double? blockTimestampMs)
    {
        if (blockTimestampMs is not { } ms || !double.IsFinite(ms) || ms <= 0) return 0;
        var seconds = Math.Floor(ms / 1000) - IpfsEpochSeconds;
        return seconds > 0 && seconds < long.MaxValue / 2 ? (long)seconds : 0;
    }

    /// <summary>
    /// Parses and validates a re-assembled payload for <paramref name="world"/>. Chunks whose key belongs to another
    /// world are skipped. Returns null (with an error) when the payload is structurally malformed; nothing is applied
    /// in that case.
    /// </summary>
    public static WorldImport? Parse(string world, string json, out string? error)
    {
        error = null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException e)
        {
            error = "payload is not valid JSON: " + e.Message;
            return null;
        }

        var prefix = world.Length > 8 ? world[..8] : world;
        JsonArray? deltas;
        JsonObject? obj = root as JsonObject;
        if (root is JsonArray arr) deltas = arr;
        else if (obj != null)
        {
            var d = obj["deltas"];
            if (d != null && d is not JsonArray) return Fail(out error, "deltas must be an array");
            deltas = d as JsonArray;
        }
        else return Fail(out error, "payload must be an array or an object");

        var chunks = new List<ImportChunk>();
        int foreignWorld = 0;
        if (deltas != null)
        {
            foreach (var entry in deltas)
            {
                if (entry is not JsonObject c) return Fail(out error, "chunk entry must be an object");
                if (!TryString(c["chunk"], out var rawKey)) return Fail(out error, "chunk key missing");
                if (c["changes"] is not JsonArray changes) return Fail(out error, "chunk changes must be an array");
                var key = rawKey.StartsWith('#') ? rawKey[1..] : rawKey;
                var m = ChunkKeyPattern.Match(key);
                if (!m.Success) return Fail(out error, $"invalid chunk key '{Trunc(rawKey)}'");
                long cx = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                long cz = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (cx >= ChunksPerAxis || cz >= ChunksPerAxis) return Fail(out error, $"chunk key out of range '{Trunc(rawKey)}'");

                var list = new List<ImportChange>(changes.Count);
                foreach (var ch in changes)
                {
                    if (ch is not JsonObject o
                        || !TryInt(o["x"], out var x) || !TryInt(o["y"], out var y) || !TryInt(o["z"], out var z) || !TryInt(o["b"], out var b))
                        return Fail(out error, "block change must have integer x, y, z and b");
                    if (x < 0 || x >= WorldStateStore.ChunkSize || z < 0 || z >= WorldStateStore.ChunkSize || Math.Abs(y) > MaxBlockY
                        || b < 0 || b > MaxBlockId)
                        return Fail(out error, "block change out of range");
                    list.Add(new ImportChange(x, y, z, b));
                }

                if (!string.Equals(m.Groups[1].Value, prefix, StringComparison.Ordinal))
                {
                    foreignWorld++;
                    continue;
                }
                bool? neutral = c["ownershipNeutral"] is JsonValue nv && nv.TryGetValue<bool>(out var nb) ? nb : null;
                chunks.Add(new ImportChunk(key, cx, cz, list, neutral));
            }
        }

        var origins = new List<KeyValuePair<string, string>>();
        var magician = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var calligraphy = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var chestMap = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (obj != null)
        {
            var fo = obj["foreignBlockOrigins"];
            if (fo != null)
            {
                if (fo is not JsonArray foArr) return Fail(out error, "foreignBlockOrigins must be an array");
                foreach (var e in foArr)
                {
                    if (e is not JsonArray pair || pair.Count != 2 || !TryString(pair[0], out var k) || !TryString(pair[1], out var seed)
                        || !BlockKeyPattern.IsMatch(k) || seed.Length == 0 || seed.Length > MaxKeyLength)
                        return Fail(out error, "foreignBlockOrigins entries must be [\"x,y,z\", seed]");
                    origins.Add(new(k, seed));
                }
            }
            if (!ReadKeyed(obj["magicianStones"], magician, requirePosition: true, out error)) return null;
            if (!ReadKeyed(obj["calligraphyStones"], calligraphy, requirePosition: true, out error)) return null;
            if (!ReadKeyed(obj["chests"], chestMap, requirePosition: true, out error)) return null;
        }

        return new WorldImport
        {
            IsObjectForm = obj != null,
            Chunks = chunks,
            ForeignOrigins = origins,
            MagicianStones = magician,
            CalligraphyStones = calligraphy,
            Chests = chestMap,
            ForeignWorldChunks = foreignWorld,
        };
    }

    /// <summary>Serializes in the same shape it was received in, so applyChunkUpdates() on clients accepts it.</summary>
    public string ToJson()
    {
        var deltas = new JsonArray();
        foreach (var c in Chunks)
        {
            var o = new JsonObject
            {
                ["chunk"] = c.Key,
                ["changes"] = new JsonArray(c.Changes.Select(ch => (JsonNode)new JsonObject
                {
                    ["x"] = ch.X, ["y"] = ch.Y, ["z"] = ch.Z, ["b"] = ch.B,
                }).ToArray()),
            };
            if (c.OwnershipNeutral is { } n) o["ownershipNeutral"] = n;
            deltas.Add(o);
        }
        if (!IsObjectForm) return deltas.ToJsonString();

        var root = new JsonObject { ["deltas"] = deltas };
        if (ForeignOrigins.Count > 0)
            root["foreignBlockOrigins"] = new JsonArray(ForeignOrigins.Select(kv => (JsonNode)new JsonArray(kv.Key, kv.Value)).ToArray());
        if (MagicianStones.Count > 0) root["magicianStones"] = ToObject(MagicianStones);
        if (CalligraphyStones.Count > 0) root["calligraphyStones"] = ToObject(CalligraphyStones);
        if (Chests.Count > 0) root["chests"] = ToObject(Chests);
        return root.ToJsonString();
    }

    private static JsonObject ToObject(Dictionary<string, JsonNode> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map) o[k] = v.DeepClone();
        return o;
    }

    private static bool ReadKeyed(JsonNode? node, Dictionary<string, JsonNode> into, bool requirePosition, out string? error)
    {
        error = null;
        if (node == null) return true;
        if (node is not JsonObject o)
        {
            error = "stones/chests must be an object";
            return false;
        }
        foreach (var (k, v) in o)
        {
            if (v == null) continue; // SupGalaxy skips null entries (removed chests).
            if (k.Length == 0 || k.Length > MaxKeyLength || v is not JsonObject data
                || (requirePosition && (!IsNumber(data["x"]) || !IsNumber(data["y"]) || !IsNumber(data["z"]))))
            {
                error = $"invalid stone/chest entry '{Trunc(k)}'";
                return false;
            }
            into[k] = data.DeepClone();
        }
        return true;
    }

    private static bool IsNumber(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d);

    private static bool TryString(JsonNode? n, out string s)
    {
        s = "";
        if (n is JsonValue v && v.TryGetValue<string>(out var str) && str != null)
        {
            s = str;
            return true;
        }
        return false;
    }

    private static bool TryInt(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v || !v.TryGetValue<double>(out var d) || !double.IsFinite(d) || d != Math.Floor(d) || Math.Abs(d) > 1e15)
            return false;
        value = (long)d;
        return true;
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private static WorldImport? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
