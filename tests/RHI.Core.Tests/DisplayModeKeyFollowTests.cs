using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M2: a game's display mode state (display-modes.json, keyed by the library key) follows the game when
/// its key changes, through the real paths: an install folder move (and Repair after move, which stages the same store),
/// and a scan adopting the keys older builds wrote.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DisplayModeKeyFollowTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-dmkey-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    private static void InCustomTv(DisplayModeStore store, string key)
    {
        store.CreateCustom(key);
        store.SetActive(key, "tv");
        store.SetOriginal(key, DisplayModeItemKeys.RdxPeakNits, "400");
    }

    private static void AssertFollowed(DisplayModeStore store, string oldKey, string newKey)
    {
        Assert.True(store.HasCustom(newKey));
        Assert.Equal("tv", store.ActiveModeId(newKey));
        Assert.Equal("400", store.OriginalValue(newKey, DisplayModeItemKeys.RdxPeakNits));
        Assert.False(store.HasCustom(oldKey));
        Assert.Null(store.ActiveModeId(oldKey));
    }

    [Fact]
    public async Task AnInstallFolderMove_TakesTheGamesDisplayModeStateToItsNewKey()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var oldDir = Directory.CreateDirectory(Path.Combine(_root, "Old")).FullName;
        var newDir = Directory.CreateDirectory(Path.Combine(_root, "New")).FullName;
        File.WriteAllText(Path.Combine(oldDir, "Game.exe"), "exe");
        File.WriteAllText(Path.Combine(newDir, "Game.exe"), "exe");
        var old = new DetectedGame { Name = "Game", InstallPath = oldDir, ExecutablePath = Path.Combine(oldDir, "Game.exe"), BinaryDirectory = oldDir, Source = "Manual", Runner = "Proton" };
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
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        string oldKey = LinuxGameLibraryStore.GameKey(old), newKey = LinuxGameLibraryStore.GameKey(moved);
        Assert.NotEqual(oldKey, newKey);
        InCustomTv(dm.Store, oldKey);
        new DisplayModeFileStore(dm.FilePath).Save(dm.Store.Snapshot());

        var result = await fx.Vm.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.True(result.Success, result.Message);
        AssertFollowed(dm.Store, oldKey, newKey);
        var saved = new DisplayModeFileStore(dm.FilePath).Load(out _)!;
        Assert.True(saved.Games.ContainsKey(newKey));
        Assert.False(saved.Games.ContainsKey(oldKey));
    }

    [Fact]
    public async Task AScan_AdoptsTheKeyAnOlderBuildWrote()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Steam Game")).FullName;
        var exe = Path.Combine(dir, "game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "Steam Game", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, SteamAppId = 70101, Source = "Steam", Runner = "Proton" };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        string oldKey = LinuxGameLibraryStore.LegacyGameKey(game), newKey = LinuxGameLibraryStore.GameKey(game);
        Assert.NotEqual(oldKey, newKey);
        InCustomTv(dm.Store, oldKey);

        await fx.Vm.RefreshCommand.ExecuteAsync(null);

        AssertFollowed(dm.Store, oldKey, newKey);
        Assert.True(new DisplayModeFileStore(dm.FilePath).Load(out _)!.Games.ContainsKey(newKey));
    }

    [Fact]
    public void StateAlreadyUnderTheNewKey_IsTheNewerOne_AndStays()
    {
        var store = new DisplayModeStore();
        var state = new DisplayModeGameState(store, new DisplayModeFileStore(Path.Combine(_root, "dm.json")));
        InCustomTv(store, "old");
        store.CreateCustom("new");

        Assert.False(state.AdoptOlderKeys("new", ["old"]));
        Assert.Null(store.ActiveModeId("new"));
        Assert.Equal("tv", store.ActiveModeId("old"));
    }
}
