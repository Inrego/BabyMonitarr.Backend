namespace BabyMonitarr.Backend.Models;

public sealed class CastDeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public bool IsVideoCapable { get; set; }
    public bool IsGroup { get; set; }
    public bool ManuallyAdded { get; set; }

    /// <summary>Which discovery path last saw this device — see <see cref="CastDeviceOrigins"/>.</summary>
    public string Origin { get; set; } = CastDeviceOrigins.Discovered;

    public int Port { get; set; } = 8009;
    public bool IsOnline { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Room currently being cast to this device, if any.</summary>
    public int? CastingRoomId { get; set; }

    /// <summary>Last error reported for this device, cleared on a successful start.</summary>
    public string? LastError { get; set; }
}

public sealed class CastSessionInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public int RoomId { get; set; }
    public bool Video { get; set; }
    public DateTime StartedAtUtc { get; set; }
}

public sealed class CastStartResult
{
    public List<CastSessionInfo> Started { get; set; } = new();

    /// <summary>Device id to failure reason for targets that could not be started.</summary>
    public Dictionary<string, string> Failed { get; set; } = new();
}

/// <summary>
/// One receiver seen by Home Assistant's Zeroconf browser and pushed to the backend, which cannot
/// see link-local multicast from a Docker bridge network. The fields are the address, the port and
/// the <c>_googlecast._tcp.local.</c> TXT record that CastDeviceService already parses itself.
/// </summary>
public sealed class CastProxyDiscovery
{
    /// <summary>TXT "id" — the same value CastDevice.DeviceId stores, so both paths dedupe.</summary>
    public string Id { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8009;

    /// <summary>TXT "fn", the friendly name.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>TXT "md", the model name.</summary>
    public string? Model { get; set; }

    /// <summary>TXT "ca", the capability bitmask. Null when the record did not carry one.</summary>
    public int? Capabilities { get; set; }
}
