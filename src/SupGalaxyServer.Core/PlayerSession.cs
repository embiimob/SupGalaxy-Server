namespace SupGalaxyServer;

/// <summary>Abstraction over the player's data channel so the game relay can be tested without WebRTC.</summary>
public interface IPlayerTransport
{
    bool IsOpen { get; }

    /// <summary>Bytes queued but not yet sent. Used to skip non-critical updates for congested players.</summary>
    ulong BufferedAmount { get; }

    void Send(string message);

    void Close();
}

public enum PlayerState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>A connected (or connecting) player.</summary>
public sealed class PlayerSession
{
    private readonly object _rateGate = new();
    private long _rateWindowStart;
    private int _rateCount;
    private long _messagesIn;
    private long _messagesOut;
    private long _bytesIn;
    private long _bytesOut;
    private int _closed;

    public PlayerSession(string username, string? world, string? remoteAddress)
    {
        Username = username;
        World = string.IsNullOrEmpty(world) ? null : world;
        RemoteAddress = remoteAddress;
        ConnectedAtUtc = DateTime.UtcNow;
        LastSeenUtc = ConnectedAtUtc;
    }

    public string Username { get; }
    public string? RemoteAddress { get; }
    public DateTime ConnectedAtUtc { get; }

    /// <summary>Monotonic join order, used to pick the oldest player as world authority.</summary>
    public long JoinSequence { get; internal set; }

    public volatile PlayerState State = PlayerState.Connecting;

    /// <summary>UDP port (from the player range) used by this player's WebRTC connection.</summary>
    public int Port { get; internal set; }

    public IPlayerTransport? Transport { get; internal set; }

    public string? World { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Z { get; internal set; }
    public DateTime LastSeenUtc { get; internal set; }
    public string? IceState { get; internal set; }

    /// <summary>Worlds whose saved state has already been streamed to this player.</summary>
    internal HashSet<string> SyncedWorlds { get; } = new();

    // World-state updates (block/stone edits, imports) that arrive while a world sync snapshot is being streamed
    // are held back and delivered right after the snapshot, so a newer live edit is never overwritten by the
    // older snapshot on the client and nothing applied after the snapshot is missed.
    private readonly object _syncGate = new();
    private readonly List<string> _syncBacklog = new();
    private long _syncBacklogChars;
    private int _activeSyncs;
    private bool _syncBacklogOverflowed;

    internal const long MaxSyncBacklogChars = 128L * 1024 * 1024;

    internal bool IsSyncing
    {
        get { lock (_syncGate) return _activeSyncs > 0; }
    }

    internal void BeginSync()
    {
        lock (_syncGate) _activeSyncs++;
    }

    /// <summary>
    /// Ends one snapshot stream. When it was the last one, flushes the held back updates in order.
    /// Returns false if the backlog overflowed and was discarded (the caller must send a fresh snapshot).
    /// </summary>
    internal bool EndSync()
    {
        lock (_syncGate)
        {
            if (--_activeSyncs > 0) return true;
            _activeSyncs = 0;
            foreach (var m in _syncBacklog) Send(m);
            _syncBacklog.Clear();
            _syncBacklogChars = 0;
            var ok = !_syncBacklogOverflowed;
            _syncBacklogOverflowed = false;
            return ok;
        }
    }

    /// <summary>Delivers a world-state update now, or holds it until the running world sync has been sent.</summary>
    internal void SendStateUpdate(string message)
    {
        lock (_syncGate)
        {
            if (_activeSyncs == 0)
            {
                Send(message);
                return;
            }
            if (_syncBacklogOverflowed) return;
            _syncBacklogChars += message.Length;
            if (_syncBacklogChars > MaxSyncBacklogChars)
            {
                _syncBacklog.Clear();
                _syncBacklogChars = 0;
                _syncBacklogOverflowed = true;
                return;
            }
            _syncBacklog.Add(message);
        }
    }

    public long MessagesIn => Interlocked.Read(ref _messagesIn);
    public long MessagesOut => Interlocked.Read(ref _messagesOut);
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

    internal bool IsClosed => Volatile.Read(ref _closed) == 1;

    internal void CountIn(int chars)
    {
        Interlocked.Increment(ref _messagesIn);
        Interlocked.Add(ref _bytesIn, chars);
        LastSeenUtc = DateTime.UtcNow;
    }

    /// <summary>Simple fixed one second window rate limiter.</summary>
    internal bool TryConsumeRate(int maxPerSecond)
    {
        lock (_rateGate)
        {
            long now = Environment.TickCount64;
            if (now - _rateWindowStart >= 1000)
            {
                _rateWindowStart = now;
                _rateCount = 0;
            }
            return ++_rateCount <= maxPerSecond;
        }
    }

    /// <summary>Sends a message if the channel is open. Never throws.</summary>
    public bool Send(string message)
    {
        var t = Transport;
        if (t == null || !t.IsOpen || IsClosed) return false;
        try
        {
            t.Send(message);
            Interlocked.Increment(ref _messagesOut);
            Interlocked.Add(ref _bytesOut, message.Length);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public PlayerInfo ToInfo(bool isAuthority) => new(
        Username, World, X, Y, Z, State, Port, RemoteAddress, ConnectedAtUtc, LastSeenUtc,
        MessagesIn, MessagesOut, BytesIn, BytesOut, IceState, isAuthority);
}

/// <summary>Immutable snapshot of a player for display in the GUI / console.</summary>
public sealed record PlayerInfo(
    string Username,
    string? World,
    double X,
    double Y,
    double Z,
    PlayerState State,
    int Port,
    string? RemoteAddress,
    DateTime ConnectedAtUtc,
    DateTime LastSeenUtc,
    long MessagesIn,
    long MessagesOut,
    long BytesIn,
    long BytesOut,
    string? IceState,
    bool IsWorldAuthority);
