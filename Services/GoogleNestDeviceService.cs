using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public class RateLimitException : Exception
{
    public int RetryAfterSeconds { get; }

    public RateLimitException(int retryAfterSeconds, string message)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}

public interface IGoogleNestDeviceService
{
    Task<List<NestDevice>> ListDevicesAsync();
    Task<NestDevice?> GetDeviceAsync(string deviceId);
    Task<NestStreamInfo> GenerateWebRtcStreamAsync(string deviceId, string sdpOffer);
    Task<NestStreamInfo> ExtendWebRtcStreamAsync(string deviceId, string mediaSessionId);
    Task StopWebRtcStreamAsync(string deviceId, string mediaSessionId);
}

public class GoogleNestDeviceService : IGoogleNestDeviceService
{
    private readonly IGoogleNestAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleNestDeviceService> _logger;

    private const string SdmBaseUrl = "https://smartdevicemanagement.googleapis.com/v1";

    public GoogleNestDeviceService(
        IGoogleNestAuthService authService,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleNestDeviceService> logger)
    {
        _authService = authService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task<HttpClient> GetAuthenticatedClient()
    {
        var token = await _authService.GetValidAccessToken();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Google Nest account is not linked or token is invalid.");
        }

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> GetProjectId()
    {
        var settings = await _authService.GetSettings();
        if (string.IsNullOrEmpty(settings.ProjectId))
        {
            throw new InvalidOperationException("Google Nest Project ID is not configured.");
        }
        return settings.ProjectId;
    }

    private void ThrowIfRateLimited(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            int retryAfterSeconds = 60; // Default backoff
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            {
                retryAfterSeconds = Math.Max((int)delta.TotalSeconds, 30);
            }
            else if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            {
                retryAfterSeconds = Math.Max((int)(date - DateTimeOffset.UtcNow).TotalSeconds, 30);
            }

            _logger.LogWarning("Rate limited by Nest SDM API during {Operation}, retry after {Seconds}s",
                operation, retryAfterSeconds);
            throw new RateLimitException(retryAfterSeconds,
                $"Rate limited during {operation}. Retry after {retryAfterSeconds}s.");
        }
    }

