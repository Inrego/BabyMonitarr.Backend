namespace BabyMonitarr.Backend.Models;

public sealed class WebRtcOptions
{
    public List<WebRtcIceServerOptions> IceServers { get; set; } = new()
    {
        new WebRtcIceServerOptions
        {
            Urls = "stun:stun.l.google.com:19302"
        }
    };

    public string? AdvertisedAddress { get; set; }
    public bool InferAdvertisedAddressFromForwardedHost { get; set; } = true;
    public string? BindAddress { get; set; }
    public int BindPort { get; set; } = 0;
    public bool IncludeAllInterfaceAddresses { get; set; } = false;
    public int GatherTimeoutMs { get; set; } = 0;
    public WebRtcRtpPortRangeOptions? RtpPortRange { get; set; }
}

public sealed class WebRtcIceServerOptions
{
    public string Urls { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Credential { get; set; }
}

public sealed class WebRtcRtpPortRangeOptions
{
    public int Start { get; set; }
    public int End { get; set; }
    public bool Shuffle { get; set; } = true;
    public int? RandomSeed { get; set; }
}
