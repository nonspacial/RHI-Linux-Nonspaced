using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class OptiScalerDeploymentService
{
    internal const string VariantMetadataKey = "OptiScaler.Variant";
    internal const string DlssNrRuntimeVersionMetadataKey = "OptiScaler.DlssNrRuntimeVersion";

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

    public OptiScalerDeploymentService(
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        IOptiScalerPackageProvider packages,
        DlssManagerService? dlss = null,
        StandaloneDlssEnablerDeploymentService? dlssEnabler = null)
    {
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
        try
        {
            var variant = GetInstalledVariant(record);
            var latest = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.SourceUrl, StringComparison.OrdinalIgnoreCase);
            if (variant == OptiScalerVariant.DlssNr && _dlss is not null)
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
            return new ManagedUpdateCheck(true, false, $"OptiScaler update check failed: {ex.Message}", record.Version);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail("OptiScaler is not managed by RHI Linux for this game.");

        foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
        {
            if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"OptiScaler managed file is missing or has no ownership hash: {file.Path}");
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
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
        try
        {
            foreach (var (source, targetRaw) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(targetRaw);
                if (byPath.TryGetValue(target, out var owned))
                {
                    if (owned.AllowUserChanges)
                        continue; // OptiScaler.ini is user state and never overwritten by Update All.
                    var backup = Path.Combine(backupRoot, $"{backups.Count:D4}-{Path.GetFileName(target)}");
                    File.Copy(target, backup, overwrite: false);
                    backups[target] = backup;
                    await ReplaceAtomicAsync(source, target, cancellationToken).ConfigureAwait(false);
                    owned.Sha256 = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (File.Exists(target))
                    throw new InvalidOperationException($"New OptiScaler package file collides with an external/untracked file: {target}");
                EnsureTargetDirectory(binaryDirectory, target, createdDirs);
                await ReplaceAtomicAsync(source, target, cancellationToken).ConfigureAwait(false);
                newlyCreated.Add(target);
                var added = new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = false,
                    Sha256 = await Sha256Async(target, cancellationToken).ConfigureAwait(false),
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

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("OptiScaler is a Windows DLL middleware and is only deployed to Wine/Proton games.");
        if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.DirectX12))
            return ComponentOperationResult.Fail("O suporte de proxy do OptiScaler é habilitado automaticamente apenas para jogos DirectX 11/12 detectados. Vulkan exige uma escolha explícita de loader/proxy antes de poder ser habilitado com segurança.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
            return ComponentOperationResult.Fail("Current OptiScaler packages are deployed by RHI Linux only to 64-bit Windows games.");
        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            return ComponentOperationResult.Fail("Start the Windows game once so its Wine/Proton prefix is initialized before installing OptiScaler.");
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing OptiScaler. The ShortFuse transaction owns the ReShade/UAL/DLSS/Streamline chain.");
        if (variant == OptiScalerVariant.DlssNr && _records.Find(game, ManagedComponentKind.Dlss5Feeder) is not null)
            return ComponentOperationResult.Fail("Remove DLSS5 Feeder before installing the OptiScaler DLSS NR variant. Both components would own nvngx_dlssnr.dll and RHI Linux does not yet have a shared-file ownership ledger for that runtime.");
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("OptiScaler is already managed by RHI Linux for this game. Remove it before reinstalling/updating.");

        OptiScalerPackage package;
        string? nrRuntime = null;
        string? nrRuntimeVersion = null;
        try
        {
            package = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
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
                overrideRecord = _overrides.EnsureNativeBuiltin(prefix, "dxgi.dll", Path.GetFileName(game.ExecutablePath));
                overrideCreatedHere = true;
            }

            BuildDeploymentPlan(package.RootDirectory, binaryDirectory, proxyTarget, requested);
            if (nrRuntime is not null) requested.Add((nrRuntime, Path.Combine(binaryDirectory, "nvngx_dlssnr.dll")));
            foreach (var (_, target) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

            foreach (var (_, target) in requested)
            {
                record.Files.Add(new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = existedBefore[target],
                    Sha256 = await Sha256Async(target, cancellationToken).ConfigureAwait(false),
                    AllowUserChanges = Path.GetFileName(target).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                });
            }

            _records.Upsert(record);
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
            {
                try { _overrides.Restore(prefix, overrideRecord); } catch { }
            }
            if (migration is not null)
                await RollbackReShadeMigrationAsync(migration, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail($"OptiScaler install failed: {ex.Message}");
        }
    }

    internal static OptiScalerVariant GetInstalledVariant(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(VariantMetadataKey, out var raw) && Enum.TryParse<OptiScalerVariant>(raw, true, out var parsed)
            ? parsed
            : OptiScalerVariant.Stable;

    internal static string VariantLabel(OptiScalerVariant variant) => variant switch
    {
        OptiScalerVariant.Stable => "Stable",
        OptiScalerVariant.Nightly => "Nightly",
        OptiScalerVariant.DlssNr => "DLSS NR",
        _ => variant.ToString()
    };

    private async Task<(string? Path, string? Version)> PrepareDlssNrRuntimeAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken)
    {
        if (variant != OptiScalerVariant.DlssNr) return (null, null);
        if (_dlss is null)
            throw new InvalidOperationException("OptiScaler DLSS NR requires the DLSS runtime service so nvngx_dlssnr.dll can be staged safely.");
        var path = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.NeuralRendering, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The current DLSS manifest did not provide nvngx_dlssnr.dll for the DLSS NR variant.");
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
            // Preflight every destructive action before changing any file. Kept/taken-over files are
            // hash-checked too: restoring their .original would overwrite a file the user replaced.
            foreach (var file in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall && !File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                    return ComponentOperationResult.Fail($"Cannot safely remove OptiScaler: preserved original is missing for {file.Path}.");
                if (!File.Exists(file.Path) || file.AllowUserChanges) continue;
                var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so OptiScaler was not removed.");
            }

            if (reShade64 is not null && File.Exists(reShade64.Path))
            {
                var current = await Sha256Async(reShade64.Path, cancellationToken).ConfigureAwait(false);
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

            foreach (var file in record.Files.OrderByDescending(file => file.Path.Length).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            if (reShadeRecord is not null && reShade64 is not null && File.Exists(reShade64.Path))
            {
                if (File.Exists(dxgi))
                    return FailKeepingProgress("OptiScaler files were removed, but dxgi.dll is unexpectedly occupied; RHI did not move ReShade back over it.");
                File.Move(reShade64.Path, dxgi);
                reShade64.Path = dxgi;
                reShade64.Sha256 = await Sha256Async(dxgi, cancellationToken).ConfigureAwait(false);
                reShadeRecord.WineOverride = record.WineOverride;
                _records.Upsert(reShadeRecord);
                keptReShade = true;
            }
            else if (game.PrefixPath is { } prefix && Directory.Exists(prefix))
            {
                _overrides.Restore(prefix, record.WineOverride);
            }

            CleanupDirectories(record.DirectoriesCreatedByRhi);
            _records.Remove(game, ManagedComponentKind.OptiScaler);
            return ComponentOperationResult.Ok(keptReShade
                ? "RHI-managed OptiScaler removed; ReShade64.dll was moved back to dxgi.dll and retained its Wine override."
                : "RHI-managed OptiScaler removed; preserved game files and the Wine DLL override were restored.");
        }
        catch (Exception ex)
        {
            return FailKeepingProgress($"OptiScaler removal failed: {ex.Message}");
        }
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

        var current = await Sha256Async(proxyTarget, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed ReShade dxgi.dll was modified outside RHI and will not be migrated automatically.");

        var oldPath = file.Path;
        var oldHash = file.Sha256;
        var oldOverride = record.WineOverride;
        File.Move(proxyTarget, reShade64);
        file.Path = reShade64;
        file.Sha256 = await Sha256Async(reShade64, cancellationToken).ConfigureAwait(false);
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
                ? await Sha256Async(migration.OldPath, cancellationToken).ConfigureAwait(false)
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

        foreach (var source in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(packageRoot, source);
            if (relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (SetupJunkNames.Any(name => Path.GetFileName(relative).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment.Equals("Licenses", StringComparison.OrdinalIgnoreCase))) continue;

            var target = Path.GetFullPath(Path.Combine(binaryDirectory, relative));
            var rootWithSep = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new InvalidOperationException($"OptiScaler archive path escapes the game binary directory: {relative}");
            requested.Add((source, target));
        }
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

    private static async Task ReplaceAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var temp = target + $".rhi-update-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(source).Length != new FileInfo(temp).Length)
                throw new IOException($"OptiScaler update copy verification failed for {Path.GetFileName(target)}.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed record ReShadeMigration(
        LinuxDeploymentRecord Record,
        LinuxDeployedFile File,
        string OldPath,
        string? OldHash,
        string NewPath,
        WineOverrideRecord? TransferredOverride);
}
