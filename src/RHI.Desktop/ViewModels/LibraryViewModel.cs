using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// One library card (Refs #85): the game plus its observable library state. A favourite or hidden change updates the
/// card in place, so the list never has to be rebuilt for the badge to follow. The flags are written on the UI thread
/// only, through <see cref="SetLibraryFlags"/>; the probe never touches them.
/// </summary>
public sealed class GameRowViewModel(DetectedGame game) : ObservableObject
{
    public DetectedGame Game { get; } = game;
    /// <summary>The card's name: Edit game's display name, else the detected name (Refs #190).</summary>
    public string Name => Game.DisplayName();
    public string Source => Game.Source;
    public bool IsFavourite => Game.IsFavourite;
    public bool IsHidden => Game.IsHidden;
    public string LibraryBadge => Game.LibraryBadge;

    /// <summary>Sets the favourite/hidden flags and tells the card; true when either changed.</summary>
    public bool SetLibraryFlags(bool favourite, bool hidden)
    {
        if (Game.IsFavourite == favourite && Game.IsHidden == hidden) return false;
        Game.IsFavourite = favourite;
        Game.IsHidden = hidden;
        OnPropertyChanged(nameof(IsFavourite));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(LibraryBadge));
        return true;
    }

    public override string ToString() => Game.ToString();
}

/// <summary>
/// Unit 3 of the view-model split (epic #89): the library list. Owns every scanned game, one card per game, and the
/// filtered, name-sorted <see cref="Games"/> list, which a filter or search change updates in place (Refs #75): cards
/// that still match stay where they are, so the selected card is never written back as null and its analysis is never
/// restarted by a keystroke. UI thread only.
/// </summary>
public sealed class LibraryViewModel
{
    private readonly List<DetectedGame> _all = [];
    private readonly Dictionary<DetectedGame, GameRowViewModel> _rows = new(ReferenceEqualityComparer.Instance);

    /// <summary>The cards shown, filtered and sorted by name.</summary>
    public ObservableCollection<GameRowViewModel> Games { get; } = [];

    /// <summary>Every scanned game, filtered or not.</summary>
    public IReadOnlyList<DetectedGame> AllGames => _all;

    /// <summary>Replaces the scanned games; cards of games kept from the previous scan are reused.</summary>
    public void SetGames(IEnumerable<DetectedGame> games)
    {
        _all.Clear();
        _all.AddRange(games);
        var keep = _all.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var gone in _rows.Keys.Where(game => !keep.Contains(game)).ToArray()) _rows.Remove(gone);
        foreach (var game in _all)
            if (!_rows.ContainsKey(game)) _rows[game] = new GameRowViewModel(game);
    }

    /// <summary>The card of <paramref name="game"/>, or null when it is not in the library.</summary>
    public GameRowViewModel? RowFor(DetectedGame? game)
        => game is not null && _rows.TryGetValue(game, out var row) ? row : null;

    /// <summary>True when <paramref name="game"/>'s card is in the filtered list.</summary>
    public bool IsShown(DetectedGame? game) => RowFor(game) is { } row && Games.Contains(row);

    /// <summary>Sets a game's favourite/hidden flags through its card (or directly when it has none yet).</summary>
    public void SetLibraryFlags(DetectedGame game, bool favourite, bool hidden)
    {
        if (RowFor(game) is { } row) row.SetLibraryFlags(favourite, hidden);
        else
        {
            game.IsFavourite = favourite;
            game.IsHidden = hidden;
        }
    }

    /// <summary>
    /// Shows the games matching <paramref name="mode"/> and <paramref name="search"/>, sorted by name, by removing and
    /// inserting cards in place (never clearing the list).
    /// </summary>
    public void ApplyFilter(GameLibraryFilterMode mode, string? search, Func<DetectedGame, GameLibraryFilterFacts>? facts)
    {
        var wanted = _all
            .Where(game => GameLibraryFilter.Matches(game, mode, search, facts?.Invoke(game) ?? default))
            .OrderBy(game => game.DisplayName(), StringComparer.CurrentCultureIgnoreCase)
            .Select(game => _rows[game])
            .ToList();
        var wantedSet = wanted.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = Games.Count - 1; i >= 0; i--)
            if (!wantedSet.Contains(Games[i])) Games.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Games.Count && ReferenceEquals(Games[i], wanted[i])) continue;
            var at = Games.IndexOf(wanted[i]);
            if (at >= 0) Games.Move(at, i);
            else Games.Insert(i, wanted[i]);
        }
    }
}
