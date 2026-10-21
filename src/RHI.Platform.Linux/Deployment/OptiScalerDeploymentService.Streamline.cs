using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class OptiScalerDeploymentService
{
    internal const string FgStackEnabledMetadataKey = "OptiScaler.FgStackEnabled";
    internal const string FgStackStreamlineVersionMetadataKey = "OptiScaler.StreamlineVersion";
    internal const string FgStackDlssEnablerVersionMetadataKey = "OptiScaler.DlssEnablerVersion";
    internal const string FgStackFilesMetadataKey = "OptiScaler.FgStackFiles";
    internal const string FgStackDlssgVersionMetadataKey = "OptiScaler.DlssgVersion";
    internal const string FgStackDlssEnablerDownloadPart = "DlssEnabler";
    internal const string DlssEnablerHeadlessFileName = "dlss-enabler-headless.dll";
    internal const string DlssgFileName = "nvngx_dlssg.dll";

    /// <summary>Advanced-setting id that deploys/removes the managed Streamline + DLSS Enabler FG stack.</summary>
    public const string DeployStreamlineDlssEnablerSettingId = "DeployStreamlineDlssEnabler";

    private static readonly OptiScalerAdvancedOption[] YesNoOptions =
    [
        new("false", "No"),
        new("true", "Yes")
    ];

    internal static bool SupportsManagedFgStack(LinuxDeploymentRecord record)
        => GetInstalledVariant(record) is OptiScalerVariant.Nightly or OptiScalerVariant.DlssNr or OptiScalerVariant.F5;

    internal bool IsManagedFgStackEnabled(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(FgStackEnabledMetadataKey, out var enabled)
           && enabled.Equals("1", StringComparison.OrdinalIgnoreCase);

    internal string GetSelectedStreamlineVersion(LinuxDeploymentRecord record)
    {
        if (record.Metadata.TryGetValue(FgStackStreamlineVersionMetadataKey, out var saved) && !string.IsNullOrWhiteSpace(saved))
            return saved;

        var versions = _dlss?.Manifest?.Streamline.Select(entry => entry.Version).Where(version => !string.IsNullOrWhiteSpace(version)).ToArray() ?? [];
        if (versions.Contains("2.12.0", StringComparer.OrdinalIgnoreCase)) return "2.12.0";
        return versions.FirstOrDefault() ?? "2.12.0";
    }

    internal IReadOnlyList<OptiScalerAdvancedOption> GetStreamlineVersionOptions(LinuxDeploymentRecord record)
    {
        var selected = GetSelectedStreamlineVersion(record);
        var versions = _dlss?.Manifest?.Streamline
            .Select(entry => entry.Version)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (versions.Length == 0)
            return [new OptiScalerAdvancedOption(selected, selected)];
        if (versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            return versions.Select(version => new OptiScalerAdvancedOption(version, version)).ToArray();
        return versions
            .Select(version => new OptiScalerAdvancedOption(version, version))
            .Prepend(new OptiScalerAdvancedOption(selected, $"Current / custom: {selected}"))
            .ToArray();
    }

    public async Task<ComponentOperationResult> SetAdvancedSettingAsync(
        DetectedGame game,
        string id,
        string value,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = id?.Trim() ?? "";
        if (normalizedId.Equals(DeployStreamlineDlssEnablerSettingId, StringComparison.Ordinal))
        {
            var enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (!enabled && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Unsupported Streamline/DLSS Enabler toggle value: {value}");
            return enabled
                ? await EnableManagedFgStackAsync(game, cancellationToken).ConfigureAwait(false)
                : await DisableManagedFgStackAsync(game, cancellationToken).ConfigureAwait(false);
        }

        if (normalizedId.Equals("StreamlineVersion", StringComparison.Ordinal))
            return await SetManagedStreamlineVersionAsync(game, value, cancellationToken).ConfigureAwait(false);

        return SetAdvancedSetting(game, normalizedId, value);
    }

    private async Task<ComponentOperationResult> SetManagedStreamlineVersionAsync(
        DetectedGame game,
        string version,
        CancellationToken cancellationToken)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler is external/unmanaged. RHI Linux will not deploy Streamline into it.");
        if (!SupportsManagedFgStack(record))
            return ComponentOperationResult.Fail("Streamline/DLSS Enabler deployment is available only for the OptiScaler Nightly and DLSS NR variants, matching upstream RHI.");
        if (_dlss is null)
            return ComponentOperationResult.Fail("The DLSS/Streamline service is unavailable.");

        try
        {
            if (_dlss.Manifest is null)
                await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
            var canonical = _dlss.Manifest?.Streamline
                .Select(entry => entry.Version)
                .FirstOrDefault(candidate => string.Equals(candidate, version?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(canonical))
                return ComponentOperationResult.Fail($"Streamline version '{version}' is not present in the current upstream RHI manifest.");

            record.Metadata[FgStackStreamlineVersionMetadataKey] = canonical;
            _records.Upsert(record);

            if (!IsManagedFgStackEnabled(record))
                return ComponentOperationResult.Ok($"OptiScaler Streamline version set to {canonical}. It will be used when Streamline/DLSS Enabler deployment is enabled.");

            return await DeployManagedFgStackAsync(game, record, canonical, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"Could not set OptiScaler Streamline version: {ex.Message}");
        }
    }

    private async Task<ComponentOperationResult> EnableManagedFgStackAsync(
        DetectedGame game,
        CancellationToken cancellationToken)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler is external/unmanaged. RHI Linux will not deploy Streamline/DLSS Enabler into it.");
        if (!SupportsManagedFgStack(record))
            return ComponentOperationResult.Fail("Streamline/DLSS Enabler deployment is available only for OptiScaler Nightly and DLSS NR, matching the Windows RHI flow.");
        if (_dlss is null || _dlssEnabler is null)
            return ComponentOperationResult.Fail("The Streamline/DLSS Enabler deployment services are unavailable.");

        try
        {
            if (_dlss.Manifest is null)
                await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
            var version = GetSelectedStreamlineVersion(record);
            if (!(_dlss.Manifest?.Streamline.Any(entry => entry.Version.Equals(version, StringComparison.OrdinalIgnoreCase)) ?? false))
            {
                var fallback = _dlss.Manifest?.Streamline.FirstOrDefault()?.Version;
                if (string.IsNullOrWhiteSpace(fallback))
                    return ComponentOperationResult.Fail("The current upstream RHI manifest does not provide a Streamline package.");
                version = fallback;
            }
            return await DeployManagedFgStackAsync(game, record, version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"OptiScaler Streamline/DLSS Enabler deployment failed: {ex.Message}");
        }
    }

    private async Task<ComponentOperationResult> DeployManagedFgStackAsync(
        DetectedGame game,
        LinuxDeploymentRecord record,
        string streamlineVersion,
        CancellationToken cancellationToken)
    {
        if (_dlss is null || _dlssEnabler is null)
            return ComponentOperationResult.Fail("The Streamline/DLSS Enabler deployment services are unavailable.");

        var streamlineCache = await _dlss.EnsureStreamlineCachedAsync(streamlineVersion, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(streamlineCache) || !Directory.Exists(streamlineCache))
            return ComponentOperationResult.Fail($"Could not stage Streamline {streamlineVersion}.");

        var (enablerRelease, enablerPayload) = await _dlssEnabler
            .EnsureLatestPayloadForOptiScalerAsync(cancellationToken)
            .ConfigureAwait(false);
        var dlssg = await StageNewestDlssgAsync(cancellationToken).ConfigureAwait(false);

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var optiScalerDirectory = Path.Combine(binaryDirectory, "OptiScaler");
        var streamlineDirectory = Path.Combine(optiScalerDirectory, "Streamline");
        var requested = DlssManagerService.KnownStreamlineDlls
            .Select(name => (Source: Path.Combine(streamlineCache, name), Target: Path.Combine(streamlineDirectory, name)))
            .Where(item => File.Exists(item.Source))
            .ToList();
        requested.Add((enablerPayload, Path.Combine(optiScalerDirectory, DlssEnablerHeadlessFileName)));
        if (dlssg.Path is not null)
            requested.Add((dlssg.Path, Path.Combine(streamlineDirectory, DlssgFileName)));

        if (!requested.Any(item => Path.GetFileName(item.Target).Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(item.Target).Equals("sl.common.dll", StringComparison.OrdinalIgnoreCase)))
            return ComponentOperationResult.Fail("The staged Streamline package contains no usable sl.interposer.dll/sl.common.dll payload.");

        var previousOwned = ReadFgStackRelativePaths(record);
        var fileByPath = record.Files.ToDictionary(file => Path.GetFullPath(file.Path), StringComparer.Ordinal);
        var createdDirectories = new List<string>();
        // Foreign files found at a stack target: kept as <name>.original and restored on disable/remove.
        var foreign = new HashSet<string>(StringComparer.Ordinal);
        var preserved = new List<string>();
        var leftUnowned = new HashSet<string>(StringComparer.Ordinal);

        var legacySwaps = await FindLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);
        try
        {
            // Preflight all destinations before touching any file.
            foreach (var (sourceRaw, targetRaw) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(targetRaw);
                var relative = RelativeToBinary(binaryDirectory, target);
                if (!File.Exists(target)) continue;

                if (!previousOwned.Contains(relative))
                {
                    if (fileByPath.ContainsKey(target))
                        throw new InvalidOperationException($"OptiScaler FG stack target belongs to the OptiScaler package itself and will not be replaced: {target}");
                    // A foreign file already identical to the payload is what the stack needs: it is left
                    // untouched and unowned, so an existing .original next to it does not matter.
                    if (string.Equals(await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false),
                            await FileHash.Sha256HexAsync(sourceRaw, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    {
                        leftUnowned.Add(target);
                        continue;
                    }
                    if (File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
                        throw new InvalidOperationException($"Backup collision protection: {target}{TransactionalFileDeploymentService.BackupExtension} already exists. RHI will not guess which previous tool owns that backup.");
                    foreign.Add(target);
                    continue;
                }

                if (!fileByPath.TryGetValue(target, out var ownedFile))
                    throw new InvalidOperationException($"OptiScaler FG stack ownership record is missing for {target}; nothing was changed.");
                if (ownedFile.AllowUserChanges)
                    throw new InvalidOperationException($"OptiScaler FG stack target is unexpectedly user-editable and will not be overwritten: {target}");
                if (!string.IsNullOrWhiteSpace(ownedFile.Sha256) && !legacySwaps.Contains(ownedFile.Path))
                {
                    var currentHash = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentHash, ownedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Managed OptiScaler FG stack file was modified outside RHI: {target}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ComponentOperationResult.Fail($"OptiScaler Streamline/DLSS Enabler deployment rolled back: {ex.Message}");
        }

        // Journaled (#23, #31): every stack file is tracked with a verified copy (a foreign one with its .original) and
        // every placed content declared before it changes; the record (stamped) is the commit point. A failure, or a
        // crash before the record is saved, puts every file back.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"OptiScaler FG stack: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        string message;
        try
        {
            // Preflighted: from the first file on, the deployment runs to completion or full rollback.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await GiveBackLegacyDlssSwapsAsync(legacySwaps, scope, cancellationToken).ConfigureAwait(false);
            foreach (var (source, targetRaw) in requested)
            {
                var target = Path.GetFullPath(targetRaw);
                if (leftUnowned.Contains(target)) continue;
                if (foreign.Contains(target))
                {
                    await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
                    await scope.DeclareWriteAsync(target, await FileHash.Sha256HexAsync(source, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    var replaced = await _files.ReplaceAsync(source, target, cancellationToken).ConfigureAwait(false);
                    if (!replaced.Success)
                        throw new IOException(replaced.Error ?? $"Could not replace {target} while keeping its original.");
                    // An identical file is already what the stack needs; it stays the user's, not RHI's.
                    if (replaced.Changed) preserved.Add(target);
                    else leftUnowned.Add(target);
                    continue;
                }

                EnsureTargetDirectory(binaryDirectory, target, createdDirectories);
                await scope.PlaceCopyAsync(source, target, cancellationToken, overwrite: File.Exists(target)).ConfigureAwait(false);
            }

            var ownedRelative = new HashSet<string>(previousOwned, StringComparer.Ordinal);
            foreach (var (_, targetRaw) in requested)
            {
                var target = Path.GetFullPath(targetRaw);
                if (leftUnowned.Contains(target)) continue;
                var relative = RelativeToBinary(binaryDirectory, target);
                ownedRelative.Add(relative);
                var hash = await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false);
                if (fileByPath.TryGetValue(target, out var file))
                {
                    file.Sha256 = hash;
                    file.AllowUserChanges = false;
                }
                else
                {
                    file = new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = preserved.Contains(target, StringComparer.Ordinal),
                        Sha256 = hash,
                        AllowUserChanges = false
                    };
                    record.Files.Add(file);
                    fileByPath[target] = file;
                }
            }

            record.Metadata[FgStackEnabledMetadataKey] = "1";
            record.Metadata[FgStackStreamlineVersionMetadataKey] = streamlineVersion;
            record.Metadata[FgStackDlssEnablerVersionMetadataKey] = enablerRelease.Version;
            // The DLSS Enabler payload is a download of this record too (#26); Streamline and nvngx_dlssg come from the
            // DLSS manager's own cache (its manifest sources, not a deployment download policy).
            VerifiedDownload.Record(record.Metadata, await FileHash.Sha256HexAsync(enablerPayload, CancellationToken.None).ConfigureAwait(false),
                !string.IsNullOrWhiteSpace(enablerRelease.AssetSha256), FgStackDlssEnablerDownloadPart);
            if (dlssg.Version is not null && !leftUnowned.Contains(Path.GetFullPath(Path.Combine(streamlineDirectory, DlssgFileName))))
                record.Metadata[FgStackDlssgVersionMetadataKey] = dlssg.Version;
            record.Metadata[FgStackFilesMetadataKey] = JsonSerializer.Serialize(ownedRelative.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            foreach (var directory in createdDirectories)
                if (!record.DirectoriesCreatedByRhi.Contains(directory, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(directory);
            _records.Upsert(scope.Stamp(record));

            message = dlssg.Version is not null
                ? $"OptiScaler Streamline {streamlineVersion} + DLSS Enabler {enablerRelease.Version} + {DlssgFileName} {dlssg.Version} deployed to OptiScaler/."
                : $"OptiScaler Streamline {streamlineVersion} + DLSS Enabler {enablerRelease.Version} deployed to OptiScaler/; {DlssgFileName} was not deployed: {dlssg.Problem}";
            if (preserved.Count > 0)
                message += $" Existing files kept as {TransactionalFileDeploymentService.BackupExtension} and restored on removal: {string.Join(", ", preserved.Select(Path.GetFileName))}.";
        }
        catch (Exception ex)
        {
            // Compensation must run even when the failure is a cancellation; never cancelled itself.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("OptiScaler Streamline/DLSS Enabler");
            if (rollback.RolledBack) CleanupDirectories(createdDirectories);
            return ComponentOperationResult.Fail(rollback.Success
                ? $"OptiScaler Streamline/DLSS Enabler deployment rolled back: {ex.Message}"
                : $"OptiScaler Streamline/DLSS Enabler deployment failed ({ex.Message}) and the rollback is incomplete: {string.Join("; ", rollback.Failures)}");
        }
        // The record carries the operation id: it is the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(message, requested
            .Select(item => item.Target)
            .Where(target => !leftUnowned.Contains(Path.GetFullPath(target)))
            .ToArray());
    }

    /// <summary>
    /// Stages the newest manifest nvngx_dlssg.dll for the FG stack. Best effort, as upstream RHI:
    /// Streamline + DLSS Enabler still deploy when the manifest lists no DLSSG or its download fails.
    /// </summary>
    private async Task<(string? Path, string? Version, string? Problem)> StageNewestDlssgAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _dlss!.EnsureCachedVersionAsync(DlssComponentKind.FrameGeneration, null, cancellationToken).ConfigureAwait(false);
            return cached is { } staged && File.Exists(staged.Path)
                ? (staged.Path, staged.Version, null)
                : (null, null, "the DLSS manifest lists no FG build.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null, ex.Message);
        }
    }

    private async Task<ComponentOperationResult> DisableManagedFgStackAsync(
        DetectedGame game,
        CancellationToken cancellationToken)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler is not RHI-managed for this game.");
        if (!IsManagedFgStackEnabled(record))
            return ComponentOperationResult.Ok("OptiScaler Streamline/DLSS Enabler deployment is already disabled.");

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var ownedRelative = ReadFgStackRelativePaths(record);
        List<(string Relative, string Target)> targets;
        try
        {
            targets = ownedRelative.Select(relative => (relative, ResolveManagedRelativePath(binaryDirectory, relative))).ToList();
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"OptiScaler FG stack ownership metadata is unsafe; removal is blocked: {ex.Message}");
        }

        var ownedFiles = new List<(string Relative, LinuxDeployedFile File)>(targets.Count);
        foreach (var (relative, target) in targets)
        {
            var owned = record.Files.FirstOrDefault(file => Path.GetFullPath(file.Path).Equals(target, StringComparison.Ordinal));
            if (owned is null)
                return ComponentOperationResult.Fail($"OptiScaler FG stack ownership record is missing for {target}; removal is blocked.");
            ownedFiles.Add((relative, owned));
        }

        var legacySwaps = await FindLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);
        foreach (var (_, file) in ownedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ExistedBeforeInstall && !File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                return ComponentOperationResult.Fail($"Cannot safely remove the OptiScaler FG stack: preserved original is missing for {file.Path}.");
            if (!File.Exists(file.Path) || legacySwaps.Contains(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"OptiScaler FG stack ownership hash is missing for {file.Path}; removal is blocked.");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"OptiScaler FG stack file was modified outside RHI and will not be removed: {file.Path}");
        }

        // Journaled (#23, #31): the narrowed record (stamped) is the commit point; every file the teardown touches is
        // tracked with a verified copy first, so a failure or a crash before the save puts the whole stack back (the
        // disable never stops half-way).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"OptiScaler FG stack removal: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Cancel is honoured only up to here.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await GiveBackLegacyDlssSwapsAsync(legacySwaps, scope, cancellationToken).ConfigureAwait(false);
            foreach (var (_, file) in ownedFiles.OrderByDescending(item => item.File.Path.Length))
            {
                await scope.TrackAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (file.ExistedBeforeInstall)
                {
                    await scope.DeclareWriteAsync(file.Path, scope.Files.Find(file.Path)!.BackupPreSha256
                        ?? await FileHash.Sha256HexAsync(file.Path + TransactionalFileDeploymentService.BackupExtension, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success)
                        throw new IOException($"Could not restore the original {file.Path}: {restored.Error}");
                }
                else if (File.Exists(file.Path))
                {
                    AtomicFile.Delete(file.Path);
                }
                record.Files.Remove(file);
            }

            record.Metadata[FgStackEnabledMetadataKey] = "0";
            record.Metadata.Remove(FgStackDlssEnablerVersionMetadataKey);
            VerifiedDownload.Forget(record.Metadata, FgStackDlssEnablerDownloadPart);
            record.Metadata.Remove(FgStackDlssgVersionMetadataKey);
            record.Metadata.Remove(FgStackFilesMetadataKey);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("OptiScaler Streamline/DLSS Enabler", removal: true);
            return ComponentOperationResult.Fail($"Could not remove OptiScaler Streamline/DLSS Enabler deployment: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The narrowed record carries the operation id: it is the commit point. The emptied folder goes after it.
        await scope.CommitAsync().ConfigureAwait(false);
        var streamlineDirectory = Path.Combine(binaryDirectory, "OptiScaler", "Streamline");
        CleanupDirectories([streamlineDirectory]);
        if (record.DirectoriesCreatedByRhi.RemoveAll(path => !Directory.Exists(path)) > 0)
        {
            try { _records.Upsert(record); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn("optiscaler", "Could not drop removed folders from the OptiScaler record", ex);
            }
        }
        return ComponentOperationResult.Ok("OptiScaler Streamline/DLSS Enabler deployment removed. The selected Streamline version preference was preserved.");
    }

    internal static bool HasManagedOptiScalerStreamlineFgFiles(LinuxDeploymentRecord record)
    {
        if (!SupportsManagedFgStack(record)) return false;
        if (!record.Metadata.TryGetValue(FgStackEnabledMetadataKey, out var enabled) || enabled != "1") return false;
        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var streamlineDirectory = Path.Combine(binaryDirectory, "OptiScaler", "Streamline");
        var hasStreamline = File.Exists(Path.Combine(streamlineDirectory, "sl.interposer.dll"))
                            || File.Exists(Path.Combine(streamlineDirectory, "sl.common.dll"))
                            || File.Exists(Path.Combine(streamlineDirectory, "sl.dlss_g.dll"));
        var hasEnabler = File.Exists(Path.Combine(binaryDirectory, "OptiScaler", DlssEnablerHeadlessFileName));
        return hasStreamline && hasEnabler;
    }

    private static HashSet<string> ReadFgStackRelativePaths(LinuxDeploymentRecord record)
    {
        if (!record.Metadata.TryGetValue(FgStackFilesMetadataKey, out var json) || string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string ResolveManagedRelativePath(string binaryDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("Managed relative path is empty.");

        var normalizedRelative = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
            throw new InvalidDataException($"Managed path must be relative: {relativePath}");

        var root = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        if (!target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidDataException($"Managed path escapes the game binary directory: {relativePath}");

        return target;
    }

    private static string RelativeToBinary(string binaryDirectory, string path)
        => Path.GetRelativePath(binaryDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
}
