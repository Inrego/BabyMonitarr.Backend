using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using BabyMonitarr.Backend.Hubs;

namespace BabyMonitarr.Backend.Services
{
    public interface IAudioWebRtcService
    {
        Task<string> CreateAudioPeerConnection(string peerId, int roomId);
        Task SetAudioRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc);
        Task AddAudioIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate);
        Task CloseAudioPeerConnection(string peerId, int roomId);
        Task CloseAllAudioPeerConnections(string peerId);
    }

    public class AudioWebRtcService : IAudioWebRtcService, IDisposable
    {
        private readonly ILogger<AudioWebRtcService> _logger;
        private readonly IAudioStreamingService _audioStreamingService;
        private readonly IHubContext<AudioStreamHub> _hubContext;

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
            IHubContext<AudioStreamHub> hubContext)
        {
            _logger = logger;
            _audioStreamingService = audioStreamingService;
            _hubContext = hubContext;

            // Subscribe to sound threshold events
            _audioStreamingService.SoundThresholdExceeded += OnSoundThresholdExceeded;
        }

        private static string GetConnectionKey(string peerId, int roomId) => $"{peerId}_a_{roomId}";

        public async Task<string> CreateAudioPeerConnection(string peerId, int roomId)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Audio peer connection already exists for {Key}, closing existing", key);
                await CloseAudioPeerConnection(peerId, roomId);
            }

            _logger.LogInformation("Creating audio WebRTC peer connection for peer {PeerId}, room {RoomId}", peerId, roomId);

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                },
                bundlePolicy = RTCBundlePolicy.max_bundle,
                rtcpMuxPolicy = RTCRtcpMuxPolicy.require
            };

            var pc = new RTCPeerConnection(config);

            // ICE candidate handler - send to client via SignalR
            pc.onicecandidate += (candidate) =>
            {
                if (candidate != null)
                {
                    _ = _hubContext.Clients.Client(peerId).SendAsync(
                        "ReceiveAudioIceCandidate",
                        roomId,
                        candidate.candidate,
                        candidate.sdpMid ?? string.Empty,
                        candidate.sdpMLineIndex);
                }
            };

            // Connection state handler
            pc.onconnectionstatechange += (state) =>
            {
                _logger.LogInformation("Audio connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state, peerId, roomId);

                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Audio peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseAudioPeerConnection(peerId, roomId));
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
                await CloseAudioPeerConnection(peerId, roomId);
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

        public Task CloseAudioPeerConnection(string peerId, int roomId)
        {
            string key = GetConnectionKey(peerId, roomId);
            CloseConnection(key, roomId);
            return Task.CompletedTask;
        }

        public Task CloseAllAudioPeerConnections(string peerId)
        {
            string prefix = $"{peerId}_a_";
            foreach (var key in _peerConnections.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                if (int.TryParse(key.Substring(prefix.Length), out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }
            return Task.CompletedTask;
        }

        private void CloseConnection(string key, int roomId)
        {
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

            if (_peerConnections.TryRemove(key, out var pc))
            {
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
