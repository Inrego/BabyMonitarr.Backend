using System.Collections.Concurrent;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// Lets the WebRTC services deliver a server-generated ICE candidate to a Home Assistant peer.
///
/// The WebRTC services push candidates to SignalR connection ids. A Home Assistant peer id is a
/// /ha/ws connection id instead, so <see cref="TrySendIceCandidate"/> claims those and the service
/// falls back to SignalR for everything else. Deliberately dependency-free: the WebRTC services
/// depend on this, and the bridge that drives them depends on both.
/// </summary>
public interface IHaPeerRouter
{
    /// <summary>
    /// Sends the candidate when <paramref name="peerId"/> is a live Home Assistant connection.
    /// Returns false when it is not, which means the caller owns delivery.
    /// </summary>
    bool TrySendIceCandidate(
        string peerId, string kind, int roomId, string candidate, string sdpMid, int sdpMLineIndex);
}

public sealed class HaPeerRegistry : IHaPeerRouter
{
    private readonly ConcurrentDictionary<string, HaConnection> _connections = new();

    public void Register(HaConnection connection) => _connections[connection.Id] = connection;

    public void Unregister(HaConnection connection) => _connections.TryRemove(connection.Id, out _);

    public bool TrySendIceCandidate(
        string peerId, string kind, int roomId, string candidate, string sdpMid, int sdpMLineIndex)
    {
        if (!_connections.TryGetValue(peerId, out var connection)) return false;

        connection.TryEnqueue(HaFrames.Build(
            HaProtocol.WebRtcCandidate,
            new HaWebRtcCandidateData(roomId, kind, candidate, sdpMid, sdpMLineIndex)));
        return true;
    }
}
