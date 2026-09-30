using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace BabyMonitarr.Backend.Tests.Talkback;

/// <summary>
/// The uplink decodes the app's Opus and the Foyer stream re-encodes it with the same format;
/// both sides rely on the decoder handing back interleaved stereo 20 ms frames.
/// </summary>
public class OpusFrameTests
{
    private static readonly AudioFormat Opus = new(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");

    [Fact]
    public void Decoded_frames_are_interleaved_stereo_20_ms()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var pcm = new short[960 * 2];
        for (int i = 0; i < 960; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 8000);
            pcm[2 * i] = s;
            pcm[2 * i + 1] = s;
        }

        byte[] encoded = encoder.EncodeAudio(pcm, Opus);
        short[] decoded = new AudioEncoder(includeOpus: true).DecodeAudio(encoded, Opus);

        Assert.NotEmpty(encoded);
        Assert.Equal(960 * 2, decoded.Length);
    }
}
