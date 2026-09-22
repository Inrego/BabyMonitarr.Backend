namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// Tuning for the Home Assistant WebSocket endpoint. Bound from the "Ha" configuration section.
/// </summary>
public class HaOptions
{
    /// <summary>
    /// How often coalesced sound levels are pushed to connected HA clients. One message per tick
    /// per connection, never one per audio frame.
    /// </summary>
    public int LevelBroadcastIntervalMs { get; set; } = 1000;

    /// <summary>
    /// How long the sound state stays true after the last threshold crossing. The underlying
    /// event is an edge with its own ThresholdPauseDuration mute window, so the state needs its
    /// own hold to be usable as a binary_sensor.
    /// </summary>
    public int SoundClearHoldSeconds { get; set; } = 30;

    /// <summary>
    /// How long a monitored room may go without an audio frame before it is reported offline.
    /// </summary>
    public int StreamOnlineTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How often rooms, global settings, active room and viewer count are re-read and pushed
    /// on change. These have no change notification of their own on the backend.
    /// </summary>
    public int StatePollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Outbound queue depth per connection. A client that cannot keep up is dropped rather than
    /// allowed to grow the queue without bound.
    /// </summary>
    public int SendQueueCapacity { get; set; } = 256;
}
