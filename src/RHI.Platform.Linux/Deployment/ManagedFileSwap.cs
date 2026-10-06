using System.Security.Cryptography;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// In-place replacement of files already owned by an RHI deployment record (upstream RHI 2.7.6
/// "swap while installed"). Only the named files change; every other file of the record is left
/// alone. Each target must still match its recorded hash, so a file edited outside RHI is never
/// overwritten. When copying or persisting the record fails, the previous bytes and hashes are
/// put back.
/// </summary>
public static class ManagedFileSwap
{
    private const string BackupSuffix = ".rhi-swap-old";
    private const string StagingSuffix = ".rhi-swap-new";

    /// <summary>Returns the record file matching <paramref name="fileName"/> (case-insensitive leaf name).</summary>
    public static LinuxDeployedFile? FindFile(LinuxDeploymentRecord record, string fileName)
        => record.Files.FirstOrDefault(file =>
            Path.GetFileName(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces each recorded file with its source, updates the recorded hashes, then calls
    /// <paramref name="persist"/>. Returns the number of files whose bytes actually changed.
    /// </summary>
    public static async Task<int> ReplaceAsync(
        IReadOnlyList<(LinuxDeployedFile File, string SourcePath)> items,
        Action persist,
        CancellationToken cancellationToken = default)
    {
        // Verify everything before touching anything.
        var plan = new List<(LinuxDeployedFile File, string Source, string NewHash, string? OldHash)>();
        foreach (var (file, source) in items)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("Swap source is missing.", source);
            if (!File.Exists(file.Path))
                throw new InvalidOperationException($"Managed file is missing: {file.Path}. Reinstall the component instead.");
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(file.Sha256) &&
                !string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Managed file was modified outside RHI and will not be replaced: {file.Path}");
            plan.Add((file, source, await Sha256Async(source, cancellationToken).ConfigureAwait(false), file.Sha256));
        }

        var swapped = new List<(LinuxDeployedFile File, string? OldHash)>();
        try
        {
            foreach (var (file, source, newHash, oldHash) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(newHash, oldHash, StringComparison.OrdinalIgnoreCase)) continue;
                var staging = file.Path + StagingSuffix;
                File.Copy(file.Path, file.Path + BackupSuffix, overwrite: true);
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, file.Path, overwrite: true);
                swapped.Add((file, oldHash));
                file.Sha256 = newHash;
            }
            persist();
        }
        catch
        {
            // Compensation never honours cancellation: a half-swapped component must be restored.
            foreach (var (file, oldHash) in swapped.AsEnumerable().Reverse())
            {
                try { File.Copy(file.Path + BackupSuffix, file.Path, overwrite: true); } catch { }
                file.Sha256 = oldHash;
            }
            throw;
        }
        finally
        {
            foreach (var (file, _, _, _) in plan)
            {
                TryDelete(file.Path + StagingSuffix);
                TryDelete(file.Path + BackupSuffix);
            }
        }
        return swapped.Count;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
