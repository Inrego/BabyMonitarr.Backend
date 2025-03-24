using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Hubs;

public class AudioStreamHub : Hub
{
    private readonly ILogger<AudioStreamHub> _logger;
    private readonly IAudioProcessingService _audioService;

    public AudioStreamHub(ILogger<AudioStreamHub> logger, IAudioProcessingService audioService)
    {
        _logger = logger;
        _audioService = audioService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

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