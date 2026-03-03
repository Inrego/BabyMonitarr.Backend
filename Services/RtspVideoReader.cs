using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services
{
    public class VideoFrameEventArgs : EventArgs
    {
        public byte[] I420Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public long TimestampMs { get; set; }
        public byte[]? RawH264Data { get; set; }
        public uint DurationRtpUnits { get; set; }
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
        private const int TargetFps = 10;
        private const int MaxWidth = 640;
        private const int MaxHeight = 480;

        public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;

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
                        _logger.LogInformation("Retrying RTSP video connection for room {RoomId} (attempt {Attempt} of {Max})...",
                            _room.Id, retryCount + 1, MaxRetryAttempts);
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
                    _logger.LogError(ex, "Error processing RTSP video stream for room {RoomId} (attempt {Attempt} of {Max})",
                        _room.Id, retryCount, MaxRetryAttempts);

                    if (retryCount >= MaxRetryAttempts)
                    {
                        _logger.LogError("Max retry attempts reached for room {RoomId}. Giving up on RTSP video stream.", _room.Id);
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
            AVCodecContext* codecContext = null;
            SwsContext* swsContext = null;
            AVDictionary* options = null;
            AVPacket* packet = null;
            AVFrame* frame = null;
            AVFrame* i420Frame = null;
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

                // Find video stream
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

                AVCodecParameters* codecParams = formatContext->streams[videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec == null)
                {
                    throw new Exception("Unsupported video codec");
                }

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (codecContext == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg codec context for RTSP video stream");
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
                    throw new Exception($"Failed to open video codec: {RtspDiagnostics.GetFfmpegError(ret)}");
                }

                int srcWidth = codecContext->width;
                int srcHeight = codecContext->height;
                AVPixelFormat srcPixFmt = codecContext->pix_fmt;

                if (srcWidth <= 0 || srcHeight <= 0)
                {
                    srcWidth = codecParams->width;
                    srcHeight = codecParams->height;
                }

                if (srcWidth <= 0 || srcHeight <= 0)
                {
                    throw new Exception(
                        $"Invalid video dimensions: {srcWidth}x{srcHeight} (codec={codec->id}, format={srcPixFmt})");
                }

                int dstWidth, dstHeight;
                CalculateScaledDimensions(srcWidth, srcHeight, out dstWidth, out dstHeight);

                _logger.LogInformation("Video stream found for room {RoomId}: {SrcW}x{SrcH} -> {DstW}x{DstH}, format={Fmt}",
                    _room.Id, srcWidth, srcHeight, dstWidth, dstHeight, srcPixFmt);

                swsContext = ffmpeg.sws_getContext(
                    srcWidth, srcHeight, srcPixFmt,
                    dstWidth, dstHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);

                if (swsContext == null)
                {
                    throw new Exception("Could not create sws scaling context");
                }

                packet = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
                i420Frame = ffmpeg.av_frame_alloc();
                if (packet == null || frame == null || i420Frame == null)
                {
                    throw new InvalidOperationException("Could not allocate FFmpeg packet/frame buffers for RTSP video stream");
                }

                i420Frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                i420Frame->width = dstWidth;
                i420Frame->height = dstHeight;
                ret = ffmpeg.av_frame_get_buffer(i420Frame, 32);
                if (ret < 0)
                {
                    throw new Exception($"Could not allocate I420 frame buffer: {RtspDiagnostics.GetFfmpegError(ret)}");
                }

                AVRational timeBase = formatContext->streams[videoStreamIndex]->time_base;
                long minPtsInterval = (long)(timeBase.den / (timeBase.num * TargetFps));
                long lastEmittedPts = long.MinValue;

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
                            _logger.LogWarning("Error reading video frame for room {RoomId}: {Error}",
                                _room.Id, RtspDiagnostics.GetFfmpegError(ret));
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

                    ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                    ffmpeg.av_packet_unref(packet);

                    if (ret < 0)
                    {
                        stats?.RecordSendPacketError();

                        if (diagnostics.Enabled)
                        {
                            _logger.LogDebug(
                                "avcodec_send_packet failed for RTSP video room {RoomId}: {Error}",
                                _room.Id,
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
                        else if (ret < 0)
                        {
                            stats?.RecordReceiveFrameError();
                            _logger.LogWarning(
                                "avcodec_receive_frame failed for RTSP video room {RoomId}: {Error}",
                                _room.Id,
                                RtspDiagnostics.GetFfmpegError(ret));
                            break;
                        }

                        long pts = frame->best_effort_timestamp;
                        if (pts != ffmpeg.AV_NOPTS_VALUE && lastEmittedPts != long.MinValue)
                        {
                            long ptsDiff = pts - lastEmittedPts;
                            if (ptsDiff < minPtsInterval)
                            {
                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }
                        }
                        lastEmittedPts = pts;

                        ffmpeg.sws_scale(swsContext,
                            frame->data, frame->linesize, 0, frame->height,
                            i420Frame->data, i420Frame->linesize);

                        EmitVideoFrame(i420Frame, dstWidth, dstHeight, pts, timeBase);

                        ffmpeg.av_frame_unref(frame);

                        if (stats != null && diagnostics.Enabled && diagnostics.LogFrameStats && stats.ShouldLog())
                        {
                            _logger.LogDebug(
                                "RTSP video decode stats for room {RoomId}: {Stats}",
                                _room.Id,
                                stats.BuildSummary());
                        }
                    }
                }

                ffmpeg.avcodec_send_packet(codecContext, null);
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (ret < 0) break;
                    ffmpeg.av_frame_unref(frame);
                }
            }
            finally
            {
                if (options != null)
                {
                    ffmpeg.av_dict_free(&options);
                }

                if (i420Frame != null)
                {
                    ffmpeg.av_frame_free(&i420Frame);
                }

                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (packet != null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                if (swsContext != null)
                {
                    ffmpeg.sws_freeContext(swsContext);
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

        private void CalculateScaledDimensions(int srcWidth, int srcHeight, out int dstWidth, out int dstHeight)
        {
            if (srcWidth <= MaxWidth && srcHeight <= MaxHeight)
            {
                // Ensure dimensions are even (required for YUV420P)
                dstWidth = srcWidth & ~1;
                dstHeight = srcHeight & ~1;
                return;
            }

            double scale = Math.Min((double)MaxWidth / srcWidth, (double)MaxHeight / srcHeight);
            dstWidth = ((int)(srcWidth * scale)) & ~1;  // Round down to even
            dstHeight = ((int)(srcHeight * scale)) & ~1;
        }

        private unsafe void EmitVideoFrame(AVFrame* i420Frame, int width, int height, long pts, AVRational timeBase)
        {
            try
            {
                // Calculate I420 data size: Y plane + U plane + V plane
                int ySize = width * height;
                int uvSize = (width / 2) * (height / 2);
                int totalSize = ySize + uvSize * 2;

                byte[] i420Data = new byte[totalSize];
                int offset = 0;

                // Copy Y plane
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy((IntPtr)(i420Frame->data[0] + row * i420Frame->linesize[0]),
                        i420Data, offset, width);
                    offset += width;
                }

                // Copy U plane
                int uvWidth = width / 2;
                int uvHeight = height / 2;
                for (int row = 0; row < uvHeight; row++)
                {
                    Marshal.Copy((IntPtr)(i420Frame->data[1] + row * i420Frame->linesize[1]),
                        i420Data, offset, uvWidth);
                    offset += uvWidth;
                }

                // Copy V plane
                for (int row = 0; row < uvHeight; row++)
                {
                    Marshal.Copy((IntPtr)(i420Frame->data[2] + row * i420Frame->linesize[2]),
                        i420Data, offset, uvWidth);
                    offset += uvWidth;
                }

                // Convert PTS to milliseconds
                long timestampMs = 0;
                if (pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    timestampMs = pts * 1000 * timeBase.num / timeBase.den;
                }

                VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
                {
                    I420Data = i420Data,
                    Width = width,
                    Height = height,
                    TimestampMs = timestampMs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error emitting video frame for room {RoomId}", _room.Id);
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
                    // Task was cancelled, expected
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
