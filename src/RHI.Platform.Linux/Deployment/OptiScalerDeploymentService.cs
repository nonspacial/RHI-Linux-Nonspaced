using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class OptiScalerDeploymentService
{
    internal const string VariantMetadataKey = "OptiScaler.Variant";
    internal const string DlssNrRuntimeVersionMetadataKey = "OptiScaler.DlssNrRuntimeVersion";
    /// <summary>The nightly build (date) an install is pinned to (#156); absent = Latest, which auto-updates.</summary>
    internal const string NightlyBuildMetadataKey = "OptiScaler.NightlyBuild";

    public const string NightlyPinnedCurrentPrefix = "OptiScaler Nightly is pinned and current: ";
    public const string NightlyPinnedNotUpdatedPrefix = "OptiScaler Nightly is pinned, so it was not updated: ";
    public const string NightlyPinWrongChannelMessage = "A nightly build can be pinned only for the OptiScaler Nightly channel.";
    public const string NightlyBuildInvalidPrefix = "Not an OptiScaler nightly build date: ";
    /// <summary>Appended to the OptiScaler row note while DLSS5 Feeder owns nvngx_dlssnr.dll (#157).</summary>
    public const string FeederBlocksNrVariantsNote = " DLSS NR and F5 DLSSNR are blocked while DLSS5 Feeder is managed because both would own nvngx_dlssnr.dll; Stable/Nightly remain available.";

    private static readonly string[] SetupJunkNames =
    [
        "setup_windows.bat", "setup_linux.sh", "remove_optiscaler.sh", "!! EXTRACT ALL FILES TO GAME FOLDER !!"
    ];

    private readonly IFileDeploymentService _files;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly IOptiScalerPackageProvider _packages;
    private readonly DlssManagerService? _dlss;
    private readonly StandaloneDlssEnablerDeploymentService? _dlssEnabler;
    private readonly DeploymentJournal? _journal;

    public OptiScalerDeploymentService(
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        IOptiScalerPackageProvider packages,
        DlssManagerService? dlss = null,
        StandaloneDlssEnablerDeploymentService? dlssEnabler = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _files = files;
        _records = records;
        _overrides = overrides;
        _packages = packages;
        _dlss = dlss;
        _dlssEnabler = dlssEnabler;
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "OptiScaler is not managed by RHI Linux.");
        // A pinned nightly stays on its build (upstream 2.8.6): current, never an update and never "could not check".
        if (GetPinnedNightlyBuild(record) is { } pinned)
            return new ManagedUpdateCheck(true, false, NightlyPinnedCurrentPrefix + pinned, record.Version, pinned);
        try
        {
            var variant = GetInstalledVariant(record);
            var latest = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.SourceUrl, StringComparison.OrdinalIgnoreCase);
            if (NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant) && _dlss is not null)
            {
                await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
                var latestNr = _dlss.Versions(DlssComponentKind.NeuralRendering).FirstOrDefault();
                var installedNr = record.Metadata.GetValueOrDefault(DlssNrRuntimeVersionMetadataKey);
                if (!string.IsNullOrWhiteSpace(latestNr) && !string.Equals(latestNr, installedNr, StringComparison.OrdinalIgnoreCase))
                    update = true;
            }
            var label = VariantLabel(variant);
            return new ManagedUpdateCheck(true, update,
                update ? $"OptiScaler {label} update available: {record.Version ?? "unknown"} → {latest.Version}." : $"OptiScaler {label} {record.Version ?? latest.Version} is current.",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"OptiScaler update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail("OptiScaler is not managed by RHI Linux for this game.");
        if (GetPinnedNightlyBuild(record) is { } pinned)
            return ComponentOperationResult.Ok(NightlyPinnedNotUpdatedPrefix + pinned);

        try
        {
            await RestoreLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ComponentOperationResult.Fail($"Could not give back an OptiScaler file an older DLSS swap replaced: {ex.Message}");
        }

        foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
        {
            if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"OptiScaler managed file is missing or has no ownership hash: {file.Path}");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed OptiScaler file was modified outside RHI and will not be updated: {file.Path}");
        }

        var variant = GetInstalledVariant(record);
        OptiScalerPackage package;
        string? nrRuntime = null;
        string? nrRuntimeVersion = null;
        try
        {
            package = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
            (nrRuntime, nrRuntimeVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"Could not obtain latest OptiScaler {VariantLabel(variant)} package: {ex.Message}");
        }

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var proxyTarget = Path.Combine(binaryDirectory, "dxgi.dll");
        var requested = new List<(string Source, string Target)>();
        try
        {
            BuildDeploymentPlan(package.RootDirectory, binaryDirectory, proxyTarget, requested);
            if (nrRuntime is not null) requested.Add((nrRuntime, Path.Combine(binaryDirectory, "nvngx_dlssnr.dll")));
        }
        catch (Exception ex) { return ComponentOperationResult.Fail($"OptiScaler update package layout is invalid: {ex.Message}"); }

        var byPath = record.Files.ToDictionary(file => Path.GetFullPath(file.Path), StringComparer.Ordinal);
        var backupRoot = Path.Combine(Path.GetTempPath(), $"rhi-optiscaler-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var newlyCreated = new List<string>();
        var createdDirs = new List<string>();
        // Downloaded and checked: from the first replaced file on, the update runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var (source, targetRaw) in requested)
            {
                var target = Path.GetFullPath(targetRaw);
                if (byPath.TryGetValue(target, out var owned))
                {
                    if (owned.AllowUserChanges)
                        continue; // OptiScaler.ini is user state and never overwritten by Update All.
                    var backup = Path.Combine(backupRoot, $"{backups.Count:D4}-{Path.GetFileName(target)}");
                    File.Copy(target, backup, overwrite: false);
                    backups[target] = backup;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    owned.Sha256 = await AtomicFile.CopyAsync(source, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (File.Exists(target))
                    throw new InvalidOperationException($"New OptiScaler package file collides with an external/untracked file: {target}");
                EnsureTargetDirectory(binaryDirectory, target, createdDirs);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var copiedHash = await AtomicFile.CopyAsync(source, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                newlyCreated.Add(target);
                var added = new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = false,
                    Sha256 = copiedHash,
                    AllowUserChanges = Path.GetFileName(target).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                };
                record.Files.Add(added);
                byPath[target] = added;
            }

            record.SourceUrl = package.SourceUrl;
            record.Version = package.Version;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[VariantMetadataKey] = variant.ToString();
            if (!string.IsNullOrWhiteSpace(nrRuntimeVersion))
                record.Metadata[DlssNrRuntimeVersionMetadataKey] = nrRuntimeVersion;
            else
                record.Metadata.Remove(DlssNrRuntimeVersionMetadataKey);
            if (package.Download is { } updateDownload) updateDownload.RecordIn(record.Metadata);
            else VerifiedDownload.Forget(record.Metadata);
            foreach (var dir in createdDirs)
                if (!record.DirectoriesCreatedByRhi.Contains(dir, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(dir);
            _records.Upsert(record);
            return ComponentOperationResult.Ok($"OptiScaler {VariantLabel(variant)} updated to {package.Version}; OptiScaler.ini, ReShade coexistence and Wine override ownership were preserved.",
                requested.Select(item => item.Target).Where(path => !Path.GetFileName(path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        catch (Exception ex)
        {
            foreach (var pair in backups)
            {
                try { if (File.Exists(pair.Value)) File.Copy(pair.Value, pair.Key, overwrite: true); } catch { }
            }
            foreach (var path in newlyCreated.AsEnumerable().Reverse())
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            CleanupDirectories(createdDirs);
            return ComponentOperationResult.Fail($"OptiScaler update rolled back: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(backupRoot, recursive: true); } catch { }
        }
    }

    public Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallAsync(game, OptiScalerVariant.Stable, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, variant, nightlyBuild: null, cancellationToken);

    /// <summary>
    /// Installs OptiScaler Nightly pinned to one build (#156; Control UE frame generation pins <c>20261004</c>, #136). The
    /// build is recorded, so the game stays on it: Update All reports it current and never replaces it.
    /// </summary>
    public Task<ComponentOperationResult> InstallPinnedNightlyAsync(
        DetectedGame game,
        string build,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, OptiScalerVariant.Nightly, build, cancellationToken);

    /// <param name="nightlyBuild">Nightly only: the build date to pin (<c>20261004</c>); null = Latest, which auto-updates.</param>
    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        string? nightlyBuild,
        CancellationToken cancellationToken = default)
    {
        if (nightlyBuild is not null)
        {
            if (variant != OptiScalerVariant.Nightly) return ComponentOperationResult.Fail(NightlyPinWrongChannelMessage);
            if (!OptiScalerPackageProvider.IsNightlyBuild(nightlyBuild)) return ComponentOperationResult.Fail(NightlyBuildInvalidPrefix + nightlyBuild);
        }
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("OptiScaler is a Windows DLL middleware and is only deployed to Wine/Proton games.");
        if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.DirectX12))
            return ComponentOperationResult.Fail("O suporte de proxy do OptiScaler é habilitado automaticamente apenas para jogos DirectX 11/12 detectados. Vulkan exige uma escolha explícita de loader/proxy antes de poder ser habilitado com segurança.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (GameArchitecture.Of(game) != PeArchitecture.X64)
            return ComponentOperationResult.Fail("Current OptiScaler packages are deployed by RHI Linux only to 64-bit Windows games.");
        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            return ComponentOperationResult.Fail("Start the Windows game once so its Wine/Proton prefix is initialized before installing OptiScaler.");
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing OptiScaler. The ShortFuse transaction owns the ReShade/UAL/DLSS/Streamline chain.");
        // nvngx_dlssnr.dll has one owner (NeuralRenderingRuntimeOwnership, #157): DLSS NR / F5 refuse while another owner is recorded.
        if (NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant)
            && NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.OptiScaler) is { } nrOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("OptiScaler is already managed by RHI Linux for this game. Remove it before reinstalling/updating.");
        if (_overrides.EditRefusal(prefix) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        OptiScalerPackage package;
        string? nrRuntime = null;
        string? nrRuntimeVersion = null;
        try
        {
            package = nightlyBuild is null
                ? await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false)
                : await _packages.GetNightlyBuildAsync(nightlyBuild, cancellationToken).ConfigureAwait(false);
            (nrRuntime, nrRuntimeVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { return ComponentOperationResult.Fail($"Could not obtain the official OptiScaler {VariantLabel(variant)} package: {ex.Message}"); }

        var binaryDirectory = Path.GetFullPath(game.BinaryDirectory);
        var proxyTarget = Path.Combine(binaryDirectory, "dxgi.dll");
        ReShadeMigration? migration = null;
        WineOverrideRecord? overrideRecord = null;
        var overrideCreatedHere = false;
        var deployed = false;
        var createdDirectories = new List<string>();
        var requested = new List<(string Source, string Target)>();
        var existedBefore = new Dictionary<string, bool>(StringComparer.Ordinal);

        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (operation, journalRefusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(
            _journal, $"OptiScaler install: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var journaled = operation;

        // Downloaded: from the first change on, the install runs to completion or full rollback (Cancel is refused).
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            migration = await MigrateManagedReShadeIfNeededAsync(game, proxyTarget, cancellationToken).ConfigureAwait(false);

            if (File.Exists(proxyTarget))
            {
                if (BinarySignatureInspector.LooksLikeOptiScaler(proxyTarget))
                    throw new InvalidOperationException("OptiScaler is already present as dxgi.dll but is not recorded as RHI-managed. The external installation is protected.");
                if (BinarySignatureInspector.LooksLikeReShade(proxyTarget))
                    throw new InvalidOperationException("ReShade occupies dxgi.dll but is not a migratable RHI-managed install. External ReShade is protected.");
                throw new InvalidOperationException("Foreign DLL protection: dxgi.dll is occupied by an unknown DLL. Nothing was overwritten.");
            }

            overrideRecord = migration?.TransferredOverride;
            if (overrideRecord is null)
            {
                overrideRecord = await _overrides.EnsureNativeBuiltinAsync(operation, prefix, "dxgi.dll", Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                overrideCreatedHere = true;
            }

            BuildDeploymentPlan(package.RootDirectory, binaryDirectory, proxyTarget, requested);
            if (nrRuntime is not null) requested.Add((nrRuntime, Path.Combine(binaryDirectory, "nvngx_dlssnr.dll")));
            foreach (var (_, target) in requested)
            {
                existedBefore[target] = File.Exists(target);
                if (File.Exists(target) && File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
                    throw new InvalidOperationException($"Backup collision protection: {target}.original already exists. RHI will not guess which previous tool owns that backup.");
                EnsureTargetDirectory(binaryDirectory, target, createdDirectories);
            }

            var batch = await _files.ReplaceBatchAsync(requested, cancellationToken).ConfigureAwait(false);
            if (!batch.Success) throw new InvalidOperationException(batch.Error ?? "OptiScaler file deployment failed.");
            deployed = true;

            if (migration is not null)
                OptiScalerIniEditor.SetLoadReShade(binaryDirectory, true);

            var record = new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.OptiScaler,
                BinaryDirectory = binaryDirectory,
                SourceUrl = package.SourceUrl,
                Version = package.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = overrideRecord,
                DirectoriesCreatedByRhi = createdDirectories.Distinct(StringComparer.Ordinal).ToList(),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [VariantMetadataKey] = variant.ToString(),
                    [DlssNrRuntimeVersionMetadataKey] = nrRuntimeVersion ?? ""
                }
            };
            if (nightlyBuild is not null) record.Metadata[NightlyBuildMetadataKey] = nightlyBuild;
            package.Download?.RecordIn(record.Metadata);
            if (operation is not null) record.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey] = operation.Id;

            foreach (var (_, target) in requested)
            {
                record.Files.Add(new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = existedBefore[target],
                    Sha256 = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false),
                    AllowUserChanges = Path.GetFileName(target).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                });
            }

            _records.Upsert(record);
            // The record carries the operation id: it is the commit point even if this cleanup fails.
            if (operation is not null) await operation.CommitAsync().ConfigureAwait(false);
            return ComponentOperationResult.Ok(
                $"OptiScaler {VariantLabel(variant)} {package.Version} installed from the official GitHub release as dxgi.dll" +
                (migration is null ? "." : "; RHI-managed ReShade was moved to ReShade64.dll and OptiScaler LoadReshade was enabled."),
                proxyTarget);
        }
        catch (Exception ex)
        {
            // Compensation must run even when the failure *is* a cancellation.
            if (deployed)
                await RollbackDeploymentAsync(requested, existedBefore, createdDirectories, CancellationToken.None).ConfigureAwait(false);
            if (overrideCreatedHere && overrideRecord is not null)
                _overrides.TryRestore(prefix, overrideRecord);
            if (migration is not null)
                await RollbackReShadeMigrationAsync(migration, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail($"OptiScaler install failed: {ex.Message}");
        }
    }

    internal static OptiScalerVariant GetInstalledVariant(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(VariantMetadataKey, out var raw) && Enum.TryParse<OptiScalerVariant>(raw, true, out var parsed)
            ? parsed
            : OptiScalerVariant.Stable;

    /// <summary>The nightly build an installed Nightly is pinned to, or null (Latest, another channel, no valid pin).</summary>
    public static string? GetPinnedNightlyBuild(LinuxDeploymentRecord record)
        => GetInstalledVariant(record) == OptiScalerVariant.Nightly
           && record.Metadata.TryGetValue(NightlyBuildMetadataKey, out var build)
           && OptiScalerPackageProvider.IsNightlyBuild(build)
            ? build
            : null;

    /// <summary>The newest nightly builds a game can be pinned to (dates, newest first).</summary>
    public Task<IReadOnlyList<string>> ListNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => _packages.ListNightlyBuildsAsync(cancellationToken);

    internal static string VariantLabel(OptiScalerVariant variant) => variant switch
    {
        OptiScalerVariant.Stable => "Stable",
        OptiScalerVariant.Nightly => "Nightly",
        OptiScalerVariant.DlssNr => "DLSS NR",
        OptiScalerVariant.F5 => "F5 DLSSNR",
        _ => variant.ToString()
    };

    private async Task<(string? Path, string? Version)> PrepareDlssNrRuntimeAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken)
    {
        if (!NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant)) return (null, null);
        if (_dlss is null)
            throw new InvalidOperationException($"OptiScaler {VariantLabel(variant)} requires the DLSS runtime service so nvngx_dlssnr.dll can be staged safely.");
        var path = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.NeuralRendering, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"The current DLSS manifest did not provide nvngx_dlssnr.dll for the {VariantLabel(variant)} variant.");
        var version = _dlss.Manifest?.Dlssnr?.FirstOrDefault()?.Version;
        return (path, version);
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.OptiScaler);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler was detected, but it is not managed by RHI Linux. Removal is locked to protect the external installation.");

        var reShadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        var reShade64 = reShadeRecord?.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        var dxgi = Path.Combine(record.BinaryDirectory, "dxgi.dll");

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var changed = false;
        ComponentOperationResult FailKeepingProgress(string message)
        {
            // Files already removed/restored must leave the record, or a restored .original blocks every retry.
            if (changed)
            {
                PruneFgStackOwnership(record);
                _records.Upsert(record);
            }
            return ComponentOperationResult.Fail(message);
        }

        try
        {
            await RestoreLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);

            // Preflight every destructive action before changing any file. Kept/taken-over files are
            // hash-checked too: restoring their .original would overwrite a file the user replaced.
            foreach (var file in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall && !File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                    return ComponentOperationResult.Fail($"Cannot safely remove OptiScaler: preserved original is missing for {file.Path}.");
                if (!File.Exists(file.Path) || file.AllowUserChanges) continue;
                var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so OptiScaler was not removed.");
            }

            if (reShade64 is not null && File.Exists(reShade64.Path))
            {
                var current = await FileHash.Sha256HexAsync(reShade64.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, reShade64.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed ReShade64.dll was modified outside RHI. OptiScaler removal is blocked so the ReShade chain is not broken: {reShade64.Path}");

                // ReShade64 can only be moved back to dxgi.dll after OptiScaler is removed when
                // dxgi.dll did not contain a pre-existing game/third-party DLL before OptiScaler.
                // If OptiScaler preserved such a DLL as dxgi.dll.original, restoring it and moving
                // ReShade to the same path would be mutually exclusive. In that topology the safe
                // order is ReShade first, then OptiScaler.
                var proxyRecord = record.Files.FirstOrDefault(file =>
                    Path.GetFullPath(file.Path).Equals(Path.GetFullPath(dxgi), StringComparison.Ordinal));
                // No proxy record with dxgi.dll gone: an earlier, partly failed removal already deleted it.
                if (proxyRecord is null && File.Exists(dxgi))
                    return ComponentOperationResult.Fail("Cannot safely remove OptiScaler while ReShade depends on it: the managed dxgi.dll record is missing.");
                if (proxyRecord is { ExistedBeforeInstall: true })
                    return ComponentOperationResult.Fail("Remove RHI-managed ReShade first. OptiScaler preserved a pre-existing dxgi.dll, so ReShade64.dll cannot be moved back to dxgi.dll without overwriting that original.");
            }

            // Cancel is honoured only up to here: the teardown never stops half-way (a restored original no longer
            // matches RHI's hash, so a retry would be refused).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            foreach (var file in record.Files.OrderByDescending(file => file.Path.Length).ToArray())
            {
                if (file.ExistedBeforeInstall)
                {
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success) return FailKeepingProgress(restored.Error ?? $"Could not restore {file.Path}");
                }
                else
                {
                    try { if (File.Exists(file.Path)) File.Delete(file.Path); }
                    catch (Exception ex) { return FailKeepingProgress($"Could not remove {file.Path}: {ex.Message}"); }
                }
                record.Files.Remove(file);
                changed = true;
            }

            var keptReShade = false;
            WineOverrideRestoreResult? overrideRestore = null;
            if (reShadeRecord is not null && reShade64 is not null && File.Exists(reShade64.Path))
            {
                if (File.Exists(dxgi))
                    return FailKeepingProgress("OptiScaler files were removed, but dxgi.dll is unexpectedly occupied; RHI did not move ReShade back over it.");
                // From the move on, nothing may be cancelled before the ReShade record follows the file. The
                // content is the ReShade64.dll the preflight just hash-checked, so its recorded hash stays valid.
                File.Move(reShade64.Path, dxgi);
                reShade64.Path = dxgi;
                reShadeRecord.WineOverride = record.WineOverride;
                _records.Upsert(reShadeRecord);
                keptReShade = true;
            }
            else if (game.PrefixPath is { } prefix && Directory.Exists(prefix))
            {
                overrideRestore = _overrides.Restore(prefix, record.WineOverride);
            }

            CleanupDirectories(record.DirectoriesCreatedByRhi);
            _records.Remove(game, ManagedComponentKind.OptiScaler);
            return ComponentOperationResult.Ok((keptReShade
                ? "RHI-managed OptiScaler removed; ReShade64.dll was moved back to dxgi.dll and retained its Wine override."
                : "RHI-managed OptiScaler removed; preserved game files and the Wine DLL override were restored.")
                + WineDllOverrideService.KeptValueNotes(overrideRestore));
        }
        catch (Exception ex)
        {
            return FailKeepingProgress($"OptiScaler removal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Before #135 the direct DLSS flows (Quick Apply / Batch Deploy with a Streamline default, the panel's Streamline
    /// swap) also swapped RHI's own <c>OptiScaler/Streamline/sl.*</c>: the owned file was kept next to the new one as
    /// <c>&lt;name&gt;.original</c> (the DLSS swap backup) and no longer matched the record. Those flows no longer reach
    /// OptiScaler's files, so that backup would be stranded and every disable/redeploy/removal refused. When a managed
    /// file RHI created (not a kept original) mismatches its recorded hash and its <c>.original</c> carries exactly that
    /// hash, the backup is RHI's own file: it is moved back, which only returns ownership and overwrites nothing foreign.
    /// Returns the files given back.
    /// </summary>
    private async Task<IReadOnlyList<string>> RestoreLegacyDlssSwapsAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var restored = new List<string>();
        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ExistedBeforeInstall || file.AllowUserChanges || string.IsNullOrWhiteSpace(file.Sha256)) continue;
            var backup = file.Path + TransactionalFileDeploymentService.BackupExtension;
            if (!File.Exists(file.Path) || !File.Exists(backup)) continue;
            if (string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(await FileHash.Sha256HexAsync(backup, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            restored.Add(file.Path);
        }
        if (restored.Count == 0) return restored;
        // Checked: the give-back runs for every file found, whatever Cancel says.
        _ = OperationCancellation.BeginWrites(cancellationToken);
        foreach (var path in restored)
            File.Move(path + TransactionalFileDeploymentService.BackupExtension, path, overwrite: true);
        return restored;
    }

    private async Task<ReShadeMigration?> MigrateManagedReShadeIfNeededAsync(
        DetectedGame game,
        string proxyTarget,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(proxyTarget)) return null;
        if (!BinarySignatureInspector.LooksLikeReShade(proxyTarget)) return null;

        var record = _records.Find(game, ManagedComponentKind.ReShade)
            ?? throw new InvalidOperationException("External ReShade occupies dxgi.dll. RHI will not rename or take ownership of it automatically.");
        var file = record.Files.FirstOrDefault(item => Path.GetFullPath(item.Path).Equals(Path.GetFullPath(proxyTarget), StringComparison.Ordinal))
            ?? throw new InvalidOperationException("ReShade is RHI-managed, but its deployment record does not own the current dxgi.dll.");
        var reShade64 = Path.Combine(Path.GetDirectoryName(proxyTarget)!, "ReShade64.dll");
        if (File.Exists(reShade64))
            throw new InvalidOperationException("ReShade64.dll already exists, so RHI cannot create the OptiScaler/ReShade coexistence chain safely.");

        var current = await FileHash.Sha256HexAsync(proxyTarget, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed ReShade dxgi.dll was modified outside RHI and will not be migrated automatically.");

        var oldPath = file.Path;
        var oldHash = file.Sha256;
        var oldOverride = record.WineOverride;
        File.Move(proxyTarget, reShade64);
        file.Path = reShade64;
        file.Sha256 = await FileHash.Sha256HexAsync(reShade64, cancellationToken).ConfigureAwait(false);
        record.WineOverride = null;
        _records.Upsert(record);
        return new ReShadeMigration(record, file, oldPath, oldHash, reShade64, oldOverride);
    }

    private async Task RollbackReShadeMigrationAsync(ReShadeMigration migration, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(migration.NewPath) && !File.Exists(migration.OldPath))
                File.Move(migration.NewPath, migration.OldPath);
            migration.File.Path = migration.OldPath;
            migration.File.Sha256 = File.Exists(migration.OldPath)
                ? await FileHash.Sha256HexAsync(migration.OldPath, cancellationToken).ConfigureAwait(false)
                : migration.OldHash;
            migration.Record.WineOverride = migration.TransferredOverride;
            _records.Upsert(migration.Record);
        }
        catch { }
    }

    private async Task RollbackDeploymentAsync(
        IReadOnlyList<(string Source, string Target)> requested,
        IReadOnlyDictionary<string, bool> existedBefore,
        IReadOnlyList<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        foreach (var (_, target) in requested.Reverse())
        {
            try
            {
                var existed = existedBefore.TryGetValue(target, out var value) && value;
                if (existed && File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
                {
                    await _files.RestoreAsync(target, cancellationToken).ConfigureAwait(false);
                }
                else if (!existed && File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch { }
        }
        CleanupDirectories(createdDirectories);
    }

    internal static void BuildDeploymentPlan(
        string packageRoot,
        string binaryDirectory,
        string proxyTarget,
        List<(string Source, string Target)> requested)
    {
        var optiDll = Path.Combine(packageRoot, "OptiScaler.dll");
        var optiIni = Path.Combine(packageRoot, "OptiScaler.ini");
        if (!File.Exists(optiDll) || !File.Exists(optiIni))
            throw new InvalidOperationException("OptiScaler package root is incomplete.");
        requested.Add((optiDll, proxyTarget));

        foreach (var source in SafeFileEnumeration.Files(packageRoot))
        {
            var relative = Path.GetRelativePath(packageRoot, source);
            if (relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (SetupJunkNames.Any(name => Path.GetFileName(relative).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment.Equals("Licenses", StringComparison.OrdinalIgnoreCase))) continue;
            if (IsNonGamePackageEntry(segments)) continue;

            var target = Path.GetFullPath(Path.Combine(binaryDirectory, relative));
            var rootWithSep = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new InvalidOperationException($"OptiScaler archive path escapes the game binary directory: {relative}");
            requested.Add((source, target));
        }
    }

    /// <summary>
    /// Package entries upstream never copies into the game (2.8.7 install step 5, needed since the F5 DLSSNR package,
    /// #157): top-level scripts, notes and tools (<c>.bat .sh .ps1 .txt .md .exe .pdb</c>, <c>LICENSE</c>, <c>!!…</c>) and
    /// the top-level <c>docs</c> and <c>redist</c> folders. Folders OptiScaler loads (<c>OptiScaler/</c>,
    /// <c>D3D12_Optiscaler/</c>) and every DLL/INI are still deployed.
    /// </summary>
    internal static bool IsNonGamePackageEntry(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return true;
        if (segments.Count > 1)
            return segments[0].Equals("docs", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("redist", StringComparison.OrdinalIgnoreCase);
        var name = segments[0];
        return name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("!!", StringComparison.Ordinal)
               || new[] { ".bat", ".sh", ".ps1", ".txt", ".md", ".exe", ".pdb" }.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureTargetDirectory(string binaryDirectory, string target, List<string> createdDirectories)
    {
        var baseDir = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(target);
        if (dir is null || dir.Equals(baseDir, StringComparison.Ordinal)) return;
        var missing = new Stack<string>();
        var current = dir;
        while (!current.Equals(baseDir, StringComparison.Ordinal) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? baseDir;
        }
        while (missing.Count > 0)
        {
            var create = missing.Pop();
            Directory.CreateDirectory(create);
            createdDirectories.Add(create);
        }
    }

    private static void CleanupDirectories(IEnumerable<string> directories)
    {
        foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch { }
        }
    }

    private sealed record ReShadeMigration(
        LinuxDeploymentRecord Record,
        LinuxDeployedFile File,
        string OldPath,
        string? OldHash,
        string NewPath,
        WineOverrideRecord? TransferredOverride);
}
