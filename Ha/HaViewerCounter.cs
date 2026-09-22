using Microsoft.AspNetCore.SignalR;

namespace BabyMonitarr.Backend.Ha;

public interface IHaViewerCounter
{
    /// <summary>Clients currently connected to the SignalR hub — the web dashboard and app clients.</summary>
    int Count { get; }
}

/// <summary>
/// Counts hub connections for <c>sensor.connected_viewers</c>. A filter is used rather than a
/// change to <see cref="Hubs.AudioStreamHub"/> so the hub itself stays untouched.
/// </summary>
public class HaViewerCountFilter : IHubFilter, IHaViewerCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        Interlocked.Increment(ref _count);
        await next(context);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        Interlocked.Decrement(ref _count);
        await next(context, exception);
    }
}
