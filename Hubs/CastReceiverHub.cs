using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Hubs;

/// <summary>
/// WebRTC signalling for the custom cast receiver page (wwwroot/cast/receiver.html).
/// </summary>
/// <remarks>
/// Anonymous for the same reason as the HLS endpoints: a cast device has no cookie or API key.
/// Every call is gated by the 128-bit token the server hands the receiver over the cast channel,
/// which lives only as long as the cast session and only ever unlocks that one room's stream.
/// Nothing else of the application - rooms, settings, camera URLs - is reachable from here.
/// </remarks>
[AllowAnonymous]
public class CastReceiverHub : Hub
{
    private const string TokenKey = "castReceiverToken";

    private readonly ILogger<CastReceiverHub> _logger;
    private readonly ICastSessionService _castSessionService;
    private readonly IVideoWebRtcService _videoWebRtcService;
    private readonly IAudioWebRtcService _audioWebRtcService;
    private readonly IWebRtcConfigService _webRtcConfigService;
    private readonly CastReceiverPeers _peers;

    public CastReceiverHub(
        ILogger<CastReceiverHub> logger,
        ICastSessionService castSessionService,
        IVideoWebRtcService videoWebRtcService,
        IAudioWebRtcService audioWebRtcService,
        IWebRtcConfigService webRtcConfigService,
        CastReceiverPeers peers)
    {
        _logger = logger;
        _castSessionService = castSessionService;
        _videoWebRtcService = videoWebRtcService;
        _audioWebRtcService = audioWebRtcService;
        _webRtcConfigService = webRtcConfigService;
        _peers = peers;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _peers.Unregister(Context.ConnectionId);
        await _videoWebRtcService.CloseAllVideoPeerConnections(Context.ConnectionId);
        await _audioWebRtcService.CloseAllAudioPeerConnections(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public CastReceiverJoinResult Join(string token)
    {
        var ticket = _castSessionService.ResolveReceiverToken(token)
            ?? throw new HubException("This cast session has ended.");

        Context.Items[TokenKey] = token;
        _peers.Register(Context.ConnectionId);
        _logger.LogInformation(
            "Cast receiver {ConnectionId} joined room {RoomId}", Context.ConnectionId, ticket.RoomId);

        return new CastReceiverJoinResult
        {
            RoomName = ticket.RoomName,
            Video = ticket.Video,
            Audio = ticket.Audio,
            IceServers = _webRtcConfigService.GetClientConfig().IceServers
        };
    }

    /// <summary>Creates the server peer for <paramref name="kind"/> ("video" or "audio") and returns its offer.</summary>
    public Task<string> StartStream(string kind)
    {
        var ticket = RequireTicket(kind);
        string? hostHint = Context.GetHttpContext()?.Request.Host.Host;
        return IsVideo(kind)
            ? _videoWebRtcService.CreateVideoPeerConnection(Context.ConnectionId, ticket.RoomId, hostHint)
            : _audioWebRtcService.CreateAudioPeerConnection(Context.ConnectionId, ticket.RoomId, hostHint);
    }

    public Task SetAnswer(string kind, string sdp)
    {
        var ticket = RequireTicket(kind);
        var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp };
        return IsVideo(kind)
            ? _videoWebRtcService.SetVideoRemoteDescription(Context.ConnectionId, ticket.RoomId, answer)
            : _audioWebRtcService.SetAudioRemoteDescription(Context.ConnectionId, ticket.RoomId, answer);
    }

    public Task AddIceCandidate(string kind, string candidate, string sdpMid, int? sdpMLineIndex)
    {
        var ticket = RequireTicket(kind);
        var init = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };
        return IsVideo(kind)
            ? _videoWebRtcService.AddVideoIceCandidate(Context.ConnectionId, ticket.RoomId, init)
            : _audioWebRtcService.AddAudioIceCandidate(Context.ConnectionId, ticket.RoomId, init);
    }

    /// <summary>
    /// Re-checks the token on every call, so a receiver left running after its session ended
    /// cannot keep opening streams.
    /// </summary>
    private CastReceiverTicket RequireTicket(string kind)
    {
        var ticket = Context.Items.TryGetValue(TokenKey, out var token) && token is string value
            ? _castSessionService.ResolveReceiverToken(value)
            : null;
        if (ticket == null) throw new HubException("This cast session has ended.");

        bool allowed = IsVideo(kind) ? ticket.Video : kind == "audio" && ticket.Audio;
        if (!allowed) throw new HubException($"The '{kind}' stream is not part of this cast.");
        return ticket;
    }

    private static bool IsVideo(string kind) => kind == "video";
}
