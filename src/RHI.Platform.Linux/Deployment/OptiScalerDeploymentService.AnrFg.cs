using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #272 ANR FG for the OptiScaler preset that asks for it (upstream 2.9.3 <c>EnsureAnrFgForPresetAsync</c> and
/// <c>AnrFgService.Install</c>): the provider goes into OptiScaler's custom folder
/// (<c>OptiScaler/custom/Apex Neural Rendering FG/…</c>), where builds with custom DLSSG providers load it. The build is
/// checked in the installed DLL, not by version name, so forks and custom builds based on a recent Nightly count. Linux:
/// every file is journaled and recorded on OptiScaler's record (removing OptiScaler removes them); the user's
/// <c>nr_fg.ini</c> is kept, and a file there RHI did not put refuses the install.
/// </summary>
public sealed partial class OptiScalerDeploymentService
{
    /// <summary>OptiScaler's log text for custom providers (upstream <c>SupportsCustomFgProviders</c>); Nightly since 29 Oct 2026.</summary>
    public const string CustomFgProvidersMarker = "Custom DLSSG";
    public const string AnrFgVersionMetadataKey = "OptiScaler.AnrFg";

    public const string AnrFgNotManagedMessage = "Install OptiScaler (Nightly) first.";
    /// <summary>The version label follows; then the hint.</summary>
    public const string AnrFgUnsupportedPrefix = "This OptiScaler version can't load ANR FG yet: ";
    public const string AnrFgUnsupportedHint = ". Set Version to Nightly, or a build based on a recent Nightly, then apply the preset again.";
    public const string AnrFgDownloadFailedPrefix = "Couldn't download ANR FG: ";
    /// <summary>The file follows.</summary>
    public const string AnrFgForeignFilePrefix = "A file RHI did not install is in ANR FG's folder, so nothing was changed: ";
    /// <summary>w14 final review m14: a file RHI put in ANR FG's folder was changed since; the file follows.</summary>
    public const string AnrFgChangedFilePrefix = "A file RHI put in ANR FG's folder was changed since, so nothing was changed: ";
    public const string AnrFgFailedPrefix = "ANR FG was not installed: ";
    /// <summary>The tag follows.</summary>
    public const string AnrFgInstalledPrefix = "ANR FG is in place: ";
    public const string AnrFgKeptIniNote = " Your nr_fg.ini was kept.";

    /// <summary>Where ANR FG comes from (null: offered by no build, e.g. tests).</summary>
    public IAnrFgProvider? AnrFg { get; set; }

    /// <summary>Whether the OptiScaler RHI installed on the game loads custom frame generation providers.</summary>
    public bool SupportsCustomFgProviders(DetectedGame game)
        => GetRecord(game) is { } record && File.Exists(ProxyPath(record))
           && BinaryStringSearch.ContainsAny(ProxyPath(record), CustomFgProvidersMarker);

    private static string ProxyPath(LinuxDeploymentRecord record) => Path.Combine(Path.GetFullPath(record.BinaryDirectory), GetProxyName(record));

    /// <summary>Makes sure ANR FG's newest release is in the game's OptiScaler custom folder (the line to show).</summary>
    public async Task<ComponentOperationResult> EnsureAnrFgAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(AnrFgNotManagedMessage);
        if (!SupportsCustomFgProviders(game))
            return ComponentOperationResult.Fail(AnrFgUnsupportedPrefix + VariantLabel(GetInstalledVariant(record)) + AnrFgUnsupportedHint);
        if (AnrFg is not { } source) return ComponentOperationResult.Fail(AnrFgDownloadFailedPrefix + "no source");
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.OptiScaler).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        AnrFgPackage package;
        try { package = await source.GetLatestAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(AnrFgDownloadFailedPrefix + ex.Message);
        }

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var os10 = Path.Combine(binaryDirectory, OptiScalerFolderName);
        var destination = Path.Combine(Directory.Exists(os10) ? os10 : binaryDirectory, CustomProvidersFolder, AnrFgProvider.ProviderName);
        var owned = record.Files.ToDictionary(file => Path.GetFullPath(file.Path), StringComparer.Ordinal);
        var plan = new List<(string Source, string Target, bool UserIni)>();
        var keptIni = false;
        foreach (var source2 in SafeFileEnumeration.Files(package.ProviderDirectory))
        {
            var relative = Path.GetRelativePath(package.ProviderDirectory, source2);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!PathIdentity.IsSameOrInside(target, destination)) continue;
            var userIni = relative.Replace('\\', '/').Equals(AnrFgProvider.UserIniRelativePath, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(target))
            {
                if (userIni) { keptIni = true; continue; } // the user's settings, whoever put the file there (upstream)
                if (!owned.TryGetValue(target, out var mine)) return ComponentOperationResult.Fail(AnrFgForeignFilePrefix + target);
                // w14 final review m14: RHI's own file the user changed since is never overwritten.
                if (mine.Sha256 is not { Length: > 0 } recorded
                    || !FileHash.HexEquals(await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false), recorded))
                    return ComponentOperationResult.Fail(AnrFgChangedFilePrefix + target);
            }
            plan.Add((source2, target, userIni));
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, $"ANR FG: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var createdDirs = new List<string>();
        try
        {
            foreach (var (source2, target, userIni) in plan)
            {
                if (owned.TryGetValue(target, out var file))
                {
                    file.Sha256 = await scope.PlaceCopyAsync(source2, target, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                EnsureTargetDirectory(binaryDirectory, target, createdDirs);
                var hash = await scope.PlaceCopyAsync(source2, target, cancellationToken, overwrite: false).ConfigureAwait(false);
                record.Files.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = hash, AllowUserChanges = userIni });
            }
            foreach (var dir in createdDirs)
                if (!record.DirectoriesCreatedByRhi.Contains(dir, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(dir);
            record.Metadata[AnrFgVersionMetadataKey] = package.Tag;
            package.Download.RecordIn(record.Metadata, "AnrFg");
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("ANR FG");
            if (rollback.RolledBack) CleanupDirectories(createdDirs);
            return ComponentOperationResult.Fail(AnrFgFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(AnrFgInstalledPrefix + package.Tag + "." + (keptIni ? AnrFgKeptIniNote : ""), destination);
    }
}
