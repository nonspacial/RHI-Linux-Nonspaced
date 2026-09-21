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
    internal const string DlssEnablerHeadlessFileName = "dlss-enabler-headless.dll";

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
        if (normalizedId.Equals("DeployStreamlineDlssEnabler", StringComparison.Ordinal))
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

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var optiScalerDirectory = Path.Combine(binaryDirectory, "OptiScaler");
        var streamlineDirectory = Path.Combine(optiScalerDirectory, "Streamline");
        var requested = DlssManagerService.KnownStreamlineDlls
            .Select(name => (Source: Path.Combine(streamlineCache, name), Target: Path.Combine(streamlineDirectory, name)))
            .Where(item => File.Exists(item.Source))
            .ToList();
        requested.Add((enablerPayload, Path.Combine(optiScalerDirectory, DlssEnablerHeadlessFileName)));

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

        try
        {
            // Preflight all destinations before touching any file.
            foreach (var (_, targetRaw) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(targetRaw);
                var relative = RelativeToBinary(binaryDirectory, target);
                if (!File.Exists(target)) continue;

                if (!previousOwned.Contains(relative) || !fileByPath.TryGetValue(target, out var ownedFile))
                    throw new InvalidOperationException($"OptiScaler FG stack target already exists outside this managed Streamline deployment: {target}");
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
                        ExistedBeforeInstall = false,
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
            record.Metadata[FgStackFilesMetadataKey] = JsonSerializer.Serialize(ownedRelative.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            foreach (var directory in createdDirectories)
                if (!record.DirectoriesCreatedByRhi.Contains(directory, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(directory);
            _records.Upsert(record);

            return ComponentOperationResult.Ok(
                $"OptiScaler Streamline {streamlineVersion} + DLSS Enabler {enablerRelease.Version} deployed to OptiScaler/ exactly like the upstream Windows flow.",
                requested.Select(item => item.Target).ToArray());
        }
        catch (Exception ex)
        {
            foreach (var pair in backups)
            {
                try { if (File.Exists(pair.Value)) File.Copy(pair.Value, pair.Key, overwrite: true); } catch { }
            }
            foreach (var target in newFiles.AsEnumerable().Reverse())
            {
                try { if (File.Exists(target)) File.Delete(target); } catch { }
            }
            CleanupDirectories(createdDirectories);
            return ComponentOperationResult.Fail($"OptiScaler Streamline/DLSS Enabler deployment rolled back: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(backupRoot, recursive: true); } catch { }
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
        string[] targets;
        try
        {
            targets = ownedRelative.Select(relative => ResolveManagedRelativePath(binaryDirectory, relative)).ToArray();
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"OptiScaler FG stack ownership metadata is unsafe; removal is blocked: {ex.Message}");
        }

        var ownedFiles = new List<LinuxDeployedFile>(targets.Length);
        foreach (var target in targets)
        {
            var owned = record.Files.FirstOrDefault(file => Path.GetFullPath(file.Path).Equals(target, StringComparison.Ordinal));
            if (owned is null)
                return ComponentOperationResult.Fail($"OptiScaler FG stack ownership record is missing for {target}; removal is blocked.");
            ownedFiles.Add(owned);
        }

        try
        {
            foreach (var file in ownedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(file.Path)) continue;
                if (string.IsNullOrWhiteSpace(file.Sha256))
                    return ComponentOperationResult.Fail($"OptiScaler FG stack ownership hash is missing for {file.Path}; removal is blocked.");
                var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"OptiScaler FG stack file was modified outside RHI and will not be removed: {file.Path}");
            }

            foreach (var file in ownedFiles.OrderByDescending(file => file.Path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (File.Exists(file.Path)) File.Delete(file.Path); }
                catch (Exception ex) { return ComponentOperationResult.Fail($"Could not remove {file.Path}: {ex.Message}"); }
                record.Files.Remove(file);
            }

            record.Metadata[FgStackEnabledMetadataKey] = "0";
            record.Metadata.Remove(FgStackDlssEnablerVersionMetadataKey);
            record.Metadata.Remove(FgStackFilesMetadataKey);
            var streamlineDirectory = Path.Combine(binaryDirectory, "OptiScaler", "Streamline");
            try { if (Directory.Exists(streamlineDirectory) && !Directory.EnumerateFileSystemEntries(streamlineDirectory).Any()) Directory.Delete(streamlineDirectory); } catch { }
            record.DirectoriesCreatedByRhi.RemoveAll(path => !Directory.Exists(path));
            _records.Upsert(record);
            return ComponentOperationResult.Ok("OptiScaler Streamline/DLSS Enabler deployment removed. The selected Streamline version preference was preserved.");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Could not remove OptiScaler Streamline/DLSS Enabler deployment: {ex.Message}");
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
