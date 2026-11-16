using RHI.Core.Models;

namespace RHI.Core.Services;

public enum GameLibraryFilterMode
{
    AllGames,
    Favourites,
    Installed,
    Unreal,
    Unity,
    Other,
    RenoDX,
    Luma,
    Hidden
}

public readonly record struct GameLibraryFilterFacts(bool Installed, bool RenoDx, bool Luma);

/// <summary>Pure filtering/search contract shared by the Linux UI and regression tests.</summary>
public static class GameLibraryFilter
{
    /// <summary>
    /// #291 (upstream 2.8.9): the games a library-wide change may touch without the user picking the game: Update All,
    /// the automatic component pass, Follow newest DLSS / Streamline and Settings' "Apply to all games" buttons. Hidden
    /// games (favourites too) are left out; their own buttons still work.
    /// </summary>
    public static IReadOnlyList<DetectedGame> Unattended(IEnumerable<DetectedGame> games)
        => games.Where(game => !game.IsHidden).ToArray();

    /// <param name="customQuery">
    /// The active saved custom filter's search (Refs #193, upstream <c>CustomFilter</c>): a second search pass on top of
    /// the search box and the filter tab, independent of both. Null or blank = none.
    /// </param>
    public static bool Matches(
        DetectedGame game,
        GameLibraryFilterMode mode,
        string? search,
        GameLibraryFilterFacts facts,
        string? customQuery = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!MatchesSearch(game, search)) return false;
        if (!MatchesSearch(game, customQuery)) return false;

        if (mode == GameLibraryFilterMode.Hidden) return game.IsHidden;
        if (mode == GameLibraryFilterMode.Favourites) return game.IsFavourite; // upstream intentionally includes hidden favourites
        if (game.IsHidden) return false;

        return mode switch
        {
            GameLibraryFilterMode.AllGames => true,
            GameLibraryFilterMode.Installed => facts.Installed,
            GameLibraryFilterMode.Unreal => IsUnreal(game),
            GameLibraryFilterMode.Unity => IsUnity(game),
            GameLibraryFilterMode.Other => !IsUnreal(game) && !IsUnity(game),
            GameLibraryFilterMode.RenoDX => facts.RenoDx,
            GameLibraryFilterMode.Luma => facts.Luma,
            _ => true
        };
    }

    /// <summary>
    /// #299 (upstream 2.9.3, search "like Steam's"): the game's names (Edit game's display name, the detected name and
    /// the manifest's name, Refs #190) match as typed or loosely (<see cref="GameSearch"/>: punctuation, Roman numerals,
    /// initials, any word order, typos); everything else (store, runner, engine, graphics API, bitness, author, notes,
    /// folders) matches whole words only, so "st" is not every Steam game and "un" not every Unreal one.
    /// </summary>
    public static bool MatchesSearch(DetectedGame game, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var q = search.Trim();
        return Names(game).Any(name => GameSearch.NameMatches(name, q)) || MatchesSearchOutsideName(game, q);
    }

    /// <summary>
    /// #299: the games matching <paramref name="mode"/>, the search and the custom filter, best match first: A to Z by the
    /// shown name, then (with a search) by how well the name matches (<see cref="GameSearch.NameRank"/>; stable, so A to Z
    /// within each rank). When a shown game's name matches the search properly (the whole name or whole words), games
    /// that matched only by a near miss or part of a word are dropped; games found through something other than the name
    /// (store, engine…) stay.
    /// </summary>
    public static IReadOnlyList<DetectedGame> Filter(
        IEnumerable<DetectedGame> games,
        GameLibraryFilterMode mode,
        string? search,
        Func<DetectedGame, GameLibraryFilterFacts>? facts,
        string? customQuery = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var shown = games
            .Where(game => Matches(game, mode, search, facts?.Invoke(game) ?? default, customQuery))
            .OrderBy(game => game.DisplayName(), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(search)) return shown;
        var q = search.Trim();
        bool Proper(DetectedGame game) => Names(game).Any(name => GameSearch.NameMatchesWholeWords(name, q));
        if (shown.Any(Proper)) shown = shown.Where(game => Proper(game) || MatchesSearchOutsideName(game, q)).ToList();
        return shown.OrderBy(game => Names(game).Select(name => GameSearch.NameRank(name, q)).DefaultIfEmpty(GameSearch.NotTheName).Min()).ToList();
    }

    /// <summary>The names a card is found by: the shown (display) name, the detected name and the manifest's name.</summary>
    private static IEnumerable<string> Names(DetectedGame game)
        => new[] { game.DisplayName(), game.Name, game.CanonicalManifestName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)!;

    /// <summary>#299: the search against everything except the game's names, as whole words (<see cref="GameSearch.TagMatches"/>).</summary>
    private static bool MatchesSearchOutsideName(DetectedGame game, string search)
    {
        var q = GameSearch.LettersAndDigits(search);
        if (q.Length == 0) return false;
        bool Tag(string? value) => GameSearch.TagMatches(value, q);
        return Tag(game.Source)
            || Tag(game.Runner)
            || Tag(game.CompatibilityTool)
            || Tag(game.CompatibilityLayerType)
            || Tag(game.EngineHint)
            || Tag(game.AuthorOverride)
            || Tag(game.WikiStatusOverride)
            || Tag(game.ManifestGameNote)
            || Tag(game.InstallPath)
            || Tag(game.ExecutablePath)
            || Tag(game.PrefixPath)
            || game.EffectiveGraphicsApis.Any(api => Tag(FormatApi(api)))
            || (DeclaredArchitecture(game) is PeArchitecture.X86 && Tag("32-bit"))
            || (DeclaredArchitecture(game) is PeArchitecture.X64 && Tag("64-bit"))
            || (game.NativeHdrByManifest && Tag("HDR"))
            || (game.IsManuallyAdded && Tag("Manual"));
    }

    // Search reads only declared bitness (Edit game, then the manifest), never the exe header: a keystroke must not read files.
    private static PeArchitecture? DeclaredArchitecture(DetectedGame game)
        => game.UserArchitectureOverride is PeArchitecture.X86 or PeArchitecture.X64 ? game.UserArchitectureOverride : game.ForcedArchitecture;

    private static bool IsUnreal(DetectedGame game)
        => game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsUnity(DetectedGame game)
        => game.EngineHint?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true;

    private static string FormatApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX8 => "DirectX 8 DX8",
        GraphicsApi.DirectX9 => "DirectX 9 DX9",
        GraphicsApi.DirectX10 => "DirectX 10 DX10",
        GraphicsApi.DirectX11 => "DirectX 11 DX11",
        GraphicsApi.DirectX12 => "DirectX 12 DX12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "Unknown"
    };
}
