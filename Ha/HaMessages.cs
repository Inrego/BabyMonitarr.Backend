using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// Wire contract for /ha/ws. Documented in docs/ha-websocket-protocol.md — keep the two in step.
/// </summary>
public static class HaProtocol
{
    /// <summary>Bumped only for a breaking change to the envelope or to an existing message type.</summary>
    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Server -> client
    public const string Hello = "hello";
    public const string Rooms = "rooms";
    public const string GlobalSettings = "global_settings";
    public const string ActiveRoom = "active_room";
    public const string ConnectedViewers = "connected_viewers";
    public const string RoomState = "room_state";
    public const string Ready = "ready";
    public const string SoundLevel = "sound_level";
    public const string SoundState = "sound_state";
    public const string SoundEvent = "sound_event";
    public const string StreamOnline = "stream_online";
    public const string Monitoring = "monitoring";
    public const string Ack = "ack";
    public const string Error = "error";
    public const string Pong = "pong";

    // Client -> server
    public const string CmdPing = "ping";
    public const string CmdGetState = "get_state";
    public const string CmdSetMonitoring = "set_monitoring";
    public const string CmdSetGlobalSettings = "set_global_settings";
    public const string CmdSetActiveRoom = "set_active_room";

    // Error codes
    public const string ErrBadRequest = "bad_request";
    public const string ErrUnknownType = "unknown_type";
    public const string ErrUnknownRoom = "unknown_room";
    public const string ErrInternal = "internal_error";
}

/// <summary>Every frame in both directions is one of these, JSON, one message per frame.</summary>
public sealed record HaEnvelope
{
    [JsonPropertyName("v")] public int V { get; init; } = HaProtocol.Version;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("ts")] public DateTime Ts { get; init; } = DateTime.UtcNow;
    /// <summary>Echo of the client message id this frame answers, when it answers one.</summary>
    [JsonPropertyName("ref")] public string? Ref { get; init; }
    [JsonPropertyName("data")] public object? Data { get; init; }
}

public sealed record HaHelloData(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("server_version")] string ServerVersion,
    [property: JsonPropertyName("level_interval_ms")] int LevelIntervalMs,
    [property: JsonPropertyName("sound_clear_hold_seconds")] int SoundClearHoldSeconds,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features);

public sealed record HaRoomInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("audio_enabled")] bool AudioEnabled,
    [property: JsonPropertyName("video_enabled")] bool VideoEnabled,
    [property: JsonPropertyName("source_type")] string SourceType);

public sealed record HaRoomsData(
    [property: JsonPropertyName("rooms")] IReadOnlyList<HaRoomInfo> Rooms);

public sealed record HaGlobalSettingsData(
    [property: JsonPropertyName("sound_threshold_db")] double SoundThresholdDb,
    [property: JsonPropertyName("threshold_pause_seconds")] int ThresholdPauseSeconds,
    [property: JsonPropertyName("volume_adjustment_db")] double VolumeAdjustmentDb,
    [property: JsonPropertyName("audio_filter_enabled")] bool AudioFilterEnabled,
    [property: JsonPropertyName("average_sample_count")] int AverageSampleCount,
    [property: JsonPropertyName("low_pass_hz")] int LowPassHz,
    [property: JsonPropertyName("high_pass_hz")] int HighPassHz);

public sealed record HaActiveRoomData(
    [property: JsonPropertyName("room_id")] int? RoomId,
    [property: JsonPropertyName("name")] string? Name);

public sealed record HaConnectedViewersData(
    [property: JsonPropertyName("count")] int Count);

public sealed record HaRoomStateData(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("monitoring")] bool Monitoring,
    [property: JsonPropertyName("sound_detected")] bool SoundDetected,
    [property: JsonPropertyName("stream_online")] bool StreamOnline,
    [property: JsonPropertyName("level_db")] double? LevelDb);

public sealed record HaLevelSample(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("level_db")] double LevelDb);

public sealed record HaSoundLevelData(
    [property: JsonPropertyName("levels")] IReadOnlyList<HaLevelSample> Levels);

public sealed record HaSoundStateData(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("last_event_at")] DateTime? LastEventAt);

public sealed record HaSoundEventData(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("level_db")] double LevelDb,
    [property: JsonPropertyName("threshold_db")] double ThresholdDb,
    [property: JsonPropertyName("at")] DateTime At);

public sealed record HaStreamOnlineData(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("online")] bool Online);

public sealed record HaMonitoringData(
    [property: JsonPropertyName("room_id")] int RoomId,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record HaAckData(
    [property: JsonPropertyName("command")] string Command);

public sealed record HaErrorData(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
