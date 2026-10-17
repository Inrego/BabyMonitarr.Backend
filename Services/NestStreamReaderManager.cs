using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BabyMonitarr.Backend.Services;

/// <summary>
/// Shares one <see cref="NestStreamReader"/> per room between the audio and video services.
/// Acquire and release run under one lock: a release that drops the count to zero must not
/// race an acquire that would pick the dying entry back up, and a holder may only release the
/// reader it actually holds (never a newer one registered for the same room).
/// </summary>
public class NestStreamReaderManager : IDisposable
{
    private readonly Dictionary<int, NestStreamReaderEntry> _readers = new();
    private readonly object _gate = new();
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
        lock (_gate)
        {
            if (!_readers.TryGetValue(roomId, out var entry))
            {
                _logger.LogInformation("Creating NestStreamReader for room {RoomId}, device {DeviceId}", roomId, nestDeviceId);

                var reader = new NestStreamReader(roomId, nestDeviceId, _scopeFactory, _logger);
                reader.StartAsync(ct);

                entry = new NestStreamReaderEntry { Reader = reader };
                _readers[roomId] = entry;
            }

            entry.ReferenceCount++;
            return entry.Reader;
        }
    }

    public void ReleaseReader(int roomId, NestStreamReader reader)
    {
        lock (_gate)
        {
            // A stale holder (its reader already replaced) has nothing left to release.
            if (!_readers.TryGetValue(roomId, out var entry) || !ReferenceEquals(entry.Reader, reader))
            {
                return;
            }

            if (--entry.ReferenceCount > 0)
            {
                return;
            }

            _readers.Remove(roomId);
        }

        _logger.LogInformation("Disposing NestStreamReader for room {RoomId} (no more references)", roomId);
        // Outside the lock: Dispose waits up to 10s for the connection loop to wind down.
        reader.Dispose();
    }

    public void StopReader(int roomId)
    {
        NestStreamReaderEntry? entry;
        lock (_gate)
        {
            if (!_readers.Remove(roomId, out entry))
            {
                return;
            }
        }

        _logger.LogInformation("Force-stopping NestStreamReader for room {RoomId}", roomId);
        entry.Reader.Dispose();
    }

    public bool HasReader(int roomId)
    {
        lock (_gate)
        {
            return _readers.ContainsKey(roomId);
        }
    }

    public NestStreamReader? GetReader(int roomId)
    {
        lock (_gate)
        {
            return _readers.TryGetValue(roomId, out var entry) ? entry.Reader : null;
        }
    }

    public void Dispose()
    {
        List<NestStreamReaderEntry> entries;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }
            entries = _readers.Values.ToList();
            _readers.Clear();
            _isDisposed = true;
        }

        foreach (var entry in entries)
        {
            entry.Reader.Dispose();
        }
    }
}
