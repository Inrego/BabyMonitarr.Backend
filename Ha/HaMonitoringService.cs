using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Hubs;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Ha;

public interface IHaMonitoringService
{
    /// <summary>Adds a connection and sends it the full state snapshot.</summary>
    Task RegisterAsync(HaConnection connection, CancellationToken ct);

    void Unregister(HaConnection connection);

    Task HandleMessageAsync(HaConnection connection, JsonElement root, CancellationToken ct);
}

/// <summary>
/// Keeps sound detection running independently of who is streaming, and fans the resulting state
/// out to the Home Assistant clients on /ha/ws.
///
/// The always-on part is deliberately boring: for a monitored room this registers an ordinary
/// <see cref="IAudioStreamingService.SubscribeToRoom"/> handler — the same kind an app client
/// registers — and stays subscribed, which keeps the lazily-started reader alive. There is no
/// second reader lifecycle here.
/// </summary>
public class HaMonitoringService : IHaMonitoringService, IHostedService, IDisposable
{
    private readonly ILogger<HaMonitoringService> _logger;
    private readonly IAudioStreamingService _audioStreamingService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<AudioStreamHub> _hubContext;
    private readonly IHaViewerCounter _viewerCounter;
    private readonly IAppVersionProvider _versionProvider;
    private readonly IHaCastBridge _castBridge;
    private readonly HaOptions _options;

    private readonly ConcurrentDictionary<string, HaConnection> _connections = new();
    private readonly ConcurrentDictionary<int, RoomMonitor> _monitors = new();

    private volatile IReadOnlyList<HaRoomInfo> _rooms = Array.Empty<HaRoomInfo>();
    private string? _lastRoomsFrame;
    private string? _lastSettingsFrame;
    private string? _lastActiveRoomFrame;
    private string? _lastViewersFrame;

