using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using BabyMonitarr.Backend.Hubs;

namespace BabyMonitarr.Backend.Services
{
    public class WebRtcPeerConnectionEventArgs : EventArgs
    {
        public required string PeerId { get; set; }
        public RTCPeerConnection? PeerConnection { get; set; }
    }

    public class IceCandidateEventArgs : EventArgs
    {
        public required string PeerId { get; set; }
        public required RTCIceCandidate Candidate { get; set; }
    }

    public interface IWebRtcService
    {
        Task<RTCPeerConnection> CreatePeerConnection(string peerId);
        Task<string> CreateOffer(string peerId);
        Task SetRemoteDescription(string peerId, RTCSessionDescriptionInit description);
        Task AddIceCandidate(string peerId, RTCIceCandidateInit iceCandidate);
        Task ClosePeerConnection(string peerId);
        event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionCreated;
        event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionClosed;
        event EventHandler<IceCandidateEventArgs>? OnIceCandidateGenerated;
        void SendAudioData(byte[] audioData);
        void SendAudioLevel(double audioLevel, DateTime timestamp);
        void SendSoundAlert(double audioLevel, double threshold, DateTime timestamp);
    }

    public class WebRtcService : IWebRtcService, IDisposable
    {
        private readonly ILogger<WebRtcService> _logger;
        private readonly IAudioProcessingService _audioService;
        private readonly IHubContext<AudioStreamHub> _hubContext;
        private readonly ConcurrentDictionary<string, RTCPeerConnection> _peerConnections =
            new ConcurrentDictionary<string, RTCPeerConnection>();
        private readonly ConcurrentDictionary<string, AudioExtrasSource> _audioSources =
            new ConcurrentDictionary<string, AudioExtrasSource>();
        private readonly ConcurrentDictionary<string, AudioEncoder> _audioEncoders =
            new ConcurrentDictionary<string, AudioEncoder>();
        private readonly ConcurrentDictionary<string, AudioFormat> _negotiatedFormats =
            new ConcurrentDictionary<string, AudioFormat>();
        private readonly ConcurrentDictionary<string, RTCDataChannel> _dataChannels =
            new ConcurrentDictionary<string, RTCDataChannel>();

        public event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionCreated;
        public event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionClosed;
        public event EventHandler<IceCandidateEventArgs>? OnIceCandidateGenerated;

        public WebRtcService(
            ILogger<WebRtcService> logger,
            IAudioProcessingService audioService,
            IHubContext<AudioStreamHub> hubContext)
        {
            _logger = logger;
            _audioService = audioService;
            _hubContext = hubContext;
        }

        public async Task<RTCPeerConnection> CreatePeerConnection(string peerId)
        {
            if (_peerConnections.TryGetValue(peerId, out var existingPeerConnection))
            {
                _logger.LogWarning($"Peer connection already exists for {peerId}, closing existing connection");
                await ClosePeerConnection(peerId);
            }

            _logger.LogInformation($"Creating WebRTC peer connection for {peerId}");

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

            // Setup event handlers
            pc.onicecandidate += (candidate) =>
            {
                if (candidate != null)
                {
                    _logger.LogDebug($"Generated ICE candidate for {peerId}: {candidate.candidate}");

                    // Fire the event for any external subscribers
                    OnIceCandidateGenerated?.Invoke(this, new IceCandidateEventArgs
                    {
                        PeerId = peerId,
                        Candidate = candidate
                    });

                    // Send the ICE candidate to the client via SignalR
                    _ = _hubContext.Clients.Client(peerId).SendAsync(
                        "ReceiveIceCandidate",
                        candidate.candidate,
                        candidate.sdpMid ?? string.Empty,
                        candidate.sdpMLineIndex);
                }
            };

            pc.onconnectionstatechange += (state) =>
            {
                _logger.LogInformation($"Connection state changed to {state} for peer {peerId}");

                // Only close on 'failed' state - 'closed' is expected when we close it ourselves
                // Don't close on 'new' or other transitional states
                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning($"Peer connection failed for {peerId}, closing...");
                    Task.Run(() => ClosePeerConnection(peerId));
                }
            };

