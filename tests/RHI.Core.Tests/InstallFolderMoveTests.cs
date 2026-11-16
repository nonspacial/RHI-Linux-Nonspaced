using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #293 (upstream 2.9.2 install folder migration): a game's RHI installs move from the folder they were installed in to
/// its new one — copied, verified, then removed, journaled and rolled back as a whole; the game's own files RHI kept as
/// .original return in the old folder, a game file in the new folder is kept as .original, nothing a user changed is
/// overwritten, and nothing moves while the game runs.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class InstallFolderMoveTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-move-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Running(bool running) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => running;
    }

    private (DetectedGame Old, DetectedGame New, string OldDir, string NewDir) Game(string source = "Steam")
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        var oldDir = Directory.CreateDirectory(Path.Combine(root, "Binaries", "Win64")).FullName;
        var newDir = Directory.CreateDirectory(Path.Combine(root, "platform", "steam")).FullName;
        File.WriteAllText(Path.Combine(oldDir, "Game.exe"), "old exe");
        File.WriteAllText(Path.Combine(newDir, "Game.exe"), "new exe");
        var old = new DetectedGame
        {
            Name = "Game", InstallPath = root, ExecutablePath = Path.Combine(oldDir, "Game.exe"), BinaryDirectory = oldDir, Source = source,
            SteamAppId = source == "Steam" ? 4242 : null, Runner = "Proton"
        };
        var moved = old.ShallowClone();
        moved.ExecutablePath = Path.Combine(newDir, "Game.exe");
        moved.BinaryDirectory = newDir;
        return (old, moved, oldDir, newDir);
    }

    private static LinuxDeployedFile Owned(string path, string content, bool existedBefore = false)
    {
        File.WriteAllText(path, content);
        return new LinuxDeployedFile { Path = path, Sha256 = FileHash.Sha256Hex(path), ExistedBeforeInstall = existedBefore };
    }

    // ReShade over the game's own dxgi.dll (kept as .original), its ReShade.ini (changed by the user since), a shader in a
    // folder RHI created; DOF Fix beside it; and a game file RHI edited in place.
    private void Install(ViewModelFixture fx, DetectedGame game, string dir)
    {
        File.WriteAllText(Path.Combine(dir, "dxgi.dll.original"), "game dxgi");
        var dxgi = Owned(Path.Combine(dir, "dxgi.dll"), "reshade", existedBefore: true);
        var ini = Owned(Path.Combine(dir, "ReShade.ini"), "[GENERAL]\nRHI=1\n");
        ini.AllowUserChanges = true;
        File.WriteAllText(ini.Path, "[GENERAL]\nUser=1\n");
        Directory.CreateDirectory(Path.Combine(dir, "reshade-shaders", "Shaders"));
        var shader = Owned(Path.Combine(dir, "reshade-shaders", "Shaders", "a.fx"), "fx");
        var edited = Owned(Path.Combine(dir, "renderer.ini"), "game setting", existedBefore: true);
        edited.AllowUserChanges = true;
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade, BinaryDirectory = dir,
            Files = [dxgi, ini, shader, edited],
            DirectoriesCreatedByRhi = [Path.Combine(dir, "reshade-shaders"), Path.Combine(dir, "reshade-shaders", "Shaders")]
        });
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DofFix, BinaryDirectory = dir,
            Files = [Owned(Path.Combine(dir, "dof.addon64"), "dof")]
        });
    }

    [Fact]
    public async Task Move_TakesRhisFilesAndSettings_PutsTheGamesFileBack_AndKeepsAGameFileInTheNewFolderAsOriginal()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        Install(fx, old, oldDir);
        File.WriteAllText(Path.Combine(newDir, "dxgi.dll"), "new folder's own dxgi");
        fx.Managed.FolderMoveRunningProbe = new Running(false);
        var journal = Path.Combine(_root, "journal");
        fx.Managed.FolderMoveJournal = new DeploymentJournal(journal);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.True(result.Success, result.Message);
        Assert.Equal([ManagedComponentKind.ReShade, ManagedComponentKind.DofFix], result.Components);
        Assert.Equal("reshade", File.ReadAllText(Path.Combine(newDir, "dxgi.dll")));
        Assert.Equal("new folder's own dxgi", File.ReadAllText(Path.Combine(newDir, "dxgi.dll.original")));
        Assert.Equal("[GENERAL]\nUser=1\n", File.ReadAllText(Path.Combine(newDir, "ReShade.ini"))); // the user's settings move as they are
        Assert.Equal("fx", File.ReadAllText(Path.Combine(newDir, "reshade-shaders", "Shaders", "a.fx")));
        Assert.Equal("dof", File.ReadAllText(Path.Combine(newDir, "dof.addon64")));
        Assert.Equal("game dxgi", File.ReadAllText(Path.Combine(oldDir, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(oldDir, "dxgi.dll.original")));
        Assert.False(File.Exists(Path.Combine(oldDir, "ReShade.ini")));
        Assert.False(File.Exists(Path.Combine(oldDir, "dof.addon64")));
        Assert.False(Directory.Exists(Path.Combine(oldDir, "reshade-shaders")));
        Assert.Equal("game setting", File.ReadAllText(Path.Combine(oldDir, "renderer.ini"))); // edited in place: stays

        var reshade = fx.Records.Find(moved, ManagedComponentKind.ReShade)!;
        Assert.Equal(newDir, reshade.BinaryDirectory);
        Assert.Equal([Path.Combine(newDir, "dxgi.dll"), Path.Combine(newDir, "ReShade.ini"), Path.Combine(newDir, "reshade-shaders", "Shaders", "a.fx"),
            Path.Combine(oldDir, "renderer.ini")], reshade.Files.Select(f => f.Path));
        Assert.True(reshade.Files[0].ExistedBeforeInstall); // the new folder's dxgi.dll is the one removal puts back
        Assert.Equal([Path.Combine(newDir, "reshade-shaders"), Path.Combine(newDir, "reshade-shaders", "Shaders")], reshade.DirectoriesCreatedByRhi);
        Assert.Equal(newDir, fx.Records.Find(moved, ManagedComponentKind.DofFix)!.BinaryDirectory);
        Assert.Empty(Directory.EnumerateFiles(journal)); // committed: no entry left
    }

    [Fact]
    public async Task NothingMoves_WhileTheGameRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        Install(fx, old, oldDir);
        fx.Managed.FolderMoveRunningProbe = new Running(true);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.False(result.Success);
        Assert.Equal(LinuxManagedComponentService.FolderMoveRunningMessage, result.Message);
        Assert.True(File.Exists(Path.Combine(oldDir, "dof.addon64")));
        Assert.Equal(oldDir, fx.Records.Find(old, ManagedComponentKind.DofFix)!.BinaryDirectory);
    }

    [Fact]
    public async Task AFileChangedSinceTheInstall_StopsTheMoveBeforeAnythingChanges()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        Install(fx, old, oldDir);
        File.WriteAllText(Path.Combine(oldDir, "dof.addon64"), "changed by the user");
        fx.Managed.FolderMoveRunningProbe = new Running(false);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.Equal(LinuxManagedComponentService.FolderMoveChangedPrefix + Path.Combine(oldDir, "dof.addon64"), result.Message);
        Assert.Equal("reshade", File.ReadAllText(Path.Combine(oldDir, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(newDir, "dxgi.dll")));
    }

    [Fact]
    public async Task ANewFolderFileWithItsOwnOriginal_IsNeverOverwritten()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        Install(fx, old, oldDir);
        File.WriteAllText(Path.Combine(newDir, "dof.addon64"), "user copy");
        File.WriteAllText(Path.Combine(newDir, "dof.addon64.original"), "user backup");
        fx.Managed.FolderMoveRunningProbe = new Running(false);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.Equal(LinuxManagedComponentService.FolderMoveTakenPrefix + Path.Combine(newDir, "dof.addon64"), result.Message);
        Assert.Equal("user copy", File.ReadAllText(Path.Combine(newDir, "dof.addon64")));
        Assert.Equal("user backup", File.ReadAllText(Path.Combine(newDir, "dof.addon64.original")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailurePartWay_PutsEverythingBack(bool journaled)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        Install(fx, old, oldDir);
        File.WriteAllText(Path.Combine(newDir, "dxgi.dll"), "new folder's own dxgi");
        fx.Managed.FolderMoveRunningProbe = new Running(false);
        if (journaled) fx.Managed.FolderMoveJournal = new DeploymentJournal(Path.Combine(_root, "journal"));
        fx.Managed.BeforeFolderMoveFileForTesting = path => { if (path.EndsWith("dof.addon64", StringComparison.Ordinal)) throw new IOException("disk full"); };

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.StartsWith(LinuxManagedComponentService.FolderMoveFailedPrefix + "disk full", result.Message, StringComparison.Ordinal);
        Assert.Equal("reshade", File.ReadAllText(Path.Combine(oldDir, "dxgi.dll")));
        Assert.Equal("game dxgi", File.ReadAllText(Path.Combine(oldDir, "dxgi.dll.original")));
        Assert.Equal("[GENERAL]\nUser=1\n", File.ReadAllText(Path.Combine(oldDir, "ReShade.ini")));
        Assert.Equal("fx", File.ReadAllText(Path.Combine(oldDir, "reshade-shaders", "Shaders", "a.fx")));
        Assert.Equal("new folder's own dxgi", File.ReadAllText(Path.Combine(newDir, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(newDir, "dxgi.dll.original")));
        Assert.False(File.Exists(Path.Combine(newDir, "ReShade.ini")));
        Assert.False(Directory.Exists(Path.Combine(newDir, "reshade-shaders")));
        Assert.Equal(oldDir, fx.Records.Find(old, ManagedComponentKind.ReShade)!.BinaryDirectory);
    }

    [Fact]
    public async Task AFolderKeyedGame_TakesItsRecordsToItsNewKey()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, _, oldDir, _) = Game(source: "Manual");
        var otherRoot = Directory.CreateDirectory(Path.Combine(_root, "Elsewhere")).FullName;
        File.WriteAllText(Path.Combine(otherRoot, "Game.exe"), "copy");
        var moved = old.ShallowClone();
        moved.InstallPath = otherRoot;
        moved.ExecutablePath = Path.Combine(otherRoot, "Game.exe");
        moved.BinaryDirectory = otherRoot;
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(old), GameName = old.Name, Component = ManagedComponentKind.DofFix, BinaryDirectory = oldDir,
            Files = [Owned(Path.Combine(oldDir, "dof.addon64"), "dof")]
        });
        fx.Managed.FolderMoveRunningProbe = new Running(false);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, otherRoot);

        Assert.True(result.Success, result.Message);
        Assert.Null(fx.Records.Find(old, ManagedComponentKind.DofFix));
        Assert.Equal(LinuxDeploymentRecordStore.GameKey(moved), fx.Records.Find(moved, ManagedComponentKind.DofFix)!.GameKey);
    }

    [Fact]
    public async Task AWineOverrideForAnotherExecutable_IsRefused()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (old, moved, oldDir, newDir) = Game();
        moved.ExecutablePath = Path.Combine(newDir, "Launcher.exe");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(old), GameName = old.Name, Component = ManagedComponentKind.ReShade, BinaryDirectory = oldDir,
            Files = [Owned(Path.Combine(oldDir, "dxgi.dll"), "reshade")],
            WineOverride = new WineOverrideRecord { DllName = "dxgi", ApplicationName = "Game.exe" }
        });
        fx.Managed.FolderMoveRunningProbe = new Running(false);

        var result = await fx.Managed.MoveInstallFolderAsync(old, moved, oldDir, newDir);

        Assert.StartsWith(LinuxManagedComponentService.FolderMoveWineOverridePrefix, result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(oldDir, "dxgi.dll")));
    }
}
