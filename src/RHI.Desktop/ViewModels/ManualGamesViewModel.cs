using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>What manual add / edit / remove needs from the shell beyond <see cref="IPanelHost"/>: the library copy rules.</summary>
internal interface ILibraryEditHost : IPanelHost
{
    /// <summary>A copy of the in-memory library state to change (an unreadable file is read again first).</summary>
    LinuxGameLibraryState WorkingLibraryCopy();
    /// <summary>Saves a changed copy; the localised refusal (nothing changed), or null when it is on disk.</summary>
    string? SaveLibraryCopy(LinuxGameLibraryState state);
    /// <summary>Makes a saved copy the in-memory library state.</summary>
    void AdoptLibraryCopy(LinuxGameLibraryState state);
    Task<bool> RefreshWhenIdleAsync(IReadOnlySet<string>? onlyDetectors);
    LinuxDeploymentRecordStore DeploymentRecords { get; }
    bool LibraryRepairRunning { get; }
    void RaiseNotice(string message);
    void ShowAllGames();
    void Select(DetectedGame game);
    // #293 install folder migration
    IReadOnlyList<LinuxDeploymentRecord> InstallsIn(DetectedGame game, string? folder);
    Task<bool> AskFolderMoveAsync(DetectedGame game, string oldDir, string newDir);
    Task<InstallFolderMoveResult> MoveInstallFolderAsync(DetectedGame oldGame, DetectedGame newGame, string oldDir, string newDir,
        Func<RHI.Platform.Linux.Persistence.StagedFileWrite?>? alongside);
    RHI.Platform.Linux.Persistence.StagedFileWrite StageLibrary(LinuxGameLibraryState state);
    bool FolderChangeRunning { get; set; }
}

/// <summary>
/// Manual library entries and Edit game (epic #89 unit 7, off the shell): Add game, the Edit game target and launch
/// command (Save / Reset) and Remove manual entry. Each change mutates a copy of the library state and is adopted only
/// once saved; <c>Added</c>/<c>Saved</c>/<c>Reset</c> are the structural success flags (the messages are localised).
/// </summary>
public sealed partial class ManualGamesViewModel : PanelViewModel
{
    private new ILibraryEditHost Host => (ILibraryEditHost)base.Host;

    internal ManualGamesViewModel(ILibraryEditHost host) : base(host) { }

    internal void NotifyRemove()
    {
        OnPropertyChanged(nameof(CanRemoveManualGame));
        RemoveManualGameCommand.NotifyCanExecuteChanged();
    }

    public bool CanRemoveManualGame => SelectedGame?.IsManuallyAdded == true && !IsBusy;

