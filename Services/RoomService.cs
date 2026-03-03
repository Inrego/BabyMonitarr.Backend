using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public interface IRoomService
{
    Task<List<Room>> GetAllRoomsAsync();
    Task<Room?> GetRoomAsync(int id);
    Task<Room> CreateRoomAsync(Room room);
    Task<Room?> UpdateRoomAsync(Room room);
    Task<bool> DeleteRoomAsync(int id);
    Task<Room?> SetActiveRoomAsync(int roomId);
    Task<Room?> GetActiveRoomAsync();
    Task UpdateRoomVideoCodecMetadataAsync(
        int roomId,
        string sourceCodecName,
        VideoPassthroughCodec? passthroughCodec,
        string? failureReason,
        DateTime checkedAtUtc);
    Task<GlobalSettings> GetGlobalSettingsAsync();
    Task<GlobalSettings> UpdateGlobalSettingsAsync(GlobalSettings settings);
    Task<AudioSettings> GetComposedAudioSettingsAsync();
    Task<AudioSettings> GetAudioSettingsForRoomAsync(int roomId);
}

public class RoomService : IRoomService
{
    private readonly BabyMonitarrDbContext _db;
    private readonly IVideoCodecProbeService _videoCodecProbeService;
    private readonly ILogger<RoomService> _logger;
    private static readonly TimeSpan CodecProbeTimeout = TimeSpan.FromSeconds(5);

    public RoomService(
        BabyMonitarrDbContext db,
        IVideoCodecProbeService videoCodecProbeService,
        ILogger<RoomService> logger)
    {
        _db = db;
        _videoCodecProbeService = videoCodecProbeService;
        _logger = logger;
    }

    public async Task<List<Room>> GetAllRoomsAsync()
    {
        return await _db.Rooms.OrderBy(r => r.CreatedAt).ToListAsync();
    }

    public async Task<Room?> GetRoomAsync(int id)
    {
        return await _db.Rooms.FindAsync(id);
    }

    public async Task<Room> CreateRoomAsync(Room room)
    {
        room.CreatedAt = DateTime.UtcNow;

        // If the name already exists, append a number to make it unique
        var baseName = room.Name;
        var suffix = 2;
        while (await _db.Rooms.AnyAsync(r => r.Name == room.Name))
        {
            room.Name = $"{baseName} {suffix}";
            suffix++;
        }

        await RefreshVideoCodecMetadataAsync(room, CancellationToken.None);

        _db.Rooms.Add(room);
        await _db.SaveChangesAsync();
        return room;
    }

    public async Task<Room?> UpdateRoomAsync(Room room)
    {
        var existing = await _db.Rooms.FindAsync(room.Id);
        if (existing == null) return null;

        bool shouldRefreshCodecMetadata =
            existing.VideoCodecCheckedAtUtc == null ||
            existing.EnableVideoStream != room.EnableVideoStream ||
            !string.Equals(existing.StreamSourceType, room.StreamSourceType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.CameraStreamUrl, room.CameraStreamUrl, StringComparison.Ordinal) ||
            !string.Equals(existing.CameraUsername, room.CameraUsername, StringComparison.Ordinal) ||
            !string.Equals(existing.CameraPassword, room.CameraPassword, StringComparison.Ordinal) ||
            !string.Equals(existing.NestDeviceId, room.NestDeviceId, StringComparison.Ordinal);

        existing.Name = room.Name;
        existing.Icon = room.Icon;
        existing.MonitorType = room.MonitorType;
        existing.EnableVideoStream = room.EnableVideoStream;
        existing.EnableAudioStream = room.EnableAudioStream;
        existing.CameraStreamUrl = room.CameraStreamUrl;
        existing.CameraUsername = room.CameraUsername;
        existing.CameraPassword = room.CameraPassword;
        existing.StreamSourceType = room.StreamSourceType;
        existing.NestDeviceId = room.NestDeviceId;

        if (shouldRefreshCodecMetadata)
        {
            await RefreshVideoCodecMetadataAsync(existing, CancellationToken.None);
        }

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteRoomAsync(int id)
    {
        var room = await _db.Rooms.FindAsync(id);
        if (room == null) return false;

        _db.Rooms.Remove(room);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Room?> SetActiveRoomAsync(int roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room == null) return null;

        // Deactivate all rooms
        await _db.Rooms.ExecuteUpdateAsync(r => r.SetProperty(x => x.IsActive, false));

        // Activate the selected room
        room.IsActive = true;
        await _db.SaveChangesAsync();
        return room;
    }

    public async Task<Room?> GetActiveRoomAsync()
    {
        return await _db.Rooms.FirstOrDefaultAsync(r => r.IsActive);
    }

    public async Task UpdateRoomVideoCodecMetadataAsync(
        int roomId,
        string sourceCodecName,
        VideoPassthroughCodec? passthroughCodec,
        string? failureReason,
        DateTime checkedAtUtc)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            return;
        }

        room.VideoSourceCodecName = string.IsNullOrWhiteSpace(sourceCodecName)
            ? null
            : sourceCodecName.Trim();
        room.VideoPassthroughCodec = passthroughCodec?.ToString();
        room.VideoCodecFailureReason = string.IsNullOrWhiteSpace(failureReason)
            ? null
            : failureReason.Trim();
        room.VideoCodecCheckedAtUtc = checkedAtUtc;

