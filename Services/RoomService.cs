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
    Task<GlobalSettings> GetGlobalSettingsAsync();
    Task<GlobalSettings> UpdateGlobalSettingsAsync(GlobalSettings settings);
    Task<AudioSettings> GetComposedAudioSettingsAsync();
}

public class RoomService : IRoomService
{
    private readonly BabyMonitarrDbContext _db;

    public RoomService(BabyMonitarrDbContext db)
    {
        _db = db;
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
        _db.Rooms.Add(room);
        await _db.SaveChangesAsync();
        return room;
    }

    public async Task<Room?> UpdateRoomAsync(Room room)
    {
        var existing = await _db.Rooms.FindAsync(room.Id);
        if (existing == null) return null;

        existing.Name = room.Name;
        existing.Icon = room.Icon;
        existing.MonitorType = room.MonitorType;
        existing.EnableVideoStream = room.EnableVideoStream;
        existing.CameraStreamUrl = room.CameraStreamUrl;
        existing.CameraUsername = room.CameraUsername;
        existing.CameraPassword = room.CameraPassword;
        existing.UseCameraAudioStream = room.UseCameraAudioStream;
        existing.FallbackAudioDevice = room.FallbackAudioDevice;

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
            CameraPassword = activeRoom?.CameraPassword,
            UseCameraAudioStream = activeRoom?.UseCameraAudioStream ?? false
        };
    }
}
