using System.Text.RegularExpressions;

namespace RHI.Core.Deployment;

/// <summary>
/// Start-up sweep of transaction leftovers (<c>&lt;name&gt;.rhi-&lt;tag&gt;-&lt;32 hex id&gt;</c>) that a crash or an
/// older build left in game directories. It never wipes data it cannot prove redundant:
/// <list type="bullet">
/// <item>leftovers younger than <c>minAge</c>, or whose id belongs to a journal entry still on disk, are skipped;</item>
/// <item>temporary copies (<c>.rhi-tmp-</c>, <c>.rhi-restore-tmp-</c>) are partial writes of new content and are deleted;</item>
/// <item>any other leftover (snapshots, staged copies) is deleted only when it is byte-identical to the file
/// it belongs to or to that file's <c>.original</c>; otherwise it is moved into <c>quarantineRoot</c>
/// under its original absolute path, so a copy that may be the only one of a user's file survives.</item>
/// </list>
/// Only the listed directories are read (not recursively).
/// </summary>
public static partial class DeploymentLeftoverSweeper
{
    private static readonly HashSet<string> TemporaryTags = new(StringComparer.Ordinal) { "tmp", "restore-tmp" };

    [GeneratedRegex(@"^(?<base>.+)\.rhi-(?<tag>[a-z]+(?:-[a-z]+)*)-(?<id>[0-9a-f]{32})$")]
    private static partial Regex LeftoverName();

    public static bool IsLeftoverName(string fileName) => LeftoverName().IsMatch(fileName);

    public static LeftoverSweepReport Sweep(
        IEnumerable<string> directories,
        IReadOnlySet<string> referencedOperationIds,
        string quarantineRoot,
        TimeSpan minAge,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var deleted = new List<string>();
        var quarantined = new List<(string From, string To)>();
        var problems = new List<string>();
        string? batchRoot = null;

        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            string[] files;
            try
            {
                if (!Directory.Exists(directory)) continue;
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{directory}: {ex.Message}");
                continue;
            }

            foreach (var path in files)
            {
                var match = LeftoverName().Match(Path.GetFileName(path));
                if (!match.Success) continue;
                if (referencedOperationIds.Contains(match.Groups["id"].Value)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.LinkTarget is not null || now - info.LastWriteTimeUtc < minAge) continue;

                    if (TemporaryTags.Contains(match.Groups["tag"].Value) || IsRedundant(path, Path.Combine(directory, match.Groups["base"].Value)))
                    {
                        AtomicFile.Delete(path);
                        deleted.Add(path);
                        continue;
                    }

                    batchRoot ??= Path.Combine(quarantineRoot, now.ToString("yyyyMMdd-HHmmss"));
                    var destination = Path.Combine(batchRoot, path.TrimStart(Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(path, destination, overwrite: false);
                    AtomicFile.FsyncDirectoryOf(destination);
                    AtomicFile.FsyncDirectoryOf(path);
                    quarantined.Add((path, destination));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{path}: {ex.Message}");
                }
            }
        }

        return new LeftoverSweepReport(deleted, quarantined, problems);
    }

    private static bool IsRedundant(string leftover, string basePath)
    {
        var references = new List<string> { basePath, basePath + OwnedFileSet.BackupExtension };
        if (basePath.EndsWith(OwnedFileSet.BackupExtension, StringComparison.Ordinal))
            references.Add(basePath[..^OwnedFileSet.BackupExtension.Length]);
        var length = new FileInfo(leftover).Length;
        string? hash = null;
        foreach (var reference in references)
        {
            if (!File.Exists(reference) || new FileInfo(reference).Length != length) continue;
            hash ??= FileHash.Sha256Hex(leftover);
            if (FileHash.HexEquals(hash, FileHash.Sha256Hex(reference))) return true;
        }
        return false;
    }
}

public sealed record LeftoverSweepReport(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<(string From, string To)> Quarantined,
    IReadOnlyList<string> Problems)
{
    public bool IsEmpty => Deleted.Count == 0 && Quarantined.Count == 0 && Problems.Count == 0;
}
