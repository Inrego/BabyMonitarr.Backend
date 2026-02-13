using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;
using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Hubs;

public class AudioStreamHub : Hub
{
    private readonly ILogger<AudioStreamHub> _logger;
    private readonly IAudioWebRtcService _audioWebRtcService;
    private readonly IAudioStreamingService _audioStreamingService;
    private readonly IRoomService _roomService;
    private readonly IVideoWebRtcService _videoWebRtcService;
    private readonly IVideoStreamingService _videoStreamingService;
    private readonly IGoogleNestAuthService _nestAuthService;
    private readonly IGoogleNestDeviceService _nestDeviceService;

    public AudioStreamHub(
        ILogger<AudioStreamHub> logger,
        IAudioWebRtcService audioWebRtcService,
        IAudioStreamingService audioStreamingService,
        IRoomService roomService,
        IVideoWebRtcService videoWebRtcService,
        IVideoStreamingService videoStreamingService,
        IGoogleNestAuthService nestAuthService,
        IGoogleNestDeviceService nestDeviceService)
    {
        _logger = logger;
        _audioWebRtcService = audioWebRtcService;
        _audioStreamingService = audioStreamingService;
        _roomService = roomService;
        _videoWebRtcService = videoWebRtcService;
        _videoStreamingService = videoStreamingService;
        _nestAuthService = nestAuthService;
        _nestDeviceService = nestDeviceService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);

        // Close all audio peer connections for this client
        await _audioWebRtcService.CloseAllAudioPeerConnections(Context.ConnectionId);

        // Close all video peer connections for this client
        await _videoWebRtcService.CloseAllVideoPeerConnections(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    #region Audio WebRTC Signaling Methods
    public async Task<string> StartAudioStream(int roomId)
    {
        _logger.LogInformation("Client {ConnectionId} requested audio stream for room {RoomId}",
            Context.ConnectionId, roomId);

        var offerSdp = await _audioWebRtcService.CreateAudioPeerConnection(Context.ConnectionId, roomId);
        return offerSdp;
    }

    public async Task SetAudioRemoteDescription(int roomId, string type, string sdp)
    {
        _logger.LogInformation("Client {ConnectionId} sent audio SDP answer for room {RoomId}",
            Context.ConnectionId, roomId);

        var description = new RTCSessionDescriptionInit
        {
            type = type == "answer" ? RTCSdpType.answer : RTCSdpType.offer,
            sdp = sdp
        };

        await _audioWebRtcService.SetAudioRemoteDescription(Context.ConnectionId, roomId, description);
    }

    public async Task AddAudioIceCandidate(int roomId, string candidate, string sdpMid, int? sdpMLineIndex)
    {
        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };

        await _audioWebRtcService.AddAudioIceCandidate(Context.ConnectionId, roomId, iceCandidate);
    }

    public async Task StopAudioStream(int roomId)
    {
        _logger.LogInformation("Client {ConnectionId} requested to stop audio stream for room {RoomId}",
            Context.ConnectionId, roomId);
        await _audioWebRtcService.CloseAudioPeerConnection(Context.ConnectionId, roomId);
    }
    #endregion

    #region Video WebRTC Signaling Methods
    public async Task<string> StartVideoStream(int roomId)
    {
        _logger.LogInformation("Client {ConnectionId} requested video stream for room {RoomId}",
            Context.ConnectionId, roomId);

        var offerSdp = await _videoWebRtcService.CreateVideoPeerConnection(Context.ConnectionId, roomId);
        return offerSdp;
    }

    public async Task SetVideoRemoteDescription(int roomId, string type, string sdp)
    {
        _logger.LogInformation("Client {ConnectionId} sent video SDP answer for room {RoomId}",
            Context.ConnectionId, roomId);

        var description = new RTCSessionDescriptionInit
        {
            type = type == "answer" ? RTCSdpType.answer : RTCSdpType.offer,
            sdp = sdp
        };

        await _videoWebRtcService.SetVideoRemoteDescription(Context.ConnectionId, roomId, description);
    }

    public async Task AddVideoIceCandidate(int roomId, string candidate, string sdpMid, int? sdpMLineIndex)
    {
        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };

        await _videoWebRtcService.AddVideoIceCandidate(Context.ConnectionId, roomId, iceCandidate);
    }

    public async Task StopVideoStream(int roomId)
    {
        _logger.LogInformation("Client {ConnectionId} requested to stop video stream for room {RoomId}",
            Context.ConnectionId, roomId);
        await _videoWebRtcService.CloseVideoPeerConnection(Context.ConnectionId, roomId);
    }
    #endregion

    #region Room Management
    public async Task<List<Room>> GetRooms()
    {
        return await _roomService.GetAllRoomsAsync();
    }

    public async Task<Room> CreateRoom(Room room)
    {
        var created = await _roomService.CreateRoomAsync(room);
        _videoStreamingService.RefreshRooms();
        _audioStreamingService.RefreshRooms();
        await Clients.Others.SendAsync("RoomsUpdated");
        return created;
    }

    public async Task<Room?> UpdateRoom(Room room)
    {
        var updated = await _roomService.UpdateRoomAsync(room);
        if (updated != null)
        {
            _videoStreamingService.RefreshRooms();
            _audioStreamingService.RefreshRooms();
            await Clients.Others.SendAsync("RoomsUpdated");
        }
        return updated;
    }

    public async Task<bool> DeleteRoom(int id)
    {
        var result = await _roomService.DeleteRoomAsync(id);
        if (result)
        {
            _videoStreamingService.RefreshRooms();
            _audioStreamingService.RefreshRooms();
            await Clients.Others.SendAsync("RoomsUpdated");
        }
        return result;
    }

    public async Task<Room?> SelectRoom(int roomId)
    {
        var room = await _roomService.SetActiveRoomAsync(roomId);
        if (room != null)
        {
            await Clients.All.SendAsync("ActiveRoomChanged", room);
        }
        return room;
    }

    public async Task<Room?> GetActiveRoom()
    {
        return await _roomService.GetActiveRoomAsync();
    }
    #endregion

    #region Settings
    public async Task<AudioSettings> GetAudioSettings()
    {
        return await _roomService.GetComposedAudioSettingsAsync();
    }

    public async Task<GlobalSettings> GetGlobalSettings()
    {
        return await _roomService.GetGlobalSettingsAsync();
    }

    public async Task UpdateAudioSettings(GlobalSettings settings)
    {
        _logger.LogInformation("Client {ConnectionId} updated audio settings", Context.ConnectionId);
        await _roomService.UpdateGlobalSettingsAsync(settings);

        // Refresh audio streaming service so processors pick up new settings
        _audioStreamingService.RefreshRooms();

        await Clients.Others.SendAsync("SettingsUpdated");
    }
    #endregion

    #region Google Nest
    public async Task<GoogleNestSettings> GetNestSettings()
    {
        return await _nestAuthService.GetSettings();
    }

    public async Task UpdateNestSettings(GoogleNestSettings settings)
    {
        await _nestAuthService.UpdateSettings(settings);
    }

    public async Task<string> GetNestAuthUrl()
    {
        var redirectUri = Context.GetHttpContext()?.Request is { } req
            ? $"{req.Scheme}://{req.Host}/nest/auth/callback"
            : "/nest/auth/callback";
        return await _nestAuthService.GetAuthorizationUrl(redirectUri);
    }

    public async Task<List<NestDevice>> GetNestDevices()
    {
        return await _nestDeviceService.ListDevicesAsync();
    }

    public async Task<bool> IsNestLinked()
    {
        return await _nestAuthService.IsLinked();
    }
    #endregion
}
