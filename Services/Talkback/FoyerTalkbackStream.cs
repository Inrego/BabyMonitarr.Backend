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

    /// <summary>Encodes and sends interleaved stereo 48 kHz PCM.</summary>
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
public sealed class FoyerTalkbackStream : IFoyerTalkbackStream
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly TalkbackTarget _target;
    private readonly FoyerClient _foyer;
    private readonly ILogger _logger;
    private readonly AudioFormat _opus = new(AudioCodecsEnum.OPUS, NestStreamReader.OpusPayloadType, 48000, 2, "minptime=10;useinbandfec=1");
    private readonly AudioEncoder _encoder = new(includeOpus: true);
    private RTCPeerConnection? _pc;
    private string? _streamId;
    private long _framesSent;
    private int _disposed;

    public event Action<string>? Closed;

    public FoyerTalkbackStream(TalkbackTarget target, FoyerClient foyer, ILogger logger)
    {
        _target = target;
        _foyer = foyer;
        _logger = logger;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } },
            X_UseRtpFeedbackProfile = true,
        });
        _pc = pc;
        pc.addTrack(new MediaStreamTrack(new List<AudioFormat> { _opus }, MediaStreamStatusEnum.SendRecv));
        pc.addTrack(new MediaStreamTrack(
            new List<VideoFormat>
            {
                new(VideoCodecsEnum.H264, NestStreamReader.H264PayloadType, 90000,
                    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
            },
            MediaStreamStatusEnum.RecvOnly));
        await pc.createDataChannel("data", new RTCDataChannelInit());

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
            Sdp = offer.sdp.Replace("OPUS", "opus"),
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
    }

    public async Task StartTalkbackAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _framesSent, 0);
        await SendTalkbackAsync(SendTalkbackRequest.Types.TalkbackCommand.CommandStart, ct);
    }

    public async Task StopTalkbackAsync(CancellationToken ct)
    {
        _logger.LogDebug("Talkback stream for room {RoomId}: {Frames} audio frames sent to the camera",
            _target.RoomId, Interlocked.Read(ref _framesSent));
        await SendTalkbackAsync(SendTalkbackRequest.Types.TalkbackCommand.CommandStop, ct);
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
        var pc = _pc;
        if (pc == null || pc.connectionState != RTCPeerConnectionState.connected) return;

        byte[] encoded = _encoder.EncodeAudio(pcm, _opus);
        if (encoded.Length > 0)
        {
            pc.SendAudio((uint)(pcm.Length / _opus.ChannelCount), encoded);
            Interlocked.Increment(ref _framesSent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

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
