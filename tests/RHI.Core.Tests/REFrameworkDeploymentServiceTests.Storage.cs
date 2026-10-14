using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: RE Framework ⚙ "Delete _storage_ folder" (#159, upstream 2.8.6 PAR-D72).
public sealed partial class REFrameworkDeploymentServiceTests
{
    private (REFrameworkDeploymentService Service, DetectedGame Game) StorageFixture(Func<string, bool>? prefixInUse = null)
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var service = new REFrameworkDeploymentService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no network"))),
            new LinuxDeploymentRecordStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json")),
            new WineDllOverrideService(new WineRegistryEditor(prefixInUse ?? (_ => false))), Path.Combine(_root, "cache-storage"));
        return (service, game);
    }

    [Fact]
    public async Task DeleteStorage_RemovesTheCacheFolder_LinksInsideAsLinksOnly()
    {
        var (service, game) = StorageFixture();
        var storage = Path.Combine(game.InstallPath, "_storage_");
        Directory.CreateDirectory(Path.Combine(storage, "nested", "deeper"));
        File.WriteAllText(Path.Combine(storage, "a.bin"), "cache");
        File.WriteAllText(Path.Combine(storage, "nested", "deeper", "b.bin"), "cache");
        var outside = Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "user data");
        Directory.CreateSymbolicLink(Path.Combine(storage, "nested", "linked-folder"), outside);
        File.CreateSymbolicLink(Path.Combine(storage, "linked-file"), Path.Combine(outside, "keep.txt"));
        Assert.Equal(storage, REFrameworkDeploymentService.FindStorageFolder(game));

        var result = await service.DeleteStorageFolderAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(REFrameworkDeploymentService.StorageDeletedPrefix + storage, result.Message);
        Assert.False(Directory.Exists(storage));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(outside, "keep.txt"))); // never followed
        Assert.Null(REFrameworkDeploymentService.FindStorageFolder(game));
        Assert.True(File.Exists(game.ExecutablePath)); // nothing else in the game folder touched
    }

    [Fact]
    public async Task DeleteStorage_NotFound_SaysSo()
    {
        var (service, game) = StorageFixture();

        var result = await service.DeleteStorageFolderAsync(game);

        Assert.False(result.Success);
        Assert.StartsWith(REFrameworkDeploymentService.StorageNotFoundPrefix, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteStorage_ALinkedStorageFolder_IsRefusedAndItsTargetKept()
    {
        var (service, game) = StorageFixture();
        var elsewhere = Path.Combine(_root, "elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "data.bin"), "keep");
        var storage = Path.Combine(game.InstallPath, "_storage_");
        Directory.CreateSymbolicLink(storage, elsewhere);

        var result = await service.DeleteStorageFolderAsync(game);

        Assert.False(result.Success);
        Assert.Equal(REFrameworkDeploymentService.StorageLinkRefusedPrefix + storage, result.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(elsewhere, "data.bin")));
        Assert.NotNull(new DirectoryInfo(storage).LinkTarget);
    }

    [Fact]
    public async Task DeleteStorage_BesideAnExecutableReachedThroughALinkOutsideTheGame_IsRefused()
    {
        var (service, game) = StorageFixture();
        var realBin = Path.Combine(_root, "real-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(realBin, "_storage_"));
        File.WriteAllText(Path.Combine(realBin, "_storage_", "x.bin"), "keep");
        var linkedBin = Path.Combine(game.InstallPath, "bin");
        Directory.CreateSymbolicLink(linkedBin, realBin);
        game.BinaryDirectory = linkedBin;

        var result = await service.DeleteStorageFolderAsync(game);

        Assert.False(result.Success);
        Assert.StartsWith(REFrameworkDeploymentService.StorageOutsideGameRefusedPrefix, result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(realBin, "_storage_", "x.bin")));
    }

    [Fact]
    public async Task DeleteStorage_WhileTheGamesPrefixRuns_IsRefused()
    {
        var (service, game) = StorageFixture(prefixInUse: _ => true);
        var storage = Path.Combine(game.InstallPath, "_storage_");
        Directory.CreateDirectory(storage);
        File.WriteAllText(Path.Combine(storage, "a.bin"), "cache");

        var result = await service.DeleteStorageFolderAsync(game);

        Assert.False(result.Success);
        Assert.Equal(REFrameworkDeploymentService.StorageWineRunningPrefix + game.PrefixPath, result.Message);
        Assert.True(File.Exists(Path.Combine(storage, "a.bin")));
    }

    [Fact]
    public async Task DeleteStorage_FindsTheFolderBesideTheExecutableFirst()
    {
        var (service, game) = StorageFixture();
        var bin = Path.Combine(game.InstallPath, "bin");
        Directory.CreateDirectory(Path.Combine(bin, "_storage_"));
        Directory.CreateDirectory(Path.Combine(game.InstallPath, "_storage_"));
        game.BinaryDirectory = bin;

        Assert.Equal(Path.Combine(bin, "_storage_"), REFrameworkDeploymentService.FindStorageFolder(game));
        Assert.True((await service.DeleteStorageFolderAsync(game)).Success);
        Assert.False(Directory.Exists(Path.Combine(bin, "_storage_")));
        Assert.True(Directory.Exists(Path.Combine(game.InstallPath, "_storage_")));
    }

    [Fact]
    public async Task DeleteStorage_ThroughTheComponentService_NeedsAConfirmedPatchTarget()
    {
        var (reFramework, game) = StorageFixture();
        var storage = Path.Combine(game.InstallPath, "_storage_");
        Directory.CreateDirectory(storage);
        game.RequiresExplicitPatchTarget = true; // e.g. a launcher card: no confirmed target
        game.ExecutablePath = null;
        var managed = new LinuxManagedComponentService(new LinuxDeploymentRecordStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json")),
            reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: reFramework, lumaFramework: null!, lumaPolicy: null!);

        var refused = await managed.DeleteReFrameworkStorageFolderAsync(game);

        Assert.False(refused.Success);
        Assert.True(Directory.Exists(storage));
    }
}
