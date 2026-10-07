using System.Reflection;

namespace RHI.Core;

/// <summary>
/// Product identity used by the UI and HTTP clients. The version has one source of truth:
/// &lt;Version&gt; in Directory.Build.props, compiled into the assembly's informational version.
/// </summary>
public static class RhiProductInfo
{
    public static string Version { get; } = NormalizeVersion(
        typeof(RhiProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string UserAgent { get; } = "RHI-Linux/" + Version;

    public const string RepositoryUrl = "https://github.com/wizardkof/RHI-Linux";
    public const string MotdUrl = "https://raw.githubusercontent.com/wizardkof/RHI-Linux/main/motd.md";

    /// <summary>Drops SemVer build metadata ("+commit") so the version stays stable across builds.</summary>
    internal static string NormalizeVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            throw new InvalidOperationException("RHI.Core was built without a product version; set <Version> in Directory.Build.props.");
        var plus = informationalVersion.IndexOf('+');
        return (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
    }
}
