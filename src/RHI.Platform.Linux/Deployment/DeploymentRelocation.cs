using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A record that belongs to a game but still points at the folder the game was moved from.</summary>
/// <param name="Record">The stored record (a copy).</param>
/// <param name="OldRoot">The folder its paths are under (the part of the old location that moved).</param>
/// <param name="NewRoot">Where that folder is now.</param>
/// <param name="Adopted">True for a folder-keyed record of the same game found by its files (no store id to match it by).</param>
public sealed record RelocatedRecord(LinuxDeploymentRecord Record, string OldRoot, string NewRoot, bool Adopted);

public sealed record RelocationRepairResult(bool Success, int Repaired, string Message);

/// <summary>
/// Finds and repairs deployment records left pointing at a game's previous location after the game
/// (or its whole library) was moved (DEP-19, Refs #38).
/// <list type="bullet">
/// <item>A record under the game's key whose binary directory is outside the game's current folders
/// is "moved": installing, updating or removing is refused (<see cref="Guard"/>), so nothing is
/// written to, or deleted from, the old folder.</item>
/// <item>"Repair after move" (<see cref="Repair"/>) is the explicit fix: it maps the old folder onto the
/// new one by their common trailing path, checks every hashed file RHI owns is at its (mapped) path
/// with the same SHA-256, and only then rewrites the record. It refuses while the old folder still exists
/// (a copy, not a move: RHI cannot tell which one is in use).</item>
/// <item>Games without a store id are keyed by folder, so a moved one has no record under its key;
/// the same repair adopts a folder-keyed record with the game's name whose old folder is gone and
/// whose hashed files verify at the new location.</item>
/// </list>
/// </summary>
public sealed class DeploymentRelocation
{
    private readonly LinuxDeploymentRecordStore _records;

    public DeploymentRelocation(LinuxDeploymentRecordStore records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public const string GuardPrefix = "This game was moved after RHI installed components: their records still point to ";
    public const string GuardSuffix = ". Nothing was changed. Use Repair after move first.";

    /// <summary>Refusal for any install, update or removal while the game has moved records; null when none.</summary>
    public ComponentOperationResult? Guard(DetectedGame game)
        => FindMoved(game).FirstOrDefault() is { } moved
            ? ComponentOperationResult.Fail(GuardPrefix + moved.Record.BinaryDirectory + GuardSuffix)
            : null;

    /// <summary>True when "Repair after move" has something to do for this game (cheap: no hashing).</summary>
    public bool NeedsRepair(DetectedGame game) => FindMoved(game).Count > 0 || FindAdoptable(game).Count > 0;

    /// <summary>The game's own records that point outside its current folders.</summary>
    public IReadOnlyList<RelocatedRecord> FindMoved(DetectedGame game)
    {
        var anchors = Anchors(game);
        if (anchors.Count == 0) return [];
        var result = new List<RelocatedRecord>();
        foreach (var record in _records.FindAll(game))
        {
            if (string.IsNullOrWhiteSpace(record.BinaryDirectory)) continue;
            if (anchors.Any(anchor => IsUnder(record.BinaryDirectory, anchor))) continue;
            result.Add(TryMap(record, game, out var oldRoot, out var newRoot)
                ? new RelocatedRecord(record, oldRoot, newRoot, Adopted: false)
                : new RelocatedRecord(record, record.BinaryDirectory, "", Adopted: false));
        }
        return result;
    }

    /// <summary>Folder-keyed records of a same-named game whose old folder is gone and whose owned files are at the new location.</summary>
    public IReadOnlyList<RelocatedRecord> FindAdoptable(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.Name) || Anchors(game).Count == 0) return [];
        var result = new List<RelocatedRecord>();
        foreach (var record in _records.All())
        {
            if (GameRecordIdentity.IsStoreKey(record.GameKey) || GameRecordIdentity.Matches(record.GameKey, game)) continue;
            if (!string.Equals(record.GameName, game.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(record.BinaryDirectory) || Directory.Exists(record.BinaryDirectory)) continue;
            if (!TryMap(record, game, out var oldRoot, out var newRoot)) continue;
            var hashed = record.Files.Where(f => IsOwnedHashedFile(f) && IsUnder(f.Path, oldRoot)).ToArray();
            if (hashed.Length > 0 && hashed.All(f => File.Exists(Rebase(f.Path, oldRoot, newRoot))))
                result.Add(new RelocatedRecord(record, oldRoot, newRoot, Adopted: true));
        }
        return result;
    }

