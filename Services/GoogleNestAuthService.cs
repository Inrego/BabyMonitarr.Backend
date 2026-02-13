using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public interface IGoogleNestAuthService
{
    Task<string> GetAuthorizationUrl(string redirectUri);
    Task<bool> ExchangeCodeForTokens(string code, string redirectUri);
    Task<string?> GetValidAccessToken();
    Task<bool> IsLinked();
    Task UnlinkAccount();
    Task<GoogleNestSettings> GetSettings();
    Task UpdateSettings(GoogleNestSettings settings);
}

public class GoogleNestAuthService : IGoogleNestAuthService
{
    private readonly BabyMonitarrDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleNestAuthService> _logger;

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string SdmScope = "https://www.googleapis.com/auth/sdm.service";

    public GoogleNestAuthService(
        BabyMonitarrDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleNestAuthService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GoogleNestSettings> GetSettings()
    {
        var settings = await _db.GoogleNestSettings.FindAsync(1);
        return settings ?? new GoogleNestSettings { Id = 1 };
    }

    public async Task UpdateSettings(GoogleNestSettings settings)
    {
        var existing = await _db.GoogleNestSettings.FindAsync(1);
        if (existing == null)
        {
            settings.Id = 1;
            _db.GoogleNestSettings.Add(settings);
        }
        else
        {
            existing.ClientId = settings.ClientId;
            existing.ClientSecret = settings.ClientSecret;
            existing.ProjectId = settings.ProjectId;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<string> GetAuthorizationUrl(string redirectUri)
    {
        var settings = await GetSettings();
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(settings.ProjectId))
        {
            throw new InvalidOperationException("Google Nest Client ID and Project ID must be configured before linking.");
        }

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = SdmScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };

        var queryString = string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"https://nestservices.google.com/partnerconnections/{settings.ProjectId}/auth?{queryString}";
    }

    public async Task<bool> ExchangeCodeForTokens(string code, string redirectUri)
    {
        var settings = await GetSettings();
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(settings.ClientSecret))
        {
            _logger.LogError("Cannot exchange code: Client ID or Client Secret not configured");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            });

            var response = await client.PostAsync(TokenEndpoint, requestBody);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token exchange failed: {StatusCode} {Response}", response.StatusCode, responseContent);
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            var existing = await _db.GoogleNestSettings.FindAsync(1);
            if (existing == null)
            {
                _logger.LogError("GoogleNestSettings row not found");
                return false;
            }

            existing.AccessToken = tokenResponse.GetProperty("access_token").GetString();
            existing.RefreshToken = tokenResponse.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : existing.RefreshToken;
            existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.GetProperty("expires_in").GetInt32());
            existing.IsLinked = true;

            await _db.SaveChangesAsync();

            _logger.LogInformation("Google Nest account linked successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging authorization code for tokens");
            return false;
        }
    }

    public async Task<string?> GetValidAccessToken()
    {
        var settings = await GetSettings();
        if (!settings.IsLinked || string.IsNullOrEmpty(settings.AccessToken))
        {
            return null;
        }

        // Refresh if token expires within 5 minutes
        if (settings.TokenExpiresAt.HasValue && settings.TokenExpiresAt.Value < DateTime.UtcNow.AddMinutes(5))
        {
            var refreshed = await RefreshAccessToken(settings);
            if (!refreshed)
            {
                return null;
            }

            // Re-read after refresh
            settings = await GetSettings();
        }

        return settings.AccessToken;
    }

    private async Task<bool> RefreshAccessToken(GoogleNestSettings settings)
    {
        if (string.IsNullOrEmpty(settings.RefreshToken) ||
            string.IsNullOrEmpty(settings.ClientId) ||
            string.IsNullOrEmpty(settings.ClientSecret))
        {
            _logger.LogError("Cannot refresh token: missing refresh token or client credentials");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["refresh_token"] = settings.RefreshToken,
                ["grant_type"] = "refresh_token"
            });

            var response = await client.PostAsync(TokenEndpoint, requestBody);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh failed: {StatusCode} {Response}", response.StatusCode, responseContent);
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            var existing = await _db.GoogleNestSettings.FindAsync(1);
            if (existing == null) return false;

            existing.AccessToken = tokenResponse.GetProperty("access_token").GetString();
            existing.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.GetProperty("expires_in").GetInt32());

            if (tokenResponse.TryGetProperty("refresh_token", out var newRefreshToken))
            {
                existing.RefreshToken = newRefreshToken.GetString();
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Google Nest access token refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing access token");
            return false;
        }
    }

    public async Task<bool> IsLinked()
    {
        var settings = await GetSettings();
        return settings.IsLinked && !string.IsNullOrEmpty(settings.RefreshToken);
    }

    public async Task UnlinkAccount()
    {
        var existing = await _db.GoogleNestSettings.FindAsync(1);
        if (existing != null)
        {
            existing.AccessToken = null;
            existing.RefreshToken = null;
            existing.TokenExpiresAt = null;
            existing.IsLinked = false;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Google Nest account unlinked");
    }
}
