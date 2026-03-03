using BabyMonitarr.Backend.Models;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BabyMonitarr.Backend.Services;

public sealed class VideoCodecProbeResult
{
    public string SourceCodecName { get; set; } = string.Empty;
    public VideoPassthroughCodec? PassthroughCodec { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsSupported => PassthroughCodec.HasValue;
}

public interface IVideoCodecProbeService
{
    Task<VideoCodecProbeResult> ProbeAsync(
        int roomId,
        string rtspUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken);
}

internal sealed class VideoCodecProbeService : IVideoCodecProbeService
{
    private readonly ILogger<VideoCodecProbeService> _logger;
    private readonly IOptionsMonitor<FfmpegDiagnosticsOptions> _diagnosticsOptions;
    private readonly FfprobeSnapshotService _ffprobeSnapshotService;

    public VideoCodecProbeService(
        ILogger<VideoCodecProbeService> logger,
        IOptionsMonitor<FfmpegDiagnosticsOptions> diagnosticsOptions,
        FfprobeSnapshotService ffprobeSnapshotService)
    {
        _logger = logger;
        _diagnosticsOptions = diagnosticsOptions;
        _ffprobeSnapshotService = ffprobeSnapshotService;

        FFmpegLibraryLoader.EnsureInitialized(_logger, _diagnosticsOptions.CurrentValue);
    }

    public Task<VideoCodecProbeResult> ProbeAsync(
        int roomId,
        string rtspUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => ProbeInternal(roomId, rtspUrl, username, password, cancellationToken), cancellationToken);
    }

    private unsafe VideoCodecProbeResult ProbeInternal(
        int roomId,
        string rtspUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            return new VideoCodecProbeResult
            {
                FailureReason = "Camera stream URL is not configured.",
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        string redactedRtspUrl = RtspDiagnostics.RedactRtspUrl(rtspUrl);
        FfmpegDiagnosticsOptions diagnostics = _diagnosticsOptions.CurrentValue;

        AVFormatContext* formatContext = null;
        AVDictionary* options = null;
        string failureStage = "probe_start";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
            {
                throw new InvalidOperationException("Could not allocate FFmpeg format context for codec probe.");
            }

            ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
            ffmpeg.av_dict_set(&options, "max_delay", "100000", 0);
            ffmpeg.av_dict_set(&options, "analyzeduration", "1000000", 0);
            ffmpeg.av_dict_set(&options, "probesize", "100000", 0);

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                ffmpeg.av_dict_set(&options, "username", username, 0);
                ffmpeg.av_dict_set(&options, "password", password, 0);
            }

            if (diagnostics.Enabled && diagnostics.LogRtspOptions)
            {
                _logger.LogDebug(
                    "RTSP codec probe options for room {RoomId}: {Options}",
                    roomId,
                    RtspDiagnostics.FormatDictionary(options));
            }

            failureStage = "avformat_open_input";
            int ret = ffmpeg.avformat_open_input(&formatContext, rtspUrl, null, &options);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"Could not open RTSP stream for codec probe: {RtspDiagnostics.GetFfmpegError(ret)}");
            }

            failureStage = "avformat_find_stream_info";
            ret = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"Could not read stream information for codec probe: {RtspDiagnostics.GetFfmpegError(ret)}");
            }

            int videoStreamIndex = -1;
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }

            failureStage = "find_video_stream";
            if (videoStreamIndex == -1)
            {
                throw new InvalidOperationException("Could not find a video stream in the RTSP feed.");
            }

            AVCodecID sourceCodecId = formatContext->streams[videoStreamIndex]->codecpar->codec_id;
            string sourceCodecName = GetCodecName(sourceCodecId);

            if (TryResolvePassthroughCodec(sourceCodecId, out var passthroughCodec))
            {
                return new VideoCodecProbeResult
                {
                    SourceCodecName = sourceCodecName,
                    PassthroughCodec = passthroughCodec,
                    CheckedAtUtc = DateTime.UtcNow
                };
            }

            return new VideoCodecProbeResult
            {
                SourceCodecName = sourceCodecName,
                FailureReason =
                    $"Video passthrough does not support RTSP codec '{sourceCodecName}'. Supported codecs are H264, H265, and VP8.",
                CheckedAtUtc = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RTSP video codec probe failed for room {RoomId} ({Url})",
                roomId,
                redactedRtspUrl);

            _ffprobeSnapshotService.CaptureIfEnabled(
                roomId,
                "video",
                rtspUrl,
                $"codec_probe:{failureStage}",
                cancellationToken);

            return new VideoCodecProbeResult
            {
                FailureReason = RtspDiagnostics.RedactFreeText(ex.Message),
                CheckedAtUtc = DateTime.UtcNow
            };
        }
        finally
        {
            if (options != null)
            {
                ffmpeg.av_dict_free(&options);
            }

            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    private static bool TryResolvePassthroughCodec(AVCodecID codecId, out VideoPassthroughCodec codec)
    {
        switch (codecId)
        {
            case AVCodecID.AV_CODEC_ID_H264:
                codec = VideoPassthroughCodec.H264;
                return true;
            case AVCodecID.AV_CODEC_ID_HEVC:
                codec = VideoPassthroughCodec.H265;
                return true;
            case AVCodecID.AV_CODEC_ID_VP8:
                codec = VideoPassthroughCodec.VP8;
                return true;
            default:
                codec = default;
                return false;
        }
    }

    private static unsafe string GetCodecName(AVCodecID codecId)
    {
        string? codecName = ffmpeg.avcodec_get_name(codecId);
        return string.IsNullOrWhiteSpace(codecName) ? codecId.ToString() : codecName.Trim();
    }
}
