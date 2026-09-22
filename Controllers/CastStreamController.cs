using BabyMonitarr.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BabyMonitarr.Backend.Controllers;

/// <summary>
/// Serves the HLS playlist and segments that Cast receivers pull.
/// </summary>
/// <remarks>
/// Anonymous by necessity: a Chromecast cannot present a cookie or an API key. Access is gated
/// by the per-stream random token in the path, which only lives as long as the cast session and
/// is never persisted. Anyone who can reach the server and guess a 128-bit token could watch the
/// stream; that is the same trust boundary as the LAN the camera is already on.
/// </remarks>
[AllowAnonymous]
[Route("cast/hls")]
public class CastStreamController : Controller
{
    private readonly ICastHlsStreamService _streams;

    public CastStreamController(ICastHlsStreamService streams)
    {
        _streams = streams;
    }

    [HttpGet("{token}/{file}")]
    public IActionResult GetFile(string token, string file)
    {
        if (!_streams.TryResolveFile(token, file, out string fullPath))
        {
            return NotFound();
        }

        string contentType = file.EndsWith(".m3u8", StringComparison.Ordinal)
            ? "application/vnd.apple.mpegurl"
            : "video/mp2t";

        // The playlist changes every segment, so nothing here may be cached.
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
    }
}
