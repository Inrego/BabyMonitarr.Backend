using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Hubs;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>A room's talkback capability and state, as the app sees it (docs/TALKBACK.md).</summary>
public sealed record TalkbackStatus(
    int RoomId,
    bool Supported,
    bool Available,
    string? UnavailableReason,
    string? Message,
    string State,
    bool Busy,
    double Volume);

/// <summary>A camera the user can map a room's talkback to.</summary>
public sealed record TalkbackCameraOption(string NestDeviceId, string GoogleUuid, string RoomName);

public interface ITalkbackService
{
    Task<TalkbackStatus> GetStatusAsync(int roomId);
    Task<TalkbackStartResult> StartAsync(string connectionId, int roomId);
    Task StopAsync(string connectionId, int roomId, string why);
    Task StopAllForConnectionAsync(string connectionId, string why);
    Task<TalkbackStartResult> PrepareAsync(int roomId);
    Task<TalkbackStatus> SetVolumeAsync(int roomId, double volume);
    Task<IReadOnlyList<TalkbackCameraOption>> GetCamerasAsync();

    /// <summary>Pins the room to a camera, or with null goes back to automatic matching.</summary>
    Task<TalkbackStatus> SetCameraAsync(int roomId, string? nestDeviceId);

    /// <summary>One decoded uplink packet: interleaved stereo 48 kHz PCM.</summary>
    void OnUplinkAudio(string connectionId, int roomId, short[] pcm);
}

public sealed class TalkbackService : ITalkbackService, IAsyncDisposable
{
    private const string NestSourceType = "google_nest";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGoogleHomeAuthService _auth;
    private readonly ITalkbackCameraDirectory _cameras;
    private readonly IFoyerTalkbackStreamFactory _streamFactory;
    private readonly IHubContext<AudioStreamHub> _hub;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TalkbackService> _logger;

    private readonly ConcurrentDictionary<int, TalkbackSession> _sessions = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    // SDM device → its room name; SDM calls are rate-limited and room names rarely change.
    private readonly ConcurrentDictionary<string, string> _sdmRoomNames = new();

    public TalkbackService(
        IServiceScopeFactory scopeFactory,
        IGoogleHomeAuthService auth,
        ITalkbackCameraDirectory cameras,
        IFoyerTalkbackStreamFactory streamFactory,
        IHubContext<AudioStreamHub> hub,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _auth = auth;
        _cameras = cameras;
        _streamFactory = streamFactory;
        _hub = hub;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TalkbackService>();

        _auth.StatusChanged += () => _ = BroadcastAllAsync();
    }

    public async Task<TalkbackStatus> GetStatusAsync(int roomId)
    {
        var room = await LoadRoomAsync(roomId);
        if (room == null || !IsNestRoom(room))
        {
            return new TalkbackStatus(roomId, false, false, TalkbackReasons.NotNest,
                "Talkback is only available for Google Nest cameras.", "closed", false, room?.TalkbackVolume ?? 1.0);
        }

        _sessions.TryGetValue(roomId, out var session);
        string state = StateName(session?.State ?? TalkbackState.Closed);
        bool busy = session?.State == TalkbackState.Talking;

        string? reason = null;
        string? message = null;
        try
        {
            await ResolveTargetAsync(room);
            if (session?.LastError is { } lastError && session.State == TalkbackState.Closed)
            {
                // Transient: the next StartTalkback retries, so talking stays available.
                reason = TalkbackReasons.CameraError;
                message = lastError;
            }
        }
        catch (TalkbackUnavailableException ex)
        {
            reason = ex.Reason;
            message = ex.Message;
        }

        bool available = reason is null or TalkbackReasons.CameraError;
        return new TalkbackStatus(roomId, true, available, reason, message, state, busy, room.TalkbackVolume);
    }

