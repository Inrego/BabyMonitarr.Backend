using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using BabyMonitarr.Backend.Ha;
using BabyMonitarr.Backend.Hubs;

namespace BabyMonitarr.Backend.Services
{
    public interface IAudioWebRtcService
    {
        Task<string> CreateAudioPeerConnection(string peerId, int roomId, string? hostHint);

        /// <summary>
        /// Answers a remote offer instead of making one. Used by Home Assistant, which supplies
        /// the offer. Separate from <see cref="CreateAudioPeerConnection"/> on purpose — the offer
        /// path is what every app client uses and is not touched by this.
        /// </summary>
        Task<string> CreateAudioAnswer(string peerId, int roomId, string offerSdp, string? hostHint);
        Task SetAudioRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc);
        Task AddAudioIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate);
        /// <summary>
        /// Tears the peer down and, for a Home Assistant peer, reports it with
        /// <paramref name="reason"/>. Exactly one <c>webrtc.closed</c> is sent per peer: the frame
        /// rides on the removal of the connection from the registry, so a second close — the
        /// re-entrant one <c>pc.Close()</c> itself triggers, say — sends nothing.
        /// </summary>
        Task CloseAudioPeerConnection(string peerId, int roomId, string? reason = null);
        Task CloseAllAudioPeerConnections(string peerId, string? reason = null);
    }

    public class AudioWebRtcService : IAudioWebRtcService, IDisposable
    {
        private readonly ILogger<AudioWebRtcService> _logger;
        private readonly IAudioStreamingService _audioStreamingService;
        private readonly IHubContext<AudioStreamHub> _hubContext;
        private readonly IWebRtcConfigService _webRtcConfigService;
        private readonly IHaPeerRouter _haPeerRouter;
        private readonly ICastReceiverPeers _castReceiverPeers;

        // Peer connections keyed by "{peerId}_a_{roomId}"
        private readonly ConcurrentDictionary<string, RTCPeerConnection> _peerConnections = new();
        private readonly ConcurrentDictionary<string, AudioExtrasSource> _audioSources = new();
        private readonly ConcurrentDictionary<string, AudioEncoder> _audioEncoders = new();
        private readonly ConcurrentDictionary<string, AudioFormat> _negotiatedFormats = new();
        private readonly ConcurrentDictionary<string, RTCDataChannel> _dataChannels = new();
        private readonly ConcurrentDictionary<string, List<RTCIceCandidateInit>> _pendingIceCandidates = new();

        // Track frame handlers for unsubscription
        private readonly ConcurrentDictionary<string, Action<AudioFrameEventArgs>> _frameHandlers = new();

        // Throttle audio level data channel updates (~10/sec per connection)
        private readonly ConcurrentDictionary<string, DateTime> _lastLevelSent = new();
        private static readonly TimeSpan LevelSendInterval = TimeSpan.FromMilliseconds(100);

        public AudioWebRtcService(
            ILogger<AudioWebRtcService> logger,
            IAudioStreamingService audioStreamingService,
            IHubContext<AudioStreamHub> hubContext,
            IWebRtcConfigService webRtcConfigService,
            IHaPeerRouter haPeerRouter,
            ICastReceiverPeers castReceiverPeers)
        {
            _logger = logger;
            _audioStreamingService = audioStreamingService;
            _hubContext = hubContext;
            _webRtcConfigService = webRtcConfigService;
            _haPeerRouter = haPeerRouter;
            _castReceiverPeers = castReceiverPeers;

            // Subscribe to sound threshold events
            _audioStreamingService.SoundThresholdExceeded += OnSoundThresholdExceeded;
        }

        private static string GetConnectionKey(string peerId, int roomId) => $"{peerId}_a_{roomId}";

        public async Task<string> CreateAudioPeerConnection(string peerId, int roomId, string? hostHint)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Audio peer connection already exists for {Key}, closing existing", key);
                await CloseAudioPeerConnection(peerId, roomId, HaWebRtcCloseReasons.Superseded);
            }

            _logger.LogInformation("Creating audio WebRTC peer connection for peer {PeerId}, room {RoomId}", peerId, roomId);

            var settings = _webRtcConfigService.GetPeerConnectionSettings(hostHint);
            var advertisedAddress = settings.AdvertisedAddress;
            var pc = new RTCPeerConnection(settings.Configuration, settings.BindPort, settings.PortRange, false);

            // ICE candidate handler - send to client via SignalR
            pc.onicecandidate += (candidate) =>
            {
                if (candidate != null)
                {
                    _ = SendAudioIceCandidate(peerId, roomId, candidate);

                    var advertisedCandidate = _webRtcConfigService.CreateAdvertisedCandidate(candidate, advertisedAddress);
                    if (advertisedCandidate != null)
                    {
                        _logger.LogDebug(
                            "Sending mapped audio ICE candidate for {Key}: {SourceAddress}:{SourcePort} -> {MappedAddress}:{MappedPort}",
                            key,
                            candidate.address,
                            candidate.port,
                            advertisedCandidate.address,
                            advertisedCandidate.port);

                        _ = SendAudioIceCandidate(peerId, roomId, advertisedCandidate);
                    }
                }
            };

            pc.onicecandidateerror += (candidate, error) =>
            {
                _logger.LogWarning(
                    "Audio ICE candidate error for {Key}. Error={Error}, Candidate={Candidate}",
                    key,
                    error,
                    candidate?.candidate);
            };

            // Connection state handler
            pc.onconnectionstatechange += (state) =>
            {
                _logger.LogInformation("Audio connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state, peerId, roomId);

                // Both terminal states are handled: a peer that fails ICE and a peer the remote
                // end closed are equally gone, and a Home Assistant camera must be told either way.
                // The close is scoped to this exact peer object, never to whatever occupies the key
                // when the queued task runs: a supersede re-registers a new peer under the same key.
                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Audio peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerFailed, pc));
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerClosed, pc));
                }
            };

            // Add to connections FIRST
            _peerConnections.TryAdd(key, pc);

            // Create and add audio track
            bool isNestRoom = _audioStreamingService.IsNestRoom(roomId);
            try
            {
                if (isNestRoom)
                {
                    // Nest passthrough: Opus-only track ensures browser negotiates Opus,
                    // matching the raw Opus data we pass through from the Nest camera.
                    var opusFormat = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2,
                        "minptime=10;useinbandfec=1");
                    var audioTrack = new MediaStreamTrack(
                        new List<AudioFormat> { opusFormat }, MediaStreamStatusEnum.SendOnly);
                    pc.addTrack(audioTrack);

                    pc.OnAudioFormatsNegotiated += (audioFormats) =>
                    {
                        var selectedFormat = audioFormats.First();
                        _logger.LogInformation(
                            "Audio formats negotiated for {Key}: {Formats}. Selected: {Selected}",
                            key,
                            string.Join(", ", audioFormats.Select(f => f.FormatName)),
                            selectedFormat.FormatName);
                        _negotiatedFormats[key] = selectedFormat;
                    };
                }
                else
                {
                    // RTSP: multi-codec encoder
                    var audioEncoder = new AudioEncoder(includeOpus: true);
                    var audioSource = new AudioExtrasSource(audioEncoder,
                        new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
                    var audioTrack = new MediaStreamTrack(
                        audioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
                    pc.addTrack(audioTrack);

                    pc.OnAudioFormatsNegotiated += (audioFormats) =>
                    {
                        var selectedFormat = audioFormats.First();
                        _logger.LogInformation(
                            "Audio formats negotiated for {Key}: {Formats}. Selected: {Selected}",
                            key,
                            string.Join(", ", audioFormats.Select(f => f.FormatName)),
                            selectedFormat.FormatName);
                        audioSource.SetAudioSourceFormat(selectedFormat);
                        _negotiatedFormats[key] = selectedFormat;
                    };

                    _audioSources.TryAdd(key, audioSource);
                    _audioEncoders.TryAdd(key, audioEncoder);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding audio track for {Key}", key);
                _peerConnections.TryRemove(key, out _);
                pc.Close("Error adding audio track");
                throw;
            }

            // Create data channel for audio level updates
            await CreateDataChannel(key, pc);

            // Create SDP offer
            var offerInit = pc.createOffer(null);
            await pc.setLocalDescription(offerInit);

            string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sdp))
            {
                _logger.LogError("Failed to create audio SDP offer for {Key}", key);
                await CloseAudioPeerConnection(peerId, roomId, HaWebRtcCloseReasons.SetupFailed);
                return string.Empty;
            }

            // Subscribe to audio frames for this room
            Action<AudioFrameEventArgs> frameHandler = (args) =>
            {
                if (args.RawOpusData != null)
                {
                    // Nest passthrough: send raw Opus directly
                    SendRawAudioToPeer(key, args.RawOpusData, args.DurationRtpUnits);
                }
                else
                {
                    // RTSP: encode PCM to Opus
                    SendAudioDataToPeer(key, args.AudioData, args.SampleRate);
                }
                SendAudioLevelToPeer(key, args.AudioLevel, args.Timestamp);
            };
            _frameHandlers.TryAdd(key, frameHandler);
            _audioStreamingService.SubscribeToRoom(roomId, frameHandler);

            _logger.LogInformation("Audio peer connection created for {Key}", key);
            return sdp;
        }

        /// <summary>
        /// Builds an answer to <paramref name="offerSdp"/>. The answer carries only codecs this
        /// room can actually produce, using the payload ids the remote proposed.
        /// </summary>
        /// <remarks>
        /// No data channel is created here. The offer path adds one for audio-level updates, but a
        /// locally-added channel would not appear in an answer to an offer that has no
        /// <c>m=application</c> section. Home Assistant gets levels from <c>sound_level</c> instead.
        /// </remarks>
        public async Task<string> CreateAudioAnswer(string peerId, int roomId, string offerSdp, string? hostHint)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Audio peer connection already exists for {Key}, closing existing", key);
                await CloseAudioPeerConnection(peerId, roomId, HaWebRtcCloseReasons.Superseded);
            }

            _logger.LogInformation(
                "Answering audio WebRTC offer for peer {PeerId}, room {RoomId}", peerId, roomId);

            var offeredFormats = ParseOfferedAudioFormats(offerSdp, out string offeredSummary);
            if (offeredFormats.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The WebRTC offer for room {roomId} has no usable audio media section " +
                    $"(it listed: {offeredSummary}).");
            }

            var settings = _webRtcConfigService.GetPeerConnectionSettings(hostHint);
            var advertisedAddress = settings.AdvertisedAddress;
            var pc = new RTCPeerConnection(settings.Configuration, settings.BindPort, settings.PortRange, false);

            pc.onicecandidate += candidate =>
            {
                if (candidate == null) return;

                _ = SendAudioIceCandidate(peerId, roomId, candidate);

                var advertisedCandidate = _webRtcConfigService.CreateAdvertisedCandidate(candidate, advertisedAddress);
                if (advertisedCandidate != null)
                {
                    _ = SendAudioIceCandidate(peerId, roomId, advertisedCandidate);
                }
            };

            pc.onicecandidateerror += (candidate, error) =>
            {
                _logger.LogWarning(
                    "Audio ICE candidate error for {Key}. Error={Error}, Candidate={Candidate}",
                    key, error, candidate?.candidate);
            };

            pc.onconnectionstatechange += state =>
            {
                _logger.LogInformation(
                    "Audio connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state, peerId, roomId);

                // Both terminal states are handled: a peer that fails ICE and a peer the remote
                // end closed are equally gone, and a Home Assistant camera must be told either way.
                // The close is scoped to this exact peer object, never to whatever occupies the key
                // when the queued task runs: a supersede re-registers a new peer under the same key.
                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Audio peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerFailed, pc));
                }
                else if (state == RTCPeerConnectionState.closed)
                {
                    Task.Run(() => CloseConnection(key, roomId, HaWebRtcCloseReasons.PeerClosed, pc));
                }
            };

            _peerConnections.TryAdd(key, pc);

            bool isNestRoom = _audioStreamingService.IsNestRoom(roomId);
            try
            {
                if (isNestRoom)
                {
                    // Nest is raw Opus passthrough: nothing else can be answered.
                    var localOpus = new AudioFormat(
                        AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");
                    var matched = MatchAudioFormats(new List<AudioFormat> { localOpus }, offeredFormats);

                    if (matched.Count == 0)
                    {
                        throw new WebRtcCodecMismatchException(
                            $"The WebRTC offer does not include Opus, which room {roomId} requires: " +
                            $"its Nest audio is forwarded without transcoding. The offer listed: {offeredSummary}.");
                    }

                    pc.addTrack(new MediaStreamTrack(matched, MediaStreamStatusEnum.SendOnly));

                    pc.OnAudioFormatsNegotiated += audioFormats =>
                    {
                        var selectedFormat = audioFormats.First();
                        _logger.LogInformation(
                            "Audio formats negotiated for {Key}: {Formats}. Selected: {Selected}",
                            key,
                            string.Join(", ", audioFormats.Select(f => f.FormatName)),
                            selectedFormat.FormatName);
                        _negotiatedFormats[key] = selectedFormat;
                    };
                }
                else
                {
                    // RTSP audio is re-encoded, so anything the encoder supports and the offer
                    // listed is fair game.
                    var audioEncoder = new AudioEncoder(includeOpus: true);
                    var audioSource = new AudioExtrasSource(audioEncoder,
                        new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

                    var matched = MatchAudioFormats(audioSource.GetAudioSourceFormats(), offeredFormats);
                    if (matched.Count == 0)
                    {
                        audioSource.CloseAudio().Wait();
                        throw new WebRtcCodecMismatchException(
                            $"The WebRTC offer shares no audio codec with room {roomId}. " +
                            $"The offer listed: {offeredSummary}.");
                    }

                    pc.addTrack(new MediaStreamTrack(matched, MediaStreamStatusEnum.SendOnly));

                    pc.OnAudioFormatsNegotiated += audioFormats =>
                    {
                        var selectedFormat = audioFormats.First();
                        _logger.LogInformation(
                            "Audio formats negotiated for {Key}: {Formats}. Selected: {Selected}",
                            key,
                            string.Join(", ", audioFormats.Select(f => f.FormatName)),
                            selectedFormat.FormatName);
                        audioSource.SetAudioSourceFormat(selectedFormat);
                        _negotiatedFormats[key] = selectedFormat;
                    };

                    _audioSources.TryAdd(key, audioSource);
                    _audioEncoders.TryAdd(key, audioEncoder);
                }

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

                var answerInit = pc.createAnswer(null);
                await pc.setLocalDescription(answerInit);

                string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(sdp))
                {
                    throw new InvalidOperationException(
                        $"Failed to create an audio SDP answer for room {roomId}.");
                }

                Action<AudioFrameEventArgs> frameHandler = args =>
                {
                    if (args.RawOpusData != null)
                    {
                        SendRawAudioToPeer(key, args.RawOpusData, args.DurationRtpUnits);
                    }
                    else
                    {
                        SendAudioDataToPeer(key, args.AudioData, args.SampleRate);
                    }
                };
                _frameHandlers.TryAdd(key, frameHandler);
                _audioStreamingService.SubscribeToRoom(roomId, frameHandler);

                _logger.LogInformation("Audio answer created for {Key}", key);
                return sdp;
            }
            catch
            {
                await CloseAudioPeerConnection(peerId, roomId, HaWebRtcCloseReasons.SetupFailed);
                throw;
            }
        }

        /// <summary>The remote offer's audio formats, or an empty list when it has no audio section.</summary>
        private static List<SDPAudioVideoMediaFormat> ParseOfferedAudioFormats(
            string offerSdp, out string offeredSummary)
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
                return new List<SDPAudioVideoMediaFormat>();
            }

            var announcement = offer.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (announcement?.MediaFormats == null || announcement.MediaFormats.Count == 0)
            {
                offeredSummary = "no audio media section";
                return new List<SDPAudioVideoMediaFormat>();
            }

            var formats = announcement.MediaFormats.Values.ToList();
            offeredSummary = string.Join(", ", formats
                .Select(f => $"{f.Name()}({f.ID})")
                .Where(n => !string.IsNullOrWhiteSpace(n)));
            return formats;
        }

        /// <summary>
        /// Intersects what this room can send with what the remote offered, keeping the remote's
        /// payload id so the answer stays on the wire numbering the remote chose, and the local
        /// clock rate, channel count and parameters so the encoder gets what it expects.
        /// </summary>
        private static List<AudioFormat> MatchAudioFormats(
            List<AudioFormat> localFormats, List<SDPAudioVideoMediaFormat> offeredFormats)
        {
            var matched = new List<AudioFormat>();

            foreach (var offered in offeredFormats)
            {
                string? name = offered.Name();
                if (string.IsNullOrWhiteSpace(name)) continue;

                foreach (var local in localFormats)
                {
                    if (!string.Equals(local.Codec.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (matched.Any(m => m.Codec == local.Codec)) continue;

                    matched.Add(new AudioFormat(
                        local.Codec, offered.ID, local.ClockRate, local.ChannelCount, local.Parameters));
                    break;
                }
            }

            return matched;
        }

        public Task SetAudioRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (!_peerConnections.TryGetValue(key, out var pc))
            {
                throw new KeyNotFoundException($"No audio peer connection found for {key}");
            }

            pc.setRemoteDescription(desc);
            _logger.LogInformation("Set audio remote description for {Key}, type: {Type}", key, desc.type);

            // Process queued ICE candidates
            if (_pendingIceCandidates.TryRemove(key, out var pendingCandidates))
            {
                _logger.LogInformation("Processing {Count} queued audio ICE candidates for {Key}",
                    pendingCandidates.Count, key);
                foreach (var candidate in pendingCandidates)
                {
                    try
                    {
                        pc.addIceCandidate(candidate);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error adding queued audio ICE candidate for {Key}", key);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task AddAudioIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (!_peerConnections.TryGetValue(key, out var pc))
            {
                throw new KeyNotFoundException($"No audio peer connection found for {key}");
            }

            if (pc.signalingState == RTCSignalingState.stable)
            {
                pc.addIceCandidate(candidate);
            }
            else
            {
                _pendingIceCandidates.AddOrUpdate(key,
                    new List<RTCIceCandidateInit> { candidate },
                    (_, list) => { list.Add(candidate); return list; });
            }

            return Task.CompletedTask;
        }

        public Task CloseAudioPeerConnection(string peerId, int roomId, string? reason = null)
        {
            string key = GetConnectionKey(peerId, roomId);
            CloseConnection(key, roomId, reason);
            return Task.CompletedTask;
        }

        public Task CloseAllAudioPeerConnections(string peerId, string? reason = null)
        {
            string prefix = $"{peerId}_a_";
            foreach (var key in _peerConnections.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                if (int.TryParse(key.Substring(prefix.Length), out int roomId))
                {
                    CloseConnection(key, roomId, reason);
                }
            }
            return Task.CompletedTask;
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

            _logger.LogInformation("Closing audio peer connection {Key}", key);

            // Unsubscribe from audio frames
            if (_frameHandlers.TryRemove(key, out var handler))
            {
                _audioStreamingService.UnsubscribeFromRoom(roomId, handler);
            }

            _pendingIceCandidates.TryRemove(key, out _);
            _lastLevelSent.TryRemove(key, out _);
            _negotiatedFormats.TryRemove(key, out _);

            if (_audioEncoders.TryRemove(key, out _))
            {
                _logger.LogDebug("Removed audio encoder for {Key}", key);
            }

            if (_audioSources.TryRemove(key, out var audioSource))
            {
                audioSource.CloseAudio().Wait();
            }

            if (_dataChannels.TryRemove(key, out var dataChannel))
            {
                dataChannel.close();
            }

            // Removing the peer is the exactly-once gate for the closed notification: pc.Close()
            // below re-enters here through onconnectionstatechange, and so does a client stop that
            // races the failure handler. Only the caller that actually removed it reports.
            // With expectedPc the removal is a compare-and-remove, so a supersede that slipped in
            // after the identity check above still keeps its peer.
            if (TryRemovePeer(key, expectedPc, out var pc))
            {
                _haPeerRouter.TrySendClosed(
                    PeerIdFromKey(key, roomId),
                    HaProtocol.KindAudio,
                    roomId,
                    reason ?? HaWebRtcCloseReasons.ClosedByServer);
                // A supersede is the receiver's own renegotiation; telling it would restart it again.
                if (reason != HaWebRtcCloseReasons.Superseded)
                {
                    _castReceiverPeers.TrySendClosed(PeerIdFromKey(key, roomId), "audio");
                }

                try
                {
                    pc.Close("Audio peer connection closed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing audio peer connection {Key}", key);
                }
            }
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
            string suffix = $"_a_{roomId}";
            return key.EndsWith(suffix, StringComparison.Ordinal)
                ? key.Substring(0, key.Length - suffix.Length)
                : key;
        }

        private async Task CreateDataChannel(string key, RTCPeerConnection pc)
        {
            try
            {
                var dataChannel = await pc.createDataChannel("audioLevels", new RTCDataChannelInit());

                dataChannel.onopen += () =>
                {
                    _logger.LogInformation("Audio data channel opened for {Key}", key);
                };

                dataChannel.onclose += () =>
                {
                    _logger.LogInformation("Audio data channel closed for {Key}", key);
                    _dataChannels.TryRemove(key, out _);
                };

                dataChannel.onerror += (error) =>
                {
                    _logger.LogError("Audio data channel error for {Key}: {Error}", key, error);
                };

                _dataChannels.TryAdd(key, dataChannel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating audio data channel for {Key}", key);
            }
        }

        private Task SendAudioIceCandidate(string peerId, int roomId, RTCIceCandidate candidate)
        {
            // A Home Assistant peer signals over /ha/ws and has no SignalR connection to push to.
            if (_haPeerRouter.TrySendIceCandidate(
                    peerId, "audio", roomId, candidate.candidate, candidate.sdpMid ?? string.Empty,
                    candidate.sdpMLineIndex))
            {
                return Task.CompletedTask;
            }

            // A cast receiver page signals over CastReceiverHub.
            if (_castReceiverPeers.TrySendIceCandidate(
                    peerId, "audio", candidate.candidate, candidate.sdpMid ?? string.Empty,
                    candidate.sdpMLineIndex))
            {
                return Task.CompletedTask;
            }

            return _hubContext.Clients.Client(peerId).SendAsync(
                "ReceiveAudioIceCandidate",
                roomId,
                candidate.candidate,
                candidate.sdpMid ?? string.Empty,
                candidate.sdpMLineIndex);
        }

        private void SendRawAudioToPeer(string key, byte[] rawOpusData, uint durationRtpUnits)
        {
            if (!_peerConnections.TryGetValue(key, out var pc)) return;
            if (pc.connectionState != RTCPeerConnectionState.connected) return;

            if (_negotiatedFormats.TryGetValue(key, out var fmt) &&
                fmt.Codec != AudioCodecsEnum.OPUS)
            {
                _logger.LogWarning(
                    "Nest audio passthrough for {Key} requires Opus but negotiated {Format}; dropping frame",
                    key, fmt.FormatName);
                return;
            }

            try
            {
                pc.SendAudio(durationRtpUnits, rawOpusData);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error sending raw audio to peer {Key}: {Error}", key, ex.Message);
            }
        }

        private void SendAudioDataToPeer(string key, byte[] audioData, int sampleRate)
        {
            if (!_peerConnections.TryGetValue(key, out var pc)) return;
            if (pc.connectionState != RTCPeerConnectionState.connected) return;
            if (!_audioEncoders.TryGetValue(key, out var encoder)) return;
            if (!_negotiatedFormats.TryGetValue(key, out var audioFormat)) return;

            try
            {
                // Convert byte[] to short[] (16-bit PCM)
                short[] samples = new short[audioData.Length / 2];
                Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);

                // Resample if needed
                short[] resampledSamples = samples;
                if (sampleRate != audioFormat.ClockRate)
                {
                    resampledSamples = ResampleAudio(samples, sampleRate, audioFormat.ClockRate);
                }

                byte[] encodedSample = encoder.EncodeAudio(resampledSamples, audioFormat);

                if (encodedSample != null && encodedSample.Length > 0)
                {
                    uint durationRtpUnits = (uint)resampledSamples.Length;
                    pc.SendAudio(durationRtpUnits, encodedSample);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error encoding/sending audio to peer {Key}: {Error}", key, ex.Message);
            }
        }

        private void SendAudioLevelToPeer(string key, double audioLevel, DateTime timestamp)
        {
            // Throttle to ~10 updates/second
            var now = DateTime.UtcNow;
            if (_lastLevelSent.TryGetValue(key, out var lastSent) && (now - lastSent) < LevelSendInterval)
            {
                return;
            }
            _lastLevelSent[key] = now;

            if (!_dataChannels.TryGetValue(key, out var dataChannel)) return;
            if (dataChannel.readyState != RTCDataChannelState.open) return;

            try
            {
                var unixTimestamp = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
                var message = JsonSerializer.Serialize(new
                {
                    type = "audioLevel",
                    level = audioLevel,
                    timestamp = unixTimestamp
                });

                dataChannel.send(message);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error sending audio level via data channel for {Key}: {Error}", key, ex.Message);
            }
        }

        private void OnSoundThresholdExceeded(object? sender, SoundThresholdEventArgs e)
        {
            // Send sound alert to all peers subscribed to this room
            string roomSuffix = $"_a_{e.RoomId}";
            foreach (var kvp in _dataChannels)
            {
                if (!kvp.Key.EndsWith(roomSuffix)) continue;
                if (kvp.Value.readyState != RTCDataChannelState.open) continue;

                try
                {
                    var unixTimestamp = new DateTimeOffset(e.Timestamp).ToUnixTimeMilliseconds();
                    var message = JsonSerializer.Serialize(new
                    {
                        type = "soundAlert",
                        level = e.AudioLevel,
                        threshold = e.Threshold,
                        roomId = e.RoomId,
                        timestamp = unixTimestamp
                    });

                    kvp.Value.send(message);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error sending sound alert via data channel for {Key}: {Error}", kvp.Key, ex.Message);
                }
            }
        }

        private short[] ResampleAudio(short[] input, int inputRate, int outputRate)
        {
            if (inputRate == outputRate)
                return input;

            double ratio = (double)outputRate / inputRate;
            int outputLength = (int)(input.Length * ratio);
            short[] output = new short[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                double srcIndex = i / ratio;
                int srcIndexInt = (int)srcIndex;
                double frac = srcIndex - srcIndexInt;

                if (srcIndexInt + 1 < input.Length)
                {
                    output[i] = (short)(input[srcIndexInt] * (1 - frac) + input[srcIndexInt + 1] * frac);
                }
                else if (srcIndexInt < input.Length)
                {
                    output[i] = input[srcIndexInt];
                }
            }

            return output;
        }

        public void Dispose()
        {
            _audioStreamingService.SoundThresholdExceeded -= OnSoundThresholdExceeded;

            foreach (var key in _peerConnections.Keys.ToList())
            {
                var parts = key.Split("_a_");
                if (parts.Length == 2 && int.TryParse(parts[1], out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }

            _peerConnections.Clear();
            _audioEncoders.Clear();
            _audioSources.Clear();
            _negotiatedFormats.Clear();
            _dataChannels.Clear();
            _frameHandlers.Clear();
            _pendingIceCandidates.Clear();
            _lastLevelSent.Clear();
        }
    }
}
