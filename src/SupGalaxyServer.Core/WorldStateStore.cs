using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupGalaxyServer;

public enum StoneKind
{
    Magician,
    Calligraphy,
}

public sealed record WorldSummary(string World, int Chunks, int Blocks, int Stones, bool Dirty);

/// <summary>
/// Authoritative, persistent copy of every world's edits (block deltas, foreign block origins and stones).
/// The layout mirrors SupGalaxy's WORLD_STATES (chunkDeltas keyed by makeChunkKey(world, cx, cz)) so it can be
/// streamed straight to clients with the existing world_sync_start / world_sync_chunk messages.
///
/// Saving is incremental: only worlds changed since the previous save are rewritten. Each world is its own file
/// and every file is written atomically, so a crash or power loss restarts from the last completed save.
/// </summary>
public sealed class WorldStateStore
{
    // Must match SupGalaxy js/declare.js
    public const int ChunkSize = 16;
    public const int MapSize = 16384;

    private sealed class WorldData
    {
        public WorldData(string name) => Name = name;
        public string Name { get; }
        public Dictionary<string, Dictionary<(long X, long Y, long Z), long>> Chunks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ForeignOrigins { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonNode> MagicianStones { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonNode> CalligraphyStones { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonNode> Chests { get; } = new(StringComparer.Ordinal);

        /// <summary>Per block ("wx,wy,wz") truncated IPFS date of the last applied import (SupGalaxy ipfsTruncatedDates).</summary>
        public Dictionary<string, long> IpfsDates { get; } = new(StringComparer.Ordinal);

        /// <summary>Imported transaction ids, oldest first. Sent to clients as processedIds.</summary>
        public HashSet<string> ProcessedIds { get; } = new(StringComparer.Ordinal);
        public Queue<string> ProcessedOrder { get; } = new();
        public long Version;
        public long SavedVersion;
        public bool IsEmpty => Chunks.Count == 0 && ForeignOrigins.Count == 0 && MagicianStones.Count == 0 && CalligraphyStones.Count == 0
                               && Chests.Count == 0 && ProcessedIds.Count == 0;

        public void AddProcessed(string id)
        {
            if (!ProcessedIds.Add(id)) return;
            ProcessedOrder.Enqueue(id);
            while (ProcessedOrder.Count > MaxProcessedIds) ProcessedIds.Remove(ProcessedOrder.Dequeue());
        }
    }

    /// <summary>Most recent imported transaction ids remembered per world (for idempotency and processedIds).</summary>
    public const int MaxProcessedIds = 2048;

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, WorldData> _worlds = new(StringComparer.Ordinal);
    private readonly string? _directory;

    public WorldStateStore(string? sessionDirectory)
    {
        _directory = sessionDirectory;
    }

    public DateTime? LastSaveUtc { get; private set; }

    public string? Directory => _directory;

    /// <summary>JavaScript style modulo that always returns a positive result (SupGalaxy modWrap).</summary>
    public static long ModWrap(long value, long size) => ((value % size) + size) % size;

    /// <summary>Port of SupGalaxy makeChunkKey: first 8 UTF-16 chars of the world name + ":" + cx + ":" + cz.</summary>
    public static string MakeChunkKey(string world, long cx, long cz) =>
        (world.Length > 8 ? world[..8] : world) + ":" + cx + ":" + cz;

    public static string ChunkKeyForBlock(string world, long wx, long wz) =>
        MakeChunkKey(world, ModWrap(wx, MapSize) / ChunkSize, ModWrap(wz, MapSize) / ChunkSize);

    public void ApplyBlock(string world, long wx, long wy, long wz, long blockId, string? originSeed, bool clearOrigin = false)
    {
        lock (_gate)
        {
            var w = GetOrCreate(world);
            var key = ChunkKeyForBlock(world, wx, wz);
            if (!w.Chunks.TryGetValue(key, out var chunk))
            {
                chunk = new Dictionary<(long, long, long), long>();
                w.Chunks[key] = chunk;
            }
            chunk[(ModWrap(wx, ChunkSize), wy, ModWrap(wz, ChunkSize))] = blockId;

            var blockKey = $"{wx},{wy},{wz}";
            if (clearOrigin)
                w.ForeignOrigins.Remove(blockKey);
            else if (!string.IsNullOrEmpty(originSeed) && originSeed != world)
                w.ForeignOrigins[blockKey] = originSeed;
            w.Version++;
        }
    }

    /// <summary>True when an imported (Chunk Keyword / IPFS) transaction was already merged into the world.</summary>
    public bool IsImportProcessed(string world, string transactionId)
    {
        lock (_gate) return _worlds.TryGetValue(world, out var w) && w.ProcessedIds.Contains(transactionId);
    }

    /// <summary>
    /// Atomically merges a validated Chunk Keyword / IPFS import into the world. Block changes follow SupGalaxy's
    /// monotonic IPFS ordering (shouldApplyIpfsUpdate): a change is applied only when the import's truncated date is
    /// valid and &gt;= the date of the last import that wrote that block. Foreign origins, stones and chests are
    /// last-writer-wins, as on the client. Returns the subset that was applied, or null when the transaction was
    /// already merged (repeated delivery is a no-op).
    /// </summary>
    public WorldImport? ApplyImport(string world, string transactionId, long truncatedDate, WorldImport import)
    {
        lock (_gate)
        {
            var w = GetOrCreate(world);
            if (w.ProcessedIds.Contains(transactionId)) return null;

            var acceptedChunks = new List<ImportChunk>(import.Chunks.Count);
            foreach (var c in import.Chunks)
            {
                var accepted = new List<ImportChange>();
                foreach (var ch in c.Changes)
                {
                    var blockKey = $"{c.Cx * ChunkSize + ch.X},{ch.Y},{c.Cz * ChunkSize + ch.Z}";
                    w.IpfsDates.TryGetValue(blockKey, out var existing);
                    if (truncatedDate <= 0 || (existing > 0 && truncatedDate < existing)) continue;
                    w.IpfsDates[blockKey] = truncatedDate;
                    if (!w.Chunks.TryGetValue(c.Key, out var chunk))
                    {
                        chunk = new Dictionary<(long, long, long), long>();
                        w.Chunks[c.Key] = chunk;
                    }
                    chunk[(ch.X, ch.Y, ch.Z)] = ch.B;
                    accepted.Add(ch);
                }
                acceptedChunks.Add(new ImportChunk(c.Key, c.Cx, c.Cz, accepted, c.OwnershipNeutral));
            }
            foreach (var (k, seed) in import.ForeignOrigins) w.ForeignOrigins[k] = seed;
            foreach (var (k, v) in import.MagicianStones) w.MagicianStones[k] = v.DeepClone();
            foreach (var (k, v) in import.CalligraphyStones) w.CalligraphyStones[k] = v.DeepClone();
            foreach (var (k, v) in import.Chests) w.Chests[k] = v.DeepClone();
            w.AddProcessed(transactionId);
            w.Version++;

            return new WorldImport
            {
                IsObjectForm = import.IsObjectForm,
                Chunks = acceptedChunks,
                ForeignOrigins = import.ForeignOrigins,
                MagicianStones = import.MagicianStones,
                CalligraphyStones = import.CalligraphyStones,
                Chests = import.Chests,
            };
        }
    }

    public bool HasChest(string world, string key)
    {
        lock (_gate) return _worlds.TryGetValue(world, out var w) && w.Chests.ContainsKey(key);
    }

    /// <summary>Current revision of a world (bumped by every applied edit), 0 for unknown worlds.</summary>
    public long GetRevision(string world)
    {
        lock (_gate) return _worlds.TryGetValue(world, out var w) ? w.Version : 0;
    }

    public void SetStone(StoneKind kind, string world, string key, JsonNode data)
    {
        lock (_gate)
        {
            var w = GetOrCreate(world);
            (kind == StoneKind.Magician ? w.MagicianStones : w.CalligraphyStones)[key] = data.DeepClone();
            w.Version++;
        }
    }

    public void RemoveStone(StoneKind kind, string world, string key)
    {
        lock (_gate)
        {
            if (!_worlds.TryGetValue(world, out var w)) return;
            if ((kind == StoneKind.Magician ? w.MagicianStones : w.CalligraphyStones).Remove(key)) w.Version++;
        }
    }

    /// <summary>Returns the block id stored for a position or null when unmodified.</summary>
    public long? GetBlock(string world, long wx, long wy, long wz)
    {
        lock (_gate)
        {
            if (!_worlds.TryGetValue(world, out var w)) return null;
            if (!w.Chunks.TryGetValue(ChunkKeyForBlock(world, wx, wz), out var chunk)) return null;
            return chunk.TryGetValue((ModWrap(wx, ChunkSize), wy, ModWrap(wz, ChunkSize)), out var b) ? b : null;
        }
    }

    public string? GetForeignOrigin(string world, long wx, long wy, long wz)
    {
        lock (_gate)
        {
            return _worlds.TryGetValue(world, out var w) && w.ForeignOrigins.TryGetValue($"{wx},{wy},{wz}", out var o) ? o : null;
        }
    }

    public bool HasStone(StoneKind kind, string world, string key)
    {
        lock (_gate)
        {
            return _worlds.TryGetValue(world, out var w) && (kind == StoneKind.Magician ? w.MagicianStones : w.CalligraphyStones).ContainsKey(key);
        }
    }

    public WorldSummary[] Summaries()
    {
        lock (_gate)
        {
            return _worlds.Values
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => new WorldSummary(w.Name, w.Chunks.Count, w.Chunks.Values.Sum(c => c.Count),
                    w.MagicianStones.Count + w.CalligraphyStones.Count, w.Version != w.SavedVersion))
                .ToArray();
        }
    }

    /// <summary>
    /// Builds the JSON payload SupGalaxy expects after re-assembling world_sync_chunk messages
    /// (see applyWorldStructureSync in js/web-rtc.js). Returns null if the world has no saved edits.
    /// </summary>
    public string? BuildSyncPayload(string world) => BuildSyncPayload(world, out _);

    /// <summary>Same as <see cref="BuildSyncPayload(string)"/>, also returning the world revision the payload reflects.</summary>
    public string? BuildSyncPayload(string world, out long revision)
    {
        lock (_gate)
        {
            revision = 0;
            if (!_worlds.TryGetValue(world, out var w) || w.IsEmpty) return null;
            revision = w.Version;
            return Serialize(w, includeHeader: false);
        }
    }

    /// <summary>Writes every world changed since the last save. Returns the number of world files written.</summary>
    public int Save()
    {
        if (_directory == null) return 0;
        lock (_saveGate) return SaveCore(_directory);
    }

    private int SaveCore(string directory)
    {
        var pending = new List<(WorldData World, long Version, string Json)>();
        lock (_gate)
        {
            foreach (var w in _worlds.Values)
            {
                if (w.Version == w.SavedVersion) continue;
                pending.Add((w, w.Version, Serialize(w, includeHeader: true)));
            }
        }

        var worldsDir = Path.Combine(directory, "worlds");
        System.IO.Directory.CreateDirectory(worldsDir);
        foreach (var (w, version, json) in pending)
        {
            FileUtil.WriteAllTextAtomic(Path.Combine(worldsDir, FileNameFor(w.Name)), json);
            lock (_gate)
            {
                if (version > w.SavedVersion) w.SavedVersion = version;
            }
        }

        LastSaveUtc = DateTime.UtcNow;
        var meta = new JsonObject
        {
            ["savedAtUtc"] = LastSaveUtc.Value.ToString("O"),
            ["worlds"] = new JsonArray(Summaries().Select(s => (JsonNode)JsonValue.Create(s.World)!).ToArray()),
        };
        FileUtil.WriteAllTextAtomic(Path.Combine(directory, "session.json"), meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return pending.Count;
    }

    /// <summary>Loads the last saved session. Unreadable world files are skipped. Returns the number of worlds loaded.</summary>
    public int Load()
    {
        if (_directory == null) return 0;
        var worldsDir = Path.Combine(_directory, "worlds");
        if (!System.IO.Directory.Exists(worldsDir)) return 0;
        int loaded = 0;
        lock (_gate)
        {
            _worlds.Clear();
            foreach (var file in System.IO.Directory.EnumerateFiles(worldsDir, "*.json"))
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                    var name = root?["world"]?.GetValue<string>();
                    if (root == null || string.IsNullOrEmpty(name)) continue;
                    var w = new WorldData(name);
                    if (root["chunkDeltas"] is JsonArray chunks)
                    {
                        foreach (var entry in chunks.OfType<JsonArray>())
                        {
                            if (entry.Count != 2 || entry[1] is not JsonArray changes) continue;
                            var key = entry[0]!.GetValue<string>();
                            var chunk = new Dictionary<(long, long, long), long>();
                            foreach (var c in changes.OfType<JsonObject>())
                                chunk[(c["x"]!.GetValue<long>(), c["y"]!.GetValue<long>(), c["z"]!.GetValue<long>())] = c["b"]!.GetValue<long>();
                            w.Chunks[key] = chunk;
                        }
                    }
                    if (root["foreignBlockOrigins"] is JsonArray origins)
                    {
                        foreach (var entry in origins.OfType<JsonArray>())
                        {
                            if (entry.Count == 2) w.ForeignOrigins[entry[0]!.GetValue<string>()] = entry[1]!.GetValue<string>();
                        }
                    }
                    if (root["magicianStones"] is JsonObject ms)
                        foreach (var kv in ms) if (kv.Value != null) w.MagicianStones[kv.Key] = kv.Value.DeepClone();
                    if (root["calligraphyStones"] is JsonObject cs)
                        foreach (var kv in cs) if (kv.Value != null) w.CalligraphyStones[kv.Key] = kv.Value.DeepClone();
                    if (root["chests"] is JsonObject chs)
                        foreach (var kv in chs) if (kv.Value != null) w.Chests[kv.Key] = kv.Value.DeepClone();
                    if (root["ipfsDates"] is JsonArray dates)
                        foreach (var entry in dates.OfType<JsonArray>())
                            if (entry.Count == 2) w.IpfsDates[entry[0]!.GetValue<string>()] = entry[1]!.GetValue<long>();
                    if (root["processedIds"] is JsonArray ids)
                        foreach (var id in ids) if (id is JsonValue v && v.TryGetValue<string>(out var s)) w.AddProcessed(s);
                    if (root["version"] is JsonValue ver && ver.TryGetValue<long>(out var version) && version > 0)
                        w.Version = w.SavedVersion = version;
                    _worlds[name] = w;
                    loaded++;
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IOException or NullReferenceException)
                {
                    // Skip corrupt files; the rest of the session still loads.
                }
            }
        }
        return loaded;
    }

