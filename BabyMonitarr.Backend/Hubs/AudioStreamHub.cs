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

    public AudioStreamHub(
        ILogger<AudioStreamHub> logger, 
        IAudioProcessingService audioService,
        IWebRtcService webRtcService)
    {
        _logger = logger;
        _audioService = audioService;
        _webRtcService = webRtcService;
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

    #region SignalR Streaming Methods
    public async Task StartStream()
    {
        _logger.LogInformation($"Client {Context.ConnectionId} requested to start audio stream");
        await Groups.AddToGroupAsync(Context.ConnectionId, "AudioStreamListeners");
    }

    public async Task StopStream()
    {
        _logger.LogInformation($"Client {Context.ConnectionId} requested to stop audio stream");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "AudioStreamListeners");
    }
    #endregion

    #region WebRTC Signaling Methods
    public async Task<string> StartWebRtcStream()
    {
        _logger.LogInformation($"Client {Context.ConnectionId} requested to start WebRTC stream");

        // Add client to AudioLevelListeners group for audio level updates
        await Groups.AddToGroupAsync(Context.ConnectionId, "AudioLevelListeners");

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

        // Remove client from AudioLevelListeners group
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "AudioLevelListeners");

        await _webRtcService.ClosePeerConnection(Context.ConnectionId);
    }
    #endregion

    public AudioSettings GetAudioSettings()
    {
        return _audioService.GetSettings();
    }

    public void UpdateAudioSettings(AudioSettings settings)
    {
        _logger.LogInformation($"Client {Context.ConnectionId} updated audio settings");
        _audioService.UpdateSettings(settings);
    }
}