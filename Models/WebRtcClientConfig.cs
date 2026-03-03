namespace BabyMonitarr.Backend.Models;

public sealed class WebRtcClientConfig
{
    public List<WebRtcClientIceServer> IceServers { get; set; } = new();
}

public sealed class WebRtcClientIceServer
{
    public string Urls { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Credential { get; set; }
}
