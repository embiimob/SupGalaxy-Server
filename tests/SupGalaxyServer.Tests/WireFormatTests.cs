using System.Text;
using System.Text.Json.Nodes;

namespace SupGalaxyServer.Tests;

public class WireFormatTests
{
    [Theory]
    [InlineData('"')]
    [InlineData('x')]
    [InlineData('é')]
    [InlineData('\n')]
    public void ChunkMessages_StayUnderDataChannelLimit(char fill)
    {
        var payload = new string(fill, 1_000_000);
        var pieces = WireFormat.Split(payload, GameRelay.SyncChunkSize);
        Assert.Equal(payload, string.Concat(pieces));
        foreach (var piece in pieces)
        {
            var msg = GameRelay.Json(new JsonObject
            {
                ["type"] = "world_sync_chunk", ["world"] = "Ender", ["transactionId"] = "world_sync_someone_1790000000000",
                ["index"] = 999999, ["chunk"] = piece, ["total"] = 999999,
            });
            Assert.True(Encoding.UTF8.GetByteCount(msg) <= WireFormat.MaxDataChannelMessageBytes);
        }
    }

    [Fact]
    public void Split_NeverBreaksSurrogatePairs()
    {
        var payload = string.Concat(Enumerable.Repeat("a😀", 100_000));
        var pieces = WireFormat.Split(payload, 1001);
        Assert.Equal(payload, string.Concat(pieces));
        Assert.All(pieces, p => Assert.False(char.IsHighSurrogate(p[^1])));
        Assert.All(pieces, p => Assert.False(char.IsLowSurrogate(p[0])));
    }

    [Fact]
    public void Quotes_AreEscapedLikeJsonStringify()
    {
        Assert.Equal("""{"chunk":"{\"x\":1}"}""", GameRelay.Json(new JsonObject { ["chunk"] = """{"x":1}""" }));
    }
}
