using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>DEP-19 (Refs #38): records survive moving a game or its whole library.</summary>
public sealed class GameRecordRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-relocation-" + Guid.NewGuid().ToString("N"));

    public GameRecordRelocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LinuxDeploymentRecordStore Store() => new(Path.Combine(_root, "deployments.json"));

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static DetectedGame SteamGame(string library) => SteamGameAt(Path.Combine(library, "steamapps", "common", "Game"), library);

    private static DetectedGame SteamGameAt(string install, string library) => new()
    {
        Name = "Game",
        Source = "Steam",
        SteamAppId = 1234,
        InstallPath = install,
        BinaryDirectory = Path.Combine(install, "bin"),
        ExecutablePath = Path.Combine(install, "bin", "Game.exe"),
        PrefixPath = Path.Combine(library, "steamapps", "compatdata", "1234", "pfx"),
        Runner = "Proton"
    };

    /// <summary>Installs a fake component: a DLL in bin and a config in the prefix, both owned and hashed.</summary>
    private static LinuxDeploymentRecord Install(LinuxDeploymentRecordStore store, DetectedGame game, string content = "reshade")
    {
        Directory.CreateDirectory(game.BinaryDirectory!);
        var dll = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        File.WriteAllText(dll, content);
        var config = Path.Combine(game.PrefixPath!, "drive_c", "rhi.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "cfg");
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Files =
            [
                new LinuxDeployedFile { Path = dll, Sha256 = Sha(dll) },
                new LinuxDeployedFile { Path = config, Sha256 = Sha(config) }
            ],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["PayloadPath"] = dll, ["Channel"] = "Stable" }
        };
        store.Upsert(record);
        return record;
    }

    private static void MoveLibrary(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
    }

    private static LinuxManagedComponentService Service(LinuxDeploymentRecordStore records)
        => new(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    // ---- Identity ------------------------------------------------------------------------------

    [Fact]
    public void Key_UsesStoreIdsAndKeepsTheOldFolderKeyAsLegacy()
    {
        var steam = new DetectedGame { InstallPath = "/a/G", SteamAppId = 7 };
        var heroic = new DetectedGame { InstallPath = "/a/H", HeroicAppName = "Fortnite" };
        var lutris = new DetectedGame { InstallPath = "/a/L/" };

        Assert.Equal("steam:7", GameRecordIdentity.Key(steam));
        Assert.Empty(GameRecordIdentity.LegacyKeys(steam));
        Assert.Equal("heroic:Fortnite", GameRecordIdentity.Key(heroic));
        Assert.Equal(["/a/H"], GameRecordIdentity.LegacyKeys(heroic));
        Assert.Equal(Path.GetFullPath("/a/L/"), GameRecordIdentity.Key(lutris));
    }

    [Fact]
    public void HeroicRecord_WrittenByAnOlderBuild_IsMigratedAndStillFoundAfterTheGameMoves()
    {
        var store = Store();
        var oldInstall = Path.Combine(_root, "old", "Heroic", "Game");
        var game = new DetectedGame { Name = "Game", HeroicAppName = "abc123", InstallPath = oldInstall, BinaryDirectory = oldInstall, PrefixPath = Path.Combine(_root, "pfx") };
        var record = Install(store, game);
        // Older builds keyed Heroic games by folder.
        record.GameKey = Path.GetFullPath(oldInstall);
        store.Remove(game, ManagedComponentKind.ReShade);
        store.Upsert(record);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade)); // legacy key still read

        Assert.Equal(1, store.MigrateGameKeys([game]));
        Assert.Equal("heroic:abc123", Assert.Single(Store().FindAll(game)).GameKey);

        var newInstall = Path.Combine(_root, "new", "Heroic", "Game");
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var moved = new DetectedGame { Name = "Game", HeroicAppName = "abc123", InstallPath = newInstall, BinaryDirectory = newInstall, PrefixPath = game.PrefixPath };
        Assert.Equal(ManagedComponentKind.ReShade, Assert.Single(Store().FindAll(moved)).Component);
    }

    [Fact]
    public void PreferenceStores_ReadLegacyKeyAndMigrateOnWrite()
    {
        var path = Path.Combine(_root, "api.json");
        var game = new DetectedGame { Name = "Game", HeroicAppName = "abc", InstallPath = Path.Combine(_root, "H", "Game") };
        File.WriteAllText(path, $$"""{ "{{Path.GetFullPath(game.InstallPath).Replace("\\", "\\\\")}}": "Vulkan" }""");
        var store = new GraphicsApiOverrideStore(path);

        Assert.Equal(GraphicsApi.Vulkan, store.Get(game));
        store.Set(game, GraphicsApi.DirectX12);

        var text = File.ReadAllText(path);
        Assert.Contains("heroic:abc", text);
        Assert.DoesNotContain(Path.GetFullPath(game.InstallPath), text);
        Assert.Equal(GraphicsApi.DirectX12, new GraphicsApiOverrideStore(path).Get(game));
    }

    // ---- Moved Steam library -------------------------------------------------------------------

    [Fact]
    public async Task MovedLibrary_OldFolderLeftBehind_UpdateAndRemoveRefuseWithoutWritingToIt()
    {
        var store = Store();
        var oldLibrary = Path.Combine(_root, "a", "lib");
        Install(store, SteamGame(oldLibrary));
        // Steam copied the game to /b and left the old folder (and RHI's file) behind.
        var moved = SteamGame(Path.Combine(_root, "b", "lib"));
        Directory.CreateDirectory(moved.BinaryDirectory!);
        var oldDll = Path.Combine(SteamGame(oldLibrary).BinaryDirectory!, "dxgi.dll");
        var before = File.GetLastWriteTimeUtc(oldDll);
        var service = Service(store);

        var remove = await service.RemoveAsync(moved, ManagedComponentKind.ReShade);
        var check = await service.CheckManagedUpdateAsync(moved, ManagedComponentKind.ReShade, []);

        Assert.False(remove.Success);
        Assert.StartsWith(DeploymentRelocation.GuardPrefix, remove.Message);
        Assert.Contains(SteamGame(oldLibrary).BinaryDirectory!, remove.Message);
        Assert.True(check.Blocked);
        Assert.True(File.Exists(oldDll));
        Assert.Equal(before, File.GetLastWriteTimeUtc(oldDll));
        Assert.True(service.NeedsRelocationRepair(moved));

        var repair = service.RepairAfterMove(moved);
        Assert.False(repair.Success);
        Assert.StartsWith(DeploymentRelocation.RepairOldFolderExistsPrefix, repair.Message);
    }

    [Fact]
    public void MovedLibrary_RepairVerifiesFilesThenRebasesEveryPath()
    {
        var store = Store();
        var oldLibrary = Path.Combine(_root, "a", "lib");
        var original = Install(store, SteamGame(oldLibrary));
        var newLibrary = Path.Combine(_root, "b", "games", "lib");
        MoveLibrary(oldLibrary, newLibrary);
        var moved = SteamGame(newLibrary);
        var service = Service(store);
        Assert.NotNull(new DeploymentRelocation(store).Guard(moved));

        var result = service.RepairAfterMove(moved);

        Assert.True(result.Success, result.Message);
        var record = Assert.Single(Store().FindAll(moved));
        Assert.Equal(moved.BinaryDirectory, record.BinaryDirectory);
        Assert.Equal(Path.Combine(moved.BinaryDirectory!, "dxgi.dll"), record.Files[0].Path);
        Assert.Equal(Path.Combine(moved.PrefixPath!, "drive_c", "rhi.ini"), record.Files[1].Path); // compatdata moved with the library
        Assert.Equal(record.Files[0].Path, record.Metadata["PayloadPath"]);
        Assert.Equal("Stable", record.Metadata["Channel"]);
        Assert.Equal(original.Files[0].Sha256, record.Files[0].Sha256);
        Assert.Null(new DeploymentRelocation(store).Guard(moved));
        Assert.False(service.NeedsRelocationRepair(moved));
    }

    [Fact]
    public void MovedLibrary_ChangedFile_RepairChangesNothing()
    {
        var store = Store();
        var oldLibrary = Path.Combine(_root, "a", "lib");
        Install(store, SteamGame(oldLibrary));
        var newLibrary = Path.Combine(_root, "b", "lib");
        MoveLibrary(oldLibrary, newLibrary);
        var moved = SteamGame(newLibrary);
        File.WriteAllText(Path.Combine(moved.BinaryDirectory!, "dxgi.dll"), "someone else's dll");
        var ledger = File.ReadAllBytes(Path.Combine(_root, "deployments.json"));

        var result = Service(store).RepairAfterMove(moved);

        Assert.False(result.Success);
        Assert.StartsWith(DeploymentRelocation.RepairChangedPrefix, result.Message);
        Assert.Equal(ledger, File.ReadAllBytes(Path.Combine(_root, "deployments.json")));
    }

    [Fact]
    public void LibraryReachedThroughSymlink_IsNotMoved()
    {
        var store = Store();
        var realLibrary = Path.Combine(_root, "real", "lib");
        Install(store, SteamGame(realLibrary));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "real"));

        Assert.Empty(new DeploymentRelocation(store).FindMoved(SteamGame(Path.Combine(link, "lib"))));
    }

    // ---- Folder-keyed game (no store id) -------------------------------------------------------

    [Fact]
    public void MovedFolderKeyedGame_RepairAdoptsItsRecords()
    {
        var store = Store();
        static DetectedGame Manual(string install) => new()
        {
            Name = "Indie",
            Source = "Manual",
            InstallPath = install,
            BinaryDirectory = install,
            ExecutablePath = Path.Combine(install, "Indie.exe"),
            PrefixPath = "/nonexistent-prefix",
            Runner = "Wine"
        };
        var oldGame = Manual(Path.Combine(_root, "old", "Games", "Indie"));
        Directory.CreateDirectory(oldGame.BinaryDirectory!);
        var dll = Path.Combine(oldGame.BinaryDirectory!, "dxgi.dll");
        File.WriteAllText(dll, "reshade");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(oldGame), GameName = "Indie", Component = ManagedComponentKind.ReShade,
            BinaryDirectory = oldGame.BinaryDirectory!, Files = [new LinuxDeployedFile { Path = dll, Sha256 = Sha(dll) }]
        });
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var moved = Manual(Path.Combine(_root, "new", "Games", "Indie"));
        Assert.Empty(store.FindAll(moved)); // folder key no longer matches
        var service = Service(store);
        Assert.True(service.NeedsRelocationRepair(moved));

        var result = service.RepairAfterMove(moved);

        Assert.True(result.Success, result.Message);
        var record = Assert.Single(Store().FindAll(moved));
        Assert.Equal(Path.Combine(moved.BinaryDirectory!, "dxgi.dll"), Assert.Single(record.Files).Path);
        Assert.Single(store.All()); // re-keyed, not duplicated
    }

    [Fact]
    public void SameNamedGameElsewhere_IsNotAdoptedWhileItsFolderExists()
    {
        var store = Store();
        var other = Path.Combine(_root, "other", "Indie");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "dxgi.dll"), "reshade");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = Path.GetFullPath(other), GameName = "Indie", Component = ManagedComponentKind.ReShade, BinaryDirectory = other,
            Files = [new LinuxDeployedFile { Path = Path.Combine(other, "dxgi.dll"), Sha256 = Sha(Path.Combine(other, "dxgi.dll")) }]
        });
        var here = Path.Combine(_root, "here", "Indie");
        Directory.CreateDirectory(here);
        File.WriteAllText(Path.Combine(here, "dxgi.dll"), "reshade");

        Assert.Empty(new DeploymentRelocation(store).FindAdoptable(new DetectedGame { Name = "Indie", InstallPath = here, BinaryDirectory = here }));
    }
}
