namespace BabyMonitarr.Backend.Models;

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "baby";
    public string MonitorType { get; set; } = "camera_audio";
    public bool EnableVideoStream { get; set; }
    public bool EnableAudioStream { get; set; } = true;
    public string? CameraStreamUrl { get; set; }
    public string? CameraUsername { get; set; }
    public string? CameraPassword { get; set; }
    public string StreamSourceType { get; set; } = "rtsp";
    public string? NestDeviceId { get; set; }

    /// <summary>Google Home's Nest id (<c>DEVICE_…</c>) of the camera talkback plays on.</summary>
    public string? TalkbackNestDeviceId { get; set; }

    /// <summary>Google Home graph uuid of the same camera.</summary>
    public string? TalkbackGoogleUuid { get; set; }

    /// <summary>Talkback gain, 0.0–2.0; 1.0 plays the parent's voice unchanged.</summary>
    public double TalkbackVolume { get; set; } = 1.0;
    public string? VideoSourceCodecName { get; set; }
    public string? VideoPassthroughCodec { get; set; }
    public string? VideoCodecFailureReason { get; set; }
    public DateTime? VideoCodecCheckedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
