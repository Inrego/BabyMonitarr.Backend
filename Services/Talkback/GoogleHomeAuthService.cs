using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>What the admin UI and the app may know about the Google Home credential: never the credential.</summary>
public sealed record GoogleHomeStatus(
    bool Configured,
    bool Linked,
    string? Message,
    DateTime? LastSuccessAtUtc,
    DateTime? LastErrorAtUtc);

public interface IGoogleHomeAuthService
{
    Task<GoogleHomeStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Validates, stores and immediately tests a newly captured credential.</summary>
    Task<GoogleHomeStatus> SetCredentialsAsync(string issueTokenUrl, string cookie, CancellationToken ct = default);

    Task ClearCredentialsAsync(CancellationToken ct = default);

    /// <summary>A Foyer access token. Throws <see cref="TalkbackUnavailableException"/> when there is none to be had.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>Drops the cached token after Foyer refused it, so the next call exchanges a new one.</summary>
    void InvalidateAccessToken();

    event Action? StatusChanged;
}

public static class GoogleHomeCredentialInput
{
    private const string IssueTokenPrefix = "https://accounts.google.com/o/oauth2/iframerpc";

    /// <summary>Checks a pasted capture and trims it into the stored form.</summary>
    public static bool TryNormalize(
        string? issueTokenUrl, string? cookie,
        out string normalizedUrl, out string normalizedCookie, out string error)
    {
        normalizedUrl = (issueTokenUrl ?? string.Empty).Trim();
        normalizedCookie = (cookie ?? string.Empty).Trim();
        error = string.Empty;

        if (normalizedCookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            normalizedCookie = normalizedCookie["Cookie:".Length..].Trim();
        }

        if (!normalizedUrl.StartsWith(IssueTokenPrefix, StringComparison.Ordinal) ||
            !normalizedUrl.Contains("action=issueToken", StringComparison.Ordinal))
        {
            error = "The URL must be the full https://accounts.google.com/o/oauth2/iframerpc?action=issueToken… request URL.";
            return false;
        }

        bool hasSid = normalizedCookie
            .Split(';', StringSplitOptions.TrimEntries)
            .Any(pair => pair.StartsWith("SID=", StringComparison.Ordinal));
        if (!hasSid)
        {
            error = "The cookie has no SID= entry; copy the whole Cookie request header of that request.";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Turns the browser-captured issueToken URL + Google cookie into 1-hour Foyer access tokens,
/// the way homebridge-nest-accfactory does, and keeps the cookie alive by exchanging it
/// periodically and merging Google's rotated cookies back. Never logs the credential or a token.
/// </summary>
public sealed class GoogleHomeAuthService : BackgroundService, IGoogleHomeAuthService
{
    public const string HttpClientName = "GoogleHome";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    // Exchanging on this cadence keeps the rotated cookies fresh, like an open browser tab would.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
    // A rejected credential stays rejected; retrying it on every status poll only hammers Google.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleHomeAuthService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;

    public event Action? StatusChanged;

    public GoogleHomeAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleHomeAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GoogleHomeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
        var row = await db.GoogleHomeCredentials.AsNoTracking().FirstOrDefaultAsync(ct);
        return ToStatus(row);
    }

    public async Task<GoogleHomeStatus> SetCredentialsAsync(string issueTokenUrl, string cookie, CancellationToken ct = default)
    {
        if (!GoogleHomeCredentialInput.TryNormalize(issueTokenUrl, cookie, out var url, out var normalizedCookie, out var error))
        {
            throw new ArgumentException(error);
        }

        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var row = await db.GoogleHomeCredentials.FirstOrDefaultAsync(ct);
            if (row == null)
            {
                row = new GoogleHomeCredential();
                db.GoogleHomeCredentials.Add(row);
            }

            row.IssueTokenUrl = url;
            row.Cookie = normalizedCookie;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.LastSuccessAtUtc = null;
            row.LastError = null;
            row.LastErrorAtUtc = null;
            await db.SaveChangesAsync(ct);

            _accessToken = null;
            _logger.LogInformation("Google Home talkback credential saved; testing it");

            try
            {
                await ExchangeAsync(db, row, ct);
            }
            catch (TalkbackUnavailableException)
            {
                // Recorded on the row; the status below carries it.
            }

            return ToStatus(row);
        }
        finally
        {
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    public async Task ClearCredentialsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            db.GoogleHomeCredentials.RemoveRange(await db.GoogleHomeCredentials.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            _accessToken = null;
            _logger.LogInformation("Google Home talkback credential removed");
        }
        finally
        {
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is { } cached && DateTime.UtcNow < _accessTokenExpiresUtc - TokenRefreshMargin)
        {
            return cached;
        }

        return await RefreshAsync(keepAlive: false, ct);
    }

    public void InvalidateAccessToken() => _accessToken = null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(keepAlive: true, stoppingToken);
                }
                catch (TalkbackUnavailableException)
                {
                    // Not configured, or already logged and recorded by the exchange.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Google Home credential keep-alive failed");
                }

                await Task.Delay(KeepAliveInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <param name="keepAlive">
    /// The periodic exchange: always goes to Google, even with a valid token or a recent failure,
    /// so rotated cookies keep being merged and a recovered credential is noticed.
    /// </param>
    private async Task<string> RefreshAsync(bool keepAlive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        bool changed = false;
        try
        {
            if (!keepAlive && _accessToken is { } cached && DateTime.UtcNow < _accessTokenExpiresUtc - TokenRefreshMargin)
            {
                return cached;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
            var row = await db.GoogleHomeCredentials.FirstOrDefaultAsync(ct)
                ?? throw new TalkbackUnavailableException(TalkbackReasons.NotConfigured,
                    "Talkback needs a Google Home credential; add it on the backend's System page.");

            if (!keepAlive && row.LastError != null && row.LastErrorAtUtc is { } failedAt &&
                DateTime.UtcNow - failedAt < FailureBackoff)
            {
                throw new TalkbackUnavailableException(TalkbackReasons.CredentialFailing, row.LastError);
            }

            bool wasLinked = IsLinked(row);
            try
            {
                return await ExchangeAsync(db, row, ct);
            }
            finally
            {
                changed = wasLinked != IsLinked(row);
            }
        }
        finally
        {
            _gate.Release();
            if (changed) StatusChanged?.Invoke();
        }
    }

    /// <summary>One issueToken exchange. Caller holds the gate. Records the outcome on <paramref name="row"/>.</summary>
    private async Task<string> ExchangeAsync(BabyMonitarrDbContext db, GoogleHomeCredential row, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        string? lastError = null;

        // Sources disagree on which user-agent issueToken accepts, so try both.
        foreach (var (label, userAgent) in new[] { ("Nest iOS", FoyerClient.UserAgent), ("browser", BrowserUserAgent) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, row.IssueTokenUrl);
            request.Headers.TryAddWithoutValidation("Cookie", row.Cookie);
            request.Headers.TryAddWithoutValidation("Referer", "https://accounts.google.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XmlHttpRequest");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Could not reach Google ({ex.Message}).";
                continue;
            }

            using (response)
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    var rotated = setCookies.ToList();
                    row.Cookie = GoogleHomeCookies.Merge(row.Cookie, rotated);
                    _logger.LogDebug("Merged rotated Google cookies: {Names}", string.Join(", ", GoogleHomeCookies.Names(rotated)));
                }

                string body = await response.Content.ReadAsStringAsync(ct);
                JsonElement json;
                try
                {
                    json = JsonDocument.Parse(body).RootElement;
                }
                catch (JsonException)
                {
                    lastError = $"Google answered HTTP {(int)response.StatusCode} without a token.";
                    continue;
                }

                if (json.ValueKind == JsonValueKind.Object &&
                    json.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } accessToken)
                {
                    int expiresIn = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3599;
                    _accessToken = accessToken;
                    _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);

                    row.LastSuccessAtUtc = DateTime.UtcNow;
                    row.LastError = null;
                    row.LastErrorAtUtc = null;
                    await db.SaveChangesAsync(ct);

                    _logger.LogInformation("Google Home access token refreshed via {UserAgent} user-agent, valid for {Seconds}s",
                        label, expiresIn);
                    return accessToken;
                }

                string error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err)
                    ? err.ToString()
                    : "no access_token";
                lastError = $"Google rejected the credential ({error}). Capture a new one.";
            }
        }

        _accessToken = null;
        row.LastError = lastError ?? "The credential exchange failed.";
        row.LastErrorAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogWarning("Google Home credential exchange failed: {Error}", row.LastError);
        throw new TalkbackUnavailableException(TalkbackReasons.CredentialFailing, row.LastError);
    }

    private static bool IsLinked(GoogleHomeCredential row) => row.LastError == null && row.LastSuccessAtUtc != null;

    private static GoogleHomeStatus ToStatus(GoogleHomeCredential? row)
    {
        if (row == null)
        {
            return new GoogleHomeStatus(false, false, "Not configured", null, null);
        }

        bool linked = IsLinked(row);
        string? message = row.LastError ?? (linked ? null : "Not tested yet");
        return new GoogleHomeStatus(true, linked, message, row.LastSuccessAtUtc, row.LastErrorAtUtc);
    }
}
