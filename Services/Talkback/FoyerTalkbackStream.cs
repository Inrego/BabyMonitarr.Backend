using System.Collections.Concurrent;
using System.Diagnostics;
using BabyMonitarr.Backend.Services;
using BabyMonitarr.Backend.Talkback.Foyer;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>The camera a talkback stream plays on.</summary>
public sealed record TalkbackTarget(int RoomId, string NestDeviceId, string GoogleUuid);

/// <summary>One open Google Home camera stream that talkback audio can be sent on.</summary>
public interface IFoyerTalkbackStream : IAsyncDisposable
{
    Task StartTalkbackAsync(CancellationToken ct);
    Task StopTalkbackAsync(CancellationToken ct);
    Task ExtendAsync(CancellationToken ct);

    /// <summary>Queues interleaved stereo 48 kHz PCM for the stream's paced 20 ms sender.</summary>
    void SendPcm(short[] pcm);

    /// <summary>The stream ended without being disposed (peer connection failed or closed); carries a reason.</summary>
    event Action<string>? Closed;
}

public interface IFoyerTalkbackStreamFactory
{
    /// <summary>Opens and connects a stream. Throws <see cref="TalkbackUnavailableException"/> on failure.</summary>
    Task<IFoyerTalkbackStream> OpenAsync(TalkbackTarget target, CancellationToken ct);
}

public sealed class FoyerTalkbackStreamFactory : IFoyerTalkbackStreamFactory
{
    private readonly IGoogleHomeAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public FoyerTalkbackStreamFactory(
        IGoogleHomeAuthService auth, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<IFoyerTalkbackStream> OpenAsync(TalkbackTarget target, CancellationToken ct)
    {
        string token = await _auth.GetAccessTokenAsync(ct);
        var foyer = new FoyerClient(_httpClientFactory.CreateClient(GoogleHomeAuthService.HttpClientName), token);
        var stream = new FoyerTalkbackStream(target, foyer, _loggerFactory.CreateLogger<FoyerTalkbackStream>());
        try
        {
            await stream.ConnectAsync(ct);
            return stream;
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync();
            if (ex is FoyerException { IsUnauthenticated: true }) _auth.InvalidateAccessToken();
            if (ex is OperationCanceledException) throw;
            throw ex as TalkbackUnavailableException
                ?? new TalkbackUnavailableException(TalkbackReasons.CameraError, $"Could not open the camera stream: {ex.Message}");
        }
    }
}

/// <summary>
/// A Google Home "Foyer" camera stream opened only for talkback: audio sendrecv Opus, video
/// recvonly (ignored), data channel, in the order Google expects. Separate from, and never
/// touching, the SDM monitoring stream in <see cref="NestStreamReader"/>.
/// </summary>
/// <remarks>
/// Like the Google Home web client, which sends a silent track from the moment the stream
/// connects and swaps the microphone in at START, the stream sends Opus every 20 ms for its
/// whole life: silence, or the talker's queued frames. This also smooths the uplink's arrival
/// jitter. Every audio packet carries the RFC 6464 audio level header extension
/// (<see cref="TalkbackAudioLevel"/>): Google's relay plays nothing without it (2026-10-01).
/// </remarks>
public sealed class FoyerTalkbackStream : IFoyerTalkbackStream
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private const int FrameSamples = 960;

    /// <summary>About 200 ms of talker audio; older frames are dropped so a burst cannot build up delay.</summary>
    private const int MaxQueuedFrames = 10;

    /// <summary>After a stall longer than this, the sender resumes from now instead of bursting to catch up.</summary>
    private const int MaxCatchUpSamples = 5 * FrameSamples;

    private readonly TalkbackTarget _target;
    private readonly FoyerClient _foyer;
    private readonly ILogger _logger;
    private readonly AudioFormat _opus = new(AudioCodecsEnum.OPUS, NestStreamReader.OpusPayloadType, 48000, 2, "minptime=10;useinbandfec=1");
    private readonly AudioEncoder _encoder = new(includeOpus: true);
    private readonly ConcurrentQueue<short[]> _talkerFrames = new();
    private readonly CancellationTokenSource _sendLoopCts = new();
    private readonly AudioEncoder _cameraDecoder = new(includeOpus: true);
    private readonly object _cameraLevelLock = new();
    private double _cameraEnergy;
    private long _cameraSamples;
    private double _cameraLevelBeforeTalk = double.NegativeInfinity;
    private long _cameraAudioPackets;
    private long _cameraVideoPackets;
    private RTCPeerConnection? _pc;
    private string? _streamId;
    private long _framesSent;
    private long _sentAllFrames;
    private int _disposed;