    /// <summary>
    /// Saves the Edit game target and launch command; <c>Saved</c> is the structural success flag (the message is
    /// localised). <paramref name="saved"/> runs as soon as the edit is on disk, before the library refresh waits for a
    /// running operation, so the dialog can close instead of sitting disabled with no message.
    /// </summary>
    /// <param name="launchCommand">
    /// The Edit game launch command box (Add game's rule: trimmed, blank = none). None, or the detected command itself,
    /// saves no command: the card uses the detected (launcher or Add game) command again.
    /// </param>
    /// <param name="names">
    /// Edit game's display name, wiki mod name and bitness (Refs #190); null keeps the ones already saved. A name equal to
    /// the detected one (or the manifest's wiki name) saves none. A bitness change that moves the game's effective
    /// bitness is refused while RHI components are installed (their files were picked for the old bitness).
    /// </param>
    public async Task<(bool Saved, string Message)> SaveSelectedGameEditAsync(
        string patchExecutablePath,
        string gameRootPath,
        string binaryDirectory,
        string? launchProvider,
        string? launchCommand,
        Action? saved = null,
        GameNameEdit? names = null)
    {
        if (SelectedGame is null) return (false, T("Vm.SelectGameFirst"));
        names = names?.Normalized(SelectedGame) ?? GameNameEdit.Current(SelectedGame);
        var namesChanged = names != GameNameEdit.Current(SelectedGame);
        if (names.Architecture != SelectedGame.UserArchitectureOverride
            && GameArchitecture.With(SelectedGame, names.Architecture) != GameArchitecture.Of(SelectedGame)
            && Host.DeploymentRecords.FindAll(SelectedGame).Count > 0)
            return (false, T("Shell.RemoveComponentsBeforeBitness"));

        var detectedCommand = SelectedGame.DetectedLaunch is { } detected ? detected.Command
            : SelectedGame.UserLaunchCommand is null ? SelectedGame.LaunchCommand : null;
        var userCommand = LinuxGameEditOverride.NormalizeLaunchCommand(launchCommand);
        if (userCommand is not null && string.Equals(userCommand, LinuxGameEditOverride.NormalizeLaunchCommand(detectedCommand), StringComparison.Ordinal))
            userCommand = null;
        var commandChanged = !string.Equals(userCommand, SelectedGame.UserLaunchCommand, StringComparison.Ordinal);

        // All three target fields left blank with a new launch command or new names: only those are saved, and a saved
        // patch target stays as it is (a launcher card has none to show; a stale one waits for Repair after move).
        if ((commandChanged || namesChanged) && string.IsNullOrWhiteSpace(patchExecutablePath) && string.IsNullOrWhiteSpace(gameRootPath)
            && string.IsNullOrWhiteSpace(binaryDirectory))
            return await SaveSelectedGameEditAsync(null, launchProvider, userCommand, names, saved);

        if (string.IsNullOrWhiteSpace(patchExecutablePath) || !File.Exists(patchExecutablePath))
            return (false, T("Vm.SelectRealGameExecutableShouldReceive"));
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
            return (false, T("Vm.SelectedGameRootDoesNotExist"));
        if (MainWindowViewModel.GameRootRefusal(gameRootPath) is { } rootRefusal) return (false, rootRefusal);
        if (string.IsNullOrWhiteSpace(binaryDirectory) || !Directory.Exists(binaryDirectory))
            return (false, T("Vm.SelectedBinariesPatchesDirectoryDoesNot"));

        var executable = Path.GetFullPath(patchExecutablePath);
        var root = Path.GetFullPath(gameRootPath);
        var binaries = Path.GetFullPath(binaryDirectory);

        var executableDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return (false, T("Vm.CouldNotDetermineTargetExecutableDirectory"));
        if (!IsPathWithin(root, executable))
            return (false, T("Vm.TargetExecutableMustInsideSelectedGame"));
        if (!IsPathWithin(root, binaries))
            return (false, T("Vm.BinariesPatchesDirectoryMustInsideSelected"));

        var managedRecords = Host.DeploymentRecords.FindAll(SelectedGame);
        FolderMove? move = null;
        if (managedRecords.Count > 0)
        {
            var targetChanged = !PathEquals(SelectedGame.ExecutablePath, executable)
                                || !PathEquals(SelectedGame.InstallPath, root)
                                || !PathEquals(SelectedGame.BinaryDirectory, binaries);
            if (targetChanged)
            {
                // #293 (upstream 2.9.2 Change install folder): RHI offers to move the game's installs to the new folder;
                // declined, nothing changes (Linux: records left in a folder the game no longer uses would block it). A
                // change that keeps the folder (another exe or root only) still needs the components removed first.
                var oldDir = SelectedGame.BinaryDirectory;
                if (oldDir is null || PathEquals(oldDir, binaries) || Host.InstallsIn(SelectedGame, oldDir).Count == 0)
                    return (false, T("Vm.RemoveRHIManagedComponentsBeforeChanging"));
                if (!await Host.AskFolderMoveAsync(SelectedGame, oldDir, binaries))
                    return (false, TF("FolderMove.NotChanged", SelectedGame.Name, oldDir));
                var newGame = SelectedGame.ShallowClone();
                newGame.InstallPath = root;
                newGame.ExecutablePath = executable;
                newGame.BinaryDirectory = binaries;
                move = new FolderMove(SelectedGame.ShallowClone(), newGame, oldDir, binaries);
            }
        }

        return await SaveSelectedGameEditAsync(new LinuxGameEditOverride
        {
            PatchExecutablePath = executable,
            GameRootPath = root,
            BinaryDirectory = binaries
        }, launchProvider, userCommand, names, saved, move);
    }

    /// <param name="target">The patch target to save; null keeps the one already saved for the game (if any).</param>
    /// <summary>#293: the installs that move with an Edit game target change (old and new game, folders).</summary>
    private sealed record FolderMove(DetectedGame OldGame, DetectedGame NewGame, string OldDir, string NewDir);

