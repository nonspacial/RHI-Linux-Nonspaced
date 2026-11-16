using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One game collection's chip above the game list (#300).</summary>
public sealed partial class CollectionChip(string name, int index, int count) : ObservableObject
{
    public string Name { get; } = name;
    /// <summary>Its place in the user's order (Move left / right are offered where it can move).</summary>
    public int Index { get; } = index;
    public int Count { get; } = count;
    public bool CanMoveLeft => Index > 0;
    [ObservableProperty] private bool canMoveRight;
    [ObservableProperty] private bool isActive;

    public string Tip => UiLocalizationService.Format(IsActive ? "Coll.ChipTipActive" : "Coll.ChipTip", Name);

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(Tip));
}

/// <summary>
/// #300 (upstream 2.9.2 <c>MainViewModel.Collections</c> / <c>MainWindow.Collections</c>): the user's game collections.
/// One chip per collection above the game list: click shows only its games (hidden ones excepted) and goes back to All
/// underneath, click again shows everything; its menu renames, moves or deletes it (never the games). A game's menu has
/// "Add to collection" with a tick per collection and "New collection…". Picking a filter tab leaves the collection. The
/// list and the active collection live in the library file (<see cref="LinuxGameLibraryState.GameCollections"/>,
/// <see cref="LinuxGameLibraryState.ActiveCollection"/>), saved like every library change: a copy is changed and adopted
/// once it is on disk. UI thread only.
/// </summary>
public sealed partial class CollectionsPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, CollectionsPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private readonly ILibraryEditHost _library;
    private readonly Action _applyFilter;
    private List<LinuxGameCollection> _saved = [];

    public static CollectionsPresenter For(MainWindowViewModel shell, Action applyFilter)
        => Presenters.GetValue(shell, s => new CollectionsPresenter(s, applyFilter));

    private CollectionsPresenter(MainWindowViewModel shell, Action applyFilter)
    {
        _shell = shell;
        _library = shell;
        _applyFilter = applyFilter;
    }

    /// <summary>The chips, in the user's order.</summary>
    public ObservableCollection<CollectionChip> Chips { get; } = [];

    /// <summary>The collection the list shows, or null.</summary>
    public string? ActiveName { get; private set; }

    /// <summary>
    /// The active collection's membership test for the list filter (by library key), or null when none is active.
    /// </summary>
    public Func<DetectedGame, bool>? Membership
        => LinuxGameCollections.Members(_saved, ActiveName) is { } members
            ? game => members.Contains(LinuxGameLibraryStore.GameKey(game))
            : null;

    /// <summary>
    /// The message over an empty list while a collection is shown (upstream's empty-list text), or null.
    /// </summary>
    public string? EmptyText { get; private set; }

    public bool HasEmptyText => EmptyText is not null;

    /// <summary>Set by the window: asks for a new collection's name (with the game to put in it, or null) and creates it.</summary>
    public Func<DetectedGame?, Task>? AskNewCollectionAsync { get; set; }

    /// <summary>Brings the chips and the active collection in line with <paramref name="state"/> (every filter pass).</summary>
    internal void Sync(LinuxGameLibraryState state)
    {
        _saved = state.GameCollections ?? [];
        ActiveName = LinuxGameCollections.Find(_saved, state.ActiveCollection)?.Name;
        var same = _saved.Count == Chips.Count
                   && _saved.Zip(Chips).All(pair => pair.First.Name == pair.Second.Name && pair.First.Games.Count == pair.Second.Count);
        if (!same)
        {
            Chips.Clear();
            for (var i = 0; i < _saved.Count; i++)
                Chips.Add(new CollectionChip(_saved[i].Name, i, _saved[i].Games.Count) { CanMoveRight = i < _saved.Count - 1 });
        }
        foreach (var chip in Chips) chip.IsActive = string.Equals(chip.Name, ActiveName, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ActiveName));
    }

    /// <summary>After the list was filtered: the empty-list message for the active collection.</summary>
    internal void UpdateEmptyText(int shown, bool searching)
    {
        string? text = null;
        if (ActiveName is { } name && shown == 0)
        {
            var members = LinuxGameCollections.Find(_saved, name)?.Games.Count ?? 0;
            text = UiLocalizationService.Format(members == 0 ? "Coll.EmptyNoGames" : searching ? "Coll.EmptySearch" : "Coll.EmptyHidden", name);
        }
        if (text == EmptyText) return;
        EmptyText = text;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
    }

    /// <summary>A chip click: shows the collection's games (the filter tab goes back to All), or all games when it is the active one.</summary>
    [RelayCommand]
    private void Toggle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var next = string.Equals(ActiveName, name, StringComparison.OrdinalIgnoreCase) ? null : LinuxGameCollections.Find(_saved, name)?.Name;
        if (!SaveActive(next)) return;
        // Upstream: a collection replaces the main filter, which goes back to All underneath.
        if (next is not null && _shell.LibraryFilterMode != "All Games") _shell.LibraryFilterMode = "All Games";
    }

    /// <summary>A filter tab was picked: the list leaves the collection (upstream <c>SetFilter</c>). True when one was active.</summary>
    internal bool Leave() => ActiveName is not null && SaveActive(null);

    /// <summary>Creates a collection (with <paramref name="firstGame"/> in it). The localised refusal stays in the dialog.</summary>
    public (bool Done, string Message) Create(string? name, DetectedGame? firstGame)
    {
        if (_library.LibraryRepairRunning) return (false, UiLocalizationService.Text("Vm.WaitForRepairAfterMove"));
        var state = _library.WorkingLibraryCopy();
        var list = state.GameCollections ?? [];
        var problem = LinuxGameCollections.Create(list, name, firstGame is null ? null : LinuxGameLibraryStore.GameKey(firstGame), out var created);
        if (problem != CollectionNameProblem.None) return (false, ProblemText(problem, created));
        state.GameCollections = list;
        if (Save(state) is { } refused) return (false, refused);
        var message = firstGame is null
            ? UiLocalizationService.Format("Coll.Created", created)
            : UiLocalizationService.Format("Coll.Added", firstGame.DisplayName(), created);
        _shell.StatusText = message;
        return (true, message);
    }

    /// <summary>Renames a collection (the active one stays active). The localised refusal stays in the dialog.</summary>
    public (bool Done, string Message) Rename(string oldName, string? newName)
    {
        if (_library.LibraryRepairRunning) return (false, UiLocalizationService.Text("Vm.WaitForRepairAfterMove"));
        var state = _library.WorkingLibraryCopy();
        var list = state.GameCollections ?? [];
        var wasActive = string.Equals(state.ActiveCollection, oldName, StringComparison.OrdinalIgnoreCase);
        var problem = LinuxGameCollections.Rename(list, oldName, newName, out var renamed);
        if (problem != CollectionNameProblem.None) return (false, ProblemText(problem, renamed));
        state.GameCollections = list;
        if (wasActive) state.ActiveCollection = renamed;
        if (Save(state) is { } refused) return (false, refused);
        var message = UiLocalizationService.Format("Coll.Renamed", oldName, renamed);
        _shell.StatusText = message;
        return (true, message);
    }

    /// <summary>Checks a typed name as the dialog shows it (null = fine).</summary>
    public string? NameProblem(string? name, string? renaming)
    {
        var problem = LinuxGameCollections.Validate(_saved, name, renaming, out var clean);
        return problem == CollectionNameProblem.None ? null : ProblemText(problem, clean);
    }

    /// <summary>Deletes a collection, never its games; the list goes back to all games if it showed it.</summary>
    public bool Delete(string name)
    {
        if (RefusedDuringRepair()) return false;
        var state = _library.WorkingLibraryCopy();
        var list = state.GameCollections ?? [];
        if (LinuxGameCollections.Find(list, name) is not { } collection || !LinuxGameCollections.Delete(list, name)) return false;
        state.GameCollections = list;
        if (string.Equals(state.ActiveCollection, collection.Name, StringComparison.OrdinalIgnoreCase)) state.ActiveCollection = null;
        if (Save(state) is { } refused)
        {
            _shell.StatusText = refused;
            return false;
        }
        _shell.StatusText = UiLocalizationService.Format("Coll.Deleted", collection.Name);
        return true;
    }

    [RelayCommand]
    private void MoveLeft(string? name) => Move(name, -1);

    [RelayCommand]
    private void MoveRight(string? name) => Move(name, 1);

    private void Move(string? name, int delta)
    {
        if (string.IsNullOrWhiteSpace(name) || RefusedDuringRepair()) return;
        var state = _library.WorkingLibraryCopy();
        var list = state.GameCollections ?? [];
        if (!LinuxGameCollections.Move(list, name, delta)) return;
        state.GameCollections = list;
        if (Save(state) is { } refused) _shell.StatusText = refused;
    }

    public bool IsMember(string collection, DetectedGame game)
        => LinuxGameCollections.Contains(_saved, collection, LinuxGameLibraryStore.GameKey(game));

    /// <summary>Puts a game in a collection or takes it out (the game menu's ticks).</summary>
    public void SetMembership(string collection, DetectedGame game, bool member)
    {
        if (RefusedDuringRepair()) return;
        var state = _library.WorkingLibraryCopy();
        var list = state.GameCollections ?? [];
        if (!LinuxGameCollections.SetMembership(list, collection, LinuxGameLibraryStore.GameKey(game), member)) return;
        state.GameCollections = list;
        if (Save(state) is { } refused)
        {
            _shell.StatusText = refused;
            return;
        }
        var name = LinuxGameCollections.Find(list, collection)?.Name ?? collection;
        _shell.StatusText = UiLocalizationService.Format(member ? "Coll.Added" : "Coll.RemovedGame", game.DisplayName(), name);
    }

    /// <summary>
    /// The game menu's "Add to collection" submenu (upstream <c>BuildCollectionsMenu</c>): a ticked or plain entry per
    /// collection (choosing it flips the game's membership), then "New collection…" (<paramref name="newCollection"/>
    /// asks for the name; the window supplies it).
    /// </summary>
    internal CardMenuItem MenuFor(DetectedGame game)
    {
        var children = new List<CardMenuItem>();
        foreach (var collection in _saved)
        {
            var name = collection.Name;
            var member = IsMember(name, game);
            children.Add(new CardMenuItem("collection-" + name, (member ? "✓ " : "    ") + name,
                new RelayCommand(() => SetMembership(name, game, !member))));
        }
        children.Add(new CardMenuItem("collection-new", UiLocalizationService.Text("Coll.New"),
            new AsyncRelayCommand(() => AskNewCollectionAsync?.Invoke(game) ?? Task.CompletedTask), SeparatorBefore: children.Count > 0));
        return new CardMenuItem("add-to-collection", UiLocalizationService.Text("Coll.AddTo"), CardMenuItem.OpensSubmenu,
            Children: children);
    }

    private bool SaveActive(string? name)
    {
        if (string.Equals(ActiveName, name, StringComparison.Ordinal)) return true;
        if (RefusedDuringRepair()) return false;
        var state = _library.WorkingLibraryCopy();
        state.ActiveCollection = name;
        if (Save(state) is { } refused)
        {
            _shell.StatusText = refused;
            return false;
        }
        return true;
    }

    /// <summary>Saves and adopts a changed library copy, then re-filters; the localised refusal, or null.</summary>
    private string? Save(LinuxGameLibraryState state)
    {
        if (_library.SaveLibraryCopy(state) is { } refused) return refused;
        _library.AdoptLibraryCopy(state);
        _applyFilter(); // Sync rebuilds the chips
        return null;
    }

    private bool RefusedDuringRepair()
    {
        if (!_library.LibraryRepairRunning) return false;
        _shell.StatusText = UiLocalizationService.Text("Vm.WaitForRepairAfterMove");
        return true;
    }

    internal static string ProblemText(CollectionNameProblem problem, string name) => problem switch
    {
        CollectionNameProblem.Empty => UiLocalizationService.Text("Coll.NameEmpty"),
        CollectionNameProblem.TooLong => UiLocalizationService.Format("Coll.NameTooLong", LinuxGameCollections.MaxNameLength),
        CollectionNameProblem.ControlCharacters => UiLocalizationService.Text("Coll.NameControl"),
        CollectionNameProblem.BuiltInFilter => UiLocalizationService.Format("Coll.NameBuiltIn", name),
        CollectionNameProblem.AlreadyUsed => UiLocalizationService.Format("Coll.NameUsed", name),
        CollectionNameProblem.Missing => UiLocalizationService.Text("Coll.Missing"),
        _ => ""
    };
}
