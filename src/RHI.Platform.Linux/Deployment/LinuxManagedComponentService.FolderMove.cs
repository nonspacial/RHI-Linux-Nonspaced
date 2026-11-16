using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What an install-folder move did (English platform text; the UI presents it).</summary>
/// <param name="Success">Everything moved and the records follow the new folder.</param>
/// <param name="NothingToMove">The old folder holds no RHI install of the game: nothing was changed.</param>
/// <param name="Components">The components whose installs moved.</param>
public sealed record InstallFolderMoveResult(bool Success, bool NothingToMove, string Message, IReadOnlyList<ManagedComponentKind> Components)
{
    public static InstallFolderMoveResult Refused(string message) => new(false, false, message, []);
}

/// <summary>
/// #293 (upstream 2.9.2 install folder migration, <c>InstallFolderMigrationService</c> / <c>InstallFolderMover</c>): moves a
/// game's RHI installs — mods, their settings and presets — from the folder they were installed in to the game's new one
/// after Change install folder (Edit game), Reset install folder (Restore automatic detection) or a folder RHI corrected
/// itself (the manifest's <c>installPathOverrides</c> / <c>installPathMigrations</c>).
/// <list type="bullet">
/// <item>What moves comes from the deployment records (Linux has no per-component inventory to rebuild): every record whose
/// binary directory is the old folder, each file of it under the old folder, plus the ReShade presets its record lists.
/// A file RHI did not place (it existed before the install and has no <c>.original</c> copy beside it: a game or user file
/// RHI edited in place) stays where it is, as do files outside the old folder.</item>
/// <item>Nothing is touched when the game is running, a file RHI owns changed since it was installed or is missing, the
/// new folder already holds a file and its <c>.original</c> copy, another component RHI installed already uses a target
/// name there, a component's Wine override names another executable, or an NR Cost Scaler sits over a component.</item>
/// <item>Each file is copied to the new folder, verified, and only then removed from the old one; a game file the move
/// replaces in the new folder is kept as <c>.original</c> (as an install would), and a game file RHI had kept as
/// <c>.original</c> in the old folder is put back. Every step is journaled (start-up recovery undoes a crash) and any failure
/// rolls the whole move back. The records (re-pointed, re-keyed when the game's key changed) and the game's per-game
/// settings are written in one staged commit, which is the move's commit point.</item>
/// </list>
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The journal folder moves write ahead to (set by the app's composition; null = rolled back in process only).</summary>
    public DeploymentJournal? FolderMoveJournal { get; set; }

    /// <summary>Whether a game is running (a move never runs then).</summary>
    public IGameRunningProbe FolderMoveRunningProbe { get; set; } = new LinuxGameRunningProbe();

    /// <summary>Test seam: runs before each file is moved; throwing there fails the move part-way.</summary>
    internal Action<string>? BeforeFolderMoveFileForTesting { get; set; }

    public const string FolderMoveRunningMessage = "Nothing was moved: the game is running. Close it and try again.";
    public const string FolderMoveMissingPrefix = "Nothing was moved: a file RHI installed is missing: ";
    public const string FolderMoveChangedPrefix = "Nothing was moved: a file RHI installed changed since it was installed: ";
    public const string FolderMoveTakenPrefix = "Nothing was moved: the new folder already has this file and its .original copy: ";
    public const string FolderMoveHeldPrefix = "Nothing was moved: another component RHI installed already uses this file in the new folder: ";
    public const string FolderMoveWineOverridePrefix = "Nothing was moved: this component's Wine override names another executable; remove it and install it again in the new folder: ";
    public const string FolderMoveNestedPrefix = "Nothing was moved: a file RHI installed is already inside the new folder: ";
    public const string FolderMoveLayerMessage = "Nothing was moved: remove NR Cost Scaler first, then move the game's folder.";
    public const string FolderMoveFailedPrefix = "The move failed and was undone; nothing was changed: ";
    public const string FolderMoveDonePrefix = "Moved to the new folder: ";
    public const string FolderMoveNothingMessage = "RHI has nothing installed in the old folder.";

    /// <summary>The game's records installed in <paramref name="folder"/> (its binary directory when they were installed).</summary>
    public IReadOnlyList<LinuxDeploymentRecord> InstallsIn(DetectedGame game, string? folder)
        => string.IsNullOrWhiteSpace(folder) ? []
            : _records.FindAll(game).Where(record => !string.IsNullOrWhiteSpace(record.BinaryDirectory) && PathIdentity.AreSame(record.BinaryDirectory, folder)).ToArray();

    /// <summary>
    /// The folder the game's installs sit in when it is not the game's binary directory any more (its folder changed), or
    /// null. Read only; no hashing.
    /// </summary>
    public string? InstallFolderLeftBehind(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory)) return null;
        return _records.FindAll(game)
            .Where(record => record.Component is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline or ManagedComponentKind.ReShadeAddonManager))
            .Select(record => record.BinaryDirectory)
            .Where(folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !PathIdentity.AreSame(folder, game.BinaryDirectory!))
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private sealed record FileMove(string Source, string Target, string? Sha256, bool TargetTaken, bool SourceHasBackup);

    private sealed record FolderMovePlan(
        IReadOnlyList<LinuxDeploymentRecord> Old, IReadOnlyList<LinuxDeploymentRecord> New, IReadOnlyList<FileMove> Moves,
        IReadOnlyList<string> NewDirectories, IReadOnlyList<string> OldDirectories);

    /// <summary>
    /// Moves <paramref name="oldGame"/>'s installs from <paramref name="oldDir"/> to <paramref name="newDir"/> and re-points
    /// their records at <paramref name="newGame"/> (its folders and key after the change). <paramref name="stores"/> are the
    /// UI's per-game stores (besides this service's own) that follow a changed key; <paramref name="alongside"/> stages
    /// further files written in the same commit (Edit game's library entry).
    /// </summary>
    public async Task<InstallFolderMoveResult> MoveInstallFolderAsync(DetectedGame oldGame, DetectedGame newGame, string oldDir, string newDir,
        IReadOnlyList<IGameStateStore>? stores = null, Func<StagedFileWrite?>? alongside = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldGame);
        ArgumentNullException.ThrowIfNull(newGame);
        oldDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldDir));
        newDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDir));
        if (PathIdentity.AreSame(oldDir, newDir) || InstallsIn(oldGame, oldDir) is not { Count: > 0 })
            return new(false, true, FolderMoveNothingMessage, []);
        if (!Directory.Exists(newDir)) return InstallFolderMoveResult.Refused(FolderMoveMissingPrefix + newDir);
        if (XboxWriteGate.Refusal(oldGame) is { } xbox) return InstallFolderMoveResult.Refused(xbox);
        if (FolderMoveRunningProbe.IsRunning(oldGame) || FolderMoveRunningProbe.IsRunning(newGame))
            return InstallFolderMoveResult.Refused(FolderMoveRunningMessage);
        var plan = PlanFolderMove(oldGame, newGame, oldDir, newDir, out var refusal);
        if (plan is null) return InstallFolderMoveResult.Refused(refusal!);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            FolderMoveJournal, "Install folder move: " + newGame.Name, newGame, plan.New[0].Component, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return InstallFolderMoveResult.Refused(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        try
        {
            foreach (var directory in plan.NewDirectories.Concat(plan.Moves.Select(m => Path.GetDirectoryName(m.Target)!)).Distinct(StringComparer.Ordinal))
                CreateDirectoryTracked(directory, created);
            foreach (var move in plan.Moves)
            {
                BeforeFolderMoveFileForTesting?.Invoke(move.Source);
                // The old file (and the game's own copy kept beside it) are snapshotted before anything changes.
                await scope.TrackAsync(move.Source, cancellationToken).ConfigureAwait(false);
                if (move.TargetTaken)
                {
                    // A game or user file of that name in the new folder is kept as .original, as an install would.
                    await scope.TrackAsync(move.Target, cancellationToken).ConfigureAwait(false);
                    await scope.TrackAsync(move.Target + TransactionalFileDeploymentService.BackupExtension, cancellationToken).ConfigureAwait(false);
                    await scope.DeclareWriteAsync(move.Target + TransactionalFileDeploymentService.BackupExtension,
                        await FileHash.Sha256HexAsync(move.Target, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    AtomicFile.Rename(move.Target, move.Target + TransactionalFileDeploymentService.BackupExtension);
                }
                // Copy, verify (the copy is hashed against the source), then remove the old one.
                await scope.PlaceCopyAsync(move.Source, move.Target, cancellationToken, move.Sha256, overwrite: false).ConfigureAwait(false);
                AtomicFile.Delete(move.Source);
                if (move.SourceHasBackup)
                {
                    // The game's own file RHI kept as .original returns to its place in the old folder.
                    var backup = move.Source + TransactionalFileDeploymentService.BackupExtension;
                    await scope.DeclareWriteAsync(move.Source, await FileHash.Sha256HexAsync(backup, CancellationToken.None).ConfigureAwait(false), cancellationToken)
                        .ConfigureAwait(false);
                    AtomicFile.Rename(backup, move.Source);
                }
            }

            // The commit point: the re-pointed records (stamped) and the game's per-game settings, all written or none.
            var stateMove = new GameStateMove(newGame, [(oldDir, newDir)], [LinuxDeploymentRecordStore.GameKey(oldGame)]);
            var writes = new List<StagedFileWrite> { _records.StageReplace(plan.Old, plan.New.Select(scope.Stamp).ToArray()) };
            foreach (var store in RepairStores(stores).Distinct())
                if (store.StageMove(stateMove) is { } write) writes.Add(write);
            if (alongside?.Invoke() is { } extra) writes.Add(extra);
            try { StagedFileCommit.Commit(writes); }
            finally { _records.InvalidateCache(); }
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return Done(plan);
            for (var i = created.Count - 1; i >= 0; i--) TryRemoveEmptyDirectory(created[i]);
            AppLog.Warn("folder-move", $"Moving {newGame.Name}'s installs from {oldDir} to {newDir} failed", ex);
            return InstallFolderMoveResult.Refused(FolderMoveFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        // Folders RHI had created in the old folder go once they are empty (deepest first); the old folder itself stays.
        foreach (var directory in plan.OldDirectories.OrderByDescending(d => d.Length)) TryRemoveEmptyDirectory(directory);
        return Done(plan);

        static InstallFolderMoveResult Done(FolderMovePlan plan)
        {
            var components = plan.New.Select(r => r.Component).Distinct().ToArray();
            return new(true, false, FolderMoveDonePrefix + string.Join(", ", components.Select(DisplayDependencyName)), components);
        }
    }

    private FolderMovePlan? PlanFolderMove(DetectedGame oldGame, DetectedGame newGame, string oldDir, string newDir, out string? refusal)
    {
        refusal = null;
        var all = _records.FindAll(oldGame);
        if (all.Any(r => r.Component == ManagedComponentKind.NrCostScaler))
        {
            refusal = FolderMoveLayerMessage;
            return null;
        }
        var moving = InstallsIn(oldGame, oldDir);
        var staying = all.Except(moving).ToArray();
        var newKey = LinuxDeploymentRecordStore.GameKey(newGame);
        var newExe = string.IsNullOrWhiteSpace(newGame.ExecutablePath) ? null : Path.GetFileName(newGame.ExecutablePath);
        var newDirInsideOld = PathIdentity.IsSameOrInside(newDir, oldDir);
        var moves = new Dictionary<string, FileMove>(StringComparer.Ordinal);
        var newRecords = new List<LinuxDeploymentRecord>();
        var newDirectories = new List<string>();
        var oldDirectories = new List<string>();
        string Map(string path) => Rebase(path, oldDir, newDir);
        bool Under(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && PathIdentity.IsSameOrInside(path, oldDir);

        foreach (var record in moving)
        {
            foreach (var wine in new[] { record.WineOverride }.Concat(record.AdditionalWineOverrides ?? []).OfType<WineOverrideRecord>())
                if (!string.IsNullOrWhiteSpace(wine.ApplicationName) && !string.Equals(wine.ApplicationName, newExe, StringComparison.OrdinalIgnoreCase))
                {
                    refusal = FolderMoveWineOverridePrefix + DisplayDependencyName(record.Component);
                    return null;
                }
            var copy = LinuxDeploymentRecordStore.Clone(record);
            copy.GameKey = newKey;
            copy.GameName = newGame.Name;
            copy.BinaryDirectory = newDir;
            foreach (var file in copy.Files.Where(f => Under(f.Path)))
            {
                var source = Path.GetFullPath(file.Path);
                var backup = source + TransactionalFileDeploymentService.BackupExtension;
                // A game or user file RHI edited in place (it was there before, and RHI kept no copy of it) stays.
                if (file.ExistedBeforeInstall && !File.Exists(backup)) continue;
                if (newDirInsideOld && PathIdentity.IsSameOrInside(source, newDir))
                {
                    refusal = FolderMoveNestedPrefix + source;
                    return null;
                }
                var target = Map(source);
                if (!File.Exists(source))
                {
                    if (!file.AllowUserChanges)
                    {
                        refusal = FolderMoveMissingPrefix + source;
                        return null;
                    }
                    file.Path = target; // a settings file the user deleted: the record follows the folder
                    continue;
                }
                if (!file.AllowUserChanges && !string.IsNullOrWhiteSpace(file.Sha256) && !FileHash.HexEquals(FileHash.Sha256Hex(source), file.Sha256))
                {
                    refusal = FolderMoveChangedPrefix + source;
                    return null;
                }
                if (moves.TryGetValue(source, out var planned))
                {
                    file.Path = planned.Target; // listed by two records (shared files): moved once
                    file.ExistedBeforeInstall = planned.TargetTaken;
                    continue;
                }
                var taken = File.Exists(target);
                if (taken && staying.SelectMany(r => r.Files).Any(f => !string.IsNullOrWhiteSpace(f.Path) && PathIdentity.AreSame(f.Path, target)))
                {
                    refusal = FolderMoveHeldPrefix + target;
                    return null;
                }
                if (taken && File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
                {
                    refusal = FolderMoveTakenPrefix + target;
                    return null;
                }
                moves[source] = new FileMove(source, target, FileHash.Sha256Hex(source), taken, File.Exists(backup));
                file.Path = target;
                file.ExistedBeforeInstall = taken;
            }
            copy.DirectoriesCreatedByRhi = copy.DirectoriesCreatedByRhi.Select(directory =>
            {
                if (!Under(directory)) return directory;
                oldDirectories.Add(Path.GetFullPath(directory));
                var mapped = Map(directory);
                if (Directory.Exists(mapped)) return null; // already there: not RHI's in the new folder
                newDirectories.Add(mapped);
                return mapped;
            }).OfType<string>().ToList();
            foreach (var key in copy.Metadata.Keys.ToArray())
                if (Under(copy.Metadata[key])) copy.Metadata[key] = Map(copy.Metadata[key]);
            // ReShade presets RHI put beside ReShade (#180): listed by file name on its record, so they follow the folder.
            if (record.Component == ManagedComponentKind.ReShade && record.Metadata.TryGetValue(ReShadePresetService.MetadataKey, out var presets))
            {
                foreach (var preset in ReadPresetNames(presets))
                {
                    var source = Path.Combine(oldDir, preset);
                    if (!File.Exists(source) || moves.ContainsKey(source)) continue;
                    var target = Path.Combine(newDir, preset);
                    if (File.Exists(target))
                    {
                        refusal = FolderMoveTakenPrefix + target;
                        return null;
                    }
                    moves[source] = new FileMove(source, target, FileHash.Sha256Hex(source), false, false);
                }
            }
            newRecords.Add(copy);
        }
        // Each target is reached once: two old files mapping onto one new name would overwrite each other.
        if (moves.Values.GroupBy(m => m.Target, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            refusal = FolderMoveTakenPrefix + clash.Key;
            return null;
        }
        return new FolderMovePlan(moving, newRecords, moves.Values.ToArray(), newDirectories, oldDirectories);
    }

    private static IReadOnlyList<string> ReadPresetNames(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<InstalledReShadePreset>>(json) ?? [])
                .Select(p => p.FileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name)
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static string Rebase(string path, string oldRoot, string newRoot)
    {
        var relative = Path.GetRelativePath(oldRoot, Path.GetFullPath(path));
        return relative == "." ? newRoot : Path.Combine(newRoot, relative);
    }

    private static void CreateDirectoryTracked(string directory, List<string> created)
    {
        if (Directory.Exists(directory)) return;
        CreateDirectoryTracked(Path.GetDirectoryName(directory)!, created);
        Directory.CreateDirectory(directory);
        created.Add(directory);
    }

    private static void TryRemoveEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("folder-move", $"Could not remove the empty folder {directory}", ex);
        }
    }
}
