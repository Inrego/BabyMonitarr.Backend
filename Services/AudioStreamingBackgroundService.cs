using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Services;

public class AudioStreamingBackgroundService : BackgroundService
{
    private readonly ILogger<AudioStreamingBackgroundService> _logger;
    private readonly IAudioProcessingService _audioService;
    private readonly IWebRtcService _webRtcService;
    private readonly IServiceScopeFactory _scopeFactory;

    public AudioStreamingBackgroundService(
        ILogger<AudioStreamingBackgroundService> logger,
        IAudioProcessingService audioService,
        IWebRtcService webRtcService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _audioService = audioService;
        _webRtcService = webRtcService;
        _scopeFactory = scopeFactory;

        // Subscribe to events
        _audioService.AudioSampleProcessed += OnAudioSampleProcessed;
        _audioService.SoundThresholdExceeded += OnSoundThresholdExceeded;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audio streaming background service starting");

        try
        {
            // Load settings from DB before starting capture
            using (var scope = _scopeFactory.CreateScope())
            {
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                var settings = await roomService.GetComposedAudioSettingsAsync();
                _audioService.UpdateSettings(settings);
            }

            await _audioService.StartAudioCapture(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio streaming background service");
        }
        finally
        {
            _logger.LogInformation("Audio streaming background service stopping");
            await _audioService.StopAudioCapture();
        }
    }

    private void OnAudioSampleProcessed(object? sender, AudioSampleEventArgs e)
    {
        try
        {
            // Send audio data to WebRTC peers
            _webRtcService.SendAudioData(e.AudioData);

            // Send audio level updates via WebRTC data channel
            _webRtcService.SendAudioLevel(e.AudioLevel, e.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending audio sample to clients");
        }
    }

    private void OnSoundThresholdExceeded(object? sender, SoundThresholdEventArgs e)
    {
        try
        {
            _logger.LogInformation($"Sound threshold exceeded: {e.AudioLevel:F2} dB (threshold: {e.Threshold:F2} dB)");

            // Send notification via WebRTC data channel
            _webRtcService.SendSoundAlert(e.AudioLevel, e.Threshold, e.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending sound threshold alert to clients");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _audioService.AudioSampleProcessed -= OnAudioSampleProcessed;
        _audioService.SoundThresholdExceeded -= OnSoundThresholdExceeded;

        await base.StopAsync(cancellationToken);
    }
}