    public async Task<TalkbackStartResult> StartAsync(string connectionId, int roomId)
    {
        try
        {
            var session = await GetSessionAsync(roomId);
            return await session.StartAsync(connectionId);
        }
        catch (TalkbackUnavailableException ex)
        {
            _logger.LogInformation("Talkback unavailable for room {RoomId} ({Reason}): {Message}", roomId, ex.Reason, ex.Message);
            await BroadcastAsync(roomId);
            return new TalkbackStartResult(false, ex.Reason, ex.Message);
        }
    }

    public async Task<TalkbackStartResult> PrepareAsync(int roomId)
    {
        try
        {
            var session = await GetSessionAsync(roomId);
            return await session.PrepareAsync();
        }
        catch (TalkbackUnavailableException ex)
        {
            return new TalkbackStartResult(false, ex.Reason, ex.Message);
        }
    }

    public async Task StopAsync(string connectionId, int roomId, string why)
    {
        if (_sessions.TryGetValue(roomId, out var session))
        {
            await session.StopAsync(connectionId, why);
        }
    }

    public async Task StopAllForConnectionAsync(string connectionId, string why)
    {
        foreach (var session in _sessions.Values)
        {
            await session.StopAsync(connectionId, why);
        }
    }

    public void OnUplinkAudio(string connectionId, int roomId, short[] pcm)
    {
        if (_sessions.TryGetValue(roomId, out var session))
        {
            session.OnAudio(connectionId, pcm);
        }
    }

    public async Task<TalkbackStatus> SetVolumeAsync(int roomId, double volume)
    {
        double clamped = TalkbackGain.Clamp(volume);
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var room = await db.Rooms.FindAsync(roomId) ?? throw new KeyNotFoundException($"Room {roomId} not found");
            room.TalkbackVolume = clamped;
            await db.SaveChangesAsync();
        }

        if (_sessions.TryGetValue(roomId, out var session))
        {
            session.SetVolume(clamped);
        }

