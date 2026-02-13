namespace BabyMonitarr.Backend.Models;

public class NestStreamInfo
{
    public string SdpAnswer { get; set; } = string.Empty;
    public string MediaSessionId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
