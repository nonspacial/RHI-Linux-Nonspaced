using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// w12 integration A8 (re-review epic, major): a Lutris prefix whose only store entries start one game each (a lutris.net
/// per-game installer put the client into the game's own prefix, or the client's own entry was removed from Lutris and the
/// prefix kept). A game with no entry of its own, and every store-library game found by layout, is still LISTED — no launch
/// command (Play says "set one in Edit game"), no parent launcher, the prefix/runner fields of the prefix's configs — with
/// one scan warning per prefix. Another game's entry never starts it. (Before #247's correction these cards were listed with
/// the wrong game's Play; the correction dropped them.)
/// </summary>
public sealed class LutrisPerGameStoreEntryOnlyTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private string Home => _tree.Path_("home");
    public LutrisPerGameStoreEntryOnlyTests() => Directory.CreateDirectory(Home);
    public void Dispose() => _tree.Dispose();

    private const string UbiExe = "drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe";

    private (string Prefix, string Root, string Own, string Other) UbiPrefixWithOnlyAPerGameEntry(string name)
    {
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        var prefix = Prefix(name);
        var own = Path.Combine(prefix, "drive_c", "StoreGames", "Own");
        Pe(own, "Own.exe", 8192);
        var other = Path.Combine(prefix, "drive_c", "StoreGames", "Other");
        Pe(other, "Other.exe", 8192);
        Registry(prefix, ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\4553""", [("InstallDir", "C:/StoreGames/Own/")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\7014""", [("InstallDir", "C:/StoreGames/Other/")]));
        Pe(Path.Combine(prefix, Path.GetDirectoryName(UbiExe)!), Path.GetFileName(UbiExe));
        // Lutris' Ubisoft service entry for Own only; the client's own entry is not in Lutris (prefix kept).
        File.WriteAllText(Path.Combine(configRoot, "own-game-ubisoft-1763420600.yml"),
            $"game:\n  args: 'uplay://launch/4553'\n  exe: {UbiExe}\n  prefix: {prefix}\nwine:\n  version: lutris-ge-8-26\nname: Own Game\n");
        return (prefix, configRoot, own, other);
    }

    [Fact]
    public async Task RecordedGame_WithOnlyAnotherGamesEntry_IsListedWithoutACommand_AndOneWarning()
    {
        var (prefix, root, own, other) = UbiPrefixWithOnlyAPerGameEntry("p1");

        var (games, warnings, listed) = await DetectorDiagnostics.CollectScanAsync(() => Task.FromResult(LutrisGameDetector.DetectIn([root], Home, CancellationToken.None)));

        var card = Assert.Single(games, g => g.InstallPath == other);
        Assert.Null(card.LaunchCommand); // never Own's "lutris:rungame/own-game-ubisoft"
        Assert.Null(card.ParentLauncherName);
        Assert.Equal(prefix, card.PrefixPath);
        Assert.Equal(Path.Combine(other, "Other.exe"), card.ExecutablePath);
        Assert.Equal("Lutris", card.Source);
        // The game with its own entry keeps that entry's Play.
        Assert.Equal("lutris 'lutris:rungame/own-game-ubisoft'", Assert.Single(games, g => g.InstallPath == own).LaunchCommand);
        // w12 follow-up (merge review nit 1): counted as listed without a launch command (its own scan line), never as a
        // skipped item (no warning).
        Assert.Equal(1, listed);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task LayoutGame_InAPrefixWithOnlyPerGameEntries_IsListedWithoutACommand_OneWarningForThePrefix()
    {
        var (prefix, root, _, other) = UbiPrefixWithOnlyAPerGameEntry("p2");
        var layout = Path.Combine(prefix, "drive_c", "Games", "Layout");
        Pe(layout, "Layout.exe", 8192);

        var (games, warnings, listed) = await DetectorDiagnostics.CollectScanAsync(() => Task.FromResult(LutrisGameDetector.DetectIn([root], Home, CancellationToken.None)));

        var card = Assert.Single(games, g => g.InstallPath == layout);
        Assert.Null(card.LaunchCommand);
        Assert.Null(card.ParentLauncherName);
        Assert.Equal(prefix, card.PrefixPath);
        Assert.Null(Assert.Single(games, g => g.InstallPath == other).LaunchCommand);
        Assert.DoesNotContain(games, g => g.InstallPath != Path.Combine(prefix, "drive_c", "StoreGames", "Own") &&
                                          g.LaunchCommand?.Contains("own-game-ubisoft", StringComparison.Ordinal) == true);
        Assert.Equal(2, listed); // Other and Layout, in the prefix's one count (follow-up: no longer a "skipped" warning)
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task PrefixWithNoStoreEntryAtAll_OnlyPlainGamesShareIt_ListsNoLayoutGames_AndWarnsNothing()
    {
        // Unchanged rule: only games share the prefix (every config is a store-library game; no store client entry, no
        // per-game store entry): nothing to start the other library games through, so they are not listed.
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        var prefix = Prefix("plain");
        var plain = Path.Combine(prefix, "drive_c", "Games", "Plain");
        Pe(plain, "Plain.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "Games", "Layout"), "Layout.exe", 8192);
        File.WriteAllText(Path.Combine(configRoot, "plain-1763420700.yml"),
            $"game:\n  exe: drive_c/Games/Plain/Plain.exe\n  prefix: {prefix}\nname: Plain\n");

        var (games, warnings, listed) = await DetectorDiagnostics.CollectScanAsync(() => Task.FromResult(LutrisGameDetector.DetectIn([configRoot], Home, CancellationToken.None)));

        Assert.Equal(plain, Assert.Single(games).InstallPath);
        Assert.Empty(warnings);
        Assert.Equal(0, listed);
    }

    private string Prefix(string name)
    {
        var prefix = _tree.Dir("home", "Games", name);
        _tree.Dir("home", "Games", name, "drive_c", "windows");
        _tree.Dir("home", "Games", name, "dosdevices");
        _tree.Link("../drive_c", "home", "Games", name, "dosdevices", "c:");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        return prefix;
    }

    private static void Registry(string prefix, params (string Key, (string Name, string Value)[] Values)[] keys)
    {
        var text = new StringBuilder("WINE REGISTRY Version 2\n;; All keys relative to \\\\Machine\n\n#arch=win64\n\n");
        foreach (var (key, values) in keys)
        {
            text.Append('[').Append(key).Append("] 1700000000\n#time=1d000000000000\n");
            foreach (var (n, value) in values)
                text.Append('"').Append(n).Append("\"=\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
            text.Append('\n');
        }
        File.WriteAllText(Path.Combine(prefix, "system.reg"), text.ToString(), Encoding.Latin1);
    }

    private static string Pe(string directory, string name, int bytes = 64)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var data = new byte[bytes];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        File.WriteAllBytes(path, data);
        return path;
    }
}
