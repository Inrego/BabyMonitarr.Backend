using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using BabyMonitarr.Backend.Hubs;

namespace BabyMonitarr.Backend.Services
{
    public interface IVideoWebRtcService
    {
        Task<string> CreateVideoPeerConnection(string peerId, int roomId);
        Task SetVideoRemoteDescription(string peerId, int roomId, RTCSessionDescriptionInit desc);
        Task AddVideoIceCandidate(string peerId, int roomId, RTCIceCandidateInit candidate);
        Task CloseVideoPeerConnection(string peerId, int roomId);
        Task CloseAllVideoPeerConnections(string peerId);
    }

    public class VideoWebRtcService : IVideoWebRtcService, IDisposable
    {
        private readonly ILogger<VideoWebRtcService> _logger;
        private readonly IVideoStreamingService _videoStreamingService;
        private readonly IHubContext<AudioStreamHub> _hubContext;

        // Peer connections keyed by "{peerId}_v_{roomId}"
        private readonly ConcurrentDictionary<string, RTCPeerConnection> _peerConnections = new();
        private readonly ConcurrentDictionary<string, VpxVideoEncoder> _videoEncoders = new();
        private readonly ConcurrentDictionary<string, List<RTCIceCandidateInit>> _pendingIceCandidates = new();

        // Track which rooms each peer is subscribed to, and the handler reference for unsubscription
        private readonly ConcurrentDictionary<string, Action<VideoFrameEventArgs>> _frameHandlers = new();

        public VideoWebRtcService(
            ILogger<VideoWebRtcService> logger,
            IVideoStreamingService videoStreamingService,
            IHubContext<AudioStreamHub> hubContext)
        {
            _logger = logger;
            _videoStreamingService = videoStreamingService;
            _hubContext = hubContext;
        }

        private static string GetConnectionKey(string peerId, int roomId) => $"{peerId}_v_{roomId}";

        public async Task<string> CreateVideoPeerConnection(string peerId, int roomId)
        {
            string key = GetConnectionKey(peerId, roomId);

            if (_peerConnections.ContainsKey(key))
            {
                _logger.LogWarning("Video peer connection already exists for {Key}, closing existing", key);
                await CloseVideoPeerConnection(peerId, roomId);
            }

            _logger.LogInformation("Creating video WebRTC peer connection for peer {PeerId}, room {RoomId}", peerId, roomId);

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
                        "ReceiveVideoIceCandidate",
                        roomId,
                        candidate.candidate,
                        candidate.sdpMid ?? string.Empty,
                        candidate.sdpMLineIndex);
                }
            };

            // Connection state handler
            pc.onconnectionstatechange += (state) =>
            {
                _logger.LogInformation("Video connection state changed to {State} for peer {PeerId}, room {RoomId}",
                    state, peerId, roomId);

                if (state == RTCPeerConnectionState.failed)
                {
                    _logger.LogWarning("Video peer connection failed for {Key}, closing...", key);
                    Task.Run(() => CloseVideoPeerConnection(peerId, roomId));
                }
            };

            // Add video track with VP8
            var videoEncoder = new VpxVideoEncoder();
            var videoFormats = new List<VideoFormat>
            {
                new VideoFormat(VideoCodecsEnum.VP8, VpxVideoEncoder.VP8_FORMATID)
            };
            var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendOnly);
            pc.addTrack(videoTrack);

            _peerConnections.TryAdd(key, pc);
            _videoEncoders.TryAdd(key, videoEncoder);

            // Create SDP offer
            var offerInit = pc.createOffer(null);
            await pc.setLocalDescription(offerInit);

            string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sdp))
            {
                _logger.LogError("Failed to create video SDP offer for {Key}", key);
                await CloseVideoPeerConnection(peerId, roomId);
                return string.Empty;
            }

            // Subscribe to video frames for this room
            Action<VideoFrameEventArgs> frameHandler = (args) =>
            {
                SendVideoFrameToPeer(key, args.I420Data, args.Width, args.Height, args.TimestampMs);
            };
            _frameHandlers.TryAdd(key, frameHandler);
            _videoStreamingService.SubscribeToRoom(roomId, frameHandler);

            _logger.LogInformation("Video peer connection created for {Key}", key);
            return sdp;
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

            // Process queued ICE candidates
            if (_pendingIceCandidates.TryRemove(key, out var pendingCandidates))
            {
                _logger.LogInformation("Processing {Count} queued video ICE candidates for {Key}",
                    pendingCandidates.Count, key);
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
                _pendingIceCandidates.AddOrUpdate(key,
                    new List<RTCIceCandidateInit> { candidate },
                    (_, list) => { list.Add(candidate); return list; });
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
                // Extract roomId from key
                if (int.TryParse(key.Substring(prefix.Length), out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }
            return Task.CompletedTask;
        }

        private void CloseConnection(string key, int roomId)
        {
            _logger.LogInformation("Closing video peer connection {Key}", key);

            // Unsubscribe from video frames
            if (_frameHandlers.TryRemove(key, out var handler))
            {
                _videoStreamingService.UnsubscribeFromRoom(roomId, handler);
            }

            _pendingIceCandidates.TryRemove(key, out _);
            _videoEncoders.TryRemove(key, out _);

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
        }

        private void SendVideoFrameToPeer(string key, byte[] i420Data, int width, int height, long timestampMs)
        {
            if (!_peerConnections.TryGetValue(key, out var pc)) return;
            if (pc.connectionState != RTCPeerConnectionState.connected) return;
            if (!_videoEncoders.TryGetValue(key, out var encoder)) return;

            try
            {
                // Encode I420 to VP8
                byte[] encodedFrame = encoder.EncodeVideo(width, height, i420Data,
                    VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);

                if (encodedFrame != null && encodedFrame.Length > 0)
                {
                    // Calculate duration in RTP timestamp units (90kHz clock for video)
                    uint durationRtpUnits = (uint)(90000 / 10); // 10 fps -> 9000 units per frame

                    pc.SendVideo(durationRtpUnits, encodedFrame);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error encoding/sending video to peer {Key}: {Error}", key, ex.Message);
            }
        }

        public void Dispose()
        {
            foreach (var key in _peerConnections.Keys.ToList())
            {
                // Extract roomId from key
                var parts = key.Split("_v_");
                if (parts.Length == 2 && int.TryParse(parts[1], out int roomId))
                {
                    CloseConnection(key, roomId);
                }
            }

            _peerConnections.Clear();
            _videoEncoders.Clear();
            _frameHandlers.Clear();
            _pendingIceCandidates.Clear();
        }
    }
}
