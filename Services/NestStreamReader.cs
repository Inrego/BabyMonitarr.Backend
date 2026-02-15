using System;
using System.Linq;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
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
    private const int InitialRetryDelayMs = 5000;
    private const int StreamExtensionIntervalMs = 4 * 60 * 1000; // 4 minutes
    private const int MinStableConnectionMs = 60_000; // 1 minute - connection must last this long to reset retry count
    private const int MaxConsecutiveExtendFailures = 3;

    // Extension failure tracking
    private int _consecutiveExtendFailures;

    // Connection timing
    private long _connectionStartTicks;

    // H264 depacketization state
    private readonly List<byte[]> _h264NalBuffer = new();

    // Diagnostic counters
    private long _rtpVideoPacketCount;
    private long _rtpAudioPacketCount;
    private long _emittedFrameCount;

    // RTP timestamp tracking for video duration calculation
    private uint _lastVideoRtpTimestamp;
    private bool _hasLastVideoRtpTimestamp;

    // Opus audio decoder (for dB measurement only)
    private AudioEncoder? _opusDecoder;

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
                    int delayMs = InitialRetryDelayMs * (int)Math.Pow(3, retryCount - 1); // 5s, 15s, 45s
                    _logger.LogInformation("Retrying Nest WebRTC connection for room {RoomId} in {DelayMs}ms (attempt {Attempt} of {Max})",
                        _roomId, delayMs, retryCount + 1, MaxRetryAttempts);
                    await Task.Delay(delayMs, cancellationToken);
                }

                _connectionStartTicks = Environment.TickCount64;
                await ConnectWebRtc(cancellationToken);

                // Connection ended normally — only reset retries if it was stable
                var connectionDurationMs = Environment.TickCount64 - _connectionStartTicks;
                if (connectionDurationMs >= MinStableConnectionMs)
                {
                    retryCount = 0;
                }
                else
                {
                    retryCount++;
                    _logger.LogWarning("Nest WebRTC connection for room {RoomId} lasted only {DurationMs}ms, counting as failed attempt {Attempt}",
                        _roomId, connectionDurationMs, retryCount);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Nest WebRTC stream ended for room {RoomId}, will reconnect", _roomId);
                    await Task.Delay(InitialRetryDelayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitException ex)
            {
                retryCount++;
                var delayMs = ex.RetryAfterSeconds * 1000;
                _logger.LogWarning("Rate limited connecting Nest stream for room {RoomId}, waiting {Seconds}s (attempt {Attempt} of {Max})",
                    _roomId, ex.RetryAfterSeconds, retryCount, MaxRetryAttempts);

                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogError("Max retry attempts reached for Nest stream room {RoomId} (rate limited)", _roomId);
                    break;
                }

                await Task.Delay(delayMs, cancellationToken);
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
            },
            X_UseRtpFeedbackProfile = true
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
            new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f")
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        // Add data channel required by Google Nest (produces m=application line in SDP)
        var dataChannel = await _peerConnection.createDataChannel("data", new RTCDataChannelInit());

        // Handle incoming RTP packets
        _peerConnection.OnRtpPacketReceived += OnRtpPacketReceived;

        _peerConnection.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation("Nest WebRTC connection state for room {RoomId}: {State}", _roomId, state);
        };

        _peerConnection.oniceconnectionstatechange += (state) =>
        {
            _logger.LogInformation("Nest ICE connection state for room {RoomId}: {State}", _roomId, state);
        };

        // Create offer
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);

        // Patch SDP with attributes required by Google Nest WebRTC
        var patchedSdp = PatchSdpForNest(offer.sdp);

        _logger.LogInformation("Sending SDP offer to Nest camera for room {RoomId}", _roomId);
        _logger.LogDebug("Patched SDP offer for room {RoomId}:\n{Sdp}", _roomId, patchedSdp);

        // Send offer to Google and get answer
        Models.NestStreamInfo streamInfo;
        using (var scope = _scopeFactory.CreateScope())
        {
            var deviceService = scope.ServiceProvider.GetRequiredService<IGoogleNestDeviceService>();
            streamInfo = await deviceService.GenerateWebRtcStreamAsync(_nestDeviceId, patchedSdp);
        }
        _mediaSessionId = streamInfo.MediaSessionId;

        // Extract ICE candidates from Google's SDP answer before stripping them.
        // SIPSorcery's SDP parser can't handle Google's candidate format, so we
        // strip them from the SDP and add them individually via addIceCandidate().
        var sdpLines = streamInfo.SdpAnswer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var iceCandidates = new List<(string candidate, string? sdpMid, ushort sdpMLineIndex)>();
        string? currentMid = null;
        ushort currentMLineIndex = 0;
        bool firstMLine = true;

        foreach (var line in sdpLines)
        {
            if (line.StartsWith("m="))
            {
                if (firstMLine)
                    firstMLine = false;
                else
                    currentMLineIndex++;
            }
            else if (line.StartsWith("a=mid:"))
            {
                currentMid = line.Substring(6);
            }
            else if (line.StartsWith("a=candidate:"))
            {
                // Store the candidate value (without the "a=" prefix)
                iceCandidates.Add((line.Substring(2), currentMid, currentMLineIndex));
            }
        }

        _logger.LogInformation("Extracted {Count} ICE candidates from Nest SDP answer for room {RoomId}",
            iceCandidates.Count, _roomId);

        // Strip candidate lines from SDP so SIPSorcery's parser doesn't choke
        var cleanedSdp = string.Join("\r\n",
            sdpLines.Where(line => !line.StartsWith("a=candidate:")));

        var answer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = cleanedSdp
        };
        var result = _peerConnection.setRemoteDescription(answer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new Exception($"Failed to set remote description: {result}");
        }

        // Add ICE candidates individually after remote description is set.
        // Some Nest candidates can be malformed for SIPSorcery's parser; skip
        // invalid entries so valid candidates still get applied.
        var addedCandidates = 0;
        var skippedCandidates = 0;

        foreach (var (candidate, mid, index) in iceCandidates)
        {
            if (!TryNormalizeIceCandidateForSipsorcery(candidate, out var normalizedCandidate, out var reason))
            {
                skippedCandidates++;
                _logger.LogWarning(
                    "Skipping malformed Nest ICE candidate for room {RoomId}: mid={Mid}, index={Index}, reason={Reason}, candidate={Candidate}",
                    _roomId, mid, index, reason, candidate);
                continue;
            }

            try
            {
                _logger.LogDebug("Adding ICE candidate for room {RoomId}: mid={Mid}, index={Index}, candidate={Candidate}",
                    _roomId, mid, index, normalizedCandidate);
                _peerConnection.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = normalizedCandidate,
                    sdpMid = mid,
                    sdpMLineIndex = index
                });
                addedCandidates++;
            }
            catch (FormatException ex)
            {
                skippedCandidates++;
                _logger.LogWarning(ex,
                    "Failed to parse Nest ICE candidate for room {RoomId}: mid={Mid}, index={Index}, candidate={Candidate}",
                    _roomId, mid, index, normalizedCandidate);
            }
        }

        _logger.LogInformation(
            "Processed Nest ICE candidates for room {RoomId}: added={Added}, skipped={Skipped}",
            _roomId, addedCandidates, skippedCandidates);
        if (iceCandidates.Count > 0 && addedCandidates == 0)
        {
            _logger.LogWarning(
                "No valid Nest ICE candidates were applied for room {RoomId}; stream connectivity may fail",
                _roomId);
        }

        _logger.LogInformation("Nest WebRTC connection established for room {RoomId}, media session: {SessionId}",
            _roomId, _mediaSessionId);

        _consecutiveExtendFailures = 0;

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
            _consecutiveExtendFailures = 0;
            _logger.LogDebug("Extended Nest stream for room {RoomId}, new expiry: {Expiry}", _roomId, streamInfo.ExpiresAt);
        }
        catch (RateLimitException ex)
        {
            _consecutiveExtendFailures++;
            // Stream is still alive for ~5 minutes; reschedule extension after the rate limit window
            var retryMs = ex.RetryAfterSeconds * 1000;
            _logger.LogWarning("Rate limited extending Nest stream for room {RoomId}, will retry in {Seconds}s (failure {Count}/{Max})",
                _roomId, ex.RetryAfterSeconds, _consecutiveExtendFailures, MaxConsecutiveExtendFailures);

            if (_consecutiveExtendFailures >= MaxConsecutiveExtendFailures)
            {
                _logger.LogError("Too many consecutive extend failures for room {RoomId}, reconnecting", _roomId);
                _peerConnection?.close();
                return;
            }

            // Reschedule a one-shot retry after the rate limit window
            _extensionTimer?.Change(retryMs, StreamExtensionIntervalMs);
        }
        catch (Exception ex)
        {
            _consecutiveExtendFailures++;
            _logger.LogWarning(ex, "Failed to extend Nest stream for room {RoomId} (failure {Count}/{Max})",
                _roomId, _consecutiveExtendFailures, MaxConsecutiveExtendFailures);

            if (_consecutiveExtendFailures >= MaxConsecutiveExtendFailures)
            {
                _logger.LogError("Too many consecutive extend failures for room {RoomId}, reconnecting", _roomId);
                _peerConnection?.close();
            }
            // Otherwise let the regular timer retry at the next interval
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

    /// <summary>
    /// Patches the SDP offer to support google nest.
    /// </summary>
    private static string PatchSdpForNest(string sdp)
    {
        return sdp.Replace("OPUS", "opus");
    }

    private static bool TryNormalizeIceCandidateForSipsorcery(
        string candidate,
        out string normalizedCandidate,
        out string reason)
    {
        normalizedCandidate = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            reason = "empty candidate";
            return false;
        }

        var candidateText = candidate.Trim();
        if (candidateText.StartsWith("a=", StringComparison.OrdinalIgnoreCase))
        {
            candidateText = candidateText.Substring(2).Trim();
        }

        if (!candidateText.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            reason = "missing candidate: prefix";
            return false;
        }

        var candidateBody = candidateText.Substring("candidate:".Length).TrimStart();
        var fields = candidateBody.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 7)
        {
            reason = $"expected at least 7 candidate fields but got {fields.Length}";
            return false;
        }

        // Nest SDP answers can omit the foundation and start at component-id:
        // candidate: 1 udp 2113939711 74.125.247.232 19305 typ host ...
        // SIPSorcery expects a normalized token stream with foundation present.
        var hasFoundation = fields.Length >= 8 &&
            ushort.TryParse(fields[1], out _) &&
            IsIceTransportToken(fields[2]);
        var missingFoundation = ushort.TryParse(fields[0], out _) &&
            IsIceTransportToken(fields[1]);

        if (!hasFoundation && !missingFoundation)
        {
            reason = "unrecognized candidate token layout";
            return false;
        }

        var tokens = new List<string>(fields.Length + 1);
        if (hasFoundation)
        {
            if (string.IsNullOrWhiteSpace(fields[0]))
            {
                reason = "candidate foundation missing";
                return false;
            }

            tokens.Add($"candidate:{fields[0]}");
            for (var i = 1; i < fields.Length; i++)
            {
                tokens.Add(fields[i]);
            }
        }
        else
        {
            // Synthesize a deterministic foundation from priority to satisfy parser.
            tokens.Add($"candidate:nest{fields[2]}");
            for (var i = 0; i < fields.Length; i++)
            {
                tokens.Add(fields[i]);
            }
        }

        if (tokens.Count < 8)
        {
            reason = $"expected at least 8 normalized tokens but got {tokens.Count}";
            return false;
        }
        if (!ushort.TryParse(tokens[1], out _))
        {
            reason = $"invalid component token '{tokens[1]}'";
            return false;
        }
        if (!IsIceTransportToken(tokens[2]))
        {
            reason = $"invalid transport token '{tokens[2]}'";
            return false;
        }
        if (!ushort.TryParse(tokens[5], out _))
        {
            reason = $"invalid port token '{tokens[5]}'";
            return false;
        }
        if (!tokens[6].Equals("typ", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"expected 'typ' token at position 7 but found '{tokens[6]}'";
            return false;
        }

        if (tokens[2].Equals("ssltcp", StringComparison.OrdinalIgnoreCase))
        {
            tokens[2] = "tcp";
        }
        else
        {
            tokens[2] = tokens[2].ToLowerInvariant(); // Protocol token expected by SIPSorcery parser.
        }

        tokens[6] = tokens[6].ToLowerInvariant(); // "typ" key.
        tokens[7] = tokens[7].ToLowerInvariant(); // Candidate type value.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("tcptype", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "tcpType"; // SIPSorcery parser expects camel-case key.
            }
            else if (tokens[i].Equals("raddr", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "raddr";
            }
            else if (tokens[i].Equals("rport", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "rport";
            }
        }

        normalizedCandidate = string.Join(" ", tokens);
        return true;
    }

    private static bool IsIceTransportToken(string token)
    {
        return token.Equals("udp", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ssltcp", StringComparison.OrdinalIgnoreCase);
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        try
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                var count = Interlocked.Increment(ref _rtpAudioPacketCount);
                if (count == 1)
                    _logger.LogInformation("First audio RTP packet received from Nest for room {RoomId}", _roomId);
                else if (count % 500 == 0)
                    _logger.LogDebug("Nest audio RTP packets received for room {RoomId}: {Count}", _roomId, count);

                ProcessAudioRtp(rtpPacket);
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                var count = Interlocked.Increment(ref _rtpVideoPacketCount);
                if (count == 1)
                    _logger.LogInformation("First video RTP packet received from Nest for room {RoomId}", _roomId);
                else if (count % 500 == 0)
                    _logger.LogDebug("Nest video RTP packets received for room {RoomId}: {Count}", _roomId, count);

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
        try
        {
            _opusDecoder ??= new AudioEncoder(includeOpus: true);

            // Keep the raw Opus payload for passthrough
            var rawOpusData = new byte[rtpPacket.Payload.Length];
            Buffer.BlockCopy(rtpPacket.Payload, 0, rawOpusData, 0, rtpPacket.Payload.Length);

            // Decode Opus to PCM for dB level measurement
            var pcmSamples = _opusDecoder.DecodeAudio(rtpPacket.Payload,
                new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"));

            if (pcmSamples != null && pcmSamples.Length > 0)
            {
                // Convert short[] PCM to byte[] (16-bit LE)
                var audioData = new byte[pcmSamples.Length * 2];
                Buffer.BlockCopy(pcmSamples, 0, audioData, 0, audioData.Length);

                // Opus RTP clock = 48kHz. For stereo, pcmSamples has 2 samples per clock tick.
                uint durationRtpUnits = (uint)(pcmSamples.Length / 2);

                AudioDataReceived?.Invoke(this, new AudioFormatEventArgs
                {
                    AudioData = audioData,
                    BytesPerSample = 2,
                    SampleRate = 48000,
                    Channels = 2,
                    IsPlanar = false,
                    SampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16,
                    RawOpusData = rawOpusData,
                    DurationRtpUnits = durationRtpUnits
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
            // Compute duration from RTP timestamps (90kHz clock)
            uint currentTimestamp = rtpPacket.Header.Timestamp;
            uint durationRtpUnits;
            if (_hasLastVideoRtpTimestamp)
            {
                durationRtpUnits = currentTimestamp - _lastVideoRtpTimestamp;
            }
            else
            {
                // Default to 30fps for first frame (3000 units at 90kHz)
                durationRtpUnits = 3000;
            }
            _lastVideoRtpTimestamp = currentTimestamp;
            _hasLastVideoRtpTimestamp = true;

            EmitRawH264Frame(durationRtpUnits);
            _h264NalBuffer.Clear();
        }
    }

    private void EmitRawH264Frame(uint durationRtpUnits)
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

        var frameCount = Interlocked.Increment(ref _emittedFrameCount);
        if (frameCount == 1)
            _logger.LogInformation("First raw H.264 frame emitted for Nest room {RoomId} ({Size} bytes)",
                _roomId, annexB.Length);
        else if (frameCount % 100 == 0)
            _logger.LogDebug("Nest raw H.264 frames emitted for room {RoomId}: {Count}", _roomId, frameCount);

        VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
        {
            RawH264Data = annexB,
            DurationRtpUnits = durationRtpUnits,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
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
            }
            _isDisposed = true;
        }
    }

    ~NestStreamReader()
    {
        Dispose(false);
    }
}
