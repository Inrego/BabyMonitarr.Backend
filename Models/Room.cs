namespace BabyMonitarr.Backend.Models;

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "baby";
    public string MonitorType { get; set; } = "camera_audio";
    public bool EnableVideoStream { get; set; }
    public string? CameraStreamUrl { get; set; }
    public string? CameraUsername { get; set; }
    public string? CameraPassword { get; set; }
    public bool UseCameraAudioStream { get; set; }
    public string? FallbackAudioDevice { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
