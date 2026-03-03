using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services;

internal static class RtspDiagnostics
{
    private static readonly Regex UriUserInfoRegex = new(
        @"(?<scheme>\b[a-z][a-z0-9+\-.]*://)(?<userinfo>[^/\s@]+)@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveQueryRegex = new(
        @"(?<key>(?:password|pass|pwd|token|access_token|auth|username|user))=(?<value>[^&\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string RedactRtspUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string sanitized = UriUserInfoRegex.Replace(url, "${scheme}***:***@");
        sanitized = SensitiveQueryRegex.Replace(sanitized, "${key}=***");
        return sanitized;
    }

    internal static string RedactFreeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string sanitized = UriUserInfoRegex.Replace(text, "${scheme}***:***@");
        sanitized = SensitiveQueryRegex.Replace(sanitized, "${key}=***");
        return sanitized;
    }

    internal static string RedactOptionValue(string key, string? value)
    {
        if (IsSensitiveKey(key))
        {
            return "***";
        }

        return RedactFreeText(value);
    }

    internal static unsafe string FormatDictionary(AVDictionary* dictionary)
    {
        if (dictionary == null)
        {
            return "<none>";
        }

        List<string> entries = new();
        AVDictionaryEntry* entry = null;

        while ((entry = ffmpeg.av_dict_get(dictionary, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            string key = Marshal.PtrToStringAnsi((IntPtr)entry->key) ?? string.Empty;
            string value = Marshal.PtrToStringAnsi((IntPtr)entry->value) ?? string.Empty;
            entries.Add($"{key}={RedactOptionValue(key, value)}");
        }

        return entries.Count == 0 ? "<none>" : string.Join(", ", entries);
    }

    internal static unsafe string FormatStreamMetadata(AVFormatContext* formatContext, int streamIndex)
    {
        if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams)
        {
            return $"stream={streamIndex} unavailable";
        }

        AVStream* stream = formatContext->streams[streamIndex];
        AVCodecParameters* codecParameters = stream->codecpar;
        string codecName = ffmpeg.avcodec_get_name(codecParameters->codec_id);
        if (string.IsNullOrWhiteSpace(codecName))
        {
            codecName = codecParameters->codec_id.ToString();
        }

        string mediaType = codecParameters->codec_type.ToString().Replace("AVMEDIA_TYPE_", string.Empty);
        string baseMetadata =
            $"stream={streamIndex}, type={mediaType}, codec={codecName}, codecTag=0x{codecParameters->codec_tag:x}, timeBase={stream->time_base.num}/{stream->time_base.den}";

        if (codecParameters->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
        {
            return $"{baseMetadata}, sampleRate={codecParameters->sample_rate}, channels={codecParameters->ch_layout.nb_channels}, format={(AVSampleFormat)codecParameters->format}";
        }

        if (codecParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            return $"{baseMetadata}, size={codecParameters->width}x{codecParameters->height}, format={(AVPixelFormat)codecParameters->format}";
        }

        return baseMetadata;
    }

    internal static unsafe string GetFfmpegError(int errorCode)
    {
        byte* errorBuffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, errorBuffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)errorBuffer) ?? $"FFmpeg error {errorCode}";
    }

    private static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("username", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("user", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RtspStatsAccumulator
{
    private readonly int _logInterval;
    private long _nextLogAt;

    public RtspStatsAccumulator(int logInterval)
    {
        _logInterval = Math.Max(1, logInterval);
        _nextLogAt = _logInterval;
    }

    public long PacketsRead { get; private set; }
    public long TargetPacketsRead { get; private set; }
    public long FramesDecoded { get; private set; }
    public long ReadErrors { get; private set; }
    public long SendPacketErrors { get; private set; }
    public long ReceiveFrameErrors { get; private set; }

    public void RecordPacket(bool isTargetStream)
    {
        PacketsRead++;
        if (isTargetStream)
        {
            TargetPacketsRead++;
        }
    }

    public void RecordFrameDecoded()
    {
        FramesDecoded++;
    }

    public void RecordReadError()
    {
        ReadErrors++;
    }

    public void RecordSendPacketError()
    {
        SendPacketErrors++;
    }

    public void RecordReceiveFrameError()
    {
        ReceiveFrameErrors++;
    }

    public bool ShouldLog()
    {
        if (TargetPacketsRead < _nextLogAt)
        {
            return false;
        }

        _nextLogAt += _logInterval;
        return true;
    }

    public string BuildSummary()
    {
        return $"packetsRead={PacketsRead}, targetPackets={TargetPacketsRead}, decodedFrames={FramesDecoded}, readErrors={ReadErrors}, sendPacketErrors={SendPacketErrors}, receiveFrameErrors={ReceiveFrameErrors}";
    }
}
