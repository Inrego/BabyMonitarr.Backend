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

    /// <summary>
    /// App id of the custom Web Receiver that plays video casts over WebRTC - sub-second delay
    /// instead of HLS's several seconds. Defaults to the project's published receiver (the page in
    /// wwwroot/cast, hosted on GitHub Pages), so nobody has to register anything. Set it empty to
    /// always use HLS, or to your own app id to host the page yourself. Used only when
    /// <see cref="BaseUrl"/> resolves to publicly trusted HTTPS, which the page needs to connect
    /// back; audio-only receivers cannot run a Web Receiver and always use HLS.
    /// </summary>
    public string? ReceiverAppId { get; set; } = PublishedReceiverAppId;

    /// <summary>The project's published receiver app.</summary>
    public const string? PublishedReceiverAppId = null;

    public int DiscoveryIntervalSeconds { get; set; } = 300;
    public int DiscoveryTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// HLS segment length in seconds. Lower means less cast latency and more segment churn.
    /// Copied H.264 sources can only split on keyframes, so their real segment length is the
    /// camera's keyframe interval when that is longer.
    /// </summary>
    public int SegmentSeconds { get; set; } = 1;

    /// <summary>
    /// Segments kept in the playlist window. Receivers start a few segments behind the live
    /// edge, so a smaller window is a shorter delay; below 4 the default receiver stalls.
    /// </summary>
    public int PlaylistSize { get; set; } = 4;

    /// <summary>Seconds an HLS stream stays alive after its last cast session ends.</summary>
    public int StreamLingerSeconds { get; set; } = 15;

    public string? FfmpegPath { get; set; }
}
