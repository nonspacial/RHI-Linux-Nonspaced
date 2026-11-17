using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// #293 (upstream 2.9.0 "Remove from RHI", Settings › Troubleshooting › Removed games): a game removed from RHI is gone
// from every list, Hidden included, and a scan does not bring it back; Restore does. A game the user added is removed
// with the existing "Remove manual game" instead (upstream: it is deleted).
public partial class MainWindowViewModel
{
    /// <summary>One removed game as Settings lists it.</summary>
    public sealed record RemovedGameRow(string Key, string Name, string Source, string RemovedText);

    /// <summary>Settings › Removed games, newest first.</summary>
    public ObservableCollection<RemovedGameRow> RemovedGames { get; } = [];

    public bool CanRemoveFromRhi => SelectedGame is { IsManuallyAdded: false } && !IsBusy;

    public string RemoveFromRhiText => UiLocalizationService.Text("Removed.Remove");

    public string RemoveFromRhiTip => UiLocalizationService.Text("Removed.RemoveTip");

    /// <summary>
    /// What the Remove from RHI confirmation shows (upstream 2.9.0): the question, how to undo it and, when RHI installed
    /// components in the game, which ones, with the box that uninstalls them at the same time.
    /// </summary>
    public sealed record RemoveFromRhiPrompt(string Title, string Question, string UndoNote, string? Installed, string UninstallText, string RemoveText)
    {
        public bool OffersUninstall => Installed is not null;
    }

    /// <summary>
    /// Asks the Remove from RHI confirmation: null = cancelled, false = remove only, true = remove and uninstall the
    /// components. Unset (tests, a window without dialogs): the game is removed and its components stay.
    /// </summary>
    public Func<RemoveFromRhiPrompt, Task<bool?>>? ConfirmRemoveFromRhiAsync { get; set; }

    /// <summary>The confirmation for <paramref name="game"/>.</summary>
    internal RemoveFromRhiPrompt RemoveFromRhiPromptFor(DetectedGame game)
    {
        var installed = _managedComponents.RemovableComponents(game);
        return new RemoveFromRhiPrompt(
            UiLocalizationService.Text("Removed.Title"),
            UiLocalizationService.Format("Removed.Question", game.DisplayName()),
            UiLocalizationService.Text("Removed.UndoNote"),
            installed.Count == 0 ? null
                : UiLocalizationService.Format("Removed.Installed", string.Join(", ", installed.Select(LinuxManagedComponentService.DisplayDependencyName))),
            UiLocalizationService.Text("Removed.AlsoUninstall"),
            UiLocalizationService.Text("Vm.Remove"));
    }

    [RelayCommand]
    private async Task RemoveFromRhiAsync()
    {
        if (SelectedGame is not { IsManuallyAdded: false } game) return;
        // w14 final review nit: busy is said, not a click that does nothing (the gate is asked again below).
        if (IsBusy)
        {
            StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return;
        }
        if (LibraryWritesPaused()) return;
        var prompt = RemoveFromRhiPromptFor(game);
        var uninstall = false;
        if (ConfirmRemoveFromRhiAsync is { } confirm)
        {
            if (await confirm(prompt) is not { } answer) return;
            uninstall = answer && prompt.OffersUninstall;
        }
        // #293 (upstream 2.9.0): "Also uninstall all RHI-managed components" — each through its own journaled removal.
        // Upstream removes the game whatever happens and only logs a failure; here a component that stays is named.
        string? kept = null;
        if (uninstall)
        {
            using var op = BeginOperation("component", cancellable: false);
            if (op is null) return; // BeginOperation said another operation runs
            StatusText = UiLocalizationService.Format("Removed.Uninstalling", game.Name);
            var result = await _managedComponents.RemoveAllComponentsAsync(game, op.Token);
            if (!result.Success)
                kept = UiLocalizationService.Format("Removed.KeptComponents", string.Join("; ", result.Kept.Select(k =>
                    $"{LinuxManagedComponentService.DisplayDependencyName(k.Component)} ({UiLocalizationService.Present(k.Reason)})")));
        }
        if (LibraryWritesPaused()) return;
        AdoptRestoredLibraryFile();
        var key = LinuxGameLibraryStore.GameKey(game);
        var state = WorkingLibraryCopy();
        state.RemovedGames ??= [];
        state.RemovedGames.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        state.RemovedGames.Add(new LinuxRemovedGame { Key = key, Name = game.Name, Source = game.Source, RemovedUtc = DateTimeOffset.UtcNow });
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved)
        {
            StatusText = kept is null ? notSaved : notSaved + " " + kept;
            return;
        }
        _libraryState = state;
        _library.SetGames(_library.AllGames.Where(candidate => !ReferenceEquals(candidate, game)).ToArray());
        ApplyFilter();
        SelectedGame = Games.FirstOrDefault()?.Game;
        LoadRemovedGames();
        UiExceptionGuard.Forget(RefreshLibraryFactsAsync(), "RefreshLibraryFactsAsync");
        var done = UiLocalizationService.Format("Removed.Done", game.Name);
        StatusText = kept is null ? done : done + " " + kept;
    }

    /// <summary>Restore from Settings: the game comes back with the next scan, which runs now (or once the gate is free).</summary>
    public async Task<string> RestoreRemovedGameAsync(string key)
    {
        if (LibraryWritesPaused()) return StatusText;
        AdoptRestoredLibraryFile();
        var state = WorkingLibraryCopy();
        var entry = state.RemovedGames?.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return StatusText;
        state.RemovedGames!.Remove(entry);
        if (state.RemovedGames.Count == 0) state.RemovedGames = null;
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved) return StatusText = notSaved;
        _libraryState = state;
        LoadRemovedGames();
        await RefreshWhenIdleAsync(null);
        return StatusText = UiLocalizationService.Format("Removed.Restored", entry.Name);
    }

    /// <summary>Fills <see cref="RemovedGames"/> from the library file (Settings calls it when the tab opens).</summary>
    public void LoadRemovedGames()
    {
        RemovedGames.Clear();
        foreach (var entry in (_libraryState.RemovedGames ?? []).OrderByDescending(entry => entry.RemovedUtc))
            RemovedGames.Add(new RemovedGameRow(entry.Key, entry.Name, entry.Source,
                UiLocalizationService.Format("Removed.When", entry.Source, entry.RemovedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))));
    }

    private void NotifyRemoveFromRhi()
    {
        OnPropertyChanged(nameof(CanRemoveFromRhi));
        RemoveFromRhiCommand.NotifyCanExecuteChanged();
    }
}
