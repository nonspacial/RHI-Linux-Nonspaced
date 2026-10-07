using System.Text.RegularExpressions;

namespace RHI.Core.Deployment;

/// <summary>
/// Start-up sweep of transaction leftovers (<c>&lt;name&gt;.rhi-&lt;tag&gt;-&lt;32 hex id&gt;</c>) that a crash or an
/// older build left in game directories. It never wipes data it cannot prove redundant:
/// <list type="bullet">
/// <item>it must run before this process can start any deployment, and only while no other RHI
/// process runs (<see cref="DeploymentInstanceGate"/>); the checks below are a second line;</item>
/// <item>a leftover whose inode changed (ctime, <see cref="FileChangeTime"/>) within <c>minAge</c> is
/// skipped: a rename or a fresh copy resets ctime, unlike the modification time, which a rename and
/// <see cref="File.Copy(string, string)"/> both keep;</item>
/// <item>a leftover whose id belongs to a journal entry is skipped, asked again for every file so an
/// operation that started after the sweep began is still seen;</item>
/// <item>temporary copies (<c>.rhi-tmp-</c>, <c>.rhi-restore-tmp-</c>) are partial writes of new content and are deleted;</item>
/// <item>any other leftover (snapshots, staged copies) is deleted only when it is byte-identical to the file
/// it belongs to or to that file's <c>.original</c>; otherwise it is moved, by a durable rename on the
/// same file system, into <see cref="RecoveredCopies.FolderName"/> beside it, so a copy that may be the
/// only one of a user's file survives.</item>
/// </list>
/// Only the listed directories are read (not recursively); symlinks are never followed or touched.
/// </summary>
public static partial class DeploymentLeftoverSweeper
{
    private static readonly HashSet<string> TemporaryTags = new(StringComparer.Ordinal) { "tmp", "restore-tmp" };

    [GeneratedRegex(@"^(?<base>.+)\.rhi-(?<tag>[a-z]+(?:-[a-z]+)*)-(?<id>[0-9a-f]{32})$")]
    private static partial Regex LeftoverName();

    public static bool IsLeftoverName(string fileName) => LeftoverName().IsMatch(fileName);

    /// <param name="isReferenced">Whether an operation id still has a journal entry; called per file, just before acting on it.</param>
    public static LeftoverSweepReport Sweep(
        IEnumerable<string> directories,
        Func<string, bool> isReferenced,
        TimeSpan minAge,
        DateTime? nowUtc = null)
    {
        var deleted = new List<string>();
        var quarantined = new List<(string From, string To)>();
        var problems = new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listed in directories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            string directory;
            string[] files;
            try
            {
                directory = Path.GetFullPath(listed);
                if (!seen.Add(directory) || !Directory.Exists(directory)) continue;
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A malformed recorded path must not stop the sweep of the others.
                problems.Add($"{listed.Replace('\0', '?')}: {ex.Message}");
                continue;
            }

            foreach (var path in files)
            {
                var match = LeftoverName().Match(Path.GetFileName(path));
                if (!match.Success) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.LinkTarget is not null) continue;
                    var now = nowUtc ?? DateTime.UtcNow;
                    if (now - FileChangeTime.LastChangeUtc(path) < minAge) continue;
                    if (isReferenced(match.Groups["id"].Value)) continue;

                    if (TemporaryTags.Contains(match.Groups["tag"].Value) || IsRedundant(path, Path.Combine(directory, match.Groups["base"].Value)))
                    {
                        AtomicFile.Delete(path);
                        deleted.Add(path);
                        continue;
                    }

                    quarantined.Add((path, RecoveredCopies.MoveAside(path, now)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
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
    IReadOnlyList<string> Problems,
    string? SkippedReason = null)
{
    public static LeftoverSweepReport Skipped(string reason) => new([], [], [], reason);

    public bool IsEmpty => Deleted.Count == 0 && Quarantined.Count == 0 && Problems.Count == 0;
}
