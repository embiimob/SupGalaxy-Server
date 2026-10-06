namespace SupGalaxyServer;

/// <summary>
/// Tracks players by name. Guarantees only one session per user name (case-insensitive) at a time.
/// </summary>
public sealed class PlayerRegistry
{
    public const int MaxUsernameLength = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, PlayerSession> _players = new(StringComparer.OrdinalIgnoreCase);
    private long _joinSequence;

    public int Count
    {
        get { lock (_gate) return _players.Count; }
    }

    /// <summary>Reserves the name. Returns false when a session with that name already exists.</summary>
    public bool TryAdd(PlayerSession session)
    {
        lock (_gate)
        {
            if (_players.ContainsKey(session.Username)) return false;
            session.JoinSequence = ++_joinSequence;
            _players[session.Username] = session;
            return true;
        }
    }

    /// <summary>Removes the session only if it is still the registered session for its name.</summary>
    public bool Remove(PlayerSession session)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(session.Username, out var existing) && ReferenceEquals(existing, session))
            {
                _players.Remove(session.Username);
                return true;
            }
            return false;
        }
    }

    public PlayerSession? Get(string username)
    {
        lock (_gate) return _players.TryGetValue(username, out var s) ? s : null;
    }

    public bool Contains(string username)
    {
        lock (_gate) return _players.ContainsKey(username);
    }

    /// <summary>All sessions (any state).</summary>
    public PlayerSession[] All()
    {
        lock (_gate) return _players.Values.ToArray();
    }

    /// <summary>Sessions whose data channel is open.</summary>
    public PlayerSession[] Connected()
    {
        lock (_gate) return _players.Values.Where(p => p.State == PlayerState.Connected).ToArray();
    }

    /// <summary>Alphabetical (case-insensitive) list filtered by a case-insensitive substring of the name or world.</summary>
    public PlayerSession[] Search(string? filter)
    {
        var all = All();
        IEnumerable<PlayerSession> q = all;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(p => p.Username.Contains(f, StringComparison.OrdinalIgnoreCase)
                             || (p.World?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return q.OrderBy(p => p.Username, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Username, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Validates a player name. Returns null when valid, otherwise an error message.</summary>
    public static string? ValidateUsername(string? name, string serverName)
    {
        if (string.IsNullOrWhiteSpace(name)) return "User name is required.";
        if (name != name.Trim()) return "User name must not start or end with whitespace.";
        if (name.Length > MaxUsernameLength) return $"User name must be at most {MaxUsernameLength} characters.";
        if (name.Any(char.IsControl)) return "User name contains invalid characters.";
        if (string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase)) return "User name is reserved by the server.";
        return null;
    }
}
