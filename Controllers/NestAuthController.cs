using Microsoft.AspNetCore.Mvc;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Controllers;

[Route("nest/auth")]
public class NestAuthController : Controller
{
    private readonly IGoogleNestAuthService _authService;
    private readonly ILogger<NestAuthController> _logger;

    public NestAuthController(
        IGoogleNestAuthService authService,
        ILogger<NestAuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpGet("start")]
    public async Task<IActionResult> Start()
    {
        try
        {
            var redirectUri = $"{Request.Scheme}://{Request.Host}/nest/auth/callback";
            var authUrl = await _authService.GetAuthorizationUrl(redirectUri);
            return Redirect(authUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Cannot start Nest OAuth flow");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? error)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Nest OAuth callback received error: {Error}", error);
            return Redirect("/?nestAuth=error");
        }

        if (string.IsNullOrEmpty(code))
        {
            return BadRequest(new { error = "No authorization code received" });
        }

        var redirectUri = $"{Request.Scheme}://{Request.Host}/nest/auth/callback";
        var success = await _authService.ExchangeCodeForTokens(code, redirectUri);

        return Redirect(success ? "/Home/System?nestAuth=success" : "/Home/System?nestAuth=error");
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var isLinked = await _authService.IsLinked();
        return Ok(new { isLinked });
    }

    [HttpPost("unlink")]
    public async Task<IActionResult> Unlink()
    {
        await _authService.UnlinkAccount();
        return Ok(new { success = true });
    }
}
