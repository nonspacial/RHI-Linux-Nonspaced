using System.IO.Compression;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One iMMERSE Launchpad file: its path under the game's <c>reshade-shaders</c> folder ('/' separated) and the staged copy.</summary>
public sealed record LaunchpadFile(string Relative, string Source);

/// <summary>
/// #277: the iMMERSE Launchpad step HSF needs (upstream <c>UpliftService.LaunchpadFiles</c> / <c>KeepLaunchpadFirst</c>):
/// Launchpad's files, downloaded and checked, for a component to place in a game, and its technique kept at the top of the
/// game's ReShade preset (HSF and GITC Uplift read its motion in the same frame). Kept behind this interface so the nr
/// lane's reusable Launchpad service (GITC Uplift, #274) can take its place at the wave-14 merge.
/// </summary>
public interface ILaunchpadStep
{
    /// <summary>Launchpad's effect, its includes and its texture, staged (a download the first time).</summary>
    Task<IReadOnlyList<LaunchpadFile>> StageAsync(CancellationToken cancellationToken = default);

    /// <summary>The preset text with Launchpad moved to the top where it is listed lower down; only the order changes.</summary>
    (string Text, bool Changed) KeepFirst(string presetText);
}

/// <summary>
/// The minimal Launchpad step (#277, until the nr lane's service lands): the free iMMERSE repository's branch archive
/// (Marty McFly, martymcmodding/iMMERSE, through the shader-pack download policy) and only Launchpad's files from it, under
/// <c>Shaders/iMMERSE/</c> and <c>Textures/iMMERSE/</c> as upstream places them.
/// </summary>
public sealed class ImmerseLaunchpadStep(HttpClient http, string? cacheDir = null) : ILaunchpadStep
{
    public const string ArchiveUrl = "https://github.com/martymcmodding/iMMERSE/archive/refs/heads/main.zip";
    private const long FileMaxBytes = 16L * 1024 * 1024;

    private readonly string _cacheDir = cacheDir ?? Path.Combine(Paths.LinuxPaths.CacheHome, "rhi-linux", "launchpad");

    public async Task<IReadOnlyList<LaunchpadFile>> StageAsync(CancellationToken cancellationToken = default)
    {
        var archive = Path.Combine(_cacheDir, "iMMERSE-main.zip");
        var verified = await TrustedDownload.EnsureVerifiedAsync(http, ArchiveUrl, archive, DownloadSources.ShaderPacks,
            validate: ReShadeArchiveExtractor.RequireReadableZip, cancellationToken: cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(_cacheDir, "staged");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var files = new List<LaunchpadFile>();
        using (var zip = ZipFile.OpenRead(verified.Path))
        {
            foreach (var entry in zip.Entries)
            {
                if (RelativeFor(entry.FullName) is not { } relative) continue;
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, target, FileMaxBytes, cancellationToken).ConfigureAwait(false);
                files.Add(new LaunchpadFile(relative, target));
            }
        }
        if (!files.Any(file => file.Relative.EndsWith("/MartysMods_LAUNCHPAD.fx", StringComparison.Ordinal)))
            throw new InvalidDataException(MissingMessage);
        return files;
    }

    public (string Text, bool Changed) KeepFirst(string presetText) => HsfPackage.MoveTechniqueFirst(presetText, HsfPackage.LaunchpadToken);

    /// <summary>The iMMERSE archive holds no MartysMods_LAUNCHPAD.fx (PT-BR presentation pair).</summary>
    public const string MissingMessage = "The iMMERSE download holds no Launchpad effect (MartysMods_LAUNCHPAD.fx); nothing was changed.";

    /// <summary>Where an archive entry goes under reshade-shaders, or null: Launchpad's effect, the MartysMods includes, the blue-noise texture.</summary>
    internal static string? RelativeFor(string entryFullName)
    {
        var parts = entryFullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts.Any(part => part is ".." or ".")) return null; // <repo>-<branch>/<folder>/...
        var rest = parts[1..];
        if (rest is ["Shaders", var fx] && fx.Equals("MartysMods_LAUNCHPAD.fx", StringComparison.OrdinalIgnoreCase)) return "Shaders/iMMERSE/" + fx;
        if (rest is ["Shaders", "MartysMods", var include] && include.EndsWith(".fxh", StringComparison.OrdinalIgnoreCase))
            return "Shaders/iMMERSE/MartysMods/" + include;
        if (rest is ["Textures", var png] && png.Equals("iMMERSE_bluenoise_opt.png", StringComparison.OrdinalIgnoreCase)) return "Textures/iMMERSE/" + png;
        return null;
    }
}
