namespace BabyMonitarr.Backend.Models;

public class AudioSettings
{
    /// <summary>
    /// Sound threshold level in decibels that triggers alerts
    /// </summary>
    public double SoundThreshold { get; set; } = -20.0; // Default value in dB
    
    /// <summary>
    /// Number of samples for average calculation
    /// </summary>
    public int AverageSampleCount { get; set; } = 10;
    
    /// <summary>
    /// Determines if audio filter is enabled
    /// </summary>
    public bool FilterEnabled { get; set; } = false;
    
    /// <summary>
    /// Filter low-pass cutoff frequency
    /// </summary>
    public int LowPassFrequency { get; set; } = 4000;
    
    /// <summary>
    /// Filter high-pass cutoff frequency
    /// </summary>
    public int HighPassFrequency { get; set; } = 300;
    
    /// <summary>
    /// Camera stream URL (RTSP, HTTP, etc.)
    /// </summary>
    public string? CameraStreamUrl { get; set; }
    
    /// <summary>
    /// Username for camera authentication (if required)
    /// </summary>
    public string? CameraUsername { get; set; }
    
    /// <summary>
    /// Password for camera authentication (if required)
    /// </summary>
    public string? CameraPassword { get; set; }
    
    /// <summary>
    /// Indicates whether the system should use the camera's audio stream instead of local microphone
    /// </summary>
    public bool UseCameraAudioStream { get; set; } = false;
    
    /// <summary>
    /// Number of seconds to pause threshold checking after threshold is exceeded
    /// </summary>
    public int ThresholdPauseDuration { get; set; } = 30; // Default pause of 30 seconds
    
    /// <summary>
    /// Volume adjustment in decibels for the audio stream (-20 to 20)
    /// Negative values reduce volume, positive values increase volume
    /// </summary>
    public double VolumeAdjustmentDb { get; set; } = -15.0; // Default -15dB to reduce baseline noise
}