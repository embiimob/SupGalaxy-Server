using SupGalaxyServer;

// Headless (console) host for Linux servers, containers and quick local testing on 127.0.0.1.
// The Windows GUI (SupGalaxyServer.Gui) offers the same features with a graphical interface.
//
// Usage: SupGalaxyServer.Headless [--data <dir>] [--port 55555] [--players 55556-56556] [--bind 0.0.0.0] [--name SupGalaxyServer]
var dataDir = ServerSettings.DefaultDataDirectory;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--data") dataDir = args[i + 1];
}

var settingsPath = Path.Combine(dataDir, "settings.json");
var settings = ServerSettings.Load(settingsPath);
for (int i = 0; i < args.Length - 1; i++)
{
    var value = args[i + 1];
    switch (args[i])
    {
        case "--port" when int.TryParse(value, out var p):
            settings.SignalingPort = p;
            break;
        case "--players":
            var parts = value.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var s) && int.TryParse(parts[1], out var e))
            {
                settings.PlayerPortStart = s;
                settings.PlayerPortEnd = e;
            }
            break;
        case "--bind":
            settings.BindAddress = value;
            break;
        case "--name":
            settings.ServerName = value;
            break;
        case "--public":
            settings.PublicAddress = value;
            break;
    }
}

var errors = settings.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine(error);
    return 1;
}
settings.Save(settingsPath);

await using var host = new SupGalaxyServerHost(settings, dataDir);
host.Log += Console.WriteLine;
await host.StartAsync();
Console.WriteLine($"Data directory: {dataDir}");
Console.WriteLine("Commands: list [filter] | info <name> | kick <name> | block <name> | unblock <name> | blocked | worlds | save | reset | quit");

var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.TrySetResult();
};

_ = Task.Run(() =>
{
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        var cmd = line.Trim();
        var space = cmd.IndexOf(' ');
        var verb = (space < 0 ? cmd : cmd[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : cmd[(space + 1)..].Trim();
        switch (verb)
        {
            case "":
                break;
            case "list":
                var players = host.GetPlayers(arg);
                Console.WriteLine($"{players.Length} player(s):");
                foreach (var p in players)
                    Console.WriteLine($"  {p.Username,-24} {p.State,-10} world={p.World} port={p.Port} from={p.RemoteAddress}");
                break;
            case "info":
                var info = host.GetPlayers().FirstOrDefault(p => string.Equals(p.Username, arg, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(info == null ? "No such player." : info.ToString());
                break;
            case "kick":
                Console.WriteLine(host.Kick(arg) ? $"Kicked {arg}." : "No such player.");
                break;
            case "block":
                Console.WriteLine(host.Block(arg) ? $"Blocked {arg}." : "Already blocked or invalid.");
                break;
            case "unblock":
                Console.WriteLine(host.Unblock(arg) ? $"Unblocked {arg}." : "Not blocked.");
                break;
            case "blocked":
                Console.WriteLine("Blocked: " + string.Join(", ", host.BlockList.Names));
                break;
            case "worlds":
                foreach (var w in host.Worlds.Summaries())
                    Console.WriteLine($"  {w.World,-24} chunks={w.Chunks} blocks={w.Blocks} stones={w.Stones}{(w.Dirty ? " (unsaved)" : "")}");
                break;
            case "save":
                Console.WriteLine($"Saved {host.SaveNow()} changed world(s).");
                break;
            case "reset":
                Console.Write("WARNING: this permanently deletes the saved session (all world edits). Type YES to confirm: ");
                if (Console.ReadLine()?.Trim() == "YES") host.ResetSavedSession();
                else Console.WriteLine("Reset cancelled.");
                break;
            case "quit":
            case "exit":
                quit.TrySetResult();
                return;
            default:
                Console.WriteLine("Unknown command.");
                break;
        }
    }
    // stdin closed (e.g. running as a service): keep running until Ctrl+C / SIGTERM.
});

using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        quit.TrySetResult();
    });

await quit.Task;
await host.StopAsync();
return 0;
