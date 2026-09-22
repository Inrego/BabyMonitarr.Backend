using System.Collections.Concurrent;
using BabyMonitarr.Backend.Hubs;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using BabyMonitarr.Backend.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Sharpcaster;
using Sharpcaster.Models.Media;

namespace BabyMonitarr.Backend.Services;

public interface ICastSessionService
{
    /// <summary>Starts casting a room to every given device. Failures are reported per device.</summary>
    Task<CastStartResult> StartAsync(
        int roomId,
        IReadOnlyCollection<string> deviceIds,
        string? requestBaseUrl,
        CancellationToken cancellationToken = default);

    Task<bool> StopDeviceAsync(string deviceId);

    Task<int> StopRoomAsync(int roomId);

    IReadOnlyList<CastSessionInfo> GetSessions();

    /// <summary>Room currently cast to the device, or null when it is idle.</summary>
    int? RoomForDevice(string deviceId);

    string? LastErrorForDevice(string deviceId);
}

public sealed class CastSessionService : ICastSessionService, IHostedService
{
    /// <summary>Google's Default Media Receiver — no registered app id needed for plain media.</summary>
    private const string DefaultMediaReceiverAppId = "CC1AD845";

    private sealed class CastSession
    {
        public required string DeviceId { get; init; }
        public required int RoomId { get; init; }
        public required bool Video { get; init; }
        public required ChromecastClient Client { get; init; }
        public required CastHlsStream Stream { get; init; }
        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
        public volatile bool Stopping;
    }

    private readonly ILogger<CastSessionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICastDeviceService _devices;
    private readonly ICastHlsStreamService _streams;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<AudioStreamHub> _hub;
    private readonly IOptionsMonitor<CastOptions> _castOptions;
    private readonly IOptionsMonitor<WebRtcOptions> _webRtcOptions;
    private readonly IServer? _server;

    private readonly ConcurrentDictionary<string, CastSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _lastErrors = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CastSessionService(
        ILogger<CastSessionService> logger,
        ILoggerFactory loggerFactory,
        ICastDeviceService devices,
        ICastHlsStreamService streams,
        IServiceScopeFactory scopeFactory,
        IHubContext<AudioStreamHub> hub,
        IOptionsMonitor<CastOptions> castOptions,
        IOptionsMonitor<WebRtcOptions> webRtcOptions,
        IServer? server = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _devices = devices;
        _streams = streams;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _castOptions = castOptions;
        _webRtcOptions = webRtcOptions;
        _server = server;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (string deviceId in _sessions.Keys.ToList())
        {
            await StopDeviceAsync(deviceId);
        }
    }

