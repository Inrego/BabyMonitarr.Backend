using System.Collections.Concurrent;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sharpcaster;
using Sharpcaster.Models;

namespace BabyMonitarr.Backend.Services;

public interface ICastDeviceService
{
    /// <summary>Known devices (discovered this run plus anything persisted), newest name wins.</summary>
    Task<IReadOnlyList<CastDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CastDeviceInfo>> RefreshAsync(CancellationToken cancellationToken = default);

    Task<CastDeviceInfo> AddManualAsync(
        string host,
        int port,
        string? name,
        bool isVideoCapable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts receivers seen by Home Assistant's Zeroconf browser. Identity is the TXT "id", the
    /// same value local discovery stores, so a device seen by both paths stays one row.
    /// </summary>
    Task<IReadOnlyList<CastDeviceInfo>> UpsertProxyDiscoveriesAsync(
        IReadOnlyCollection<CastProxyDiscovery> discoveries,
        CancellationToken cancellationToken = default);

    Task<bool> ForgetAsync(string deviceId, CancellationToken cancellationToken = default);

    Task<CastDevice?> FindAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Device ids pre-selected for a room.</summary>
    Task<IReadOnlyList<string>> GetRoomTargetsAsync(int roomId, CancellationToken cancellationToken = default);

    Task SetRoomTargetsAsync(int roomId, IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken = default);

    ChromecastReceiver ToReceiver(CastDevice device);
}

/// <summary>
/// Finds Cast receivers on the LAN over mDNS and remembers them in the database.
/// </summary>
/// <remarks>
/// mDNS is link-local: in Docker this only works with host networking (or an mDNS reflector).
/// Bridge-network installs use <see cref="AddManualAsync"/> to add a receiver by IP instead.
/// </remarks>
public sealed class CastDeviceService : ICastDeviceService, IHostedService, IDisposable
{
    private readonly ILogger<CastDeviceService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<CastOptions> _options;
    private readonly ConcurrentDictionary<string, DateTime> _seenThisRun = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private Timer? _timer;
    private bool _disposed;

    public CastDeviceService(
        ILogger<CastDeviceService> logger,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CastOptions> options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Google Cast support is disabled by configuration");
            return Task.CompletedTask;
        }

        int intervalSeconds = Math.Max(30, _options.CurrentValue.DiscoveryIntervalSeconds);
        _timer = new Timer(
            _ => _ = SafeRefreshAsync(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(intervalSeconds));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CastDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        var devices = await db.CastDevices
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return devices.Select(ToInfo).ToList();
    }

