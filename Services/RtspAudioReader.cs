using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services
{
    public class AudioFormatEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public int BytesPerSample { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public bool IsPlanar { get; set; }
        public AVSampleFormat SampleFormat { get; set; }
        public byte[]? RawOpusData { get; set; }
        public uint DurationRtpUnits { get; set; }
    }

    internal class RtspAudioReader : IDisposable
    {
        private readonly int _roomId;
        private readonly string _roomName;
        private readonly AudioSettings _settings;
        private readonly ILogger<RtspAudioReader> _logger;
        private readonly IOptionsMonitor<FfmpegDiagnosticsOptions> _diagnosticsOptions;
        private readonly FfprobeSnapshotService _ffprobeSnapshotService;
        private readonly string _rtspUrl;
        private readonly string _redactedRtspUrl;
        private readonly string _username;
        private readonly string _password;
        private bool _isDisposed;
        private Task? _processingTask;
        private CancellationTokenSource? _cts;
        private const int MaxRetryAttempts = 3;
        private const int RetryDelayMs = 5000;

        public event EventHandler<AudioFormatEventArgs>? AudioDataReceived;

        public RtspAudioReader(
            int roomId,
            string roomName,
            AudioSettings settings,
            ILogger<RtspAudioReader> logger,
            IOptionsMonitor<FfmpegDiagnosticsOptions> diagnosticsOptions,
            FfprobeSnapshotService ffprobeSnapshotService)
        {
            _roomId = roomId;
            _roomName = roomName;
            _settings = settings;
            _logger = logger;
            _diagnosticsOptions = diagnosticsOptions;
            _ffprobeSnapshotService = ffprobeSnapshotService;
            _rtspUrl = _settings.CameraStreamUrl ?? string.Empty;
            _redactedRtspUrl = RtspDiagnostics.RedactRtspUrl(_rtspUrl);
            _username = _settings.CameraUsername ?? string.Empty;
            _password = _settings.CameraPassword ?? string.Empty;

            FFmpegLibraryLoader.EnsureInitialized(_logger, _diagnosticsOptions.CurrentValue);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Starting RTSP audio reader for room {RoomId} ({RoomName}) with URL {Url}",
                _roomId,
                _roomName,
                _redactedRtspUrl);

            if (string.IsNullOrEmpty(_rtspUrl))
            {
                _logger.LogError("Cannot start RTSP audio reader for room {RoomId} because URL is empty", _roomId);
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _processingTask = Task.Run(() => ProcessRtspStreamWithRetry(_cts.Token), _cts.Token);

            return Task.CompletedTask;
        }

        private async Task ProcessRtspStreamWithRetry(CancellationToken cancellationToken)
        {
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount < MaxRetryAttempts && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        _logger.LogInformation(
                            "Retrying RTSP audio reader for room {RoomId} (attempt {Attempt} of {MaxAttempts})",
                            _roomId,
                            retryCount + 1,
                            MaxRetryAttempts);
                        await Task.Delay(RetryDelayMs, cancellationToken);
                    }

                    ProcessRtspStream(cancellationToken);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogError(
                        ex,
                        "Error processing RTSP audio stream for room {RoomId} (attempt {Attempt} of {MaxAttempts})",
                        _roomId,
                        retryCount,
                        MaxRetryAttempts);

                    if (retryCount >= MaxRetryAttempts)
                    {
                        _logger.LogError(
                            "Max retry attempts reached for RTSP audio reader in room {RoomId}. Giving up.",
                            _roomId);
                    }
                }
            }
        }

        private unsafe void ProcessRtspStream(CancellationToken cancellationToken)
        {
            FfmpegDiagnosticsOptions diagnostics = _diagnosticsOptions.CurrentValue;

            _logger.LogInformation(
                "Connecting RTSP audio for room {RoomId} ({RoomName}), diagnostics enabled: {DiagnosticsEnabled}",
                _roomId,
                _roomName,
                diagnostics.Enabled);

            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            AVDictionary* options = null;
            AVPacket* packet = null;
            AVFrame* frame = null;
            int audioStream = -1;

            RtspStatsAccumulator? stats = diagnostics.Enabled && diagnostics.LogFrameStats
                ? new RtspStatsAccumulator(diagnostics.FrameStatsInterval)
                : null;

            try
            {
                formatContext = ffmpeg.avformat_alloc_context();
                if (formatContext == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg format context for RTSP audio stream");
                }

                ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
                ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
                ffmpeg.av_dict_set(&options, "max_delay", "100000", 0);
                ffmpeg.av_dict_set(&options, "analyzeduration", "1000000", 0);
                ffmpeg.av_dict_set(&options, "probesize", "100000", 0);

                if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
                {
                    ffmpeg.av_dict_set(&options, "username", _username, 0);
                    ffmpeg.av_dict_set(&options, "password", _password, 0);
                }

                if (diagnostics.Enabled && diagnostics.LogRtspOptions)
                {
                    _logger.LogDebug(
                        "RTSP audio options for room {RoomId}: {Options}",
                        _roomId,
                        RtspDiagnostics.FormatDictionary(options));
                }

                int ret = ffmpeg.avformat_open_input(&formatContext, _rtspUrl, null, &options);

                if (diagnostics.Enabled && diagnostics.LogRtspOptions)
                {
                    _logger.LogDebug(
                        "Remaining FFmpeg options after avformat_open_input for room {RoomId}: {Options}",
                        _roomId,
                        RtspDiagnostics.FormatDictionary(options));
                }

                if (ret < 0)
                {
                    string ffmpegError = RtspDiagnostics.GetFfmpegError(ret);
                    _logger.LogError(
                        "avformat_open_input failed for RTSP audio in room {RoomId}. URL={Url}, Error={Error}, Code={ErrorCode}",
                        _roomId,
                        _redactedRtspUrl,
                        ffmpegError,
                        ret);

                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _roomId,
                        "audio",
                        _rtspUrl,
                        "avformat_open_input",
                        cancellationToken);

                    throw new Exception($"Could not open RTSP stream: {ffmpegError}");
                }

                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (ret < 0)
                {
                    string ffmpegError = RtspDiagnostics.GetFfmpegError(ret);
                    _logger.LogError(
                        "avformat_find_stream_info failed for RTSP audio in room {RoomId}. Error={Error}, Code={ErrorCode}",
                        _roomId,
                        ffmpegError,
                        ret);

                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _roomId,
                        "audio",
                        _rtspUrl,
                        "avformat_find_stream_info",
                        cancellationToken);

                    throw new Exception($"Could not find stream information: {ffmpegError}");
                }

                if (diagnostics.Enabled && diagnostics.LogStreamMetadata)
                {
                    for (int i = 0; i < formatContext->nb_streams; i++)
                    {
                        _logger.LogDebug(
                            "FFmpeg stream metadata for room {RoomId}: {Metadata}",
                            _roomId,
                            RtspDiagnostics.FormatStreamMetadata(formatContext, i));
                    }
                }

                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        audioStream = i;
                        break;
                    }
                }

                if (audioStream == -1)
                {
                    _logger.LogError("Could not find audio stream in RTSP feed for room {RoomId}", _roomId);
                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _roomId,
                        "audio",
                        _rtspUrl,
                        "find_audio_stream",
                        cancellationToken);
                    throw new Exception("Could not find audio stream in RTSP feed");
                }

                AVCodecParameters* codecParams = formatContext->streams[audioStream]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec == null)
                {
                    throw new Exception($"Unsupported audio codec id: {codecParams->codec_id}");
                }

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (codecContext == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg codec context for RTSP audio stream");
                }

                ret = ffmpeg.avcodec_parameters_to_context(codecContext, codecParams);
                if (ret < 0)
                {
                    throw new Exception($"Failed to copy codec parameters to context: {RtspDiagnostics.GetFfmpegError(ret)}");
                }

                codecContext->err_recognition = 0;
                codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

                ret = ffmpeg.avcodec_open2(codecContext, codec, null);
                if (ret < 0)
                {
                    throw new Exception($"Failed to open codec: {RtspDiagnostics.GetFfmpegError(ret)}");
                }

                int channelCount = codecContext->ch_layout.nb_channels;
                _logger.LogInformation(
                    "RTSP audio stream initialized for room {RoomId}: sampleFormat={SampleFormat}, sampleRate={SampleRate}, channels={Channels}",
                    _roomId,
                    codecContext->sample_fmt,
                    codecContext->sample_rate,
                    channelCount);

                packet = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
                if (packet == null || frame == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg packet/frame for RTSP audio stream");
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    ret = ffmpeg.av_read_frame(formatContext, packet);
                    if (ret < 0)
                    {
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            _logger.LogInformation("End of RTSP audio stream reached for room {RoomId}", _roomId);
                        }
                        else
                        {
                            stats?.RecordReadError();
                            _logger.LogWarning(
                                "Error reading RTSP audio frame for room {RoomId}: {Error}",
                                _roomId,
                                RtspDiagnostics.GetFfmpegError(ret));
                        }

                        break;
                    }

                    bool isAudioPacket = packet->stream_index == audioStream;
                    stats?.RecordPacket(isAudioPacket);

                    if (!isAudioPacket)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                    ffmpeg.av_packet_unref(packet);

                    if (ret < 0)
                    {
                        stats?.RecordSendPacketError();

                        if (diagnostics.Enabled)
                        {
                            _logger.LogDebug(
                                "avcodec_send_packet failed for RTSP audio room {RoomId}: {Error}",
                                _roomId,
                                RtspDiagnostics.GetFfmpegError(ret));
                        }

                        continue;
                    }

                    while (true)
                    {
                        ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        if (ret < 0)
                        {
                            stats?.RecordReceiveFrameError();
                            _logger.LogWarning(
                                "avcodec_receive_frame failed for RTSP audio room {RoomId}: {Error}",
                                _roomId,
                                RtspDiagnostics.GetFfmpegError(ret));
                            break;
                        }

                        ProcessAudioFrame(frame, codecContext);
                        stats?.RecordFrameDecoded();
                        ffmpeg.av_frame_unref(frame);

                        if (stats != null && diagnostics.Enabled && diagnostics.LogFrameStats && stats.ShouldLog())
                        {
                            _logger.LogDebug(
                                "RTSP audio decode stats for room {RoomId}: {Stats}",
                                _roomId,
                                stats.BuildSummary());
                        }
                    }
                }

                ffmpeg.avcodec_send_packet(codecContext, null);
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (ret < 0)
                    {
                        break;
                    }

                    ProcessAudioFrame(frame, codecContext);
                    ffmpeg.av_frame_unref(frame);
                }
            }
            finally
            {
                if (options != null)
                {
                    ffmpeg.av_dict_free(&options);
                }

                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (packet != null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                if (codecContext != null)
                {
                    ffmpeg.avcodec_free_context(&codecContext);
                }

                if (formatContext != null)
                {
                    ffmpeg.avformat_close_input(&formatContext);
                    ffmpeg.avformat_free_context(formatContext);
                }
            }
        }
        
        private unsafe void ProcessAudioFrame(AVFrame* frame, AVCodecContext* codecContext)
        {
            try
            {
                int channelCount = codecContext->ch_layout.nb_channels;
                int bytesPerSample = ffmpeg.av_get_bytes_per_sample(codecContext->sample_fmt);
                bool isPlanar = ffmpeg.av_sample_fmt_is_planar(codecContext->sample_fmt) != 0;
                
                int dataSize = bytesPerSample * frame->nb_samples * channelCount;
                
                if (dataSize <= 0)
                {
                    _logger.LogWarning("Invalid audio frame size for room {RoomId}", _roomId);
                    return;
                }

                byte[] audioData = new byte[dataSize];
                
                if (isPlanar)
                {
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        IntPtr sourcePtr = (IntPtr)frame->extended_data[ch];
                        
                        for (int sample = 0; sample < frame->nb_samples; sample++)
                        {
                            int sourceOffset = sample * bytesPerSample;
                            int targetOffset = (sample * channelCount + ch) * bytesPerSample;
                            
                            Marshal.Copy(sourcePtr + sourceOffset, audioData, targetOffset, bytesPerSample);
                        }
                    }
                }
                else
                {
                    IntPtr sourcePtr = (IntPtr)frame->extended_data[0];
                    Marshal.Copy(sourcePtr, audioData, 0, dataSize);
                }
                
                audioData = AdjustVolume(audioData, bytesPerSample, _settings.VolumeAdjustmentDb);
                
                var eventArgs = new AudioFormatEventArgs
                {
                    AudioData = audioData,
                    BytesPerSample = bytesPerSample,
                    SampleRate = codecContext->sample_rate,
                    Channels = channelCount,
                    IsPlanar = isPlanar,
                    SampleFormat = codecContext->sample_fmt
                };
                
                _logger.LogDebug(
                    "Audio frame for room {RoomId}: format={Format}, rate={SampleRate}, channels={Channels}, bytesPerSample={BytesPerSample}, isPlanar={IsPlanar}, dataSize={DataSize}",
                    _roomId,
                    codecContext->sample_fmt,
                    codecContext->sample_rate,
                    channelCount,
                    bytesPerSample,
                    isPlanar,
                    dataSize);
                
                AudioDataReceived?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio frame for room {RoomId}", _roomId);
            }
        }
        
        /// <summary>
        /// Adjusts the volume of audio samples by the specified decibel value
        /// </summary>
        /// <param name="audioData">Raw audio data</param>
        /// <param name="bytesPerSample">Number of bytes per audio sample</param>
        /// <param name="dbAdjustment">Volume adjustment in decibels</param>
        /// <returns>Volume-adjusted audio data</returns>
        private byte[] AdjustVolume(byte[] audioData, int bytesPerSample, double dbAdjustment)
        {
            if (Math.Abs(dbAdjustment) < 0.01)
            {
                return audioData;
            }
            
            double amplitudeMultiplier = Math.Pow(10, dbAdjustment / 20.0);
            
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Applying volume adjustment for room {RoomId}: {DbAdjustment:F2} dB (multiplier: {Multiplier:F4})",
                    _roomId,
                    dbAdjustment,
                    amplitudeMultiplier);
            }
            
            byte[] adjustedData = new byte[audioData.Length];
            
            switch (bytesPerSample)
            {
                case 2:
                    Adjust16BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                case 4:
                    Adjust32BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                case 8:
                    Adjust64BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                default:
                    _logger.LogWarning(
                        "Unsupported bytes per sample for volume adjustment in room {RoomId}: {BytesPerSample}",
                        _roomId,
                        bytesPerSample);
                    return audioData;
            }
            
            return adjustedData;
        }
        
        private void Adjust16BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(source, i);
                double adjusted = sample * multiplier;

                if (adjusted > short.MaxValue) adjusted = short.MaxValue;
                if (adjusted < short.MinValue) adjusted = short.MinValue;

                byte[] bytes = BitConverter.GetBytes((short)adjusted);
                target[i] = bytes[0];
                target[i + 1] = bytes[1];
            }
        }
        
        private void Adjust32BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 4)
            {
                float floatSample = BitConverter.ToSingle(source, i);

                if (Math.Abs(floatSample) <= 1.0)
                {
                    float adjusted = (float)(floatSample * multiplier);

                    if (adjusted > 1.0f) adjusted = 1.0f;
                    if (adjusted < -1.0f) adjusted = -1.0f;

                    byte[] bytes = BitConverter.GetBytes(adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 4);
                }
                else
                {
                    int intSample = BitConverter.ToInt32(source, i);

                    double adjusted = intSample * multiplier;

                    if (adjusted > int.MaxValue) adjusted = int.MaxValue;
                    if (adjusted < int.MinValue) adjusted = int.MinValue;

                    byte[] bytes = BitConverter.GetBytes((int)adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 4);
                }
            }
        }
        
        private void Adjust64BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 8)
            {
                double doubleSample = BitConverter.ToDouble(source, i);

                if (Math.Abs(doubleSample) <= 1.0)
                {
                    double adjusted = doubleSample * multiplier;

                    if (adjusted > 1.0) adjusted = 1.0;
                    if (adjusted < -1.0) adjusted = -1.0;

                    byte[] bytes = BitConverter.GetBytes(adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 8);
                }
                else
                {
                    long longSample = BitConverter.ToInt64(source, i);

                    double adjusted = longSample * multiplier;

                    if (adjusted > long.MaxValue) adjusted = long.MaxValue;
                    if (adjusted < long.MinValue) adjusted = long.MinValue;

                    byte[] bytes = BitConverter.GetBytes((long)adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 8);
                }
            }
        }
        
        public void Stop()
        {
            _cts?.Cancel();
            
            if (_processingTask != null)
            {
                try
                {
                    _processingTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Task was cancelled, expected.
                }
                _processingTask = null;
            }
            
            _cts?.Dispose();
            _cts = null;
            
            _logger.LogInformation("RTSP audio reader stopped for room {RoomId}", _roomId);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Stop();
                }
                
                _isDisposed = true;
            }
        }
        
        ~RtspAudioReader()
        {
            Dispose(false);
        }
    }
}
