using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace BabyMonitarr.Backend.Services
{
    internal static class FFmpegLibraryLoader
    {
        private static readonly object Sync = new();
        private static bool _isInitialized;
        private static Exception? _initializationException;

        private static readonly string[] LinuxLibraryPaths =
        {
            "/usr/lib/jellyfin-ffmpeg/lib",
            "/usr/lib/jellyfin-ffmpeg8/lib",
            "/usr/lib/jellyfin-ffmpeg7/lib",
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib/aarch64-linux-gnu",
            "/usr/lib"
        };

        public static void EnsureInitialized(ILogger logger)
        {
            if (_isInitialized)
            {
                return;
            }

            lock (Sync)
            {
                if (_isInitialized)
                {
                    return;
                }

                if (_initializationException != null)
                {
                    throw new InvalidOperationException(
                        "FFmpeg initialization previously failed. See inner exception for details.",
                        _initializationException);
                }

                string ffmpegPath = ResolveRootPath(logger);
                string bindingsVersion = typeof(ffmpeg).Assembly.GetName().Version?.ToString() ?? "unknown";

                try
                {
                    ffmpeg.RootPath = ffmpegPath;
                    DynamicallyLoadedBindings.Initialize();

                    string ffmpegVersion;
                    try
                    {
                        ffmpegVersion = ffmpeg.av_version_info();
                        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
                    }
                    catch (NotSupportedException ex)
                    {
                        throw BuildVersionMismatchException(ffmpegPath, bindingsVersion, ex);
                    }

                    _isInitialized = true;
                    logger.LogInformation(
                        "FFmpeg initialized. Version: {FFmpegVersion}. Root path: {FFmpegPath}",
                        ffmpegVersion,
                        ffmpegPath);
                }
                catch (Exception ex)
                {
                    _initializationException = ex;
                    logger.LogError(
                        ex,
                        "Failed to initialize FFmpeg. Ensure installed FFmpeg shared libraries match FFmpeg.AutoGen {BindingsVersion}.",
                        bindingsVersion);
                    throw;
                }
            }
        }

        private static string ResolveRootPath(ILogger logger)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string ffmpegPath = Path.Combine(appPath, "FFmpeg");

                if (!Directory.Exists(ffmpegPath))
                {
                    throw new DirectoryNotFoundException(
                        $"FFmpeg directory not found at '{ffmpegPath}'. Ensure FFmpeg binaries are included with the application.");
                }

                return ffmpegPath;
            }

            string? envPath = Environment.GetEnvironmentVariable("FFMPEG_LIB_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                if (Directory.Exists(envPath))
                {
                    return envPath;
                }

                logger.LogWarning(
                    "FFMPEG_LIB_PATH is set to '{FFMpegLibPath}', but that directory does not exist. Falling back to known paths.",
                    envPath);
            }

            string? detectedPath = LinuxLibraryPaths.FirstOrDefault(Directory.Exists);
            if (detectedPath != null)
            {
                return detectedPath;
            }

            throw new DirectoryNotFoundException(
                $"Unable to find FFmpeg shared libraries. Checked: {string.Join(", ", LinuxLibraryPaths)}");
        }

        private static InvalidOperationException BuildVersionMismatchException(
            string ffmpegPath,
            string bindingsVersion,
            Exception innerException)
        {
            string discoveredLibraries = "none";

            if (Directory.Exists(ffmpegPath))
            {
                IEnumerable<string> libs = Directory.EnumerateFiles(ffmpegPath, "libavutil.so*")
                    .Concat(Directory.EnumerateFiles(ffmpegPath, "libavcodec.so*"))
                    .Concat(Directory.EnumerateFiles(ffmpegPath, "libavformat.so*"))
                    .Concat(Directory.EnumerateFiles(ffmpegPath, "libswresample.so*"))
                    .Concat(Directory.EnumerateFiles(ffmpegPath, "libswscale.so*"))
                    .Select(path => Path.GetFileName(path) ?? string.Empty)
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .Distinct()
                    .OrderBy(fileName => fileName);

                discoveredLibraries = string.Join(", ", libs);
                if (string.IsNullOrWhiteSpace(discoveredLibraries))
                {
                    discoveredLibraries = "none";
                }
            }

            string message =
                $"FFmpeg bindings could not be initialized from '{ffmpegPath}'. " +
                $"FFmpeg.AutoGen {bindingsVersion} requires matching FFmpeg major versions. " +
                $"Discovered libraries: {discoveredLibraries}. " +
                "For Docker, install FFmpeg 8 (for example jellyfin-ffmpeg8) and set FFMPEG_LIB_PATH/LD_LIBRARY_PATH to its lib directory.";

            return new InvalidOperationException(message, innerException);
        }
    }
}
