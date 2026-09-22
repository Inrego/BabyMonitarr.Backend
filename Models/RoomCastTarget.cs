namespace BabyMonitarr.Backend.Models;

/// <summary>
/// A cast device pre-selected for a room. Presence of a row means "offer this target by
/// default when casting this room"; it does not start a session on its own.
/// </summary>
public class RoomCastTarget
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
}
