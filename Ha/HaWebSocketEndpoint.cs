using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Ha;

/// <summary>
/// The plain WebSocket endpoint the Home Assistant integration talks to. Deliberately not
/// SignalR: the HA side has no SignalR client. The wire contract lives in
/// docs/ha-websocket-protocol.md.
/// </summary>
public static class HaWebSocketEndpoint
{
    public const string Path = "/ha/ws";

    public static IEndpointConventionBuilder MapHaWebSocket(this IEndpointRouteBuilder endpoints)
    {
        // Anonymous at the routing layer because the API key is validated here, by hand, so that
        // a bad key produces a 401 on the handshake instead of the fallback policy's login redirect.
        return endpoints.MapGet(Path, HandleAsync).AllowAnonymous();
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(HaWebSocketEndpoint).FullName!);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket request");
            return;
        }

        string? key = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(key))
        {
            await RejectAsync(context, "Missing API key");
            return;
        }

        var apiKeyService = context.RequestServices.GetRequiredService<IApiKeyService>();
        var user = await apiKeyService.ValidateKeyAsync(key);
        if (user == null)
        {
            logger.LogWarning("Rejected HA WebSocket handshake from {Remote}: invalid API key",
                context.Connection.RemoteIpAddress);
            await RejectAsync(context, "Invalid API key");
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<HaOptions>>().Value;
        var monitoring = context.RequestServices.GetRequiredService<IHaMonitoringService>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new HaConnection(
            Guid.NewGuid().ToString("N"),
            user.DisplayName ?? user.Username,
            socket,
            Math.Max(16, options.SendQueueCapacity),
            logger);

        await monitoring.RegisterAsync(connection, context.RequestAborted);
        try
        {
            await connection.RunAsync(monitoring.HandleMessageAsync, context.RequestAborted);
        }
        finally
        {
            monitoring.Unregister(connection);
        }
    }

    /// <summary>
    /// Header first. The query parameter exists because HA's aiohttp client cannot always set
    /// headers on a WebSocket handshake; <c>access_token</c> matches the existing SignalR convention.
    /// </summary>
    private static string? ExtractApiKey(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(token)) return token;
        }

        var queryToken = request.Query["access_token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryToken)) return queryToken.Trim();

        queryToken = request.Query["api_key"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(queryToken) ? null : queryToken.Trim();
    }

    private static async Task RejectAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync(reason);
    }
}