    public async Task<IReadOnlyList<CastDeviceInfo>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return Array.Empty<CastDeviceInfo>();
        }

        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(2, _options.CurrentValue.DiscoveryTimeoutSeconds));
            using var locator = new ChromecastLocator(_loggerFactory.CreateLogger<ChromecastLocator>());
            var receivers = await locator.FindReceiversAsync(timeout);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var now = DateTime.UtcNow;

            foreach (var receiver in receivers)
            {
                string deviceId = ResolveDeviceId(receiver);
                if (string.IsNullOrWhiteSpace(deviceId)) continue;

                int capabilities = ReadCapabilities(receiver);
                var existing = await db.CastDevices.FirstOrDefaultAsync(
                    d => d.DeviceId == deviceId, cancellationToken);

                if (existing == null)
                {
                    existing = new CastDevice { DeviceId = deviceId };
                    db.CastDevices.Add(existing);
                }

                existing.Name = ResolveName(receiver, existing.Name);
                existing.Model = Extra(receiver, "md") ?? receiver.Model ?? existing.Model;
                existing.Host = receiver.DeviceUri?.Host ?? existing.Host;
                existing.Port = receiver.Port > 0 ? receiver.Port : 8009;
                existing.Capabilities = capabilities;
                existing.IsVideoCapable = (capabilities & 0x01) != 0;
                existing.IsGroup = (capabilities & 0x20) != 0;
                existing.LastSeenUtc = now;
                if (!existing.ManuallyAdded) existing.Origin = CastDeviceOrigins.Discovered;

                _seenThisRun[deviceId] = now;
            }

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Cast discovery found {Count} receiver(s)", receivers.Count());
            return await GetDevicesAsync(cancellationToken);
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public async Task<CastDeviceInfo> AddManualAsync(
        string host,
        int port,
        string? name,
        bool isVideoCapable,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        host = host.Trim();
        port = port > 0 ? port : 8009;
        string deviceId = $"{host}:{port}";

        // Connect once so a typo surfaces immediately instead of at cast time.
        try
        {
            var client = new ChromecastClient(_loggerFactory.CreateLogger<ChromecastClient>());
            await client.ConnectChromecast(new ChromecastReceiver
            {
                DeviceUri = new Uri($"https://{host}"),
                Port = port,
                Name = name ?? host
            });
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not reach a Cast device at {host}:{port}.", ex);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        var device = await db.CastDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
        if (device == null)
        {
            device = new CastDevice { DeviceId = deviceId };
            db.CastDevices.Add(device);
        }

        device.Name = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : host;
        device.Host = host;
        device.Port = port;
        device.IsVideoCapable = isVideoCapable;
        device.ManuallyAdded = true;
        device.Origin = CastDeviceOrigins.Manual;
        device.LastSeenUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        _seenThisRun[deviceId] = DateTime.UtcNow;

        return ToInfo(device);
    }

    public async Task<IReadOnlyList<CastDeviceInfo>> UpsertProxyDiscoveriesAsync(
        IReadOnlyCollection<CastProxyDiscovery> discoveries,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        var now = DateTime.UtcNow;
        int seen = 0;

        foreach (var discovery in discoveries)
        {
            string deviceId = discovery.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(discovery.Host)) continue;

            var existing = await db.CastDevices.FirstOrDefaultAsync(
                d => d.DeviceId == deviceId, cancellationToken);

            if (existing == null)
            {
                existing = new CastDevice { DeviceId = deviceId };
                db.CastDevices.Add(existing);
            }

            // Newest name wins, same rule local discovery follows.
            if (!string.IsNullOrWhiteSpace(discovery.FriendlyName)) existing.Name = discovery.FriendlyName!.Trim();
            else if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = "Cast device";

            if (!string.IsNullOrWhiteSpace(discovery.Model)) existing.Model = discovery.Model!.Trim();

            // Chromecast addresses move on DHCP renewal: a re-push for a known id updates the
            // stored host in place rather than creating a second device.
            existing.Host = discovery.Host.Trim();
            existing.Port = discovery.Port > 0 ? discovery.Port : 8009;

            if (discovery.Capabilities is int capabilities)
            {
                existing.Capabilities = capabilities;
                existing.IsVideoCapable = (capabilities & 0x01) != 0;
                existing.IsGroup = (capabilities & 0x20) != 0;
            }

            existing.LastSeenUtc = now;
            if (!existing.ManuallyAdded) existing.Origin = CastDeviceOrigins.HaProxy;

            _seenThisRun[deviceId] = now;
            seen++;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (seen > 0)
        {
            _logger.LogInformation("Home Assistant proxied {Count} Cast receiver(s)", seen);
        }

        return await GetDevicesAsync(cancellationToken);
    }

    public async Task<bool> ForgetAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        var device = await db.CastDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
        if (device == null) return false;

        db.CastDevices.Remove(device);
        var targets = await db.RoomCastTargets
            .Where(t => t.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        db.RoomCastTargets.RemoveRange(targets);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CastDevice?> FindAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        return await db.CastDevices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRoomTargetsAsync(
        int roomId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        return await db.RoomCastTargets
            .AsNoTracking()
            .Where(t => t.RoomId == roomId)
            .Select(t => t.DeviceId)
            .ToListAsync(cancellationToken);
    }

    public async Task SetRoomTargetsAsync(
        int roomId,
        IReadOnlyCollection<string> deviceIds,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();

        var existing = await db.RoomCastTargets
            .Where(t => t.RoomId == roomId)
            .ToListAsync(cancellationToken);
        db.RoomCastTargets.RemoveRange(existing);

        foreach (string deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
        {
            db.RoomCastTargets.Add(new RoomCastTarget { RoomId = roomId, DeviceId = deviceId });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public ChromecastReceiver ToReceiver(CastDevice device) => new()
    {
        Name = device.Name,
        Model = device.Model,
        Port = device.Port > 0 ? device.Port : 8009,
        DeviceUri = new Uri($"https://{device.Host}")
    };

    private CastDeviceInfo ToInfo(CastDevice device) => new()
    {
        DeviceId = device.DeviceId,
        Name = device.Name,
        Model = device.Model,
        Host = device.Host,
        IsVideoCapable = device.IsVideoCapable,
        IsGroup = device.IsGroup,
        ManuallyAdded = device.ManuallyAdded,
        Origin = string.IsNullOrWhiteSpace(device.Origin) ? CastDeviceOrigins.Discovered : device.Origin,
        Port = device.Port > 0 ? device.Port : 8009,
        LastSeenUtc = device.LastSeenUtc,
        IsOnline = device.ManuallyAdded ||
                   (device.LastSeenUtc != null &&
                    DateTime.UtcNow - device.LastSeenUtc.Value <
                        TimeSpan.FromSeconds(Math.Max(120, _options.CurrentValue.DiscoveryIntervalSeconds * 2)))
    };

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cast discovery failed");
        }
    }

    private static string ResolveDeviceId(ChromecastReceiver receiver)
    {
        string? id = Extra(receiver, "id");
        if (!string.IsNullOrWhiteSpace(id)) return id!;
        string host = receiver.DeviceUri?.Host ?? string.Empty;
        return string.IsNullOrEmpty(host) ? string.Empty : $"{host}:{receiver.Port}";
    }

    private static string ResolveName(ChromecastReceiver receiver, string fallback)
    {
        string? friendly = Extra(receiver, "fn");
        if (!string.IsNullOrWhiteSpace(friendly)) return friendly!;
        if (!string.IsNullOrWhiteSpace(receiver.Name)) return receiver.Name;
        return string.IsNullOrWhiteSpace(fallback) ? "Cast device" : fallback;
    }

    private static int ReadCapabilities(ChromecastReceiver receiver)
    {
        string? ca = Extra(receiver, "ca");
        return int.TryParse(ca, out int value) ? value : 0;
    }

    private static string? Extra(ChromecastReceiver receiver, string key)
    {
        if (receiver.ExtraInformation == null) return null;
        return receiver.ExtraInformation.TryGetValue(key, out var value) ? value : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _discoveryGate.Dispose();
    }
}
