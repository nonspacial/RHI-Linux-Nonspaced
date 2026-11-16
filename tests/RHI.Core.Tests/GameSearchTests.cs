using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>
/// #299 (upstream 2.9.3 "Game search"): search works much like Steam's. The table is upstream's own
/// (<c>RenoDXCommander.Tests/GameSearchTests.cs</c> at RHI-2.9.4) plus the release notes' examples; the library-level
/// tests check near misses are hidden, best match first and whole-word tags through <see cref="GameLibraryFilter"/>.
/// </summary>
public sealed class GameSearchTests
{
    [Theory]
    [InlineData("F.E.A.R. 3", "fear")]
    [InlineData("F.E.A.R. 3", "FEAR 3")]
    [InlineData("F.E.A.R. 3", "fear3")]
    [InlineData("Half-Life 2", "half life")]
    [InlineData("Marvel's Spider-Man Remastered", "spiderman")]
    [InlineData("S.T.A.L.K.E.R. 2: Heart of Chornobyl", "stalker 2")]
    [InlineData("Borderlands® 4", "borderlands 4")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "tom clancys")]
    public void PunctuationIgnored(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Grand Theft Auto III", "gta 3")]
    [InlineData("Grand Theft Auto III", "gta3")]
    [InlineData("Grand Theft Auto III", "grand theft auto 3")]
    [InlineData("Grand Theft Auto V", "gta5")]
    [InlineData("FINAL FANTASY VII REBIRTH", "final fantasy 7")]
    [InlineData("Final Fantasy XVI", "ff16")]
    public void RomanNumeralsMatchNumbers(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Grand Theft Auto V", "gta")]
    [InlineData("Grand Theft Auto III", "gtaiii")]
    [InlineData("FINAL FANTASY VII REBIRTH", "ff7")]
    [InlineData("Final Fantasy X", "ffx")]
    [InlineData("Half-Life 2", "hl2")]
    [InlineData("Resident Evil 4", "re4")]
    [InlineData("Marvel's Spider-Man 2", "msm")]
    [InlineData("Red Dead Redemption 2", "rdr2")]
    public void InitialsMatch(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Sifu", "sifu")]
    [InlineData("Sifu", "SIFU")]
    [InlineData("Clone Hero", "clone")]
    [InlineData("Clone Hero", "hero clone")]
    [InlineData("F.E.A.R. 3", "fear 3")]
    [InlineData("Resident Evil 4", "resident evil 4")]
    [InlineData("Grand Theft Auto III", "grand theft auto 3")]
    [InlineData("Grand Theft Auto III", "grand theft auto iii")]
    [InlineData("Marvel's Spider-Man 2", "marvels")]
    public void WholeWordMatches(string name, string query) => Assert.True(GameSearch.NameMatchesWholeWords(name, query));

    [Theory]
    [InlineData("PC Building Simulator", "sifu")]                   // typo match only
    [InlineData("Uncharted: The Nathan Drake Collection", "clone")] // typo match only
    [InlineData("Clone Hero", "clon")]                              // part of a word: still typing
    [InlineData("Resident Evil 4", "resident evil 3")]
    public void NearMissesArentWholeWordMatches(string name, string query) => Assert.False(GameSearch.NameMatchesWholeWords(name, query));

    [Theory]
    [InlineData("Grand Theft Auto V", "gta4")]  // the number has to be right
    [InlineData("Red Dead Redemption 2", "rr")] // two characters only match initials from the start
    [InlineData("I Am Bread", "1 am")]          // a lone "I" only counts as 1 at the end of a name
    public void InitialsAndNumbersDontOverMatch(string name, string query) => Assert.False(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("LEGO® Batman™: Legacy of the Dark Knight", "batman lego")]
    [InlineData("LEGO® Batman™: Legacy of the Dark Knight", "dark knight lego")]
    [InlineData("Resident Evil 4", "4 resident")]
    [InlineData("Grand Theft Auto V", "5 gta")] // initials work in any order too
    public void AnyWordOrder(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Call of Duty®: Modern Warfare® III", "mw3")] // part of the initials (codmw3)
    [InlineData("Call of Duty®: Modern Warfare® III", "mwiii")]
    [InlineData("Red Dead Redemption 2", "dr2")]
    public void PartialInitials(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Resident Evil 4", "resident eviaa")]    // Steam's example: 2 letters off
    [InlineData("Resident Evil 4", "residentaa evilaa")] // 2 extra letters on each word
    [InlineData("Resident Evil 4", "residnt")]           // a missing letter while typing
    [InlineData("Cyberpunk 2077", "cyberpnuk")]          // swapped letters
    [InlineData("Hogwarts Legacy", "hogwarst legasy")]
    [InlineData("Hades II", "hdes")]                     // 4 letters, 1 missing
    public void TyposTolerated(string name, string query) => Assert.True(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Resident Evil 4", "resident eviaaa")] // 3 letters off is too many
    [InlineData("Resident Evil 4", "resident dead")]   // every word has to match
    [InlineData("Lies of P", "lsei")]                  // 4 letters: only 1 letter off allowed
    [InlineData("The Witcher 3: Wild Hunt", "sea")]    // 3 letters or fewer: no typo slack ("the" isn't "sea")
    public void TyposDontOverMatch(string name, string query) => Assert.False(GameSearch.NameMatchesLoosely(name, query));

    [Fact]
    public void FourLetterWord_OneSwapAllowed() => Assert.True(GameSearch.NameMatchesLoosely("Lies of P", "lise"));

    [Theory]
    [InlineData("F.E.A.R. 3", "fear 4")]
    [InlineData("Portal 2", "p")] // one letter: too loose, the plain search covers it
    [InlineData("Portal 2", ".")] // nothing left to match
    public void NoFalseMatches(string name, string query) => Assert.False(GameSearch.NameMatchesLoosely(name, query));

    [Theory]
    [InlineData("Resident Evil 4", "resident evil 4", 0)]
    [InlineData("F.E.A.R.", "fear", 0)]
    [InlineData("Grand Theft Auto III", "grand theft auto 3", 0)]
    [InlineData("Resident Evil 4 Remake", "resident evil 4", 1)]
    [InlineData("Evil West", "evil", 1)]
    [InlineData("The Evil Within", "evil", 2)]
    [InlineData("Devil May Cry 5", "evil", 3)]
    [InlineData("Grand Theft Auto V", "gta", 4)]
    [InlineData("Resident Evil 2", "resident eviaa", 5)]
    [InlineData("Cyberpunk 2077", "steam", 6)]
    public void NameRank_BestMatchFirst(string name, string query, int rank) => Assert.Equal(rank, GameSearch.NameRank(name, query));

    [Theory]
    [InlineData("Steam", "steam")]
    [InlineData("Unreal Engine 5.3", "unreal")]
    [InlineData("Unreal Engine 5.3", "unreal engine")]
    [InlineData("DX12", "dx12")]
    [InlineData("32-bit", "32bit")]
    [InlineData("32-bit", "32 bit")]
    [InlineData("UW Fix", "uw fix")]
    [InlineData("Ultra+", "ultra+")]
    [InlineData("Ray Reconstruction", "reconstruction")]
    public void TagMatches_WholeWords(string tag, string query) => Assert.True(GameSearch.TagMatches(tag, GameSearch.LettersAndDigits(query)));

    [Theory]
    [InlineData("Steam", "st")]
    [InlineData("Steam", "stea")]
    [InlineData("Unreal Engine 5.3", "un")]
    [InlineData("Unity", "un")]
    [InlineData("DLSS", "dl")]
    [InlineData("Frame Generation", "frame gen")]
    [InlineData("Unreal Engine", "unrealengine5")]
    public void TagMatches_NotPartOfAWord(string tag, string query) => Assert.False(GameSearch.TagMatches(tag, GameSearch.LettersAndDigits(query)));

    // ---- through the library filter -------------------------------------------------------------------------

    [Fact]
    public void Library_ProperNameMatchHidesNearMisses()
    {
        // Release notes: "sifu shows only Sifu, clone shows only games with Clone in the name".
        var sifu = Game("Sifu");
        var pcbs = Game("PC Building Simulator");
        Assert.True(GameLibraryFilter.MatchesSearch(pcbs, "sifu")); // a near miss on its own…
        Assert.Equal(["Sifu"], Names(GameLibraryFilter.Filter([pcbs, sifu], GameLibraryFilterMode.AllGames, "sifu", null)));

        var clone = Game("Clone Hero");
        var uncharted = Game("Uncharted: The Nathan Drake Collection");
        Assert.Equal(["Clone Hero"], Names(GameLibraryFilter.Filter([uncharted, clone], GameLibraryFilterMode.AllGames, "clone", null)));
    }

    [Fact]
    public void Library_NearMissesStayWhenNothingMatchesProperly()
    {
        var re = Game("Resident Evil 4");
        Assert.Equal(["Resident Evil 4"], Names(GameLibraryFilter.Filter([re], GameLibraryFilterMode.AllGames, "resident eviaa", null)));
    }

    [Fact]
    public void Library_BestMatchFirst_ResidentEvil4OnTop()
    {
        var games = new[] { Game("Resident Evil 4 Remake"), Game("Resident Evil 2"), Game("Resident Evil 4"), Game("Resident Evil Village") };
        var shown = Names(GameLibraryFilter.Filter(games, GameLibraryFilterMode.AllGames, "resident evil 4", null));
        Assert.Equal(["Resident Evil 4", "Resident Evil 4 Remake"], shown);

        // Ranks first, A to Z within a rank: "evil" — Evil West starts with it (1), The Evil Within has the word (2).
        var evil = Names(GameLibraryFilter.Filter([Game("The Evil Within"), Game("Devil May Cry 5"), Game("Evil West")],
            GameLibraryFilterMode.AllGames, "evil", null));
        Assert.Equal(["Evil West", "The Evil Within"], evil);

        // A to Z would put Halo first; the name that starts with the search ranks above a whole-word match.
        var infinite = Names(GameLibraryFilter.Filter([Game("Halo Infinite"), Game("Infinite Wealth")], GameLibraryFilterMode.AllGames, "infinite", null));
        Assert.Equal(["Infinite Wealth", "Halo Infinite"], infinite);
    }

    [Fact]
    public void Library_NoSearch_SortsByShownName()
    {
        var shown = Names(GameLibraryFilter.Filter([Game("Tunic"), Game("Celeste"), Game("hades")], GameLibraryFilterMode.AllGames, " ", null));
        Assert.Equal(["Celeste", "hades", "Tunic"], shown);
    }

    [Fact]
    public void Library_ReleaseNoteExamples()
    {
        var games = new[]
        {
            Game("F.E.A.R."), Game("LEGO® Batman™: Legacy of the Dark Knight"), Game("Grand Theft Auto V"), Game("Red Dead Redemption 2"),
            Game("FINAL FANTASY VII REBIRTH"), Game("Half-Life 2"), Game("Call of Duty®: Modern Warfare® III"), Game("Grand Theft Auto III")
        };
        string[] Search(string q) => Names(GameLibraryFilter.Filter(games, GameLibraryFilterMode.AllGames, q, null));
        Assert.Equal(["F.E.A.R."], Search("fear"));
        Assert.Equal(["LEGO® Batman™: Legacy of the Dark Knight"], Search("batman lego"));
        Assert.Equal(["Grand Theft Auto III", "Grand Theft Auto V"], Search("gta"));
        Assert.Equal(["Red Dead Redemption 2"], Search("rdr2"));
        Assert.Equal(["FINAL FANTASY VII REBIRTH"], Search("ff7"));
        Assert.Equal(["Half-Life 2"], Search("hl2"));
        Assert.Equal(["Call of Duty®: Modern Warfare® III"], Search("mw3"));
        Assert.Equal(["Grand Theft Auto III"], Search("gta 3"));
    }

    [Fact]
    public void Library_TagsMatchWholeWordsOnly()
    {
        // Release notes: "st no longer brings up every Steam game while you type Stellar Blade, and un no longer every Unity
        // and Unreal game. Type steam, unreal, dx12 or vulkan to list those games."
        var stellar = Game("Stellar Blade", source: "Heroic Epic");
        var steamGame = Game("Hollow Knight", source: "Steam");
        steamGame.EngineHint = "Unity";
        var unreal = Game("Hogwarts Legacy", source: "Steam");
        unreal.EngineHint = "Unreal Engine 4";
        unreal.GraphicsApi = GraphicsApi.DirectX12;
        var all = new[] { stellar, steamGame, unreal };
        string[] Search(string q) => Names(GameLibraryFilter.Filter(all, GameLibraryFilterMode.AllGames, q, null));

        Assert.Equal(["Stellar Blade"], Search("st"));
        Assert.Empty(Search("un"));
        // Upstream's rules, kept: "steam" is also two letters off "stell(ar)", a near miss of a name, which ranks (5) above
        // the games found by their store (6).
        Assert.Equal(["Stellar Blade", "Hogwarts Legacy", "Hollow Knight"], Search("steam"));
        Assert.Equal(["Hogwarts Legacy"], Search("unreal"));
        Assert.Equal(["Hogwarts Legacy"], Search("unreal engine"));
        Assert.Equal(["Hogwarts Legacy"], Search("dx12"));
        Assert.Equal(["Hollow Knight"], Search("unity"));
    }

    [Fact]
    public void Library_GameFoundByATagStaysBesideAProperNameMatch()
    {
        // "Steam" is the name of one game and the store of another: both are shown, the name match first.
        var named = Game("Steam World Dig", source: "Heroic GOG");
        var store = Game("Tunic", source: "Steam");
        Assert.Equal(["Steam World Dig", "Tunic"], Names(GameLibraryFilter.Filter([store, named], GameLibraryFilterMode.AllGames, "steam", null)));
    }

    [Fact]
    public void Library_EditGameDisplayNameAndDetectedNameBothFindTheCard()
    {
        var game = Game("FINAL FANTASY VII REBIRTH");
        game.UserDisplayName = "FF7 Rebirth";
        Assert.True(GameLibraryFilter.MatchesSearch(game, "final fantasy 7"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "rebirth"));
        Assert.Equal(["FF7 Rebirth"], Names(GameLibraryFilter.Filter([game], GameLibraryFilterMode.AllGames, "final fantasy 7", null)));
    }

    private static DetectedGame Game(string name, string source = "Manual")
        => new() { Name = name, Source = source, InstallPath = "/games/x" };

    private static string[] Names(IEnumerable<DetectedGame> games) => games.Select(game => game.DisplayName()).ToArray();
}
