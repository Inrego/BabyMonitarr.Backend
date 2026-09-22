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
