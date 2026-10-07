using System.Text.Json;
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
        => GetInstalledVariant(record) is OptiScalerVariant.Nightly or OptiScalerVariant.DlssNr;

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
        var backupRoot = Path.Combine(Path.GetTempPath(), $"rhi-optiscaler-fg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var newFiles = new List<string>();
        var createdDirectories = new List<string>();
        // Foreign files found at a stack target: kept as <name>.original and restored on disable/remove.
        var foreign = new HashSet<string>(StringComparer.Ordinal);
        var preserved = new List<string>();
        var leftUnowned = new HashSet<string>(StringComparer.Ordinal);

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
                    if (string.Equals(await Sha256Async(target, cancellationToken).ConfigureAwait(false),
                            await Sha256Async(sourceRaw, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
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
                if (!string.IsNullOrWhiteSpace(ownedFile.Sha256))
                {
                    var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentHash, ownedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Managed OptiScaler FG stack file was modified outside RHI: {target}");
                }
            }

            foreach (var (source, targetRaw) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(targetRaw);
                if (leftUnowned.Contains(target)) continue;
                if (foreign.Contains(target))
                {
                    var replaced = await _files.ReplaceAsync(source, target, cancellationToken).ConfigureAwait(false);
                    if (!replaced.Success)
                        throw new IOException(replaced.Error ?? $"Could not replace {target} while keeping its original.");
                    // An identical file is already what the stack needs; it stays the user's, not RHI's.
                    if (replaced.Changed) preserved.Add(target);
                    else leftUnowned.Add(target);
                    continue;
                }

                EnsureTargetDirectory(binaryDirectory, target, createdDirectories);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupRoot, $"{backups.Count:D3}-{Path.GetFileName(target)}");
                    File.Copy(target, backup, overwrite: false);
                    backups[target] = backup;
                }
                else
                {
                    newFiles.Add(target);
                }
                await ReplaceAtomicAsync(source, target, cancellationToken).ConfigureAwait(false);
            }

            var ownedRelative = new HashSet<string>(previousOwned, StringComparer.Ordinal);
            foreach (var (_, targetRaw) in requested)
            {
                var target = Path.GetFullPath(targetRaw);
                if (leftUnowned.Contains(target)) continue;
                var relative = RelativeToBinary(binaryDirectory, target);
                ownedRelative.Add(relative);
                var hash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
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
            if (dlssg.Version is not null && !leftUnowned.Contains(Path.GetFullPath(Path.Combine(streamlineDirectory, DlssgFileName))))
                record.Metadata[FgStackDlssgVersionMetadataKey] = dlssg.Version;
            record.Metadata[FgStackFilesMetadataKey] = JsonSerializer.Serialize(ownedRelative.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            foreach (var directory in createdDirectories)
                if (!record.DirectoriesCreatedByRhi.Contains(directory, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(directory);
            _records.Upsert(record);

            var message = dlssg.Version is not null
                ? $"OptiScaler Streamline {streamlineVersion} + DLSS Enabler {enablerRelease.Version} + {DlssgFileName} {dlssg.Version} deployed to OptiScaler/."
                : $"OptiScaler Streamline {streamlineVersion} + DLSS Enabler {enablerRelease.Version} deployed to OptiScaler/; {DlssgFileName} was not deployed: {dlssg.Problem}";
            if (preserved.Count > 0)
                message += $" Existing files kept as {TransactionalFileDeploymentService.BackupExtension} and restored on removal: {string.Join(", ", preserved.Select(Path.GetFileName))}.";
            return ComponentOperationResult.Ok(message, requested
                .Select(item => item.Target)
                .Where(target => !leftUnowned.Contains(Path.GetFullPath(target)))
                .ToArray());
        }
        catch (Exception ex)
        {
            // Compensation must run even when the failure is a cancellation.
            var problems = new List<string>();
            foreach (var pair in backups)
            {
                try { if (File.Exists(pair.Value)) File.Copy(pair.Value, pair.Key, overwrite: true); }
                catch (Exception restoreError) { problems.Add($"{pair.Key}: {restoreError.Message}"); }
            }
            foreach (var target in newFiles.AsEnumerable().Reverse())
            {
                try { if (File.Exists(target)) File.Delete(target); }
                catch (Exception deleteError) { problems.Add($"{target}: {deleteError.Message}"); }
            }
            foreach (var target in preserved.AsEnumerable().Reverse())
            {
                var restored = await _files.RestoreAsync(target, CancellationToken.None).ConfigureAwait(false);
                if (!restored.Success)
                    problems.Add($"{target}: {restored.Error ?? "restore failed"}; its {TransactionalFileDeploymentService.BackupExtension} copy was kept");
            }
            CleanupDirectories(createdDirectories);
            return ComponentOperationResult.Fail(problems.Count == 0
                ? $"OptiScaler Streamline/DLSS Enabler deployment rolled back: {ex.Message}"
                : $"OptiScaler Streamline/DLSS Enabler deployment failed ({ex.Message}) and the rollback is incomplete: {string.Join("; ", problems)}");
        }
        finally
        {
            try { Directory.Delete(backupRoot, recursive: true); } catch { }
        }
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

        var remaining = new HashSet<string>(ownedRelative, StringComparer.Ordinal);
        var changed = false;
        ComponentOperationResult FailKeepingProgress(string message)
        {
            // Files already removed/restored must leave the ownership record, or the next attempt is blocked.
            if (changed)
            {
                record.Metadata[FgStackFilesMetadataKey] = JsonSerializer.Serialize(remaining.OrderBy(path => path, StringComparer.Ordinal).ToArray());
                _records.Upsert(record);
            }
            return ComponentOperationResult.Fail(message);
        }

        try
        {
            foreach (var (_, file) in ownedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall && !File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                    return ComponentOperationResult.Fail($"Cannot safely remove the OptiScaler FG stack: preserved original is missing for {file.Path}.");
                if (!File.Exists(file.Path)) continue;
                if (string.IsNullOrWhiteSpace(file.Sha256))
                    return ComponentOperationResult.Fail($"OptiScaler FG stack ownership hash is missing for {file.Path}; removal is blocked.");
                var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"OptiScaler FG stack file was modified outside RHI and will not be removed: {file.Path}");
            }

            foreach (var (relative, file) in ownedFiles.OrderByDescending(item => item.File.Path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall)
                {
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success)
                        return FailKeepingProgress($"Could not restore the original {file.Path}: {restored.Error}");
                }
                else
                {
                    try { if (File.Exists(file.Path)) File.Delete(file.Path); }
                    catch (Exception ex) { return FailKeepingProgress($"Could not remove {file.Path}: {ex.Message}"); }
                }
                record.Files.Remove(file);
                remaining.Remove(relative);
                changed = true;
            }

            record.Metadata[FgStackEnabledMetadataKey] = "0";
            record.Metadata.Remove(FgStackDlssEnablerVersionMetadataKey);
            record.Metadata.Remove(FgStackDlssgVersionMetadataKey);
            record.Metadata.Remove(FgStackFilesMetadataKey);
            var streamlineDirectory = Path.Combine(binaryDirectory, "OptiScaler", "Streamline");
            try { if (Directory.Exists(streamlineDirectory) && !Directory.EnumerateFileSystemEntries(streamlineDirectory).Any()) Directory.Delete(streamlineDirectory); } catch { }
            record.DirectoriesCreatedByRhi.RemoveAll(path => !Directory.Exists(path));
            _records.Upsert(record);
            return ComponentOperationResult.Ok("OptiScaler Streamline/DLSS Enabler deployment removed. The selected Streamline version preference was preserved.");
        }
        catch (Exception ex)
        {
            return FailKeepingProgress($"Could not remove OptiScaler Streamline/DLSS Enabler deployment: {ex.Message}");
        }
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

    /// <summary>Drops FG-stack ownership entries whose file record is gone (removed in a partly failed removal).</summary>
    private static void PruneFgStackOwnership(LinuxDeploymentRecord record)
    {
        if (!record.Metadata.ContainsKey(FgStackFilesMetadataKey)) return;
        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var recorded = record.Files.Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.Ordinal);
        var kept = ReadFgStackRelativePaths(record)
            .Where(relative =>
            {
                try { return recorded.Contains(ResolveManagedRelativePath(binaryDirectory, relative)); }
                catch { return true; } // unsafe entries stay so the disable path keeps refusing them
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        record.Metadata[FgStackFilesMetadataKey] = JsonSerializer.Serialize(kept);
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
