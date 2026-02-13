using System;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Extensions.Logging;
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
    }

    internal class RtspVideoReader : IDisposable
    {
        private readonly Room _room;
        private readonly ILogger _logger;
        private readonly string _rtspUrl;
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

        public RtspVideoReader(Room room, ILogger logger)
        {
            _room = room;
            _logger = logger;
            _rtspUrl = room.CameraStreamUrl ?? string.Empty;
            _username = room.CameraUsername ?? string.Empty;
            _password = room.CameraPassword ?? string.Empty;

            InitializeFFmpeg();
        }

        private void InitializeFFmpeg()
        {
            try
            {
                string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string ffmpegPath = Path.Combine(appPath, "FFmpeg");

                if (!Directory.Exists(ffmpegPath))
                {
                    throw new DirectoryNotFoundException($"FFmpeg directory not found at {ffmpegPath}");
                }

                ffmpeg.RootPath = ffmpegPath;
                DynamicallyLoadedBindings.Initialize();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize FFmpeg for video reader");
                throw;
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting RTSP video reader for room {RoomId}: {Url}", _room.Id, _rtspUrl);

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
            _logger.LogInformation("Connecting to RTSP video stream for room {RoomId}: {Url}", _room.Id, _rtspUrl);

            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            SwsContext* swsContext = null;
            int videoStreamIndex = -1;

            try
            {
                formatContext = ffmpeg.avformat_alloc_context();

                // Suppress noisy log messages
                av_log_set_callback_callback logCallback = (p1, level, format, vl) =>
                {
                    if (level > ffmpeg.AV_LOG_ERROR) return;
                    ffmpeg.av_log_default_callback(p1, level, format, vl);
                };
                ffmpeg.av_log_set_callback(logCallback);

                AVDictionary* options = null;
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

                int ret = ffmpeg.avformat_open_input(&formatContext, _rtspUrl, null, &options);
                if (ret < 0)
                {
                    byte* errorBuf = stackalloc byte[1024];
                    ffmpeg.av_strerror(ret, errorBuf, 1024);
                    throw new Exception($"Could not open RTSP stream: {Marshal.PtrToStringAnsi((IntPtr)errorBuf)}");
                }

                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (ret < 0)
                {
                    throw new Exception("Could not find stream information");
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
                    throw new Exception("Could not find video stream in RTSP feed");
                }

                AVCodecParameters* codecParams = formatContext->streams[videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec == null)
                {
                    throw new Exception("Unsupported video codec");
                }

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                ret = ffmpeg.avcodec_parameters_to_context(codecContext, codecParams);
                if (ret < 0)
                {
                    throw new Exception("Failed to copy codec parameters to context");
                }

                codecContext->err_recognition = 0;
                codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

                ret = ffmpeg.avcodec_open2(codecContext, codec, null);
                if (ret < 0)
                {
                    throw new Exception("Failed to open video codec");
                }

                int srcWidth = codecContext->width;
                int srcHeight = codecContext->height;
                AVPixelFormat srcPixFmt = codecContext->pix_fmt;

                // Calculate output dimensions maintaining aspect ratio, capped at MaxWidth x MaxHeight
                int dstWidth, dstHeight;
                CalculateScaledDimensions(srcWidth, srcHeight, out dstWidth, out dstHeight);

                _logger.LogInformation("Video stream found for room {RoomId}: {SrcW}x{SrcH} -> {DstW}x{DstH}, format={Fmt}",
                    _room.Id, srcWidth, srcHeight, dstWidth, dstHeight, srcPixFmt);

                // Create scaler context to convert to I420 (YUV420P) at target resolution
                swsContext = ffmpeg.sws_getContext(
                    srcWidth, srcHeight, srcPixFmt,
                    dstWidth, dstHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);

                if (swsContext == null)
                {
                    throw new Exception("Could not create sws scaling context");
                }

                AVPacket* packet = ffmpeg.av_packet_alloc();
                AVFrame* frame = ffmpeg.av_frame_alloc();
                AVFrame* i420Frame = ffmpeg.av_frame_alloc();

                // Allocate I420 output frame buffer
                i420Frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                i420Frame->width = dstWidth;
                i420Frame->height = dstHeight;
                ret = ffmpeg.av_frame_get_buffer(i420Frame, 32);
                if (ret < 0)
                {
                    throw new Exception("Could not allocate I420 frame buffer");
                }

                // Frame rate limiting
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
                            byte* errorBuf = stackalloc byte[1024];
                            ffmpeg.av_strerror(ret, errorBuf, 1024);
                            _logger.LogWarning("Error reading video frame for room {RoomId}: {Error}",
                                _room.Id, Marshal.PtrToStringAnsi((IntPtr)errorBuf));
                        }
                        break;
                    }

                    if (packet->stream_index != videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                    ffmpeg.av_packet_unref(packet);

                    if (ret < 0) continue;

                    while (true)
                    {
                        ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }
                        else if (ret < 0)
                        {
                            _logger.LogWarning("Error receiving video frame for room {RoomId}", _room.Id);
                            break;
                        }

                        // Frame rate limiting: skip frames to maintain target FPS
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

                        // Convert to I420 at target resolution
                        ffmpeg.sws_scale(swsContext,
                            frame->data, frame->linesize, 0, frame->height,
                            i420Frame->data, i420Frame->linesize);

                        EmitVideoFrame(i420Frame, dstWidth, dstHeight, pts, timeBase);

                        ffmpeg.av_frame_unref(frame);
                    }
                }

                // Flush decoder
                ffmpeg.avcodec_send_packet(codecContext, null);
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (ret < 0) break;
                    ffmpeg.av_frame_unref(frame);
                }

                ffmpeg.av_frame_free(&i420Frame);
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_packet_free(&packet);
            }
            finally
            {
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
