using System.Collections.Concurrent;
using System.Text.Json;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Ha;

public interface IHaCastBridge
{
    /// <summary>True when this message type belongs to the cast.* space.</summary>
    bool Handles(string type);

    Task<HaCommandResult> HandleAsync(
        string type, JsonElement data, string? reference, string? baseUrl, CancellationToken ct);

    /// <summary>Full cast state for a newly connected client.</summary>
    Task<IReadOnlyList<string>> BuildSnapshotFramesAsync(CancellationToken ct);

    /// <summary>Frames for whatever changed since the last poll, empty when nothing did.</summary>
    Task<IReadOnlyList<string>> PollChangedFramesAsync(CancellationToken ct);
}

/// <summary>
/// The cast.* half of the protocol: pushes device and per-room cast state to Home Assistant, and
/// maps its commands onto <see cref="ICastSessionService"/> and <see cref="ICastDeviceService"/>.
///
/// It returns frames rather than broadcasting them itself, which keeps it free of a dependency on
/// <see cref="HaMonitoringService"/> — that service depends on this one.
/// </summary>
public class HaCastBridge : IHaCastBridge
{
    private readonly ILogger<HaCastBridge> _logger;
    private readonly ICastDeviceService _castDeviceService;
    private readonly ICastSessionService _castSessionService;
    private readonly IServiceScopeFactory _scopeFactory;

    // Written by both the poll timer and a command handler, so it must tolerate concurrent writes.
    private volatile string? _lastDevicesFrame;
    private readonly ConcurrentDictionary<int, string> _lastStateFrames = new();

    public HaCastBridge(
        ILogger<HaCastBridge> logger,
        ICastDeviceService castDeviceService,
        ICastSessionService castSessionService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _castDeviceService = castDeviceService;
        _castSessionService = castSessionService;
        _scopeFactory = scopeFactory;
    }

    public bool Handles(string type) => type.StartsWith("cast.", StringComparison.Ordinal);

    public async Task<HaCommandResult> HandleAsync(
        string type, JsonElement data, string? reference, string? baseUrl, CancellationToken ct)
    {
        return type switch
        {
            HaProtocol.CmdCastDiscovered => await HandleDiscoveredAsync(data, reference, ct),
            HaProtocol.CmdCastStart => await HandleStartAsync(data, reference, baseUrl, ct),
            HaProtocol.CmdCastStop => await HandleStopAsync(data, reference, ct),
            HaProtocol.CmdCastStopDevice => await HandleStopDeviceAsync(data, reference, ct),
            HaProtocol.CmdCastSetTargets => await HandleSetTargetsAsync(data, reference, ct),
            _ => Reply(HaFrames.Error(
                HaProtocol.ErrUnknownType,
                $"Unsupported cast command '{type}' in protocol v{HaProtocol.Version}",
                reference))
        };
    }

    #region Commands

