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
    private readonly IWebRtcConfigService _webRtcConfigService;
    private readonly ICastDeviceService _castDeviceService;
    private readonly ICastSessionService _castSessionService;

    public AudioStreamHub(
        ILogger<AudioStreamHub> logger,
        IAudioWebRtcService audioWebRtcService,
        IAudioStreamingService audioStreamingService,
        IRoomService roomService,
        IVideoWebRtcService videoWebRtcService,
        IVideoStreamingService videoStreamingService,
        IGoogleNestAuthService nestAuthService,
        IGoogleNestDeviceService nestDeviceService,
        IWebRtcConfigService webRtcConfigService,
        ICastDeviceService castDeviceService,
        ICastSessionService castSessionService)
    {
        _logger = logger;
        _audioWebRtcService = audioWebRtcService;
        _audioStreamingService = audioStreamingService;
        _roomService = roomService;
        _videoWebRtcService = videoWebRtcService;
        _videoStreamingService = videoStreamingService;
        _nestAuthService = nestAuthService;
        _nestDeviceService = nestDeviceService;
        _webRtcConfigService = webRtcConfigService;
        _castDeviceService = castDeviceService;
        _castSessionService = castSessionService;
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

        var offerSdp = await _audioWebRtcService.CreateAudioPeerConnection(
            Context.ConnectionId,
            roomId,
            GetRequestHostHint());
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

        var offerSdp = await _videoWebRtcService.CreateVideoPeerConnection(
            Context.ConnectionId,
            roomId,
            GetRequestHostHint());
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

    #region WebRTC Config
    public WebRtcClientConfig GetWebRtcConfig()
    {
        return _webRtcConfigService.GetClientConfig();
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
            await _castSessionService.StopRoomAsync(id);
            await _castDeviceService.SetRoomTargetsAsync(id, Array.Empty<string>());
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

    #region Google Cast
    /// <summary>Known cast receivers, with the room each one is currently playing.</summary>
    public async Task<List<CastDeviceInfo>> GetCastDevices()
    {
        var devices = await _castDeviceService.GetDevicesAsync();
        return Decorate(devices);
    }

    /// <summary>Runs an mDNS sweep now instead of waiting for the next scheduled one.</summary>
    public async Task<List<CastDeviceInfo>> RefreshCastDevices()
    {
        var devices = await _castDeviceService.RefreshAsync();
        await Clients.Others.SendAsync("CastStateChanged");
        return Decorate(devices);
    }

    /// <summary>Adds a receiver by IP for networks where mDNS does not reach the server.</summary>
    public async Task<CastDeviceInfo> AddCastDevice(string host, int port, string? name, bool isVideoCapable)
    {
        var device = await _castDeviceService.AddManualAsync(host, port, name, isVideoCapable);
        await Clients.All.SendAsync("CastStateChanged");
        return device;
    }

    public async Task<bool> ForgetCastDevice(string deviceId)
    {
        var removed = await _castDeviceService.ForgetAsync(deviceId);
        if (removed)
        {
            await _castSessionService.StopDeviceAsync(deviceId);
            await Clients.All.SendAsync("CastStateChanged");
        }
        return removed;
    }

    public async Task<List<string>> GetRoomCastTargets(int roomId)
    {
        var targets = await _castDeviceService.GetRoomTargetsAsync(roomId);
        return targets.ToList();
    }

    /// <summary>Saves the default target selection for a room. Does not start or stop casting.</summary>
    public async Task SetRoomCastTargets(int roomId, List<string> deviceIds)
    {
        await _castDeviceService.SetRoomTargetsAsync(roomId, deviceIds ?? new List<string>());
        await Clients.Others.SendAsync("CastStateChanged");
    }

    /// <summary>Starts casting a room. Passing no device ids uses the room's saved targets.</summary>
    public async Task<CastStartResult> StartCast(int roomId, List<string>? deviceIds)
    {
        var targets = deviceIds is { Count: > 0 }
            ? deviceIds
            : (await _castDeviceService.GetRoomTargetsAsync(roomId)).ToList();

        if (targets.Count == 0)
        {
            return new CastStartResult();
        }

        var request = Context.GetHttpContext()?.Request;
        string? requestBaseUrl = request == null ? null : $"{request.Scheme}://{request.Host}";

        return await _castSessionService.StartAsync(roomId, targets, requestBaseUrl);
    }

    /// <summary>Stops casting a room everywhere, or only on the given devices.</summary>
    public async Task<int> StopCast(int roomId, List<string>? deviceIds)
    {
        if (deviceIds is not { Count: > 0 })
        {
            return await _castSessionService.StopRoomAsync(roomId);
        }

        int stopped = 0;
        foreach (var deviceId in deviceIds)
        {
            if (await _castSessionService.StopDeviceAsync(deviceId)) stopped++;
        }
        return stopped;
    }

    public List<CastSessionInfo> GetCastSessions()
    {
        return _castSessionService.GetSessions().ToList();
    }

    private List<CastDeviceInfo> Decorate(IReadOnlyList<CastDeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            device.CastingRoomId = _castSessionService.RoomForDevice(device.DeviceId);
            device.LastError = _castSessionService.LastErrorForDevice(device.DeviceId);
        }
        return devices.ToList();
    }
    #endregion

    private string? GetRequestHostHint()
    {
        var host = Context.GetHttpContext()?.Request.Host.Host;
        return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }
}
