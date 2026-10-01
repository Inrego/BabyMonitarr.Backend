using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using BabyMonitarr.Backend.Hubs;
using BabyMonitarr.Backend.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace BabyMonitarr.Backend.Talkback;

public interface ITalkbackUplinkService
{
    /// <summary>Creates the app's microphone peer connection for a room and returns the server offer.</summary>
    Task<string> CreateUplinkAsync(string connectionId, int roomId, string? hostHint);
    void SetRemoteDescription(string connectionId, int roomId, RTCSessionDescriptionInit description);
    void AddIceCandidate(string connectionId, int roomId, RTCIceCandidateInit candidate);
    Task CloseUplinkAsync(string connectionId, int roomId, string why);
    Task CloseAllForConnectionAsync(string connectionId);
}

/// <summary>
/// The app → server microphone leg of talkback: one receive-only Opus peer connection per app
/// connection and room, separate from the monitoring peer connections. Decoded audio goes to
/// <see cref="ITalkbackService"/>, which forwards it only while that connection is the talker.
/// </summary>
public sealed class TalkbackUplinkService : ITalkbackUplinkService, IDisposable
{
    private sealed class Uplink(RTCPeerConnection pc, AudioFormat format)
    {
        public RTCPeerConnection Pc { get; } = pc;
        public AudioFormat Format { get; } = format;
        public AudioEncoder Decoder { get; } = new(includeOpus: true);
        public List<RTCIceCandidateInit> PendingCandidates { get; } = new();
        public long Packets;
    }

    private readonly ConcurrentDictionary<string, Uplink> _uplinks = new();
    private readonly ITalkbackService _talkback;
    private readonly IWebRtcConfigService _webRtcConfig;
    private readonly IHubContext<AudioStreamHub> _hub;
    private readonly ILogger<TalkbackUplinkService> _logger;

    public TalkbackUplinkService(
        ITalkbackService talkback,
        IWebRtcConfigService webRtcConfig,
        IHubContext<AudioStreamHub> hub,
        ILogger<TalkbackUplinkService> logger)
    {
        _talkback = talkback;
        _webRtcConfig = webRtcConfig;
        _hub = hub;
        _logger = logger;
    }

    private static string Key(string connectionId, int roomId) => $"{connectionId}_t_{roomId}";

