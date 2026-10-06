using SIPSorcery.Net;

namespace SupGalaxyServer;

/// <summary>Player transport backed by a SIPSorcery WebRTC peer connection + "game" data channel.</summary>
internal sealed class WebRtcPlayerTransport : IPlayerTransport
{
    private readonly object _sendGate = new();
    private readonly RTCPeerConnection _pc;
    private RTCDataChannel? _dc;

    public WebRtcPlayerTransport(RTCPeerConnection pc) => _pc = pc;

    public RTCPeerConnection PeerConnection => _pc;

    /// <summary>Attaches the data channel. Returns false if a channel is already attached.</summary>
    public bool Attach(RTCDataChannel dc) => Interlocked.CompareExchange(ref _dc, dc, null) == null;

    public bool IsOpen => _dc is { readyState: RTCDataChannelState.open };

    public ulong BufferedAmount => _dc?.bufferedAmount ?? 0;

    public void Send(string message)
    {
        var dc = _dc ?? throw new InvalidOperationException("Data channel not attached.");
        lock (_sendGate) dc.send(message);
    }

    public void Close()
    {
        try { _dc?.close(); } catch (Exception) { /* already closed */ }
        try { _pc.close(); } catch (Exception) { /* already closed */ }
    }
}
