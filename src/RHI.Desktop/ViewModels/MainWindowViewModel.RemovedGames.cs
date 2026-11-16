using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
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

    [RelayCommand]
    private void RemoveFromRhi()
    {
        if (SelectedGame is not { IsManuallyAdded: false } game || IsBusy || LibraryWritesPaused()) return;
        AdoptRestoredLibraryFile();
        var key = LinuxGameLibraryStore.GameKey(game);
        var state = WorkingLibraryCopy();
        state.RemovedGames ??= [];
        state.RemovedGames.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        state.RemovedGames.Add(new LinuxRemovedGame { Key = key, Name = game.Name, Source = game.Source, RemovedUtc = DateTimeOffset.UtcNow });
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved)
        {
            StatusText = notSaved;
            return;
        }
        _libraryState = state;
        _library.SetGames(_library.AllGames.Where(candidate => !ReferenceEquals(candidate, game)).ToArray());
        ApplyFilter();
        SelectedGame = Games.FirstOrDefault()?.Game;
        LoadRemovedGames();
        UiExceptionGuard.Forget(RefreshLibraryFactsAsync(), "RefreshLibraryFactsAsync");
        StatusText = UiLocalizationService.Format("Removed.Done", game.Name);
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