    private async Task<(bool Saved, string Message)> SaveSelectedGameEditAsync(
        LinuxGameEditOverride? target, string? launchProvider, string? userCommand, GameNameEdit names, Action? saved, FolderMove? move = null)
    {
        var targetFieldsBlank = target is null;
        var game = SelectedGame!;
        var commandChanged = !string.Equals(userCommand, game.UserLaunchCommand, StringComparison.Ordinal);
        var namesChanged = names != GameNameEdit.Current(game);
        // Mutate a copy and adopt it only once it is on disk: a refused save must not stay in
        // _libraryState, or the next unrelated library save would persist it.
        var state = Host.WorkingLibraryCopy();
        var key = LinuxGameLibraryStore.GameKey(game);
        if (target is null) state.GameEdits.TryGetValue(key, out target);
        // Blank target fields with a new command keep the saved target; the status line says so (Refs #151).
        var targetKept = target is { HasPatchTarget: true } && targetFieldsBlank;
        var edit = new LinuxGameEditOverride
        {
            LaunchProvider = launchProvider,
            PatchExecutablePath = target?.PatchExecutablePath,
            GameRootPath = target?.GameRootPath,
            BinaryDirectory = target?.BinaryDirectory,
            LaunchCommand = userCommand,
            DisplayName = names.DisplayName,
            WikiName = names.WikiName,
            Architecture = names.Architecture
        }.Normalize();
        if (edit.IsEmpty()) state.GameEdits.Remove(key);
        else state.GameEdits[key] = edit;
        string? moved = null;
        if (move is not null)
        {
            // The new target is written in the move's own commit: the installs and the folder change land together or not at all.
            var result = await Host.MoveInstallFolderAsync(move.OldGame, move.NewGame, move.OldDir, move.NewDir, () => Host.StageLibrary(state));
            if (!result.Success) return (false, UiLocalizationService.Present(result.Message));
            moved = TF("FolderMove.Done", game.Name, move.NewDir);
        }
        else if (Host.SaveLibraryCopy(state) is { } notSaved) return (false, notSaved);
        Host.AdoptLibraryCopy(state);
        saved?.Invoke();

        var name = game.Name;
        Host.Session.Invalidate(game);
        var refreshed = await Host.RefreshWhenIdleAsync(MainWindowViewModel.NoDetectors);
        var message = !refreshed ? TF("Vm.ModificationTargetSavedButLibraryWas", name)
            : !commandChanged && targetFieldsBlank && namesChanged ? TF("Shell.EditSavedNames", name)
            : !commandChanged ? TF("Vm.ModificationTargetSavedLauncherStartupWas", name)
            : targetKept
                ? userCommand is null ? TF("Vm.EditSavedTargetKeptPlayUsesDetectedCommand", name) : TF("Vm.EditSavedTargetKeptPlayUsesYourCommand", name)
            : userCommand is null ? TF("Vm.EditSavedPlayUsesDetectedCommand", name)
            : TF("Vm.EditSavedPlayUsesYourCommand", name);
        if (moved is not null) message = moved + " " + message;
        // Raised as a notice: the refresh re-analyses the game and its "Ready" line would replace a plain status line
        // within milliseconds; the notice rides on that line instead (as Repair after move does; Refs #151).
        Host.RaiseNotice(message);
        return (true, message);
    }

    /// <summary>Drops the selected game's manual overrides; <c>Reset</c> is false only when the reset was refused.</summary>
    /// <param name="saved">Runs once the change is on disk, before the refresh waits for a running operation.</param>
    public async Task<(bool Reset, string Message)> ResetSelectedGameEditAsync(Action? saved = null)
    {
        if (SelectedGame is null) return (false, T("Vm.SelectGameFirst"));
        if (Host.DeploymentRecords.FindAll(SelectedGame).Count > 0)
        {
            var oldDir = SelectedGame.BinaryDirectory;
            if (oldDir is null || Host.InstallsIn(SelectedGame, oldDir).Count == 0)
                return (false, T("Vm.RemoveRHIManagedComponentsBeforeRestoring"));
            return await ResetMovingInstallsAsync(oldDir, saved);
        }
        var state = Host.WorkingLibraryCopy();
        var key = LinuxGameLibraryStore.GameKey(SelectedGame);
        var removed = state.GameEdits.Remove(key);
        if (Host.SaveLibraryCopy(state) is { } notSaved) return (false, notSaved);
        Host.AdoptLibraryCopy(state);
        saved?.Invoke();
        var name = SelectedGame.Name;
        Host.Session.Invalidate(SelectedGame);
        var message = !await Host.RefreshWhenIdleAsync(MainWindowViewModel.NoDetectors)
            ? TF("Vm.ChangesSavedButLibraryWasNot", name)
            : removed
                ? TF("Vm.ManualOverridesRemovedAutomaticDetectionWas", name)
                : TF("Vm.NoManualOverridesWereSaved", name);
        StatusText = message;
        return (true, message);
    }

