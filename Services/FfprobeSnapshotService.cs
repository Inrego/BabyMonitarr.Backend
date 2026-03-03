using System.Diagnostics;
using System.Runtime.InteropServices;
using BabyMonitarr.Backend.Models;
using Microsoft.Extensions.Options;

namespace BabyMonitarr.Backend.Services;

public sealed class FfprobeSnapshotService
{
    private static readonly string[] LinuxProbeCandidates =
    {
        "/usr/lib/jellyfin-ffmpeg/ffprobe",
        "/usr/lib/jellyfin-ffmpeg8/ffprobe",
        "/usr/lib/jellyfin-ffmpeg7/ffprobe",
        "/usr/bin/ffprobe"
    };

    private readonly ILogger<FfprobeSnapshotService> _logger;
    private readonly IOptionsMonitor<FfmpegDiagnosticsOptions> _diagnosticsOptions;

    public FfprobeSnapshotService(
        ILogger<FfprobeSnapshotService> logger,
        IOptionsMonitor<FfmpegDiagnosticsOptions> diagnosticsOptions)
    {
        _logger = logger;
        _diagnosticsOptions = diagnosticsOptions;
    }

    public void CaptureIfEnabled(
        int roomId,
        string streamType,
        string rtspUrl,
        string failureStage,
        CancellationToken cancellationToken)
    {
        FfmpegDiagnosticsOptions options = _diagnosticsOptions.CurrentValue;
        if (!options.Enabled || !options.RunFfprobeOnOpenFailure)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            return;
        }

        string redactedUrl = RtspDiagnostics.RedactRtspUrl(rtspUrl);
        string probePath = ResolveProbePath(options);
        int timeoutSeconds = Math.Max(1, options.FfprobeTimeoutSeconds);
        int maxLines = Math.Max(10, options.FfprobeMaxLogLines);

        ProcessStartInfo startInfo = BuildProbeStartInfo(probePath, options, rtspUrl);

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                _logger.LogWarning(
                    "ffprobe did not start for room {RoomId} ({StreamType}) after failure at {FailureStage}. URL={Url}",
                    roomId,
                    streamType,
                    failureStage,
                    redactedUrl);
                return;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds));
            if (!exited)
            {
                TryKill(process);
                _logger.LogWarning(
                    "ffprobe timed out after {TimeoutSeconds}s for room {RoomId} ({StreamType}) after failure at {FailureStage}. URL={Url}",
                    timeoutSeconds,
                    roomId,
                    streamType,
                    failureStage,
                    redactedUrl);
                return;
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(2));

            string combinedOutput = $"{stdoutTask.Result}{Environment.NewLine}{stderrTask.Result}";
            string sanitizedOutput = RtspDiagnostics.RedactFreeText(combinedOutput);
            IReadOnlyList<string> lines = NormalizeLines(sanitizedOutput, maxLines);

            _logger.LogWarning(
                "ffprobe snapshot for room {RoomId} ({StreamType}) after failure at {FailureStage}. URL={Url}, ProbePath={ProbePath}, ExitCode={ExitCode}{NewLine}{Output}",
                roomId,
                streamType,
                failureStage,
                redactedUrl,
                probePath,
                process.ExitCode,
                Environment.NewLine,
                string.Join(Environment.NewLine, lines));
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Skipping ffprobe snapshot because cancellation was requested for room {RoomId} ({StreamType})",
                roomId,
                streamType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to capture ffprobe snapshot for room {RoomId} ({StreamType}) after failure at {FailureStage}. URL={Url}",
                roomId,
                streamType,
                failureStage,
                redactedUrl);
        }
    }

    private static ProcessStartInfo BuildProbeStartInfo(
        string probePath,
        FfmpegDiagnosticsOptions options,
        string rtspUrl)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = probePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");

        if (!string.IsNullOrWhiteSpace(options.FfprobeRtspTransport))
        {
            startInfo.ArgumentList.Add("-rtsp_transport");
            startInfo.ArgumentList.Add(options.FfprobeRtspTransport);
        }

        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_error");
        startInfo.ArgumentList.Add(rtspUrl);

        return startInfo;
    }

    private static IReadOnlyList<string> NormalizeLines(string text, int maxLines)
    {
        List<string> lines = text
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(maxLines)
            .ToList();

        if (lines.Count == 0)
        {
            lines.Add("<no ffprobe output>");
        }

        return lines;
    }

    private static string ResolveProbePath(FfmpegDiagnosticsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfprobePath))
        {
            return options.FfprobePath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "ffprobe.exe");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            return "ffprobe.exe";
        }

        foreach (string candidate in LinuxProbeCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffprobe";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
