using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #176: Steam's non-Steam shortcuts (<c>userdata/&lt;user&gt;/config/shortcuts.vdf</c>): real Windows games become cards
/// started through <c>steam://rungameid/</c>, store clients added this way are the parents of the games they installed in
/// the shortcut's Proton prefix, and installers, native programs and scripts are not listed.
/// </summary>
public sealed class SteamShortcutTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private sealed record Shortcut(string AppName, string Exe, string? StartDir = null, uint? AppId = null);

    // Steam's binary key/values, laid out as Steam writes shortcuts.vdf (the owner's real file has this shape).
    private static byte[] Vdf(params Shortcut[] shortcuts)
    {
        using var data = new MemoryStream();
        void Byte(byte b) => data.WriteByte(b);
        void CString(string text) { data.Write(Encoding.UTF8.GetBytes(text)); Byte(0); }
        void Str(string key, string value) { Byte(0x01); CString(key); CString(value); }
        void Int(string key, int value) { Byte(0x02); CString(key); data.Write(BitConverter.GetBytes(value)); }
        Byte(0x00); CString("shortcuts");
        for (var i = 0; i < shortcuts.Length; i++)
        {
            var s = shortcuts[i];
            Byte(0x00); CString(i.ToString());
            if (s.AppId is { } id) Int("appid", unchecked((int)id));
            Str("AppName", s.AppName);
            Str("Exe", $"\"{s.Exe}\"");
            Str("StartDir", s.StartDir is null ? "" : $"\"{s.StartDir}\"");
            Str("icon", "");
            Str("LaunchOptions", "");
            Int("IsHidden", 0);
            Int("LastPlayTime", 1_700_000_000);
            Byte(0x07); CString("Unknown64"); data.Write(BitConverter.GetBytes(42UL));
            Byte(0x00); CString("tags"); Byte(0x08);
            Byte(0x08);
        }
        Byte(0x08); Byte(0x08);
        return data.ToArray();
    }

    private string SteamRoot => _tree.Path_("steam");

    private void Shortcuts(string user, params Shortcut[] shortcuts)
        => _tree.Bytes(Vdf(shortcuts), "steam", "userdata", user, "config", "shortcuts.vdf");

    private string ProtonPrefix(uint id)
    {
        var prefix = _tree.Dir("steam", "steamapps", "compatdata", id.ToString(), "pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        return prefix;
    }

    private void CompatTool(uint id, string tool)
        => _tree.File($$"""
                      "InstallConfigStore"
                      {
                        "Software" { "Valve" { "Steam" { "CompatToolMapping" {
                          "{{id}}" { "name" "{{tool}}" "config" "" "priority" "250" }
                        } } } }
                      }
                      """, "steam", "config", "config.vdf");

    private IReadOnlyList<DetectedGame> Detect() => SteamGameDetector.DetectIn([SteamRoot], CancellationToken.None);

    [Fact]
    public void Parse_ReadsTheRealLayout_AndTheShortcutsGameId()
    {
        // The owner's file: appid bytes 0F 54 3B F7 (a signed int32 on disk; Steam's id is unsigned).
        var shortcuts = SteamShortcuts.Parse(Vdf(new Shortcut("Game", "/g/Game.exe", "/g/", 0xF73B540F)));

        var shortcut = Assert.Single(shortcuts);
        Assert.Equal(0xF73B540Fu, shortcut.AppId);
        Assert.Equal("Game", shortcut.Name);
        Assert.Equal("/g/Game.exe", shortcut.Exe); // quotes taken off
        Assert.Equal("/g/", shortcut.StartDir);
        Assert.Equal((0xF73B540FUL << 32) | 0x02000000UL, shortcut.GameId);
        Assert.Equal(17_814_925_174_428_925_952UL, shortcut.GameId);
    }

    [Fact]
    public void Parse_AFileWithoutAppIds_UsesSteamsDerivedId()
    {
        var shortcut = Assert.Single(SteamShortcuts.Parse(Vdf(new Shortcut("Game", "/g/Game.exe"))));

        // Steam's legacy id: CRC-32 of the quoted program followed by the name, top bit set.
        Assert.Equal(SteamShortcuts.LegacyAppId("\"/g/Game.exe\"", "Game"), shortcut.AppId);
        Assert.Equal(0x80000000u, shortcut.AppId & 0x80000000u);
    }

    [Fact]
    public void Parse_ATruncatedFile_IsRefused()
    {
        var data = Vdf(new Shortcut("Game", "/g/Game.exe", AppId: 1));
        Assert.Throws<InvalidDataException>(() => SteamShortcuts.Parse(data[..(data.Length / 2)]));
    }

    [Fact]
    public void AWindowsGameShortcut_IsACard_StartedThroughSteamsRunGameId()
    {
        const uint id = 0xF73B540F;
        var folder = _tree.Dir("games", "Hollow");
        var bin = _tree.Dir("games", "Hollow", "bin");
        var exe = _tree.Exe("games", "Hollow", "bin", "Hollow.exe");
        Shortcuts("1485397627", new Shortcut("Hollow Game", exe, folder + "/", id));
        var prefix = ProtonPrefix(id);
        CompatTool(id, "proton_9");

        var game = Assert.Single(Detect());

        Assert.Equal("Hollow Game", game.Name);
        Assert.Equal(folder, game.InstallPath);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(SteamGameDetector.ShortcutSource, game.Source);
        Assert.Equal("Steam", LinuxGameIdentityService.InferLibraryProvider(game));
        Assert.Null(game.SteamAppId); // not a Steam app: its library key is its folder
        Assert.Equal(prefix, game.PrefixPath);
        Assert.Equal("Proton", game.Runner);
        Assert.Equal("proton_9", game.CompatibilityTool);
        Assert.EndsWith(" steam://rungameid/17814925174428925952", game.LaunchCommand);
        Assert.Equal(LaunchHandoff.Steam, LaunchHandoffClassifier.Classify(game));
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(bin, Path.GetDirectoryName(game.ExecutablePath));
    }

    [Theory]
    [InlineData("/home/u/.var/app/com.valvesoftware.Steam/data/Steam", false, "flatpak run com.valvesoftware.Steam steam://rungameid/42")]
    [InlineData("/home/u/.local/share/Steam", true, "steam steam://rungameid/42")]
    [InlineData("/home/u/.local/share/Steam", false, "xdg-open steam://rungameid/42")]
    public void ShortcutLaunchCommand_FollowsTheSteamClient(string root, bool hostSteam, string expected)
        => Assert.Equal(expected, SteamGameDetector.ShortcutLaunchCommandFor(42, root, hostSteam));

    [Fact]
    public void InstallersNativeProgramsScriptsAndLibraryFolders_AreNotGames()
    {
        // The owner's one shortcut: ReShade's installer in his games library folder.
        var library = _tree.Dir("media", "Games");
        var installer = _tree.Exe("media", "Games", "ReShade_Setup_6.8.0_Addon.exe");
        var native = _tree.File("#!/bin/sh\n", "apps", "tool.sh");
        var elf = _tree.Bytes([0x7F, (byte)'E', (byte)'L', (byte)'F', .. new byte[64]], "apps", "Program");
        var missing = _tree.Path_("gone", "Gone.exe");
        Shortcuts("1", new Shortcut("ReShade_Setup_6.8.0_Addon.exe", installer, library + "/", 1),
            new Shortcut("Tool", native, AppId: 2), new Shortcut("Program", elf, AppId: 3), new Shortcut("Gone", missing, AppId: 4),
            new Shortcut("Heroic game", "/usr/bin/flatpak", AppId: 5));

        Assert.Empty(Detect());
    }

    [Fact]
    public void AStartFolderThatIsNoGameFolder_FallsBackToTheProgramsFolder()
    {
        var exe = _tree.Exe("games", "Solo", "Solo.exe");
        // A start folder that does not hold the program, and one that is a Wine prefix root, are not the game's folder.
        Shortcuts("1", new Shortcut("Solo", exe, _tree.Dir("elsewhere"), 7));

        var game = Assert.Single(Detect());

        Assert.Equal(Path.GetDirectoryName(exe), game.InstallPath);
    }

    [Fact]
    public void AStoreClientShortcut_IsTheParentOfItsGames_NotACard()
    {
        const uint id = 0x80001234;
        var prefix = ProtonPrefix(id);
        var client = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        var anno = Pe(Path.Combine(prefix, "drive_c", "UbiGames", "Anno 1800"), "Anno1800.exe", 8192);
        // Another store's game in the same prefix: no client of its own added to Steam, so no launch command.
        var overwatch = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Overwatch"), "Overwatch.exe", 8192);
        Registry(prefix,
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher""", [("InstallDir", """C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\""")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\4553""", [("InstallDir", "C:/UbiGames/Anno 1800/")]),
            ("""Software\\Wow6432Node\\Origin Games\\OFB-1""", [("InstallDir", "C:\\Program Files (x86)\\Overwatch\\"), ("DisplayName", "Overwatch")]));
        Shortcuts("1", new Shortcut("Ubisoft Connect", client, Path.GetDirectoryName(client), id));
        CompatTool(id, "GE-Proton10-18");

        var games = Detect().ToDictionary(g => g.Name);

        Assert.Equal(["Anno 1800", "Overwatch"], games.Keys.Order(StringComparer.Ordinal));
        var game = games["Anno 1800"];
        Assert.Equal(Path.GetDirectoryName(anno), game.InstallPath);
        Assert.Equal(anno, game.ExecutablePath);
        Assert.Equal(client, game.LaunchExecutablePath);
        Assert.Equal(prefix, game.PrefixPath);
        Assert.Equal("GE-Proton10-18", game.CompatibilityTool);
        Assert.EndsWith($" steam://rungameid/{((ulong)id << 32) | 0x02000000UL}", game.LaunchCommand);
        Assert.Equal("Ubisoft Connect", game.ParentLauncherName);
        Assert.Equal(LaunchHandoff.Steam, LaunchHandoffClassifier.Classify(game));
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(anno, game.ExecutablePath);

        var other = games["Overwatch"];
        Assert.Equal(overwatch, other.ExecutablePath);
        Assert.Null(other.LaunchCommand);
        Assert.Null(other.ParentLauncherName);
        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(other)); // not handed to Steam without a command
    }

    [Fact]
    public void AStoreClientWithoutAPrefix_GivesNoCards()
    {
        var client = Pe(_tree.Path_("clients", "EA Desktop"), "EADesktop.exe");
        Shortcuts("1", new Shortcut("EA app", client, AppId: 9));

        Assert.Empty(Detect());
    }

    [Fact]
    public async Task OneUnreadableUserFile_LeavesTheOtherUsersShortcuts()
    {
        var exe = _tree.Exe("games", "Kept", "Kept.exe");
        _tree.Bytes([0x00, (byte)'s', 0x00, 0x00, (byte)'0'], "steam", "userdata", "1", "config", "shortcuts.vdf");
        Shortcuts("2", new Shortcut("Kept", exe, AppId: 11));

        var (games, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(Detect()));

        Assert.Equal("Kept", Assert.Single(games).Name);
        Assert.Contains(warnings, w => w.Contains("shortcuts.vdf", StringComparison.Ordinal));
    }

    private static void Registry(string prefix, params (string Key, (string Name, string Value)[] Values)[] keys)
    {
        var text = new StringBuilder("WINE REGISTRY Version 2\n;; All keys relative to \\\\Machine\n\n#arch=win64\n\n");
        foreach (var (key, values) in keys)
        {
            text.Append('[').Append(key).Append("] 1700000000\n#time=1d000000000000\n");
            foreach (var (name, value) in values)
                text.Append('"').Append(name).Append("\"=\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
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