    /// <summary>
    /// #293 (upstream 2.9.2 Reset install folder): the detected folder is only known once the game is analysed without its
    /// edit, so the reset is saved first; RHI then offers to move the installs there. Declined (or a move that cannot run),
    /// the edit is put back and the game keeps the folder the user chose, with its installs.
    /// </summary>
    private async Task<(bool Reset, string Message)> ResetMovingInstallsAsync(string oldDir, Action? saved)
    {
        var game = SelectedGame!;
        var oldGame = game.ShallowClone();
        var key = LinuxGameLibraryStore.GameKey(game);
        var state = Host.WorkingLibraryCopy();
        if (!state.GameEdits.TryGetValue(key, out var previous)) return (false, T("Vm.RemoveRHIManagedComponentsBeforeRestoring"));
        state.GameEdits.Remove(key);
        Host.FolderChangeRunning = true;
        try
        {
            if (Host.SaveLibraryCopy(state) is { } notSaved) return (false, notSaved);
            Host.AdoptLibraryCopy(state);
            saved?.Invoke();
            Host.Session.Invalidate(game);
            if (!await Host.RefreshWhenIdleAsync(MainWindowViewModel.NoDetectors)) return (true, StatusText = TF("Vm.ChangesSavedButLibraryWasNot", game.Name));
            var reset = Host.AllGames.FirstOrDefault(candidate => string.Equals(LinuxGameLibraryStore.GameKey(candidate), key, StringComparison.OrdinalIgnoreCase));
            if (reset is null) return (true, StatusText = TF("Vm.ChangesSavedButLibraryWasNot", game.Name));
            Host.Select(reset);
            await Host.ReprobeGameAsync(reset);
            var newDir = reset.BinaryDirectory;
            if (newDir is null || PathEquals(newDir, oldDir))
                return (true, StatusText = TF("Vm.ManualOverridesRemovedAutomaticDetectionWas", game.Name));
            string reason;
            if (!await Host.AskFolderMoveAsync(oldGame, oldDir, newDir))
                reason = TF("FolderMove.NotChanged", game.Name, oldDir);
            else
            {
                var result = await Host.MoveInstallFolderAsync(oldGame, reset, oldDir, newDir, null);
                if (result.Success)
                {
                    await Host.ReprobeGameAsync(reset);
                    var done = TF("FolderMove.Done", game.Name, newDir) + " " + TF("Vm.ManualOverridesRemovedAutomaticDetectionWas", game.Name);
                    Host.RaiseNotice(done);
                    return (true, done);
                }
                reason = UiLocalizationService.Present(result.Message);
            }
            // Put the user's folder back: the installs stay where they are and keep working.
            var back = Host.WorkingLibraryCopy();
            back.GameEdits[key] = previous;
            if (Host.SaveLibraryCopy(back) is { } notRestored) return (false, StatusText = notRestored);
            Host.AdoptLibraryCopy(back);
            Host.Session.Invalidate(reset); // its analysis is the reset folder's: the edit's folder is analysed again
            await Host.RefreshWhenIdleAsync(MainWindowViewModel.NoDetectors);
            var undone = TF("FolderMove.ResetUndone", game.Name, reason);
            Host.RaiseNotice(undone);
            return (false, undone);
        }
        finally
        {
            Host.FolderChangeRunning = false;
        }
    }

    public async Task<string> AddManualGameAsync(LinuxManualGameEntry entry)
        => (await TryAddManualGameAsync(entry)).Message;

