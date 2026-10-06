using System.Security.Cryptography;
using SupGalaxyServer.Rules;

namespace SupGalaxyServer.Tests;

public class TerrainTests
{
    // Reference hashes were produced by running SupGalaxy's own js/worker.js generateChunkData() in Node, with
    // Math.random replaced by makeSeededRandom(chunkKey + "_server_random") exactly like the server does.
    // The chunks were picked to cover every archetype, trees, hives, seaweed, volcano calderas and stalactites.
    [Theory]
    [InlineData("gamma:37:96", "Earth", "075925d12718ad74cbcec3f8bec7ecb7bc70562c1f5d6e3f99f428814623c5ce")]
    [InlineData("gamma:666:619", "Earth", "e6ebefe42bc0a06a5ca022c52f1d21946d8a738eee816d8136a21cabcfcfd0a7")]
    [InlineData("gamma:381:776", "Earth", "0c034b4f59226301c75fc19dd9ab46fd97c1b2eae722ee82a1dc79499cf2c38f")]
    [InlineData("gamma:185:460", "Earth", "b5e13acb99524d236246c2b40c87e3dbe575673a7f37bed16b5ae4d53855c02e")]
    [InlineData("alpha:0:5", "Desert", "bef33b5f42a3c1f8af4212df8bc046b6e0ec15a92a374cb4ba9116a39d8b1196")]
    [InlineData("vega:479:934", "Vulcan", "02580683b19136d4d401080d1379f5a227cf319425e318b3f203cfe8c4d5d9e7")]
    [InlineData("vega:1021:136", "Vulcan", "eb58730c8393daa800e120a23c728dc023557d571b9e3181e667526423494ea5")]
    [InlineData("vega:666:619", "Vulcan", "4a4861b1fd3098c1982e1cc9062c4c9ecf434b04bcbe7a63491691edf1e41104")]
    [InlineData("vega:185:460", "Vulcan", "201989c1606df136f69497fdc1694fb958d739773129fec15f7c5b029fa95c4d")]
    [InlineData("orion:0:5", "Moon", "5c66ea03b9906432ed3bf4ea03dbfdd482b4ceb7c83cdfccfc16affe6c9d57e4")]
    [InlineData("beta:10:10", "Massive", "2843665435b8fabe4f8af5749475820d56910fb475587659e3f82d7b5e1323d0")]
    [InlineData("MCWorlds:0:0", "Vulcan", "9c41f4bc9ed8575bd09a6f71a5eb50751dd3145f100787553f669b3daaf8e7bf")]
    [InlineData("VeryLong:3:4", "Massive", "6dc2c640bd82f107d0efa41f62d20b38fdb0542ab908134f3948ecb16784335a")]
    [InlineData("wörld:1:2", "Moon", "5c66ea03b9906432ed3bf4ea03dbfdd482b4ceb7c83cdfccfc16affe6c9d57e4")]
    public void GeneratedChunk_MatchesBrowserGenerator(string chunkKey, string archetype, string sha256)
    {
        var chunk = TerrainGenerator.Generate(chunkKey);

        Assert.Equal(archetype, chunk.Archetype.Name);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(chunk.Data)).ToLowerInvariant());
    }

    [Fact]
    public void VolcanoCaldera_IsDetected_LikeTheBrowser()
    {
        var volcano = TerrainGenerator.Generate("vega:479:934").Volcano;

        Assert.NotNull(volcano);
        Assert.Equal(1196, volcano!.LavaCount);
        Assert.Equal(7671.536789297659, volcano.X, 9);
        Assert.Null(TerrainGenerator.Generate("vega:666:619").Volcano);
    }

    [Fact]
    public void SeededRandom_MatchesJavaScript()
    {
        // makeSeededRandom("test") first values computed in Node.
        var r = JsRandom.MakeSeededRandom("test");
        Assert.Equal(0.71710589970462024, r.Next());
        Assert.Equal(0.34650851064361632, r.Next());
    }

    [Fact]
    public void Hypot_MatchesV8()
    {
        Assert.Equal(7.6157731058639087, JsRandom.Hypot(3, 7));
        Assert.Equal(12.083045973594571, JsRandom.Hypot(5, -11));
    }
}
