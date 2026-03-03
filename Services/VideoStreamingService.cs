using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services
{
    public sealed class RoomVideoSourceInfo
    {
        public int RoomId { get; set; }
        public string SourceCodecName { get; set; } = string.Empty;
        public VideoPassthroughCodec? PassthroughCodec { get; set; }
        public string? FailureReason { get; set; }
        public bool IsSupported => PassthroughCodec.HasValue;
    }

    public interface IVideoStreamingService
    {
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken cancellationToken = default);
        void SubscribeToRoom(int roomId, Action<VideoFrameEventArgs> handler);
        void UnsubscribeFromRoom(int roomId, Action<VideoFrameEventArgs> handler);
        Task<RoomVideoSourceInfo> GetRoomVideoSourceInfoAsync(int roomId, CancellationToken cancellationToken);
        void EnsureReaderStoppedIfNoSubscribers(int roomId);
        void RefreshRooms();
        bool IsNestRoom(int roomId);
    }

    public class VideoStreamingService : IVideoStreamingService, IHostedService, IDisposable
    {
        private readonly ILogger<VideoStreamingService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly NestStreamReaderManager _nestReaderManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptionsMonitor<FfmpegDiagnosticsOptions> _diagnosticsOptions;
        private readonly FfprobeSnapshotService _ffprobeSnapshotService;
        private readonly ConcurrentDictionary<int, IDisposable> _readers = new();
        private readonly ConcurrentDictionary<int, ConcurrentBag<Action<VideoFrameEventArgs>>> _subscribers = new();
        private readonly ConcurrentDictionary<int, Room> _roomCache = new();
        private readonly ConcurrentDictionary<int, RoomVideoSourceInfo> _sourceInfos = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<RoomVideoSourceInfo>> _sourceInfoWaiters = new();
        private CancellationTokenSource? _cts;
        private bool _isDisposed;

        public VideoStreamingService(
            ILogger<VideoStreamingService> logger,
            IServiceScopeFactory scopeFactory,
            NestStreamReaderManager nestReaderManager,
            ILoggerFactory loggerFactory,
            IOptionsMonitor<FfmpegDiagnosticsOptions> diagnosticsOptions,
            FfprobeSnapshotService ffprobeSnapshotService)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _nestReaderManager = nestReaderManager;
            _loggerFactory = loggerFactory;
            _diagnosticsOptions = diagnosticsOptions;
            _ffprobeSnapshotService = ffprobeSnapshotService;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _logger.LogInformation("VideoStreamingService starting...");
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await LoadAndStartReaders();

            _logger.LogInformation("VideoStreamingService started");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("VideoStreamingService stopping...");
            _cts?.Cancel();

            foreach (var kvp in _readers)
            {
                StopReader(kvp.Key);
            }

            foreach (var waiter in _sourceInfoWaiters.Values)
            {
                waiter.TrySetCanceled();
            }

            _readers.Clear();
            _roomCache.Clear();
            _sourceInfos.Clear();
            _sourceInfoWaiters.Clear();
            _logger.LogInformation("VideoStreamingService stopped");
            return Task.CompletedTask;
        }

        public void SubscribeToRoom(int roomId, Action<VideoFrameEventArgs> handler)
        {
            var bag = _subscribers.GetOrAdd(roomId, _ => new ConcurrentBag<Action<VideoFrameEventArgs>>());
            bag.Add(handler);

            _logger.LogInformation("Client subscribed to video for room {RoomId}. Total subscribers: {Count}", roomId, bag.Count);

            // Lazy start: if this is the first subscriber and a reader exists but isn't started, start it
            EnsureReaderStarted(roomId);
        }

        public void UnsubscribeFromRoom(int roomId, Action<VideoFrameEventArgs> handler)
        {
            if (_subscribers.TryGetValue(roomId, out var bag))
            {
                // ConcurrentBag doesn't support removal, so rebuild it without the handler
                var remaining = new ConcurrentBag<Action<VideoFrameEventArgs>>(
                    bag.Where(h => h != handler));
                _subscribers[roomId] = remaining;

                _logger.LogInformation("Client unsubscribed from video for room {RoomId}. Remaining subscribers: {Count}", roomId, remaining.Count);

                // If no subscribers left, stop the reader to save CPU
                if (remaining.IsEmpty)
                {
                    _logger.LogInformation("No subscribers left for room {RoomId}, stopping video reader", roomId);
                    StopReader(roomId);
                }
            }
        }

        public async Task<RoomVideoSourceInfo> GetRoomVideoSourceInfoAsync(int roomId, CancellationToken cancellationToken)
        {
            if (!_roomCache.TryGetValue(roomId, out var room))
            {
                throw new KeyNotFoundException($"Room {roomId} is not registered for video streaming.");
            }

            if (_sourceInfos.TryGetValue(roomId, out var existingInfo))
            {
                return existingInfo;
            }

            if (TryGetPersistedSourceInfo(room, out var persistedSourceInfo))
            {
                PublishSourceInfo(persistedSourceInfo);
                return persistedSourceInfo;
            }

            if (room.StreamSourceType == "google_nest")
            {
                var nestInfo = CreateNestSourceInfo(roomId);
                PublishSourceInfo(nestInfo);
                return nestInfo;
            }

            EnsureReaderStarted(roomId);

            if (_sourceInfos.TryGetValue(roomId, out existingInfo))
            {
                return existingInfo;
            }

            var waiter = _sourceInfoWaiters.GetOrAdd(roomId, _ => CreateSourceInfoWaiter());
            return await waiter.Task.WaitAsync(cancellationToken);
        }

        public void EnsureReaderStoppedIfNoSubscribers(int roomId)
        {
            if (_subscribers.TryGetValue(roomId, out var subscribers) && !subscribers.IsEmpty)
            {
                return;
            }

            StopReader(roomId);
        }

        public void RefreshRooms()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshRoomsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing video rooms");
                }
            });
        }

        private async Task RefreshRoomsAsync()
        {
            List<Room> rooms;
            using (var scope = _scopeFactory.CreateScope())
            {
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                rooms = await roomService.GetAllRoomsAsync();
            }

            var videoRooms = rooms
                .Where(r => r.EnableVideoStream &&
                    (r.StreamSourceType == "google_nest" ? !string.IsNullOrEmpty(r.NestDeviceId) : !string.IsNullOrEmpty(r.CameraStreamUrl)))
                .ToDictionary(r => r.Id);

            // Stop readers for rooms that no longer need video
            foreach (var roomId in _readers.Keys.ToList())
            {
                if (!videoRooms.ContainsKey(roomId))
                {
                    _logger.LogInformation("Room {RoomId} no longer needs video, stopping reader", roomId);
                    StopReader(roomId);
                    _readers.TryRemove(roomId, out _);
                    _roomCache.TryRemove(roomId, out _);
                    _sourceInfos.TryRemove(roomId, out _);

                    if (_sourceInfoWaiters.TryRemove(roomId, out var waiter))
                    {
                        waiter.TrySetCanceled();
                    }
                }
            }

            // Add/update readers for rooms that need video
            foreach (var room in videoRooms.Values)
            {
                var oldRoom = _roomCache.GetValueOrDefault(room.Id);
                _roomCache[room.Id] = room;
                CachePersistedSourceInfo(room);

                if (!_readers.ContainsKey(room.Id))
                {
                    // New room - only start if there are subscribers (lazy start)
                    if (_subscribers.TryGetValue(room.Id, out var subs) && !subs.IsEmpty)
                    {
                        StartReader(room);
                    }
                }
                else if (oldRoom != null && oldRoom.CameraStreamUrl != room.CameraStreamUrl)
                {
                    // URL changed - restart the reader
                    _logger.LogInformation("Camera URL changed for room {RoomId}, restarting reader", room.Id);
                    ResetSourceInfoState(room.Id);
                    StopReader(room.Id);
                    _readers.TryRemove(room.Id, out _);

                    if (_subscribers.TryGetValue(room.Id, out var subs) && !subs.IsEmpty)
                    {
                        StartReader(room);
                    }
                }
            }
        }

        private async Task LoadAndStartReaders()
        {
            List<Room> rooms;
            using (var scope = _scopeFactory.CreateScope())
            {
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                rooms = await roomService.GetAllRoomsAsync();
            }

            foreach (var room in rooms.Where(r => r.EnableVideoStream &&
                (r.StreamSourceType == "google_nest" ? !string.IsNullOrEmpty(r.NestDeviceId) : !string.IsNullOrEmpty(r.CameraStreamUrl))))
            {
                _roomCache[room.Id] = room;
                CachePersistedSourceInfo(room);
                _logger.LogInformation("Video-enabled room {RoomId} ({Name}) [{SourceType}] registered, will start on first subscriber",
                    room.Id, room.Name, room.StreamSourceType);
            }
        }

        private void EnsureReaderStarted(int roomId)
        {
            if (_readers.ContainsKey(roomId)) return;

            if (_roomCache.TryGetValue(roomId, out var room))
            {
                StartReader(room);
            }
        }

        private void StartReader(Room room)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;

            try
            {
                ResetSourceInfoState(room.Id);

                if (room.StreamSourceType == "google_nest" && !string.IsNullOrEmpty(room.NestDeviceId))
                {
                    PublishSourceInfo(CreateNestSourceInfo(room.Id));

                    // Google Nest: use shared NestStreamReader
                    var nestReader = _nestReaderManager.GetOrCreateReader(room.Id, room.NestDeviceId, _cts.Token);
                    nestReader.VideoFrameReceived += OnVideoFrameReceived;

                    if (_readers.TryAdd(room.Id, nestReader))
                    {
                        _logger.LogInformation("Started Nest video reader for room {RoomId} ({Name})", room.Id, room.Name);
                    }
                    else
                    {
                        nestReader.VideoFrameReceived -= OnVideoFrameReceived;
                        _nestReaderManager.ReleaseReader(room.Id);
                    }
                }
                else
                {
                    // RTSP: existing behavior
                    var readerLogger = _loggerFactory.CreateLogger<RtspVideoReader>();
                    var reader = new RtspVideoReader(
                        room,
                        readerLogger,
                        _diagnosticsOptions,
                        _ffprobeSnapshotService);
                    reader.VideoFrameReceived += OnVideoFrameReceived;
                    reader.VideoSourceInfoDetected += OnVideoSourceInfoDetected;

                    if (_readers.TryAdd(room.Id, reader))
                    {
                        reader.StartAsync(_cts.Token);
                        _logger.LogInformation("Started RTSP video reader for room {RoomId} ({Name})", room.Id, room.Name);
                    }
                    else
                    {
                        reader.VideoSourceInfoDetected -= OnVideoSourceInfoDetected;
                        reader.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting video reader for room {RoomId}", room.Id);
            }
        }

        private void StopReader(int roomId)
        {
            if (_readers.TryRemove(roomId, out var reader))
            {
                if (reader is RtspVideoReader rtspReader)
                {
                    rtspReader.VideoFrameReceived -= OnVideoFrameReceived;
                    rtspReader.VideoSourceInfoDetected -= OnVideoSourceInfoDetected;
                    rtspReader.Dispose();
                }
                else if (reader is NestStreamReader nestReader)
                {
                    nestReader.VideoFrameReceived -= OnVideoFrameReceived;
                    _nestReaderManager.ReleaseReader(roomId);
                }
                _logger.LogInformation("Stopped video reader for room {RoomId}", roomId);
            }
        }

        public bool IsNestRoom(int roomId) =>
            _roomCache.TryGetValue(roomId, out var room) && room.StreamSourceType == "google_nest";

        private void OnVideoSourceInfoDetected(object? sender, VideoSourceInfoEventArgs e)
        {
            var sourceInfo = new RoomVideoSourceInfo
            {
                RoomId = e.RoomId,
                SourceCodecName = e.SourceCodecName,
                PassthroughCodec = e.PassthroughCodec,
                FailureReason = e.FailureReason
            };

            PublishSourceInfo(sourceInfo);
            _ = PersistSourceInfoAsync(sourceInfo);

            if (!sourceInfo.IsSupported)
            {
                _logger.LogWarning(
                    "Unsupported RTSP video codec for room {RoomId}: {Codec}. Reason: {Reason}",
                    sourceInfo.RoomId,
                    sourceInfo.SourceCodecName,
                    sourceInfo.FailureReason ?? "unknown");
            }
            else
            {
                _logger.LogInformation(
                    "Detected RTSP video codec for room {RoomId}: source={SourceCodec}, passthrough={PassthroughCodec}",
                    sourceInfo.RoomId,
                    sourceInfo.SourceCodecName,
                    sourceInfo.PassthroughCodec);
            }
        }

        private static TaskCompletionSource<RoomVideoSourceInfo> CreateSourceInfoWaiter() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private RoomVideoSourceInfo CreateNestSourceInfo(int roomId) =>
            new()
            {
                RoomId = roomId,
                SourceCodecName = "h264",
                PassthroughCodec = VideoPassthroughCodec.H264
            };

        private void CachePersistedSourceInfo(Room room)
        {
            if (!TryGetPersistedSourceInfo(room, out var sourceInfo))
            {
                return;
            }

            PublishSourceInfo(sourceInfo);
        }

        private void PublishSourceInfo(RoomVideoSourceInfo sourceInfo)
        {
            _sourceInfos[sourceInfo.RoomId] = sourceInfo;
            var waiter = _sourceInfoWaiters.GetOrAdd(sourceInfo.RoomId, _ => CreateSourceInfoWaiter());
            waiter.TrySetResult(sourceInfo);
        }

        private static bool TryGetPersistedSourceInfo(Room room, out RoomVideoSourceInfo sourceInfo)
        {
            sourceInfo = new RoomVideoSourceInfo();

            if (string.Equals(room.StreamSourceType, "google_nest", StringComparison.OrdinalIgnoreCase))
            {
                sourceInfo = new RoomVideoSourceInfo
                {
                    RoomId = room.Id,
                    SourceCodecName = "h264",
                    PassthroughCodec = VideoPassthroughCodec.H264
                };
                return true;
            }

            if (!room.VideoCodecCheckedAtUtc.HasValue)
            {
                return false;
            }

            string sourceCodecName = (room.VideoSourceCodecName ?? string.Empty).Trim();
            string? failureReason = string.IsNullOrWhiteSpace(room.VideoCodecFailureReason)
                ? null
                : room.VideoCodecFailureReason.Trim();

            if (string.IsNullOrWhiteSpace(sourceCodecName) && string.IsNullOrWhiteSpace(failureReason))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceCodecName))
            {
                sourceCodecName = "unknown";
            }

            VideoPassthroughCodec? passthroughCodec = null;
            if (!string.IsNullOrWhiteSpace(room.VideoPassthroughCodec) &&
                Enum.TryParse(room.VideoPassthroughCodec, true, out VideoPassthroughCodec parsedCodec))
            {
                passthroughCodec = parsedCodec;
            }

            sourceInfo = new RoomVideoSourceInfo
            {
                RoomId = room.Id,
                SourceCodecName = sourceCodecName,
                PassthroughCodec = passthroughCodec,
                FailureReason = failureReason
            };

            return true;
        }

        private void ResetSourceInfoState(int roomId)
        {
            _sourceInfos.TryRemove(roomId, out _);
            var waiter = CreateSourceInfoWaiter();
            _sourceInfoWaiters.AddOrUpdate(
                roomId,
                _ => waiter,
                (_, existing) =>
                {
                    existing.TrySetCanceled();
                    return waiter;
                });
        }

        private async Task PersistSourceInfoAsync(RoomVideoSourceInfo sourceInfo)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                await roomService.UpdateRoomVideoCodecMetadataAsync(
                    sourceInfo.RoomId,
                    sourceInfo.SourceCodecName,
                    sourceInfo.PassthroughCodec,
                    sourceInfo.FailureReason,
                    DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not persist detected video codec metadata for room {RoomId}",
                    sourceInfo.RoomId);
            }
        }

        private void OnVideoFrameReceived(object? sender, VideoFrameEventArgs e)
        {
            // Find the room ID for this reader
            foreach (var kvp in _readers)
            {
                if (ReferenceEquals(kvp.Value, sender))
                {
                    int roomId = kvp.Key;
                    if (_subscribers.TryGetValue(roomId, out var handlers))
                    {
                        foreach (var handler in handlers)
                        {
                            try
                            {
                                handler(e);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error in video frame handler for room {RoomId}", roomId);
                            }
                        }
                    }
                    break;
                }
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                StopAsync().Wait();
                _cts?.Dispose();
                _isDisposed = true;
            }
        }
    }
}
