namespace BabyMonitarr.Backend.Models;

public class GlobalSettings
{
    public int Id { get; set; }
    public double SoundThreshold { get; set; } = -20.0;
    public int AverageSampleCount { get; set; } = 10;
    public bool FilterEnabled { get; set; }
    public int LowPassFrequency { get; set; } = 4000;
    public int HighPassFrequency { get; set; } = 300;
    public int ThresholdPauseDuration { get; set; } = 30;
    public double VolumeAdjustmentDb { get; set; } = -15.0;
}
