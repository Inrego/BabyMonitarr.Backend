using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BabyMonitarr.Backend.Models;
using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Services;

/// <summary>
/// Hands a Nest room's media to the cast ffmpeg process. Nest arrives as WebRTC inside the
/// backend, so there is no URL for ffmpeg to pull. Instead the decrypted RTP packets are re-sent
/// unchanged to loopback UDP ports and ffmpeg reads them through a generated SDP file. No
/// re-packetizing, no muxing, no buffering: the only latency added is one loopback hop.
/// </summary>
internal sealed class CastNestRtpRelay : IDisposable
{
    private readonly NestStreamReaderManager _readers;
    private readonly NestStreamReader _reader;
    private readonly ILogger _logger;
    private readonly int _roomId;
    private readonly bool _video;
    private readonly UdpClient _socket;
    private readonly IPEndPoint? _videoTarget;
    private readonly IPEndPoint _audioTarget;
    private long _forwarded;
    private int _videoSeen;
    private int _sendFailures;
    private uint _primarySsrc;
    private bool _disposed;

    public string SdpPath { get; }

    /// <summary>
    /// Raised when the Nest session behind the relay has been rebuilt (new SSRC on the primary
    /// stream). Sequence numbers and clocks restart with it, so whoever consumes the relay must
    /// resync; ffmpeg cannot, so the HLS service restarts it.
    /// </summary>
    public Action? SourceRestarted { get; set; }

    private CastNestRtpRelay(
        NestStreamReaderManager readers,
        NestStreamReader reader,
        ILogger logger,
        int roomId,
        bool video,
        string sdpPath,
        int videoPort,
        int audioPort)
    {
        _readers = readers;
        _reader = reader;
        _logger = logger;
        _roomId = roomId;
        _video = video;
        SdpPath = sdpPath;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _videoTarget = video ? new IPEndPoint(IPAddress.Loopback, videoPort) : null;
        _audioTarget = new IPEndPoint(IPAddress.Loopback, audioPort);
    }

    public static CastNestRtpRelay Start(
        NestStreamReaderManager readers,
        Room room,
        bool video,
        string directory,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(room.NestDeviceId))
        {
            throw new InvalidOperationException($"Room {room.Id} has no Nest device to cast.");
        }

        int videoPort = video ? ReserveRtpPortPair() : 0;
        int audioPort = ReserveRtpPortPair();

        string sdpPath = Path.Combine(directory, "source.sdp");
        File.WriteAllText(sdpPath, BuildSdp(video, videoPort, audioPort));