    public event Action<string>? Closed;

    public FoyerTalkbackStream(TalkbackTarget target, FoyerClient foyer, ILogger logger)
    {
        _target = target;
        _foyer = foyer;
        _logger = logger;
    }

    /// <summary>
    /// The peer connection Google expects, before any signalling: audio sendrecv Opus, video
    /// recvonly H264, data channel, in that order (same construction as the talkback spike).
    /// The audio m-line offers the ssrc-audio-level extension (extmap <see cref="TalkbackAudioLevel.ExtensionId"/>),
    /// without which Google's relay drops the talkback audio.
    /// </summary>
    public static async Task<RTCPeerConnection> CreatePeerConnectionAsync(AudioFormat opus)
    {
        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } },
            X_UseRtpFeedbackProfile = true,
        });
        var audio = new MediaStreamTrack(new List<AudioFormat> { opus }, MediaStreamStatusEnum.SendRecv);
        var audioLevel = TalkbackAudioLevel.CreateExtension();
        audio.HeaderExtensions[audioLevel.Id] = audioLevel;
        pc.addTrack(audio);
        pc.addTrack(new MediaStreamTrack(
            new List<VideoFormat>
            {
                new(VideoCodecsEnum.H264, NestStreamReader.H264PayloadType, 90000,
                    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
            },
            MediaStreamStatusEnum.RecvOnly));
        await pc.createDataChannel("data", new RTCDataChannelInit());
        return pc;
    }

    /// <summary>The offer SDP as sent to JoinStream (Google wants the codec name lower-case).</summary>
    public static string OfferSdpForFoyer(RTCSessionDescriptionInit offer) => offer.sdp.Replace("OPUS", "opus");

    internal async Task ConnectAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        var pc = await CreatePeerConnectionAsync(_opus);
        _pc = pc;
        try
        {
            IcmpResets.Ignore(pc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not turn off ICMP resets for the talkback stream of room {RoomId}: {Error}", _target.RoomId, ex.Message);
        }
        pc.OnRtpPacketReceived += (_, media, packet) => OnCameraRtp(media, packet);

        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.onconnectionstatechange += state =>
        {
            _logger.LogInformation("Talkback stream for room {RoomId}: connection {State}", _target.RoomId, state);
            if (state == RTCPeerConnectionState.connected)
            {
                connected.TrySetResult(true);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                connected.TrySetResult(false);
                if (Volatile.Read(ref _disposed) == 0) Closed?.Invoke($"camera stream {state}");
            }
        };

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        string offerSdp = OfferSdpForFoyer(offer);
        _logger.LogDebug("Talkback stream for room {RoomId}: offer SDP\n{Sdp}", _target.RoomId, offerSdp);

        try
        {
            await _foyer.CallAsync("CameraService", "SendCameraViewIntent", new SendCameraViewIntentRequest
            {
                Request = new SendCameraViewIntentRequest.Types.ViewIntentRequest
                {
                    GoogleDeviceId = _target.GoogleUuid,
                    Command = SendCameraViewIntentRequest.Types.ViewIntentCommand.ViewIntentStart,
                },
            }, SendCameraViewIntentResponse.Parser, ct);
        }
        catch (FoyerException ex) when (!ex.IsUnauthenticated)
        {
            // Only a wake-up hint; JoinStream decides whether the camera is reachable.
            _logger.LogWarning("SendCameraViewIntent failed for room {RoomId}: {Error}", _target.RoomId, ex.Message);
        }
        long wokeMs = clock.ElapsedMilliseconds;

        var join = await _foyer.CallAsync("CameraService", "JoinStream", new JoinStreamRequest
        {
            Command = "offer",
            DeviceId = _target.NestDeviceId,
            Sdp = offerSdp,
            Local = false,
            StreamContext = JoinStreamRequest.Types.StreamContext.Default,
            RequestedVideoResolution = JoinStreamRequest.Types.VideoResolution.Standard,
        }, JoinStreamResponse.Parser, ct);
        _streamId = join.StreamId;
        long joinedMs = clock.ElapsedMilliseconds;

        if (string.IsNullOrEmpty(join.Sdp) || string.IsNullOrEmpty(_streamId))
        {
            throw new TalkbackUnavailableException(TalkbackReasons.CameraError,
                $"The camera did not answer the stream request (response '{join.ResponseType}').");
        }

        _logger.LogDebug("Talkback stream for room {RoomId}: answer SDP\n{Sdp}", _target.RoomId, join.Sdp);
        var (cleanedSdp, candidates) = NestSdp.SplitCandidates(join.Sdp);
        _logger.LogDebug("Talkback stream for room {RoomId}: camera answer media {Media}", _target.RoomId,
            string.Join(", ", cleanedSdp.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("m=") || l is "a=sendrecv" or "a=recvonly" or "a=sendonly" or "a=inactive")));
        var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = cleanedSdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new TalkbackUnavailableException(TalkbackReasons.CameraError, $"The camera's stream answer was rejected: {result}.");
        }

        int added = 0;
        foreach (var (candidate, mid, index) in candidates)
        {
            if (!NestSdp.TryNormalizeIceCandidate(candidate, out var normalized, out _)) continue;
            try
            {
                pc.addIceCandidate(new RTCIceCandidateInit { candidate = normalized, sdpMid = mid, sdpMLineIndex = index });
                added++;
            }
            catch (FormatException)
            {
            }
        }

        var timeout = Task.Delay(ConnectTimeout, ct);
        if (await Task.WhenAny(connected.Task, timeout) != connected.Task || !connected.Task.Result)
        {
            ct.ThrowIfCancellationRequested();
            throw new TalkbackUnavailableException(TalkbackReasons.CameraError, "The camera stream did not connect.");
        }

        _logger.LogInformation(
            "Talkback stream for room {RoomId} open in {TotalMs} ms (wake {WakeMs} ms, join {JoinMs} ms, connect {ConnectMs} ms, candidates {Added}/{Count})",
            _target.RoomId, clock.ElapsedMilliseconds, wokeMs, joinedMs - wokeMs, clock.ElapsedMilliseconds - joinedMs,
            added, candidates.Count);

        _ = Task.Run(() => SendLoopAsync(_sendLoopCts.Token));
    }

    public async Task StartTalkbackAsync(CancellationToken ct)
    {
        _talkerFrames.Clear();
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _sentAllFrames, 0);
        _cameraLevelBeforeTalk = TakeCameraLevel();
        await SendTalkbackAsync(SendTalkbackRequest.Types.TalkbackCommand.CommandStart, ct);
    }

    public async Task StopTalkbackAsync(CancellationToken ct)
    {
        _talkerFrames.Clear();
        _logger.LogDebug(
            "Talkback stream for room {RoomId}: {Frames} talker frames of {AllFrames} sent; camera RTP received audio {Audio} video {Video}; camera mic {Before:0.0} dBFS before, {During:0.0} dBFS while talking",
            _target.RoomId, Interlocked.Read(ref _framesSent), Interlocked.Read(ref _sentAllFrames), Interlocked.Read(ref _cameraAudioPackets),
            Interlocked.Read(ref _cameraVideoPackets), _cameraLevelBeforeTalk, TakeCameraLevel());
        await SendTalkbackAsync(SendTalkbackRequest.Types.TalkbackCommand.CommandStop, ct);
    }

    /// <summary>
    /// Diagnostics only: what reaches us from the camera, and how loud its microphone is. The
    /// camera hears its own speaker, so a louder level while talking means the voice played.
    /// </summary>
    private void OnCameraRtp(SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media == SDPMediaTypesEnum.video)
        {
            Interlocked.Increment(ref _cameraVideoPackets);
            return;
        }
        if (media != SDPMediaTypesEnum.audio) return;

        Interlocked.Increment(ref _cameraAudioPackets);
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        try
        {
            short[] pcm = _cameraDecoder.DecodeAudio(packet.Payload, _opus);
            double energy = 0;
            foreach (short s in pcm) energy += (double)s * s;
            lock (_cameraLevelLock)
            {
                _cameraEnergy += energy;
                _cameraSamples += pcm.Length;
            }
        }
        catch (Exception)
        {
            // A frame that doesn't decode only skews a diagnostic.
        }
    }

    private double TakeCameraLevel()
    {
        lock (_cameraLevelLock)
        {
            double rms = _cameraSamples == 0 ? 0 : Math.Sqrt(_cameraEnergy / _cameraSamples) / 32768.0;
            _cameraEnergy = 0;
            _cameraSamples = 0;
            return rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
        }
    }

    private async Task SendTalkbackAsync(SendTalkbackRequest.Types.TalkbackCommand command, CancellationToken ct)
    {
        var response = await _foyer.CallAsync("CameraService", "SendTalkback", new SendTalkbackRequest
        {
            GoogleDeviceId = _target.GoogleUuid,
            StreamId = _streamId ?? string.Empty,
            Command = command,
        }, SendTalkbackResponse.Parser, ct);
        _logger.LogDebug("Talkback stream for room {RoomId}: SendTalkback {Command} status {Status}",
            _target.RoomId, command, response.Status);
    }

    public async Task ExtendAsync(CancellationToken ct)
    {
        var response = await _foyer.CallAsync("CameraService", "JoinStream", new JoinStreamRequest
        {
            Command = "extend",
            DeviceId = _target.NestDeviceId,
            StreamId = _streamId ?? string.Empty,
        }, JoinStreamResponse.Parser, ct);

        if (response.StreamExtensionStatus == JoinStreamResponse.Types.StreamExtensionStatus.StatusStreamNotExtended)
        {
            throw new TalkbackUnavailableException(TalkbackReasons.CameraError, "The camera refused to extend the stream.");
        }
    }

    public void SendPcm(short[] pcm)
    {
        _talkerFrames.Enqueue(pcm);
        while (_talkerFrames.Count > MaxQueuedFrames && _talkerFrames.TryDequeue(out _))
        {
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        int channels = _opus.ChannelCount;
        var silence = new short[FrameSamples * channels];
        var clock = Stopwatch.StartNew();
        long sentSamples = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long dueSamples = (long)(clock.Elapsed.TotalSeconds * _opus.ClockRate);
                if (dueSamples - sentSamples > MaxCatchUpSamples) sentSamples = dueSamples - FrameSamples;

                while (sentSamples < dueSamples)
                {
                    bool fromTalker = _talkerFrames.TryDequeue(out var frame);
                    frame = fromTalker ? frame! : silence;
                    SendFrame(frame, fromTalker);
                    sentSamples += frame.Length / channels;
                }

                double untilNextMs = (sentSamples - dueSamples) * 1000.0 / _opus.ClockRate;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, untilNextMs)), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Talkback audio sender for room {RoomId} stopped: {Error}", _target.RoomId, ex.Message);
        }
    }

    private void SendFrame(short[] pcm, bool fromTalker)
    {
        var pc = _pc;
        if (pc == null || pc.connectionState != RTCPeerConnectionState.connected) return;

        byte[] encoded = _encoder.EncodeAudio(pcm, _opus);
        if (encoded.Length == 0) return;

        // The level of the PCM actually encoded (talker frames are already past the session's gain).
        TalkbackAudioLevel.SetForNextPacket(pc.AudioStream, pcm);
        pc.SendAudio((uint)(pcm.Length / _opus.ChannelCount), encoded);
        Interlocked.Increment(ref _sentAllFrames);
        if (fromTalker) Interlocked.Increment(ref _framesSent);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _sendLoopCts.Cancel();

        if (_streamId != null)
        {
            using var cts = new CancellationTokenSource(CloseTimeout);
            try
            {
                await _foyer.CallAsync("CameraService", "JoinStream", new JoinStreamRequest
                {
                    Command = "end",
                    DeviceId = _target.NestDeviceId,
                    StreamId = _streamId,
                    EndStreamReason = JoinStreamRequest.Types.EndStreamReason.ReasonUserExitedSession,
                }, JoinStreamResponse.Parser, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("JoinStream end failed for room {RoomId}: {Error}", _target.RoomId, ex.Message);
            }
        }

        try
        {
            _pc?.close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Closing talkback peer connection for room {RoomId} failed: {Error}", _target.RoomId, ex.Message);
        }
    }
}
