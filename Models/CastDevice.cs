namespace BabyMonitarr.Backend.Models;

/// <summary>
/// A Google Cast receiver the backend has seen on the network, or that was added by hand.
/// Persisted so per-room target selections survive a restart and keep their friendly name
/// while the device is offline.
/// </summary>
public class CastDevice
{
    public int Id { get; set; }

    /// <summary>Stable Cast device id (mDNS "id" TXT record), or "host:port" for manual entries.</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8009;

    /// <summary>Raw "ca" capability bitmask from the mDNS TXT record.</summary>
    public int Capabilities { get; set; }

    /// <summary>True for Chromecast/TV/Nest Hub style receivers, false for speakers.</summary>
    public bool IsVideoCapable { get; set; }

    /// <summary>True for speaker groups and other multizone leaders.</summary>
    public bool IsGroup { get; set; }

    /// <summary>Added by IP instead of discovered — kept even when mDNS never sees it.</summary>
    public bool ManuallyAdded { get; set; }

    public DateTime? LastSeenUtc { get; set; }
}
