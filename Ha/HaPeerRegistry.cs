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

    /// <summary>
    /// Tells a Home Assistant connection that one of its peers is gone, whatever tore it down —
    /// a client stop, an ICE failure, the connection state going closed, or a server-side abort.
    /// Returns false when <paramref name="peerId"/> is not a live Home Assistant connection, which
    /// is also the case while its socket is being torn down: a dying client is told nothing.
    /// </summary>
    bool TrySendClosed(string peerId, string kind, int roomId, string reason);
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

    public bool TrySendClosed(string peerId, string kind, int roomId, string reason)
    {
        if (!_connections.TryGetValue(peerId, out var connection)) return false;

        connection.TryEnqueue(HaFrames.Build(
            HaProtocol.WebRtcClosed,
            new HaWebRtcClosedData(roomId, kind, reason)));
        return true;
    }
}