    public async Task<CastStartResult> StartAsync(
        int roomId,
        IReadOnlyCollection<string> deviceIds,
        string? requestBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var result = new CastStartResult();
        if (!_castOptions.CurrentValue.Enabled)
        {
            foreach (string deviceId in deviceIds)
            {
                result.Failed[deviceId] = "Google Cast is disabled on the server.";
            }
            return result;
        }

        Room? room;
        using (var scope = _scopeFactory.CreateScope())
        {
            var rooms = scope.ServiceProvider.GetRequiredService<IRoomService>();
            room = await rooms.GetRoomAsync(roomId);
        }

        if (room == null)
        {
            foreach (string deviceId in deviceIds)
            {
                result.Failed[deviceId] = $"Room {roomId} not found.";
            }
            return result;
        }

        // Nest rooms arrive as WebRTC inside the backend, so there is no URL for ffmpeg to pull.
        // Casting them needs the in-process H264/Opus frames forwarded to ffmpeg over RTP — that
        // is the upgrade path, not a tweak to this method.
        if (!string.Equals(room.StreamSourceType, "rtsp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(room.CameraStreamUrl))
        {
            foreach (string deviceId in deviceIds)
            {
                result.Failed[deviceId] = "Casting is only supported for RTSP rooms.";
            }
            return result;
        }

        string baseUrl = ResolveBaseUrl(requestBaseUrl);

        foreach (string deviceId in deviceIds.Distinct())
        {
            try
            {
                var session = await StartOneAsync(room, deviceId, baseUrl, cancellationToken);
                result.Started.Add(new CastSessionInfo
                {
                    DeviceId = session.DeviceId,
                    RoomId = session.RoomId,
                    Video = session.Video,
                    StartedAtUtc = session.StartedAtUtc
                });
                _lastErrors.TryRemove(deviceId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start cast of room {RoomId} on {DeviceId}", roomId, deviceId);
                result.Failed[deviceId] = ex.Message;
                _lastErrors[deviceId] = ex.Message;
            }
        }

        await BroadcastStateAsync();
        return result;
    }

    private async Task<CastSession> StartOneAsync(
        Room room,
        string deviceId,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var device = await _devices.FindAsync(deviceId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown cast device '{deviceId}'.");

        // One receiver plays one thing at a time; a new request replaces whatever it was showing.
        await StopDeviceAsync(deviceId);

        bool video = device.IsVideoCapable && room.EnableVideoStream;
        if (!video && !room.EnableAudioStream)
        {
            throw new InvalidOperationException($"Room '{room.Name}' has no stream enabled to cast.");
        }

        var stream = await _streams.AcquireAsync(room, video, cancellationToken);

        ChromecastClient? client = null;
        try
        {
            client = new ChromecastClient(_loggerFactory.CreateLogger<ChromecastClient>());
            await client.ConnectChromecast(_devices.ToReceiver(device));
            await client.LaunchApplicationAsync(DefaultMediaReceiverAppId, false);

            string mediaUrl = $"{baseUrl}{stream.PlaylistPath}";
            var media = new Media
            {
                ContentId = mediaUrl,
                ContentUrl = mediaUrl,
                ContentType = stream.ContentType,
                StreamType = StreamType.Live,
                HlsSegmentFormat = HlsSegmentFormat.TS_AAC,
                HlsVideoSegmentFormat = video ? HlsVideoSegmentFormat.MPEG2_TS : null,
                Metadata = new MediaMetadata
                {
                    MetadataType = MetadataType.Default,
                    Title = room.Name,
                    SubTitle = "BabyMonitarr"
                }
            };

            await client.GetChannel<Sharpcaster.Channels.MediaChannel>().LoadAsync(media);

            var session = new CastSession
            {
                DeviceId = deviceId,
                RoomId = room.Id,
                Video = video,
                Client = client,
                Stream = stream
            };

            client.Disconnected += (_, _) => OnClientDisconnected(session);
            _sessions[deviceId] = session;

            _logger.LogInformation(
                "Casting room {RoomId} to {DeviceName} ({Profile})",
                room.Id,
                device.Name,
                video ? "video" : "audio");

            return session;
        }
        catch
        {
            if (client != null)
            {
                try { await client.DisconnectAsync(); } catch { /* best effort */ }
            }
            _streams.Release(stream);
            throw;
        }
    }

    public async Task<bool> StopDeviceAsync(string deviceId)
    {
        await _gate.WaitAsync();
        CastSession? session;
        try
        {
            if (!_sessions.TryRemove(deviceId, out session) || session == null)
            {
                return false;
            }
            session.Stopping = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await session.Client.GetChannel<Sharpcaster.Channels.ReceiverChannel>().StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cast receiver {DeviceId} did not accept stop cleanly", deviceId);
        }

        try
        {
            await session.Client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disconnecting cast client {DeviceId}", deviceId);
        }

        _streams.Release(session.Stream);
        _logger.LogInformation("Stopped casting room {RoomId} to {DeviceId}", session.RoomId, deviceId);
        await BroadcastStateAsync();
        return true;
    }

    public async Task<int> StopRoomAsync(int roomId)
    {
        int stopped = 0;
        foreach (var session in _sessions.Values.Where(s => s.RoomId == roomId).ToList())
        {
            if (await StopDeviceAsync(session.DeviceId)) stopped++;
        }
        return stopped;
    }

    public IReadOnlyList<CastSessionInfo> GetSessions() => _sessions.Values
        .Select(s => new CastSessionInfo
        {
            DeviceId = s.DeviceId,
            RoomId = s.RoomId,
            Video = s.Video,
            StartedAtUtc = s.StartedAtUtc
        })
        .ToList();

    public int? RoomForDevice(string deviceId) =>
        _sessions.TryGetValue(deviceId, out var session) ? session.RoomId : null;

    public string? LastErrorForDevice(string deviceId) =>
        _lastErrors.TryGetValue(deviceId, out var error) ? error : null;

    /// <summary>
    /// A receiver dropping the connection (power off, someone cast something else, Wi-Fi blip)
    /// ends the session. We release the HLS stream rather than reconnect blindly: re-casting to a
    /// TV that a parent deliberately switched off would be worse than stopping.
    /// </summary>
    private void OnClientDisconnected(CastSession session)
    {
        if (session.Stopping) return;
        if (!_sessions.TryRemove(session.DeviceId, out _)) return;

        _streams.Release(session.Stream);
        _lastErrors[session.DeviceId] = "The cast device disconnected.";
        _logger.LogInformation(
            "Cast device {DeviceId} disconnected; room {RoomId} is no longer casting there",
            session.DeviceId,
            session.RoomId);

        _ = BroadcastStateAsync();
    }

    private async Task BroadcastStateAsync()
    {
        try
        {
            await _hub.Clients.All.SendAsync("CastStateChanged");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not broadcast cast state change");
        }
    }

    /// <summary>
    /// The URL a Cast receiver should fetch HLS from. Explicit config wins; otherwise the
    /// advertised WebRTC address (already required to be LAN-reachable) with the listening port;
    /// otherwise whatever host the calling client used.
    /// </summary>
    private string ResolveBaseUrl(string? requestBaseUrl)
    {
        string? configured = _castOptions.CurrentValue.BaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!.TrimEnd('/');
        }

        string? advertised = _webRtcOptions.CurrentValue.AdvertisedAddress;
        if (!string.IsNullOrWhiteSpace(advertised))
        {
            int? port = ResolveHttpPort();
            return port.HasValue
                ? $"http://{advertised}:{port}"
                : $"http://{advertised}";
        }

        if (!string.IsNullOrWhiteSpace(requestBaseUrl))
        {
            return requestBaseUrl!.TrimEnd('/');
        }

        throw new InvalidOperationException(
            "Cannot work out a URL for the cast device to load. Set Cast:BaseUrl or WebRtc:AdvertisedAddress.");
    }

    private int? ResolveHttpPort()
    {
        var addresses = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses == null) return null;

        foreach (string address in addresses)
        {
            if (Uri.TryCreate(address.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttp)
            {
                return uri.Port;
            }
        }

        return null;
    }
}
