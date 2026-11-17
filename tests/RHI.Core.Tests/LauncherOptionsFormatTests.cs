using System.Text;
using RHI.Platform.Linux.Launch.LauncherOptions;

namespace RHI.Core.Tests;

/// <summary>
/// #318: the launcher option formats on fixtures built from real files (Steam's localconfig.vdf and Lutris game configs from
/// a real machine, sanitised: app ids, names, paths and the account replaced; Heroic's from its v2.22.4 source), and the merge
/// and removal text rules.
/// </summary>
public sealed class LauncherOptionsFormatTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "launcher-options", name));
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ── merge / remove ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "-dx11", "-dx11")]
    [InlineData("", "-dx11", "-dx11")]
    [InlineData("gamemoderun %command%", "-dx11", "gamemoderun %command% -dx11")]
    [InlineData("VAR=1 %command% -windowed | tee log", "-dx11", "VAR=1 %command% -dx11 -windowed | tee log")]
    [InlineData("-novid", "-dx11", "-novid -dx11")]
    public void Steam_KeepsTheUsersOptions_AndPutsRhisAfterCommand(string? existing, string part, string expected)
        => Assert.Equal(expected, LauncherOptionsText.Merge(existing, part, steam: true));

    [Fact]
    public void OtherLaunchers_Append_EvenWithAPercentCommandInTheText()
        => Assert.Equal("%command% -a -dx11", LauncherOptionsText.Merge("%command% -a", "-dx11", steam: false));

    [Theory]
    [InlineData("gamemoderun %command% -dx11", "-dx11", "gamemoderun %command%")]
    [InlineData("VAR=1 %command% -dx11 -windowed", "-dx11", "VAR=1 %command% -windowed")]
    [InlineData("-dx11 -novid", "-dx11", "-novid")]
    [InlineData("-dx11", "-dx11", "")]
    [InlineData("-dx11x -dx11", "-dx11", "-dx11x")]
    public void RemovePart_TakesOutOnlyRhisWords(string current, string part, string expected)
        => Assert.Equal(expected, LauncherOptionsText.RemovePart(current, part));

    [Theory]
    [InlineData("-dx11x -novid", "-dx11")]
    [InlineData("x-dx11 -novid", "-dx11")]
    [InlineData("-novid", "-dx11")]
    public void RemovePart_IsNull_WhenThePartIsNotThereAsWholeWords(string current, string part)
        => Assert.Null(LauncherOptionsText.RemovePart(current, part));

    [Fact]
    public void ShellJoin_QuotesOnlyWhatNeedsIt_AndNeverLetsSteamSubstitute()
    {
        Assert.Equal("-dx11 '+exec auto.cfg' 'it'\\''s' -lang=pt-BR '%command%' '$HOME'",
            LauncherOptionsText.ShellJoin(["-dx11", "+exec auto.cfg", "it's", "-lang=pt-BR", "%command%", "$HOME"]));
    }

    // ── Steam localconfig.vdf ─────────────────────────────────────────────────────────────

    [Fact]
    public void Steam_ReadsTheAppsBlockUnderSoftwareValveSteam_NotWebStorages()
    {
        var file = Fixture("steam-localconfig.vdf");
        Assert.Equal(new SteamLocalConfig.Reading(true, "gamemoderun %command%"), SteamLocalConfig.Read(file, 4000001));
        Assert.Equal(new SteamLocalConfig.Reading(true, null), SteamLocalConfig.Read(file, 4000002));
        Assert.Equal(new SteamLocalConfig.Reading(true, ""), SteamLocalConfig.Read(file, 4000003));
        Assert.Equal(new SteamLocalConfig.Reading(true, "PROTON_LOG=\"C:\\\\logs\" %command% -windowed"), SteamLocalConfig.Read(file, 4000004));
        Assert.Equal(new SteamLocalConfig.Reading(false, null), SteamLocalConfig.Read(file, 4999999));
    }

    [Fact]
    public void Steam_ChangesOnlyTheValueToken_EscapedAsSteamDoes()
    {
        var file = Fixture("steam-localconfig.vdf");
        var written = SteamLocalConfig.Write(file, 4000001, "gamemoderun %command% -name \"A B\" C:\\x");
        var expected = Text(file).Replace("\"LaunchOptions\"\t\t\"gamemoderun %command%\"",
            "\"LaunchOptions\"\t\t\"gamemoderun %command% -name \\\"A B\\\" C:\\\\x\"", StringComparison.Ordinal);
        Assert.Equal(expected, Text(written));
        Assert.Equal("gamemoderun %command% -name \"A B\" C:\\x", SteamLocalConfig.Read(written, 4000001).LaunchOptions);
        // WebStorage's apps › 4000001 is untouched.
        Assert.Contains("\t\t\t\"4000001\"\n\t\t\t{\n\t\t\t\t\"DefaultLaunchOption\"", Text(written), StringComparison.Ordinal);
    }

    [Fact]
    public void Steam_AddsTheKeyAsALineOfTheAppsBlock_AndTakesItOutAgainByteForByte()
    {
        var file = Fixture("steam-localconfig.vdf");
        var written = SteamLocalConfig.Write(file, 4000002, "-dx11");
        Assert.Contains("\t\t\t\t\t\t}\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-dx11\"\n\t\t\t\t\t}\n\t\t\t\t\t\"4000003\"", Text(written), StringComparison.Ordinal);
        Assert.Equal("-dx11", SteamLocalConfig.Read(written, 4000002).LaunchOptions);
        Assert.Equal(file, SteamLocalConfig.Write(written, 4000002, null));
    }

    [Fact]
    public void Steam_AddsAnAppTheFileDoesNotListYet()
    {
        var file = Fixture("steam-localconfig.vdf");
        var written = SteamLocalConfig.Write(file, 4000009, "-dx11");
        Assert.Equal(new SteamLocalConfig.Reading(true, "-dx11"), SteamLocalConfig.Read(written, 4000009));
        Assert.Contains("\t\t\t\t\t\"4000009\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-dx11\"\n\t\t\t\t\t}\n\t\t\t\t}", Text(written), StringComparison.Ordinal);
        Assert.Equal("gamemoderun %command%", SteamLocalConfig.Read(written, 4000001).LaunchOptions);
    }

    [Fact]
    public void Steam_RefusesACutOffFile()
    {
        var file = Fixture("steam-localconfig.vdf");
        Assert.Throws<FormatException>(() => SteamLocalConfig.Read(file[..(file.Length / 2)], 4000001));
    }

    [Fact]
    public void Steam_KeepsBytesThatAreNotUtf8_Untouched()
    {
        var file = Fixture("steam-localconfig.vdf").Concat(new byte[] { 0xFF, 0xFE, (byte)'\n' }).ToArray();
        var written = SteamLocalConfig.Write(file, 4000003, "-é");
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'\n' }, written[^3..]);
        Assert.Equal("-é", SteamLocalConfig.Read(written, 4000003).LaunchOptions);
    }

    // ── Lutris game configs ───────────────────────────────────────────────────────────────

    [Fact]
    public void Lutris_ReadsOnlyTheTopLevelGameArgs()
    {
        Assert.Null(LutrisGameArgs.Read(Fixture("lutris-no-args.yml")));
        Assert.Equal("", LutrisGameArgs.Read(Fixture("lutris-empty-args.yml"))); // the script: section's -installer-only is not it
        Assert.Equal("-skipintro -language english -config \"C:\\Users\\player\\Documents\\Sample Racer\\settings custom.ini\"",
            LutrisGameArgs.Read(Fixture("lutris-wrapped-args.yml")));
        Assert.Equal("-name \"Pilot One\" -it's", LutrisGameArgs.Read(Fixture("lutris-double-quoted-args.yml")));
    }

    [Fact]
    public void Lutris_InsertsArgsInSortedPlace_AndTakesItOutAgainByteForByte()
    {
        var file = Fixture("lutris-no-args.yml");
        var written = LutrisGameArgs.Write(file, "-dx11 'it''s'");
        Assert.StartsWith("game:\n  arch: win64\n  args: '-dx11 ''it''''s'''\n  exe: /home/player/Games/sample-quest", Text(written), StringComparison.Ordinal);
        Assert.Equal("-dx11 'it''s'", LutrisGameArgs.Read(written));
        Assert.Equal(file, LutrisGameArgs.Write(written, null));
    }

    [Fact]
    public void Lutris_ReplacesAWrappedValue_WholeLinesOnly()
    {
        var file = Fixture("lutris-wrapped-args.yml");
        var written = Text(LutrisGameArgs.Write(file, "-skipintro -dx11"));
        Assert.StartsWith("game:\n  args: '-skipintro -dx11'\n  exe: /home/player/Games/sample-racer/drive_c/Sample Racer/racer.exe\n", written, StringComparison.Ordinal);
        Assert.EndsWith("wine:\n  version: wine-ge-8-26\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Lutris_NeverTouchesTheInstallerScriptsGameSection()
    {
        var file = Fixture("lutris-empty-args.yml");
        var written = Text(LutrisGameArgs.Write(file, "-dx11"));
        Assert.Equal(Text(file).Replace("  args: ''\n", "  args: '-dx11'\n", StringComparison.Ordinal), written);
        Assert.Contains("    args: -installer-only\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Lutris_FindsTheTopLevelGame_WhenTheScriptComesFirst()
    {
        var file = Encoding.UTF8.GetBytes("script:\n  game:\n    args: -installer\n    exe: setup.exe\ngame:\n  exe: game.exe\n");
        var written = Text(LutrisGameArgs.Write(file, "-dx11"));
        Assert.Equal("script:\n  game:\n    args: -installer\n    exe: setup.exe\ngame:\n  args: '-dx11'\n  exe: game.exe\n", written);
    }

    [Fact]
    public void Lutris_RefusesAFileWithoutAGameSection_AndABlockScalar()
    {
        Assert.Throws<FormatException>(() => LutrisGameArgs.Write(Encoding.UTF8.GetBytes("name: x\nsystem:\n  env: {}\n"), "-a"));
        Assert.Throws<FormatException>(() => LutrisGameArgs.Write(Encoding.UTF8.GetBytes("game:\n  args: |\n    -a\n  exe: x\n"), "-b"));
    }

    // ── Heroic GamesConfig ────────────────────────────────────────────────────────────────

    [Fact]
    public void Heroic_ReadsLauncherArgsUnderTheAppName()
    {
        Assert.Null(HeroicGameArgs.Read(Fixture("heroic-legendary-no-args.json"), "Fakeapp0000000000000000000000001"));
        Assert.Equal("-nointro -lang=pt-BR", HeroicGameArgs.Read(Fixture("heroic-gog-args.json"), "1400000001"));
        Assert.Throws<FormatException>(() => HeroicGameArgs.Read(Fixture("heroic-gog-args.json"), "someone-else"));
    }

    [Fact]
    public void Heroic_WritesAsHeroicDoes_AndTheRestOfTheFileIsUnchanged()
    {
        var file = Fixture("heroic-gog-args.json");
        // Writing the value it already has gives Heroic's own bytes back (JSON.stringify(config, null, 2), non-ASCII kept).
        Assert.Equal(Text(file), Text(HeroicGameArgs.Write(file, "1400000001", "-nointro -lang=pt-BR")));
        var written = Text(HeroicGameArgs.Write(file, "1400000001", "-nointro -lang=pt-BR -dx11 '<x>'"));
        Assert.Equal(Text(file).Replace("\"-nointro -lang=pt-BR\"", "\"-nointro -lang=pt-BR -dx11 '<x>'\"", StringComparison.Ordinal), written);
    }

    [Fact]
    public void Heroic_AddsTheKey_AndTakesItOutAgainByteForByte()
    {
        var file = Fixture("heroic-legendary-no-args.json");
        var written = HeroicGameArgs.Write(file, "Fakeapp0000000000000000000000001", "-dx11");
        Assert.Equal("-dx11", HeroicGameArgs.Read(written, "Fakeapp0000000000000000000000001"));
        Assert.Equal(file, HeroicGameArgs.Write(written, "Fakeapp0000000000000000000000001", null));
    }
}
