using BabyMonitarr.Backend.Talkback.Foyer;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>A camera in the Google Home graph, as talkback addresses it.</summary>
public sealed record TalkbackCamera(string NestDeviceId, string GoogleUuid, string RoomName);

public interface ITalkbackCameraDirectory
{
    /// <summary>The account's cameras. Cached for a few minutes; <paramref name="refresh"/> bypasses the cache.</summary>
    Task<IReadOnlyList<TalkbackCamera>> GetCamerasAsync(bool refresh = false, CancellationToken ct = default);
}

public static class TalkbackCameraMatcher
{
    /// <summary>
    /// Cameras in a HomeGraph response: devices that carry a Nest <c>CAMERA:DEVICE_…</c> id.
    /// The room comes from the home's room list, which is the only place devices are named by room.
    /// </summary>
    public static List<TalkbackCamera> ParseCameras(GetHomeGraphResponse graph)
    {
        var cameras = new List<TalkbackCamera>();

        foreach (var home in graph.Homes)
        {
            var roomByDevice = home.Rooms
                .SelectMany(r => r.Devices.Select(d => (Uuid: d.Id?.GoogleUuid ?? string.Empty, Room: r.Name)))
                .Where(x => x.Uuid.Length > 0)
                .GroupBy(x => x.Uuid)
                .ToDictionary(g => g.Key, g => g.First().Room);

            foreach (var device in home.Devices)
            {
                string uuid = device.Id?.GoogleUuid ?? string.Empty;
                if (uuid.Length == 0) continue;

                var ids = device.OtherIds?.OtherThirdPartyId.ToList() ?? new List<Device.Types.ThirdPartyId>();
                if (device.Id?.ThirdPartyId is { } primary) ids.Insert(0, primary);

                var camera = ids.FirstOrDefault(i =>
                    i.IdType.Equals("CAMERA", StringComparison.OrdinalIgnoreCase) &&
                    i.Id.StartsWith("DEVICE_", StringComparison.Ordinal));
                if (camera == null || cameras.Any(c => c.GoogleUuid == uuid)) continue;

                cameras.Add(new TalkbackCamera(camera.Id, uuid, roomByDevice.GetValueOrDefault(uuid, string.Empty)));
            }
        }

        return cameras;
    }

    /// <summary>
    /// The camera for an SDM room: the only camera in the Google Home room of the same name, or
    /// else the only camera on the account. Null when that is ambiguous; the user then picks.
    /// </summary>
    public static TalkbackCamera? Match(string? sdmRoomName, IReadOnlyList<TalkbackCamera> cameras)
    {
        if (!string.IsNullOrWhiteSpace(sdmRoomName))
        {
            var sameRoom = cameras
                .Where(c => string.Equals(c.RoomName.Trim(), sdmRoomName.Trim(), StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            if (sameRoom.Count == 1) return sameRoom[0];
            if (sameRoom.Count > 1) return null;
        }

        return cameras.Count == 1 ? cameras[0] : null;
    }
}

public sealed class TalkbackCameraDirectory : ITalkbackCameraDirectory
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IGoogleHomeAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TalkbackCameraDirectory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TalkbackCamera>? _cameras;
    private DateTime _fetchedAtUtc;

    public TalkbackCameraDirectory(
        IGoogleHomeAuthService auth,
        IHttpClientFactory httpClientFactory,
        ILogger<TalkbackCameraDirectory> logger)
    {
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _auth.StatusChanged += () => _cameras = null;
    }

    public async Task<IReadOnlyList<TalkbackCamera>> GetCamerasAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _cameras != null && DateTime.UtcNow - _fetchedAtUtc < CacheLifetime)
            {
                return _cameras;
            }

            string token = await _auth.GetAccessTokenAsync(ct);
            var foyer = new FoyerClient(_httpClientFactory.CreateClient(GoogleHomeAuthService.HttpClientName), token);

            GetHomeGraphResponse graph;
            try
            {
                graph = await foyer.CallAsync("StructuresService", "GetHomeGraph",
                    new GetHomeGraphRequest { RequestId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") },
                    GetHomeGraphResponse.Parser, ct);
            }
            catch (FoyerException ex)
            {
                if (ex.IsUnauthenticated) _auth.InvalidateAccessToken();
                throw new TalkbackUnavailableException(TalkbackReasons.CameraError, $"Could not read the Google Home graph: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                throw new TalkbackUnavailableException(TalkbackReasons.CameraError, $"Could not reach Google Home: {ex.Message}");
            }

            _cameras = TalkbackCameraMatcher.ParseCameras(graph);
            _fetchedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Google Home graph lists {Count} talkback-capable cameras", _cameras.Count);
            return _cameras;
        }
        finally
        {
            _gate.Release();
        }
    }
}
