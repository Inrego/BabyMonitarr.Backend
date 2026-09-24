using System.Collections.Concurrent;
using BabyMonitarr.Backend.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BabyMonitarr.Backend.Services;

/// <summary>
/// Lets the WebRTC services deliver server-side signalling to a cast receiver page.
///
/// A receiver signals over <see cref="CastReceiverHub"/>, not <see cref="AudioStreamHub"/>, so its
/// connection id means nothing to the hub context the WebRTC services push to. The services ask
/// here first, the same way they ask the Home Assistant router, and fall back to AudioStreamHub
/// when the peer id is not a receiver.
/// </summary>
public interface ICastReceiverPeers
{
    /// <summary>Returns false when <paramref name="peerId"/> is not a joined receiver connection.</summary>
    bool TrySendIceCandidate(string peerId, string kind, string candidate, string sdpMid, int sdpMLineIndex);

    /// <summary>Tells the receiver a peer is gone so it renegotiates at once instead of timing out.</summary>
    bool TrySendClosed(string peerId, string kind);
}

public sealed class CastReceiverPeers : ICastReceiverPeers
{
    private readonly IHubContext<CastReceiverHub> _hub;
    private readonly ConcurrentDictionary<string, byte> _connections = new();

    public CastReceiverPeers(IHubContext<CastReceiverHub> hub)
    {
        _hub = hub;
    }

    public void Register(string connectionId) => _connections[connectionId] = 0;

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    public bool TrySendIceCandidate(string peerId, string kind, string candidate, string sdpMid, int sdpMLineIndex)
    {
        if (!_connections.ContainsKey(peerId)) return false;
        _ = _hub.Clients.Client(peerId).SendAsync("IceCandidate", kind, candidate, sdpMid, sdpMLineIndex);
        return true;
    }

    public bool TrySendClosed(string peerId, string kind)
    {
        if (!_connections.ContainsKey(peerId)) return false;
        _ = _hub.Clients.Client(peerId).SendAsync("PeerClosed", kind);
        return true;
    }
}
