using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services
{
    public class AudioFrameEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public double AudioLevel { get; set; }
        public int SampleRate { get; set; }
        public int RoomId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public interface IAudioStreamingService
    {
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken cancellationToken = default);
        void SubscribeToRoom(int roomId, Action<AudioFrameEventArgs> handler);
        void UnsubscribeFromRoom(int roomId, Action<AudioFrameEventArgs> handler);
        void RefreshRooms();
        event EventHandler<SoundThresholdEventArgs> SoundThresholdExceeded;
    }

    public class AudioStreamingService : IAudioStreamingService, IHostedService, IDisposable
    {
        private readonly ILogger<AudioStreamingService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly NestStreamReaderManager _nestReaderManager;
        private readonly ConcurrentDictionary<int, IDisposable> _readers = new();
        private readonly ConcurrentDictionary<int, AudioProcessingService> _processors = new();
        private readonly ConcurrentDictionary<int, ConcurrentBag<Action<AudioFrameEventArgs>>> _subscribers = new();
        private readonly ConcurrentDictionary<int, Room> _roomCache = new();
        private CancellationTokenSource? _cts;
        private bool _isDisposed;

        public event EventHandler<SoundThresholdEventArgs>? SoundThresholdExceeded;

        public AudioStreamingService(
            ILogger<AudioStreamingService> logger,
            IServiceScopeFactory scopeFactory,
            NestStreamReaderManager nestReaderManager)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _nestReaderManager = nestReaderManager;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _logger.LogInformation("AudioStreamingService starting...");
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await LoadAndRegisterRooms();

            _logger.LogInformation("AudioStreamingService started");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("AudioStreamingService stopping...");
            _cts?.Cancel();

            foreach (var kvp in _readers)
            {
                StopReader(kvp.Key);
            }

            foreach (var kvp in _processors)
            {
                kvp.Value.Dispose();
            }

            _readers.Clear();
            _processors.Clear();
            _roomCache.Clear();
            _logger.LogInformation("AudioStreamingService stopped");
            return Task.CompletedTask;
        }

        public void SubscribeToRoom(int roomId, Action<AudioFrameEventArgs> handler)
        {
            var bag = _subscribers.GetOrAdd(roomId, _ => new ConcurrentBag<Action<AudioFrameEventArgs>>());
            bag.Add(handler);

            _logger.LogInformation("Client subscribed to audio for room {RoomId}. Total subscribers: {Count}", roomId, bag.Count);

            EnsureReaderStarted(roomId);
        }

        public void UnsubscribeFromRoom(int roomId, Action<AudioFrameEventArgs> handler)
        {
            if (_subscribers.TryGetValue(roomId, out var bag))
            {
                var remaining = new ConcurrentBag<Action<AudioFrameEventArgs>>(
                    bag.Where(h => h != handler));
                _subscribers[roomId] = remaining;

                _logger.LogInformation("Client unsubscribed from audio for room {RoomId}. Remaining subscribers: {Count}", roomId, remaining.Count);

                if (remaining.IsEmpty)
                {
                    _logger.LogInformation("No subscribers left for room {RoomId}, stopping audio reader", roomId);
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
                    _logger.LogError(ex, "Error refreshing audio rooms");
                }
            });
        }

        private async Task RefreshRoomsAsync()
        {
            List<Room> rooms;
            AudioSettings globalSettings;
            using (var scope = _scopeFactory.CreateScope())
            {
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                rooms = await roomService.GetAllRoomsAsync();
                globalSettings = await roomService.GetComposedAudioSettingsAsync();
            }

            var audioRooms = rooms
                .Where(r => r.EnableAudioStream &&
                    (r.StreamSourceType == "google_nest" ? !string.IsNullOrEmpty(r.NestDeviceId) : !string.IsNullOrEmpty(r.CameraStreamUrl)))
                .ToDictionary(r => r.Id);

            // Stop readers for rooms that no longer need audio
            foreach (var roomId in _readers.Keys.ToList())
            {
                if (!audioRooms.ContainsKey(roomId))
                {
                    _logger.LogInformation("Room {RoomId} no longer needs audio, stopping reader", roomId);
                    StopReader(roomId);
                    _roomCache.TryRemove(roomId, out _);
                    if (_processors.TryRemove(roomId, out var processor))
                    {
                        processor.Dispose();
                    }
                }
            }

            // Add/update readers for rooms that need audio
            foreach (var room in audioRooms.Values)
            {
                var oldRoom = _roomCache.GetValueOrDefault(room.Id);
                _roomCache[room.Id] = room;

                // Update processor settings if it exists
                if (_processors.TryGetValue(room.Id, out var existingProcessor))
                {
                    var roomSettings = CreateAudioSettingsForRoom(room, globalSettings);
                    existingProcessor.UpdateSettings(roomSettings);
                }

                if (!_readers.ContainsKey(room.Id))
                {
                    // New room - only start if there are subscribers (lazy start)
                    if (_subscribers.TryGetValue(room.Id, out var subs) && !subs.IsEmpty)
                    {
                        StartReader(room, globalSettings);
                    }
                }
                else if (oldRoom != null && oldRoom.CameraStreamUrl != room.CameraStreamUrl)
                {
                    // URL changed - restart the reader
                    _logger.LogInformation("Camera URL changed for room {RoomId}, restarting audio reader", room.Id);
                    StopReader(room.Id);
                    _readers.TryRemove(room.Id, out _);
                    if (_processors.TryRemove(room.Id, out var proc))
                    {
                        proc.Dispose();
                    }

                    if (_subscribers.TryGetValue(room.Id, out var subs) && !subs.IsEmpty)
                    {
                        StartReader(room, globalSettings);
                    }
                }
            }
        }

        private async Task LoadAndRegisterRooms()
        {
            List<Room> rooms;
            using (var scope = _scopeFactory.CreateScope())
            {
                var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                rooms = await roomService.GetAllRoomsAsync();
            }

            foreach (var room in rooms.Where(r => r.EnableAudioStream &&
                (r.StreamSourceType == "google_nest" ? !string.IsNullOrEmpty(r.NestDeviceId) : !string.IsNullOrEmpty(r.CameraStreamUrl))))
            {
                _roomCache[room.Id] = room;
                _logger.LogInformation("Audio-enabled room {RoomId} ({Name}) [{SourceType}] registered, will start on first subscriber",
                    room.Id, room.Name, room.StreamSourceType);
            }
        }

        private void EnsureReaderStarted(int roomId)
        {
            if (_readers.ContainsKey(roomId)) return;

            if (_roomCache.TryGetValue(roomId, out var room))
            {
                // Get global settings for creating the processor
                AudioSettings globalSettings;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
                    globalSettings = roomService.GetComposedAudioSettingsAsync().GetAwaiter().GetResult();
                }

                StartReader(room, globalSettings);
            }
        }

        private void StartReader(Room room, AudioSettings globalSettings)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;

            try
            {
                var roomSettings = CreateAudioSettingsForRoom(room, globalSettings);

                // Create per-room audio processor
                var processor = new AudioProcessingService(room.Id, roomSettings, _logger);
                processor.AudioSampleProcessed += OnAudioSampleProcessed;
                processor.SoundThresholdExceeded += OnSoundThresholdExceeded;

                if (room.StreamSourceType == "google_nest" && !string.IsNullOrEmpty(room.NestDeviceId))
                {
                    // Google Nest: use shared NestStreamReader
                    var nestReader = _nestReaderManager.GetOrCreateReader(room.Id, room.NestDeviceId, _cts.Token);
                    nestReader.AudioDataReceived += processor.OnAudioDataReceived;

                    if (_readers.TryAdd(room.Id, nestReader) && _processors.TryAdd(room.Id, processor))
                    {
                        _logger.LogInformation("Started Nest audio reader for room {RoomId} ({Name})", room.Id, room.Name);
                    }
                    else
                    {
                        nestReader.AudioDataReceived -= processor.OnAudioDataReceived;
                        processor.AudioSampleProcessed -= OnAudioSampleProcessed;
                        processor.SoundThresholdExceeded -= OnSoundThresholdExceeded;
                        _nestReaderManager.ReleaseReader(room.Id);
                        processor.Dispose();
                    }
                }
                else
                {
                    // RTSP: existing behavior
                    var reader = new RtspAudioReader(roomSettings, _logger);
                    reader.AudioDataReceived += processor.OnAudioDataReceived;

                    if (_readers.TryAdd(room.Id, reader) && _processors.TryAdd(room.Id, processor))
                    {
                        reader.StartAsync(_cts.Token);
                        _logger.LogInformation("Started RTSP audio reader for room {RoomId} ({Name})", room.Id, room.Name);
                    }
                    else
                    {
                        reader.AudioDataReceived -= processor.OnAudioDataReceived;
                        processor.AudioSampleProcessed -= OnAudioSampleProcessed;
                        processor.SoundThresholdExceeded -= OnSoundThresholdExceeded;
                        reader.Dispose();
                        processor.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting audio reader for room {RoomId}", room.Id);
            }
        }

        private void StopReader(int roomId)
        {
            if (_readers.TryRemove(roomId, out var reader))
            {
                if (_processors.TryGetValue(roomId, out var processor))
                {
                    if (reader is RtspAudioReader rtspReader)
                    {
                        rtspReader.AudioDataReceived -= processor.OnAudioDataReceived;
                    }
                    else if (reader is NestStreamReader nestReader)
                    {
                        nestReader.AudioDataReceived -= processor.OnAudioDataReceived;
                        _nestReaderManager.ReleaseReader(roomId);
                    }
                    processor.AudioSampleProcessed -= OnAudioSampleProcessed;
                    processor.SoundThresholdExceeded -= OnSoundThresholdExceeded;
                }

                // Only dispose RTSP readers directly; Nest readers are managed by NestStreamReaderManager
                if (reader is RtspAudioReader)
                {
                    reader.Dispose();
                }
                _logger.LogInformation("Stopped audio reader for room {RoomId}", roomId);
            }

            if (_processors.TryRemove(roomId, out var proc))
            {
                proc.Dispose();
            }
        }

        private void OnAudioSampleProcessed(object? sender, AudioSampleEventArgs e)
        {
            if (sender is not AudioProcessingService processor) return;

            int roomId = processor.RoomId;
            if (_subscribers.TryGetValue(roomId, out var handlers))
            {
                var frameArgs = new AudioFrameEventArgs
                {
                    AudioData = e.AudioData,
                    AudioLevel = e.AudioLevel,
                    SampleRate = e.SampleRate,
                    RoomId = roomId,
                    Timestamp = e.Timestamp
                };

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(frameArgs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in audio frame handler for room {RoomId}", roomId);
                    }
                }
            }
        }

        private void OnSoundThresholdExceeded(object? sender, SoundThresholdEventArgs e)
        {
            SoundThresholdExceeded?.Invoke(this, e);
        }

        private static AudioSettings CreateAudioSettingsForRoom(Room room, AudioSettings globalSettings)
        {
            return new AudioSettings
            {
                SoundThreshold = globalSettings.SoundThreshold,
                AverageSampleCount = globalSettings.AverageSampleCount,
                FilterEnabled = globalSettings.FilterEnabled,
                LowPassFrequency = globalSettings.LowPassFrequency,
                HighPassFrequency = globalSettings.HighPassFrequency,
                ThresholdPauseDuration = globalSettings.ThresholdPauseDuration,
                VolumeAdjustmentDb = globalSettings.VolumeAdjustmentDb,
                CameraStreamUrl = room.CameraStreamUrl,
                CameraUsername = room.CameraUsername,
                CameraPassword = room.CameraPassword
            };
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
