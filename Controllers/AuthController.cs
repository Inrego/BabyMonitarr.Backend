using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Controllers;

[Route("auth")]
public class AuthController : Controller
{
    private readonly IUserService _userService;
    private readonly AuthOptions _authOptions;

    public AuthController(IUserService userService, IOptions<AuthOptions> authOptions)
    {
        _userService = userService;
        _authOptions = authOptions.Value;
    }

    [AllowAnonymous]
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var isFirstRun = await _userService.IsFirstRunAsync();
        return Ok(new
        {
            method = _authOptions.Method,
            setupRequired = isFirstRun && string.Equals(_authOptions.Method, "Local", StringComparison.OrdinalIgnoreCase)
        });
    }

    [AllowAnonymous]
    [HttpGet("setup")]
    public async Task<IActionResult> Setup()
    {
        if (!await _userService.IsFirstRunAsync())
            return RedirectToAction("Login");

        if (!string.Equals(_authOptions.Method, "Local", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Login");

        ViewData["Title"] = "Setup";
        return View();
    }

    [AllowAnonymous]
    [HttpPost("setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetupPost(string username, string password, string confirmPassword)
    {
        if (!await _userService.IsFirstRunAsync())
            return RedirectToAction("Login");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ViewData["Title"] = "Setup";
            ViewData["Error"] = "Username and password are required.";
            return View("Setup");
        }

        if (password != confirmPassword)
        {
            ViewData["Title"] = "Setup";
            ViewData["Error"] = "Passwords do not match.";
            return View("Setup");
        }

        if (password.Length < 8)
        {
            ViewData["Title"] = "Setup";
            ViewData["Error"] = "Password must be at least 8 characters.";
            return View("Setup");
        }

        var user = await _userService.CreateUserAsync(username.Trim(), password, isAdmin: true);
        await SignInUserAsync(user);

        return Redirect("/");
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        // If first run, redirect to setup
        if (string.Equals(_authOptions.Method, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (await _userService.IsFirstRunAsync())
                return RedirectToAction("Setup");
        }

        // If already authenticated, go to dashboard
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(returnUrl ?? "/");

        // For OIDC, trigger the OIDC challenge directly
        if (string.Equals(_authOptions.Method, "OIDC", StringComparison.OrdinalIgnoreCase))
        {
            return Challenge(new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
                Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
        }

        ViewData["Title"] = "Login";
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginPost(string username, string password, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ViewData["Title"] = "Login";
            ViewData["Error"] = "Username and password are required.";
            ViewData["ReturnUrl"] = returnUrl;
            return View("Login");
        }

        var user = await _userService.ValidateCredentialsAsync(username.Trim(), password);
        if (user == null)
        {
            ViewData["Title"] = "Login";
            ViewData["Error"] = "Invalid username or password.";
            ViewData["ReturnUrl"] = returnUrl;
            return View("Login");
        }

        await SignInUserAsync(user);
        return Redirect(returnUrl ?? "/");
    }

    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            name = User.Identity?.Name,
            username = User.FindFirst("username")?.Value,
            email = User.FindFirst(ClaimTypes.Email)?.Value
        });
    }

    private async Task SignInUserAsync(User user)
    {
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

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });
    }
}
