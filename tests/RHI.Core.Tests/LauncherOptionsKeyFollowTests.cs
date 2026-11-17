using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Launch.LauncherOptions;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #318 (w14 last fixes): a game's launcher record — what RHI wrote into Steam / Lutris / Heroic launch options, and the
/// link the button made — follows the game when its library key changes, through the real paths: an install folder move,
/// Repair after move (the same store, staged with its commit) and a scan adopting the keys older builds wrote.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LauncherOptionsKeyFollowTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-lokey-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    private static LauncherOptionsRecord Record(LauncherKind kind) => new()
    {
        Launcher = kind, Arguments = "-dx11", OperationId = "op-1",
        Files = [new LauncherOptionsFileRecord { Path = "/home/u/.config/lutris/games/game-1.yml", OriginalPresent = false, Written = "-dx11", Added = "-dx11" }]
    };

    private static void Seed(LauncherOptionsStore store, string key, LauncherKind kind)
    {
        var data = store.Load();
        data.Games[key] = Record(kind);
        store.Save(data);
    }

    [Fact]
    public async Task AnInstallFolderMove_TakesTheLauncherRecordToTheGamesNewKey()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var oldDir = Directory.CreateDirectory(Path.Combine(_root, "Old")).FullName;
        var newDir = Directory.CreateDirectory(Path.Combine(_root, "New")).FullName;
        File.WriteAllText(Path.Combine(oldDir, "Game.exe"), "exe");
        File.WriteAllText(Path.Combine(newDir, "Game.exe"), "exe");
        var old = new DetectedGame { Name = "Game", InstallPath = oldDir, ExecutablePath = Path.Combine(oldDir, "Game.exe"), BinaryDirectory = oldDir, Source = "Lutris", Runner = "Wine" };
        var moved = old.ShallowClone();
        moved.InstallPath = newDir;
        moved.ExecutablePath = Path.Combine(newDir, "Game.exe");
        moved.BinaryDirectory = newDir;
        var addon = Path.Combine(oldDir, "dof.addon64");
        File.WriteAllText(addon, "dof");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(old), GameName = old.Name, Component = ManagedComponentKind.DofFix, BinaryDirectory = oldDir,
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        });
        fx.Managed.FolderMoveRunningProbe = new NotRunning();
        var args = fx.Vm.LaunchArguments;
        string oldKey = LinuxGameLibraryStore.GameKey(old), newKey = LinuxGameLibraryStore.GameKey(moved);
        Assert.NotEqual(oldKey, newKey);
        Seed(args.LauncherOptions.Store, oldKey, LauncherKind.Lutris);

        var result = await fx.Vm.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.True(result.Success, result.Message);
        Assert.True(args.InLauncher(moved)); // the button's link follows: Remove from the new key finds the record
        Assert.False(args.InLauncher(old));
        Assert.Equal("op-1", args.LauncherRecord(moved)!.OperationId);
    }

    [Fact]
    public async Task AScan_AdoptsTheKeyAnOlderBuildWrote()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Steam Game")).FullName;
        var exe = Path.Combine(dir, "game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "Steam Game", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, SteamAppId = 70102, Source = "Steam", Runner = "Proton" };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var args = fx.Vm.LaunchArguments;
        string oldKey = LinuxGameLibraryStore.LegacyGameKey(game), newKey = LinuxGameLibraryStore.GameKey(game);
        Assert.NotEqual(oldKey, newKey);
        Seed(args.LauncherOptions.Store, oldKey, LauncherKind.Steam);

        await fx.Vm.RefreshCommand.ExecuteAsync(null);

        var saved = args.LauncherOptions.Store.Load().Games;
        Assert.True(saved.ContainsKey(newKey));
        Assert.False(saved.ContainsKey(oldKey));
        Assert.True(args.InLauncher(game));
    }

    [Fact]
    public async Task RepairAfterMove_StagesTheLauncherRecordsWithItsCommit()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var store = fx.Vm.LaunchArguments.LauncherOptions.Store;
        Assert.Contains(fx.Vm.RepairUiStores, s => ReferenceEquals(s, store));
        var oldDir = Path.Combine(_root, "a", "Game");
        var newDir = Directory.CreateDirectory(Path.Combine(_root, "b", "Game")).FullName;
        var moved = new DetectedGame { Name = "Game", InstallPath = newDir, Source = "Lutris", Runner = "Wine" };
        var oldGame = moved.ShallowClone();
        oldGame.InstallPath = oldDir;
        Seed(store, LinuxGameLibraryStore.GameKey(oldGame), LauncherKind.Lutris);

        var staged = store.StageMove(new GameStateMove(moved, [(oldDir, newDir)], []));
        Assert.NotNull(staged);
        RHI.Platform.Linux.Persistence.StagedFileCommit.Commit([staged!]);

        Assert.Equal([LinuxGameLibraryStore.GameKey(moved)], store.GameKeys);
    }

    [Fact]
    public void ARecordAlreadyUnderTheNewKey_Stays_AndTheOlderOneIsKept()
    {
        var store = new LauncherOptionsStore(Path.Combine(_root, "launcher-options.json"));
        Seed(store, "old", LauncherKind.Steam);
        var data = store.Load();
        data.Games["new"] = new LauncherOptionsRecord { Launcher = LauncherKind.Steam, OperationId = "newer" };
        store.Save(data);

        Assert.False(store.AdoptOlderKeys([("new", ["old"])]));
        Assert.Equal("newer", store.Get("new")!.OperationId);
        Assert.Equal("op-1", store.Get("old")!.OperationId);
    }
}
