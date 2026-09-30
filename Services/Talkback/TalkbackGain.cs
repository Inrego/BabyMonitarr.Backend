namespace BabyMonitarr.Backend.Talkback;

/// <summary>
/// Talkback volume. The camera speaker's level is fixed, so loudness is set by scaling the
/// parent's voice; a soft limiter above <see cref="Knee"/> keeps boosted peaks from clipping hard.
/// </summary>
public static class TalkbackGain
{
    public const double MinGain = 0.0;
    public const double MaxGain = 2.0;

    /// <summary>Fraction of full scale above which the limiter starts compressing.</summary>
    public const double Knee = 0.8;

    public static double Clamp(double gain) =>
        double.IsFinite(gain) ? Math.Clamp(gain, MinGain, MaxGain) : 1.0;

    /// <summary>
    /// Scales 16-bit PCM in place. Unity gain still runs through the limiter, since the parent's
    /// mic can reach full scale on its own; samples under the knee come out unchanged.
    /// </summary>
    public static void Apply(short[] pcm, double gain)
    {
        for (int i = 0; i < pcm.Length; i++)
        {
            double x = pcm[i] / 32768.0 * gain;
            double magnitude = Math.Abs(x);
            if (magnitude > Knee)
            {
                // tanh maps (knee, ∞) smoothly onto (knee, 1): continuous and never above full scale.
                magnitude = Knee + (1 - Knee) * Math.Tanh((magnitude - Knee) / (1 - Knee));
                x = Math.CopySign(magnitude, x);
            }

            pcm[i] = (short)Math.Clamp(Math.Round(x * 32768.0), short.MinValue, short.MaxValue);
        }
    }
}
