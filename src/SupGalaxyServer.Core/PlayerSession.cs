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

    /// <summary>Monotonic join order.</summary>
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

    /// <summary>True once the player has reported a position (player_move) in its current world.</summary>
    public bool HasPosition { get; internal set; }
    public string? IceState { get; internal set; }

    /// <summary>Worlds whose saved state has already been streamed to this player.</summary>
    internal HashSet<string> SyncedWorlds { get; } = new();

    // Per-player state of the server-side game rules (see Rules/WorldRules.cs).
    internal object RuleGate { get; } = new();
    internal long LastPvpHitMs;
    internal long LastLavaDamageMs;
    internal Dictionary<string, long> LastClientDamageMs { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public PlayerInfo ToInfo() => new(
        Username, World, X, Y, Z, State, Port, RemoteAddress, ConnectedAtUtc, LastSeenUtc,
        MessagesIn, MessagesOut, BytesIn, BytesOut, IceState);
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
    string? IceState);
