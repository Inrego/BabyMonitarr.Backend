using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Controllers;

[Route("api-keys")]
public class ApiKeysController : Controller
{
    private readonly IApiKeyService _apiKeyService;

    public ApiKeysController(IApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var userId = GetUserId();
        var keys = await _apiKeyService.ListKeysForUserAsync(userId);
        ViewData["Title"] = "API Keys";
        return View(keys);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name)
    {
        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Name is required.";
            return RedirectToAction("Index");
        }

        var (_, plainTextKey) = await _apiKeyService.GenerateKeyAsync(userId, name.Trim());
        TempData["NewKey"] = plainTextKey;
        TempData["NewKeyName"] = name.Trim();

        return RedirectToAction("Index");
    }

    [HttpPost("delete/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        await _apiKeyService.DeleteKeyAsync(id, userId);
        return RedirectToAction("Index");
    }
}
