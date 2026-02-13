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
    private readonly IAudioProcessingService _audioService;
    private readonly IWebRtcService _webRtcService;
    private readonly IRoomService _roomService;

    public AudioStreamHub(
        ILogger<AudioStreamHub> logger,
        IAudioProcessingService audioService,
        IWebRtcService webRtcService,
        IRoomService roomService)
    {
        _logger = logger;
        _audioService = audioService;
        _webRtcService = webRtcService;
        _roomService = roomService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");

        // Close any WebRTC peer connection for this client
        await _webRtcService.ClosePeerConnection(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    #region WebRTC Signaling Methods
    public async Task<string> StartWebRtcStream()
    {
        _logger.LogInformation($"Client {Context.ConnectionId} requested to start WebRTC stream");

        // Create a peer connection for this client
        await _webRtcService.CreatePeerConnection(Context.ConnectionId);

        // Create an offer and return the SDP
        var offerSdp = await _webRtcService.CreateOffer(Context.ConnectionId);
        return offerSdp;
    }

    public async Task SetRemoteDescription(string type, string sdp)
    {
        _logger.LogInformation($"Client {Context.ConnectionId} sent SDP answer");

        var description = new RTCSessionDescriptionInit
        {
            type = type == "answer" ? RTCSdpType.answer : RTCSdpType.offer,
            sdp = sdp
        };

        await _webRtcService.SetRemoteDescription(Context.ConnectionId, description);
    }

    public async Task AddIceCandidate(string candidate, string sdpMid, int? sdpMLineIndex)
    {
        _logger.LogInformation($"Client {Context.ConnectionId} sent ICE candidate");

        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };

        await _webRtcService.AddIceCandidate(Context.ConnectionId, iceCandidate);
    }

    public async Task StopWebRtcStream()
    {
        _logger.LogInformation($"Client {Context.ConnectionId} requested to stop WebRTC stream");
        await _webRtcService.ClosePeerConnection(Context.ConnectionId);
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
        await Clients.Others.SendAsync("RoomsUpdated");
        return created;
    }

    public async Task<Room?> UpdateRoom(Room room)
    {
        var updated = await _roomService.UpdateRoomAsync(room);
        if (updated != null)
        {
            await Clients.Others.SendAsync("RoomsUpdated");

            // If the updated room is the active one, refresh audio settings
            if (updated.IsActive)
            {
                var settings = await _roomService.GetComposedAudioSettingsAsync();
                _audioService.UpdateSettings(settings);
            }
        }
        return updated;
    }

    public async Task<bool> DeleteRoom(int id)
    {
        var result = await _roomService.DeleteRoomAsync(id);
        if (result)
        {
            await Clients.Others.SendAsync("RoomsUpdated");
        }
        return result;
    }

    public async Task<Room?> SelectRoom(int roomId)
    {
        var room = await _roomService.SetActiveRoomAsync(roomId);
        if (room != null)
        {
            // Update audio service with new room's settings
            var settings = await _roomService.GetComposedAudioSettingsAsync();
            _audioService.UpdateSettings(settings);

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
        _logger.LogInformation($"Client {Context.ConnectionId} updated audio settings");
        await _roomService.UpdateGlobalSettingsAsync(settings);

        // Update the running audio service
        var composed = await _roomService.GetComposedAudioSettingsAsync();
        _audioService.UpdateSettings(composed);

        await Clients.Others.SendAsync("SettingsUpdated");
    }
    #endregion
}
