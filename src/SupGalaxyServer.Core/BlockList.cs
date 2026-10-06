using System.Text.Json;

namespace SupGalaxyServer;

/// <summary>
/// Persistent, case-insensitive list of blocked player names (blocked.json in the data directory).
/// </summary>
public sealed class BlockList
{
    private readonly object _gate = new();
    private readonly SortedSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;

    public event Action? Changed;

    public BlockList(string? path = null)
    {
        _path = path;
        if (path != null && File.Exists(path))
        {
            try
            {
                var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
                foreach (var n in names)
                {
                    if (!string.IsNullOrWhiteSpace(n)) _names.Add(n.Trim());
                }
            }
            catch (JsonException)
            {
                // Corrupt file: start with an empty list rather than refusing to start.
            }
        }
    }

    public bool IsBlocked(string name)
    {
        lock (_gate) return _names.Contains(name.Trim());
    }

    /// <summary>Returns the blocked names sorted alphabetically.</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _names.ToList(); }
    }

    public bool Block(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        bool added;
        lock (_gate)
        {
            added = _names.Add(name.Trim());
            if (added) Persist();
        }
        if (added) Changed?.Invoke();
        return added;
    }

    public bool Unblock(string name)
    {
        bool removed;
        lock (_gate)
        {
            removed = _names.Remove(name.Trim());
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    private void Persist()
    {
        if (_path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        FileUtil.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_names.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}