    private int _pollInFlight;
    private Timer? _levelTimer;
    private Timer? _pollTimer;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public HaMonitoringService(
        ILogger<HaMonitoringService> logger,
        IAudioStreamingService audioStreamingService,
        IServiceScopeFactory scopeFactory,
        IHubContext<AudioStreamHub> hubContext,
        IHaViewerCounter viewerCounter,
        IAppVersionProvider versionProvider,
        IHaCastBridge castBridge,
        IOptions<HaOptions> options)
    {
        _logger = logger;
        _audioStreamingService = audioStreamingService;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _viewerCounter = viewerCounter;
        _versionProvider = versionProvider;
        _castBridge = castBridge;
        _options = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioStreamingService.SoundThresholdExceeded += OnSoundThresholdExceeded;

        var levelInterval = TimeSpan.FromMilliseconds(Math.Max(100, _options.LevelBroadcastIntervalMs));
        _levelTimer = new Timer(_ => OnLevelTick(), null, levelInterval, levelInterval);

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.StatePollIntervalSeconds));
        _pollTimer = new Timer(_ => _ = PollStateAsync(), null, TimeSpan.Zero, pollInterval);

        _logger.LogInformation("HA monitoring service started (level interval {Interval}ms, clear hold {Hold}s)",
            _options.LevelBroadcastIntervalMs, _options.SoundClearHoldSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _audioStreamingService.SoundThresholdExceeded -= OnSoundThresholdExceeded;
        _levelTimer?.Dispose();
        _pollTimer?.Dispose();
        _cts?.Cancel();

        foreach (var monitor in _monitors.Values)
        {
            StopMonitoring(monitor);
        }

        _connections.Clear();
        _logger.LogInformation("HA monitoring service stopped");
        return Task.CompletedTask;
    }

    #region Connections

    public async Task RegisterAsync(HaConnection connection, CancellationToken ct)
    {
        _connections[connection.Id] = connection;
        _logger.LogInformation("HA client {ConnectionId} connected as {User}. Total HA clients: {Count}",
            connection.Id, connection.UserName, _connections.Count);

        // A fresh client must be able to populate every entity from the snapshot alone.
        await RefreshRoomsCacheAsync();
        await SendSnapshotAsync(connection, null, ct);
    }

    public void Unregister(HaConnection connection)
    {
        if (_connections.TryRemove(connection.Id, out _))
        {
            _logger.LogInformation("HA client {ConnectionId} disconnected. Remaining HA clients: {Count}",
                connection.Id, _connections.Count);
        }

        // Monitoring deliberately survives the client dropping: a Home Assistant restart must not
        // create a sound-detection gap. It resets only when the backend restarts.
    }

    private async Task SendSnapshotAsync(HaConnection connection, string? reference, CancellationToken ct)
    {
        connection.TryEnqueue(HaFrames.Build(HaProtocol.Hello, new HaHelloData(
            HaProtocol.Version,
            _versionProvider.DisplayVersion,
            _options.LevelBroadcastIntervalMs,
            _options.SoundClearHoldSeconds,
            new[] { "monitoring", "sound_state", "sound_level", "global_settings", "rooms", "cast" }), reference));

        connection.TryEnqueue(_lastRoomsFrame ?? HaFrames.Build(HaProtocol.Rooms, new HaRoomsData(_rooms)));
        if (_lastSettingsFrame != null) connection.TryEnqueue(_lastSettingsFrame);
        if (_lastActiveRoomFrame != null) connection.TryEnqueue(_lastActiveRoomFrame);
        connection.TryEnqueue(_lastViewersFrame
            ?? HaFrames.Build(HaProtocol.ConnectedViewers, new HaConnectedViewersData(_viewerCounter.Count)));

        foreach (var room in _rooms)
        {
            connection.TryEnqueue(HaFrames.Build(HaProtocol.RoomState, BuildRoomState(room.Id)));
        }

        foreach (var frame in await _castBridge.BuildSnapshotFramesAsync(ct))
        {
            connection.TryEnqueue(frame);
        }

        connection.TryEnqueue(HaFrames.Build(HaProtocol.Ready, null, reference));
    }

    private void Broadcast(string frame)
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.TryEnqueue(frame)) continue;

            _logger.LogWarning("HA client {ConnectionId} is not draining its queue, closing it", connection.Id);
            _ = connection.CloseAsync(WebSocketCloseStatus.InternalServerError, "Send queue overflow", CancellationToken.None);
        }
    }

    #endregion

    #region Commands

    public async Task HandleMessageAsync(HaConnection connection, JsonElement root, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrBadRequest, "Missing 'type'", null));
            return;
        }

        string type = typeElement.GetString()!;
        string? reference = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;

        switch (type)
        {
            case HaProtocol.CmdPing:
                connection.TryEnqueue(HaFrames.Build(HaProtocol.Pong, null, reference));
                break;

            case HaProtocol.CmdGetState:
                await RefreshRoomsCacheAsync();
                await SendSnapshotAsync(connection, reference, ct);
                break;

            case HaProtocol.CmdSetMonitoring:
                await HandleSetMonitoringAsync(connection, data, reference);
                break;

            case HaProtocol.CmdSetGlobalSettings:
                await HandleSetGlobalSettingsAsync(connection, data, reference);
                break;

            case HaProtocol.CmdSetActiveRoom:
                await HandleSetActiveRoomAsync(connection, data, reference);
                break;

            default:
                if (_castBridge.Handles(type))
                {
                    var castResult = await _castBridge.HandleAsync(type, data, reference, connection.BaseUrl, ct);
                    foreach (var frame in castResult.Reply) connection.TryEnqueue(frame);
                    foreach (var frame in castResult.Broadcast) Broadcast(frame);
                    break;
                }

                connection.TryEnqueue(HaFrames.Error(
                    HaProtocol.ErrUnknownType,
                    $"Unsupported command '{type}' in protocol v{HaProtocol.Version}",
                    reference));
                break;
        }
    }

    private async Task HandleSetMonitoringAsync(HaConnection connection, JsonElement data, string? reference)
    {
        int? requestedRoom = HaJson.Int(data, "room_id");
        bool? requestedEnabled = HaJson.Bool(data, "enabled");
        if (requestedRoom == null || requestedEnabled == null)
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id and enabled", reference));
            return;
        }

        int roomId = requestedRoom.Value;
        bool enabled = requestedEnabled.Value;

        await RefreshRoomsCacheAsync();
        if (_rooms.All(r => r.Id != roomId))
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrUnknownRoom, $"No room {roomId}", reference));
            return;
        }

        SetMonitoring(roomId, enabled);

        Broadcast(HaFrames.Build(HaProtocol.Monitoring, new HaMonitoringData(roomId, enabled)));
        Broadcast(HaFrames.Build(HaProtocol.RoomState, BuildRoomState(roomId)));
        connection.TryEnqueue(HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdSetMonitoring), reference));
    }

    private async Task HandleSetGlobalSettingsAsync(HaConnection connection, JsonElement data, string? reference)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected a data object", reference));
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var settings = await roomService.GetGlobalSettingsAsync();

        if (HaJson.Double(data, "sound_threshold_db") is double threshold) settings.SoundThreshold = threshold;
        if (HaJson.Int(data, "threshold_pause_seconds") is int pause) settings.ThresholdPauseDuration = pause;
        if (HaJson.Double(data, "volume_adjustment_db") is double volume) settings.VolumeAdjustmentDb = volume;
        if (HaJson.Bool(data, "audio_filter_enabled") is bool filter) settings.FilterEnabled = filter;
        if (HaJson.Int(data, "average_sample_count") is int samples) settings.AverageSampleCount = samples;
        if (HaJson.Int(data, "low_pass_hz") is int lowPass) settings.LowPassFrequency = lowPass;
        if (HaJson.Int(data, "high_pass_hz") is int highPass) settings.HighPassFrequency = highPass;

        await roomService.UpdateGlobalSettingsAsync(settings);

        // Same follow-up the hub does, so processors pick the new values up and app clients refresh.
        _audioStreamingService.RefreshRooms();
        await _hubContext.Clients.All.SendAsync("SettingsUpdated");

        await PublishGlobalSettingsAsync(roomService);
        connection.TryEnqueue(HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdSetGlobalSettings), reference));
    }

    private async Task HandleSetActiveRoomAsync(HaConnection connection, JsonElement data, string? reference)
    {
        int? roomId = HaJson.Int(data, "room_id");
        if (roomId == null)
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrBadRequest, "Expected room_id", reference));
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var room = await roomService.SetActiveRoomAsync(roomId.Value);

        if (room == null)
        {
            connection.TryEnqueue(HaFrames.Error(HaProtocol.ErrUnknownRoom, $"No room {roomId}", reference));
            return;
        }

        await _hubContext.Clients.All.SendAsync("ActiveRoomChanged", room);
        PublishActiveRoom(room);
        connection.TryEnqueue(HaFrames.Build(HaProtocol.Ack, new HaAckData(HaProtocol.CmdSetActiveRoom), reference));
    }

    #endregion

    #region Monitoring subscription

    private void SetMonitoring(int roomId, bool enabled)
    {
        var monitor = _monitors.GetOrAdd(roomId, id => new RoomMonitor(id));
        Action<AudioFrameEventArgs> handler;

        // Subscribe/unsubscribe happens outside the lock: unsubscribing the last subscriber stops
        // the reader, and the reader thread may be inside OnAudioFrame waiting for this same lock.
        lock (monitor.Sync)
        {
            if (enabled)
            {
                if (monitor.Handler != null) return;

                handler = frame => OnAudioFrame(monitor, frame);
                monitor.Handler = handler;
            }
            else
            {
                if (monitor.Handler == null) return;

                handler = monitor.Handler;
                monitor.Handler = null;
                monitor.HasLevel = false;
                monitor.LastFrameUtc = DateTime.MinValue;
                monitor.StreamOnline = false;
            }
        }

        if (!enabled)
        {
            _audioStreamingService.UnsubscribeFromRoom(roomId, handler);
            _logger.LogInformation("HA monitoring disabled for room {RoomId}", roomId);
            return;
        }

        _audioStreamingService.SubscribeToRoom(roomId, handler);

        // A room created since the reader cache was last built is not started by SubscribeToRoom
        // alone; RefreshRooms starts readers for rooms that already have subscribers.
        _audioStreamingService.RefreshRooms();
        _logger.LogInformation("HA monitoring enabled for room {RoomId}", roomId);
    }

    private void StopMonitoring(RoomMonitor monitor)
    {
        Action<AudioFrameEventArgs>? handler;
        lock (monitor.Sync)
        {
            handler = monitor.Handler;
            monitor.Handler = null;
        }

        if (handler != null)
        {
            _audioStreamingService.UnsubscribeFromRoom(monitor.RoomId, handler);
        }
    }

    /// <summary>
    /// Runs on the reader thread for every audio frame, so it does no work beyond folding the
    /// frame into the per-room accumulator the level tick drains.
    /// </summary>
    private void OnAudioFrame(RoomMonitor monitor, AudioFrameEventArgs frame)
    {
        if (!double.IsFinite(frame.AudioLevel)) return;

        lock (monitor.Sync)
        {
            monitor.LastFrameUtc = DateTime.UtcNow;
            if (!monitor.HasLevel || frame.AudioLevel > monitor.PeakLevelDb)
            {
                monitor.PeakLevelDb = frame.AudioLevel;
            }
            monitor.HasLevel = true;
        }
    }

    private void OnSoundThresholdExceeded(object? sender, SoundThresholdEventArgs e)
    {
        var monitor = _monitors.GetOrAdd(e.RoomId, id => new RoomMonitor(id));
        bool becameDetected;

        lock (monitor.Sync)
        {
            monitor.LastSoundEventUtc = e.Timestamp;
            becameDetected = !monitor.SoundDetected;
            monitor.SoundDetected = true;
        }

        // The event is an edge with a mute window; the state carries its own clear hold, so both
        // are sent — one drives the binary_sensor, the other fires on the HA bus.
        Broadcast(HaFrames.Build(HaProtocol.SoundEvent,
            new HaSoundEventData(e.RoomId, e.AudioLevel, e.Threshold, e.Timestamp)));

        if (becameDetected)
        {
            Broadcast(HaFrames.Build(HaProtocol.SoundState,
                new HaSoundStateData(e.RoomId, true, e.Timestamp)));
        }
    }

    #endregion

    #region Timers

    private void OnLevelTick()
    {
        try
        {
            var now = DateTime.UtcNow;
            var levels = new List<HaLevelSample>();
            var onlineChanges = new List<HaStreamOnlineData>();
            var cleared = new List<HaSoundStateData>();
            var offlineTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.StreamOnlineTimeoutSeconds));
            var clearHold = TimeSpan.FromSeconds(Math.Max(1, _options.SoundClearHoldSeconds));

            foreach (var monitor in _monitors.Values)
            {
                lock (monitor.Sync)
                {
                    if (monitor.HasLevel)
                    {
                        // Coalesced as the peak over the interval: a short cry must not be averaged away.
                        levels.Add(new HaLevelSample(monitor.RoomId, Math.Round(monitor.PeakLevelDb, 2)));
                        monitor.LastLevelDb = monitor.PeakLevelDb;
                        monitor.HasLevel = false;
                    }

                    bool online = monitor.Handler != null && now - monitor.LastFrameUtc <= offlineTimeout;
                    if (online != monitor.StreamOnline)
                    {
                        monitor.StreamOnline = online;
                        onlineChanges.Add(new HaStreamOnlineData(monitor.RoomId, online));
                    }

                    if (monitor.SoundDetected && now - monitor.LastSoundEventUtc >= clearHold)
                    {
                        monitor.SoundDetected = false;
                        cleared.Add(new HaSoundStateData(monitor.RoomId, false, monitor.LastSoundEventUtc));
                    }
                }
            }

            if (_connections.IsEmpty) return;

            if (levels.Count > 0)
            {
                Broadcast(HaFrames.Build(HaProtocol.SoundLevel, new HaSoundLevelData(levels)));
            }

            foreach (var change in onlineChanges)
            {
                Broadcast(HaFrames.Build(HaProtocol.StreamOnline, change));
            }

            foreach (var clear in cleared)
            {
                Broadcast(HaFrames.Build(HaProtocol.SoundState, clear));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HA level broadcast tick");
        }
    }

    private async Task PollStateAsync()
    {
        // A slow database read must not let two polls overlap and double-broadcast.
        if (Interlocked.Exchange(ref _pollInFlight, 1) == 1) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();

            await RefreshRoomsCacheAsync(roomService);
            await PublishGlobalSettingsAsync(roomService);
            PublishActiveRoom(await roomService.GetActiveRoomAsync());

            var viewersFrame = HaFrames.Build(HaProtocol.ConnectedViewers, new HaConnectedViewersData(_viewerCounter.Count));
            if (!FramesEqual(_lastViewersFrame, viewersFrame))
            {
                _lastViewersFrame = viewersFrame;
                Broadcast(viewersFrame);
            }

            foreach (var frame in await _castBridge.PollChangedFramesAsync(CancellationToken.None))
            {
                Broadcast(frame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling state for HA clients");
        }
        finally
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    private async Task RefreshRoomsCacheAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        await RefreshRoomsCacheAsync(scope.ServiceProvider.GetRequiredService<IRoomService>());
    }

    private async Task RefreshRoomsCacheAsync(IRoomService roomService)
    {
        var rooms = await roomService.GetAllRoomsAsync();
        _rooms = rooms
            .Select(r => new HaRoomInfo(r.Id, r.Name, r.Icon, r.EnableAudioStream, r.EnableVideoStream, r.StreamSourceType))
            .ToList();

        var frame = HaFrames.Build(HaProtocol.Rooms, new HaRoomsData(_rooms));
        if (FramesEqual(_lastRoomsFrame, frame)) return;

        _lastRoomsFrame = frame;
        Broadcast(frame);
    }

    private async Task PublishGlobalSettingsAsync(IRoomService roomService)
    {
        var settings = await roomService.GetComposedAudioSettingsAsync();
        var frame = HaFrames.Build(HaProtocol.GlobalSettings, ToSettingsData(settings));
        if (FramesEqual(_lastSettingsFrame, frame)) return;

        _lastSettingsFrame = frame;
        Broadcast(frame);
    }

    private void PublishActiveRoom(Room? room)
    {
        var frame = HaFrames.Build(HaProtocol.ActiveRoom, new HaActiveRoomData(room?.Id, room?.Name));
        if (FramesEqual(_lastActiveRoomFrame, frame)) return;

        _lastActiveRoomFrame = frame;
        Broadcast(frame);
    }

    #endregion

    #region Helpers

    private HaRoomStateData BuildRoomState(int roomId)
    {
        if (!_monitors.TryGetValue(roomId, out var monitor))
        {
            return new HaRoomStateData(roomId, false, false, false, null);
        }

        lock (monitor.Sync)
        {
            return new HaRoomStateData(
                roomId,
                monitor.Handler != null,
                monitor.SoundDetected,
                monitor.StreamOnline,
                monitor.LastLevelDb.HasValue ? Math.Round(monitor.LastLevelDb.Value, 2) : null);
        }
    }

    private static HaGlobalSettingsData ToSettingsData(AudioSettings settings) => new(
        settings.SoundThreshold,
        settings.ThresholdPauseDuration,
        settings.VolumeAdjustmentDb,
        settings.FilterEnabled,
        settings.AverageSampleCount,
        settings.LowPassFrequency,
        settings.HighPassFrequency);

    private static bool FramesEqual(string? left, string? right) => HaFrames.PayloadEquals(left, right);

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;

        _levelTimer?.Dispose();
        _pollTimer?.Dispose();
        _cts?.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>Per-room monitoring state. Guarded by <see cref="Sync"/>.</summary>
    private sealed class RoomMonitor
    {
        public RoomMonitor(int roomId) => RoomId = roomId;

        public int RoomId { get; }
        public object Sync { get; } = new();
        public Action<AudioFrameEventArgs>? Handler { get; set; }
        public double PeakLevelDb { get; set; }
        public bool HasLevel { get; set; }
        public double? LastLevelDb { get; set; }
        public DateTime LastFrameUtc { get; set; } = DateTime.MinValue;
        public bool StreamOnline { get; set; }
        public bool SoundDetected { get; set; }
        public DateTime LastSoundEventUtc { get; set; } = DateTime.MinValue;
    }
}