    /// <summary>
    /// Adds a manual game; <c>Added</c> is the structural success flag (the message is localised).
    /// <paramref name="saved"/> runs as soon as the entry is on disk, before the library refresh waits for
    /// a running operation (Update All…), so the dialog can close instead of sitting disabled with no message;
    /// the outcome then goes to the main status line.
    /// </summary>
    public async Task<(bool Added, string Message)> TryAddManualGameAsync(LinuxManualGameEntry entry, Action? saved = null)
    {
        // The Add game button and drag-and-drop are not busy-gated: a save between the repair's commit and its
        // reload would write the pre-repair library over the repaired file.
        if (Host.LibraryRepairRunning) return (false, T("Vm.WaitForRepairAfterMove"));
        if (EntryRefusal(entry) is { } refused) return (false, refused);
        var identity = PathIdentity.Canonicalize(entry.InstallPath);
        bool SameFolder(string? path) => !string.IsNullOrWhiteSpace(path) && PathIdentity.Canonicalize(path).Equals(identity, StringComparison.Ordinal);

        // Mutate a copy; the live state changes only once the save succeeded.
        var state = Host.WorkingLibraryCopy();
        if (DuplicateRefusal(entry, state) is { } duplicate) return (false, duplicate);

        state.ManualGames.Add(entry);
        if (Host.SaveLibraryCopy(state) is { } notSaved) return (false, notSaved);
        Host.AdoptLibraryCopy(state);
        saved?.Invoke();
        // #309 (upstream 2.9.0): the added game opens straight away (Select below); only a search or filter that would hide
        // it is cleared, so the filter tab is no longer reset to All Games beforehand.
        string message;
        if (!await Host.RefreshWhenIdleAsync(MainWindowViewModel.ManualOnly))
            message = TF("Vm.WasSavedManualLibraryButLibrary", entry.Name);
        else if (Host.AllGames.FirstOrDefault(game => game.IsManuallyAdded && SameFolder(game.LibraryIdentityPath ?? game.InstallPath)) is { } added)
        {
            Host.Select(added);
            message = TF("Vm.AddedManualLibrary", entry.Name);
        }
        else
            message = TF("Vm.WasSavedManualLibraryButDid", entry.Name);
        StatusText = message;
        return (true, message);
    }