        // Shares the room's live WebRTC session with the in-app streams; the manager reference
        // counts it, so casting an inactive room brings the session up and tears it down after.
        var reader = readers.GetOrCreateReader(room.Id, room.NestDeviceId, CancellationToken.None);
        var relay = new CastNestRtpRelay(readers, reader, logger, room.Id, video, sdpPath, videoPort, audioPort);
        reader.RtpPacketReceived += relay.OnRtpPacket;
        return relay;
    }

    private void OnRtpPacket(SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (_disposed) return;

        IPEndPoint target;
        if (mediaType == SDPMediaTypesEnum.video)
        {
            if (_videoTarget == null) return;
            if (!TrackSsrc(packet.Header.SyncSource)) return;
            Volatile.Write(ref _videoSeen, 1);
            target = _videoTarget;
        }
        else if (mediaType == SDPMediaTypesEnum.audio)
        {
            if (_videoTarget == null && !TrackSsrc(packet.Header.SyncSource)) return;

            // Without RTCP sender reports ffmpeg zeroes each stream's clock at its first packet,
            // so audio/video sync is only as good as the arrival skew of those two packets. Holding
            // audio until video flows bounds that skew to about one frame.
            if (_videoTarget != null && Volatile.Read(ref _videoSeen) == 0) return;
            target = _audioTarget;
        }
        else
        {
            return;
        }

        try
        {
            byte[] datagram = Serialize(packet);
            _socket.Send(datagram, datagram.Length, target);

            if (Interlocked.Increment(ref _forwarded) == 1)
            {
                _logger.LogInformation("Cast RTP relay forwarding Nest media for room {RoomId}", _roomId);
            }
        }
        catch (SocketException ex) when (!_disposed)
        {
            // Expected while ffmpeg is between restarts: nothing is bound on the far side and
            // Windows reports the ICMP unreachable as a send error.
            if (Interlocked.Increment(ref _sendFailures) == 1)
            {
                _logger.LogDebug(ex, "Cast RTP relay send failed for room {RoomId}", _roomId);
            }
        }
        catch (ObjectDisposedException)
        {
            // Racing Dispose.
        }
    }

    /// <summary>
    /// Watches the primary stream's SSRC. A change means the reader reconnected to Nest; drop
    /// this packet, re-arm the audio gate and tell the consumer to start over.
    /// </summary>
    private bool TrackSsrc(uint ssrc)
    {
        uint known = _primarySsrc;
        if (known == ssrc) return true;

        _primarySsrc = ssrc;
        if (known == 0) return true;

        Volatile.Write(ref _videoSeen, 0);
        _logger.LogInformation("Nest session for room {RoomId} was rebuilt; resyncing cast encoder", _roomId);
        try
        {
            SourceRestarted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cast relay restart callback failed for room {RoomId}", _roomId);
        }
        return false;
    }

    /// <summary>
    /// Plain 12-byte header plus payload. Header extensions and padding from the WebRTC leg are
    /// dropped on purpose: ffmpeg does not need them, and rebuilding the header avoids trusting
    /// the parsed padding flag against a payload that has already had its padding stripped.
    /// </summary>
    private static byte[] Serialize(RTPPacket packet)
    {
        var header = packet.Header;
        byte[] payload = packet.Payload;
        var buffer = new byte[12 + payload.Length];

        buffer[0] = 0x80; // V=2, P=0, X=0, CC=0
        buffer[1] = (byte)(((header.MarkerBit & 0x1) << 7) | (header.PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), header.SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), header.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), header.SyncSource);
        payload.CopyTo(buffer, 12);
        return buffer;
    }

    private static string BuildSdp(bool video, int videoPort, int audioPort)
    {
        var sdp = new System.Text.StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append("o=- 0 0 IN IP4 127.0.0.1\r\n");
        sdp.Append("s=BabyMonitarr Nest relay\r\n");
        sdp.Append("c=IN IP4 127.0.0.1\r\n");
        sdp.Append("t=0 0\r\n");

        if (video)
        {
            sdp.Append($"m=video {videoPort} RTP/AVP {NestStreamReader.H264PayloadType}\r\n");
            sdp.Append($"a=rtpmap:{NestStreamReader.H264PayloadType} H264/90000\r\n");
            sdp.Append($"a=fmtp:{NestStreamReader.H264PayloadType} packetization-mode=1\r\n");
        }

        sdp.Append($"m=audio {audioPort} RTP/AVP {NestStreamReader.OpusPayloadType}\r\n");
        sdp.Append($"a=rtpmap:{NestStreamReader.OpusPayloadType} opus/48000/2\r\n");
        return sdp.ToString();
    }

    /// <summary>
    /// Finds an even loopback port with the odd neighbour also free, since ffmpeg binds port+1
    /// for RTCP. The ports are released before ffmpeg starts, so another process could grab one
    /// in between; on a loopback range that wide the odds are negligible, and the supervisor's
    /// ffmpeg restart would retry with the same ports anyway.
    /// </summary>
    private static int ReserveRtpPortPair()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int port = Random.Shared.Next(20000, 30000) * 2; // even, 40000..59998
            UdpClient? rtp = null;
            UdpClient? rtcp = null;
            try
            {
                rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                return port;
            }
            catch (SocketException)
            {
                // Taken; try another.
            }
            finally
            {
                rtp?.Dispose();
                rtcp?.Dispose();
            }
        }

        throw new InvalidOperationException("No free loopback UDP port pair for the cast RTP relay.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reader.RtpPacketReceived -= OnRtpPacket;
        _readers.ReleaseReader(_roomId);
        _socket.Dispose();
    }
}
