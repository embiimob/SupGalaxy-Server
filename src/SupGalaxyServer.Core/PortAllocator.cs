namespace SupGalaxyServer;

/// <summary>
/// Hands out one UDP port per connected player from the configured range (default 55556-56556).
/// WebRTC media is BUNDLEd so each player needs exactly one port. SIPSorcery (like classic RTP) only binds
/// RTP sockets on even port numbers, so with <c>evenOnly</c> (used by the server) only the even ports of the
/// range are handed out: the default 1001 port range supports 501 simultaneous players.
/// </summary>
public sealed class PortAllocator
{
    private readonly object _gate = new();
    private readonly HashSet<int> _inUse = new();
    private readonly Dictionary<int, DateTime> _quarantined = new();
    private readonly TimeSpan _quarantineTime;
    private readonly int[] _ports;
    private int _next;

    public int Start { get; }
    public int End { get; }

    /// <summary>Maximum number of ports (and therefore players) that can be rented at once.</summary>
    public int Capacity => _ports.Length;

    public PortAllocator(int start, int end, bool evenOnly = false, TimeSpan? quarantineTime = null)
    {
        if (end < start) throw new ArgumentException("End must be >= start.", nameof(end));
        Start = start;
        End = end;
        _ports = Enumerable.Range(start, end - start + 1).Where(p => !evenOnly || p % 2 == 0).ToArray();
        _quarantineTime = quarantineTime ?? TimeSpan.FromMinutes(1);
    }

    public static int EvenPortCount(int start, int end) =>
        end < start ? 0 : Enumerable.Range(start, end - start + 1).Count(p => p % 2 == 0);

    public int InUseCount
    {
        get { lock (_gate) return _inUse.Count; }
    }

    /// <summary>Rents a free port, or returns null when the whole range is in use.</summary>
    public int? Rent()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < _ports.Length; i++)
            {
                int port = _ports[_next];
                _next = (_next + 1) % _ports.Length;
                if (_inUse.Contains(port)) continue;
                if (_quarantined.TryGetValue(port, out var until))
                {
                    if (until > now) continue;
                    _quarantined.Remove(port);
                }
                _inUse.Add(port);
                return port;
            }
            return null;
        }
    }

    public void Release(int port)
    {
        lock (_gate) _inUse.Remove(port);
    }

    /// <summary>Releases a port that could not be bound (used by another process) and skips it for a while.</summary>
    public void Quarantine(int port)
    {
        lock (_gate)
        {
            _inUse.Remove(port);
            _quarantined[port] = DateTime.UtcNow + _quarantineTime;
        }
    }
}
