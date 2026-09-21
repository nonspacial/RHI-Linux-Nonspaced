using RHI.Core.Models;
using RHI.Platform.Linux.Settings;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class GameIdentityEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-game-identity-" + Guid.NewGuid().ToString("N"));

    public GameIdentityEditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SharedLauncher_IsLaunchOnlyUntilPatchTargetConfirmed()
    {
        var launcherRoot = Path.Combine(_root, "Battle.net");
        Directory.CreateDirectory(launcherRoot);
        var launcher = Path.Combine(launcherRoot, "Battle.net Launcher.exe");
        File.WriteAllBytes(launcher, [0]);

        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = launcherRoot,
            ExecutablePath = launcher,
            LaunchCommand = "heroic://launch/example"
        };

        LinuxGameIdentityService.ApplyDetectedIdentity(game);

        Assert.Equal("Heroic", game.LibraryProvider);
        Assert.Equal("Battle.net", game.LaunchProvider);
        Assert.Equal(launcher, game.LaunchExecutablePath);
        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.Null(game.ExecutablePath);
        Assert.Null(game.BinaryDirectory);
        Assert.False(game.HasConfirmedPatchTarget);
        Assert.Equal("heroic://launch/example", game.LaunchCommand);
    }

    [Fact]
    public void ManualPatchOverride_PreservesLaunchAndMovesRuntimeAuthority()
    {
        var launcherRoot = Path.Combine(_root, "Launcher");
        Directory.CreateDirectory(launcherRoot);
        var launcher = Path.Combine(launcherRoot, "Battle.net Launcher.exe");
        File.WriteAllBytes(launcher, [0]);

        var gameRoot = Path.Combine(_root, "ActualGame");
        var binaries = Path.Combine(gameRoot, "bin");
        Directory.CreateDirectory(binaries);
        var gameExe = Path.Combine(binaries, "Game.exe");
        File.WriteAllBytes(gameExe, [0]);

        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = launcherRoot,
            ExecutablePath = launcher,
            LaunchExecutablePath = launcher,
            LaunchCommand = "heroic://launch/example"
        };
        var edit = new LinuxGameEditOverride
        {
            LaunchProvider = "Battle.net",
            PatchExecutablePath = gameExe,
            GameRootPath = gameRoot,
            BinaryDirectory = binaries
        }.Normalize();

        LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);

        Assert.Equal(launcher, game.LaunchExecutablePath);
        Assert.Equal("heroic://launch/example", game.LaunchCommand);
        Assert.Equal(Path.GetFullPath(gameRoot), game.InstallPath);
        Assert.Equal(Path.GetFullPath(gameExe), game.ExecutablePath);
        Assert.Equal(Path.GetFullPath(binaries), game.BinaryDirectory);
        Assert.Equal("Manual", game.PatchTargetAuthority);
        Assert.True(game.HasConfirmedPatchTarget);
    }

    [Fact]
    public void LibraryGameKey_RemainsStableAcrossPatchRootOverride()
    {
        var libraryRoot = Path.Combine(_root, "LauncherEntry");
        var patchRoot = Path.Combine(_root, "ActualGame");
        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = libraryRoot,
            LibraryIdentityPath = libraryRoot
        };

        var before = LinuxGameLibraryStore.GameKey(game);
        game.InstallPath = patchRoot;
        var after = LinuxGameLibraryStore.GameKey(game);

        Assert.Equal(before, after);
    }

    [Fact]
    public void GameEditStore_RoundTripsPatchTargetAndProvider()
    {
        var libraryPath = Path.Combine(_root, "data", "game-library.json");
        var store = new LinuxGameLibraryStore(libraryPath);
        var state = new LinuxGameLibraryState();
        state.GameEdits["heroic|nosteam|example"] = new LinuxGameEditOverride
        {
            LaunchProvider = "Battle.net",
            PatchExecutablePath = Path.Combine(_root, "Game", "Game.exe"),
            GameRootPath = Path.Combine(_root, "Game"),
            BinaryDirectory = Path.Combine(_root, "Game", "bin")
        };

        store.Save(state);
        var loaded = store.Load();
        var edit = Assert.Single(loaded.GameEdits).Value;

        Assert.Equal("Battle.net", edit.LaunchProvider);
        Assert.EndsWith(Path.Combine("Game", "Game.exe"), edit.PatchExecutablePath);
        Assert.EndsWith("Game", edit.GameRootPath);
        Assert.EndsWith(Path.Combine("Game", "bin"), edit.BinaryDirectory);
    }

    [Fact]
    public void ManualPatchAuthority_DoesNotFallBackToAnotherExeWhenTargetDisappears()
    {
        var root = Path.Combine(_root, "ManualMissing");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "ChosenGame.exe");
        var other = Path.Combine(root, "OtherLauncher.exe");
        File.WriteAllBytes(target, [0]);
        File.WriteAllBytes(other, [0]);

        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = root,
            ExecutablePath = target,
            BinaryDirectory = root,
            RequiresExplicitPatchTarget = true,
            PatchTargetAuthority = "Manual"
        };
        File.Delete(target);

        DetectionHelpers.ApplyRuntimeMetadata(game);

        Assert.Equal(target, game.ExecutablePath);
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);
        Assert.Contains("não substituirá", game.GraphicsApiEvidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedLauncher_PreservesDistinctManifestPatchExecutable()
    {
        var root = Path.Combine(_root, "ManifestTarget");
        var launcherDir = Path.Combine(root, "Battle.net");
        var gameDir = Path.Combine(root, "Game");
        Directory.CreateDirectory(launcherDir);
        Directory.CreateDirectory(gameDir);
        var launcher = Path.Combine(launcherDir, "Battle.net Launcher.exe");
        var target = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(launcher, [0]);
        File.WriteAllBytes(target, [0]);

        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = root,
            LaunchExecutablePath = launcher,
            ExecutablePath = target,
            BinaryDirectory = gameDir,
            ManifestLaunchExecutable = "Game/Game.exe"
        };

        LinuxGameIdentityService.ApplyDetectedIdentity(game);

        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(target, game.ExecutablePath);
        Assert.Equal(gameDir, game.BinaryDirectory);
        Assert.Equal("Manifest", game.PatchTargetAuthority);
    }

    [Fact]
    public void NormalGameExecutable_DoesNotRequireSeparatePatchTarget()
    {
        var root = Path.Combine(_root, "DirectGame");
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Game.exe");
        File.WriteAllBytes(exe, [0]);
        var game = new DetectedGame
        {
            Name = "Direct Game",
            Source = "Wine",
            InstallPath = root,
            ExecutablePath = exe
        };

        LinuxGameIdentityService.ApplyDetectedIdentity(game);

        Assert.False(game.RequiresExplicitPatchTarget);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(exe, game.LaunchExecutablePath);
        Assert.True(game.HasConfirmedPatchTarget);
    }

    [Fact]
    public void ProviderBadges_KeepLibrarySourceSeparateFromLauncher()
    {
        var launcher = Path.Combine(_root, "Battle.net", "Battle.net Launcher.exe");
        var game = new DetectedGame
        {
            Name = "Example Game",
            Source = "Heroic/Sideload",
            InstallPath = Path.GetDirectoryName(launcher)!,
            ExecutablePath = launcher
        };

        LinuxGameIdentityService.ApplyDetectedIdentity(game);

        Assert.Equal("Heroic", game.LibraryProvider);
        Assert.Equal("Battle.net", game.LaunchProvider);
        Assert.Equal("heroic", LinuxGameIdentityService.ProviderIconKey(game.LibraryProvider));
        Assert.Equal("battlenet", LinuxGameIdentityService.ProviderIconKey(game.LaunchProvider));
    }

    [Fact]
    public void ProviderNamedInstallDirectory_DoesNotTurnRealGameExeIntoSharedLauncher()
    {
        var gameExe = "/prefix/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Example/Game.exe";
        Assert.Null(LinuxGameIdentityService.InferProviderFromExecutable(gameExe));
    }

    [Theory]
    [InlineData("/prefix/Program Files (x86)/Battle.net/Battle.net Launcher.exe", "Battle.net")]
    [InlineData("/prefix/Program Files/Electronic Arts/EA Desktop/EADesktop.exe", "EA")]
    [InlineData("/prefix/Program Files/Epic Games/Launcher/Portal/Binaries/Win64/EpicGamesLauncher.exe", "Epic")]
    [InlineData("/prefix/Program Files (x86)/GOG Galaxy/GalaxyClient.exe", "GOG")]
    [InlineData("/prefix/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe", "Ubisoft")]
    [InlineData("/prefix/Program Files/Rockstar Games/Launcher/RockstarGamesLauncher.exe", "Rockstar")]
    [InlineData("/prefix/Program Files (x86)/Steam/steam.exe", "Steam")]
    [InlineData("/prefix/Xbox/XboxPcApp.exe", "Xbox")]
    public void SharedLauncherInference_IsGenericAcrossProviders(string executable, string expected)
        => Assert.Equal(expected, LinuxGameIdentityService.InferProviderFromExecutable(executable));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
