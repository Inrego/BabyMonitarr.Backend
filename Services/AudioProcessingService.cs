using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using NAudio.Dsp;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services;

public class AudioSampleEventArgs : EventArgs
{
    public byte[] AudioData { get; set; } = Array.Empty<byte>();
    public double AudioLevel { get; set; }
    public int SampleRate { get; set; }
    public DateTime Timestamp { get; set; }
    public byte[]? RawOpusData { get; set; }
    public uint DurationRtpUnits { get; set; }
}

public class SoundThresholdEventArgs : EventArgs
{
    public double AudioLevel { get; set; }
    public double Threshold { get; set; }
    public int RoomId { get; set; }
    public DateTime Timestamp { get; set; }
}

public class AudioProcessingService : IDisposable
{
    private readonly ILogger _logger;
    private AudioSettings _settings;
    private ConcurrentQueue<float> _audioLevelQueue = new ConcurrentQueue<float>();
    private BiQuadFilter? _lowPassFilter;
    private BiQuadFilter? _highPassFilter;
    private int _sampleRate;
    private DateTime _lastThresholdExceededTime = DateTime.MinValue;

    private const float REFERENCE_LEVEL = 1.0f;
    private const float DB_FLOOR = -90.0f;

    public int RoomId { get; }

    public event EventHandler<AudioSampleEventArgs>? AudioSampleProcessed;
    public event EventHandler<SoundThresholdEventArgs>? SoundThresholdExceeded;

    public AudioProcessingService(int roomId, AudioSettings settings, ILogger logger)
    {
        RoomId = roomId;
        _logger = logger;
        _settings = settings;
        InitializeFilters();
    }

    public AudioSettings GetSettings()
    {
        return _settings;
    }

    public void UpdateSettings(AudioSettings settings)
    {
        _settings = settings;
        InitializeFilters();
    }

    private void InitializeFilters()
    {
        int sampleRate = _sampleRate > 0 ? _sampleRate : 44100;
        _lowPassFilter = BiQuadFilter.LowPassFilter(sampleRate, _settings.LowPassFrequency, 1.0f);
        _highPassFilter = BiQuadFilter.HighPassFilter(sampleRate, _settings.HighPassFrequency, 1.0f);
    }

    public void OnAudioDataReceived(object? sender, AudioFormatEventArgs e)
    {
        if (e.AudioData == null || e.AudioData.Length == 0)
            return;

        try
        {
            _logger.LogTrace("Received audio for room {RoomId}: format={Format}, bytes/sample={BytesPerSample}, channels={Channels}, rate={SampleRate}Hz, size={Size} bytes",
                RoomId, e.SampleFormat, e.BytesPerSample, e.Channels, e.SampleRate, e.AudioData.Length);

            if (e.SampleRate != _sampleRate)
            {
                _sampleRate = e.SampleRate;
                InitializeFilters();
            }

            float[] samples = ConvertBytesToSamples(e.AudioData, e.AudioData.Length, e.BytesPerSample, e.SampleFormat);
            ProcessAudioSamples(samples, e.SampleRate, e.RawOpusData, e.DurationRtpUnits);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio data for room {RoomId}", RoomId);
        }
    }

    private void ProcessAudioSamples(float[] samples, int sampleRate, byte[]? rawOpusData = null, uint durationRtpUnits = 0)
    {
        // Only apply filters for non-passthrough (RTSP) streams
        if (rawOpusData == null && _settings.FilterEnabled && _lowPassFilter != null && _highPassFilter != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = _highPassFilter.Transform(_lowPassFilter.Transform(samples[i]));
            }
        }

        float rms = CalculateRmsLevel(samples);

        double dbLevel;
        if (rms > 0)
        {
            dbLevel = 20 * Math.Log10(rms / REFERENCE_LEVEL);
            dbLevel = Math.Max(dbLevel, DB_FLOOR);
        }
        else
        {
            dbLevel = DB_FLOOR;
        }

        _audioLevelQueue.Enqueue((float)dbLevel);
        while (_audioLevelQueue.Count > _settings.AverageSampleCount)
        {
            _audioLevelQueue.TryDequeue(out _);
        }

