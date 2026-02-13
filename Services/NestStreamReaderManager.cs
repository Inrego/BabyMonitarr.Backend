using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BabyMonitarr.Backend.Services;

public class NestStreamReaderManager : IDisposable
{
    private readonly ConcurrentDictionary<int, NestStreamReaderEntry> _readers = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NestStreamReaderManager> _logger;
    private bool _isDisposed;

    private class NestStreamReaderEntry
    {
        public NestStreamReader Reader { get; set; } = null!;
        public int ReferenceCount;
    }

    public NestStreamReaderManager(
        IServiceScopeFactory scopeFactory,
        ILogger<NestStreamReaderManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public NestStreamReader GetOrCreateReader(int roomId, string nestDeviceId, CancellationToken ct)
    {
        var entry = _readers.GetOrAdd(roomId, _ =>
        {
            _logger.LogInformation("Creating NestStreamReader for room {RoomId}, device {DeviceId}", roomId, nestDeviceId);

            var reader = new NestStreamReader(roomId, nestDeviceId, _scopeFactory, _logger);
            reader.StartAsync(ct);

            return new NestStreamReaderEntry
            {
                Reader = reader,
                ReferenceCount = 0
            };
        });

        Interlocked.Increment(ref entry.ReferenceCount);
        return entry.Reader;
    }

    public void ReleaseReader(int roomId)
    {
        if (_readers.TryGetValue(roomId, out var entry))
        {
            var newCount = Interlocked.Decrement(ref entry.ReferenceCount);
            if (newCount <= 0)
            {
                if (_readers.TryRemove(roomId, out var removed))
                {
                    _logger.LogInformation("Disposing NestStreamReader for room {RoomId} (no more references)", roomId);
                    removed.Reader.Dispose();
                }
            }
        }
    }

    public void StopReader(int roomId)
    {
        if (_readers.TryRemove(roomId, out var entry))
        {
            _logger.LogInformation("Force-stopping NestStreamReader for room {RoomId}", roomId);
            entry.Reader.Dispose();
        }
    }

    public bool HasReader(int roomId) => _readers.ContainsKey(roomId);

    public NestStreamReader? GetReader(int roomId)
    {
        return _readers.TryGetValue(roomId, out var entry) ? entry.Reader : null;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            foreach (var kvp in _readers)
            {
                kvp.Value.Reader.Dispose();
            }
            _readers.Clear();
            _isDisposed = true;
        }
    }
}
