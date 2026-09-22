namespace BabyMonitarr.Backend.Models;

public sealed class CastOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL the Cast devices should fetch HLS from, e.g. "http://192.168.0.100:8080".
    /// Leave empty to derive it from WebRtc:AdvertisedAddress, then from the request host.
    /// Must be plain HTTP unless the certificate is publicly trusted — Cast receivers reject
    /// self-signed TLS.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Directory for generated HLS segments. Defaults to a folder under the system temp path.</summary>
    public string? HlsPath { get; set; }

    public int DiscoveryIntervalSeconds { get; set; } = 300;
    public int DiscoveryTimeoutSeconds { get; set; } = 5;

    /// <summary>HLS segment length in seconds. Lower means less cast latency and more segment churn.</summary>
    public int SegmentSeconds { get; set; } = 2;

    /// <summary>Segments kept in the playlist window.</summary>
    public int PlaylistSize { get; set; } = 6;

    /// <summary>Seconds an HLS stream stays alive after its last cast session ends.</summary>
    public int StreamLingerSeconds { get; set; } = 15;

    public string? FfmpegPath { get; set; }
}
