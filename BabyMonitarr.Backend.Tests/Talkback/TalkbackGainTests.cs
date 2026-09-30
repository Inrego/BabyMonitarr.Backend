using BabyMonitarr.Backend.Talkback;

namespace BabyMonitarr.Backend.Tests.Talkback;

public class TalkbackGainTests
{
    [Fact]
    public void Unity_gain_leaves_samples_below_the_knee_unchanged()
    {
        short[] pcm = { 0, 1000, -1000, 20000, -26000 };
        short[] expected = (short[])pcm.Clone();

        TalkbackGain.Apply(pcm, 1.0);

        Assert.Equal(expected, pcm);
    }

    [Fact]
    public void Zero_gain_is_silence()
    {
        short[] pcm = { 1000, -32768, 32767 };

        TalkbackGain.Apply(pcm, 0.0);

        Assert.All(pcm, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Attenuation_scales_linearly()
    {
        short[] pcm = { 10000, -10000 };

        TalkbackGain.Apply(pcm, 0.5);

        Assert.Equal(new short[] { 5000, -5000 }, pcm);
    }

    [Fact]
    public void Boost_is_limited_below_full_scale_without_wrapping()
    {
        short[] pcm = { 30000, -30000, 32767, short.MinValue };

        TalkbackGain.Apply(pcm, TalkbackGain.MaxGain);

        Assert.All(pcm, s => Assert.True(Math.Abs((int)s) <= 32768));
        Assert.True(pcm[0] > 0 && pcm[1] < 0, "sign must survive the limiter");
        Assert.True(pcm[0] > 26214, "limited peaks still sit above the knee");
    }

    [Fact]
    public void Limiter_is_monotonic()
    {
        short previous = short.MinValue;
        for (int x = -32768; x <= 32767; x += 97)
        {
            short[] pcm = { (short)x };
            TalkbackGain.Apply(pcm, 1.8);
            Assert.True(pcm[0] >= previous);
            previous = pcm[0];
        }
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(5.0, 2.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.75, 0.75)]
    public void Clamp_keeps_gain_in_range(double input, double expected)
    {
        Assert.Equal(expected, TalkbackGain.Clamp(input));
    }
}
