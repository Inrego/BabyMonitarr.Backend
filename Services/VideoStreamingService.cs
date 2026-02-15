using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services
{
    public interface IVideoStreamingService
    {
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken cancellationToken = default);
        void SubscribeToRoom(int roomId, Action<VideoFrameEventArgs> handler);
        void UnsubscribeFromRoom(int roomId, Action<VideoFrameEventArgs> handler);
        void RefreshRooms();
        bool IsNestRoom(int roomId);
    }

    public class VideoStreamingService : IVideoStreamingService, IHostedService, IDisposable
    {
        private readonly ILogger<VideoStreamingService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly NestStreamReaderManager _nestReaderManager;
        private readonly ConcurrentDictionary<int, IDisposable> _readers = new();
        private readonly ConcurrentDictionary<int, ConcurrentBag<Action<VideoFrameEventArgs>>> _subscribers = new();
        private readonly ConcurrentDictionary<int, Room> _roomCache = new();
        private CancellationTokenSource? _cts;
        private bool _isDisposed;

        public VideoStreamingService(
            ILogger<VideoStreamingService> logger,
            IServiceScopeFactory scopeFactory,
            NestStreamReaderManager nestReaderManager)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _nestReaderManager = nestReaderManager;
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

            _readers.Clear();
            _roomCache.Clear();
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
                }
            }

            // Add/update readers for rooms that need video
            foreach (var room in videoRooms.Values)
            {
                _roomCache[room.Id] = room;

                if (!_readers.ContainsKey(room.Id))
                {
                    // New room - only start if there are subscribers (lazy start)
                    if (_subscribers.TryGetValue(room.Id, out var subs) && !subs.IsEmpty)
                    {
                        StartReader(room);
                    }
                }
                else if (_roomCache.TryGetValue(room.Id, out var cached) &&
                         cached.CameraStreamUrl != room.CameraStreamUrl)
                {
                    // URL changed - restart the reader
                    _logger.LogInformation("Camera URL changed for room {RoomId}, restarting reader", room.Id);
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
                if (room.StreamSourceType == "google_nest" && !string.IsNullOrEmpty(room.NestDeviceId))
                {
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
                    var reader = new RtspVideoReader(room, _logger);
                    reader.VideoFrameReceived += OnVideoFrameReceived;

                    if (_readers.TryAdd(room.Id, reader))
                    {
                        reader.StartAsync(_cts.Token);
                        _logger.LogInformation("Started RTSP video reader for room {RoomId} ({Name})", room.Id, room.Name);
                    }
                    else
                    {
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