    // Add game's checks on one entry before the library copy is read (name, folders, executable, prefix).
    private static string? EntryRefusal(LinuxManualGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return T("Vm.EnterNameGame");
        if (string.IsNullOrWhiteSpace(entry.InstallPath) || !Directory.Exists(entry.InstallPath)) return T("Vm.GameDirectoryDoesNotExist");
        if (MainWindowViewModel.GameRootRefusal(entry.InstallPath) is { } rootRefusal) return rootRefusal;
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath) && !File.Exists(entry.ExecutablePath)) return T("Vm.SelectedExecutableDoesNotExist");
        if (!string.IsNullOrWhiteSpace(entry.PrefixPath) && !Directory.Exists(entry.PrefixPath)) return T("Vm.SelectedWineProtonPrefixDoesNot");
        return null;
    }

    // Duplicates are judged by the library's own identity (PathIdentity: symlinks resolved, ordinal), so a symlink
    // spelling of a listed folder is refused here instead of being saved and merged away by the scan.
    private string? DuplicateRefusal(LinuxManualGameEntry entry, LinuxGameLibraryState state)
    {
        var identity = PathIdentity.Canonicalize(entry.InstallPath);
        bool SameFolder(string? path) => !string.IsNullOrWhiteSpace(path) && PathIdentity.Canonicalize(path).Equals(identity, StringComparison.Ordinal);
        if (Host.AllGames.Any(game => SameFolder(game.InstallPath) || SameFolder(game.LibraryIdentityPath))) return T("Vm.GameDirectoryAlreadyLibrary");
        if (state.ManualGames.Any(existing => SameFolder(existing.InstallPath))) return T("Vm.ManualGameAlreadySaved");
        return null;
    }

    /// <summary>
    /// #293 (custom game folders' review): Add game for several entries at once, with Add game's checks on each; the ones
    /// that pass are saved together and the library is refreshed once. <c>Refused</c> pairs each refused entry with its
    /// localised reason (all of them when the library could not be saved); <c>Refreshed</c> is false when the refresh failed.
    /// </summary>
    internal async Task<(IReadOnlyList<LinuxManualGameEntry> Added, IReadOnlyList<(LinuxManualGameEntry Entry, string Reason)> Refused, bool Refreshed)>
        TryAddManualGamesAsync(IReadOnlyList<LinuxManualGameEntry> entries)
    {
        if (Host.LibraryRepairRunning) return ([], entries.Select(entry => (entry, T("Vm.WaitForRepairAfterMove"))).ToArray(), true);
        var state = Host.WorkingLibraryCopy();
        var added = new List<LinuxManualGameEntry>();
        var refused = new List<(LinuxManualGameEntry, string)>();
        foreach (var entry in entries)
        {
            // Each accepted entry joins the copy first, so two entries for one folder cannot both be saved.
            if ((EntryRefusal(entry) ?? DuplicateRefusal(entry, state)) is { } reason) refused.Add((entry, reason));
            else
            {
                state.ManualGames.Add(entry);
                added.Add(entry);
            }
        }
        if (added.Count == 0) return ([], refused, true);
        state.FilterMode = "All Games";
        if (Host.SaveLibraryCopy(state) is { } notSaved) return ([], entries.Select(entry => (entry, notSaved)).ToArray(), true);
        Host.AdoptLibraryCopy(state);
        Host.ShowAllGames();
        var refreshed = await Host.RefreshWhenIdleAsync(MainWindowViewModel.ManualOnly);
        return (added, refused, refreshed);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualGame))]
    private async Task RemoveManualGameAsync()
    {
        if (SelectedGame is not { IsManuallyAdded: true } selected) return;
        // Mutate a copy; the live state changes only once the save succeeded. Matched by every folder the
        // entry may have been saved with (an edit or the manifest may have moved InstallPath).
        var state = Host.WorkingLibraryCopy();
        if (state.RemoveManualEntriesFor(selected) == 0)
        {
            StatusText = TF("Vm.SavedManualEntryWasNotFound", selected.Name);
            return;
        }
        // #293 (upstream 2.9.0): a game added from a custom game folder is not offered again by the next scan.
        state.IgnoreCustomFolderGame(selected.LibraryIdentityPath ?? selected.InstallPath);
        if (Host.SaveLibraryCopy(state) is not null) return;
        Host.AdoptLibraryCopy(state);
        StatusText = await Host.RefreshWhenIdleAsync(MainWindowViewModel.ManualOnly)
            ? TF("Vm.RemovedFromManualLibraryGameFiles", selected.Name)
            : TF("Vm.RemovedFromManualLibraryButList", selected.Name);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.GetFullPath(candidate);
            if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>
/// Edit game's display name, wiki mod name and bitness (Refs #190); null fields = automatic (the detected name, the
/// manifest's wiki mapping or the game's name, the bitness <see cref="GameArchitecture.Of"/> finds without a choice).
/// </summary>
public sealed record GameNameEdit(string? DisplayName, string? WikiName, PeArchitecture? Architecture)
{
    /// <summary>What <paramref name="game"/> has saved now.</summary>
    public static GameNameEdit Current(DetectedGame game)
        => new(game.UserDisplayName, game.UserWikiName, game.UserArchitectureOverride);

    /// <summary>
    /// Typed values as they are saved: trimmed, blank = none; a display name equal to the detected name and a wiki name
    /// equal to the manifest's mapping save none (the card shows the same either way); bitness only x86/x64.
    /// </summary>
    public GameNameEdit Normalized(DetectedGame game)
    {
        var display = LinuxGameEditOverride.NormalizeName(DisplayName);
        if (string.Equals(display, game.Name, StringComparison.Ordinal)) display = null;
        var wiki = LinuxGameEditOverride.NormalizeName(WikiName);
        var manifestWiki = game.DetectedWikiName is { } detected ? detected.Value : game.UserWikiName is null ? game.WikiNameOverride : null;
        if (string.Equals(wiki, LinuxGameEditOverride.NormalizeName(manifestWiki), StringComparison.Ordinal)) wiki = null;
        return new(display, wiki, Architecture is PeArchitecture.X86 or PeArchitecture.X64 ? Architecture : null);
    }
}
