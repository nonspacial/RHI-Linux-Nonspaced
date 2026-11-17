using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;

namespace RHI.Desktop.ViewModels;

// #293 (upstream 2.9.2 install folder migration): after Change install folder (Edit game) or Reset install folder (Restore
// automatic detection) RHI offers to move the game's mods, settings and presets to the new folder; when RHI corrects a
// folder itself (the manifest's installPathOverrides / installPathMigrations) it moves them on its own. Nothing moves
// while the game is running. The move itself is LinuxManagedComponentService.MoveInstallFolderAsync (journaled).
public partial class MainWindowViewModel
{
    /// <summary>The question asked before a move: title, text and the Move button.</summary>
    public sealed record FolderMovePrompt(string Title, string Message, string MoveText);

    /// <summary>Asks the folder-move question; true = move. Unset (a window without dialogs): never moves.</summary>
    public Func<FolderMovePrompt, Task<bool>>? ConfirmFolderMoveAsync { get; set; }

    /// <summary>True while Edit game or Restore automatic detection changes a game's folder: no automatic move meanwhile.</summary>
    internal bool FolderChangeRunning { get; set; }

    internal async Task<bool> AskFolderMoveAsync(DetectedGame game, string oldDir, string newDir)
    {
        if (ConfirmFolderMoveAsync is not { } confirm) return false;
        var installed = _managedComponents.InstallsIn(game, oldDir)
            .Select(r => LinuxManagedComponentService.DisplayDependencyName(r.Component)).Distinct();
        return await confirm(new FolderMovePrompt(T("FolderMove.Title"),
            TF("FolderMove.Question", string.Join(", ", installed), oldDir, newDir), T("FolderMove.Move")));
    }

    /// <summary>Runs a move behind the operation gate; the result's message is platform text (present it).</summary>
    internal async Task<InstallFolderMoveResult> MoveInstallFolderAsync(DetectedGame oldGame, DetectedGame newGame, string oldDir, string newDir,
        Func<StagedFileWrite?>? alongside = null)
    {
        using var op = BeginOperation("folder-move", cancellable: false);
        if (op is null) return InstallFolderMoveResult.Refused(T("Vm.AnotherRHIOperationAlreadyRunning"));
        StatusText = TF("FolderMove.Moving", newGame.Name);
        try
        {
            var result = await Task.Run(() => _managedComponents.MoveInstallFolderAsync(oldGame, newGame, oldDir, newDir,
                [_apiOverrideStore, _optiScalerVariantStore, DisplayModes.GameState], alongside, op.Token));
            if (result.Success && !result.NothingToMove) DisplayModes.FollowCommittedMove(); // w14 final review M2
            return result;
        }
        finally
        {
            _session.Invalidate(oldGame);
            _session.Invalidate(newGame);
            UiExceptionGuard.Forget(RefreshLibraryFactsAsync(), "RefreshLibraryFactsAsync");
        }
    }

    /// <summary>
    /// After an analysis: a game whose folder the manifest corrected (its install folder comes from installPathOverrides or
    /// installPathMigrations and the user picked none) has its installs moved there from the folder they were made in.
    /// A running game keeps them until a later analysis; the status line says so.
    /// </summary>
    private async Task AutoMoveInstallFolderAsync(DetectedGame game)
    {
        // A splitGames child shares its store key (and root) with its siblings: their installs are not its own.
        if (game.ManifestInstallRoot is null || game.IsManifestSplitChild || game.PatchTargetAuthority != "Automatic") return;
        // The first analysis lands inside the start-up scan (or Refresh): the move waits for it to finish.
        while (IsBusy) await _operations.WhenIdleAsync();
        if (FolderChangeRunning || game.ManifestInstallRoot is not { } storeRoot || game.PatchTargetAuthority != "Automatic"
            || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return;
        if (_managedComponents.InstallFolderLeftBehind(game) is not { } oldDir || !PathIdentity.IsSameOrInside(oldDir, storeRoot)) return;
        var newDir = game.BinaryDirectory!;
        var result = await MoveInstallFolderAsync(game, game, oldDir, newDir);
        if (result.Success)
        {
            StatusText = _statusNotice.Raise(TF("FolderMove.Done", game.Name, Path.GetRelativePath(storeRoot, newDir)));
            if (ReferenceEquals(SelectedGame, game)) await ReprobeGameAsync(game);
        }
        else if (!result.NothingToMove)
            StatusText = _statusNotice.Raise(result.Message == LinuxManagedComponentService.FolderMoveRunningMessage
                ? TF("FolderMove.AutoWaiting", game.Name, oldDir)
                : TF("FolderMove.AutoFailed", game.Name, UiLocalizationService.Present(result.Message)));
    }
}
