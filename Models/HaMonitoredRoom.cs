namespace BabyMonitarr.Backend.Models;

/// <summary>
/// A room whose Home Assistant always-on monitoring switch is on. Presence of a row means
/// "re-establish the always-on sound subscriber for this room when the backend starts"; absence
/// means the switch is off.
///
/// Kept in its own table rather than as a column on <see cref="Room"/> on purpose: monitoring is
/// Home Assistant's state, not a property of the room, and <see cref="Room"/> is serialised to the
/// SignalR hub, the web dashboard and every app client, none of which should grow a field for it.
/// </summary>
public class HaMonitoredRoom
{
    public int Id { get; set; }
    public int RoomId { get; set; }
}
