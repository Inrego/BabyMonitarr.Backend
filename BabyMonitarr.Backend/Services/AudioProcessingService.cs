using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.IO;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services;

public interface IAudioProcessingService
{
    Task StartAudioCapture(CancellationToken cancellationToken);
    Task StopAudioCapture();
    event EventHandler<AudioSampleEventArgs> AudioSampleProcessed;
    event EventHandler<SoundThresholdEventArgs> SoundThresholdExceeded;
    AudioSettings GetSettings();
    void UpdateSettings(AudioSettings settings);
}

public class AudioSampleEventArgs : EventArgs
{
    public byte[] AudioData { get; set; } = Array.Empty<byte>();
    public double AudioLevel { get; set; }
    public DateTime Timestamp { get; set; }
}

public class SoundThresholdEventArgs : EventArgs
{
    public double AudioLevel { get; set; }
    public double Threshold { get; set; }
    public DateTime Timestamp { get; set; }
}

public class AudioProcessingService : IAudioProcessingService, IDisposable
{
    private readonly ILogger<AudioProcessingService> _logger;
    private AudioSettings _settings;
    private WaveInEvent? _waveIn;
    private ConcurrentQueue<float> _audioLevelQueue = new ConcurrentQueue<float>();
    private bool _isCapturing;
    private BiQuadFilter? _lowPassFilter;
    private BiQuadFilter? _highPassFilter;
    private CancellationTokenSource? _cameraStreamCts;
    private DateTime _lastThresholdExceededTime = DateTime.MinValue;
    
    private const float REFERENCE_LEVEL = 1.0f;  // Reference level for dB calculation
    private const float DB_FLOOR = -90.0f;       // Minimum dB level
    
    public event EventHandler<AudioSampleEventArgs>? AudioSampleProcessed;
    public event EventHandler<SoundThresholdEventArgs>? SoundThresholdExceeded;

    public AudioProcessingService(ILogger<AudioProcessingService> logger, IOptions<AudioSettings> settings)
    {
        _logger = logger;
        _settings = settings?.Value ?? new AudioSettings();
        InitializeFilters();
    }

    public AudioSettings GetSettings()
    {
        return _settings;
    }

    public void UpdateSettings(AudioSettings settings)
    {
        bool streamSourceChanged = _settings.UseCameraAudioStream != settings.UseCameraAudioStream ||
                                 _settings.CameraStreamUrl != settings.CameraStreamUrl;
                                 
        _settings = settings;
        InitializeFilters();
        
        // If stream source has changed and we're capturing, restart the capture
        if (streamSourceChanged && _isCapturing)
        {
            _logger.LogInformation("Audio source changed. Restarting audio capture.");
            var isCapturing = _isCapturing;
            StopAudioCapture().Wait();
            
            if (isCapturing)
            {
                StartAudioCapture(CancellationToken.None).Wait();
            }
        }
    }

    private void InitializeFilters()
    {
        // Sample rate typically used for audio (CD quality)
        int sampleRate = 44100;
        
        // Create low pass filter (allows frequencies below cutoff)
        _lowPassFilter = BiQuadFilter.LowPassFilter(sampleRate, _settings.LowPassFrequency, 1.0f);
        
        // Create high pass filter (allows frequencies above cutoff)
        _highPassFilter = BiQuadFilter.HighPassFilter(sampleRate, _settings.HighPassFrequency, 1.0f);
    }