        await _db.SaveChangesAsync();
    }

    public async Task<GlobalSettings> GetGlobalSettingsAsync()
    {
        var settings = await _db.GlobalSettings.FindAsync(1);
        return settings ?? new GlobalSettings { Id = 1 };
    }

    public async Task<GlobalSettings> UpdateGlobalSettingsAsync(GlobalSettings settings)
    {
        var existing = await _db.GlobalSettings.FindAsync(1);
        if (existing == null)
        {
            settings.Id = 1;
            _db.GlobalSettings.Add(settings);
        }
        else
        {
            existing.SoundThreshold = settings.SoundThreshold;
            existing.AverageSampleCount = settings.AverageSampleCount;
            existing.FilterEnabled = settings.FilterEnabled;
            existing.LowPassFrequency = settings.LowPassFrequency;
            existing.HighPassFrequency = settings.HighPassFrequency;
            existing.ThresholdPauseDuration = settings.ThresholdPauseDuration;
            existing.VolumeAdjustmentDb = settings.VolumeAdjustmentDb;
        }

        await _db.SaveChangesAsync();
        return await GetGlobalSettingsAsync();
    }

    public async Task<AudioSettings> GetComposedAudioSettingsAsync()
    {
        var global = await GetGlobalSettingsAsync();
        var activeRoom = await GetActiveRoomAsync();

        return new AudioSettings
        {
            SoundThreshold = global.SoundThreshold,
            AverageSampleCount = global.AverageSampleCount,
            FilterEnabled = global.FilterEnabled,
            LowPassFrequency = global.LowPassFrequency,
            HighPassFrequency = global.HighPassFrequency,
            ThresholdPauseDuration = global.ThresholdPauseDuration,
            VolumeAdjustmentDb = global.VolumeAdjustmentDb,
            CameraStreamUrl = activeRoom?.CameraStreamUrl,
            CameraUsername = activeRoom?.CameraUsername,
            CameraPassword = activeRoom?.CameraPassword
        };
    }

    public async Task<AudioSettings> GetAudioSettingsForRoomAsync(int roomId)
    {
        var global = await GetGlobalSettingsAsync();
        var room = await GetRoomAsync(roomId);

        return new AudioSettings
        {
            SoundThreshold = global.SoundThreshold,
            AverageSampleCount = global.AverageSampleCount,
            FilterEnabled = global.FilterEnabled,
            LowPassFrequency = global.LowPassFrequency,
            HighPassFrequency = global.HighPassFrequency,
            ThresholdPauseDuration = global.ThresholdPauseDuration,
            VolumeAdjustmentDb = global.VolumeAdjustmentDb,
            CameraStreamUrl = room?.CameraStreamUrl,
            CameraUsername = room?.CameraUsername,
            CameraPassword = room?.CameraPassword
        };
    }

    private async Task RefreshVideoCodecMetadataAsync(Room room, CancellationToken cancellationToken)
    {
        room.VideoSourceCodecName = null;
        room.VideoPassthroughCodec = null;
        room.VideoCodecFailureReason = null;
        room.VideoCodecCheckedAtUtc = null;

        if (!room.EnableVideoStream)
        {
            return;
        }

        if (string.Equals(room.StreamSourceType, "google_nest", StringComparison.OrdinalIgnoreCase))
        {
            room.VideoSourceCodecName = "h264";
            room.VideoPassthroughCodec = VideoPassthroughCodec.H264.ToString();
            room.VideoCodecCheckedAtUtc = DateTime.UtcNow;
            return;
        }

        if (string.IsNullOrWhiteSpace(room.CameraStreamUrl))
        {
            room.VideoCodecFailureReason = "Camera stream URL is not configured.";
            room.VideoCodecCheckedAtUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CodecProbeTimeout);

            var probeResult = await _videoCodecProbeService.ProbeAsync(
                room.Id,
                room.CameraStreamUrl,
                room.CameraUsername,
                room.CameraPassword,
                timeoutCts.Token);

            room.VideoSourceCodecName = string.IsNullOrWhiteSpace(probeResult.SourceCodecName)
                ? null
                : probeResult.SourceCodecName.Trim();
            room.VideoPassthroughCodec = probeResult.PassthroughCodec?.ToString();
            room.VideoCodecFailureReason = string.IsNullOrWhiteSpace(probeResult.FailureReason)
                ? null
                : probeResult.FailureReason.Trim();
            room.VideoCodecCheckedAtUtc = probeResult.CheckedAtUtc;
        }
        catch (OperationCanceledException)
        {
            room.VideoCodecFailureReason = "Timed out while probing video source codec.";
            room.VideoCodecCheckedAtUtc = DateTime.UtcNow;
            _logger.LogWarning(
                "Timed out probing video codec for room {RoomId} ({Name})",
                room.Id,
                room.Name);
        }
        catch (Exception ex)
        {
            room.VideoCodecFailureReason = RtspDiagnostics.RedactFreeText(ex.Message);
            room.VideoCodecCheckedAtUtc = DateTime.UtcNow;
            _logger.LogWarning(
                ex,
                "Could not probe video codec for room {RoomId} ({Name})",
                room.Id,
                room.Name);
        }
    }
}
