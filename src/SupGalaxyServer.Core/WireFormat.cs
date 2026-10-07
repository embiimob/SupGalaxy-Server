using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SupGalaxyServer;

/// <summary>
/// How the server serializes and splits what it sends over a data channel. SIPSorcery refuses to send a data-channel
/// message above 262144 bytes, and the default System.Text.Json encoder writes every '"' inside a string as
/// \u0022 (6 bytes). A 128K piece of SupGalaxy world JSON is full of quotes, so with default escaping a single
/// world_sync_chunk / ipfs_chunk_update_chunk grew past that limit and was silently dropped.
/// </summary>
public static class WireFormat
{
    /// <summary>SIPSorcery's (and the SDP-advertised) maximum data-channel message size.</summary>
    public const int MaxDataChannelMessageBytes = 262144;

    /// <summary>Budget for the escaped payload piece inside one chunk message; leaves room for the envelope.</summary>
    public const int MaxPieceEncodedBytes = 192 * 1024;

    /// <summary>Escapes only what JSON requires (like JavaScript's JSON.stringify), so '"' costs 2 bytes, not 6.</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static readonly JsonSerializerOptions Options = new() { Encoder = Encoder };

    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = Encoder };

    public static string Serialize(JsonNode node) => node.ToJsonString(Options);

    /// <summary>
    /// Splits <paramref name="payload"/> into pieces of at most <paramref name="maxChars"/> characters whose
    /// escaped size (pessimistic estimate) stays within <paramref name="maxEncodedBytes"/>. Surrogate pairs are
    /// never split, so every piece is valid UTF-16 and survives re-encoding unchanged.
    /// </summary>
    public static List<string> Split(string payload, int maxChars, int maxEncodedBytes = MaxPieceEncodedBytes)
    {
        var pieces = new List<string>();
        int start = 0;
        while (start < payload.Length)
        {
            int i = start, bytes = 0;
            while (i < payload.Length && i - start < maxChars)
            {
                char c = payload[i];
                int width = char.IsHighSurrogate(c) && i + 1 < payload.Length && char.IsLowSurrogate(payload[i + 1]) ? 2 : 1;
                int cost = width == 2 ? 12 : EncodedCost(c);
                if (i > start && (bytes + cost > maxEncodedBytes || i - start + width > maxChars)) break;
                bytes += cost;
                i += width;
            }
            pieces.Add(payload.Substring(start, i - start));
            start = i;
        }
        return pieces;
    }

    private static int EncodedCost(char c) => c switch
    {
        '"' or '\\' => 2,
        < ' ' or (char)0x7F => 6,
        < (char)0x7F => 1,
        < (char)0x800 => 2,
        _ => 6,
    };
}
