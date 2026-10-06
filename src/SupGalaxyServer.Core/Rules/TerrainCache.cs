namespace SupGalaxyServer.Rules;

/// <summary>
/// Thread-safe LRU cache of generated terrain chunks (64 KB each). Generation runs outside the lock; two threads
/// asking for the same missing chunk may both generate it, which is harmless because generation is deterministic.
/// </summary>
public sealed class TerrainCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<TerrainChunk>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<TerrainChunk> _lru = new();

    public TerrainCache(int capacity = 1024)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public TerrainChunk Get(string chunkKey)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(chunkKey, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }
        }

        var chunk = TerrainGenerator.Generate(chunkKey);

        lock (_gate)
        {
            if (_map.TryGetValue(chunkKey, out var existing)) return existing.Value;
            _map[chunkKey] = _lru.AddFirst(chunk);
            while (_map.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
        return chunk;
    }
}
