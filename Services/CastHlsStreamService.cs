using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BabyMonitarr.Backend.Models;
using Microsoft.Extensions.Options;

namespace BabyMonitarr.Backend.Services;

/// <summary>
/// One live HLS rendition of a room, produced by an ffmpeg process and served over HTTP so
/// Cast receivers can pull it. Reference counted: several cast devices showing the same room
/// with the same profile share one ffmpeg process.
/// </summary>
public sealed class CastHlsStream
{
    public required string Key { get; init; }
    public required int RoomId { get; init; }
    public required bool Video { get; init; }
    public required string Token { get; init; }
    public required string DirectoryPath { get; init; }

    internal int RefCount;
    internal DateTime? IdleSinceUtc;
    internal Process? Process;
    internal CancellationTokenSource? Cts;
    internal Task? Supervisor;

    public string PlaylistPath => $"/cast/hls/{Token}/index.m3u8";
    public string ContentType => "application/vnd.apple.mpegurl";
}

public interface ICastHlsStreamService
{
    Task<CastHlsStream> AcquireAsync(Room room, bool video, CancellationToken cancellationToken);
    void Release(CastHlsStream stream);
    bool TryResolveFile(string token, string fileName, out string fullPath);
}

public sealed class CastHlsStreamService : ICastHlsStreamService, IHostedService, IDisposable
{
    private static readonly string[] LinuxFfmpegCandidates =
    {
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "/usr/lib/jellyfin-ffmpeg8/ffmpeg",
        "/usr/lib/jellyfin-ffmpeg7/ffmpeg",
        "/usr/bin/ffmpeg"
    };

    private readonly ILogger<CastHlsStreamService> _logger;
    private readonly IOptionsMonitor<CastOptions> _options;
    private readonly ConcurrentDictionary<string, CastHlsStream> _streams = new();
    private readonly ConcurrentDictionary<string, CastHlsStream> _streamsByToken = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _reaper;
    private bool _disposed;