        _logger.LogInformation("Talkback volume for room {RoomId} set to {Volume:0.00}", roomId, clamped);
        return await BroadcastAsync(roomId);
    }

    public async Task<IReadOnlyList<TalkbackCameraOption>> GetCamerasAsync()
    {
        var cameras = await _cameras.GetCamerasAsync(refresh: true);
        return cameras.Select(c => new TalkbackCameraOption(c.NestDeviceId, c.GoogleUuid, c.RoomName)).ToList();
    }

    public async Task<TalkbackStatus> SetCameraAsync(int roomId, string? nestDeviceId)
    {
        TalkbackCamera? camera = null;
        if (!string.IsNullOrWhiteSpace(nestDeviceId))
        {
            var cameras = await _cameras.GetCamerasAsync();
            camera = cameras.FirstOrDefault(c => c.NestDeviceId == nestDeviceId)
                ?? throw new ArgumentException($"No Google Home camera {nestDeviceId}");
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var room = await db.Rooms.FindAsync(roomId) ?? throw new KeyNotFoundException($"Room {roomId} not found");
            room.TalkbackNestDeviceId = camera?.NestDeviceId;
            room.TalkbackGoogleUuid = camera?.GoogleUuid;
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("Talkback camera for room {RoomId} set to {Camera}", roomId, camera?.NestDeviceId ?? "automatic");
        await RemoveSessionAsync(roomId);
        return await BroadcastAsync(roomId);
    }

    /// <summary>The room's session, replaced when the room now resolves to a different camera.</summary>
    private async Task<TalkbackSession> GetSessionAsync(int roomId)
    {
        var room = await LoadRoomAsync(roomId);
        if (room == null || !IsNestRoom(room))
        {
            throw new TalkbackUnavailableException(TalkbackReasons.NotNest, "Talkback is only available for Google Nest cameras.");
        }

        var target = await ResolveTargetAsync(room);

        await _sessionGate.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(roomId, out var existing))
            {
                if (existing.Target == target) return existing;
                _sessions.TryRemove(roomId, out _);
                await existing.DisposeAsync();
            }

            var session = new TalkbackSession(target, _streamFactory, _time,
                _loggerFactory.CreateLogger<TalkbackSession>(), room.TalkbackVolume);
            session.Changed += () => _ = BroadcastAsync(roomId);
            _sessions[roomId] = session;
            return session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task RemoveSessionAsync(int roomId)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_sessions.TryRemove(roomId, out var session))
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Where this room's talkback plays. Throws <see cref="TalkbackUnavailableException"/> with the
    /// reason the app shows when it cannot be determined.
    /// </summary>
    private async Task<TalkbackTarget> ResolveTargetAsync(Room room)
    {
        var credential = await _auth.GetStatusAsync();
        if (!credential.Configured)
        {
            throw new TalkbackUnavailableException(TalkbackReasons.NotConfigured,
                "Talkback needs a Google Home credential; add it on the backend's System page.");
        }
        if (!credential.Linked && credential.LastErrorAtUtc != null)
        {
            throw new TalkbackUnavailableException(TalkbackReasons.CredentialFailing,
                credential.Message ?? "The Google Home credential stopped working; capture a new one.");
        }

        if (!string.IsNullOrEmpty(room.TalkbackNestDeviceId) && !string.IsNullOrEmpty(room.TalkbackGoogleUuid))
        {
            return new TalkbackTarget(room.Id, room.TalkbackNestDeviceId, room.TalkbackGoogleUuid);
        }

        var cameras = await _cameras.GetCamerasAsync();
        var match = TalkbackCameraMatcher.Match(await GetSdmRoomNameAsync(room.NestDeviceId!), cameras)
            ?? throw new TalkbackUnavailableException(TalkbackReasons.CameraNotMapped,
                "Choose which camera talkback plays on in the room's settings on the backend.");

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var stored = await db.Rooms.FindAsync(room.Id);
            if (stored != null)
            {
                stored.TalkbackNestDeviceId = match.NestDeviceId;
                stored.TalkbackGoogleUuid = match.GoogleUuid;
                await db.SaveChangesAsync();
            }
        }

        _logger.LogInformation("Talkback for room {RoomId} mapped automatically to the camera in Google Home room '{HomeRoom}'",
            room.Id, match.RoomName);
        return new TalkbackTarget(room.Id, match.NestDeviceId, match.GoogleUuid);
    }

    private async Task<string?> GetSdmRoomNameAsync(string sdmDeviceId)
    {
        if (_sdmRoomNames.TryGetValue(sdmDeviceId, out var cached)) return cached;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var devices = scope.ServiceProvider.GetRequiredService<IGoogleNestDeviceService>();
            var device = await devices.GetDeviceAsync(sdmDeviceId);
            if (device == null) return null;
            _sdmRoomNames[sdmDeviceId] = device.RoomName;
            return device.RoomName;
        }
        catch (Exception ex)
        {
            // Matching then falls back to "the only camera on the account".
            _logger.LogWarning("Could not read the SDM room of a Nest camera for talkback mapping: {Error}", ex.Message);
            return null;
        }
    }

    private async Task<TalkbackStatus> BroadcastAsync(int roomId)
    {
        var status = await GetStatusAsync(roomId);
        try
        {
            await _hub.Clients.All.SendAsync("TalkbackStatusChanged", status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Broadcasting talkback status for room {RoomId} failed: {Error}", roomId, ex.Message);
        }
        return status;
    }

    private async Task BroadcastAllAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var roomIds = await db.Rooms.Where(r => r.StreamSourceType == NestSourceType).Select(r => r.Id).ToListAsync();
            foreach (var roomId in roomIds)
            {
                await BroadcastAsync(roomId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Broadcasting talkback status failed: {Error}", ex.Message);
        }
    }

    private async Task<Room?> LoadRoomAsync(int roomId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        return await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId);
    }

    private static bool IsNestRoom(Room room) =>
        string.Equals(room.StreamSourceType, NestSourceType, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(room.NestDeviceId);

    private static string StateName(TalkbackState state) => state switch
    {
        TalkbackState.Connecting => "connecting",
        TalkbackState.Open => "open",
        TalkbackState.Talking => "talking",
        _ => "closed",
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }
}