            // Add to peer connections dictionary FIRST before any operations that might trigger state changes
            _peerConnections.TryAdd(peerId, pc);
            _logger.LogInformation($"Added peer connection to dictionary for {peerId}");

            // Create and add audio track
            try
            {
                // Use AudioEncoder with Opus support for WebRTC compatibility
                var audioEncoder = new AudioEncoder(includeOpus: true);
                var audioSource = new AudioExtrasSource(audioEncoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

                // Create audio track with send-only capability
                var audioTrack = new MediaStreamTrack(audioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
                pc.addTrack(audioTrack);

                // Handle format negotiation
                pc.OnAudioFormatsNegotiated += (audioFormats) =>
                {
                    var selectedFormat = audioFormats.First();
                    _logger.LogInformation($"Audio formats negotiated for peer {peerId}: {string.Join(", ", audioFormats.Select(f => f.FormatName))}. Selected: {selectedFormat.FormatName}");
                    audioSource.SetAudioSourceFormat(selectedFormat);
                    _negotiatedFormats[peerId] = selectedFormat;
                };

                _audioSources.TryAdd(peerId, audioSource);
                _audioEncoders.TryAdd(peerId, audioEncoder);
                _logger.LogInformation($"Added audio track for peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding audio track for peer {peerId}");
                _peerConnections.TryRemove(peerId, out _);
                pc.Close("Error adding audio track");
                throw;
            }

            // Create data channel for audio level updates
            CreateDataChannel(peerId, pc);
            
            OnPeerConnectionCreated?.Invoke(this, new WebRtcPeerConnectionEventArgs
            {
                PeerId = peerId, 
                PeerConnection = pc
            });

            return pc;
        }

        private async void CreateDataChannel(string peerId, RTCPeerConnection pc)
        {
            try
            {
                var dataChannel = await pc.createDataChannel("audioLevels", new RTCDataChannelInit());

                dataChannel.onopen += () =>
                {
                    _logger.LogInformation($"Data channel opened for peer {peerId}");
                };

                dataChannel.onclose += () =>
                {
                    _logger.LogInformation($"Data channel closed for peer {peerId}");
                    _dataChannels.TryRemove(peerId, out _);
                };

                dataChannel.onerror += (error) =>
                {
                    _logger.LogError($"Data channel error for peer {peerId}: {error}");
                };

                _dataChannels.TryAdd(peerId, dataChannel);
                _logger.LogInformation($"Created data channel for peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating data channel for peer {peerId}");
            }
        }

        private readonly ConcurrentDictionary<string, List<RTCIceCandidateInit>> _pendingIceCandidates =
            new ConcurrentDictionary<string, List<RTCIceCandidateInit>>();

        public async Task<string> CreateOffer(string peerId)
        {
            if (!_peerConnections.TryGetValue(peerId, out var pc))
            {
                _logger.LogError($"No peer connection found for {peerId}");
                throw new KeyNotFoundException($"No peer connection found for {peerId}");
            }

            var offerInit = pc.createOffer(null);
            await pc.setLocalDescription(offerInit);
            
            // Get the SDP content directly from the sdp property for browser compatibility
            if (pc.localDescription != null)
            {
                var sdp = pc.localDescription.sdp;
                _logger.LogDebug($"Generated SDP offer: {sdp}");
                return sdp.ToString();
            }
            
            _logger.LogError("Failed to create offer: localDescription is null");
            return string.Empty;
        }

        public Task SetRemoteDescription(string peerId, RTCSessionDescriptionInit description)
        {
            if (!_peerConnections.TryGetValue(peerId, out var pc))
            {
                _logger.LogError($"No peer connection found for {peerId}");
                throw new KeyNotFoundException($"No peer connection found for {peerId}");
            }

            try
            {
                pc.setRemoteDescription(description);
                _logger.LogInformation($"Set remote description for peer {peerId}, type: {description.type}");

                // Process queued ICE candidates
                if (_pendingIceCandidates.TryRemove(peerId, out var pendingCandidates))
                {
                    _logger.LogInformation($"Processing {pendingCandidates.Count} queued ICE candidates for peer {peerId}");
                    foreach (var candidate in pendingCandidates)
                    {
                        try 
                        {
                            pc.addIceCandidate(candidate);
                            _logger.LogDebug($"Added queued ICE candidate for peer {peerId}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error adding queued ICE candidate for peer {peerId}: {candidate.candidate}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting remote description for peer {peerId}");
                throw;
            }
            
            return Task.CompletedTask;
        }

        public Task AddIceCandidate(string peerId, RTCIceCandidateInit iceCandidate)
        {
            if (!_peerConnections.TryGetValue(peerId, out var pc))
            {
                _logger.LogError($"No peer connection found for {peerId}");
                throw new KeyNotFoundException($"No peer connection found for {peerId}");
            }

            // Check if we can add the candidate immediately (signaling state needs to be stable to accept answer-related candidates, 
            // or have-remote-offer if we were receiving an offer, but here we act as the offerer usually)
            // SIPSorcery/WebRTC logic: If we sent an Offer, we are in 'have-local-offer'. We need 'stable' (received Answer) 
            // to add candidates that belong to the remote answer.
            
            if (pc.signalingState == RTCSignalingState.stable)
            {
                _logger.LogInformation($"Adding ICE candidate for peer {peerId}: {iceCandidate.candidate}");
                pc.addIceCandidate(iceCandidate);
                _logger.LogInformation($"ICE candidate added. Connection state: {pc.connectionState}, ICE state: {pc.iceConnectionState}");
            }
            else
            {
                _logger.LogInformation($"Queueing ICE candidate for peer {peerId} (Signaling State: {pc.signalingState})");
                
                _pendingIceCandidates.AddOrUpdate(peerId, 
                    new List<RTCIceCandidateInit> { iceCandidate },
                    (key, list) => { list.Add(iceCandidate); return list; });
            }

            return Task.CompletedTask;
        }

        public Task ClosePeerConnection(string peerId)
        {
            _logger.LogInformation($"ClosePeerConnection called for {peerId}");

            // Clean up negotiated format
            _negotiatedFormats.TryRemove(peerId, out _);

            // Clean up audio encoder if exists
            if (_audioEncoders.TryRemove(peerId, out _))
            {
                _logger.LogInformation($"Removed audio encoder for {peerId}");
            }

            // Clean up audio source if exists
            if (_audioSources.TryRemove(peerId, out var audioSource))
            {
                _logger.LogInformation($"Closing audio source for {peerId}");
                audioSource.CloseAudio().Wait();
            }

            // Clean up data channel if exists
            if (_dataChannels.TryRemove(peerId, out var dataChannel))
            {
                _logger.LogInformation($"Closing data channel for {peerId}");
                dataChannel.close();
            }

            // Clean up pending ICE candidates
            if (_pendingIceCandidates.TryRemove(peerId, out _))
            {
                _logger.LogDebug($"Removed pending ICE candidates for {peerId}");
            }

            if (_peerConnections.TryRemove(peerId, out var pc))
            {
                _logger.LogInformation($"Closing WebRTC peer connection for {peerId}");
                
                try
                {
                    pc.Close("Peer connection closed by server"); // Corrected: Was pc.close() and added reason
                    
                    // Raise event
                    OnPeerConnectionClosed?.Invoke(this, new WebRtcPeerConnectionEventArgs
                    {
                        PeerId = peerId, 
                        PeerConnection = null
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error closing peer connection for {peerId}");
                }
            }
            
            return Task.CompletedTask;
        }

        // This method is called by AudioStreamingBackgroundService with audio data
        public void SendAudioData(byte[] audioData)
        {
            if (_peerConnections.IsEmpty || audioData == null || audioData.Length == 0)
            {
                return;
            }

            try
            {
                // Convert byte[] to short[] (16-bit PCM)
                short[] samples = new short[audioData.Length / 2];
                Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);

                foreach (var peerConnectionPair in _peerConnections)
                {
                    string peerId = peerConnectionPair.Key;
                    RTCPeerConnection pc = peerConnectionPair.Value;

                    // Only send to connected peers
                    if (pc.connectionState != RTCPeerConnectionState.connected)
                        continue;

                    if (!_audioEncoders.TryGetValue(peerId, out var encoder))
                        continue;

                    if (!_negotiatedFormats.TryGetValue(peerId, out var audioFormat))
                    {
                        _logger.LogDebug($"No audio format negotiated yet for peer {peerId}");
                        continue;
                    }

                    try
                    {
                        // Resample from 44100Hz to the format's clock rate if needed
                        short[] resampledSamples = samples;
                        if (44100 != audioFormat.ClockRate)
                        {
                            resampledSamples = ResampleAudio(samples, 44100, audioFormat.ClockRate);
                        }

                        // Encode the audio using the encoder
                        byte[] encodedSample = encoder.EncodeAudio(resampledSamples, audioFormat);

                        if (encodedSample != null && encodedSample.Length > 0)
                        {
                            // Calculate RTP timestamp units
                            // For Opus at 48kHz, each sample = 1 timestamp unit
                            uint durationRtpUnits = (uint)resampledSamples.Length;

                            // Send the encoded audio via the peer connection
                            pc.SendAudio(durationRtpUnits, encodedSample);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error encoding/sending audio to peer {peerId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending audio data to WebRTC peers");
            }
        }

        public void SendAudioLevel(double audioLevel, DateTime timestamp)
        {
            if (_dataChannels.IsEmpty)
                return;

            try
            {
                var unixTimestamp = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
                var message = JsonSerializer.Serialize(new
                {
                    type = "audioLevel",
                    level = audioLevel,
                    timestamp = unixTimestamp
                });

                var messageBytes = Encoding.UTF8.GetBytes(message);

                foreach (var dataChannelPair in _dataChannels)
                {
                    var dataChannel = dataChannelPair.Value;
                    if (dataChannel.readyState == RTCDataChannelState.open)
                    {
                        dataChannel.send(message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Error sending audio level via data channel: {ex.Message}");
            }
        }

        public void SendSoundAlert(double audioLevel, double threshold, DateTime timestamp)
        {
            if (_dataChannels.IsEmpty)
                return;

            try
            {
                var unixTimestamp = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
                var message = JsonSerializer.Serialize(new
                {
                    type = "soundAlert",
                    level = audioLevel,
                    threshold = threshold,
                    timestamp = unixTimestamp
                });

                foreach (var dataChannelPair in _dataChannels)
                {
                    var dataChannel = dataChannelPair.Value;
                    if (dataChannel.readyState == RTCDataChannelState.open)
                    {
                        dataChannel.send(message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Error sending sound alert via data channel: {ex.Message}");
            }
        }

        /// <summary>
        /// Simple linear interpolation resampler
        /// </summary>
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
                    // Linear interpolation
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
            // Close all peer connections
            foreach (var peerId in _peerConnections.Keys.ToList())
            {
                ClosePeerConnection(peerId).Wait(); // This will also dispose associated AudioExtrasSource, encoder, and format
            }

            // Ensure any remaining audio sources are disposed (should be covered by ClosePeerConnection)
            // but as a safeguard:
            foreach (var audioSource in _audioSources.Values)
            {
                audioSource.CloseAudio().Wait();
            }
            _audioSources.Clear();
            _audioEncoders.Clear();
            _negotiatedFormats.Clear();
            _dataChannels.Clear();
        }
    }
}