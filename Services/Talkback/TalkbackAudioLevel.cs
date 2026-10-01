using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>
/// The RFC 6464 client-to-mixer audio level (<c>urn:ietf:params:rtp-hdrext:ssrc-audio-level</c>)
/// that every talkback audio packet carries. Google's media relay only plays uplink talkback
/// audio whose RTP has this header extension (observed 2026-10-01); see docs/TALKBACK.md.
/// </summary>
public static class TalkbackAudioLevel
{
    /// <summary>The extmap id the Google Home web client offers for the audio level (and the camera's answer accepts).</summary>
    public const int ExtensionId = 1;

    /// <summary>The level of digital silence (and anything quieter than −127 dBov).</summary>
    public const int Silence = 127;

    /// <summary>
    /// Frames at or louder than this (−dBov) set the V (voice activity) bit. Far below any
    /// microphone's noise floor, so in practice only digital silence (the paced sender's
    /// filler frames) goes out with V=0.
    /// </summary>
    public const int VoiceThreshold = 90;

    public static RTPHeaderExtension CreateExtension() => new AudioLevelExtension(ExtensionId);

    /// <summary>
    /// The frame's RMS in −dBov (0 = full scale, 127 = silence or quieter), computed like
    /// WebRTC's RmsLevel (10·log10 of the mean square relative to 32768²).
    /// </summary>
    public static int Dbov(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length == 0) return Silence;
        double sumSquares = 0;
        foreach (short s in pcm) sumSquares += (double)s * s;
        double meanSquare = sumSquares / pcm.Length;
        if (meanSquare <= 0) return Silence;
        double dbov = 10 * Math.Log10(meanSquare / (32768.0 * 32768.0));
        return (int)Math.Clamp(Math.Round(-dbov), 0, Silence);
    }

    public static bool IsVoice(int level) => level <= VoiceThreshold;

    /// <summary>The extension value for one frame of PCM (empty = no PCM: silence, V=0).</summary>
    public static AudioLevelExtension.AudioLevel For(ReadOnlySpan<short> pcm)
    {
        int level = Dbov(pcm);
        return new AudioLevelExtension.AudioLevel { Level = (ushort)level, Voice = IsVoice(level) };
    }

    /// <summary>Sets the level the next audio packet sent on <paramref name="audio"/> carries.</summary>
    public static void SetForNextPacket(MediaStream audio, ReadOnlySpan<short> pcm) =>
        audio.SetRtpHeaderExtensionValue(AudioLevelExtension.RTP_HEADER_EXTENSION_URI, For(pcm));
}
