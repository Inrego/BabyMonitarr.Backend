using System.Text.Json;
using BabyMonitarr.Backend.Services;
using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// The remote offer did not contain the codec this room is forwarded in. Distinct from a generic
/// failure because it is actionable: the client controls what it offers.
/// </summary>
public sealed class WebRtcCodecMismatchException : Exception
{
    public WebRtcCodecMismatchException(string message) : base(message) { }
}

public interface IHaWebRtcBridge
{
    bool Handles(string type);

    Task<HaCommandResult> HandleAsync(
        HaConnection connection, string type, JsonElement data, string? reference, CancellationToken ct);

    /// <summary>Tears down every peer this connection owns, mirroring the hub's disconnect.</summary>
    Task CloseAllAsync(HaConnection connection);
}

/// <summary>
/// The webrtc.* half of the protocol. Home Assistant's camera API hands an integration an offer
/// and expects an answer, which is the opposite of the backend's own flow, so this drives the
/// answer paths on the WebRTC services rather than the offer paths the app clients use.
///
/// The /ha/ws connection id is the peer id, exactly as the SignalR connection id is for the hub.
/// </summary>
public class HaWebRtcBridge : IHaWebRtcBridge
{
    private readonly ILogger<HaWebRtcBridge> _logger;
    private readonly IVideoWebRtcService _videoWebRtcService;
    private readonly IAudioWebRtcService _audioWebRtcService;

    public HaWebRtcBridge(
        ILogger<HaWebRtcBridge> logger,
        IVideoWebRtcService videoWebRtcService,
        IAudioWebRtcService audioWebRtcService)
    {
        _logger = logger;
        _videoWebRtcService = videoWebRtcService;
        _audioWebRtcService = audioWebRtcService;
    }

    public bool Handles(string type) => type.StartsWith("webrtc.", StringComparison.Ordinal);

    public async Task<HaCommandResult> HandleAsync(
        HaConnection connection, string type, JsonElement data, string? reference, CancellationToken ct)
    {
        return type switch
        {
            HaProtocol.CmdWebRtcOffer => await HandleOfferAsync(connection, data, reference),
            HaProtocol.CmdWebRtcCandidate => await HandleCandidateAsync(connection, data, reference),
            HaProtocol.CmdWebRtcStop => await HandleStopAsync(connection, data, reference),
            _ => Reply(HaFrames.Error(
                HaProtocol.ErrUnknownType,
                $"Unsupported webrtc command '{type}' in protocol v{HaProtocol.Version}",
                reference))
        };
    }

    private async Task<HaCommandResult> HandleOfferAsync(
        HaConnection connection, JsonElement data, string? reference)
    {
        int? roomId = HaJson.Int(data, "room_id");
        string? sdp = HaJson.String(data, "sdp");
        string kind = ResolveKind(data);

        if (roomId == null || string.IsNullOrWhiteSpace(sdp))
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id and sdp", reference));
        }

        try
        {
            string answer = kind == HaProtocol.KindAudio
                ? await _audioWebRtcService.CreateAudioAnswer(connection.Id, roomId.Value, sdp!, HostHint(connection))
                : await _videoWebRtcService.CreateVideoAnswer(connection.Id, roomId.Value, sdp!, HostHint(connection));

            return Reply(HaFrames.Build(
                HaProtocol.WebRtcAnswer,
                new HaWebRtcAnswerData(roomId.Value, kind, answer),
                reference));
        }
        catch (WebRtcCodecMismatchException ex)
        {
            // The one failure the client can actually fix, so it gets its own code.
            _logger.LogWarning("HA {Kind} offer for room {RoomId} rejected: {Message}", kind, roomId, ex.Message);
            return Reply(HaFrames.Error(HaProtocol.ErrWebRtcCodecMismatch, ex.Message, reference));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer HA {Kind} offer for room {RoomId}", kind, roomId);
            return Reply(HaFrames.Error(HaProtocol.ErrWebRtcFailed, ex.Message, reference));
        }
    }

    private async Task<HaCommandResult> HandleCandidateAsync(
        HaConnection connection, JsonElement data, string? reference)
    {
        int? roomId = HaJson.Int(data, "room_id");
        string? candidate = HaJson.String(data, "candidate");
        string kind = ResolveKind(data);

        if (roomId == null || candidate == null)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id and candidate", reference));
        }

        var init = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = HaJson.String(data, "sdp_mid") ?? string.Empty,
            sdpMLineIndex = (ushort)(HaJson.Int(data, "sdp_m_line_index") ?? 0)
        };

        try
        {
            if (kind == HaProtocol.KindAudio)
            {
                await _audioWebRtcService.AddAudioIceCandidate(connection.Id, roomId.Value, init);
            }
            else
            {
                await _videoWebRtcService.AddVideoIceCandidate(connection.Id, roomId.Value, init);
            }
        }
        catch (KeyNotFoundException)
        {
            // A candidate arriving after the peer went away is routine, not an error worth raising.
            _logger.LogDebug(
                "Dropped HA {Kind} ICE candidate for room {RoomId}: no peer connection", kind, roomId);
        }

        // Candidates are high-rate and fire-and-forget; an ack per candidate would be noise.
        return HaCommandResult.Empty;
    }

    private async Task<HaCommandResult> HandleStopAsync(
        HaConnection connection, JsonElement data, string? reference)
    {
        int? roomId = HaJson.Int(data, "room_id");
        if (roomId == null)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id", reference));
        }

        string kind = ResolveKind(data);

        // The closed frame is not built here. Every teardown — a failed peer, a peer the remote
        // closed, a codec drift mid-stream — has to report itself, so the WebRTC services own the
        // notification and emit exactly one per peer. A stop for a peer that is already gone
        // therefore acks without a closed frame; there was nothing left to close.
        if (kind == HaProtocol.KindAudio)
        {
            await _audioWebRtcService.CloseAudioPeerConnection(
                connection.Id, roomId.Value, HaWebRtcCloseReasons.ClosedByClient);
        }
        else
        {
            await _videoWebRtcService.CloseVideoPeerConnection(
                connection.Id, roomId.Value, HaWebRtcCloseReasons.ClosedByClient);
        }

        return Reply(HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdWebRtcStop), reference));
    }

    public async Task CloseAllAsync(HaConnection connection)
    {
        // Mirrors AudioStreamHub.OnDisconnectedAsync: a dropped socket must not leak peers.
        try
        {
            await _videoWebRtcService.CloseAllVideoPeerConnections(connection.Id);
            await _audioWebRtcService.CloseAllAudioPeerConnections(connection.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing WebRTC peers for HA connection {ConnectionId}", connection.Id);
        }
    }

    /// <summary>Anything but "audio" is video, so an omitted kind means the camera.</summary>
    private static string ResolveKind(JsonElement data) =>
        string.Equals(HaJson.String(data, "kind"), HaProtocol.KindAudio, StringComparison.OrdinalIgnoreCase)
            ? HaProtocol.KindAudio
            : HaProtocol.KindVideo;

    /// <summary>
    /// The host the client reached us on, which is what the ICE advertised-address logic uses to
    /// decide which local address to announce — the same hint the hub takes from its request.
    /// </summary>
    private static string? HostHint(HaConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl)) return null;

        return Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri) &&
               !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static HaCommandResult Reply(string frame) =>
        new(new[] { frame }, Array.Empty<string>());
}
