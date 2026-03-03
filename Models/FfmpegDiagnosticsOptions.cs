namespace BabyMonitarr.Backend.Models;

public sealed class FfmpegDiagnosticsOptions
{
    public bool Enabled { get; set; } = false;
    public string NativeLogLevel { get; set; } = "warning";
    public bool LogRtspOptions { get; set; } = true;
    public bool LogStreamMetadata { get; set; } = true;
    public bool LogFrameStats { get; set; } = true;
    public int FrameStatsInterval { get; set; } = 300;
    public bool RunFfprobeOnOpenFailure { get; set; } = true;
    public string FfprobePath { get; set; } = string.Empty;
    public int FfprobeTimeoutSeconds { get; set; } = 8;
    public string FfprobeRtspTransport { get; set; } = "tcp";
    public int FfprobeMaxLogLines { get; set; } = 120;
}
