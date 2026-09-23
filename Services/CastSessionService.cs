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

    /// <summary>The idle "Backdrop" app a receiver shows when nothing is cast.</summary>
    private const string BackdropAppId = "E8C28D3C";

    /// <summary>
    /// How long after a stream disruption a receiver quitting counts as a failure, not a person.
    /// A Nest Hub that loses the stream shows "something went wrong" and drops to Backdrop,
    /// which looks exactly like someone pressing stop. Ceiling: a real stop inside this window
    /// gets one reconnect; stopping again once the stream is healthy ends it.
    /// </summary>
    private static readonly TimeSpan DisruptionWindow = TimeSpan.FromMinutes(3);

    private sealed class CastSession
    {
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required int RoomId { get; init; }
        public required bool Video { get; init; }
        public required Media Media { get; init; }
        public required CastHlsStream Stream { get; init; }
        public ChromecastClient Client { get; set; } = null!;
        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
        public volatile bool Stopping;
        public int Recovering;
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

        // RTSP rooms are pulled by ffmpeg directly; Nest rooms are fed to it over the in-process
        // RTP relay (CastNestRtpRelay). Either way the room needs a configured source.
        bool isNest = string.Equals(room.StreamSourceType, "google_nest", StringComparison.OrdinalIgnoreCase);
        bool hasSource = isNest
            ? !string.IsNullOrWhiteSpace(room.NestDeviceId)
            : !string.IsNullOrWhiteSpace(room.CameraStreamUrl);
        if (!hasSource)
        {
            foreach (string deviceId in deviceIds)
            {
                result.Failed[deviceId] = $"Room '{room.Name}' has no camera source configured.";
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

        var stream = await _streams.AcquireAsync(room, video, MaxVideoHeightFor(device), cancellationToken);

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

        var session = new CastSession
        {
            DeviceId = deviceId,
            DeviceName = device.Name,
            RoomId = room.Id,
            Video = video,
            Media = media,
            Stream = stream
        };

        try
        {
            session.Client = await ConnectAndLoadAsync(session, device);
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
            _streams.Release(stream);
            throw;
        }
    }

    /// <summary>
    /// Nest Hub displays fail the load (LOAD_FAILED, then IdleReason ERROR) on 1080p H.264 of
    /// any profile but play 720p, so they get a scaled rendition. Everything else is passed the
    /// source untouched. Ceiling: matched on the mDNS model name; another receiver that turns
    /// out to be resolution-limited needs adding here.
    /// </summary>
    private static int? MaxVideoHeightFor(CastDevice device) =>
        device.Model.Contains("Nest Hub", StringComparison.OrdinalIgnoreCase) ? 720 : null;

    /// <summary>
    /// Connects to the receiver, launches the media app and loads the room. Used for the first
    /// start and for every recovery, so both paths behave the same.
    /// </summary>
    private async Task<ChromecastClient> ConnectAndLoadAsync(CastSession session, CastDevice device)
    {
        var client = new ChromecastClient(_loggerFactory.CreateLogger<ChromecastClient>());
        try
        {
            await client.ConnectChromecast(_devices.ToReceiver(device));
            await client.LaunchApplicationAsync(DefaultMediaReceiverAppId, false);
            await client.GetChannel<Sharpcaster.Channels.MediaChannel>().LoadAsync(session.Media);
        }
        catch
        {
            try { await client.DisconnectAsync(); } catch { /* best effort */ }
            throw;
        }

        // Subscribed only after a successful load so the transient states of connecting and
        // launching (Backdrop showing, player idle with no media) are not mistaken for trouble.
        client.Disconnected += (_, _) => OnClientDisconnected(session, client);
        client.GetChannel<Sharpcaster.Channels.MediaChannel>().StatusChanged +=
            (_, status) => OnMediaStatus(session, client, status);
        client.GetChannel<Sharpcaster.Channels.ReceiverChannel>().ReceiverStatusChanged +=
            (_, status) => OnReceiverStatus(session, client, status);
        return client;
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
    /// A cast session lives until someone deliberately ends it. Losing the receiver (Wi-Fi blip,
    /// reboot, power cycle) or seeing playback die on it triggers recovery, which retries until
    /// the receiver is back. Someone stopping playback on the device itself, or casting something
    /// else to it, is a deliberate choice and ends the session instead of fighting them for it.
    /// </summary>
    private void OnClientDisconnected(CastSession session, ChromecastClient client)
    {
        if (!IsCurrent(session, client)) return;
        BeginRecovery(session, "the cast device disconnected");
    }

    private void OnMediaStatus(CastSession session, ChromecastClient client, MediaStatus? status)
    {
        if (status == null || !IsCurrent(session, client)) return;
        if (status.PlayerState != PlayerStateType.Idle) return;

        switch (status.IdleReason?.ToUpperInvariant())
        {
            case "ERROR":
            case "FINISHED":
                BeginRecovery(session, $"playback stopped on the device ({status.IdleReason})");
                break;
            case "CANCELLED":
            case "INTERRUPTED":
                EndOrRecover(session, $"Playback was stopped on the cast device ({status.IdleReason}).");
                break;
            // No reason: the player is idle between load and play. Leave it alone.
        }
    }

    private void OnReceiverStatus(
        CastSession session,
        ChromecastClient client,
        Sharpcaster.Models.ChromecastStatus.ChromecastStatus? status)
    {
        if (status == null || !IsCurrent(session, client)) return;

        string? appId = status.Application?.AppId;
        if (appId == null || string.Equals(appId, DefaultMediaReceiverAppId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string what = string.Equals(appId, BackdropAppId, StringComparison.OrdinalIgnoreCase)
            ? "Casting was stopped on the device."
            : $"Another app ({status.Application?.DisplayName ?? appId}) took over the cast device.";
        EndOrRecover(session, what);
    }

    /// <summary>
    /// The receiver left the stream in a way a person could have caused. If the stream was
    /// disrupted around then, the receiver gave up on it and the session is recovered instead.
    /// </summary>
    private void EndOrRecover(CastSession session, string what)
    {
        if (session.Stream.DisruptedWithin(DisruptionWindow))
        {
            BeginRecovery(session, $"{what} right after the stream was disrupted");
            return;
        }

        _ = EndSessionAsync(session, what);
    }

    private bool IsCurrent(CastSession session, ChromecastClient client) =>
        !session.Stopping &&
        ReferenceEquals(session.Client, client) &&
        _sessions.TryGetValue(session.DeviceId, out var tracked) &&
        ReferenceEquals(tracked, session);

    private void BeginRecovery(CastSession session, string reason)
    {
        if (session.Stopping) return;
        if (Interlocked.Exchange(ref session.Recovering, 1) == 1) return;
        _ = RecoverAsync(session, reason);
    }

    /// <summary>
    /// Rebuilds the receiver connection with a capped backoff for as long as the session exists.
    /// There is no attempt limit on purpose: a monitor that silently gives up after a long outage
    /// is worse than one that keeps knocking once a minute.
    /// </summary>
    private async Task RecoverAsync(CastSession session, string reason)
    {
        _logger.LogWarning(
            "Cast to {DeviceName} for room {RoomId} needs recovery: {Reason}",
            session.DeviceName,
            session.RoomId,
            reason);
        _lastErrors[session.DeviceId] = $"Reconnecting: {reason}.";
        await BroadcastStateAsync();

        var previous = session.Client;
        try { await previous.DisconnectAsync(); } catch { /* it is probably already gone */ }

        int attempt = 0;
        try
        {
            while (!session.Stopping)
            {
                attempt++;
                int delaySeconds = (int)Math.Min(60, Math.Pow(2, attempt)); // 2, 4, 8, 16, 32, 60...
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (session.Stopping) return;

                var device = await _devices.FindAsync(session.DeviceId, CancellationToken.None);
                if (device == null)
                {
                    await EndSessionAsync(session, "The cast device was removed.");
                    return;
                }

                try
                {
                    var client = await ConnectAndLoadAsync(session, device);
                    if (session.Stopping)
                    {
                        // Stopped while we were reconnecting; do not leave the receiver playing.
                        try { await client.GetChannel<Sharpcaster.Channels.ReceiverChannel>().StopApplication(); } catch { }
                        try { await client.DisconnectAsync(); } catch { }
                        return;
                    }

                    session.Client = client;
                    _lastErrors.TryRemove(session.DeviceId, out _);
                    _logger.LogInformation(
                        "Cast to {DeviceName} for room {RoomId} recovered after {Attempts} attempt(s)",
                        session.DeviceName,
                        session.RoomId,
                        attempt);
                    await BroadcastStateAsync();
                    return;
                }
                catch (Exception ex)
                {
                    // First few attempts are worth a warning; after that once a minute at debug.
                    if (attempt <= 3)
                    {
                        _logger.LogWarning(ex,
                            "Cast recovery attempt {Attempt} for {DeviceName} failed; next in {Delay}s",
                            attempt, session.DeviceName, Math.Min(60, (int)Math.Pow(2, attempt + 1)));
                    }
                    else
                    {
                        _logger.LogDebug(ex,
                            "Cast recovery attempt {Attempt} for {DeviceName} failed",
                            attempt, session.DeviceName);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref session.Recovering, 0);
        }
    }

    private async Task EndSessionAsync(CastSession session, string message)
    {
        if (session.Stopping) return;
        if (!_sessions.TryRemove(session.DeviceId, out var tracked) || !ReferenceEquals(tracked, session)) return;
        session.Stopping = true;

        try { await session.Client.DisconnectAsync(); } catch { /* best effort */ }
        _streams.Release(session.Stream);
        _lastErrors[session.DeviceId] = message;
        _logger.LogWarning(
            "Cast of room {RoomId} to {DeviceName} ended: {Message}",
            session.RoomId,
            session.DeviceName,
            message);

        await BroadcastStateAsync();
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
