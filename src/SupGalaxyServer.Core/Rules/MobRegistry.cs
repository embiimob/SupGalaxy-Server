using System.Text.Json.Nodes;

namespace SupGalaxyServer.Rules;

/// <summary>
/// Remembers which player simulates each mob. SupGalaxy's mob AI runs on the client that spawned the mob (the
/// "spawner", or the pet owner for tamed wolves). Mob AI needs THREE.js physics and is not run on the server, so the
/// server instead makes sure only that player can move, kill or despawn the mob.
/// </summary>
public sealed class MobRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string World, string Id), string> _owners = new();

    public int Count
    {
        get { lock (_gate) return _owners.Count; }
    }

    /// <summary>Mob ids may be numbers or strings in SupGalaxy, so the raw JSON text is the key.</summary>
    public static string? IdOf(JsonNode? id) => id is JsonValue ? id.ToJsonString() : null;

    /// <summary>True if <paramref name="user"/> owns the mob. Unknown mobs are claimed by the first player who reports them.</summary>
    public bool TryClaim(string world, string id, string user)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue((world, id), out var owner))
                return string.Equals(owner, user, StringComparison.OrdinalIgnoreCase);
            _owners[(world, id)] = user;
            return true;
        }
    }

    /// <summary>True if a player other than <paramref name="user"/> owns the mob.</summary>
    public bool IsOwnedByOther(string world, string id, string user)
    {
        lock (_gate)
            return _owners.TryGetValue((world, id), out var owner) && !string.Equals(owner, user, StringComparison.OrdinalIgnoreCase);
    }

    public void Release(string world, string id)
    {
        lock (_gate) _owners.Remove((world, id));
    }

    public void Transfer(string world, string id, string newOwner)
    {
        lock (_gate) _owners[(world, id)] = newOwner;
    }

    /// <summary>Forgets every mob owned by <paramref name="user"/> and returns them.</summary>
    public List<(string World, string Id)> ReleaseAll(string user)
    {
        lock (_gate)
        {
            var owned = _owners.Where(kv => string.Equals(kv.Value, user, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var k in owned) _owners.Remove(k);
            return owned;
        }
    }
}