    private async Task<HaCommandResult> HandleDiscoveredAsync(
        JsonElement data, string? reference, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("devices", out var devicesElement) ||
            devicesElement.ValueKind != JsonValueKind.Array)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected a devices array", reference));
        }

        var discoveries = new List<CastProxyDiscovery>();
        foreach (var element in devicesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            string? id = HaJson.String(element, "id");
            string? host = HaJson.String(element, "host");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(host)) continue;

            discoveries.Add(new CastProxyDiscovery
            {
                Id = id!,
                Host = host!,
                Port = HaJson.Int(element, "port") ?? 8009,
                FriendlyName = HaJson.String(element, "fn"),
                Model = HaJson.String(element, "md"),
                // "ca" is a TXT record, so it may arrive as a JSON number or as a string.
                Capabilities = HaJson.IntOrParsedString(element, "ca")
            });
        }

        if (discoveries.Count == 0)
        {
            return Reply(HaFrames.Error(
                HaProtocol.ErrBadRequest, "No device carried both an id and a host", reference));
        }

        await _castDeviceService.UpsertProxyDiscoveriesAsync(discoveries, ct);

        return new HaCommandResult(
            new[] { HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdCastDiscovered), reference) },
            new[] { await BuildDevicesFrameAsync(ct) });
    }

    private async Task<HaCommandResult> HandleStartAsync(
        JsonElement data, string? reference, string? baseUrl, CancellationToken ct)
    {
        int? roomId = HaJson.Int(data, "room_id");
        if (roomId == null)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id", reference));
        }

        // No explicit targets means the room's saved selection, exactly as the hub's StartCast does.
        var deviceIds = HaJson.StringArray(data, "device_ids");
        if (deviceIds.Count == 0)
        {
            deviceIds = (await _castDeviceService.GetRoomTargetsAsync(roomId.Value, ct)).ToList();
        }

        if (deviceIds.Count == 0)
        {
            return Reply(HaFrames.Build(
                HaProtocol.CastStartResult,
                new HaCastStartResultData(roomId.Value, Array.Empty<HaCastSessionInfo>(),
                    new Dictionary<string, string>()),
                reference));
        }

        // Not ct: a cast is server-side state and must not be torn down because the Home Assistant
        // socket dropped while the start was in flight. The hub starts casts the same way.
        var result = await _castSessionService.StartAsync(
            roomId.Value, deviceIds, baseUrl, CancellationToken.None);

        // Per-device failures are surfaced, never swallowed.
        if (result.Failed.Count > 0)
        {
            _logger.LogWarning("HA cast start for room {RoomId} failed on {Count} device(s): {Failures}",
                roomId.Value, result.Failed.Count, string.Join("; ", result.Failed.Select(f => $"{f.Key}: {f.Value}")));
        }

        var reply = HaFrames.Build(
            HaProtocol.CastStartResult,
            new HaCastStartResultData(
                roomId.Value,
                result.Started.Select(ToSessionInfo).ToList(),
                result.Failed),
            reference);

        return new HaCommandResult(new[] { reply }, await BuildChangeFramesAsync(roomId.Value, ct));
    }

    private async Task<HaCommandResult> HandleStopAsync(
        JsonElement data, string? reference, CancellationToken ct)
    {
        int? roomId = HaJson.Int(data, "room_id");
        if (roomId == null)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id", reference));
        }

        await _castSessionService.StopRoomAsync(roomId.Value);

        return new HaCommandResult(
            new[] { HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdCastStop), reference) },
            await BuildChangeFramesAsync(roomId.Value, ct));
    }

    private async Task<HaCommandResult> HandleStopDeviceAsync(
        JsonElement data, string? reference, CancellationToken ct)
    {
        string? deviceId = HaJson.String(data, "device_id");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected device_id", reference));
        }

        // Capture the room before stopping; afterwards the device is no longer mapped to one.
        int? roomId = _castSessionService.RoomForDevice(deviceId!);
        bool stopped = await _castSessionService.StopDeviceAsync(deviceId!);

        if (!stopped && roomId == null)
        {
            return Reply(HaFrames.Error(
                HaProtocol.ErrUnknownDevice, $"No cast session on device '{deviceId}'", reference));
        }

        return new HaCommandResult(
            new[] { HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdCastStopDevice), reference) },
            await BuildChangeFramesAsync(roomId, ct));
    }

    private async Task<HaCommandResult> HandleSetTargetsAsync(
        JsonElement data, string? reference, CancellationToken ct)
    {
        int? roomId = HaJson.Int(data, "room_id");
        if (roomId == null)
        {
            return Reply(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id", reference));
        }

        await _castDeviceService.SetRoomTargetsAsync(roomId.Value, HaJson.StringArray(data, "device_ids"), ct);

        return new HaCommandResult(
            new[] { HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdCastSetTargets), reference) },
            await BuildChangeFramesAsync(roomId.Value, ct));
    }

    #endregion

    #region State

    public async Task<IReadOnlyList<string>> BuildSnapshotFramesAsync(CancellationToken ct)
    {
        var frames = new List<string> { await BuildDevicesFrameAsync(ct) };

        foreach (int roomId in await GetRoomIdsAsync(ct))
        {
            frames.Add(await BuildStateFrameAsync(roomId, ct));
        }

        return frames;
    }

    public async Task<IReadOnlyList<string>> PollChangedFramesAsync(CancellationToken ct)
    {
        var changed = new List<string>();

        var devicesFrame = await BuildDevicesFrameAsync(ct);
        if (!HaFrames.PayloadEquals(_lastDevicesFrame, devicesFrame))
        {
            _lastDevicesFrame = devicesFrame;
            changed.Add(devicesFrame);
        }

        foreach (int roomId in await GetRoomIdsAsync(ct))
        {
            var stateFrame = await BuildStateFrameAsync(roomId, ct);
            if (_lastStateFrames.TryGetValue(roomId, out var previous) &&
                HaFrames.PayloadEquals(previous, stateFrame))
            {
                continue;
            }

            _lastStateFrames[roomId] = stateFrame;
            changed.Add(stateFrame);
        }

        return changed;
    }

    /// <summary>Devices always change with a session, so both frames go out together.</summary>
    private async Task<IReadOnlyList<string>> BuildChangeFramesAsync(int? roomId, CancellationToken ct)
    {
        var frames = new List<string> { await BuildDevicesFrameAsync(ct) };
        _lastDevicesFrame = frames[0];

        if (roomId != null)
        {
            var stateFrame = await BuildStateFrameAsync(roomId.Value, ct);
            _lastStateFrames[roomId.Value] = stateFrame;
            frames.Add(stateFrame);
        }

        return frames;
    }

    private async Task<string> BuildDevicesFrameAsync(CancellationToken ct)
    {
        var devices = await _castDeviceService.GetDevicesAsync(ct);
        var infos = devices
            .Select(d => new HaCastDeviceInfo(
                d.DeviceId,
                d.Name,
                d.Model,
                d.Host,
                d.Port,
                d.Origin,
                d.ManuallyAdded,
                d.IsVideoCapable,
                d.IsGroup,
                d.IsOnline,
                d.LastSeenUtc,
                _castSessionService.RoomForDevice(d.DeviceId),
                _castSessionService.LastErrorForDevice(d.DeviceId)))
            .ToList();

        return HaFrames.Build(HaProtocol.CastDevices, new HaCastDevicesData(infos));
    }

    private async Task<string> BuildStateFrameAsync(int roomId, CancellationToken ct)
    {
        var targets = await _castDeviceService.GetRoomTargetsAsync(roomId, ct);
        var sessions = _castSessionService.GetSessions()
            .Where(s => s.RoomId == roomId)
            .Select(ToSessionInfo)
            .ToList();

        return HaFrames.Build(HaProtocol.CastState, new HaCastStateData(
            roomId, sessions.Count > 0, targets.ToList(), sessions));
    }

    private async Task<IReadOnlyList<int>> GetRoomIdsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var rooms = await roomService.GetAllRoomsAsync();
        return rooms.Select(r => r.Id).ToList();
    }

    #endregion

    private static HaCastSessionInfo ToSessionInfo(CastSessionInfo session) =>
        new(session.DeviceId, session.Video, session.StartedAtUtc);

    private static HaCommandResult Reply(string frame) =>
        new(new[] { frame }, Array.Empty<string>());
}