        double averageLevel = CalculateAverageLevel();
        DateTime now = DateTime.UtcNow;

        if (averageLevel > _settings.SoundThreshold &&
            (now - _lastThresholdExceededTime).TotalSeconds > _settings.ThresholdPauseDuration)
        {
            _lastThresholdExceededTime = now;
            _logger.LogInformation("Sound threshold exceeded for room {RoomId}: {Level:F2} dB. Pausing threshold checks for {Pause} seconds",
                RoomId, averageLevel, _settings.ThresholdPauseDuration);

            SoundThresholdExceeded?.Invoke(this, new SoundThresholdEventArgs
            {
                AudioLevel = averageLevel,
                Threshold = _settings.SoundThreshold,
                RoomId = RoomId,
                Timestamp = now
            });
        }

        // For passthrough (Nest), skip PCM conversion - raw Opus will be sent directly
        byte[] pcmBytes;
        if (rawOpusData != null)
        {
            pcmBytes = Array.Empty<byte>();
        }
        else
        {
            // Convert float samples to int16 PCM bytes for RTSP path
            pcmBytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short pcmSample = (short)(Math.Clamp(samples[i], -1.0f, 1.0f) * 32767);
                pcmBytes[i * 2] = (byte)(pcmSample & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((pcmSample >> 8) & 0xFF);
            }
        }

        AudioSampleProcessed?.Invoke(this, new AudioSampleEventArgs
        {
            AudioData = pcmBytes,
            AudioLevel = averageLevel,
            SampleRate = sampleRate,
            Timestamp = now,
            RawOpusData = rawOpusData,
            DurationRtpUnits = durationRtpUnits
        });
    }

    private float[] ConvertBytesToSamples(byte[] buffer, int bytesRecorded, int bytesPerSample, AVSampleFormat sampleFormat)
    {
        int sampleCount = bytesRecorded / bytesPerSample;
        float[] samples = new float[sampleCount];

        switch (sampleFormat)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                ProcessFloatSamples(buffer, samples);
                break;

            case AVSampleFormat.AV_SAMPLE_FMT_DBL:
                ProcessDoubleSamples(buffer, samples);
                break;

            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                Process16BitSamples(buffer, samples, bytesPerSample);
                break;

            case AVSampleFormat.AV_SAMPLE_FMT_S32:
            case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                Process32BitSamples(buffer, samples);
                break;

            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                ProcessFloatPlanarSamples(buffer, samples);
                break;

            case AVSampleFormat.AV_SAMPLE_FMT_DBLP:
                ProcessDoublePlanarSamples(buffer, samples);
                break;

            default:
                _logger.LogWarning("Unsupported audio format {Format}, falling back to 16-bit PCM", sampleFormat);
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
            samples[i] = sample / 32768f;
        }
    }

    private void Process32BitSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int sample = BitConverter.ToInt32(buffer, i * 4);
            samples[i] = sample / (float)int.MaxValue;
        }
    }

    private void ProcessFloatSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToSingle(buffer, i * 4);
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }

    private void ProcessDoubleSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = BitConverter.ToDouble(buffer, i * 8);
            samples[i] = (float)sample;
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }

    private void ProcessFloatPlanarSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToSingle(buffer, i * 4);
            if (samples[i] > 1.0f) samples[i] = 1.0f;
            if (samples[i] < -1.0f) samples[i] = -1.0f;
        }
    }

    private void ProcessDoublePlanarSamples(byte[] buffer, float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = BitConverter.ToDouble(buffer, i * 8);
            samples[i] = (float)sample;
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
            if (float.IsNaN(samples[i]) || float.IsInfinity(samples[i]))
                continue;

            sum += samples[i] * samples[i];
            validSamples++;
        }

        if (validSamples == 0)
            return 0;

        return (float)Math.Sqrt(sum / validSamples);
    }

    private double CalculateAverageLevel()
    {
        if (_audioLevelQueue.IsEmpty)
            return -100.0;

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
        // No resources to dispose - RtspAudioReader lifecycle is managed by AudioStreamingService
    }
}
