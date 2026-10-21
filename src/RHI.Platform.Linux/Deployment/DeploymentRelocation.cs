using System.Text.Json;
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
/// <item>A moved game with no records whose per-game entries still name its old folder (an Edit game patch
/// target, <see cref="IGameStateStore.MovedPaths"/>) is repaired the same way without records: every named path must
/// be found under the game's current folder (Refs #147).</item>
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

    /// <summary>
    /// True when "Repair after move" has something to do for this game (cheap: no hashing): records left at its old
    /// folder, or entries in <paramref name="stores"/> that still name it (<see cref="IGameStateStore.MovedPaths"/>).
    /// </summary>
    public bool NeedsRepair(DetectedGame game, IReadOnlyList<IGameStateStore>? stores = null)
        => FindMoved(game).Count > 0 || FindAdoptable(game).Count > 0 || MovedPaths(game, stores ?? []).Count > 0;

    private static IReadOnlyList<string> MovedPaths(DetectedGame game, IReadOnlyList<IGameStateStore> stores)
        => stores.Distinct().SelectMany(store => store.MovedPaths(game))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
            if (anchors.Any(anchor => IsUnder(record.BinaryDirectory, anchor)))
            {
                // w7/reshade (#201, review 3): an XNA ReShade record's GAC copies live in the prefix, which moved on its own.
                if (XnaGacReShade.MovedFolder(record, null, game) is { } gac)
                    result.Add(new RelocatedRecord(record, gac.Old, gac.New, Adopted: false));
                continue;
            }
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
        // Refs #90: the owned files are hashed and the records re-pointed only while the installer app leaves the title alone.
        if (XboxWriteGate.Refusal(game) is { } xbox) return new(false, 0, xbox); // w3/scan hook
        var candidates = Preview(game).ToList();
        if (candidates.Count == 0)
            return RepairStoresOnly(game, stores);

        var oldFolders = candidates.Where(c => c.Adopted).Select(c => c.Record.GameKey).Distinct(StringComparer.Ordinal).ToArray();
        if (oldFolders.Length > 1)
            return new(false, 0, RepairAmbiguousPrefix + string.Join(", ", oldFolders));

        var key = LinuxDeploymentRecordStore.GameKey(game);
        var current = _records.FindAll(game);
        // w3/parity (#96): a file another record shares with an NR Cost Scaler record holds the scaler's proxy; the
        // record's own content is beside it under the scaler's _real name (NrCostScalerService.ContentPath).
        var gameRecords = candidates.Select(c => c.Record).Concat(current).ToArray();
        var rebased = new List<LinuxDeploymentRecord>();
        foreach (var candidate in candidates)
        {
            var record = candidate.Record;
            if (string.IsNullOrEmpty(candidate.NewRoot))
                return new(false, 0, $"{RepairNoMatchPrefix}{record.BinaryDirectory}");
            if (candidates.Count(c => c.Record.Component == record.Component) > 1
                || current.Any(r => r.Component == record.Component && r.BinaryDirectory != record.BinaryDirectory))
                return new(false, 0, $"{RepairDuplicatePrefix}{record.Component}");

            // w7/reshade (#201, review 3): XNA ReShade's GAC copies follow the prefix when it moved apart from the game folder.
            var gac = XnaGacReShade.MovedFolder(record, path => Map(path, candidate), game);
            string MapPath(string value)
            {
                var mapped = Map(value, candidate);
                return gac is { } moved && Path.IsPathRooted(mapped) && IsUnder(mapped, moved.Old) ? Rebase(mapped, moved.Old, moved.New) : mapped;
            }

            // Every file RHI owns must be found, moved or not: a record left pointing at a missing
            // file would block its own update and removal later.
            foreach (var file in record.Files.Where(IsOwnedHashedFile))
            {
                var mapped = MapPath(NrCostScalerService.ContentPath(record, file, gameRecords));
                if (!File.Exists(mapped))
                    return new(false, 0, $"{RepairMissingPrefix}{mapped}");
                if (!string.Equals(FileHash.Sha256Hex(mapped), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, 0, $"{RepairChangedPrefix}{mapped}");
            }

            // The old folder may still exist (an emptied leftover, or a backup copy). Only one
            // that still holds RHI's files unchanged is a live copy RHI cannot choose between.
            if (Directory.Exists(record.BinaryDirectory) && StillHoldsOwnedFiles(record, MapPath, gameRecords))
                return new(false, 0, $"{RepairOldFolderExistsPrefix}{record.BinaryDirectory}{RepairOldFolderExistsSuffix}");

            var copy = LinuxDeploymentRecordStore.Clone(record);
            copy.GameKey = key;
            copy.BinaryDirectory = MapPath(copy.BinaryDirectory);
            foreach (var file in copy.Files) file.Path = MapPath(file.Path);
            copy.DirectoriesCreatedByRhi = copy.DirectoriesCreatedByRhi.Select(MapPath).ToList();
            foreach (var name in copy.Metadata.Keys.ToArray())
                copy.Metadata[name] = MapPath(copy.Metadata[name]);
            // The Control UE step's state is a JSON blob of paths (DLLs, renderer.ini, the user-kept list): rebase inside it,
            // or the next update applies the step again over the user's own DLL/preset in the new folder.
            if (copy.Metadata.TryGetValue(ControlUePostInstallService.MetadataKey, out var controlUe) && !string.IsNullOrWhiteSpace(controlUe))
            {
                try { copy.Metadata[ControlUePostInstallService.MetadataKey] = ControlUePostInstallService.RebasePaths(controlUe, MapPath); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    return new(false, 0, $"{RepairNotSavedPrefix}{ex.Message}");
                }
            }
            rebased.Add(copy);
        }

        // Stage the ledger and every per-game store first: nothing is written unless all can be.
        var move = new GameStateMove(game, candidates.Select(c => (c.OldRoot, c.NewRoot)), oldFolders);
        if (Commit(move, stores, _records.StageReplace(candidates.Select(c => c.Record).ToArray(), rebased)) is { } failed)
            return failed;

        repaired = candidates;
        return new(true, rebased.Count, $"{RepairDonePrefix}{rebased.Count}");
    }

    /// <summary>
    /// A moved game without RHI records whose per-game entries still name its old folder (an Edit game patch target):
    /// re-points them once every named path is found under the game's current folder (Refs #147). Only the stores
    /// holding those entries take part: no record key changes here, so the others have nothing of this game to move
    /// (and one of them being unreadable must not block it).
    /// </summary>
    private RelocationRepairResult RepairStoresOnly(DetectedGame game, IReadOnlyList<IGameStateStore> stores)
    {
        IReadOnlyList<IGameStateStore> involved;
        IReadOnlyList<string> paths;
        try
        {
            involved = stores.Distinct().Where(store => MovedPaths(game, [store]).Count > 0).ToArray();
            paths = MovedPaths(game, involved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, 0, RepairNotSavedPrefix + ex.Message);
        }
        if (paths.Count == 0)
            return new(false, 0, "Nothing to repair: RHI's records for this game already match its folder.");

        var roots = new List<(string OldRoot, string NewRoot)>();
        foreach (var path in paths)
        {
            if (!TryMapPath(path, game, out var oldRoot, out var newRoot))
                return new(false, 0, $"{RepairNoMatchPrefix}{path}");
            roots.Add((oldRoot, newRoot));
        }
        var move = new GameStateMove(game, roots, []);
        foreach (var path in paths)
        {
            var mapped = move.MapPath(path);
            if (!Path.Exists(mapped))
                return new(false, 0, $"{RepairMissingAfterMovePrefix}{mapped}");
        }
        return Commit(move, involved) ?? new(true, 0, RepairSettingsDone);
    }

    /// <summary>Stages <paramref name="records"/> (if any) and every store's part of <paramref name="move"/>, then writes them all or none; null when written.</summary>
    private RelocationRepairResult? Commit(GameStateMove move, IReadOnlyList<IGameStateStore> stores, StagedFileWrite? records = null)
    {
        var writes = new List<StagedFileWrite>();
        try
        {
            if (records is not null) writes.Add(records);
            foreach (var store in stores.Distinct())
                if (store.StageMove(move) is { } write)
                    writes.Add(write);
            StagedFileCommit.Commit(writes);
        }
        catch (StagedFileCommitException ex) when (ex.NotRestored.Count > 0)
        {
            return new(false, 0, RepairPartlyWrittenPrefix + ex.Message);
        }
        catch (StagedFileCommitException ex)
        {
            // Rolled back cleanly: the commit's own "Nothing was changed: " would repeat this message's prefix.
            return new(false, 0, RepairNotSavedPrefix + ex.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, 0, RepairNotSavedPrefix + ex.Message);
        }
        finally
        {
            _records.InvalidateCache();
        }
        return null;
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
    public const string RepairMissingAfterMovePrefix = "Nothing was changed: the folder or file the game's settings name is not at the new location: ";
    public const string RepairSettingsDone = "The game's settings now point to its new folder.";

    private static bool IsOwnedHashedFile(LinuxDeployedFile file)
        => !file.AllowUserChanges && !string.IsNullOrWhiteSpace(file.Sha256) && !string.IsNullOrWhiteSpace(file.Path);

    /// <summary>
    /// True when any owned file is still at its old (unmapped) path with its recorded hash; an NR runtime owner's content
    /// is read where it really is under an NR Cost Scaler (<see cref="NrCostScalerService.ContentPath"/>).
    /// </summary>
    private static bool StillHoldsOwnedFiles(LinuxDeploymentRecord record, Func<string, string> map, IReadOnlyList<LinuxDeploymentRecord> gameRecords)
        => record.Files.Where(IsOwnedHashedFile)
            .Where(f => !string.Equals(map(f.Path), f.Path, StringComparison.Ordinal))
            .Select(f => (Content: NrCostScalerService.ContentPath(record, f, gameRecords), f.Sha256))
            .Any(f => File.Exists(f.Content) && string.Equals(FileHash.Sha256Hex(f.Content), f.Sha256, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Maps <paramref name="path"/>, a path under the game's old folder (deeper or not), onto the game's current folder
    /// by the longest run of trailing folder names they share (e.g. /a/steamapps/common/G/Game → /b/steamapps/common/G/Game
    /// for a game now at /b/steamapps/common/G). Roots follow <see cref="TryMap"/>'s convention.
    /// </summary>
    internal static bool TryMapPath(string path, DetectedGame game, out string oldRoot, out string newRoot)
    {
        oldRoot = newRoot = "";
        var old = Segments(path);
        var best = 0;
        foreach (var anchor in new[] { game.LibraryIdentityPath, game.InstallPath, game.BinaryDirectory })
        {
            if (string.IsNullOrWhiteSpace(anchor) || !Path.IsPathRooted(anchor)) continue;
            var current = Segments(anchor);
            for (var length = old.Length; length > 0; length--)
            {
                var common = 0;
                while (common < length && common < current.Length
                       && string.Equals(old[length - 1 - common], current[^(common + 1)], StringComparison.Ordinal))
                    common++;
                if (common <= best || common >= length || common >= current.Length) continue;
                best = common;
                oldRoot = Join(old[..(length - common + 1)]);
                newRoot = Join(current[..(current.Length - common + 1)]);
            }
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
