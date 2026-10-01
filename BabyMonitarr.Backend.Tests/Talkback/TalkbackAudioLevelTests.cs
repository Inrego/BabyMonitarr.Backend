using BabyMonitarr.Backend.Services;
using BabyMonitarr.Backend.Talkback;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace BabyMonitarr.Backend.Tests.Talkback;

public class TalkbackAudioLevelTests
{
    private const string AudioLevelUri = "urn:ietf:params:rtp-hdrext:ssrc-audio-level";

    private static readonly AudioFormat Opus =
        new(AudioCodecsEnum.OPUS, NestStreamReader.OpusPayloadType, 48000, 2, "minptime=10;useinbandfec=1");

    private static short[] Sine(double amplitude, int samples = 960, double hz = 440) =>
        Enumerable.Range(0, samples).Select(i => (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / 48000) * amplitude * short.MaxValue)).ToArray();

    [Fact]
    public void Level_IsSilenceFor127AndZeroForFullScaleSquare()
    {
        Assert.Equal(127, TalkbackAudioLevel.Dbov(new short[960]));
        Assert.Equal(127, TalkbackAudioLevel.Dbov(ReadOnlySpan<short>.Empty));
        var square = Enumerable.Range(0, 960).Select(i => i % 2 == 0 ? short.MaxValue : short.MinValue).ToArray();
        Assert.Equal(0, TalkbackAudioLevel.Dbov(square));
    }

    [Theory]
    [InlineData(1.0, 3)]    // full-scale sine: RMS −3.01 dBov
    [InlineData(0.25, 15)]  // −15.05 dBov
    [InlineData(0.01, 43)]  // −43.0 dBov
    public void Level_IsMinusDbovOfTheRms(double amplitude, int expected)
    {
        Assert.Equal(expected, TalkbackAudioLevel.Dbov(Sine(amplitude)));
    }

    [Fact]
    public void Level_OfTheQuietestNonSilentFrameStaysInRange()
    {
        // One LSB in 960 samples: 10·log10(1 / 960 / 32768²) = −120.1 dBov.
        var oneLsb = new short[960];
        oneLsb[0] = 1;
        Assert.Equal(120, TalkbackAudioLevel.Dbov(oneLsb));
    }

    [Fact]
    public void Value_MarksSilenceAsNoVoiceAndSoundAsVoice()
    {
        var silence = TalkbackAudioLevel.For(new short[1920]);
        Assert.Equal((127, false), ((int)silence.Level, silence.Voice));

        var none = TalkbackAudioLevel.For((short[]?)null);
        Assert.Equal((127, false), ((int)none.Level, none.Voice));

        var tone = TalkbackAudioLevel.For(Sine(0.25, 1920));
        Assert.Equal((15, true), ((int)tone.Level, tone.Voice));

        var oneLsb = new short[960];
        oneLsb[0] = 1;
        Assert.False(TalkbackAudioLevel.For(oneLsb).Voice); // −120 dBov: below the voice threshold
        Assert.True(TalkbackAudioLevel.IsVoice(TalkbackAudioLevel.VoiceThreshold));
        Assert.False(TalkbackAudioLevel.IsVoice(TalkbackAudioLevel.VoiceThreshold + 1));
    }

    [Fact]
    public void Level_IsTakenAfterTheGainStage()
    {
        var pcm = Sine(0.25);
        TalkbackGain.Apply(pcm, 0.5);
        Assert.Equal(21, TalkbackAudioLevel.Dbov(pcm)); // −15 dBov − 6 dB
    }

    [Fact]
    public async Task Offer_HasTheAudioLevelExtmapOnTheAudioMLine()
    {
        var pc = await FoyerTalkbackStream.CreatePeerConnectionAsync(Opus);
        try
        {
            var offer = pc.createOffer();
            var (audio, video) = MediaSections(FoyerTalkbackStream.OfferSdpForFoyer(offer));

            Assert.Contains($"a=extmap:{TalkbackAudioLevel.ExtensionId} {AudioLevelUri}", audio);
            Assert.DoesNotContain(video, l => l.StartsWith("a=extmap") && l.Contains(AudioLevelUri));
            Assert.Contains("a=sendrecv", audio);
        }
        finally
        {
            pc.close();
        }
    }

    /// <summary>
    /// Two Foyer-shaped peer connections over loopback: every audio packet carries the level that
    /// was set for it, silence as 127 with V=0.
    /// </summary>
    [Fact]
    public async Task Loopback_PacketsCarryTheLevelSetForThem()
    {
        var sender = await FoyerTalkbackStream.CreatePeerConnectionAsync(Opus);
        var receiver = await FoyerTalkbackStream.CreatePeerConnectionAsync(Opus);
        try
        {
            var received = new System.Collections.Concurrent.ConcurrentQueue<(int Level, bool Voice)?>();
            receiver.OnRtpPacketReceived += (_, media, packet) =>
            {
                if (media == SDPMediaTypesEnum.audio) received.Enqueue(ReadAudioLevel(packet.Header));
            };
            sender.onicecandidate += c => { if (c != null) receiver.addIceCandidate(new RTCIceCandidateInit { candidate = c.candidate, sdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex }); };
            receiver.onicecandidate += c => { if (c != null) sender.addIceCandidate(new RTCIceCandidateInit { candidate = c.candidate, sdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex }); };

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.onconnectionstatechange += s => { if (s == RTCPeerConnectionState.connected) connected.TrySetResult(); };

            var offer = sender.createOffer();
            await sender.setLocalDescription(offer);
            Assert.Equal(SetDescriptionResultEnum.OK, receiver.setRemoteDescription(offer));
            var answer = receiver.createAnswer();
            await receiver.setLocalDescription(answer);
            Assert.Equal(SetDescriptionResultEnum.OK, sender.setRemoteDescription(answer));

            Assert.True(await Task.WhenAny(connected.Task, Task.Delay(10_000)) == connected.Task, "loopback did not connect");

            var encoder = new AudioEncoder(includeOpus: true);
            var tone = Sine(0.25, 1920);
            var silence = new short[1920];
            for (int i = 0; i < 25; i++)
            {
                var pcm = i % 2 == 0 ? tone : silence;
                TalkbackAudioLevel.SetForNextPacket(sender.AudioStream, pcm);
                sender.SendAudio(960, encoder.EncodeAudio(pcm, Opus));
                await Task.Delay(20);
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (received.Count < 10 && DateTime.UtcNow < deadline) await Task.Delay(50);

            var levels = received.ToArray();
            Assert.True(levels.Length >= 10, $"only {levels.Length} audio packets arrived");
            Assert.All(levels, l => Assert.NotNull(l));
            Assert.Contains((15, true), levels.Select(l => l!.Value));
            Assert.Contains((127, false), levels.Select(l => l!.Value));
            Assert.All(levels, l => Assert.True(l!.Value is (15, true) or (127, false), $"unexpected level {l}"));
        }
        finally
        {
            sender.close();
            receiver.close();
        }
    }

    private static (List<string> Audio, List<string> Video) MediaSections(string sdp)
    {
        var lines = sdp.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int audioAt = lines.FindIndex(l => l.StartsWith("m=audio"));
        int videoAt = lines.FindIndex(l => l.StartsWith("m=video"));
        Assert.True(audioAt >= 0 && videoAt > audioAt, "expected audio then video m-lines");
        int afterVideo = lines.FindIndex(videoAt + 1, l => l.StartsWith("m="));
        return (lines[audioAt..videoAt], lines[videoAt..(afterVideo < 0 ? lines.Count : afterVideo)]);
    }

    /// <summary>The RFC 6464 element of a one-byte-header (RFC 8285) extension block, or null.</summary>
    private static (int Level, bool Voice)? ReadAudioLevel(RTPHeader header)
    {
        if (header.HeaderExtensionFlag != 1 || header.ExtensionProfile != 0xBEDE || header.ExtensionPayload == null) return null;
        var data = header.ExtensionPayload;
        for (int i = 0; i < data.Length;)
        {
            if (data[i] == 0) { i++; continue; }
            int id = data[i] >> 4;
            int length = (data[i] & 0x0F) + 1;
            if (id == TalkbackAudioLevel.ExtensionId && i + 1 < data.Length)
            {
                return (data[i + 1] & 0x7F, (data[i + 1] & 0x80) != 0);
            }
            i += 1 + length;
        }
        return null;
    }
}
