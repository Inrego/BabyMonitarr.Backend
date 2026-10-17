using BabyMonitarr.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabyMonitarr.Backend.Tests;

public class NestStreamReaderManagerTests
{
    private const int RoomId = 1;
    private const string DeviceId = "device";

    // A pre-cancelled token keeps the reader from ever dialling Google.
    private static readonly CancellationToken NoConnect = new(canceled: true);

    private static NestStreamReaderManager CreateManager() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NestStreamReaderManager>.Instance);

    [Fact]
    public void Release_ByStaleHolder_DoesNotDisposeNewerReader()
    {
        using var manager = CreateManager();
        var old = manager.GetOrCreateReader(RoomId, DeviceId, NoConnect);
        manager.ReleaseReader(RoomId, old);
        Assert.True(old.IsDisposed);

        var current = manager.GetOrCreateReader(RoomId, DeviceId, NoConnect);
        manager.ReleaseReader(RoomId, old);

        Assert.False(current.IsDisposed);
        Assert.Same(current, manager.GetReader(RoomId));
    }

    [Fact]
    public void Reader_StaysAliveWhileAnyHolderRemains()
    {
        using var manager = CreateManager();
        var audio = manager.GetOrCreateReader(RoomId, DeviceId, NoConnect);
        var video = manager.GetOrCreateReader(RoomId, DeviceId, NoConnect);
        Assert.Same(audio, video);

        manager.ReleaseReader(RoomId, video);

        Assert.False(audio.IsDisposed);
        manager.ReleaseReader(RoomId, audio);
        Assert.True(audio.IsDisposed);
    }

    // Prod 2026-10-17: video released its last reference while audio was acquiring;
    // audio incremented the dying entry and kept a disposed reader forever.
    [Fact]
    public void ConcurrentAcquireAndRelease_NeverOrphansAHolder()
    {
        using var manager = CreateManager();
        var orphaned = 0;

        Parallel.For(0, 20000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            var reader = manager.GetOrCreateReader(RoomId, DeviceId, NoConnect);
            Thread.SpinWait(200);
            // While held, the reader must be live and still the one the manager shares out.
            if (reader.IsDisposed || !ReferenceEquals(manager.GetReader(RoomId), reader))
            {
                Interlocked.Increment(ref orphaned);
            }
            manager.ReleaseReader(RoomId, reader);
        });

        Assert.Equal(0, orphaned);
        Assert.Null(manager.GetReader(RoomId));
    }
}
