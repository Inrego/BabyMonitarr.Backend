# Configuration

BabyMonitarr is configured through standard ASP.NET Core configuration — `appsettings.json` or environment variables. All monitor/room settings are managed through the web UI; the options below control server-level behavior.

## Environment Variable Format

ASP.NET Core maps nested JSON keys to environment variables using double underscores:

```
WebRtc__AdvertisedAddress=192.168.1.50
FFmpegDiagnostics__Enabled=true
Auth__Method=OIDC
```

## WebRTC Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `WebRtc__AdvertisedAddress` | *(empty)* | Force the ICE host candidate address (LAN IP or FQDN) |
| `WebRtc__InferAdvertisedAddressFromForwardedHost` | `true` | Infer advertised address from reverse proxy `Forwarded`/`X-Forwarded-Host` headers |
| `WebRtc__BindAddress` | *(empty)* | Bind WebRTC to a specific network interface |
| `WebRtc__BindPort` | `0` | Bind to a specific port (0 = ephemeral) |
| `WebRtc__IncludeAllInterfaceAddresses` | `false` | Advertise all network interface addresses as ICE candidates |
| `WebRtc__GatherTimeoutMs` | `0` | ICE gathering timeout in milliseconds (0 = no timeout) |
| `WebRtc__RtpPortRange__Start` | *(null)* | Start of deterministic UDP media port range |
| `WebRtc__RtpPortRange__End` | *(null)* | End of deterministic UDP media port range |
| `WebRtc__RtpPortRange__Shuffle` | `false` | Randomize port selection within the range |

### ICE Servers

Default STUN server: `stun:stun.l.google.com:19302`. Override with:

```
WebRtc__IceServers__0__Urls=stun:your-stun-server:3478
WebRtc__IceServers__0__Username=user
WebRtc__IceServers__0__Credential=pass
```

## FFmpeg Diagnostics

Enable deep FFmpeg diagnostics for debugging RTSP stream issues without rebuilding:

| Variable | Default | Description |
|----------|---------|-------------|
| `FFmpegDiagnostics__Enabled` | `false` | Enable diagnostic logging |
| `FFmpegDiagnostics__NativeLogLevel` | `warning` | FFmpeg native log level (`trace`, `debug`, `info`, `warning`, `error`) |
| `FFmpegDiagnostics__LogRtspOptions` | `true` | Log RTSP connection options |
| `FFmpegDiagnostics__LogStreamMetadata` | `true` | Log stream metadata on open |
| `FFmpegDiagnostics__LogFrameStats` | `true` | Log periodic frame statistics |
| `FFmpegDiagnostics__FrameStatsInterval` | `300` | Frames between stats logs |
| `FFmpegDiagnostics__RunFfprobeOnOpenFailure` | `true` | Run `ffprobe` when stream open fails |
| `FFmpegDiagnostics__FfprobePath` | *(empty)* | Custom path to `ffprobe` binary |
| `FFmpegDiagnostics__FfprobeTimeoutSeconds` | `8` | Probe timeout |
| `FFmpegDiagnostics__FfprobeRtspTransport` | `tcp` | RTSP transport for ffprobe |
| `FFmpegDiagnostics__FfprobeMaxLogLines` | `120` | Max log lines to capture from ffprobe |

### Example: Debug RTSP issues

```bash
docker run -d \
  --name babymonitarr \
  -p 8080:8080 \
  -v babymonitarr-data:/app/data \
  -e FFmpegDiagnostics__Enabled=true \
  -e FFmpegDiagnostics__NativeLogLevel=trace \
  -e Logging__LogLevel__BabyMonitarr.Backend.Services.RtspAudioReader=Debug \
  -e Logging__LogLevel__BabyMonitarr.Backend.Services.RtspVideoReader=Debug \
  ghcr.io/inrego/babymonitarr:latest
```

## Video Codec Passthrough

Video streaming is passthrough-only — BabyMonitarr does **not** transcode video.

- Supported source codecs: `H264`, `H265`, `VP8`
- If the source codec is unsupported or the browser cannot negotiate it, stream startup fails with an explicit error
- Video frame cadence follows source packet timing (no fixed FPS cap)

## Logging

Standard ASP.NET Core logging configuration:

```
Logging__LogLevel__Default=Information
Logging__LogLevel__Microsoft.AspNetCore=Warning
```

Credentials in RTSP URLs are automatically redacted in application logs.

## Frontend WebRTC Diagnostics

For browser-side debugging, open the dashboard with `?webrtcDebug=1`:

```
http://localhost:8080/Home/Dashboard?webrtcDebug=1
```

This enables detailed `[BM-DIAG]` log lines in the browser console covering SignalR signaling, WebRTC state transitions, media events, and ICE candidate diagnostics. Disable with `?webrtcDebug=0`.
