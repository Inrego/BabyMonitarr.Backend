using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Auth;

public class ProxyHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ProxyHeader";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthOptions _authOptions;

    public ProxyHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory,
        IOptions<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _scopeFactory = scopeFactory;
        _authOptions = authOptions.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userHeader = _authOptions.Proxy.UserHeader;
        var username = Request.Headers[userHeader].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(username))
            return AuthenticateResult.NoResult();

        var displayName = Request.Headers[_authOptions.Proxy.NameHeader].FirstOrDefault();
        var email = Request.Headers[_authOptions.Proxy.EmailHeader].FirstOrDefault();

        // Auto-provision or update the user in the database
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var user = await userService.EnsureUserFromExternalAsync(username, displayName, email);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName ?? user.Username),
            new("username", user.Username)
        };

        if (user.Email != null)
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