    public CastHlsStreamService(ILogger<CastHlsStreamService> logger, IOptionsMonitor<CastOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Clear anything a previous run left behind before segments start piling up.
        TryCleanRoot();
        _reaper = new Timer(_ => ReapIdleStreams(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _reaper?.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var stream in _streams.Values.ToList())
        {
            await StopStreamAsync(stream);
        }
        TryCleanRoot();
    }

    public async Task<CastHlsStream> AcquireAsync(Room room, bool video, CancellationToken cancellationToken)
    {
        string key = $"{room.Id}:{(video ? "v" : "a")}";

        await _gate.WaitAsync(cancellationToken);
        CastHlsStream stream;
        bool created = false;
        try
        {
            if (!_streams.TryGetValue(key, out var existing))
            {
                existing = StartStream(room, video, key);
                _streams[key] = existing;
                _streamsByToken[existing.Token] = existing;
                created = true;
            }

            existing.RefCount++;
            existing.IdleSinceUtc = null;
            stream = existing;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await WaitForPlaylistAsync(stream, cancellationToken);
        }
        catch
        {
            Release(stream);
            throw;
        }

        if (created)
        {
            _logger.LogInformation(
                "Cast HLS stream started for room {RoomId} ({Profile})",
                room.Id,
                video ? "video" : "audio");
        }

        return stream;
    }

    public void Release(CastHlsStream stream)
    {
        _gate.Wait();
        try
        {
            if (!_streams.TryGetValue(stream.Key, out var tracked) || !ReferenceEquals(tracked, stream))
            {
                return;
            }

            stream.RefCount = Math.Max(0, stream.RefCount - 1);
            if (stream.RefCount == 0)
            {
                stream.IdleSinceUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryResolveFile(string token, string fileName, out string fullPath)
    {
        fullPath = string.Empty;
        if (!_streamsByToken.TryGetValue(token, out var stream))
        {
            return false;
        }

        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
        {
            return false;
        }

        // Only the playlist and the numbered segments ffmpeg writes are reachable.
        bool isSegment = fileName.StartsWith("seg", StringComparison.Ordinal) &&
                         fileName.EndsWith(".ts", StringComparison.Ordinal);
        if (fileName != "index.m3u8" && !isSegment)
        {
            return false;
        }

        string root = Path.GetFullPath(stream.DirectoryPath);
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private CastHlsStream StartStream(Room room, bool video, string key)
    {
        string token = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(RootPath, token);
        Directory.CreateDirectory(dir);

        var stream = new CastHlsStream
        {
            Key = key,
            RoomId = room.Id,
            Video = video,
            Token = token,
            DirectoryPath = dir
        };

        stream.Cts = new CancellationTokenSource();
        string sourceUrl = BuildSourceUrl(room);
        stream.Supervisor = Task.Run(() => SuperviseAsync(stream, room, sourceUrl, stream.Cts.Token));
        return stream;
    }

    /// <summary>
    /// Keeps ffmpeg alive for as long as the stream has listeners. Cameras drop RTSP sessions
    /// overnight, and a dead encoder means a frozen TV with no error anywhere.
    /// </summary>
    private async Task SuperviseAsync(CastHlsStream stream, Room room, string sourceUrl, CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var startInfo = BuildFfmpegStartInfo(stream, sourceUrl, room);
            try
            {
                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("ffmpeg failed to start");
                }

                stream.Process = process;
                _ = DrainAsync(process.StandardError, stream, ct);
                await process.WaitForExitAsync(ct);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogWarning(
                    "Cast ffmpeg for room {RoomId} exited with code {ExitCode}; restarting",
                    stream.RoomId,
                    process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cast ffmpeg for room {RoomId} could not run", stream.RoomId);
            }
            finally
            {
                stream.Process = null;
            }

            consecutiveFailures++;
            int delaySeconds = Math.Min(30, consecutiveFailures * 2);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrainAsync(StreamReader reader, CastHlsStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) return;
                if (string.IsNullOrWhiteSpace(line)) continue;
                _logger.LogDebug(
                    "Cast ffmpeg (room {RoomId}): {Line}",
                    stream.RoomId,
                    RtspDiagnostics.RedactFreeText(line));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cast ffmpeg log reader ended for room {RoomId}", stream.RoomId);
        }
    }

    private ProcessStartInfo BuildFfmpegStartInfo(CastHlsStream stream, string sourceUrl, Room room)
    {
        CastOptions options = _options.CurrentValue;
        int segmentSeconds = Math.Max(1, options.SegmentSeconds);
        int playlistSize = Math.Max(3, options.PlaylistSize);

        var info = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(options),
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Arg(params string[] values)
        {
            foreach (string value in values) info.ArgumentList.Add(value);
        }

        Arg("-hide_banner", "-loglevel", "warning", "-nostdin");
        Arg("-rtsp_transport", "tcp");
        Arg("-fflags", "+genpts");
        Arg("-i", sourceUrl);

        if (stream.Video)
        {
            Arg("-map", "0:v:0", "-map", "0:a:0?");

            // Chromecast plays H.264 in MPEG-TS; anything else has to be re-encoded, which costs
            // roughly one CPU core per 1080p stream. Fine for the one or two rooms a household
            // casts at once - a camera farm would want hardware encoding instead.
            bool sourceIsH264 =
                string.Equals(room.VideoPassthroughCodec, "h264", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(room.VideoSourceCodecName, "h264", StringComparison.OrdinalIgnoreCase);

            if (sourceIsH264)
            {
                Arg("-c:v", "copy");
            }
            else
            {
                Arg("-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                    "-pix_fmt", "yuv420p", "-g", (segmentSeconds * 25).ToString());
            }
        }
        else
        {
            Arg("-vn", "-map", "0:a:0");
        }

        Arg("-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "44100");
        Arg("-f", "hls");
        Arg("-hls_time", segmentSeconds.ToString());
        Arg("-hls_list_size", playlistSize.ToString());
        Arg("-hls_flags", "delete_segments+omit_endlist+independent_segments+temp_file");
        Arg("-hls_segment_type", "mpegts");
        Arg("-hls_segment_filename", Path.Combine(stream.DirectoryPath, "seg%05d.ts"));
        Arg(Path.Combine(stream.DirectoryPath, "index.m3u8"));

        return info;
    }

    private static string BuildSourceUrl(Room room)
    {
        string url = room.CameraStreamUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"Room {room.Id} has no camera stream URL to cast.");
        }

        if (string.IsNullOrEmpty(room.CameraUsername) || string.IsNullOrEmpty(room.CameraPassword))
        {
            return url;
        }

        // The ffmpeg CLI has no -username/-password for RTSP the way the AutoGen bindings do,
        // so credentials go in the userinfo component. Logs are redacted by RtspDiagnostics.
        var builder = new UriBuilder(url)
        {
            UserName = Uri.EscapeDataString(room.CameraUsername),
            Password = Uri.EscapeDataString(room.CameraPassword)
        };
        return builder.Uri.ToString();
    }

    private static async Task WaitForPlaylistAsync(CastHlsStream stream, CancellationToken cancellationToken)
    {
        string playlist = Path.Combine(stream.DirectoryPath, "index.m3u8");
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A playlist with at least one segment - an empty one makes the receiver give up.
            if (File.Exists(playlist))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(playlist, cancellationToken);
                    if (content.Contains(".ts", StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // ffmpeg is mid-write; try again.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            $"Room {stream.RoomId} produced no HLS segments within 20s - check the camera stream.");
    }

    private void ReapIdleStreams()
    {
        if (_disposed) return;

        int linger = Math.Max(0, _options.CurrentValue.StreamLingerSeconds);
        foreach (var stream in _streams.Values.ToList())
        {
            if (stream.RefCount > 0 || stream.IdleSinceUtc == null) continue;
            if (DateTime.UtcNow - stream.IdleSinceUtc.Value < TimeSpan.FromSeconds(linger)) continue;

            _ = StopStreamAsync(stream);
        }
    }

    private async Task StopStreamAsync(CastHlsStream stream)
    {
        _streams.TryRemove(stream.Key, out _);
        _streamsByToken.TryRemove(stream.Token, out _);

        try
        {
            stream.Cts?.Cancel();
            var process = stream.Process;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            if (stream.Supervisor != null)
            {
                await Task.WhenAny(stream.Supervisor, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping cast HLS stream for room {RoomId}", stream.RoomId);
        }
        finally
        {
            stream.Cts?.Dispose();
            stream.Cts = null;
            TryDeleteDirectory(stream.DirectoryPath);
            _logger.LogInformation(
                "Cast HLS stream stopped for room {RoomId} ({Profile})",
                stream.RoomId,
                stream.Video ? "video" : "audio");
        }
    }

    private string RootPath =>
        string.IsNullOrWhiteSpace(_options.CurrentValue.HlsPath)
            ? Path.Combine(Path.GetTempPath(), "babymonitarr-cast")
            : _options.CurrentValue.HlsPath!;

    private void TryCleanRoot()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
            Directory.CreateDirectory(RootPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean cast HLS directory {Path}", RootPath);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete cast HLS directory {Path}", path);
        }
    }

    private static string ResolveFfmpegPath(CastOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfmpegPath))
        {
            return options.FfmpegPath!;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : "ffmpeg.exe";
        }

        foreach (string candidate in LinuxFfmpegCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "ffmpeg";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reaper?.Dispose();
        _gate.Dispose();
    }
}
