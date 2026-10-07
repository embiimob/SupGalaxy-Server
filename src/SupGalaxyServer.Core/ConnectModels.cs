using System.Text.Json.Serialization;

namespace SupGalaxyServer;

/// <summary>
/// Body of POST /connect. Identical in shape to the offer file SupGalaxy already produces in connectToServer()
/// (js/web-rtc.js): { world, user, offer: { type, sdp }, iceCandidates: [...] }.
/// </summary>
public sealed class ConnectRequest
{
    [JsonPropertyName("world")] public string? World { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("offer")] public SessionDescription? Offer { get; set; }
    [JsonPropertyName("iceCandidates")] public List<IceCandidate>? IceCandidates { get; set; }

    /// <summary>Optional client capabilities, e.g. "http_world_sync" (see ClientIntegrationNotes).</summary>
    [JsonPropertyName("features")] public List<string>? Features { get; set; }
}

public sealed class SessionDescription
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("sdp")] public string? Sdp { get; set; }
}

public sealed class IceCandidate
{
    [JsonPropertyName("candidate")] public string? Candidate { get; set; }
    [JsonPropertyName("sdpMid")] public string? SdpMid { get; set; }
    [JsonPropertyName("sdpMLineIndex")] public int? SdpMLineIndex { get; set; }
    [JsonPropertyName("usernameFragment")] public string? UsernameFragment { get; set; }
}

/// <summary>Response of POST /connect.</summary>
public sealed class ConnectResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>bad_request | blocked | name_in_use | server_full | negotiation_failed (only when ok = false).</summary>
    [JsonPropertyName("code")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Code { get; set; }

    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }

    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("protocol")] public int Protocol { get; set; } = GameRelay.ProtocolVersion;
    [JsonPropertyName("user")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? User { get; set; }
    [JsonPropertyName("world")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? World { get; set; }
    [JsonPropertyName("port")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Port { get; set; }
    [JsonPropertyName("answer")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SessionDescription? Answer { get; set; }
    [JsonPropertyName("iceCandidates")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<IceCandidate>? IceCandidates { get; set; }

    /// <summary>Requested features the server accepted (only those it supports and has enabled).</summary>
    [JsonPropertyName("features")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Features { get; set; }

    internal int StatusCode { get; set; } = 200;

    internal static ConnectResponse Fail(int status, string code, string error, string serverName) =>
        new() { Ok = false, StatusCode = status, Code = code, Error = error, ServerName = serverName };
}
