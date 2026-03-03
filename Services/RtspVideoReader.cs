using System;
using System.Runtime.InteropServices;
using BabyMonitarr.Backend.Models;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BabyMonitarr.Backend.Services
{
    public enum VideoPassthroughCodec
    {
        H264,
        H265,
        VP8
    }

    public class VideoFrameEventArgs : EventArgs
    {
        public VideoPassthroughCodec Codec { get; set; }
        public byte[] EncodedData { get; set; } = Array.Empty<byte>();
        public uint DurationRtpUnits { get; set; }
        public long TimestampMs { get; set; }
    }

    public class VideoSourceInfoEventArgs : EventArgs
    {
        public int RoomId { get; set; }
        public string SourceCodecName { get; set; } = string.Empty;
        public VideoPassthroughCodec? PassthroughCodec { get; set; }
        public string? FailureReason { get; set; }
        public bool IsSupported => PassthroughCodec.HasValue;
    }

    internal class RtspVideoReader : IDisposable
    {
        private readonly Room _room;
        private readonly ILogger<RtspVideoReader> _logger;
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

        public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;
        public event EventHandler<VideoSourceInfoEventArgs>? VideoSourceInfoDetected;

        public RtspVideoReader(
            Room room,
            ILogger<RtspVideoReader> logger,
            IOptionsMonitor<FfmpegDiagnosticsOptions> diagnosticsOptions,
            FfprobeSnapshotService ffprobeSnapshotService)
        {
            _room = room;
            _logger = logger;
            _diagnosticsOptions = diagnosticsOptions;
            _ffprobeSnapshotService = ffprobeSnapshotService;
            _rtspUrl = room.CameraStreamUrl ?? string.Empty;
            _redactedRtspUrl = RtspDiagnostics.RedactRtspUrl(_rtspUrl);
            _username = room.CameraUsername ?? string.Empty;
            _password = room.CameraPassword ?? string.Empty;

            FFmpegLibraryLoader.EnsureInitialized(_logger, _diagnosticsOptions.CurrentValue);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Starting RTSP video reader for room {RoomId}: {Url}",
                _room.Id,
                _redactedRtspUrl);

            if (string.IsNullOrEmpty(_rtspUrl))
            {
                _logger.LogError("Cannot start RTSP video stream without a URL");
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
                            "Retrying RTSP video connection for room {RoomId} (attempt {Attempt} of {Max})...",
                            _room.Id,
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
                        "Error processing RTSP video stream for room {RoomId} (attempt {Attempt} of {Max})",
                        _room.Id,
                        retryCount,
                        MaxRetryAttempts);

                    if (retryCount >= MaxRetryAttempts)
                    {
                        _logger.LogError(
                            "Max retry attempts reached for room {RoomId}. Giving up on RTSP video stream.",
                            _room.Id);
                    }
                }
            }
        }

        private unsafe void ProcessRtspStream(CancellationToken cancellationToken)
        {
            FfmpegDiagnosticsOptions diagnostics = _diagnosticsOptions.CurrentValue;

            _logger.LogInformation(
                "Connecting to RTSP video stream for room {RoomId}: {Url}. Diagnostics enabled: {DiagnosticsEnabled}",
                _room.Id,
                _redactedRtspUrl,
                diagnostics.Enabled);

            AVFormatContext* formatContext = null;
            AVDictionary* options = null;
            AVPacket* packet = null;
            AVBSFContext* bitstreamFilterContext = null;
            int videoStreamIndex = -1;

            RtspStatsAccumulator? stats = diagnostics.Enabled && diagnostics.LogFrameStats
                ? new RtspStatsAccumulator(diagnostics.FrameStatsInterval)
                : null;

            try
            {
                formatContext = ffmpeg.avformat_alloc_context();
                if (formatContext == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg format context for RTSP video stream");
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
                        "RTSP video options for room {RoomId}: {Options}",
                        _room.Id,
                        RtspDiagnostics.FormatDictionary(options));
                }

                int ret = ffmpeg.avformat_open_input(&formatContext, _rtspUrl, null, &options);

                if (diagnostics.Enabled && diagnostics.LogRtspOptions)
                {
                    _logger.LogDebug(
                        "Remaining FFmpeg options after avformat_open_input for room {RoomId}: {Options}",
                        _room.Id,
                        RtspDiagnostics.FormatDictionary(options));
                }

                if (ret < 0)
                {
                    string ffmpegError = RtspDiagnostics.GetFfmpegError(ret);
                    _logger.LogError(
                        "avformat_open_input failed for RTSP video in room {RoomId}. URL={Url}, Error={Error}, Code={ErrorCode}",
                        _room.Id,
                        _redactedRtspUrl,
                        ffmpegError,
                        ret);

                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _room.Id,
                        "video",
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
                        "avformat_find_stream_info failed for RTSP video in room {RoomId}. Error={Error}, Code={ErrorCode}",
                        _room.Id,
                        ffmpegError,
                        ret);

                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _room.Id,
                        "video",
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
                            _room.Id,
                            RtspDiagnostics.FormatStreamMetadata(formatContext, i));
                    }
                }

                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamIndex = i;
                        break;
                    }
                }

                if (videoStreamIndex == -1)
                {
                    _ffprobeSnapshotService.CaptureIfEnabled(
                        _room.Id,
                        "video",
                        _rtspUrl,
                        "find_video_stream",
                        cancellationToken);
                    throw new Exception("Could not find video stream in RTSP feed");
                }

                AVStream* videoStream = formatContext->streams[videoStreamIndex];
                AVCodecParameters* codecParams = videoStream->codecpar;

                string sourceCodecName = GetCodecName(codecParams->codec_id);
                if (!TryResolvePassthroughCodec(codecParams->codec_id, out var passthroughCodec))
                {
                    string reason =
                        $"Video passthrough for room {_room.Id} does not support RTSP codec '{sourceCodecName}'. " +
                        "Supported codecs are H264, H265, and VP8.";

                    EmitVideoSourceInfo(sourceCodecName, null, reason);
                    throw new InvalidOperationException(reason);
                }

                EmitVideoSourceInfo(sourceCodecName, passthroughCodec, null);

                AVRational streamTimeBase = videoStream->time_base;
                if (passthroughCodec == VideoPassthroughCodec.H264)
                {
                    bitstreamFilterContext = CreateBitstreamFilterContext(codecParams, streamTimeBase, "h264_mp4toannexb");
                }
                else if (passthroughCodec == VideoPassthroughCodec.H265)
                {
                    bitstreamFilterContext = CreateBitstreamFilterContext(codecParams, streamTimeBase, "hevc_mp4toannexb");
                }

                packet = ffmpeg.av_packet_alloc();
                if (packet == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg packet buffer for RTSP video stream");
                }

                long lastPacketPts = ffmpeg.AV_NOPTS_VALUE;

                while (!cancellationToken.IsCancellationRequested)
                {
                    ret = ffmpeg.av_read_frame(formatContext, packet);
                    if (ret < 0)
                    {
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            _logger.LogInformation("End of video stream for room {RoomId}", _room.Id);
                        }
                        else
                        {
                            stats?.RecordReadError();
                            _logger.LogWarning(
                                "Error reading video frame for room {RoomId}: {Error}",
                                _room.Id,
                                RtspDiagnostics.GetFfmpegError(ret));
                        }
                        break;
                    }

                    bool isVideoPacket = packet->stream_index == videoStreamIndex;
                    stats?.RecordPacket(isVideoPacket);

                    if (!isVideoPacket)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    if (bitstreamFilterContext != null)
                    {
                        ret = ffmpeg.av_bsf_send_packet(bitstreamFilterContext, packet);
                        ffmpeg.av_packet_unref(packet);

                        if (ret < 0)
                        {
                            _logger.LogDebug(
                                "av_bsf_send_packet failed for RTSP video room {RoomId}: {Error}",
                                _room.Id,
                                RtspDiagnostics.GetFfmpegError(ret));
                            continue;
                        }

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            ret = ffmpeg.av_bsf_receive_packet(bitstreamFilterContext, packet);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            {
                                break;
                            }

                            if (ret < 0)
                            {
                                _logger.LogDebug(
                                    "av_bsf_receive_packet failed for RTSP video room {RoomId}: {Error}",
                                    _room.Id,
                                    RtspDiagnostics.GetFfmpegError(ret));
                                break;
                            }

                            EmitEncodedPacket(packet, passthroughCodec, streamTimeBase, ref lastPacketPts);
                            stats?.RecordFrameDecoded();
                            ffmpeg.av_packet_unref(packet);

                            if (stats != null && diagnostics.Enabled && diagnostics.LogFrameStats && stats.ShouldLog())
                            {
                                _logger.LogDebug(
                                    "RTSP video passthrough stats for room {RoomId}: {Stats}",
                                    _room.Id,
                                    stats.BuildSummary());
                            }
                        }
                    }
                    else
                    {
                        EmitEncodedPacket(packet, passthroughCodec, streamTimeBase, ref lastPacketPts);
                        stats?.RecordFrameDecoded();
                        ffmpeg.av_packet_unref(packet);

                        if (stats != null && diagnostics.Enabled && diagnostics.LogFrameStats && stats.ShouldLog())
                        {
                            _logger.LogDebug(
                                "RTSP video passthrough stats for room {RoomId}: {Stats}",
                                _room.Id,
                                stats.BuildSummary());
                        }
                    }
                }

                if (bitstreamFilterContext != null)
                {
                    ret = ffmpeg.av_bsf_send_packet(bitstreamFilterContext, null);
                    if (ret >= 0)
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            ret = ffmpeg.av_bsf_receive_packet(bitstreamFilterContext, packet);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            {
                                break;
                            }

                            if (ret < 0)
                            {
                                break;
                            }

                            EmitEncodedPacket(packet, passthroughCodec, streamTimeBase, ref lastPacketPts);
                            ffmpeg.av_packet_unref(packet);
                        }
                    }
                }
            }
            finally
            {
                if (options != null)
                {
                    ffmpeg.av_dict_free(&options);
                }

                if (packet != null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                if (bitstreamFilterContext != null)
                {
                    ffmpeg.av_bsf_free(&bitstreamFilterContext);
                }

                if (formatContext != null)
                {
                    ffmpeg.avformat_close_input(&formatContext);
                    ffmpeg.avformat_free_context(formatContext);
                }
            }
        }

        private unsafe AVBSFContext* CreateBitstreamFilterContext(
            AVCodecParameters* codecParameters,
            AVRational streamTimeBase,
            string filterName)
        {
            AVBitStreamFilter* bitstreamFilter = ffmpeg.av_bsf_get_by_name(filterName);
            if (bitstreamFilter == null)
            {
                throw new InvalidOperationException($"FFmpeg bitstream filter '{filterName}' is not available.");
            }

            AVBSFContext* context = null;
            int ret = ffmpeg.av_bsf_alloc(bitstreamFilter, &context);
            if (ret < 0 || context == null)
            {
                throw new InvalidOperationException(
                    $"Could not allocate FFmpeg bitstream filter '{filterName}': {RtspDiagnostics.GetFfmpegError(ret)}");
            }

            ret = ffmpeg.avcodec_parameters_copy(context->par_in, codecParameters);
            if (ret < 0)
            {
                ffmpeg.av_bsf_free(&context);
                throw new InvalidOperationException(
                    $"Could not copy codec parameters for bitstream filter '{filterName}': {RtspDiagnostics.GetFfmpegError(ret)}");
            }

            context->time_base_in = streamTimeBase;

            ret = ffmpeg.av_bsf_init(context);
            if (ret < 0)
            {
                ffmpeg.av_bsf_free(&context);
                throw new InvalidOperationException(
                    $"Could not initialize FFmpeg bitstream filter '{filterName}': {RtspDiagnostics.GetFfmpegError(ret)}");
            }

            return context;
        }

        private unsafe void EmitEncodedPacket(
            AVPacket* packet,
            VideoPassthroughCodec codec,
            AVRational timeBase,
            ref long lastPacketPts)
        {
            if (packet == null || packet->data == null || packet->size <= 0)
            {
                return;
            }

            try
            {
                long packetPts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                uint durationRtpUnits = CalculateDurationRtpUnits(packet, timeBase, packetPts, ref lastPacketPts);
                long timestampMs = packetPts != ffmpeg.AV_NOPTS_VALUE
                    ? ffmpeg.av_rescale_q(packetPts, timeBase, new AVRational { num = 1, den = 1000 })
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                byte[] encodedData = new byte[packet->size];
                Marshal.Copy((IntPtr)packet->data, encodedData, 0, encodedData.Length);

                VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
                {
                    Codec = codec,
                    EncodedData = encodedData,
                    DurationRtpUnits = durationRtpUnits,
                    TimestampMs = timestampMs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error emitting encoded video packet for room {RoomId}", _room.Id);
            }
        }

        private unsafe uint CalculateDurationRtpUnits(
            AVPacket* packet,
            AVRational timeBase,
            long packetPts,
            ref long lastPacketPts)
        {
            long durationRtpUnits;
            if (packet->duration > 0)
            {
                durationRtpUnits = ffmpeg.av_rescale_q(
                    packet->duration,
                    timeBase,
                    new AVRational { num = 1, den = 90000 });
            }
            else if (packetPts != ffmpeg.AV_NOPTS_VALUE &&
                     lastPacketPts != ffmpeg.AV_NOPTS_VALUE &&
                     packetPts > lastPacketPts)
            {
                durationRtpUnits = ffmpeg.av_rescale_q(
                    packetPts - lastPacketPts,
                    timeBase,
                    new AVRational { num = 1, den = 90000 });
            }
            else
            {
                durationRtpUnits = 3000;
            }

            if (packetPts != ffmpeg.AV_NOPTS_VALUE)
            {
                lastPacketPts = packetPts;
            }

            durationRtpUnits = Math.Clamp(durationRtpUnits, 1, 90000);
            return (uint)durationRtpUnits;
        }

        private void EmitVideoSourceInfo(
            string sourceCodecName,
            VideoPassthroughCodec? passthroughCodec,
            string? failureReason)
        {
            VideoSourceInfoDetected?.Invoke(this, new VideoSourceInfoEventArgs
            {
                RoomId = _room.Id,
                SourceCodecName = sourceCodecName,
                PassthroughCodec = passthroughCodec,
                FailureReason = failureReason
            });
        }

        private static bool TryResolvePassthroughCodec(AVCodecID codecId, out VideoPassthroughCodec codec)
        {
            switch (codecId)
            {
                case AVCodecID.AV_CODEC_ID_H264:
                    codec = VideoPassthroughCodec.H264;
                    return true;
                case AVCodecID.AV_CODEC_ID_HEVC:
                    codec = VideoPassthroughCodec.H265;
                    return true;
                case AVCodecID.AV_CODEC_ID_VP8:
                    codec = VideoPassthroughCodec.VP8;
                    return true;
                default:
                    codec = default;
                    return false;
            }
        }

        private static unsafe string GetCodecName(AVCodecID codecId)
        {
            string? codecName = ffmpeg.avcodec_get_name(codecId);
            return string.IsNullOrWhiteSpace(codecName) ? codecId.ToString() : codecName.Trim();
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
                    // Task cancellation is expected on shutdown.
                }
                _processingTask = null;
            }

            _cts?.Dispose();
            _cts = null;

            _logger.LogInformation("RTSP video reader stopped for room {RoomId}", _room.Id);
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

        ~RtspVideoReader()
        {
            Dispose(false);
        }
    }
}