    public async Task StartAudioCapture(CancellationToken cancellationToken)
    {
        if (_isCapturing)
            return;

        _logger.LogInformation("Starting audio capture");
        _isCapturing = true;

        try
        {
            if (_settings.UseCameraAudioStream && !string.IsNullOrEmpty(_settings.CameraStreamUrl))
            {
                await StartCameraAudioCapture(cancellationToken);
            }
            else
            {
                await StartMicrophoneCapture(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _isCapturing = false;
            _logger.LogError(ex, "Error starting audio capture");
            throw;
        }
    }

    private async Task StartMicrophoneCapture(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Using local microphone for audio capture");
        
        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0, // Default device
                WaveFormat = new WaveFormat(44100, 1), // 44.1kHz, mono
                BufferMilliseconds = 50 // Short buffer for low latency
            };

            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;
            _waveIn.StartRecording();

            // Keep the task running until cancellation is requested
            await Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested && _isCapturing)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting microphone audio capture");
            throw;
        }
    }

    private async Task StartCameraAudioCapture(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Using camera stream for audio capture: {0}", _settings.CameraStreamUrl);
        
        _cameraStreamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        try
        {
            await Task.Run(async () =>
            {
                _logger.LogInformation("Connecting to camera audio stream...");
                
                // Use the real RTSP stream reader instead of the mock
                var rtspReader = new RtspAudioReader(_settings, _logger);
                rtspReader.AudioDataReceived += OnCameraAudioDataReceived;
                
                await rtspReader.StartAsync(_cameraStreamCts.Token);
                
                while (!_cameraStreamCts.Token.IsCancellationRequested && _isCapturing)
                {
                    await Task.Delay(100, _cameraStreamCts.Token);
                }
                
                rtspReader.AudioDataReceived -= OnCameraAudioDataReceived;
                rtspReader.Dispose();
                
            }, _cameraStreamCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing camera audio stream");
            throw;
        }
    }

    private void OnCameraAudioDataReceived(object? sender, AudioFormatEventArgs e)
    {
        if (e.AudioData == null || e.AudioData.Length == 0)
            return;
            
        try
        {
            // Use the format information provided by RtspAudioReader
            _logger.LogTrace($"Received audio: format={e.SampleFormat}, " +
                           $"bytes/sample={e.BytesPerSample}, " +
                           $"channels={e.Channels}, " +
                           $"rate={e.SampleRate}Hz, " +
                           $"size={e.AudioData.Length} bytes");
            
            // Convert bytes to samples using the correct format information
            float[] samples = ConvertBytesToSamples(e.AudioData, e.AudioData.Length, e.BytesPerSample, e.SampleFormat);
            
            // Process audio samples with the correct information
            ProcessAudioSamples(samples, e.AudioData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing camera audio data");
        }
    }

    public Task StopAudioCapture()
    {
        if (!_isCapturing)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping audio capture");
        
        // Stop microphone capture if active
        _waveIn?.StopRecording();
        
        // Stop camera stream capture if active
        if (_cameraStreamCts != null && !_cameraStreamCts.IsCancellationRequested)
        {
            _cameraStreamCts.Cancel();
            _cameraStreamCts.Dispose();
            _cameraStreamCts = null;
        }
        
        _isCapturing = false;
        return Task.CompletedTask;
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        try
        {
            // Convert bytes to samples - microphone data is always 16-bit PCM
            float[] samples = ConvertBytesToSamples(e.Buffer, e.BytesRecorded, 2, AVSampleFormat.AV_SAMPLE_FMT_S16);
            ProcessAudioSamples(samples, e.Buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio data");
        }
    }

    private void ProcessAudioSamples(float[] samples, byte[] originalBuffer)
    {
        // Apply filtering if enabled
        if (_settings.FilterEnabled && _lowPassFilter != null && _highPassFilter != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = _highPassFilter.Transform(_lowPassFilter.Transform(samples[i]));
            }
        }

        // Calculate RMS (Root Mean Square) level
        float rms = CalculateRmsLevel(samples);

        // Convert to decibels with proper scaling
        double dbLevel;
        if (rms > 0)
        {
            dbLevel = 20 * Math.Log10(rms / REFERENCE_LEVEL);
            // Clamp to minimum dB level
            dbLevel = Math.Max(dbLevel, DB_FLOOR);
        }
        else
        {
            dbLevel = DB_FLOOR;
        }
        
        // Add to the queue for averaging
        _audioLevelQueue.Enqueue((float)dbLevel);
        while (_audioLevelQueue.Count > _settings.AverageSampleCount)
        {
            _audioLevelQueue.TryDequeue(out _);
        }
        
        // Calculate average level
        double averageLevel = CalculateAverageLevel();

        // Get current time
        DateTime now = DateTime.UtcNow;

        // Check if sound exceeds threshold and we're not in the pause period
        if (averageLevel > _settings.SoundThreshold && 
            (now - _lastThresholdExceededTime).TotalSeconds > _settings.ThresholdPauseDuration)
        {
            _lastThresholdExceededTime = now;
            _logger.LogInformation($"Sound threshold exceeded: {averageLevel:F2} dB. Pausing threshold checks for {_settings.ThresholdPauseDuration} seconds");
            
            SoundThresholdExceeded?.Invoke(this, new SoundThresholdEventArgs
            {
                AudioLevel = averageLevel,
                Threshold = _settings.SoundThreshold,
                Timestamp = now
            });
        }

        // Send audio data to clients
        AudioSampleProcessed?.Invoke(this, new AudioSampleEventArgs
        {
            AudioData = originalBuffer,
            AudioLevel = averageLevel,
            Timestamp = now
        });
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        _waveIn?.Dispose();
        _waveIn = null;
        _logger.LogInformation("Audio recording stopped");
        
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Error in audio recording");
        }
    }

    private float[] ConvertBytesToSamples(byte[] buffer, int bytesRecorded)
    {
        // Default to 16-bit PCM sample format (old implementation for backward compatibility)
        return ConvertBytesToSamples(buffer, bytesRecorded, 2, AVSampleFormat.AV_SAMPLE_FMT_S16);
    }
    
    private float[] ConvertBytesToSamples(byte[] buffer, int bytesRecorded, int bytesPerSample, AVSampleFormat sampleFormat)
    {
        int sampleCount = bytesRecorded / bytesPerSample;
        float[] samples = new float[sampleCount];

        // Process based on audio format
        switch (sampleFormat)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLT: // 32-bit float
                ProcessFloatSamples(buffer, samples);
                break;
                
            case AVSampleFormat.AV_SAMPLE_FMT_DBL: // 64-bit double
                ProcessDoubleSamples(buffer, samples);
                break;
                
            case AVSampleFormat.AV_SAMPLE_FMT_S16: // 16-bit signed integer
            case AVSampleFormat.AV_SAMPLE_FMT_S16P: // 16-bit signed integer (planar)
                Process16BitSamples(buffer, samples, bytesPerSample);
                break;
                
            case AVSampleFormat.AV_SAMPLE_FMT_S32: // 32-bit signed integer
            case AVSampleFormat.AV_SAMPLE_FMT_S32P: // 32-bit signed integer (planar)
                Process32BitSamples(buffer, samples);
                break;
                
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP: // 32-bit float (planar)
                ProcessFloatPlanarSamples(buffer, samples);
                break;
                
            case AVSampleFormat.AV_SAMPLE_FMT_DBLP: // 64-bit double (planar)
                ProcessDoublePlanarSamples(buffer, samples);
                break;
                
            // For any other format, fallback to 16-bit PCM
            default:
                _logger.LogWarning($"Unsupported audio format {sampleFormat}, falling back to 16-bit PCM");
                Process16BitSamples(buffer, samples, bytesPerSample);
                break;
        }

        return samples;
    }
    
    private void Process16BitSamples(byte[] buffer, float[] samples, int bytesPerSample)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)((buffer[i * bytesPerSample + 1] << 8) | buffer[i * bytesPerSample]);
            samples[i] = sample / 32768f; // Normalize to -1.0 to 1.0
        }
    }
    
    private void Process32BitSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int sample = BitConverter.ToInt32(buffer, i * 4);
            samples[i] = sample / (float)int.MaxValue; // Normalize to -1.0 to 1.0
        }
    }
    
    private void ProcessFloatSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToSingle(buffer, i * 4);
            
            // Ensure samples are properly normalized
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }
    
    private void ProcessDoubleSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = BitConverter.ToDouble(buffer, i * 8);
            samples[i] = (float)sample; // Convert to float
            
            // Ensure samples are properly normalized
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }
    
    private void ProcessFloatPlanarSamples(byte[] buffer, float[] samples)
    {
        // This is a simplified implementation for mono channel
        // For multi-channel planar formats, you'd need additional processing
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToSingle(buffer, i * 4);
            
            // Ensure samples are properly normalized
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }
    
    private void ProcessDoublePlanarSamples(byte[] buffer, float[] samples)
    {
        // This is a simplified implementation for mono channel
        // For multi-channel planar formats, you'd need additional processing
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = BitConverter.ToDouble(buffer, i * 8);
            samples[i] = (float)sample; // Convert to float
            
            // Ensure samples are properly normalized
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }

    private float CalculateRmsLevel(float[] samples)
    {
        if (samples == null || samples.Length == 0)
            return 0;

        double sum = 0;
        int validSamples = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            // Skip invalid samples
            if (float.IsNaN(samples[i]) || float.IsInfinity(samples[i]))
                continue;

            sum += samples[i] * samples[i]; // Square each sample
            validSamples++;
        }
        
        if (validSamples == 0)
            return 0;

        return (float)Math.Sqrt(sum / validSamples);
    }
    
    private double CalculateAverageLevel()
    {
        if (_audioLevelQueue.IsEmpty)
            return -100.0; // Very quiet when no samples
                
        double sum = 0;
        int count = 0;
        
        foreach (var level in _audioLevelQueue)
        {
            sum += level;
            count++;
        }
        
        return sum / count;
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _cameraStreamCts?.Dispose();
    }
}