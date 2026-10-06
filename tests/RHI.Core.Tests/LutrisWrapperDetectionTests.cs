using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>DET-05: native wrapper scripts in a shared bin directory must never become a game root.</summary>
public sealed class LutrisWrapperDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-lutris-wrap-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private string ConfigRoot => Path.Combine(Home, ".local", "share", "lutris", "games");
    private string Bin => Path.Combine(Home, ".local", "bin");

    public LutrisWrapperDetectionTests()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(Bin);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Script(string name)
    {
        var path = Path.Combine(Bin, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\nexec true\n");
        return path;
    }

    private string Pe(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 0, 0]);
        return path;
    }

    private string WinePrefix(string name)
    {
        var prefix = Path.Combine(Home, "Applications", name);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        return prefix;
    }

    private string Config(string stem, string yaml)
    {
        var path = Path.Combine(ConfigRoot, stem + ".yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void WrappersSharingABinDirectory_StayDistinctAndNeverUseTheBinDirectory()
    {
        var gameDir = Path.Combine(Home, "Games", "blue-prince-xodus");
        var gameExe = Pe(gameDir, "BLUE PRINCE.exe");
        var blueWrapper = Script("blue-prince-xodus-launch");
        var rekordWrapper = Script("rekordbox-linux");
        var prefix = WinePrefix("rekordbox");
        Config("blue-prince-1763483489", $"""
            game:
              exe: {blueWrapper}
              working_dir: {gameDir}
            game_slug: blue-prince
            name: Blue Prince
            """);
        var rekordConfig = Config("rekordbox-setup-1761693617", $"""
            game:
              exe: {rekordWrapper}
              prefix: {prefix}
            name: Rekordbox
            runner: linux
            wine:
              version: rekordbox-wine-11.13
            """);
        // A config whose wrapper no longer exists is stale: it stays hidden as before.
        Config("gone-1700000000", $"""
            game:
              exe: {Path.Combine(Bin, "gone-launch")}
              prefix: {prefix}
            name: Gone
            """);

        var games = LutrisGameDetector.DetectIn([ConfigRoot], Home, CancellationToken.None);

        Assert.Equal(["Blue Prince", "Rekordbox"], games.Select(g => g.Name).Order().ToArray());
        Assert.DoesNotContain(games, g => PathEq(g.InstallPath, Bin) || PathEq(g.InstallPath, prefix));

        var blue = games.Single(g => g.Name == "Blue Prince");
        Assert.Equal(gameDir, blue.InstallPath);
        Assert.Equal(gameExe, blue.ExecutablePath);
        Assert.Equal(blueWrapper, blue.LaunchExecutablePath);
        Assert.False(blue.RequiresExplicitPatchTarget);

        var rekord = games.Single(g => g.Name == "Rekordbox");
        Assert.True(rekord.RequiresExplicitPatchTarget);
        Assert.Null(rekord.ExecutablePath);
        Assert.Equal(rekordWrapper, rekord.LaunchExecutablePath);
        Assert.Equal(rekordConfig, rekord.LibraryIdentityPath);
        Assert.Equal(prefix, rekord.PrefixPath);
        Assert.Equal("lutris 'lutris:rungame/rekordbox-setup'", rekord.LaunchCommand);

        // The flag survives identity resolution, so nothing is probed or deployed until Edit game.
        LinuxGameIdentityService.ApplyDetectedIdentity(rekord);
        Assert.True(rekord.RequiresExplicitPatchTarget);
        Assert.False(rekord.HasConfirmedPatchTarget);
    }

    [Fact]
    public void WindowsExe_KeepsItsOwnFolder_EvenWhenWorkingDirPointsElsewhere()
    {
        var prefix = WinePrefix("battlenet");
        var launcherDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net");
        var launcher = Pe(launcherDir, "Battle.net Launcher.exe");
        var gamesDir = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "Games")).FullName;
        Config("battlenet-1763437958", $"""
            game:
              exe: {launcher}
              prefix: {prefix}
              working_dir: {gamesDir}
            name: Battle.net
            """);

        var game = Assert.Single(LutrisGameDetector.DetectIn([ConfigRoot], Home, CancellationToken.None));

        Assert.Equal(launcherDir, game.InstallPath);
        Assert.Equal(launcher, game.ExecutablePath);
        Assert.Null(game.LibraryIdentityPath);
    }

    [Fact]
    public void DistinctSlugsResolvingToOneFolder_GetDistinctLibraryIdentities()
    {
        var shared = Path.Combine(Home, "Games", "collection");
        var first = Pe(shared, "First.exe");
        var second = Pe(shared, "Second.exe");
        var firstConfig = Config("first-1700000001", $"game:\n  exe: {first}\nname: First\n");
        var secondConfig = Config("second-1700000002", $"game:\n  exe: {second}\nname: Second\n");

        var games = LutrisGameDetector.DetectIn([ConfigRoot], Home, CancellationToken.None);

        Assert.All(games, g => Assert.Equal(shared, g.InstallPath));
        Assert.Equal(
            new[] { firstConfig, secondConfig }.Order(),
            games.Select(g => g.LibraryIdentityPath!).Order());
    }

    [Theory]
    [InlineData("/usr/bin", true)]
    [InlineData("/usr/local/bin/", true)]
    [InlineData("~/.local/bin", true)]
    [InlineData("~/bin", true)]
    [InlineData("~", true)]
    [InlineData("~/Games/blue-prince-xodus", false)]
    public void SharedDirectories_AreNeverGameRoots(string path, bool shared)
    {
        var expanded = path.StartsWith('~') ? Home + path[1..] : path;
        Assert.Equal(shared, LutrisGameDetector.IsSharedDirectory(expanded, Home));
    }

    private static bool PathEq(string a, string b)
        => Path.GetFullPath(a).TrimEnd('/') == Path.GetFullPath(b).TrimEnd('/');
}
