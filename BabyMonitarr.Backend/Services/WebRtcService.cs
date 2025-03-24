using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Services
{
    public class WebRtcPeerConnectionEventArgs : EventArgs
    {
        public required string PeerId { get; set; }
        public RTCPeerConnection? PeerConnection { get; set; }
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
        void SendAudioData(byte[] audioData);
    }

    public class WebRtcService : IWebRtcService, IDisposable
    {
        private readonly ILogger<WebRtcService> _logger;
        private readonly IAudioProcessingService _audioService;
        private readonly ConcurrentDictionary<string, RTCPeerConnection> _peerConnections = 
            new ConcurrentDictionary<string, RTCPeerConnection>();
        private readonly ConcurrentDictionary<string, RTCDataChannel> _dataChannels =
            new ConcurrentDictionary<string, RTCDataChannel>();
        
        public event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionCreated;
        public event EventHandler<WebRtcPeerConnectionEventArgs>? OnPeerConnectionClosed;

        public WebRtcService(ILogger<WebRtcService> logger, IAudioProcessingService audioService)
        {
            _logger = logger;
            _audioService = audioService;
        }

        public async Task<RTCPeerConnection> CreatePeerConnection(string peerId)
        {
            if (_peerConnections.TryGetValue(peerId, out var existingPeerConnection))
            {
                _logger.LogWarning($"Peer connection already exists for {peerId}, closing existing connection");
                await ClosePeerConnection(peerId);
            }

            _logger.LogInformation($"Creating WebRTC peer connection for {peerId}");

            // Configure ice servers (STUN/TURN) for NAT traversal
            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            // Create the peer connection
            var pc = new RTCPeerConnection(config);

            // Setup event handlers for ICE negotiation and connection state
            pc.onicecandidate += (candidate) =>
            {
                _logger.LogDebug($"Generated ICE candidate: {candidate?.candidate}");
            };

            pc.onconnectionstatechange += (state) =>
            {
                _logger.LogInformation($"Connection state changed to {state} for peer {peerId}");
                
                if (state == RTCPeerConnectionState.failed || 
                    state == RTCPeerConnectionState.closed ||
                    state == RTCPeerConnectionState.disconnected)
                {
                    Task.Run(() => ClosePeerConnection(peerId));
                }
            };

            _peerConnections.TryAdd(peerId, pc);
            
            // Create a data channel immediately when setting up the peer connection
            // This ensures the client will receive the ondatachannel event
            CreateDataChannel(peerId, pc);
            
            // Raise event
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
                // Check if we already have a data channel for this peer
                if (_dataChannels.ContainsKey(peerId))
                {
                    _logger.LogDebug($"Data channel already exists for peer {peerId}");
                    return;
                }

                // Create a data channel for audio streaming
                var dataChannel = await pc.createDataChannel("audio", null);
                
                // Store the data channel for later use
                _dataChannels[peerId] = dataChannel;
                
                _logger.LogInformation($"Created data channel for peer {peerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating data channel for peer {peerId}");
            }
        }

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

            pc.addIceCandidate(iceCandidate);
            return Task.CompletedTask;
        }

        public Task ClosePeerConnection(string peerId)
        {
            // Clean up data channel if exists
            _dataChannels.TryRemove(peerId, out _);
            
            if (_peerConnections.TryRemove(peerId, out var pc))
            {
                _logger.LogInformation($"Closing WebRTC peer connection for {peerId}");
                
                try
                {
                    pc.close();
                    
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
            if (_dataChannels.Count == 0 || audioData == null || audioData.Length == 0)
            {
                return;
            }

            try
            {
                // Get the audio format information
                var audioFormat = _audioService.GetAudioFormat();
                
                // Create a header with format information (simple 8-byte header)
                // Format: [sample rate (4 bytes)][channels (2 bytes)][bits per sample (2 bytes)]
                byte[] header = new byte[8];
                
                // Sample rate (e.g., 44100Hz)
                BitConverter.GetBytes(audioFormat.SampleRate).CopyTo(header, 0);
                
                // Channels (e.g., 1 for mono, 2 for stereo)
                BitConverter.GetBytes((short)audioFormat.Channels).CopyTo(header, 4);
                
                // Bits per sample (e.g., 16 for 16-bit PCM)
                BitConverter.GetBytes((short)audioFormat.BitsPerSample).CopyTo(header, 6);
                
                // Combine header and audio data
                byte[] dataWithHeader = new byte[header.Length + audioData.Length];
                header.CopyTo(dataWithHeader, 0);
                audioData.CopyTo(dataWithHeader, header.Length);
                
                _logger.LogDebug($"Sending {dataWithHeader.Length} bytes of audio data to {_dataChannels.Count} WebRTC peers");
                
                // Send audio data through data channels
                foreach (var pair in _dataChannels)
                {
                    string peerId = pair.Key;
                    RTCDataChannel dataChannel = pair.Value;
                    
                    try
                    {
                        // Send the audio data through the data channel
                        dataChannel.send(dataWithHeader);
                    }
                    catch (Exception ex)
                    {
                        // Just log and continue, don't propagate exceptions to avoid disrupting the audio flow
                        _logger.LogDebug($"Error sending audio data via data channel to peer {peerId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending audio data to WebRTC peers");
            }
        }

        public void Dispose()
        {
            // Close all peer connections
            foreach (var peerId in _peerConnections.Keys.ToList())
            {
                ClosePeerConnection(peerId).Wait();
            }
            
            // Clear data channels
            _dataChannels.Clear();
        }
    }
}