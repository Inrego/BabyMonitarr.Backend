using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services;

public class NestStreamReader : IDisposable
{
    private readonly string _nestDeviceId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly int _roomId;
    private bool _isDisposed;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;
    private RTCPeerConnection? _peerConnection;
    private string? _mediaSessionId;
    private Timer? _extensionTimer;

    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMs = 5000;
    private const int StreamExtensionIntervalMs = 4 * 60 * 1000; // 4 minutes
    private const int TargetFps = 10;
    private const int MaxWidth = 640;
    private const int MaxHeight = 480;

    // H264 depacketization state
    private readonly List<byte[]> _h264NalBuffer = new();

    // Frame rate limiting
    private DateTime _lastVideoFrameTime = DateTime.MinValue;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);

    // Opus audio decoder
    private AudioEncoder? _opusDecoder;

    // FFmpeg H264 decoder
    private unsafe AVCodecContext* _videoCodecContext;
    private unsafe SwsContext* _swsContext;
    private bool _videoDecoderInitialized;
    private int _decodedWidth;
    private int _decodedHeight;
    private readonly object _videoDecoderLock = new();

    public event EventHandler<AudioFormatEventArgs>? AudioDataReceived;
    public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;

    public int RoomId => _roomId;

    public NestStreamReader(
        int roomId,
        string nestDeviceId,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        _roomId = roomId;
        _nestDeviceId = nestDeviceId;
        _scopeFactory = scopeFactory;
        _logger = logger;

        InitializeFFmpeg();
    }

    private void InitializeFFmpeg()
    {
        try
        {
            string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string ffmpegPath = Path.Combine(appPath, "FFmpeg");

            if (Directory.Exists(ffmpegPath))
            {
                ffmpeg.RootPath = ffmpegPath;
            }

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize FFmpeg for Nest stream reader (room {RoomId})", _roomId);
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Nest stream reader for room {RoomId}, device {DeviceId}", _roomId, _nestDeviceId);

        if (string.IsNullOrEmpty(_nestDeviceId))
        {
            _logger.LogError("Cannot start Nest stream without a device ID");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ConnectWithRetry(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    private async Task ConnectWithRetry(CancellationToken cancellationToken)
    {
        int retryCount = 0;

        while (retryCount < MaxRetryAttempts && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (retryCount > 0)
                {
                    _logger.LogInformation("Retrying Nest WebRTC connection for room {RoomId} (attempt {Attempt} of {Max})",
                        _roomId, retryCount + 1, MaxRetryAttempts);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }

                await ConnectWebRtc(cancellationToken);

                // If we reach here, the connection was established and then ended normally
                // Reset retry count for the next reconnection cycle
                retryCount = 0;

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Nest WebRTC stream ended for room {RoomId}, will reconnect", _roomId);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, "Error in Nest WebRTC stream for room {RoomId} (attempt {Attempt} of {Max})",
                    _roomId, retryCount, MaxRetryAttempts);

                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogError("Max retry attempts reached for Nest stream room {RoomId}", _roomId);
                }
            }
        }
    }

    private async Task ConnectWebRtc(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Establishing Nest WebRTC connection for room {RoomId}", _roomId);

        // Create peer connection configured to receive
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        _peerConnection = new RTCPeerConnection(config);

        // Add receive-only audio transceiver (Opus)
        var audioFormats = new List<AudioFormat>
        {
            new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1")
        };
        var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(audioTrack);

        // Add receive-only video transceiver (H264)
        var videoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1")
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        // Handle incoming RTP packets
        _peerConnection.OnRtpPacketReceived += OnRtpPacketReceived;

        _peerConnection.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation("Nest WebRTC connection state for room {RoomId}: {State}", _roomId, state);
        };

        // Create offer
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);

        _logger.LogInformation("Sending SDP offer to Nest camera for room {RoomId}", _roomId);

        // Send offer to Google and get answer
        Models.NestStreamInfo streamInfo;
        using (var scope = _scopeFactory.CreateScope())
        {
            var deviceService = scope.ServiceProvider.GetRequiredService<IGoogleNestDeviceService>();
            streamInfo = await deviceService.GenerateWebRtcStreamAsync(_nestDeviceId, offer.sdp);
        }
        _mediaSessionId = streamInfo.MediaSessionId;

        // Set remote description with Google's SDP answer
        var answer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = streamInfo.SdpAnswer
        };
        var result = _peerConnection.setRemoteDescription(answer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new Exception($"Failed to set remote description: {result}");
        }

        _logger.LogInformation("Nest WebRTC connection established for room {RoomId}, media session: {SessionId}",
            _roomId, _mediaSessionId);

        // Start extension timer to keep stream alive (every 4 minutes)
        _extensionTimer = new Timer(
            async _ => await ExtendStream(),
            null,
            StreamExtensionIntervalMs,
            StreamExtensionIntervalMs);

        // Wait until cancelled or connection drops
        var tcs = new TaskCompletionSource();
        cancellationToken.Register(() => tcs.TrySetResult());

        _peerConnection.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.closed ||
                state == RTCPeerConnectionState.disconnected)
            {
                tcs.TrySetResult();
            }
        };

        await tcs.Task;

        // Cleanup
        await StopStream();
    }

    private async Task ExtendStream()
    {
        if (string.IsNullOrEmpty(_mediaSessionId)) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deviceService = scope.ServiceProvider.GetRequiredService<IGoogleNestDeviceService>();
            var streamInfo = await deviceService.ExtendWebRtcStreamAsync(_nestDeviceId, _mediaSessionId);
            _mediaSessionId = streamInfo.MediaSessionId;
            _logger.LogDebug("Extended Nest stream for room {RoomId}, new expiry: {Expiry}", _roomId, streamInfo.ExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extend Nest stream for room {RoomId}, will reconnect", _roomId);
            // Kill current connection to trigger reconnect
            _peerConnection?.close();
        }
    }

    private async Task StopStream()
    {
        _extensionTimer?.Dispose();
        _extensionTimer = null;

        if (!string.IsNullOrEmpty(_mediaSessionId))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deviceService = scope.ServiceProvider.GetRequiredService<IGoogleNestDeviceService>();
                await deviceService.StopWebRtcStreamAsync(_nestDeviceId, _mediaSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping Nest stream for room {RoomId}", _roomId);
            }
            _mediaSessionId = null;
        }

        if (_peerConnection != null)
        {
            _peerConnection.OnRtpPacketReceived -= OnRtpPacketReceived;
            _peerConnection.close();
            _peerConnection.Dispose();
            _peerConnection = null;
        }
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        try
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                ProcessAudioRtp(rtpPacket);
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                ProcessVideoRtp(rtpPacket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing RTP packet for room {RoomId}", _roomId);
        }
    }

    private void ProcessAudioRtp(RTPPacket rtpPacket)
    {
        // Opus RTP payload is the raw Opus frame
        // Decode Opus to PCM using SIPSorcery's AudioEncoder
        try
        {
            _opusDecoder ??= new AudioEncoder(includeOpus: true);

            var pcmSamples = _opusDecoder.DecodeAudio(rtpPacket.Payload,
                new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"));

            if (pcmSamples != null && pcmSamples.Length > 0)
            {
                // Convert short[] PCM to byte[] (16-bit LE)
                var audioData = new byte[pcmSamples.Length * 2];
                Buffer.BlockCopy(pcmSamples, 0, audioData, 0, audioData.Length);

                AudioDataReceived?.Invoke(this, new AudioFormatEventArgs
                {
                    AudioData = audioData,
                    BytesPerSample = 2,
                    SampleRate = 48000,
                    Channels = 1,
                    IsPlanar = false,
                    SampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error decoding Opus audio for room {RoomId}", _roomId);
        }
    }

    private void ProcessVideoRtp(RTPPacket rtpPacket)
    {
        // H264 RTP depacketization
        var payload = rtpPacket.Payload;
        if (payload == null || payload.Length == 0) return;

        byte nalHeader = payload[0];
        int nalType = nalHeader & 0x1F;

        if (nalType >= 1 && nalType <= 23)
        {
            // Single NAL unit packet
            _h264NalBuffer.Add(payload);
        }
        else if (nalType == 28)
        {
            // FU-A fragmentation unit
            if (payload.Length < 2) return;

            byte fuHeader = payload[1];
            bool startBit = (fuHeader & 0x80) != 0;
            bool endBit = (fuHeader & 0x40) != 0;
            int originalNalType = fuHeader & 0x1F;

            if (startBit)
            {
                // Reconstruct NAL header
                byte reconstructedHeader = (byte)((nalHeader & 0xE0) | originalNalType);
                var nalData = new byte[payload.Length - 1];
                nalData[0] = reconstructedHeader;
                Buffer.BlockCopy(payload, 2, nalData, 1, payload.Length - 2);
                _h264NalBuffer.Add(nalData);
            }
            else if (_h264NalBuffer.Count > 0)
            {
                // Append fragment data to last NAL
                var lastNal = _h264NalBuffer[^1];
                var extended = new byte[lastNal.Length + payload.Length - 2];
                Buffer.BlockCopy(lastNal, 0, extended, 0, lastNal.Length);
                Buffer.BlockCopy(payload, 2, extended, lastNal.Length, payload.Length - 2);
                _h264NalBuffer[^1] = extended;
            }
        }
        else if (nalType == 24)
        {
            // STAP-A: multiple NALs in one packet
            int offset = 1;
            while (offset + 2 <= payload.Length)
            {
                int nalSize = (payload[offset] << 8) | payload[offset + 1];
                offset += 2;
                if (offset + nalSize > payload.Length) break;

                var nalData = new byte[nalSize];
                Buffer.BlockCopy(payload, offset, nalData, 0, nalSize);
                _h264NalBuffer.Add(nalData);
                offset += nalSize;
            }
        }

        // If RTP marker bit is set, this is the last packet of the frame
        if (rtpPacket.Header.MarkerBit == 1 && _h264NalBuffer.Count > 0)
        {
            // Frame rate limiting
            var now = DateTime.UtcNow;
            if (now - _lastVideoFrameTime < _minFrameInterval)
            {
                _h264NalBuffer.Clear();
                return;
            }
            _lastVideoFrameTime = now;

            DecodeH264Frame();
            _h264NalBuffer.Clear();
        }
    }

    private void DecodeH264Frame()
    {
        // Build an Annex B byte stream from NAL units
        int totalSize = 0;
        foreach (var nal in _h264NalBuffer)
        {
            totalSize += 4 + nal.Length; // 4 bytes start code + NAL data
        }

        var annexB = new byte[totalSize];
        int offset = 0;
        foreach (var nal in _h264NalBuffer)
        {
            // Start code: 00 00 00 01
            annexB[offset++] = 0;
            annexB[offset++] = 0;
            annexB[offset++] = 0;
            annexB[offset++] = 1;
            Buffer.BlockCopy(nal, 0, annexB, offset, nal.Length);
            offset += nal.Length;
        }

        lock (_videoDecoderLock)
        {
            unsafe
            {
                DecodeH264FrameUnsafe(annexB);
            }
        }
    }

    private unsafe void DecodeH264FrameUnsafe(byte[] annexBData)
    {
        // Initialize decoder if needed
        if (!_videoDecoderInitialized)
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                _logger.LogError("H264 decoder not found");
                return;
            }

            _videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
            _videoCodecContext->err_recognition = 0;
            _videoCodecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            int ret = ffmpeg.avcodec_open2(_videoCodecContext, codec, null);
            if (ret < 0)
            {
                _logger.LogError("Failed to open H264 decoder for room {RoomId}", _roomId);
                return;
            }

            _videoDecoderInitialized = true;
        }

        // Send data to decoder
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();

        try
        {
            fixed (byte* pData = annexBData)
            {
                packet->data = pData;
                packet->size = annexBData.Length;

                int ret = ffmpeg.avcodec_send_packet(_videoCodecContext, packet);
                if (ret < 0) return;

                while (true)
                {
                    ret = ffmpeg.avcodec_receive_frame(_videoCodecContext, frame);
                    if (ret < 0) break;

                    // Initialize scaler on first decoded frame
                    if (_swsContext == null || _decodedWidth != frame->width || _decodedHeight != frame->height)
                    {
                        if (_swsContext != null)
                        {
                            ffmpeg.sws_freeContext(_swsContext);
                        }

                        _decodedWidth = frame->width;
                        _decodedHeight = frame->height;

                        CalculateScaledDimensions(_decodedWidth, _decodedHeight, out int dstW, out int dstH);

                        _swsContext = ffmpeg.sws_getContext(
                            _decodedWidth, _decodedHeight, _videoCodecContext->pix_fmt,
                            dstW, dstH, AVPixelFormat.AV_PIX_FMT_YUV420P,
                            ffmpeg.SWS_BILINEAR, null, null, null);
                    }

                    EmitI420Frame(frame);
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private unsafe void EmitI420Frame(AVFrame* frame)
    {
        if (_swsContext == null) return;

        CalculateScaledDimensions(frame->width, frame->height, out int dstWidth, out int dstHeight);

        // Allocate output frame
        AVFrame* i420Frame = ffmpeg.av_frame_alloc();
        i420Frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        i420Frame->width = dstWidth;
        i420Frame->height = dstHeight;
        int ret = ffmpeg.av_frame_get_buffer(i420Frame, 32);
        if (ret < 0)
        {
            ffmpeg.av_frame_free(&i420Frame);
            return;
        }

        // Scale/convert
        ffmpeg.sws_scale(_swsContext,
            frame->data, frame->linesize, 0, frame->height,
            i420Frame->data, i420Frame->linesize);

        // Copy I420 data to managed byte array
        int ySize = dstWidth * dstHeight;
        int uvSize = (dstWidth / 2) * (dstHeight / 2);
        int totalSize = ySize + uvSize * 2;

        byte[] i420Data = new byte[totalSize];
        int offset = 0;

        // Y plane
        for (int row = 0; row < dstHeight; row++)
        {
            Marshal.Copy((IntPtr)(i420Frame->data[0] + row * i420Frame->linesize[0]),
                i420Data, offset, dstWidth);
            offset += dstWidth;
        }

        // U plane
        int uvWidth = dstWidth / 2;
        int uvHeight = dstHeight / 2;
        for (int row = 0; row < uvHeight; row++)
        {
            Marshal.Copy((IntPtr)(i420Frame->data[1] + row * i420Frame->linesize[1]),
                i420Data, offset, uvWidth);
            offset += uvWidth;
        }

        // V plane
        for (int row = 0; row < uvHeight; row++)
        {
            Marshal.Copy((IntPtr)(i420Frame->data[2] + row * i420Frame->linesize[2]),
                i420Data, offset, uvWidth);
            offset += uvWidth;
        }

        ffmpeg.av_frame_free(&i420Frame);

        VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
        {
            I420Data = i420Data,
            Width = dstWidth,
            Height = dstHeight,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private static void CalculateScaledDimensions(int srcWidth, int srcHeight, out int dstWidth, out int dstHeight)
    {
        if (srcWidth <= MaxWidth && srcHeight <= MaxHeight)
        {
            dstWidth = srcWidth & ~1;
            dstHeight = srcHeight & ~1;
            return;
        }

        double scale = Math.Min((double)MaxWidth / srcWidth, (double)MaxHeight / srcHeight);
        dstWidth = ((int)(srcWidth * scale)) & ~1;
        dstHeight = ((int)(srcHeight * scale)) & ~1;
    }

    public void Stop()
    {
        _cts?.Cancel();

        if (_processingTask != null)
        {
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Expected when cancelled
            }
            _processingTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("Nest stream reader stopped for room {RoomId}", _roomId);
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
                _extensionTimer?.Dispose();

                unsafe
                {
                    if (_swsContext != null)
                    {
                        ffmpeg.sws_freeContext(_swsContext);
                        _swsContext = null;
                    }

                    if (_videoCodecContext != null)
                    {
                        var ctx = _videoCodecContext;
                        ffmpeg.avcodec_free_context(&ctx);
                        _videoCodecContext = null;
                    }
                }
            }
            _isDisposed = true;
        }
    }

    ~NestStreamReader()
    {
        Dispose(false);
    }
}
