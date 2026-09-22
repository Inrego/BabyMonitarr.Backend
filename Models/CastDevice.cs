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

    /// <summary>
    /// Which path last saw this device: see <see cref="CastDeviceOrigins"/>. Diagnostics only —
    /// identity is <see cref="DeviceId"/>, so a device seen by several paths is still one row.
    /// </summary>
    public string Origin { get; set; } = CastDeviceOrigins.Discovered;

    public DateTime? LastSeenUtc { get; set; }
}

public static class CastDeviceOrigins
{
    /// <summary>Found by the backend's own mDNS browse. Needs host networking.</summary>
    public const string Discovered = "discovered";

    /// <summary>Added by IP through the UI.</summary>
    public const string Manual = "manual";

    /// <summary>Pushed in by Home Assistant's Zeroconf browser, which can see link-local multicast.</summary>
    public const string HaProxy = "ha-proxy";
}
