using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One saved custom filter's chip (Refs #193).</summary>
public sealed partial class CustomFilterChip(string name, string query) : ObservableObject
{
    public string Name { get; } = name;
    public string Query { get; } = query;
    /// <summary>The query in a 2 s tooltip (the chip shows only the name).</summary>
    public string Tip => UiLocalizationService.Format("Shell.CustomFilterTip", Query);
    [ObservableProperty] private bool isActive;
}

/// <summary>
/// Saved custom filters (Refs #193, upstream <c>CustomFilter</c> / <c>SaveFilterButton_Click</c>): the current search is
/// saved under a name in the library file (<see cref="LinuxGameLibraryState.CustomFilters"/>) and shown as a chip below the
/// filter tabs; a chip applies its search on top of the filter tab and the search box, clicking the active chip turns it
/// off, and its context menu deletes it. Which chip is active is kept for the session only (upstream). Saves follow the
/// library-write rules: a copy is changed and adopted only once it is on disk, and nothing is written during Repair after
/// move. UI thread only.
/// </summary>
public sealed partial class CustomFiltersPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, CustomFiltersPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private readonly ILibraryEditHost _library;
    private readonly Action _applyFilter;

    public static CustomFiltersPresenter For(MainWindowViewModel shell, Action applyFilter)
        => Presenters.GetValue(shell, s => new CustomFiltersPresenter(s, applyFilter));

    private CustomFiltersPresenter(MainWindowViewModel shell, Action applyFilter)
    {
        _shell = shell;
        _library = shell;
        _applyFilter = applyFilter;
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SearchText)) OnPropertyChanged(nameof(CanSave));
        };
    }

    public ObservableCollection<CustomFilterChip> Chips { get; } = [];

    /// <summary>The chip row shows only when there is a chip or a search to save (inapplicable rows are hidden).</summary>
    public bool IsShown => Chips.Count > 0 || CanSave;

    /// <summary>"Save as filter" shows while the search box holds a search.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(_shell.SearchText);

    /// <summary>The active chip's name, or null.</summary>
    public string? ActiveName { get; private set; }

    /// <summary>The active chip's search, applied on top of the filter tab and the search box; null when none is active.</summary>
    public string? ActiveQuery => Chips.FirstOrDefault(chip => chip.IsActive)?.Query;

    /// <summary>
    /// Brings the chips in line with <paramref name="state"/> (called on every filter pass, so a refresh, a restored
    /// library file or another save shows at once); an active chip whose filter is gone is turned off.
    /// </summary>
    internal void Sync(LinuxGameLibraryState state)
    {
        var saved = state.CustomFilters ?? [];
        var same = saved.Count == Chips.Count
                   && saved.Zip(Chips).All(pair => pair.First.Name == pair.Second.Name && pair.First.Query == pair.Second.Query);
        if (!same)
        {
            Chips.Clear();
            foreach (var filter in saved) Chips.Add(new CustomFilterChip(filter.Name, filter.Query));
        }
        if (ActiveName is not null && !Chips.Any(chip => string.Equals(chip.Name, ActiveName, StringComparison.OrdinalIgnoreCase)))
            ActiveName = null;
        foreach (var chip in Chips) chip.IsActive = string.Equals(chip.Name, ActiveName, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(IsShown));
    }

    /// <summary>
    /// Saves the current search as a filter named <paramref name="name"/> (upstream: the name is required and unique in
    /// any case), clears the search box and turns the new chip on. The localised refusal stays in the dialog.
    /// </summary>
    public (bool Saved, string Message) Save(string? name, string? query)
    {
        var trimmedName = name?.Trim() ?? "";
        var trimmedQuery = query?.Trim() ?? "";
        if (trimmedQuery.Length == 0) return (false, UiLocalizationService.Text("Shell.CustomFilterNoSearch"));
        if (trimmedName.Length == 0) return (false, UiLocalizationService.Text("Shell.CustomFilterNameEmpty"));
        if (_library.LibraryRepairRunning) return (false, UiLocalizationService.Text("Vm.WaitForRepairAfterMove"));
        var state = _library.WorkingLibraryCopy();
        var filters = state.CustomFilters ?? [];
        if (filters.Any(filter => string.Equals(filter.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return (false, UiLocalizationService.Format("Shell.CustomFilterNameExists", trimmedName));
        filters.Add(new LinuxCustomFilter { Name = trimmedName, Query = trimmedQuery });
        state.CustomFilters = filters;
        if (_library.SaveLibraryCopy(state) is { } refused) return (false, refused);
        _library.AdoptLibraryCopy(state);
        ActiveName = trimmedName;
        _shell.SearchText = "";
        _applyFilter();
        var message = UiLocalizationService.Format("Shell.CustomFilterSaved", trimmedName);
        _shell.StatusText = message;
        return (true, message);
    }

    /// <summary>Turns a chip on, or off when it is the active one (upstream's toggle).</summary>
    [RelayCommand]
    private void Toggle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        ActiveName = string.Equals(ActiveName, name, StringComparison.OrdinalIgnoreCase) ? null : name;
        _applyFilter();
    }

    /// <summary>Deletes a saved filter (its chip's context menu); an active one is turned off with it.</summary>
    [RelayCommand]
    private void Delete(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_library.LibraryRepairRunning)
        {
            _shell.StatusText = UiLocalizationService.Text("Vm.WaitForRepairAfterMove");
            return;
        }
        var state = _library.WorkingLibraryCopy();
        var filters = state.CustomFilters ?? [];
        if (filters.RemoveAll(filter => string.Equals(filter.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        state.CustomFilters = filters.Count == 0 ? null : filters;
        if (_library.SaveLibraryCopy(state) is { } refused)
        {
            _shell.StatusText = refused;
            return;
        }
        _library.AdoptLibraryCopy(state);
        _applyFilter(); // Sync turns the chip off with it

        _shell.StatusText = UiLocalizationService.Format("Shell.CustomFilterDeleted", name);
    }
}