    public async Task<string> CreateUplinkAsync(string connectionId, int roomId, string? hostHint)
    {
        string key = Key(connectionId, roomId);
        if (_uplinks.ContainsKey(key))
        {
            await CloseUplinkAsync(connectionId, roomId, "replaced by a new uplink");
        }

        var settings = _webRtcConfig.GetPeerConnectionSettings(hostHint);
        var pc = new RTCPeerConnection(settings.Configuration, settings.BindPort, settings.PortRange, false);
        IgnoreIcmpResets(pc, key);
        var format = new AudioFormat(AudioCodecsEnum.OPUS, NestStreamReader.OpusPayloadType, 48000, 2, "minptime=10;useinbandfec=1");
        var uplink = new Uplink(pc, format);

        pc.addTrack(new MediaStreamTrack(new List<AudioFormat> { format }, MediaStreamStatusEnum.RecvOnly));

        pc.onicecandidate += candidate =>
        {
            if (candidate == null) return;
            _ = SendIceCandidateAsync(connectionId, roomId, candidate);

            var advertised = _webRtcConfig.CreateAdvertisedCandidate(candidate, settings.AdvertisedAddress);
            if (advertised != null) _ = SendIceCandidateAsync(connectionId, roomId, advertised);
        };

        pc.oniceconnectionstatechange += state =>
            _logger.LogInformation("Talkback uplink {Key}: ICE {State}", key, state);

        pc.onconnectionstatechange += state =>
        {
            _logger.LogInformation("Talkback uplink {Key}: connection {State}", key, state);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = CloseIfCurrentAsync(key, connectionId, roomId, uplink, $"uplink {state}");
            }
        };

        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            try
            {
                if (Interlocked.Increment(ref uplink.Packets) == 1)
                {
                    _logger.LogInformation("First talkback uplink audio received for {Key}", key);
                }

                short[] pcm = uplink.Decoder.DecodeAudio(packet.Payload, uplink.Format);
                if (pcm.Length > 0) _talkback.OnUplinkAudio(connectionId, roomId, pcm);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Talkback uplink decode failed for {Key}: {Error}", key, ex.Message);
            }
        };

        _uplinks[key] = uplink;

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        string sdp = pc.localDescription?.sdp?.ToString() ?? string.Empty;
        if (sdp.Length == 0)
        {
            await CloseUplinkAsync(connectionId, roomId, "offer failed");
            throw new InvalidOperationException("Could not create the talkback uplink offer.");
        }

        _logger.LogInformation("Talkback uplink created for {Key}", key);
        return sdp;
    }

    public void SetRemoteDescription(string connectionId, int roomId, RTCSessionDescriptionInit description)
    {
        var uplink = Get(connectionId, roomId);
        var result = uplink.Pc.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogWarning("Talkback uplink answer for {Key} rejected: {Result}", Key(connectionId, roomId), result);
            throw new InvalidOperationException($"The talkback uplink answer was rejected: {result}.");
        }
        _logger.LogInformation("Talkback uplink answer applied for {Key}", Key(connectionId, roomId));

        lock (uplink.PendingCandidates)
        {
            foreach (var candidate in uplink.PendingCandidates) TryAddCandidate(uplink, candidate);
            uplink.PendingCandidates.Clear();
        }
    }

    public void AddIceCandidate(string connectionId, int roomId, RTCIceCandidateInit candidate)
    {
        var uplink = Get(connectionId, roomId);
        _logger.LogDebug("Talkback uplink {Key}: remote candidate {Candidate}", Key(connectionId, roomId), candidate.candidate);
        lock (uplink.PendingCandidates)
        {
            if (uplink.Pc.signalingState == RTCSignalingState.stable) TryAddCandidate(uplink, candidate);
            else uplink.PendingCandidates.Add(candidate);
        }
    }

    public async Task CloseUplinkAsync(string connectionId, int roomId, string why)
    {
        if (_uplinks.TryRemove(Key(connectionId, roomId), out var uplink))
        {
            await CloseAsync(connectionId, roomId, uplink, why);
        }
    }

    public async Task CloseAllForConnectionAsync(string connectionId)
    {
        string prefix = $"{connectionId}_t_";
        foreach (var key in _uplinks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (int.TryParse(key[prefix.Length..], out int roomId))
            {
                await CloseUplinkAsync(connectionId, roomId, "app disconnected");
            }
        }
    }

    private async Task CloseIfCurrentAsync(string key, string connectionId, int roomId, Uplink uplink, string why)
    {
        // Only this exact uplink: a replacement may already sit under the same key.
        if (((ICollection<KeyValuePair<string, Uplink>>)_uplinks).Remove(new KeyValuePair<string, Uplink>(key, uplink)))
        {
            await CloseAsync(connectionId, roomId, uplink, why);
        }
    }

    private async Task CloseAsync(string connectionId, int roomId, Uplink uplink, string why)
    {
        _logger.LogInformation("Closing talkback uplink {Key}: {Why}", Key(connectionId, roomId), why);
        await _talkback.StopAsync(connectionId, roomId, why);
        try
        {
            uplink.Pc.Close(why);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Closing talkback uplink failed: {Error}", ex.Message);
        }
    }

    private void IgnoreIcmpResets(RTCPeerConnection pc, string key)
    {
        try
        {
            IcmpResets.Ignore(pc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not turn off ICMP resets for talkback uplink {Key}: {Error}", key, ex.Message);
        }
    }

    private Uplink Get(string connectionId, int roomId) =>
        _uplinks.TryGetValue(Key(connectionId, roomId), out var uplink)
            ? uplink
            : throw new KeyNotFoundException($"No talkback uplink for room {roomId}; call StartTalkbackUplink first.");

    private void TryAddCandidate(Uplink uplink, RTCIceCandidateInit candidate)
    {
        try
        {
            uplink.Pc.addIceCandidate(candidate);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Skipping talkback uplink ICE candidate: {Error}", ex.Message);
        }
    }

    private Task SendIceCandidateAsync(string connectionId, int roomId, RTCIceCandidate candidate) =>
        _hub.Clients.Client(connectionId).SendAsync(
            "ReceiveTalkbackIceCandidate",
            roomId,
            candidate.candidate,
            candidate.sdpMid ?? string.Empty,
            candidate.sdpMLineIndex);

    public void Dispose()
    {
        foreach (var uplink in _uplinks.Values)
        {
            try { uplink.Pc.Close("shutdown"); } catch { }
        }
        _uplinks.Clear();
    }
}
