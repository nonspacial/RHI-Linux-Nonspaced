using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using System.Net;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
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

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static DetectedGame Indie(string install, string exe = "Indie.exe") => new()
    {
        Name = "Indie",
        Source = "Manual",
        InstallPath = install,
        LibraryIdentityPath = install,
        BinaryDirectory = install,
        ExecutablePath = Path.Combine(install, exe),
        PrefixPath = "/nonexistent-prefix",
        Runner = "Wine"
    };

    /// <summary>A folder-keyed ReShade record for <paramref name="game"/>, optionally naming the exe its Wine override targets.</summary>
    private static void InstallIndie(LinuxDeploymentRecordStore store, DetectedGame game, string? overrideExe = null, string content = "reshade")
    {
        Directory.CreateDirectory(game.BinaryDirectory!);
        var dll = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        File.WriteAllText(dll, content);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Files = [new LinuxDeployedFile { Path = dll, Sha256 = Sha(dll) }],
            WineOverride = overrideExe is null ? null : new WineOverrideRecord { DllName = "dxgi", ApplicationName = overrideExe, ChangedByRhi = true }
        });
    }

    private static void MoveLibrary(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
    }

    private static LinuxManagedComponentService Service(LinuxDeploymentRecordStore records, ComponentUpdateLogStore? updateLog = null)
        => new(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            updateLog: updateLog);

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
        // Steam copied the game to /b and left the old folder (and RHI's files) behind.
        var newLibrary = Path.Combine(_root, "b", "lib");
        CopyTree(oldLibrary, newLibrary);
        var moved = SteamGame(newLibrary);
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

        // A live copy: the refusal says exactly how to proceed...
        var repair = service.RepairAfterMove(moved);
        Assert.False(repair.Success);
        Assert.StartsWith(DeploymentRelocation.RepairOldFolderExistsPrefix, repair.Message);
        Assert.EndsWith(DeploymentRelocation.RepairOldFolderExistsSuffix, repair.Message);

        // ...and following it (keep the copy as a backup under another name) repairs.
        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "a-backup"));
        var retry = service.RepairAfterMove(moved);
        Assert.True(retry.Success, retry.Message);
        Assert.Null(new DeploymentRelocation(store).Guard(moved));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed")]
    public void MovedLibrary_OldFolderLeftoverWithoutRhiFiles_Repairs(string leftover)
    {
        // The old folder still exists but no longer holds RHI's files as installed (emptied by
        // the move, or the game was reinstalled there): not a live copy, so repair proceeds.
        var store = Store();
        var oldLibrary = Path.Combine(_root, "a", "lib");
        var oldGame = SteamGame(oldLibrary);
        Install(store, oldGame);
        var newLibrary = Path.Combine(_root, "b", "lib");
        CopyTree(oldLibrary, newLibrary);
        var oldDll = Path.Combine(oldGame.BinaryDirectory!, "dxgi.dll");
        var oldConfig = Path.Combine(oldGame.PrefixPath!, "drive_c", "rhi.ini");
        if (leftover == "deleted") { File.Delete(oldDll); File.Delete(oldConfig); }
        else { File.WriteAllText(oldDll, "the game's own dxgi.dll"); File.WriteAllText(oldConfig, "other"); }
        var moved = SteamGame(newLibrary);

        var result = Service(store).RepairAfterMove(moved);

        Assert.True(result.Success, result.Message);
        Assert.Equal(moved.BinaryDirectory, Assert.Single(Store().FindAll(moved)).BinaryDirectory);
        Assert.True(Directory.Exists(oldGame.BinaryDirectory)); // nothing written to or removed from the old folder
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

    // ---- Stronger identity for folder-keyed adoption (review finding 7) ------------------------

    [Fact]
    public void SameNameButOnlyTheBinFolderInCommon_IsNotAdopted()
    {
        // Two different games called "Indie": the old one in .../X/bin, this one in .../Y/bin.
        // RHI's payload is byte-identical, so matching files prove nothing about the game.
        var store = Store();
        var old = Indie(Path.Combine(_root, "old", "X"));
        old.BinaryDirectory = Path.Combine(old.InstallPath, "bin");
        InstallIndie(store, old);
        Directory.Delete(Path.Combine(_root, "old"), true);
        var here = Indie(Path.Combine(_root, "new", "Y"));
        here.BinaryDirectory = Path.Combine(here.InstallPath, "bin");
        Directory.CreateDirectory(here.BinaryDirectory);
        File.WriteAllText(Path.Combine(here.BinaryDirectory, "dxgi.dll"), "reshade");

        Assert.Empty(new DeploymentRelocation(store).FindAdoptable(here));
        Assert.False(Service(store).NeedsRelocationRepair(here));
    }

    [Fact]
    public void RecordForAnotherExecutable_IsNotAdopted()
    {
        var store = Store();
        InstallIndie(store, Indie(Path.Combine(_root, "old", "Games", "Indie")), overrideExe: "OtherGame.exe");
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));

        Assert.Empty(new DeploymentRelocation(store).FindAdoptable(Indie(Path.Combine(_root, "new", "Games", "Indie"))));
    }

    [Fact]
    public void RecordForThisExecutable_IsAdopted()
    {
        var store = Store();
        InstallIndie(store, Indie(Path.Combine(_root, "old", "Games", "Indie")), overrideExe: "indie.EXE");
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));

        Assert.Single(new DeploymentRelocation(store).FindAdoptable(Indie(Path.Combine(_root, "new", "Games", "Indie"))));
    }

    [Fact]
    public void RecordsFromTwoEarlierFolders_RepairRefusesToGuess()
    {
        var store = Store();
        InstallIndie(store, Indie(Path.Combine(_root, "one", "Games", "Indie")));
        var second = Indie(Path.Combine(_root, "two", "Games", "Indie"));
        Directory.CreateDirectory(second.BinaryDirectory!);
        File.WriteAllText(Path.Combine(second.BinaryDirectory!, "dxgi.dll"), "reshade");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(second), GameName = "Indie", Component = ManagedComponentKind.ReLimiter,
            BinaryDirectory = second.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = Path.Combine(second.BinaryDirectory!, "dxgi.dll"), Sha256 = Sha(Path.Combine(second.BinaryDirectory!, "dxgi.dll")) }]
        });
        var here = Indie(Path.Combine(_root, "new", "Games", "Indie"));
        Directory.CreateDirectory(here.BinaryDirectory!);
        File.WriteAllText(Path.Combine(here.BinaryDirectory!, "dxgi.dll"), "reshade");
        Directory.Delete(Path.Combine(_root, "one"), true);
        Directory.Delete(Path.Combine(_root, "two"), true);
        var ledger = File.ReadAllBytes(Path.Combine(_root, "deployments.json"));

        var result = Service(store).RepairAfterMove(here);

        Assert.False(result.Success);
        Assert.StartsWith(DeploymentRelocation.RepairAmbiguousPrefix, result.Message);
        Assert.Equal(ledger, File.ReadAllBytes(Path.Combine(_root, "deployments.json")));
    }

    // ---- Per-game state follows the repair, all or nothing (review finding 6) -----------------

    private sealed class Stores(string root)
    {
        public LinuxGameLibraryStore Library { get; } = new(Path.Combine(root, "cfg", "game-library.json"));
        public GraphicsApiOverrideStore Api { get; } = new(Path.Combine(root, "cfg", "game-api-overrides.json"));
        public OptiScalerVariantPreferenceStore OptiScaler { get; } = new(Path.Combine(root, "cfg", "optiscaler-variants.json"));
        public NeuralAddonVersionPreferenceStore Pins { get; } = new(Path.Combine(root, "cfg", "neural-addon-version-pins.json"));
        public Dlssg2030PreferenceStore Dlssg { get; } = new(Path.Combine(root, "cfg", "dlssg2030-preferences.json"));
        public Rtx40MfgPreferenceStore Mfg { get; } = new(Path.Combine(root, "cfg", "rtx40.json"));
        public StandaloneDlssEnablerPreferenceStore Enabler { get; } = new(Path.Combine(root, "cfg", "enabler.json"));
        public LumaModePreferenceStore Luma { get; } = new(Path.Combine(root, "luma", "luma-mode.json"));
        public ShortFuseSettingsStore ShortFuse { get; } = new(Path.Combine(root, "cfg", "shortfuse-settings.json"));
        public ComponentUpdateLogStore Log { get; } = new(Path.Combine(root, "data", "update-log.json"));
        public string AddonStatePath { get; } = Path.Combine(root, "cfg", "addon-manager.json");

        public ReShadeAddonManagerService Addons(string root2)
        {
            var http = new HttpClient(new NotFound());
            var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(root2, "manifest-cache.json"));
            return new ReShadeAddonManagerService(http, manifest, Path.Combine(root2, "addon-cache"), Path.Combine(root2, "custom-addons"), AddonStatePath);
        }

        public IReadOnlyList<IGameStateStore> All(string root2)
            => [Library, Api, OptiScaler, Pins, Dlssg, Mfg, Enabler, ShortFuse, Addons(root2), Luma];

        public IEnumerable<string> Files()
            => new[] { Library.LibraryPath, AddonStatePath, Path.Combine(Path.GetDirectoryName(AddonStatePath)!, "..", "luma", "luma-mode.json") }
                .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(AddonStatePath)!))
                .Select(Path.GetFullPath).Distinct();
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Gives a game every kind of per-game state RHI keeps.</summary>
    private static void SeedState(Stores stores, DetectedGame game, string addonFile)
    {
        stores.Api.Set(game, GraphicsApi.Vulkan);
        stores.OptiScaler.Set(game, OptiScalerVariant.Nightly);
        stores.Pins.Set(game, ManagedComponentKind.ShortFuse, "v1.0");
        stores.Pins.SetNeuralRenderingDllVersion(game, "310.2");
        stores.Dlssg.Set(game, new Dlssg2030Preferences { ProxyName = "winmm.dll" });
        stores.Mfg.Set(game, new Rtx40MfgPreferences { ProxyName = "winmm.dll" });
        stores.Enabler.Set(game, new StandaloneDlssEnablerPreferences { ProxyName = "winmm.dll" });
        stores.Luma.Set(game, LumaModePreference.Enabled);
        stores.ShortFuse.SetAutoConfigEnabled(game, true);
        stores.ShortFuse.SetZzzLoadOrderEnabled(game, true);
        var key = LinuxGameLibraryStore.GameKey(game);
        var state = stores.Library.Load();
        state.FavouriteGames.Add(key);
        state.HiddenGames.Add(key);
        state.GameEdits[key] = new LinuxGameEditOverride
        {
            LaunchProvider = "Lutris", GameRootPath = game.InstallPath, BinaryDirectory = game.BinaryDirectory, PatchExecutablePath = game.ExecutablePath
        };
        state.HdrProfiles[key] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, EnableLegacyWsi = true };
        stores.Library.Save(state);
        stores.Log.Record(new ComponentUpdateLogEntry
        {
            TimestampUtc = DateTime.UtcNow, GameName = game.Name, GameKey = LinuxDeploymentRecordStore.GameKey(game),
            Component = ManagedComponentKind.ReShade, ComponentName = "ReShade", Action = ComponentUpdateAction.Install, ToVersion = "6.4"
        });
        File.WriteAllText(addonFile, "addon");
        Directory.CreateDirectory(Path.GetDirectoryName(stores.AddonStatePath)!);
        File.WriteAllText(stores.AddonStatePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Schema = 1,
            Games = new Dictionary<string, object>
            {
                [LinuxDeploymentRecordStore.GameKey(game)] = new
                {
                    SelectedIds = new[] { "generic" },
                    Files = new[] { new { AddonId = "generic", Path = addonFile, Sha256 = Sha(addonFile) } }
                }
            }
        }));
    }

    private static void AssertStateFollowed(Stores stores, DetectedGame moved, string movedAddon)
    {
        Assert.Equal(GraphicsApi.Vulkan, stores.Api.Get(moved));
        Assert.Equal(OptiScalerVariant.Nightly, stores.OptiScaler.Get(moved));
        Assert.Equal("v1.0", stores.Pins.Get(moved, ManagedComponentKind.ShortFuse));
        Assert.Equal("310.2", stores.Pins.GetNeuralRenderingDllVersion(moved));
        Assert.Equal("winmm.dll", stores.Dlssg.Get(moved).ProxyName);
        Assert.Equal("winmm.dll", stores.Mfg.Get(moved).ProxyName);
        Assert.Equal("winmm.dll", stores.Enabler.Get(moved).ProxyName);
        Assert.Equal(LumaModePreference.Enabled, stores.Luma.Get(moved));
        Assert.True(stores.ShortFuse.IsAutoConfigEnabled(moved));
        Assert.True(stores.ShortFuse.IsZzzLoadOrderEnabled(moved));
        var key = LinuxGameLibraryStore.GameKey(moved);
        var state = stores.Library.Load();
        Assert.Equal([key], state.FavouriteGames);
        Assert.Equal([key], state.HiddenGames);
        var edit = Assert.Single(state.GameEdits);
        Assert.Equal(key, edit.Key);
        Assert.Equal("Lutris", edit.Value.LaunchProvider);
        Assert.Equal(Path.GetFullPath(moved.InstallPath), edit.Value.GameRootPath);
        Assert.Equal(Path.GetFullPath(moved.BinaryDirectory!), edit.Value.BinaryDirectory);
        Assert.Equal(Path.GetFullPath(moved.ExecutablePath!), edit.Value.PatchExecutablePath);
        var hdr = Assert.Single(state.HdrProfiles);
        Assert.Equal(key, hdr.Key);
        Assert.Equal(LinuxHdrModes.Gamescope, hdr.Value.Mode);
        Assert.Equal(LinuxDeploymentRecordStore.GameKey(moved), Assert.Single(stores.Log.GetAll()).GameKey);
        var addonState = File.ReadAllText(stores.AddonStatePath);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(movedAddon), addonState);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(LinuxDeploymentRecordStore.GameKey(moved)), addonState);
    }

    [Fact]
    public void MovedFolderKeyedGame_RepairCarriesEveryPerGameStoreOver()
    {
        var store = Store();
        var stores = new Stores(_root);
        var oldGame = Indie(Path.Combine(_root, "old", "Games", "Indie"));
        InstallIndie(store, oldGame);
        SeedState(stores, oldGame, Path.Combine(oldGame.BinaryDirectory!, "generic.addon64"));
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var moved = Indie(Path.Combine(_root, "new", "Games", "Indie"));

        var result = Service(store, stores.Log).RepairAfterMove(moved, stores.All(_root));

        Assert.True(result.Success, result.Message);
        Assert.Single(Store().FindAll(moved));
        AssertStateFollowed(stores, moved, Path.Combine(moved.BinaryDirectory!, "generic.addon64"));
        var oldKey = Path.GetFullPath(oldGame.InstallPath);
        foreach (var file in stores.Files().Where(File.Exists))
            Assert.DoesNotContain(System.Text.Json.JsonSerializer.Serialize(oldKey).Trim('"'), File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MovedSteamLibrary_RepairCarriesLibraryEntriesAndAddonFilesOver()
    {
        // Store-keyed preferences survive the move unchanged; the library key holds the folder.
        var store = Store();
        var stores = new Stores(_root);
        var oldLibrary = Path.Combine(_root, "a", "lib");
        var oldGame = SteamGame(oldLibrary);
        Install(store, oldGame);
        SeedState(stores, oldGame, Path.Combine(oldGame.BinaryDirectory!, "generic.addon64"));
        var newLibrary = Path.Combine(_root, "b", "lib");
        MoveLibrary(oldLibrary, newLibrary);
        var moved = SteamGame(newLibrary);

        var result = Service(store, stores.Log).RepairAfterMove(moved, stores.All(_root));

        Assert.True(result.Success, result.Message);
        AssertStateFollowed(stores, moved, Path.Combine(moved.BinaryDirectory!, "generic.addon64"));
    }

    [Fact]
    public void Repair_OneUnreadableStore_ChangesNothingAnywhere()
    {
        var store = Store();
        var stores = new Stores(_root);
        var oldGame = Indie(Path.Combine(_root, "old", "Games", "Indie"));
        InstallIndie(store, oldGame);
        SeedState(stores, oldGame, Path.Combine(oldGame.BinaryDirectory!, "generic.addon64"));
        File.WriteAllText(Path.Combine(_root, "cfg", "optiscaler-variants.json"), "{ broken");
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var moved = Indie(Path.Combine(_root, "new", "Games", "Indie"));
        var snapshot = Snapshot(stores);

        var result = Service(store, stores.Log).RepairAfterMove(moved, stores.All(_root));

        Assert.False(result.Success);
        Assert.StartsWith(DeploymentRelocation.RepairNotSavedPrefix, result.Message);
        Assert.Contains("optiscaler-variants.json", result.Message);
        AssertUnchanged(snapshot);
        Assert.Empty(Store().FindAll(moved));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Repair_LastStoreCannotBeWritten_EveryEarlierFileIsPutBack()
    {
        var store = Store();
        var stores = new Stores(_root);
        var oldGame = Indie(Path.Combine(_root, "old", "Games", "Indie"));
        InstallIndie(store, oldGame);
        SeedState(stores, oldGame, Path.Combine(oldGame.BinaryDirectory!, "generic.addon64"));
        MoveLibrary(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var moved = Indie(Path.Combine(_root, "new", "Games", "Indie"));
        var snapshot = Snapshot(stores);
        var lumaDir = Path.Combine(_root, "luma");
        File.SetUnixFileMode(lumaDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // readable, not writable
        try
        {
            var result = Service(store, stores.Log).RepairAfterMove(moved, stores.All(_root));

            Assert.False(result.Success);
            Assert.StartsWith(DeploymentRelocation.RepairNotSavedPrefix, result.Message);
            // One "Nothing was changed" (the commit's own lead-in is dropped), and a full PT-BR presentation (final
            // ui-stores review, finding 3).
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Message, "Nothing was changed"));
            var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", result.Message);
            Assert.NotEqual(RHI.Desktop.Localization.UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
            Assert.DoesNotContain("Nothing was changed", pt);
        }
        finally
        {
            File.SetUnixFileMode(lumaDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        AssertUnchanged(snapshot);
        Assert.Empty(Store().FindAll(moved));
    }

    private Dictionary<string, byte[]> Snapshot(Stores stores)
        => stores.Files().Append(Path.Combine(_root, "deployments.json")).Append(stores.Log.FilePath)
            .Where(File.Exists).Distinct().ToDictionary(f => f, File.ReadAllBytes);

    private static void AssertUnchanged(Dictionary<string, byte[]> snapshot)
    {
        foreach (var (file, bytes) in snapshot)
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(file)), file + " changed");
    }
}
