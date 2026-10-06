namespace SupGalaxyServer.Tests;

public abstract class RelayTestBase
{
    internal readonly PlayerRegistry Players = new();
    internal readonly WorldStateStore Worlds = new(null);
    internal readonly ServerSettings Settings = new() { ServerName = "TestServer" };
    internal readonly GameRelay Relay;

    protected RelayTestBase()
    {
        Relay = new GameRelay(Players, Worlds, Settings);
    }

    internal (PlayerSession Session, FakeTransport Transport) Join(string name, string world = "alpha")
    {
        var t = new FakeTransport();
        var s = new PlayerSession(name, world, "127.0.0.1") { Transport = t };
        Assert.True(Players.TryAdd(s));
        Relay.OnPlayerJoined(s);
        return (s, t);
    }

    internal static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) Assert.Fail("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
