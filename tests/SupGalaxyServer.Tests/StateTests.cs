namespace SupGalaxyServer.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sgs-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class WorldStateStoreTests
{
    [Theory]
    [InlineData("alpha", 0, 0, "alpha:0:0")]
    [InlineData("averylongworld", 17, 33, "averylon:1:2")]
    [InlineData("w", -1, -17, "w:1023:1022")]
    public void ChunkKey_MatchesSupGalaxy(string world, long wx, long wz, string expected)
    {
        Assert.Equal(expected, WorldStateStore.ChunkKeyForBlock(world, wx, wz));
    }

    [Fact]
    public void SaveIsIncremental_AndLoadRestoresState()
    {
        using var dir = new TempDir();
        var store = new WorldStateStore(dir.Path);
        store.ApplyBlock("alpha", 1, 2, 3, 4, "beta");
        store.ApplyBlock("gamma", 1, 2, 3, 5, null);
        store.SetStone(StoneKind.Calligraphy, "alpha", "1,2,3", System.Text.Json.Nodes.JsonNode.Parse("""{"text":"hello"}""")!);

        Assert.Equal(2, store.Save());
        Assert.Equal(0, store.Save());

        store.ApplyBlock("alpha", 1, 2, 3, 6, null);
        Assert.Equal(1, store.Save());

        var restored = new WorldStateStore(dir.Path);
        Assert.Equal(2, restored.Load());
        Assert.Equal(6, restored.GetBlock("alpha", 1, 2, 3));
        Assert.Equal("beta", restored.GetForeignOrigin("alpha", 1, 2, 3));
        Assert.Equal(5, restored.GetBlock("gamma", 1, 2, 3));
        Assert.True(restored.HasStone(StoneKind.Calligraphy, "alpha", "1,2,3"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "session.json")));
    }

    [Fact]
    public void Reset_DeletesSavedSession()
    {
        using var dir = new TempDir();
        var session = Path.Combine(dir.Path, "session");
        var store = new WorldStateStore(session);
        store.ApplyBlock("alpha", 1, 2, 3, 4, null);
        store.Save();

        store.Reset();

        Assert.False(Directory.Exists(session));
        Assert.Null(store.GetBlock("alpha", 1, 2, 3));
        Assert.Equal(0, new WorldStateStore(session).Load());
    }

    [Fact]
    public void CorruptWorldFile_IsSkipped()
    {
        using var dir = new TempDir();
        var store = new WorldStateStore(dir.Path);
        store.ApplyBlock("alpha", 1, 2, 3, 4, null);
        store.Save();
        File.WriteAllText(Path.Combine(dir.Path, "worlds", "broken.json"), "{ not json");

        var restored = new WorldStateStore(dir.Path);
        Assert.Equal(1, restored.Load());
    }

    [Fact]
    public void SyncPayload_IsNullForUnknownWorld()
    {
        Assert.Null(new WorldStateStore(null).BuildSyncPayload("nope"));
    }
}

public class PlayerRegistryTests
{
    [Fact]
    public void OnlyOneSessionPerName_CaseInsensitive()
    {
        var reg = new PlayerRegistry();
        Assert.True(reg.TryAdd(new PlayerSession("Alice", "w", null)));
        Assert.False(reg.TryAdd(new PlayerSession("alice", "w", null)));
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void Remove_OnlyRemovesSameSession()
    {
        var reg = new PlayerRegistry();
        var a = new PlayerSession("alice", "w", null);
        reg.TryAdd(a);
        Assert.False(reg.Remove(new PlayerSession("alice", "w", null)));
        Assert.True(reg.Remove(a));
        Assert.True(reg.TryAdd(new PlayerSession("alice", "w", null)));
    }

    [Fact]
    public void Search_IsAlphabeticalAndFiltered()
    {
        var reg = new PlayerRegistry();
        foreach (var n in new[] { "zed", "Bob", "alice", "carol" }) reg.TryAdd(new PlayerSession(n, n == "carol" ? "moon" : "earth", null));

        Assert.Equal(new[] { "alice", "Bob", "carol", "zed" }, reg.Search(null).Select(p => p.Username));
        Assert.Equal(new[] { "carol" }, reg.Search("moon").Select(p => p.Username));
        Assert.Equal(new[] { "Bob" }, reg.Search("bo").Select(p => p.Username));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" bob", false)]
    [InlineData("server", false)]
    [InlineData("bad\nname", false)]
    [InlineData("bob", true)]
    public void ValidateUsername(string? name, bool valid)
    {
        Assert.Equal(valid, PlayerRegistry.ValidateUsername(name, "Server") == null);
    }
}

public class PortAllocatorTests
{
    [Fact]
    public void RentsEachPortOnce_ThenReturnsNull()
    {
        var ports = new PortAllocator(100, 102);
        var rented = new[] { ports.Rent(), ports.Rent(), ports.Rent() };
        Assert.Equal(new int?[] { 100, 101, 102 }, rented);
        Assert.Null(ports.Rent());

        ports.Release(101);
        Assert.Equal(101, ports.Rent());
    }

    [Fact]
    public void QuarantinedPort_IsSkipped()
    {
        var ports = new PortAllocator(100, 101);
        var p = ports.Rent()!.Value;
        ports.Quarantine(p);
        Assert.Equal(101, ports.Rent());
        Assert.Null(ports.Rent());
    }

    [Fact]
    public void EvenOnly_SkipsOddPorts()
    {
        var ports = new PortAllocator(55555, 55560, evenOnly: true);
        Assert.Equal(3, ports.Capacity);
        Assert.Equal(new int?[] { 55556, 55558, 55560, null }, new[] { ports.Rent(), ports.Rent(), ports.Rent(), ports.Rent() });
    }

    [Fact]
    public void DefaultRange_Is1001Ports()
    {
        var s = new ServerSettings();
        Assert.Equal(55555, s.SignalingPort);
        Assert.Equal(55556, s.PlayerPortStart);
        Assert.Equal(56556, s.PlayerPortEnd);
        Assert.Equal(1001, s.PlayerPortCount);
        Assert.Equal(501, s.MaxPlayers);
        Assert.Equal(100 * 1024 * 1024, s.MaxImportSize);
        Assert.Empty(s.Validate());
    }
}

public class BlockListAndSettingsTests
{
    [Fact]
    public void BlockList_PersistsCaseInsensitive()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "blocked.json");
        var list = new BlockList(path);
        Assert.True(list.Block("Griefer"));
        Assert.False(list.Block("griefer"));
        Assert.True(list.IsBlocked("GRIEFER"));

        var reloaded = new BlockList(path);
        Assert.True(reloaded.IsBlocked("griefer"));
        Assert.True(reloaded.Unblock("griefer"));
        Assert.False(new BlockList(path).IsBlocked("griefer"));
    }

    [Fact]
    public void Settings_RoundTripAndValidate()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        new ServerSettings { SignalingPort = 60000, PlayerPortStart = 60001, PlayerPortEnd = 60010 }.Save(path);
        var s = ServerSettings.Load(path);
        Assert.Equal(60000, s.SignalingPort);
        Assert.Equal(10, s.PlayerPortCount);
        Assert.Equal(5, s.MaxPlayers);

        s.SignalingPort = 60005;
        Assert.NotEmpty(s.Validate());
    }
}
