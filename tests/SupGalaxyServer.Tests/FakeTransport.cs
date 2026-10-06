using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace SupGalaxyServer.Tests;

internal sealed class FakeTransport : IPlayerTransport
{
    public ConcurrentQueue<string> Sent { get; } = new();
    public bool IsOpen { get; set; } = true;
    public ulong BufferedAmount { get; set; }
    public bool Closed { get; private set; }

    public void Send(string message) => Sent.Enqueue(message);

    public void Close()
    {
        Closed = true;
        IsOpen = false;
    }

    public List<JsonObject> Messages => Sent.Select(s => (JsonObject)JsonNode.Parse(s)!).ToList();

    public List<JsonObject> OfType(string type) => Messages.Where(m => (string?)m["type"] == type).ToList();

    public void Clear() => Sent.Clear();
}
