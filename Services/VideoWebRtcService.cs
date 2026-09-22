using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using BabyMonitarr.Backend.Ha;
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

        /// <summary>
        /// Answers a remote offer instead of making one. Used by Home Assistant, whose camera API
        /// supplies the offer. Separate from <see cref="CreateVideoPeerConnection"/> on purpose —
        /// the offer path is what every app client uses and is not touched by this.
        /// </summary>
        Task<string> CreateVideoAnswer(string peerId, int roomId, string offerSdp, string? hostHint);
        Task SetVideoRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc);
        Task AddVideoIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate);
        /// <summary>
        /// Tears the peer down and, for a Home Assistant peer, reports it with
        /// <paramref name="reason"/>. Exactly one <c>webrtc.closed</c> is sent per peer: the frame
        /// rides on the removal of the connection from the registry, so a second close — the
        /// re-entrant one <c>pc.Close()</c> itself triggers, say — sends nothing.
        /// </summary>
        Task CloseVideoPeerConnection(string peerId, int roomId, string? reason = null);
        Task CloseAllVideoPeerConnections(string peerId, string? reason = null);
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
        private readonly IHaPeerRouter _haPeerRouter;

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
            IWebRtcConfigService webRtcConfigService,
            IHaPeerRouter haPeerRouter)
        {
            _logger = logger;
            _videoStreamingService = videoStreamingService;
            _hubContext = hubContext;
            _webRtcConfigService = webRtcConfigService;
            _haPeerRouter = haPeerRouter;
        }

        private static string GetConnectionKey(string peerId, int roomId) => $"{peerId}_v_{roomId}";

        public async Task<string> CreateVideoPeerConnection(string peerId, int roomId, string? hostHint)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Video peer connection already exists for {Key}, closing existing", key);
                await CloseVideoPeerConnection(peerId, roomId, HaWebRtcCloseReasons.Superseded);
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

                // Both terminal states are handled: a peer that fails ICE and a peer the remote
                // end closed are equally gone, and a Home Assistant camera must be told either way.
                // The close is scoped to this exact peer object, never to whatever occupies the key
                // when the queued task runs: a supersede re-registers a new peer under the same key.
                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Video peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerFailed, pc));
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerClosed, pc));
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
                    await CloseVideoPeerConnection(peerId, roomId, HaWebRtcCloseReasons.SetupFailed);
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
                await CloseVideoPeerConnection(peerId, roomId, HaWebRtcCloseReasons.SetupFailed);
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw;
            }
        }

        /// <summary>
        /// Builds an answer to <paramref name="offerSdp"/> carrying only the room's passthrough
        /// codec, taken from the remote offer so the payload id and fmtp are the ones the remote
        /// actually proposed.
        /// </summary>
        public async Task<string> CreateVideoAnswer(string peerId, int roomId, string offerSdp, string? hostHint)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Video peer connection already exists for {Key}, closing existing", key);
                await CloseVideoPeerConnection(peerId, roomId, HaWebRtcCloseReasons.Superseded);
            }

            _logger.LogInformation(
                "Answering video WebRTC offer for peer {PeerId}, room {RoomId}", peerId, roomId);

            // Same source-of-truth as the offer path: VideoCodecProbeService decides the codec.
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
                    throw new InvalidOperationException(
                        $"Timed out waiting for the video source codec in room {roomId}. " +
                        "Unable to answer with a passthrough-only description.");
                }
            }

            if (!sourceInfo.IsSupported || sourceInfo.PassthroughCodec is null)
            {
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw new InvalidOperationException(
                    sourceInfo.FailureReason ??
                    $"Video source codec '{sourceInfo.SourceCodecName}' is not supported for passthrough.");
            }

            VideoPassthroughCodec expectedCodec = sourceInfo.PassthroughCodec.Value;

            // The remote offer lists codecs the RTP forwarder cannot produce. Pick ours out of it,
            // or fail: transcoding is not an option here and a mismatched answer would send frames
            // the remote cannot decode.
            var offeredFormat = SelectPassthroughFormatFromOffer(offerSdp, expectedCodec, out string offeredSummary);
            if (offeredFormat == null)
            {
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw new WebRtcCodecMismatchException(
                    $"The WebRTC offer does not include the room's video codec '{expectedCodec}'. " +
                    $"The offer listed: {offeredSummary}. " +
                    "The camera is forwarded without transcoding, so the offer must include this codec.");
            }

            var settings = _webRtcConfigService.GetPeerConnectionSettings(hostHint);
            var advertisedAddress = settings.AdvertisedAddress;
            var pc = new RTCPeerConnection(settings.Configuration, settings.BindPort, settings.PortRange, false);

            pc.onicecandidate += candidate =>
            {
                if (candidate == null) return;

                _ = SendVideoIceCandidate(peerId, roomId, candidate);

                var advertisedCandidate = _webRtcConfigService.CreateAdvertisedCandidate(candidate, advertisedAddress);
                if (advertisedCandidate != null)
                {
                    _ = SendVideoIceCandidate(peerId, roomId, advertisedCandidate);
                }
            };

            pc.onicecandidateerror += (candidate, error) =>
            {
                _logger.LogWarning(
                    "Video ICE candidate error for {Key}. Error={Error}, Candidate={Candidate}",
                    key, error, candidate?.candidate);
            };

            pc.onconnectionstatechange += state =>
            {
                _logger.LogInformation(
                    "Video connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state, peerId, roomId);

                // Both terminal states are handled: a peer that fails ICE and a peer the remote
                // end closed are equally gone, and a Home Assistant camera must be told either way.
                // The close is scoped to this exact peer object, never to whatever occupies the key
                // when the queued task runs: a supersede re-registers a new peer under the same key.
                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Video peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerFailed, pc));
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerClosed, pc));
                }
            };

            pc.OnVideoFormatsNegotiated += videoFormats =>
            {
                if (videoFormats.Count == 0)
                {
                    _logger.LogWarning(
                        "No video formats were negotiated for {Key}. Expected source codec {ExpectedCodec}.",
                        key, expectedCodec);
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

            // Exactly one format, echoing the remote's own payload id and fmtp for that codec.
            var videoTrack = new MediaStreamTrack(
                new List<VideoFormat> { offeredFormat.Value }, MediaStreamStatusEnum.SendOnly);
            pc.addTrack(videoTrack);

            _expectedCodecs[key] = expectedCodec;
            _peerConnections.TryAdd(key, pc);

            try
            {
                var setResult = pc.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = offerSdp
                });

                if (setResult != SetDescriptionResultEnum.OK)
                {
                    throw new InvalidOperationException(
                        $"The WebRTC offer for room {roomId} was rejected: {setResult}.");
                }

                EnsureNegotiatedCodec(key, roomId);

                var answerInit = pc.createAnswer(null);
                await pc.setLocalDescription(answerInit);

                string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(sdp))
                {
                    throw new InvalidOperationException(
                        $"Failed to create a video SDP answer for room {roomId}.");
                }

                Action<VideoFrameEventArgs> frameHandler = args => SendVideoFrameToPeer(key, args);
                _frameHandlers.TryAdd(key, frameHandler);
                _videoStreamingService.SubscribeToRoom(roomId, frameHandler);

                _logger.LogInformation(
                    "Video answer created for {Key} with source codec {Codec} on payload {PayloadId}",
                    key, expectedCodec, offeredFormat.Value.FormatID);
                return sdp;
            }
            catch
            {
                await CloseVideoPeerConnection(peerId, roomId, HaWebRtcCloseReasons.SetupFailed);
                _videoStreamingService.EnsureReaderStoppedIfNoSubscribers(roomId);
                throw;
            }
        }

        /// <summary>
        /// Finds the remote offer's entry for <paramref name="codec"/>, preserving its payload id
        /// and fmtp. <paramref name="offeredSummary"/> describes what the offer did contain, for
        /// the error message when there is no match.
        /// </summary>
        private static VideoFormat? SelectPassthroughFormatFromOffer(
            string offerSdp, VideoPassthroughCodec codec, out string offeredSummary)
        {
            offeredSummary = "nothing";

            SDP offer;
            try
            {
                offer = SDP.ParseSDPDescription(offerSdp);
            }
            catch (Exception)
            {
                offeredSummary = "an SDP that could not be parsed";
                return null;
            }

            var videoAnnouncement = offer.Media
                .FirstOrDefault(m => m.Media == SDPMediaTypesEnum.video);
            if (videoAnnouncement?.MediaFormats == null || videoAnnouncement.MediaFormats.Count == 0)
            {
                offeredSummary = "no video media section";
                return null;
            }

            var formats = videoAnnouncement.MediaFormats.Values.ToList();
            offeredSummary = string.Join(", ", formats
                .Select(f => $"{f.Name()}({f.ID})")
                .Where(n => !string.IsNullOrWhiteSpace(n)));

            string wanted = codec switch
            {
                VideoPassthroughCodec.H264 => "H264",
                VideoPassthroughCodec.H265 => "H265",
                VideoPassthroughCodec.VP8 => "VP8",
                _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported passthrough codec.")
            };

            foreach (var format in formats)
            {
                string? name = format.Name();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                // H.265 is sometimes announced as HEVC; Name() normalises the common spellings.
                int clockRate = format.ClockRate() > 0 ? format.ClockRate() : 90000;
                return new VideoFormat(
                    ToVideoCodecsEnum(codec), format.ID, clockRate, format.Fmtp ?? string.Empty);
            }

            return null;
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

        public Task CloseVideoPeerConnection(string peerId, int roomId, string? reason = null)
        {
            string key = GetConnectionKey(peerId, roomId);
            CloseConnection(key, roomId, reason);
            return Task.CompletedTask;
        }

        public Task CloseAllVideoPeerConnections(string peerId, string? reason = null)
        {
            string prefix = $"{peerId}_v_";
            foreach (var key in _peerConnections.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                if (int.TryParse(key.Substring(prefix.Length), out int roomId))
                {
                    CloseConnection(key, roomId, reason);
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
                CloseConnection(key, roomId, HaWebRtcCloseReasons.CodecMismatch);
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

                CloseConnection(key, roomId, HaWebRtcCloseReasons.CodecMismatch);
                throw new HubException(message);
            }
        }

        /// <param name="expectedPc">
        /// When given, the close applies only to that exact peer object: if the key no longer holds
        /// it, nothing is torn down and nothing is reported. Connection-state callbacks pass it,
        /// because their close is queued and the key may already belong to a superseding peer by
        /// the time it runs. Callers that mean "whatever is on this key" pass null.
        /// </param>
        private void CloseConnection(string key, int roomId, string? reason = null, RTCPeerConnection? expectedPc = null)
        {
            if (expectedPc != null &&
                (!_peerConnections.TryGetValue(key, out var registered) || !ReferenceEquals(registered, expectedPc)))
            {
                // Already removed by whoever closed it, or replaced by a newer peer. Either way this
                // peer's teardown is not ours to do, and the newer peer must not be touched.
                return;
            }

            _logger.LogInformation("Closing video peer connection {Key}", key);

            if (_frameHandlers.TryRemove(key, out var handler))
            {
                _videoStreamingService.UnsubscribeFromRoom(roomId, handler);
            }

            _pendingIceCandidates.TryRemove(key, out _);
            _expectedCodecs.TryRemove(key, out _);
            _negotiatedCodecs.TryRemove(key, out _);

            // Removing the peer is the exactly-once gate for the closed notification: pc.Close()
            // below re-enters here through onconnectionstatechange, and so does a client stop that
            // races the failure handler. Only the caller that actually removed it reports.
            // With expectedPc the removal is a compare-and-remove, so a supersede that slipped in
            // after the identity check above still keeps its peer.
            if (TryRemovePeer(key, expectedPc, out var pc))
            {
                _haPeerRouter.TrySendClosed(
                    PeerIdFromKey(key, roomId),
                    HaProtocol.KindVideo,
                    roomId,
                    reason ?? HaWebRtcCloseReasons.ClosedByServer);

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

        /// <summary>
        /// Removes the peer registered under <paramref name="key"/>. With <paramref name="expectedPc"/>
        /// the removal is atomic and conditional on that exact instance still being the registered one.
        /// </summary>
        private bool TryRemovePeer(
            string key,
            RTCPeerConnection? expectedPc,
            [NotNullWhen(true)] out RTCPeerConnection? pc)
        {
            if (expectedPc == null)
            {
                return _peerConnections.TryRemove(key, out pc);
            }

            pc = expectedPc;
            return ((ICollection<KeyValuePair<string, RTCPeerConnection>>)_peerConnections)
                .Remove(new KeyValuePair<string, RTCPeerConnection>(key, expectedPc));
        }

        /// <summary>
        /// Recovers the peer id from a connection key. Split by length rather than by separator:
        /// a peer id may itself contain the separator, the room id suffix never does.
        /// </summary>
        private static string PeerIdFromKey(string key, int roomId)
        {
            string suffix = $"_v_{roomId}";
            return key.EndsWith(suffix, StringComparison.Ordinal)
                ? key.Substring(0, key.Length - suffix.Length)
                : key;
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
                    CloseConnection(key, roomId, HaWebRtcCloseReasons.SourceCodecChanged);
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
            // A Home Assistant peer signals over /ha/ws and has no SignalR connection to push to.
            if (_haPeerRouter.TrySendIceCandidate(
                    peerId, "video", roomId, candidate.candidate, candidate.sdpMid ?? string.Empty,
                    candidate.sdpMLineIndex))
            {
                return Task.CompletedTask;
            }

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
