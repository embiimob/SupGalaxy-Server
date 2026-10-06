using System.Net;
using System.Text.Json;

namespace SupGalaxyServer;

/// <summary>
/// User configurable server settings. Persisted as settings.json in the data directory.
/// </summary>
public sealed class ServerSettings
{
    public const int DefaultSignalingPort = 55555;
    public const int DefaultPlayerPortStart = 55556;
    public const int DefaultPlayerPortEnd = 56556;

    /// <summary>Name the server uses as its peer name inside SupGalaxy (never usable as a player name).</summary>
    public string ServerName { get; set; } = "SupGalaxyServer";

    /// <summary>
    /// Address the signaling listener and the player WebRTC sockets bind to.
    /// "0.0.0.0" (default) listens on every interface. A loopback (127.0.0.1) listener
    /// is always added so the server can be tested locally.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Main connection (signaling) port. SupGalaxy POSTs its WebRTC offer here.</summary>
    public int SignalingPort { get; set; } = DefaultSignalingPort;

    /// <summary>First UDP port handed out to connected players (one even port per player).</summary>
    public int PlayerPortStart { get; set; } = DefaultPlayerPortStart;

    /// <summary>Last UDP port (inclusive) handed out to connected players.</summary>
    public int PlayerPortEnd { get; set; } = DefaultPlayerPortEnd;

    /// <summary>
    /// Optional public IP address of this server (e.g. when behind NAT with the player port range forwarded).
    /// When set, an extra ICE host candidate with this address is returned to the client.
    /// </summary>
    public string? PublicAddress { get; set; }

    /// <summary>STUN/TURN servers used by the server side of each WebRTC connection.</summary>
    public List<string> IceServers { get; set; } = new() { "stun:supturn.com:3478" };

    /// <summary>Incremental save interval in minutes.</summary>
    public int SaveIntervalMinutes { get; set; } = 10;

    /// <summary>Optional PFX certificate used to serve the signaling port over HTTPS.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Maximum accepted size of a single data channel message (characters).</summary>
    public int MaxMessageSize { get; set; } = 512 * 1024;

    /// <summary>Maximum number of data channel messages a single player may send per second.</summary>
    public int MaxMessagesPerSecond { get; set; } = 600;

    /// <summary>Maximum re-assembled size (characters) of one imported Chunk Keyword / IPFS world update.</summary>
    public int MaxImportSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>An unfinished import transfer is discarded after this many seconds without a new chunk.</summary>
    public int ImportTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum unfinished import transfers per player.</summary>
    public int MaxPendingImportsPerPlayer { get; set; } = 4;

    public int PlayerPortCount => PlayerPortEnd - PlayerPortStart + 1;

    /// <summary>Maximum simultaneous players: one even UDP port of the player range per player.</summary>
    public int MaxPlayers => PortAllocator.EvenPortCount(PlayerPortStart, PlayerPortEnd);

    /// <summary>Returns a list of validation errors (empty when the settings are valid).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerName)) errors.Add("Server name is required.");
        if (!IPAddress.TryParse(BindAddress, out _)) errors.Add("Bind address must be a valid IP address.");
        if (!string.IsNullOrWhiteSpace(PublicAddress) && !IPAddress.TryParse(PublicAddress, out _))
            errors.Add("Public address must be a valid IP address.");
        if (SignalingPort is < 1 or > 65535) errors.Add("Main port must be between 1 and 65535.");
        if (PlayerPortStart is < 1 or > 65535 || PlayerPortEnd is < 1 or > 65535)
            errors.Add("Player ports must be between 1 and 65535.");
        if (PlayerPortEnd < PlayerPortStart) errors.Add("Player port range end must be >= start.");
        else if (PortAllocator.EvenPortCount(PlayerPortStart, PlayerPortEnd) == 0)
            errors.Add("Player port range must contain at least one even port (WebRTC/RTP sockets bind on even ports).");
        if (SignalingPort >= PlayerPortStart && SignalingPort <= PlayerPortEnd)
            errors.Add("Main port must not be inside the player port range.");
        if (SaveIntervalMinutes < 1) errors.Add("Save interval must be at least 1 minute.");
        if (MaxMessageSize < 1024) errors.Add("Max message size must be at least 1024.");
        if (MaxMessagesPerSecond < 1) errors.Add("Max messages per second must be at least 1.");
        if (MaxImportSize < 1024) errors.Add("Max import size must be at least 1024.");
        if (ImportTimeoutSeconds < 1) errors.Add("Import timeout must be at least 1 second.");
        if (MaxPendingImportsPerPlayer < 1) errors.Add("Max pending imports per player must be at least 1.");
        return errors;
    }

    public ServerSettings Clone()
    {
        var copy = (ServerSettings)MemberwiseClone();
        copy.IceServers = new List<string>(IceServers);
        return copy;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ServerSettings Load(string path)
    {
        if (!File.Exists(path)) return new ServerSettings();
        try
        {
            return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), JsonOptions) ?? new ServerSettings();
        }
        catch (JsonException)
        {
            return new ServerSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileUtil.WriteAllTextAtomic(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SupGalaxyServer");
}

internal static class FileUtil
{
    /// <summary>Writes to a temp file then swaps it in so a crash never leaves a half written file.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