    /// <summary>Discards all in-memory world state and deletes the saved session from disk.</summary>
    public void Reset()
    {
        lock (_saveGate)
        lock (_gate)
        {
            _worlds.Clear();
            if (_directory != null && System.IO.Directory.Exists(_directory))
                System.IO.Directory.Delete(_directory, recursive: true);
            LastSaveUtc = null;
        }
    }

    private WorldData GetOrCreate(string world)
    {
        if (!_worlds.TryGetValue(world, out var w))
        {
            w = new WorldData(world);
            _worlds[world] = w;
        }
        return w;
    }

    internal static string FileNameFor(string world) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(world)))[..32].ToLowerInvariant() + ".json";

    private static string Serialize(WorldData w, bool includeHeader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WireFormat.WriterOptions))
        {
            writer.WriteStartObject();
            if (includeHeader)
            {
                writer.WriteString("world", w.Name);
                writer.WriteNumber("version", w.Version);
            }

            writer.WriteStartArray("chunkDeltas");
            foreach (var (key, chunk) in w.Chunks)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteStartArray();
                foreach (var (pos, b) in chunk)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("x", pos.X);
                    writer.WriteNumber("y", pos.Y);
                    writer.WriteNumber("z", pos.Z);
                    writer.WriteNumber("b", b);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("foreignBlockOrigins");
            foreach (var (key, origin) in w.ForeignOrigins)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteStringValue(origin);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("processedIds");
            foreach (var id in w.ProcessedOrder) writer.WriteStringValue(id);
            writer.WriteEndArray();

            if (includeHeader)
            {
                writer.WriteStartArray("ipfsDates");
                foreach (var (key, date) in w.IpfsDates)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(key);
                    writer.WriteNumberValue(date);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }

            WriteStones(writer, "magicianStones", w.MagicianStones);
            WriteStones(writer, "calligraphyStones", w.CalligraphyStones);
            WriteStones(writer, "chests", w.Chests);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStones(Utf8JsonWriter writer, string name, Dictionary<string, JsonNode> stones)
    {
        writer.WriteStartObject(name);
        foreach (var (key, data) in stones)
        {
            writer.WritePropertyName(key);
            data.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
