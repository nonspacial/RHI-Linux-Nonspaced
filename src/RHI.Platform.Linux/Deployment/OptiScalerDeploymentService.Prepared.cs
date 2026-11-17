using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// An OptiScaler install whose package (and DLSS NR DLL) is already downloaded and verified: a version switch fetches both
/// the new version and the exact installed one before it removes anything (w14 final review m1).
/// </summary>
internal sealed record PreparedOptiScalerInstall(
    OptiScalerVariant Variant,
    string? NightlyBuild,
    string? ReleaseTag,
    OptiScalerPackage Package,
    string? NrRuntime,
    string? NrRuntimeVersion);

public sealed partial class OptiScalerDeploymentService
{
    /// <summary>w14 final review m1: the installed version cannot be fetched again by its exact release (nothing follows).</summary>
    public const string ExactVersionUnknownMessage = "RHI cannot tell which release the installed OptiScaler came from, so it could not be put back if the switch failed.";

    /// <summary>The package an install of <paramref name="variant"/> takes: the pinned build, the pinned release, or the newest.</summary>
    private Task<OptiScalerPackage> FetchPackageAsync(OptiScalerVariant variant, string? nightlyBuild, string? releaseTag, CancellationToken cancellationToken)
        => nightlyBuild is not null ? _packages.GetNightlyBuildAsync(nightlyBuild, cancellationToken)
            : releaseTag is not null ? _packages.GetReleaseAsync(variant, releaseTag, cancellationToken)
            : _packages.GetLatestAsync(variant, cancellationToken);

    /// <summary>Where an install's files came from, for its line (nit: forks and custom builds are not "the official" release).</summary>
    private static string InstallSourceText(OptiScalerVariant variant)
        => variant.IsCustom ? "the custom build in RHI's data folder"
            : OptiScalerForks.Get(variant) is { } fork ? $"its GitHub release ({fork.Repository})"
            : "the official GitHub release";

    /// <summary>
    /// w14 final review m1: what an install of <paramref name="variant"/> would take — refused first when another component
    /// owns nvngx_dlssnr.dll (DLSS5 Feeder, ShortFuse), then the package and the NR DLL downloaded and verified. Changes
    /// nothing in the game. The failure is the line to show.
    /// </summary>
    internal async Task<(PreparedOptiScalerInstall? Prepared, string? Failure)> PrepareInstallAsync(
        DetectedGame game, OptiScalerVariant variant, string? nightlyBuild, string? releaseTag, CancellationToken cancellationToken)
    {
        if (OwnershipRefusal(game, variant) is { } refused) return (null, refused);
        try
        {
            var package = await FetchPackageAsync(variant, nightlyBuild, releaseTag, cancellationToken).ConfigureAwait(false);
            var (nr, nrVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
            return (new PreparedOptiScalerInstall(variant, nightlyBuild, releaseTag, package, nr, nrVersion), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, $"Could not obtain the official OptiScaler {VariantLabel(variant)} package: {ex.Message}");
        }
    }

    /// <summary>
    /// w14 final review m1: the exact version <paramref name="record"/> holds, fetched again so a failed switch puts that
    /// version back (not the newest): its pinned build or release, a custom build's copy, a Nightly by its date, or the
    /// release its download link names. The pin stays as it was (a Latest install stays Latest).
    /// </summary>
    internal async Task<(PreparedOptiScalerInstall? Prepared, string? Failure)> PrepareExactInstallAsync(
        LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var variant = GetInstalledVariant(record);
        var nightlyPin = GetPinnedNightlyBuild(record);
        var releasePin = GetPinnedRelease(record);
        try
        {
            var package = nightlyPin is not null ? await _packages.GetNightlyBuildAsync(nightlyPin, cancellationToken).ConfigureAwait(false)
                : releasePin is not null ? await _packages.GetReleaseAsync(variant, releasePin, cancellationToken).ConfigureAwait(false)
                : variant.IsCustom ? await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false)
                : variant == OptiScalerVariant.Nightly && OptiScalerPackageProvider.IsNightlyBuild(record.Version)
                    ? await _packages.GetNightlyBuildAsync(record.Version!, cancellationToken).ConfigureAwait(false)
                : ReleaseTagOf(record, variant) is { } tag ? await _packages.GetReleaseAsync(variant, tag, cancellationToken).ConfigureAwait(false)
                : throw new InvalidOperationException(ExactVersionUnknownMessage);
            var (nr, nrVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
            return (new PreparedOptiScalerInstall(variant, nightlyPin, releasePin, package, nr, nrVersion), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// The release tag in a record's download link (<c>https://github.com/&lt;repo&gt;/releases/download/&lt;tag&gt;/…</c>) of
    /// the version's own repository; null when the link is another one or the tag is not one RHI can ask for.
    /// </summary>
    internal static string? ReleaseTagOf(LinuxDeploymentRecord record, OptiScalerVariant variant)
    {
        if (OptiScalerPackageProvider.ReleaseRepository(variant) is not { } repository || record.SourceUrl is not { } url) return null;
        var prefix = $"https://github.com/{repository}/releases/download/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return null;
        var tag = Uri.UnescapeDataString(rest[..slash]);
        return OptiScalerPackageProvider.IsSafeReleaseTag(tag) ? tag : null;
    }

    /// <summary>The install's refusal when another component owns nvngx_dlssnr.dll and <paramref name="variant"/> needs it (#157).</summary>
    private string? OwnershipRefusal(DetectedGame game, OptiScalerVariant variant)
        => NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant)
           && NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.OptiScaler) is { } owner
            ? NeuralRenderingRuntimeOwnership.RefusalFor(owner)
            : null;
}