    public async Task<List<NestDevice>> ListDevicesAsync()
    {
        var client = await GetAuthenticatedClient();
        var projectId = await GetProjectId();

        var response = await client.GetAsync($"{SdmBaseUrl}/enterprises/{projectId}/devices");
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to list Nest devices: {StatusCode} {Response}", response.StatusCode, content);
            throw new Exception($"Failed to list devices: {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(content);
        var devices = new List<NestDevice>();

        if (json.TryGetProperty("devices", out var devicesArray))
        {
            foreach (var device in devicesArray.EnumerateArray())
            {
                var type = device.GetProperty("type").GetString() ?? "";

                // Only include camera devices
                if (!type.Contains("CAMERA", StringComparison.OrdinalIgnoreCase) &&
                    !type.Contains("DOORBELL", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = device.GetProperty("name").GetString() ?? "";
                var displayName = "";
                var roomName = "";

                if (device.TryGetProperty("traits", out var traits))
                {
                    if (traits.TryGetProperty("sdm.devices.traits.Info", out var info) &&
                        info.TryGetProperty("customName", out var customName))
                    {
                        displayName = customName.GetString() ?? "";
                    }
                }

                if (device.TryGetProperty("parentRelations", out var parentRelations))
                {
                    foreach (var relation in parentRelations.EnumerateArray())
                    {
                        if (relation.TryGetProperty("displayName", out var rdn))
                        {
                            roomName = rdn.GetString() ?? "";
                        }
                    }
                }

                // Device ID is the full resource name (enterprises/xxx/devices/yyy)
                devices.Add(new NestDevice
                {
                    DeviceId = name,
                    DisplayName = string.IsNullOrEmpty(displayName) ? roomName : displayName,
                    Type = type,
                    RoomName = roomName
                });
            }
        }

        _logger.LogInformation("Found {Count} Nest camera devices", devices.Count);
        return devices;
    }

    public async Task<NestDevice?> GetDeviceAsync(string deviceId)
    {
        var client = await GetAuthenticatedClient();

        var response = await client.GetAsync($"{SdmBaseUrl}/{deviceId}");
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to get Nest device {DeviceId}: {StatusCode}", deviceId, response.StatusCode);
            return null;
        }

        var device = JsonSerializer.Deserialize<JsonElement>(content);
        var type = device.GetProperty("type").GetString() ?? "";
        var displayName = "";
        var roomName = "";

        if (device.TryGetProperty("traits", out var traits) &&
            traits.TryGetProperty("sdm.devices.traits.Info", out var info) &&
            info.TryGetProperty("customName", out var customName))
        {
            displayName = customName.GetString() ?? "";
        }

        if (device.TryGetProperty("parentRelations", out var parentRelations))
        {
            foreach (var relation in parentRelations.EnumerateArray())
            {
                if (relation.TryGetProperty("displayName", out var rdn))
                {
                    roomName = rdn.GetString() ?? "";
                }
            }
        }

        return new NestDevice
        {
            DeviceId = deviceId,
            DisplayName = string.IsNullOrEmpty(displayName) ? roomName : displayName,
            Type = type,
            RoomName = roomName
        };
    }

    public async Task<NestStreamInfo> GenerateWebRtcStreamAsync(string deviceId, string sdpOffer)
    {
        var client = await GetAuthenticatedClient();

        var requestBody = new
        {
            command = "sdm.devices.commands.CameraLiveStream.GenerateWebRtcStream",
            @params = new { offerSdp = sdpOffer }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync($"{SdmBaseUrl}/{deviceId}:executeCommand", jsonContent);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfRateLimited(response, "GenerateWebRtcStream");
            _logger.LogError("Failed to generate WebRTC stream for {DeviceId}: {StatusCode} {Response}",
                deviceId, response.StatusCode, content);
            throw new Exception($"Failed to generate WebRTC stream: {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(content);
        var results = json.GetProperty("results");

        return new NestStreamInfo
        {
            SdpAnswer = results.GetProperty("answerSdp").GetString() ?? "",
            MediaSessionId = results.GetProperty("mediaSessionId").GetString() ?? "",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
    }

    public async Task<NestStreamInfo> ExtendWebRtcStreamAsync(string deviceId, string mediaSessionId)
    {
        var client = await GetAuthenticatedClient();

        var requestBody = new
        {
            command = "sdm.devices.commands.CameraLiveStream.ExtendWebRtcStream",
            @params = new { mediaSessionId }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync($"{SdmBaseUrl}/{deviceId}:executeCommand", jsonContent);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfRateLimited(response, "ExtendWebRtcStream");
            _logger.LogError("Failed to extend WebRTC stream for {DeviceId}: {StatusCode} {Response}",
                deviceId, response.StatusCode, content);
            throw new Exception($"Failed to extend WebRTC stream: {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(content);
        var results = json.GetProperty("results");

        return new NestStreamInfo
        {
            SdpAnswer = "",
            MediaSessionId = results.GetProperty("mediaSessionId").GetString() ?? mediaSessionId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
    }

    public async Task StopWebRtcStreamAsync(string deviceId, string mediaSessionId)
    {
        try
        {
            var client = await GetAuthenticatedClient();

            var requestBody = new
            {
                command = "sdm.devices.commands.CameraLiveStream.StopWebRtcStream",
                @params = new { mediaSessionId }
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync($"{SdmBaseUrl}/{deviceId}:executeCommand", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Rate limited stopping WebRTC stream for {DeviceId}, stream will expire naturally", deviceId);
                    return; // Stream will expire on its own
                }

                var content = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to stop WebRTC stream for {DeviceId}: {StatusCode} {Response}",
                    deviceId, response.StatusCode, content);
            }
            else
            {
                _logger.LogInformation("Stopped WebRTC stream for device {DeviceId}", deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping WebRTC stream for device {DeviceId}", deviceId);
        }
    }
}
