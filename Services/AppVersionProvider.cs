using System.Reflection;

namespace BabyMonitarr.Backend.Services;

public interface IAppVersionProvider
{
    string DisplayVersion { get; }
}

public sealed class AppVersionProvider : IAppVersionProvider
{
    public string DisplayVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        // Use this assembly directly so the resolved version is stable across host models.
        Assembly assembly = typeof(AppVersionProvider).Assembly;

        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator >= 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is null)
        {
            return "0.0.0";
        }

        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
    }
}
