using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The explicit host policy of every download source the deployment services use (#42, DEP-06): one
/// named <see cref="TrustedDownloadPolicy"/> per source, checked on the requested link and on the final
/// link after redirects, with a size cap. Each policy's <see cref="TrustedDownloadPolicy.RefusalPrefix"/> has
/// one PT-BR presentation row (guarded by a test over <see cref="All"/>). The digest rule per source (#26)
/// is documented in <c>docs/download-trust.md</c>.
/// </summary>
public static class DownloadSources
{
    private static readonly string[] GitHubReleaseHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Largest executable payload archive accepted from a release (real ones are well under 100 MB).</summary>
    public const long PayloadArchiveMaxBytes = 512L * 1024 * 1024;

    /// <summary>GitHub REST metadata (release lists, commit lookups, Actions artifact digests): api.github.com only.</summary>
    public static readonly TrustedDownloadPolicy GitHubApi = new(
        "GitHub API", "api.github.com", ["api.github.com"], maxBytes: TrustedDownload.MetadataMaxBytes);

    /// <summary>ReShade setup (stable/legacy): crosire's own site, as upstream 2.8.5 (AuxInstallService).</summary>
    public static readonly TrustedDownloadPolicy ReShadeSetup = new(
        "ReShade setup", "reshade.me", ["reshade.me"], maxBytes: 128L * 1024 * 1024);

    /// <summary>
    /// ReShade nightly: nightly.link, as upstream 2.8.5 (ReShadeNightlyService), which redirects to the GitHub
    /// Actions artifact store (<c>productionresultssa&lt;n&gt;.blob.core.windows.net</c>). The zip is checked
    /// against the digest GitHub's Actions API publishes for the artifact (#27).
    /// </summary>
    public static readonly TrustedDownloadPolicy ReShadeNightly = new(
        "ReShade nightly build", "nightly.link or GitHub Actions storage", ["nightly.link"],
        maxBytes: 128L * 1024 * 1024, hostPatterns: [@"productionresultssa[0-9]{1,3}\.blob\.core\.windows\.net"]);

    /// <summary>ReLimiter / Display Commander / other GitHub-released ReShade add-ons (GitHubReShadeAddonDeploymentService).</summary>
    public static readonly TrustedDownloadPolicy GitHubAddonRelease = new(
        "ReShade add-on release", "GitHub", GitHubReleaseHosts, maxBytes: TrustedDownloadPolicy.AddonMaxBytes);

    /// <summary>OptiScaler stable, nightly and DLSS-NR builds: GitHub releases.</summary>
    public static readonly TrustedDownloadPolicy OptiScaler = new(
        "OptiScaler", "GitHub", GitHubReleaseHosts, maxBytes: PayloadArchiveMaxBytes);

    /// <summary>Luma Framework packages and add-ons: GitHub releases (the Filoppi namespace is checked separately).</summary>
    public static readonly TrustedDownloadPolicy Luma = new(
        "Luma Framework", "GitHub", GitHubReleaseHosts, maxBytes: PayloadArchiveMaxBytes);

    /// <summary>REFramework nightly releases: GitHub.</summary>
    public static readonly TrustedDownloadPolicy REFramework = new(
        "REFramework", "GitHub", GitHubReleaseHosts, maxBytes: PayloadArchiveMaxBytes);

    /// <summary>Ultimate ASI Loader: GitHub releases.</summary>
    public static readonly TrustedDownloadPolicy UltimateAsiLoader = new(
        "Ultimate ASI Loader", "GitHub", GitHubReleaseHosts, maxBytes: TrustedDownloadPolicy.AddonMaxBytes);

    /// <summary>Upstream RHI's own payload repository (RankFTW/rhi-repo releases): ShortFuse, DLSS Enabler, DLSS5 Feeder extras.</summary>
    public static readonly TrustedDownloadPolicy RhiRepository = new(
        "RHI repository release", "GitHub", GitHubReleaseHosts, maxBytes: PayloadArchiveMaxBytes);

    /// <summary>Universal MFG (RTX40MFG-Unlock): GitHub releases and their SHA256SUMS.txt.</summary>
    public static readonly TrustedDownloadPolicy UniversalMfg = new(
        "Universal MFG", "GitHub", GitHubReleaseHosts, maxBytes: TrustedDownloadPolicy.AddonMaxBytes);

    /// <summary>DLSSG 20/30 unlock: a file at a pinned commit, served by raw.githubusercontent.com.</summary>
    public static readonly TrustedDownloadPolicy Dlssg2030 = new(
        "DLSSG 20/30 unlock", "GitHub", ["raw.githubusercontent.com"], maxBytes: TrustedDownloadPolicy.AddonMaxBytes);

    /// <summary>dgVoodoo2: GitHub releases of dege-diosg/dgVoodoo2.</summary>
    public static readonly TrustedDownloadPolicy DgVoodoo = new(
        "dgVoodoo2", "GitHub", GitHubReleaseHosts, maxBytes: TrustedDownloadPolicy.AddonMaxBytes);

    /// <summary>
    /// DLSS5 Feeder: its GitHub release, the shader file at the release tag (raw.githubusercontent.com) and the
    /// branch archives it needs (codeload.github.com, where github.com archive links redirect).
    /// </summary>
    public static readonly TrustedDownloadPolicy Dlss5Feeder = new(
        "DLSS5 Feeder", "GitHub", [.. GitHubReleaseHosts, "raw.githubusercontent.com", "codeload.github.com"], maxBytes: PayloadArchiveMaxBytes);

    /// <summary>
    /// ReShade shader packs (built-in catalogue and the upstream manifest's <c>shaderPacks</c>): GitHub releases and
    /// branch archives (codeload.github.com) and GitLab archives, which upstream 2.8.5's manifest uses (UltraShade).
    /// </summary>
    public static readonly TrustedDownloadPolicy ShaderPacks = new(
        "ReShade shader pack", "GitHub or GitLab", [.. GitHubReleaseHosts, "codeload.github.com", "gitlab.com"], maxBytes: PayloadArchiveMaxBytes);

    /// <summary>
    /// Upstream RHI's Engine.ini templates (<c>engineIniFiles</c>): text files in RankFTW/rhi-repo served by
    /// raw.githubusercontent.com, capped like the template parser (256 KiB).
    /// </summary>
    public static readonly TrustedDownloadPolicy EngineIniTemplates = new(
        "Engine.ini template", "GitHub", ["raw.githubusercontent.com"], maxBytes: 256L * 1024);

    // w3/sync rows
    /// <summary>
    /// OptiScaler F5 DLSSNR (#157): janblade's fork, GitHub releases only (checked 2026-10-14: github.com redirects the
    /// asset to release-assets.githubusercontent.com).
    /// </summary>
    public static readonly TrustedDownloadPolicy OptiScalerF5 = new(
        "OptiScaler F5 DLSSNR", "GitHub", GitHubReleaseHosts, maxBytes: PayloadArchiveMaxBytes);
    // w3/journal rows
    // w3/scan rows
    // w3/parity rows
    // w3/vmsplit rows
    // w3/dllnames rows

    /// <summary>Every policy above plus the core ones, for the localisation contract test.</summary>
    public static IReadOnlyList<TrustedDownloadPolicy> All { get; } =
    [
        GitHubApi, ReShadeSetup, ReShadeNightly, GitHubAddonRelease, OptiScaler, Luma, REFramework, UltimateAsiLoader,
        RhiRepository, UniversalMfg, Dlssg2030, DgVoodoo, Dlss5Feeder, ShaderPacks, EngineIniTemplates,
        TrustedDownloadPolicy.GitHubReleases, TrustedDownloadPolicy.GitHub, TrustedDownloadPolicy.RenoDxAddons, TrustedDownloadPolicy.ReShadeAddons,
        // w3/sync rows
        OptiScalerF5,
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
    ];
}
