using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class LutrisConfigParsingTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void GameSection_JoinsFoldedLines_AndIgnoresTheInstallerScriptCopy()
    {
        var config = LutrisConfigFile.Parse("""
            game:
              args: ''
              exe: /home/u/Games/battlenet/drive_c/Program Files (x86)/Battle.net/Battle.net
                Launcher.exe
              prefix: '/home/u/Games/battle net'
              working_dir: /home/u/Games/x: The Collection/very long folder
                name/bin
            name: battle.net
            script:
              game:
                exe: drive_c/Program Files (x86)/Battle.net/Battle.net Setup.exe
                prefix: $GAMEDIR
              wine:
                version: lutris-7.2
            wine:
              version: GE-Proton10-29
            """);

        Assert.Equal("/home/u/Games/battlenet/drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe", config.Section("game", "exe"));
        Assert.Equal("/home/u/Games/battle net", config.Section("game", "prefix"));
        Assert.Equal("/home/u/Games/x: The Collection/very long folder name/bin", config.Section("game", "working_dir"));
        Assert.Equal("", config.Section("game", "args"));
        Assert.Equal("GE-Proton10-29", config.Section("wine", "version"));
        Assert.Equal("battle.net", config.Top("name"));
        Assert.Null(config.Top("exe"));
        Assert.Null(config.Section("system", "env"));
    }

    [Fact]
    public void GameSection_WhenOnlyTheScriptHasAnExe_HasNoExe()
    {
        var config = LutrisConfigFile.Parse("""
            game:
              prefix: /p
            script:
              game:
                exe: drive_c/setup.exe
            """);

        Assert.Null(config.Section("game", "exe"));
        Assert.Equal("/p", config.Section("game", "prefix"));
    }

    [Fact]
    public void PgaReader_ReadsEveryRow_IncludingOverflowAndColumnsAddedLater()
    {
        var rows = SqliteTableReader.ReadTable(Fixture("lutris-pga.db"), "games");

        Assert.Equal(304, rows.Count);
        var battle = rows.Single(r => (string?)r["configpath"] == "battlenet-1774722738");
        Assert.Equal("Battle.net", battle["name"]);
        Assert.Equal(1L, battle["installed"]);
        Assert.Equal(1L, battle["id"]);
        Assert.Equal(1.5, battle["playtime"]);
        Assert.Null(battle["discord_id"]);
        Assert.Equal(0L, rows.Single(r => (string?)r["slug"] == "gone-game")["installed"]);
        Assert.Contains(rows, r => (string?)r["name"] == "Filler 300");
        var longRow = rows.Single(r => (string?)r["slug"] == "long-name");
        Assert.Equal(new string('L', 5000), longRow["name"]);
        Assert.Equal("42", longRow["discord_id"]);
    }

    [Fact]
    public void PgaReader_DamagedOrForeignFiles_YieldNothing()
    {
        using var tree = new DetectionTestTree();
        var garbage = tree.Bytes([.. "SQLite format 3\0"u8, .. new byte[600]], "bad.db");
        var text = tree.File(new string('x', 1000), "text.db");
        var truncated = tree.Bytes(File.ReadAllBytes(Fixture("lutris-pga.db"))[..8192], "truncated.db");

        Assert.Empty(SqliteTableReader.ReadTable(garbage, "games"));
        Assert.Empty(SqliteTableReader.ReadTable(text, "games"));
        Assert.Empty(SqliteTableReader.ReadTable(truncated, "games"));
        Assert.Empty(SqliteTableReader.ReadTable(Fixture("lutris-pga.db"), "no_such_table"));
    }

    [Fact]
    public void Detector_TakesNamesFromPga_AndFallsBackToTheSlugWithoutTimestamp()
    {
        using var tree = new DetectionTestTree();
        var configs = tree.Dir("lutris", "games");
        File.Copy(Fixture("lutris-pga.db"), tree.Path_("lutris", "pga.db"));
        var able = tree.Exe("apps", "Able", "Live.exe");
        var other = tree.Exe("apps", "Other", "Other.exe");
        tree.File($"game:\n  exe: {able}\n", "lutris", "games", "ablechum12-1775064156.yml");
        tree.File($"game:\n  exe: {other}\nscript:\n  name: create_prefix\n", "lutris", "games", "some-tool-1775064999.yml");

        var games = LutrisGameDetector.DetectIn([configs], tree.Path_("home"), CancellationToken.None);

        Assert.Equal(["Ablechum12", "some tool"], games.Select(g => g.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Detector_UsesTheWrappedTopLevelExe_NotTheInstallerScriptOne()
    {
        using var tree = new DetectionTestTree();
        var configs = tree.Dir("lutris", "games");
        var prefix = tree.Dir("battlenet");
        tree.Dir("battlenet", "drive_c", "windows");
        var launcher = tree.Exe("battlenet", "drive_c", "Program Files (x86)", "Battle.net", "Battle.net Launcher.exe");
        tree.Exe("battlenet", "drive_c", "Program Files (x86)", "Battle.net", "Battle.net Setup.exe");
        var folder = Path.GetDirectoryName(launcher)!;
        tree.File($"""
                   game:
                     exe: {folder}/Battle.net
                       Launcher.exe
                     prefix: {prefix}
                   name: battle.net
                   script:
                     game:
                       exe: drive_c/Program Files (x86)/Battle.net/Battle.net Setup.exe
                       prefix: $GAMEDIR
                   """, "lutris", "games", "battlenet-1763437958.yml");

        var game = Assert.Single(LutrisGameDetector.DetectIn([configs], tree.Path_("home"), CancellationToken.None, []));

        Assert.Equal(launcher, game.LaunchExecutablePath);
        Assert.Equal("battle.net", game.Name);
    }
}