    /// <summary>
    /// Rewrites the game's moved and adoptable records to its current location after checking every
    /// owned file there. All or nothing: one failed check leaves every record untouched.
    /// </summary>
    public RelocationRepairResult Repair(DetectedGame game, out IReadOnlyList<RelocatedRecord> repaired)
    {
        repaired = [];
        var candidates = FindMoved(game).Concat(FindAdoptable(game)).ToList();
        if (candidates.Count == 0)
            return new(false, 0, "Nothing to repair: RHI's records for this game already match its folder.");

        var key = LinuxDeploymentRecordStore.GameKey(game);
        var current = _records.FindAll(game);
        var rebased = new List<LinuxDeploymentRecord>();
        foreach (var candidate in candidates)
        {
            var record = candidate.Record;
            if (string.IsNullOrEmpty(candidate.NewRoot))
                return new(false, 0, $"{RepairNoMatchPrefix}{record.BinaryDirectory}");
            if (Directory.Exists(record.BinaryDirectory))
                return new(false, 0, $"{RepairOldFolderExistsPrefix}{record.BinaryDirectory}");
            if (candidates.Count(c => c.Record.Component == record.Component) > 1
                || current.Any(r => r.Component == record.Component && r.BinaryDirectory != record.BinaryDirectory))
                return new(false, 0, $"{RepairDuplicatePrefix}{record.Component}");

            // Every file RHI owns must be found, moved or not: a record left pointing at a missing
            // file would block its own update and removal later.
            foreach (var file in record.Files.Where(IsOwnedHashedFile))
            {
                var mapped = Map(file.Path, candidate);
                if (!File.Exists(mapped))
                    return new(false, 0, $"{RepairMissingPrefix}{mapped}");
                if (!string.Equals(Sha256(mapped), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, 0, $"{RepairChangedPrefix}{mapped}");
            }

            var copy = LinuxDeploymentRecordStore.Clone(record);
            copy.GameKey = key;
            copy.BinaryDirectory = Map(copy.BinaryDirectory, candidate);
            foreach (var file in copy.Files) file.Path = Map(file.Path, candidate);
            copy.DirectoriesCreatedByRhi = copy.DirectoriesCreatedByRhi.Select(d => Map(d, candidate)).ToList();
            foreach (var name in copy.Metadata.Keys.ToArray())
                copy.Metadata[name] = Map(copy.Metadata[name], candidate);
            rebased.Add(copy);
        }

        _records.Replace(candidates.Select(c => c.Record).ToArray(), rebased);
        repaired = candidates;
        return new(true, rebased.Count, $"{RepairDonePrefix}{rebased.Count}");
    }

    // Messages are split before the variable part so the UI can translate the fixed text.
    public const string RepairNoMatchPrefix = "Repair after move cannot tell where this folder went: ";
    public const string RepairOldFolderExistsPrefix = "The old folder still exists, so this looks like a copy rather than a move; remove or rename it, then repair again: ";
    public const string RepairDuplicatePrefix = "More than one record for the same component; remove and reinstall it instead: ";
    public const string RepairMissingPrefix = "Nothing was changed: a file RHI installed is not at the new location: ";
    public const string RepairChangedPrefix = "Nothing was changed: a file RHI installed differs at the new location: ";
    public const string RepairDonePrefix = "Records updated to the game's new folder: ";
    public const string AddonStateNotMovedPrefix = "Addon Manager selections still point to the old folder: ";

    private static bool IsOwnedHashedFile(LinuxDeployedFile file)
        => !file.AllowUserChanges && !string.IsNullOrWhiteSpace(file.Sha256) && !string.IsNullOrWhiteSpace(file.Path);

    private static string Map(string value, RelocatedRecord candidate)
        => !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) && IsUnder(value, candidate.OldRoot)
            ? Rebase(value, candidate.OldRoot, candidate.NewRoot)
            : value;

    private static string Rebase(string path, string oldRoot, string newRoot)
    {
        var relative = Path.GetRelativePath(oldRoot, Path.GetFullPath(path));
        return relative == "." ? newRoot : Path.Combine(newRoot, relative);
    }

    /// <summary>
    /// The current folders a record may legitimately sit in: install root, binary directory, the
    /// manifest's install root and the Wine prefix.
    /// </summary>
    private static IReadOnlyList<string> Anchors(DetectedGame game)
        => new[] { game.InstallPath, game.BinaryDirectory, game.ManifestInstallRoot, game.PrefixPath }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathRooted(p))
            .Select(p => p!)
            .ToArray();

    /// <summary>
    /// Maps the record's folder onto the game's current one by their longest common trailing path
    /// (e.g. /a/steamapps/common/G/bin → /b/steamapps/common/G/bin). The mapped root keeps the first
    /// common segment, so siblings that moved with it (steamapps/compatdata) map too, while paths
    /// outside it (RHI's own data under $HOME) are left alone.
    /// </summary>
    private static bool TryMap(LinuxDeploymentRecord record, DetectedGame game, out string oldRoot, out string newRoot)
    {
        oldRoot = newRoot = "";
        var old = Segments(record.BinaryDirectory);
        var best = 0;
        foreach (var anchor in new[] { game.BinaryDirectory, game.InstallPath })
        {
            if (string.IsNullOrWhiteSpace(anchor)) continue;
            var current = Segments(anchor);
            var common = 0;
            while (common < old.Length && common < current.Length
                   && string.Equals(old[^(common + 1)], current[^(common + 1)], StringComparison.Ordinal))
                common++;
            if (common <= best || common >= old.Length || common >= current.Length) continue;
            best = common;
            oldRoot = Join(old[..(old.Length - common + 1)]);
            newRoot = Join(current[..(current.Length - common + 1)]);
        }
        return best > 0;
    }

    private static string[] Segments(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string[] segments) => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    /// <summary>Path containment on resolved paths, so a library reached through a symlink is not "moved".</summary>
    internal static bool IsUnder(string path, string root)
    {
        var p = Canonical(path);
        var r = Canonical(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.Ordinal)
               || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length == 0) return Path.DirectorySeparatorChar.ToString();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return full;
        // Resolve the deepest existing ancestor; the missing tail is appended unchanged.
        var existing = full;
        var tail = "";
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) return full;
            tail = Path.Combine(Path.GetFileName(existing), tail);
            existing = parent;
        }
        var resolved = RealPath(existing) ?? existing;
        return tail.Length == 0 ? resolved : Path.Combine(resolved, tail).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string? RealPath(string path)
    {
        var buffer = realpath(path, IntPtr.Zero);
        if (buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(buffer); }
        finally { free(buffer); }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
