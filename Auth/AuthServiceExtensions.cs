using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;
using System.Security.Claims;

namespace BabyMonitarr.Backend.Auth;

public static class AuthServiceExtensions
{
    public static IServiceCollection AddBabyMonitarrAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        var authOptions = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));

        // Register user and API key services
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();

        var method = authOptions.Method;

        // Persist data protection keys for cookie encryption
        var dataDir = configuration.GetConnectionString("DefaultConnection");
        var keysPath = "/app/data/keys";
        if (dataDir != null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(dataDir, @"Data Source=(.+)");
            if (match.Success)
            {
                var dbDir = Path.GetDirectoryName(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(dbDir))
                    keysPath = Path.Combine(dbDir, "keys");
            }
        }

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = "Auto";
            options.DefaultChallengeScheme = "Auto";
        });

        // Cookie auth — always registered for session persistence
        authBuilder.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/auth/login";
            options.LogoutPath = "/auth/logout";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        // API key auth — always registered for mobile app access
        authBuilder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, null);

        // Conditionally register OIDC or ProxyHeader
        if (string.Equals(method, "OIDC", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(authOptions.Oidc.Authority))
        {
            authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = authOptions.Oidc.Authority;
                options.ClientId = authOptions.Oidc.ClientId;
                options.ClientSecret = authOptions.Oidc.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                options.Scope.Clear();
                foreach (var scope in authOptions.Oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    options.Scope.Add(scope);
                }

                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
                        var principal = context.Principal;
                        if (principal == null) return;

                        var username = principal.FindFirst(ClaimTypes.Name)?.Value
                            ?? principal.FindFirst("preferred_username")?.Value
                            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? "unknown";
                        var displayName = principal.FindFirst("name")?.Value
                            ?? principal.FindFirst(ClaimTypes.GivenName)?.Value;
                        var email = principal.FindFirst(ClaimTypes.Email)?.Value;

                        var user = await userService.EnsureUserFromExternalAsync(username, displayName, email);

                        // Add our own claims
                        var identity = (ClaimsIdentity)principal.Identity!;
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
                        if (user.IsAdmin)
                            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
                    }
                };
            });
        }

        if (string.Equals(method, "ProxyHeader", StringComparison.OrdinalIgnoreCase))
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, ProxyHeaderAuthenticationHandler>(
                ProxyHeaderAuthenticationHandler.SchemeName, null);
        }

        // Determine the challenge scheme (for unauthenticated redirects)
        var challengeScheme = string.Equals(method, "OIDC", StringComparison.OrdinalIgnoreCase) &&
                              !string.IsNullOrWhiteSpace(authOptions.Oidc.Authority)
            ? OpenIdConnectDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;

        // Policy scheme that routes to the correct handler based on request
        authBuilder.AddPolicyScheme("Auto", "Auto", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                // If proxy header is present and proxy mode is active
                if (string.Equals(method, "ProxyHeader", StringComparison.OrdinalIgnoreCase))
                {
                    var userHeader = authOptions.Proxy.UserHeader;
                    if (context.Request.Headers.ContainsKey(userHeader))
                        return ProxyHeaderAuthenticationHandler.SchemeName;
                }

                // If API key is present (header or query param)
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    return ApiKeyAuthenticationHandler.SchemeName;

                var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(accessToken))
                    return ApiKeyAuthenticationHandler.SchemeName;

                // Fall back to cookies (which will challenge via OIDC if configured)
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };

            // Static challenge scheme: where to redirect unauthenticated users
            options.ForwardChallenge = challengeScheme;
        });

        // Require authentication on all endpoints by default
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}
