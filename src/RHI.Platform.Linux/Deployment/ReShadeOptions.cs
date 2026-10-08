using RHI.Core.Deployment;
namespace RHI.Platform.Linux.Deployment;

public enum ReShadeChannel
{
    Stable,
    Nightly,
    Legacy,
    Custom
}

public sealed class ReShadeInstallOptions
{
    public ReShadeChannel Channel { get; set; } = ReShadeChannel.Stable;
    public string? LegacyVersion { get; set; }
    public string? CustomDllPath { get; set; }
    public bool ManageIni { get; set; } = true;
    /// <summary>Optional path to a native Microsoft D3DCompiler_47.dll. An explicit selection is validated and persisted on Apply even when no ordinary RHI shader pack is selected; Proton/Wine prefixes can also install the compiler through the dedicated helper buttons.</summary>
    public string? D3DCompiler47Path { get; set; }
    public IReadOnlyList<string> ShaderPackIds { get; set; } = [];

}

public sealed record ReShadeUpdateStatus(
    bool IsInstalled,
    bool UpdateAvailable,
    string Message,
    string? InstalledVersion = null,
    string? LatestVersion = null,
    ReShadeChannel? Channel = null,
    // The check could not tell (reshade.me down, nightly size unknown, custom source missing): no update.
    bool CouldNotCheck = false);

internal sealed record ReShadeStagedDll(
    string DllPath,
    string Version,
    string SourceUrl,
    string SourceIdentity,
    ReShadeChannel Channel,
    bool FromCache = false,
    // Why the cached copy was used (the reshade.me outage message); null for a fresh download.
    string? FallbackReason = null,
    // The downloaded setup/nightly zip the DLL came from and what its integrity rests on (#26/#27); null for Custom.
    VerifiedDownload? Download = null);
