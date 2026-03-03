using System;
using System.Collections.Concurrent;
using BabyMonitarr.Backend.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace BabyMonitarr.Backend.Services
{
    public interface IVideoWebRtcService
    {
        Task<string> CreateVideoPeerConnection(string peerId, int roomId, string? hostHint);
        Task SetVideoRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc);
        Task AddVideoIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate);
        Task CloseVideoPeerConnection(string peerId, int roomId);
        Task CloseAllVideoPeerConnections(string peerId);
    }

    public class VideoWebRtcService : IVideoWebRtcService, IDisposable
    {
        private const int SourceCodecLookupTimeoutMs = 5000;
        private const string H264Fmtp =
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";
        private const string H265Fmtp = "profile-id=1";
        private const string Vp8Fmtp = "max-fs=12288;max-fr=60";

        private readonly ILogger<VideoWebRtcService> _logger;
        private readonly IVideoStreamingService _videoStreamingService;
        private readonly IHubContext<AudioStreamHub> _hubContext;
        private readonly IWebRtcConfigService _webRtcConfigService;

        // Peer connections keyed by "{peerId}_v_{roomId}".
        private readonly ConcurrentDictionary<string, RTCPeerConnection> _peerConnections = new();
        private readonly ConcurrentDictionary<string, List<RTCIceCandidateInit>> _pendingIceCandidates = new();
        private readonly ConcurrentDictionary<string, VideoPassthroughCodec> _expectedCodecs = new();
        private readonly ConcurrentDictionary<string, VideoCodecsEnum> _negotiatedCodecs = new();

        // Track which rooms each peer is subscribed to, and the handler reference for unsubscription.
        private readonly ConcurrentDictionary<string, Action<VideoFrameEventArgs>> _frameHandlers = new();

        public VideoWebRtcService(
            ILogger<VideoWebRtcService> logger,
            IVideoStreamingService videoStreamingService,
            IHubContext<AudioStreamHub> hubContext,
            IWebRtcConfigService webRtcConfigService)
        {
            _logger = logger;
            _videoStreamingService = videoStreamingService;
            _hubContext = hubContext;
            _webRtcConfigService = webRtcConfigService;
        }

        private static string GetConnectionKey(string peerId, int roomId) => $"{peerId}_v_{roomId}";

        public async Task<string> CreateVideoPeerConnection(string peerId, int roomId, string? hostHint)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Video peer connection already exists for {Key}, closing existing", key);
                await CloseVideoPeerConnection(peerId, roomId);
            }

            _logger.LogInformation(
                "Creating video WebRTC peer connection for peer {PeerId}, room {RoomId}",
                peerId,
                roomId);

            RoomVideoSourceInfo sourceInfo;
            using (var lookupTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(SourceCodecLookupTimeoutMs)))
            {
                try
                {
                    sourceInfo = await _videoStreamingService.GetRoomVideoSourceInfoAsync(roomId, lookupTimeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                    throw new HubException(
                        $"Timed out waiting for video source codec in room {roomId}. " +
                        "Unable to create a passthrough-only WebRTC offer.");
                }
            }

            if (!sourceInfo.IsSupported || sourceInfo.PassthroughCodec is null)
            {
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw new HubException(
                    sourceInfo.FailureReason ??
                    $"Video source codec '{sourceInfo.SourceCodecName}' is not supported for passthrough.");
            }

            VideoPassthroughCodec expectedCodec = sourceInfo.PassthroughCodec.Value;
            var settings = _webRtcConfigService.GetPeerConnectionSettings(hostHint);
            var advertisedAddress = settings.AdvertisedAddress;
            var pc = new RTCPeerConnection(settings.Configuration, settings.BindPort, settings.PortRange, false);

            // ICE candidate handler - send to client via SignalR.
            pc.onicecandidate += candidate =>
            {
                if (candidate != null)
                {
                    _ = SendVideoIceCandidate(peerId, roomId, candidate);

                    var advertisedCandidate = _webRtcConfigService.CreateAdvertisedCandidate(candidate, advertisedAddress);
                    if (advertisedCandidate != null)
                    {
                        _logger.LogDebug(
                            "Sending mapped video ICE candidate for {Key}: {SourceAddress}:{SourcePort} -> {MappedAddress}:{MappedPort}",
                            key,
                            candidate.address,
                            candidate.port,
                            advertisedCandidate.address,
                            advertisedCandidate.port);

                        _ = SendVideoIceCandidate(peerId, roomId, advertisedCandidate);
                    }
                }
            };

            pc.onicecandidateerror += (candidate, error) =>
            {
                _logger.LogWarning(
                    "Video ICE candidate error for {Key}. Error={Error}, Candidate={Candidate}",
                    key,
                    error,
                    candidate?.candidate);
            };

            pc.onconnectionstatechange += state =>
            {
                _logger.LogInformation(
                    "Video connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state,
                    peerId,
                    roomId);

                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Video peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseVideoPeerConnection(peerId, roomId));
                }
            };

            pc.OnVideoFormatsNegotiated += videoFormats =>
            {
                if (videoFormats.Count == 0)
                {
                    _logger.LogWarning(
                        "No video formats were negotiated for {Key}. Expected source codec {ExpectedCodec}.",
                        key,
                        expectedCodec);
                    return;
                }

                var selected = videoFormats[0];
                _negotiatedCodecs[key] = selected.Codec;
                _logger.LogInformation(
                    "Video formats negotiated for {Key}: {Formats}. Selected: {SelectedCodec}. Expected source codec: {ExpectedCodec}",
                    key,
                    string.Join(", ", videoFormats.Select(f => f.FormatName)),
                    selected.Codec,
                    expectedCodec);
            };

            var videoTrack = new MediaStreamTrack(GetVideoFormatsForCodec(expectedCodec), MediaStreamStatusEnum.SendOnly);
            pc.addTrack(videoTrack);

            _expectedCodecs[key] = expectedCodec;
            _peerConnections.TryAdd(key, pc);

            try
            {
                var offerInit = pc.createOffer(null);
                await pc.setLocalDescription(offerInit);

                string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(sdp))
                {
                    _logger.LogError("Failed to create video SDP offer for {Key}", key);
                    await CloseVideoPeerConnection(peerId, roomId);
                    _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                    return string.Empty;
                }

                Action<VideoFrameEventArgs> frameHandler = args => SendVideoFrameToPeer(key, args);
                _frameHandlers.TryAdd(key, frameHandler);
                _videoStreamingService.SubscribeToRoom(roomId, frameHandler);

                _logger.LogInformation(
                    "Video peer connection created for {Key} with source codec {Codec}",
                    key,
                    expectedCodec);
                return sdp;
            }
            catch
            {
                await CloseVideoPeerConnection(peerId, roomId);
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw;
            }
        }

        public Task SetVideoRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (!_peerConnections.TryGetValue(key, out var pc))
            {
                throw new KeyNotFoundException($"No video peer connection found for {key}");
            }

            pc.setRemoteDescription(desc);
            _logger.LogInformation("Set video remote description for {Key}, type: {Type}", key, desc.type);

            EnsureNegotiatedCodec(key, roomId);

            if (_pendingIceCandidates.TryRemove(key, out var pendingCandidates))
            {
                _logger.LogInformation(
                    "Processing {Count} queued video ICE candidates for {Key}",
                    pendingCandidates.Count,
                    key);
                foreach (var candidate in pendingCandidates)
                {
                    try
                    {
                        pc.addIceCandidate(candidate);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error adding queued video ICE candidate for {Key}", key);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task AddVideoIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (!_peerConnections.TryGetValue(key, out var pc))
            {
                throw new KeyNotFoundException($"No video peer connection found for {key}");
            }

            if (pc.signalingState == RTCSignalingState.stable)
            {
                pc.addIceCandidate(candidate);
            }
            else
            {
                _pendingIceCandidates.AddOrUpdate(
                    key,
                    new List<RTCIceCandidateInit> { candidate },
                    (_, list) =>
                    {
                        list.Add(candidate);
                        return list;
                    });
            }

            return Task.CompletedTask;
        }

        public Task CloseVideoPeerConnection(string peerId, int roomId)
        {
            string key = GetConnectionKey(peerId, roomId);
            CloseConnection(key, roomId);
            return Task.CompletedTask;
        }

        public Task CloseAllVideoPeerConnections(string peerId)
        {
            string prefix = $"{peerId}_v_";
            foreach (var key in _peerConnections.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                if (int.TryParse(key.Substring(prefix.Length), out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }
            return Task.CompletedTask;
        }

        private void EnsureNegotiatedCodec(string key, int roomId)
        {
            if (!_expectedCodecs.TryGetValue(key, out var expectedCodec))
            {
                return;
            }

            if (!_negotiatedCodecs.TryGetValue(key, out var negotiatedCodec))
            {
                string message =
                    $"Video codec negotiation failed for room {roomId}. " +
                    $"Source codec '{expectedCodec}' is not supported by the WebRTC client.";

                _logger.LogWarning("No negotiated video codec for {Key}. {Message}", key, message);
                CloseConnection(key, roomId);
                throw new HubException(message);
            }

            VideoCodecsEnum expectedNegotiatedCodec = ToVideoCodecsEnum(expectedCodec);
            if (negotiatedCodec != expectedNegotiatedCodec)
            {
                string message =
                    $"Video codec negotiation failed for room {roomId}. " +
                    $"Source codec '{expectedCodec}' did not match negotiated codec '{negotiatedCodec}'.";

                _logger.LogWarning(
                    "Negotiated video codec mismatch for {Key}. Expected={Expected}, Negotiated={Negotiated}",
                    key,
                    expectedNegotiatedCodec,
                    negotiatedCodec);

                CloseConnection(key, roomId);
                throw new HubException(message);
            }
        }

        private void CloseConnection(string key, int roomId)
        {
            _logger.LogInformation("Closing video peer connection {Key}", key);

            if (_frameHandlers.TryRemove(key, out var handler))
            {
                _videoStreamingService.UnsubscribeFromRoom(roomId, handler);
            }

            _pendingIceCandidates.TryRemove(key, out _);
            _expectedCodecs.TryRemove(key, out _);
            _negotiatedCodecs.TryRemove(key, out _);

            if (_peerConnections.TryRemove(key, out var pc))
            {
                try
                {
                    pc.Close("Video peer connection closed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing video peer connection {Key}", key);
                }
            }

            _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
        }

        private void SendVideoFrameToPeer(string key, VideoFrameEventArgs args)
        {
            if (!_peerConnections.TryGetValue(key, out var pc)) return;
            if (pc.connectionState != RTCPeerConnectionState.connected) return;
            if (!_expectedCodecs.TryGetValue(key, out var expectedCodec)) return;
            if (!_negotiatedCodecs.TryGetValue(key, out var negotiatedCodec)) return;

            VideoCodecsEnum expectedNegotiatedCodec = ToVideoCodecsEnum(expectedCodec);
            if (negotiatedCodec != expectedNegotiatedCodec)
            {
                return;
            }

            if (args.Codec != expectedCodec)
            {
                _logger.LogWarning(
                    "Closing video peer {Key}: runtime source frame codec {FrameCodec} does not match expected codec {ExpectedCodec}",
                    key,
                    args.Codec,
                    expectedCodec);

                if (TryGetRoomIdFromConnectionKey(key, out int roomId))
                {
                    CloseConnection(key, roomId);
                }

                return;
            }

            if (args.EncodedData.Length == 0)
            {
                return;
            }

            try
            {
                pc.SendVideo(args.DurationRtpUnits, args.EncodedData);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error sending passthrough video to peer {Key}: {Error}", key, ex.Message);
            }
        }

        private static VideoCodecsEnum ToVideoCodecsEnum(VideoPassthroughCodec codec) =>
            codec switch
            {
                VideoPassthroughCodec.H264 => VideoCodecsEnum.H264,
                VideoPassthroughCodec.H265 => VideoCodecsEnum.H265,
                VideoPassthroughCodec.VP8 => VideoCodecsEnum.VP8,
                _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported passthrough codec.")
            };

        private static List<VideoFormat> GetVideoFormatsForCodec(VideoPassthroughCodec codec)
        {
            switch (codec)
            {
                case VideoPassthroughCodec.H264:
                    return new List<VideoFormat>
                    {
                        new(VideoCodecsEnum.H264, 96, 90000, H264Fmtp)
                    };
                case VideoPassthroughCodec.H265:
                    return new List<VideoFormat>
                    {
                        new(VideoCodecsEnum.H265, 96, 90000, H265Fmtp)
                    };
                case VideoPassthroughCodec.VP8:
                    return new List<VideoFormat>
                    {
                        new(VideoCodecsEnum.VP8, 96, 90000, Vp8Fmtp)
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported passthrough codec.");
            }
        }

        private static bool TryGetRoomIdFromConnectionKey(string key, out int roomId)
        {
            roomId = 0;
            var parts = key.Split("_v_");
            return parts.Length == 2 && int.TryParse(parts[1], out roomId);
        }

        private Task SendVideoIceCandidate(string peerId, int roomId, RTCIceCandidate candidate)
        {
            return _hubContext.Clients.Client(peerId).SendAsync(
                "ReceiveVideoIceCandidate",
                roomId,
                candidate.candidate,
                candidate.sdpMid ?? string.Empty,
                candidate.sdpMLineIndex);
        }

        public void Dispose()
        {
            foreach (var key in _peerConnections.Keys.ToList())
            {
                var parts = key.Split("_v_");
                if (parts.Length == 2 && int.TryParse(parts[1], out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }

            _peerConnections.Clear();
            _frameHandlers.Clear();
            _pendingIceCandidates.Clear();
            _expectedCodecs.Clear();
            _negotiatedCodecs.Clear();
        }
    }
}
