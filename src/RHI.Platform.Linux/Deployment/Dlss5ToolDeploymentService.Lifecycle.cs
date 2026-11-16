using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class Dlss5ToolDeploymentService
{
    public const string UpToDatePrefix = "DLSS5 Tool is up to date: ";
    public const string UpdateAvailablePrefix = "DLSS5 Tool update available: ";
    public const string CheckFailedPrefix = "Could not check DLSS5 Tool: ";
    public const string UpdatedPrefix = "DLSS5 Tool updated to ";
    public const string SwappedPrefix = "DLSS5 Tool add-on swapped in place to ";
    public const string SwapUnchangedNote = " The NR DLL and ReShade were not changed.";

    // ── Remove ────────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        // A changed add-on or NR DLL stops the removal before anything changes (left as it is).
        foreach (var file in record.Files.Where(f => !f.AllowUserChanges && File.Exists(f.Path)))
            if (!string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(ModifiedPrefix + file.Path);

        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(_journal, _records, "DLSS5 Tool removal: " + game.Name, game,
            ManagedComponentKind.Dlss5Tool, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var file in record.Files.Where(f => !f.ExistedBeforeInstall && File.Exists(f.Path)))
                await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
            _records.Commit([], [(game, ManagedComponentKind.Dlss5Tool)]);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "dlss5-tool").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName, removal: true);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        foreach (var directory in record.DirectoriesCreatedByRhi.OrderByDescending(d => d.Length))
            FileCleanup.TryDeleteEmptyTree(directory);
        return ComponentOperationResult.Ok(RemovedMessage);
    }

    // ── Update / version swap / build switch ──────────────────────────────────

    /// <summary>
    /// The update check, on the installed build's own releases: a Foveated install is compared with the Foveated releases
    /// only, so a new stock release never reads as an update for it (upstream: stock auto-update skips Foveated folders).
    /// </summary>
    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, string? pin, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, NotInstalledMessage);
        if (await ManagedUpdateFileGuard.HeldAsync(_journal, game, ManagedComponentKind.Dlss5Tool, record.Version).ConfigureAwait(false) is { } held) return held;
        if (ManagedUpdateFileGuard.Removed(record.Files.Select(file => file.Path), record.Version) is { } removed) return removed;
        var build = BuildOf(record);
        try
        {
            var (release, _) = await ResolveAsync(build, pin, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, release.Version, StringComparison.OrdinalIgnoreCase);
            var name = NameOf(build);
            return new ManagedUpdateCheck(true, update,
                (update ? UpdateAvailablePrefix + $"{name} {record.Version} → {release.Version}" : UpToDatePrefix + $"{name} {record.Version}") + ".",
                record.Version, release.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, CheckFailedPrefix + ex.Message, record.Version, CouldNotCheck: true);
        }
    }

    /// <summary>
    /// Replaces only the installed add-on, in place, with <paramref name="build"/>'s release for <paramref name="pin"/> (null
    /// = Latest): Update All and the version list (same build), and the Foveated switch (the other build, from Latest).
    /// The record says which build is in the folder afterwards. The NR DLL and ReShade are not touched; a file changed
    /// outside RHI refuses the swap and nothing changes.
    /// </summary>
    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, Dlss5ToolBuild build, string? pin, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        var addon = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(AddonFileName, StringComparison.OrdinalIgnoreCase));
        if (addon is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        Dlss5ToolRelease release;
        string? pinNote;
        (string Path, string Sha256, bool DigestVerified) cached;
        try
        {
            (release, pinNote) = await ResolveAsync(build, pin, cancellationToken).ConfigureAwait(false);
            cached = await EnsureCachedAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var buildChanges = BuildOf(record) != build;
        try
        {
            var changed = await ManagedFileSwap.ReplaceAsync(record, [(addon.Path, cached.Path)], updated =>
            {
                updated.Version = release.Version;
                updated.SourceUrl = release.AssetUrl;
                updated.Metadata[MetaBuild] = build == Dlss5ToolBuild.Foveated ? "foveated" : "stock";
                updated.Metadata[MetaTag] = release.Tag;
                updated.Metadata[MetaPin] = pin is null || pinNote is not null ? "" : release.Version;
                updated.Metadata[MetaAddonSha256] = updated.Files.First(f => f.Path == addon.Path).Sha256 ?? "";
                VerifiedDownload.Record(updated.Metadata, updated.Metadata[MetaAddonSha256], cached.DigestVerified);
                _records.Upsert(updated);
            }, cancellationToken, _journal, game).ConfigureAwait(false);
            var verb = pin is null && !buildChanges ? UpdatedPrefix : SwappedPrefix;
            return ComponentOperationResult.Ok(verb + NameOf(build) + " " + release.Version + "." + (pinNote is null ? "" : " " + pinNote + ".")
                                               + SwapUnchangedNote + (buildChanges && build == Dlss5ToolBuild.Foveated ? FoveatedNote : ""),
                changed == 0 ? [] : [addon.Path]);
        }
        catch (ManagedFileRestoreException ex)
        {
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return ComponentOperationResult.Fail(ModifiedPrefix + ex.Message);
        }
    }

    /// <summary>The DLSS5 Tool-owned <c>nvngx_dlssnr.dll</c> to a manifest version, in place (the NR DLL list).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => _dlss is null
            ? Task.FromResult(ComponentOperationResult.Fail(NoNrDllNote.Trim()))
            : NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.Dlss5Tool, DisplayName, version, cancellationToken, _journal);

    /// <summary>Whether the DX11 Bridge row installed this DLSS5 Tool ("DLSS5 Tool + DX11 Bridge").</summary>
    public bool InstalledWithBridge(DetectedGame game) => GetRecord(game)?.Metadata.GetValueOrDefault(MetaWithBridge) == "1";
}
