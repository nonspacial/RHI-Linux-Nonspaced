using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
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
/// with the same SHA-256, and only then rewrites the record, together with the game's per-game
/// preferences, library entries (favourite, hidden, edits, HDR) and update history, all in one
/// staged commit. When the old folder still exists it must no longer hold RHI's files with their
/// recorded hashes (a leftover); otherwise it is a live copy and RHI says how to proceed.</item>
/// <item>Games without a store id are keyed by folder, so a moved one has no record under its key;
/// the same repair adopts a folder-keyed record only on strong evidence: the same game name, the old
/// install folder mapping exactly onto the current one (same folder name and layout down to the
/// binary directory), the same game executable where the record names it, one candidate old folder
/// only, and every owned file verifying at the new location. The UI also asks the user to confirm
/// the old and new folders before adopting.</item>
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

    /// <summary>What a repair would touch (moved and adoptable records), for the confirmation the UI shows.</summary>
    public IReadOnlyList<RelocatedRecord> Preview(DetectedGame game) => [.. FindMoved(game), .. FindAdoptable(game)];

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

    /// <summary>
    /// Folder-keyed records of this game left at its previous folder (see the class summary for the
    /// evidence required). Cheap: no hashing.
    /// </summary>
    public IReadOnlyList<RelocatedRecord> FindAdoptable(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(game.InstallPath) || Anchors(game).Count == 0) return [];
        var installPath = Trim(game.InstallPath);
        var exeName = string.IsNullOrWhiteSpace(game.ExecutablePath) ? null : Path.GetFileName(game.ExecutablePath);
        var result = new List<RelocatedRecord>();
        foreach (var record in _records.All())
        {
            if (GameRecordIdentity.IsStoreKey(record.GameKey) || GameRecordIdentity.Matches(record.GameKey, game)) continue;
            if (!Path.IsPathRooted(record.GameKey)) continue;
            if (!string.Equals(record.GameName, game.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(record.BinaryDirectory)) continue;
            if (!TryMap(record, game, out var oldRoot, out var newRoot)) continue;
            // The whole game folder moved, not just a same-named sub-folder ("bin", "Win64"): the
            // record's old install folder lands exactly on the game's current one.
            if (!IsUnder(record.GameKey, oldRoot) || !string.Equals(Trim(Rebase(record.GameKey, oldRoot, newRoot)), installPath, StringComparison.Ordinal))
                continue;
            // The Wine override names the executable RHI configured: it must be this game's.
            var recordedExe = RecordedExecutables(record);
            if (recordedExe.Count > 0 && (exeName is null || !recordedExe.Contains(exeName, StringComparer.OrdinalIgnoreCase)))
                continue;
            var hashed = record.Files.Where(f => IsOwnedHashedFile(f) && IsUnder(f.Path, oldRoot)).ToArray();
            if (hashed.Length == 0 || !hashed.All(f => File.Exists(Rebase(f.Path, oldRoot, newRoot)))) continue;
            // An old folder that still holds RHI's files is another live install of this game.
            if (Directory.Exists(record.BinaryDirectory) && hashed.Any(f => File.Exists(f.Path))) continue;
            result.Add(new RelocatedRecord(record, oldRoot, newRoot, Adopted: true));
        }
        return result;
    }

    /// <summary>
    /// Rewrites the game's moved and adoptable records to its current location after checking every
    /// owned file there, and carries the game's entries in <paramref name="stores"/> over with them.
    /// All or nothing: one failed check, or one store that cannot be read or written, leaves every
    /// record and every store untouched.
    /// </summary>
    public RelocationRepairResult Repair(DetectedGame game, IReadOnlyList<IGameStateStore> stores, out IReadOnlyList<RelocatedRecord> repaired)
    {
        ArgumentNullException.ThrowIfNull(stores);
        repaired = [];
        var candidates = Preview(game).ToList();
        if (candidates.Count == 0)
            return new(false, 0, "Nothing to repair: RHI's records for this game already match its folder.");

        var oldFolders = candidates.Where(c => c.Adopted).Select(c => c.Record.GameKey).Distinct(StringComparer.Ordinal).ToArray();
        if (oldFolders.Length > 1)
            return new(false, 0, RepairAmbiguousPrefix + string.Join(", ", oldFolders));

        var key = LinuxDeploymentRecordStore.GameKey(game);
        var current = _records.FindAll(game);
        var rebased = new List<LinuxDeploymentRecord>();
        foreach (var candidate in candidates)
        {
            var record = candidate.Record;
            if (string.IsNullOrEmpty(candidate.NewRoot))
                return new(false, 0, $"{RepairNoMatchPrefix}{record.BinaryDirectory}");
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
                if (!string.Equals(FileHash.Sha256Hex(mapped), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, 0, $"{RepairChangedPrefix}{mapped}");
            }

            // The old folder may still exist (an emptied leftover, or a backup copy). Only one
            // that still holds RHI's files unchanged is a live copy RHI cannot choose between.
            if (Directory.Exists(record.BinaryDirectory) && StillHoldsOwnedFiles(record, candidate))
                return new(false, 0, $"{RepairOldFolderExistsPrefix}{record.BinaryDirectory}{RepairOldFolderExistsSuffix}");

            var copy = LinuxDeploymentRecordStore.Clone(record);
            copy.GameKey = key;
            copy.BinaryDirectory = Map(copy.BinaryDirectory, candidate);
            foreach (var file in copy.Files) file.Path = Map(file.Path, candidate);
            copy.DirectoriesCreatedByRhi = copy.DirectoriesCreatedByRhi.Select(d => Map(d, candidate)).ToList();
            foreach (var name in copy.Metadata.Keys.ToArray())
                copy.Metadata[name] = Map(copy.Metadata[name], candidate);
            rebased.Add(copy);
        }

        // Stage the ledger and every per-game store first: nothing is written unless all can be.
        var move = new GameStateMove(game, candidates.Select(c => (c.OldRoot, c.NewRoot)), oldFolders);
        var writes = new List<StagedFileWrite>();
        try
        {
            writes.Add(_records.StageReplace(candidates.Select(c => c.Record).ToArray(), rebased));
            foreach (var store in stores.Distinct())
                if (store.StageMove(move) is { } write)
                    writes.Add(write);
            StagedFileCommit.Commit(writes);
        }
        catch (StagedFileCommitException ex) when (ex.NotRestored.Count > 0)
        {
            return new(false, 0, RepairPartlyWrittenPrefix + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, 0, RepairNotSavedPrefix + ex.Message);
        }
        finally
        {
            _records.InvalidateCache();
        }

        repaired = candidates;
        return new(true, rebased.Count, $"{RepairDonePrefix}{rebased.Count}");
    }

    // Messages are split before the variable part so the UI can translate the fixed text.
    public const string RepairNoMatchPrefix = "Repair after move cannot tell where this folder went: ";
    public const string RepairOldFolderExistsPrefix = "Nothing was changed: the old folder still holds the files RHI installed, so RHI cannot tell which copy the game uses: ";
    public const string RepairOldFolderExistsSuffix = ". If that copy is no longer needed, delete it; to keep it as a backup, rename or move it. Then choose Repair after move again.";
    public const string RepairAmbiguousPrefix = "Nothing was changed: RHI records from more than one earlier folder match this game, so it cannot tell which are its own. Install the components again instead. Folders: ";
    public const string RepairDuplicatePrefix = "More than one record for the same component; remove and reinstall it instead: ";
    public const string RepairMissingPrefix = "Nothing was changed: a file RHI installed is not at the new location: ";
    public const string RepairChangedPrefix = "Nothing was changed: a file RHI installed differs at the new location: ";
    public const string RepairNotSavedPrefix = "Nothing was changed: the game's records and settings could not all be updated. ";
    public const string RepairPartlyWrittenPrefix = "Repair after move failed part-way: ";
    public const string RepairDonePrefix = "Records updated to the game's new folder: ";

    private static bool IsOwnedHashedFile(LinuxDeployedFile file)
        => !file.AllowUserChanges && !string.IsNullOrWhiteSpace(file.Sha256) && !string.IsNullOrWhiteSpace(file.Path);

    /// <summary>True when any owned file is still at its old (unmapped) path with its recorded hash.</summary>
    private static bool StillHoldsOwnedFiles(LinuxDeploymentRecord record, RelocatedRecord candidate)
        => record.Files.Where(IsOwnedHashedFile)
            .Where(f => !string.Equals(Map(f.Path, candidate), f.Path, StringComparison.Ordinal))
            .Any(f => File.Exists(f.Path) && string.Equals(FileHash.Sha256Hex(f.Path), f.Sha256, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> RecordedExecutables(LinuxDeploymentRecord record)
        => new[] { record.WineOverride }.Concat(record.AdditionalWineOverrides ?? [])
            .Select(o => o?.ApplicationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Map(string value, RelocatedRecord candidate)
        => !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) && IsUnder(value, candidate.OldRoot)
            ? Rebase(value, candidate.OldRoot, candidate.NewRoot)
            : value;

    private static string Rebase(string path, string oldRoot, string newRoot)
    {
        var relative = Path.GetRelativePath(oldRoot, Path.GetFullPath(path));
        return relative == "." ? newRoot : Path.Combine(newRoot, relative);
    }

    private static string Trim(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : "/";

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
    internal static bool IsUnder(string path, string root) => PathIdentity.IsSameOrInside(path, root);
}
