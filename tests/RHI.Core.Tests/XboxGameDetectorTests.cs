using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Xbox;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Refs #90: Xbox / Game Pass installs from Xodus's package format and PartingX's records (synthetic fixtures written
/// here; an encrypted exe is random bytes without <c>MZ</c>). Owner decisions: unfinished downloads hidden, Play opens
/// the owning app (never the game), folders from Settings and from a PartingX breadcrumb, nothing hard-coded.
/// </summary>
public sealed class XboxGameDetectorTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private readonly string _home;
    private readonly string _instance;
    private readonly string _state;
    private readonly string _payloads;
    private readonly string _envs;
    private readonly string _runtimes;

    public XboxGameDetectorTests()
    {
        _home = _tree.Dir("home");
        _instance = _tree.Dir("px");
        _state = _tree.Dir("px", "state", "partingx");
        _payloads = _tree.Dir("px", "payloads");
        _envs = _tree.Dir("px", "envs");
        _tree.Dir("px", "envs", ".templates", "rt1", "pfx", "drive_c");
        _runtimes = _tree.Dir("px", "runtimes");
        _tree.File(JsonSerializer.Serialize(new
        {
            payloadRoot = _payloads, environmentRoot = _envs, runtimeRoot = _runtimes, backupRoot = "/b",
            xodusBinary = "/x/xodus-cli"
        }), "px", "state", "partingx", "settings.json");
        _tree.File("", "px", "runtimes", "rt1", "bin", "wine");
        _tree.File("", "px", "runtimes", "rt1", "bin", "wineserver");
    }

    public void Dispose() => _tree.Dispose();

    private static readonly byte[] Encrypted = [0x13, 0x37, .. Enumerable.Repeat((byte)0xA5, 4096)];
    private static readonly byte[] PlainPe = [(byte)'M', (byte)'Z', .. new byte[4096]];

    private static string GdkConfig(string exe, string? storeId = "9ABC", string display = "Shell Name", int version = 1, string arch = "x64")
        => "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
           $"<Game configVersion=\"{version}\"><Identity Name=\"Pub.Game\" Version=\"1.0.0.0\" />" +
           $"<ShellVisuals DefaultDisplayName=\"{display}\" />" +
           $"<ExecutableList><Executable Name=\"{exe}\" Id=\"Game\" TargetDeviceFamily=\"PC\" /></ExecutableList>" +
           (storeId is null ? "" : $"<StoreId>{storeId}</StoreId>") +
           $"<DesktopRegistration><ProcessorArchitecture>{arch}</ProcessorArchitecture></DesktopRegistration></Game>";

    private static string Manifest(string exe, string arch = "x64", string display = "Manifest Name")
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
           $"<Identity Name=\"Pub.Game\" Version=\"1.0.0.0\" ProcessorArchitecture=\"{arch}\" />" +
           $"<Properties><DisplayName>{display}</DisplayName></Properties>" +
           $"<Applications><Application Id=\"Game\" Executable=\"{exe}\" /></Applications></Package>";

    /// <summary>A finished PartingX title: marker, msixvc, config + manifest, encrypted exe, environment on runtime rt1.</summary>
    private string Title(string id, string exe = "Game.exe", string? title = "Marker Title", string configFile = "MicrosoftGame.Config",
        string? config = null, bool environment = true, string? marker = null)
    {
        var dir = _tree.Dir("px", "payloads", id);
        _tree.File(marker ?? JsonSerializer.Serialize(new { storeId = id, installedAt = 1, packageTitle = title }), "px", "payloads", id, ".partingx-owned");
        _tree.File("msixvc", "px", "payloads", id, XboxPackageReader.StreamingFileName);
        _tree.File(config ?? GdkConfig(exe, id), "px", "payloads", id, configFile);
        _tree.File(Manifest(exe), "px", "payloads", id, "appxmanifest.xml");
        _tree.Bytes(Encrypted, ["px", "payloads", id, .. exe.Split('/')]);
        if (environment)
        {
            _tree.Dir("px", "envs", "default", id, "prefix", "drive_c");
            _tree.File(JsonSerializer.Serialize(new { selectedRuntime = "rt1" }), "px", "envs", "default", id, "partingx.json");
        }
        return dir;
    }

    private string RunScript(string content = "#!/bin/sh\nexport XDG_STATE_HOME=x\nexec ./PartingX.AppImage \"$@\"\n")
    {
        var script = _tree.File(content, "px", "run.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private XboxDiscoveryInputs Inputs(IReadOnlyList<string>? userFolders = null, string? breadcrumbs = null,
        IReadOnlyList<string>? applicationDirs = null, IReadOnlyList<string>? pathDirs = null, IReadOnlyList<string>? defaults = null)
        => new(_home, defaults ?? [Path.Combine(_home, ".local", "state", "partingx")], userFolders ?? [_state], breadcrumbs,
            new OwningAppLocator(applicationDirs ?? [], pathDirs ?? []));

    private static (IReadOnlyList<DetectedGame> Games, IReadOnlyList<string> Warnings) Detect(XboxDiscoveryInputs inputs)
        => DetectorDiagnostics.CollectAsync(() => Task.FromResult(XboxGameDetector.DetectIn(inputs, CancellationToken.None))).GetAwaiter().GetResult();

    [Fact]
    public void PartingXTitle_IsAFullCard_OpeningItsInstanceThroughRunSh()
    {
        var dir = Title("9ABC");
        var script = RunScript();

        var (games, warnings) = Detect(Inputs());

        var game = Assert.Single(games);
        Assert.Empty(warnings);
        Assert.Equal("Marker Title", game.Name);
        Assert.Equal(dir, game.InstallPath);
        Assert.Equal("Xbox", game.Source);
        Assert.Equal(Path.Combine(dir, "Game.exe"), game.ExecutablePath);
        Assert.False(game.RequiresExplicitPatchTarget);
        // Encrypted exe: the package states the bitness.
        Assert.Equal(PeArchitecture.X64, game.ForcedArchitecture);
        Assert.Equal(Path.Combine(_envs, "default", "9ABC", "prefix"), game.PrefixPath);
        Assert.Equal(Path.Combine(_runtimes, "rt1", "bin", "wine"), game.WineBinaryPath);
        Assert.Equal(Path.Combine(_runtimes, "rt1", "bin", "wineserver"), game.WineServerPath);
        Assert.Equal("wine", game.CompatibilityLayerType);
        Assert.Equal("PartingX · rt1", game.Runner);
        // Play opens PartingX (its instance script), never the game.
        Assert.Equal($"'{script}'", game.LaunchCommand);
        Assert.Equal("PartingX", game.ParentLauncherName);
        Assert.Equal(new XboxPackageInfo("9ABC", "PartingX", $"'{script}'", Path.Combine(_state, "jobs.json")), game.XboxPackage);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    [Fact]
    public void UnrealSubfolderExe_AndManifestCase_ResolveToTheDiskSpelling()
    {
        var ue = Title("9UE1", "Proj/Binaries/WinGDK/Proj-WinGDK-Shipping.exe");
        Title("9CASE", "BloodDungeon.exe", config: GdkConfig("blooddungeon.exe", "9CASE"));

        var games = Detect(Inputs()).Games.ToDictionary(g => g.XboxPackage!.StoreId!);

        Assert.Equal(Path.Combine(ue, "Proj", "Binaries", "WinGDK", "Proj-WinGDK-Shipping.exe"), games["9UE1"].ExecutablePath);
        Assert.Equal(Path.Combine(_payloads, "9CASE", "BloodDungeon.exe"), games["9CASE"].ExecutablePath);
    }

    [Fact]
    public void CaseTwins_LeaveNoTarget_ButTheCardIsListed()
    {
        Title("9TWIN", "game.exe", config: GdkConfig("GAME.EXE", "9TWIN"));
        _tree.Bytes(Encrypted, "px", "payloads", "9TWIN", "Game.exe");

        var (games, warnings) = Detect(Inputs());

        var game = Assert.Single(games);
        Assert.Null(game.ExecutablePath);
        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.Single(warnings);
    }

    [Fact]
    public void UnfinishedDownloads_AndForeignMarkers_AreNotListed_AndAreNoScanProblem()
    {
        Title("9DONE");
        var partial = _tree.Dir("px", "payloads", "9PART");
        _tree.File("", "px", "payloads", "9PART", ".partingx-partial");
        _tree.File("tmp", "px", "payloads", "9PART", XboxPackageReader.StreamingTempFileName);
        _tree.File(GdkConfig("Game.exe", "9PART"), "px", "payloads", "9PART", "MicrosoftGame.config");
        Title("9OTHER", marker: JsonSerializer.Serialize(new { storeId = "9SOMETHINGELSE" }));

        var (games, warnings) = Detect(Inputs());

        Assert.Equal(["9DONE"], games.Select(g => g.XboxPackage!.StoreId!));
        Assert.True(Directory.Exists(partial));
        // The foreign marker is named; the unfinished download only goes to the log.
        Assert.Single(warnings);
    }

    [Fact]
    public void CorruptConfig_IsListedWithoutATarget_AndWarns()
    {
        Title("9BAD", config: "<Game><ExecutableList><Executable Name=");
        File.Delete(Path.Combine(_payloads, "9BAD", "appxmanifest.xml"));

        var (games, warnings) = Detect(Inputs());

        var game = Assert.Single(games);
        Assert.Null(game.ExecutablePath);
        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.Contains(warnings, w => w.Contains("9BAD", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyLauncherPackage_NeedsAnExplicitTarget_AndKeepsItsX86Bitness()
    {
        Title("9F3", "Launcher.exe", configFile: "MicrosoftGame.config", config: "<Game configVersion=\"0\"><Identity Name=\"Beth.F3\" /></Game>");
        File.WriteAllText(Path.Combine(_payloads, "9F3", "appxmanifest.xml"), Manifest("Launcher.exe", "x86"));

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(_payloads, "9F3", "Launcher.exe"), game.ExecutablePath);
        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.False(game.HasConfirmedPatchTarget);
        Assert.Equal(PeArchitecture.X86, game.ForcedArchitecture);
    }

    [Fact]
    public void UserRecipeExecutable_WinsOverThePackages()
    {
        Title("9EMU", "EmuMenu/EmuMenu.exe");
        _tree.Bytes(Encrypted, "px", "payloads", "9EMU", "Emu.exe");
        _tree.File(JsonSerializer.Serialize(new { schema = 1, store_id = "9EMU", executable = "Emu.exe" }), "px", "state", "partingx", "recipes", "9EMU.json");

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(_payloads, "9EMU", "Emu.exe"), game.ExecutablePath);
        Assert.False(game.RequiresExplicitPatchTarget);
    }

    [Fact]
    public void AppxPackage_LauncherCfgNamesTheEntryExe_PlainPeKeepsPeBitness()
    {
        var dir = _tree.Dir("px", "payloads", "9APPX");
        _tree.File(JsonSerializer.Serialize(new { storeId = "9APPX", packageTitle = "Broken" }), "px", "payloads", "9APPX", ".partingx-owned");
        _tree.File("{}", "px", "payloads", "9APPX", XboxPackageReader.AppxRecordFileName);
        _tree.File(Manifest("Launcher.exe"), "px", "payloads", "9APPX", "AppxManifest.xml");
        _tree.Bytes(PlainPe, "px", "payloads", "9APPX", "launcher.exe");
        _tree.Bytes(PlainPe, "px", "payloads", "9APPX", "BrokenAge.exe");
        _tree.File("BrokenAge.exe\n-arg\n", "px", "payloads", "9APPX", "launcher.cfg");

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(dir, "BrokenAge.exe"), game.ExecutablePath);
        Assert.False(game.RequiresExplicitPatchTarget);
        Assert.Null(game.ForcedArchitecture); // a readable PE: the probe reads it
        Assert.Null(game.PrefixPath); // no environment yet: still listed
    }

    [Fact]
    public void ExternalRuntime_UsesItsWinePath()
    {
        Title("9EXT", environment: false);
        var wine = _tree.File("", "opt", "wine", "bin", "wine");
        _tree.Dir("px", "envs", "default", "9EXT", "prefix", "drive_c");
        _tree.File(JsonSerializer.Serialize(new { external = new { winePath = wine } }), "px", "envs", "default", "9EXT", "partingx.json");

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(wine, game.WineBinaryPath);
        Assert.Null(game.WineServerPath);
    }

    [Fact]
    public void Name_FallsBackFromMarkerToCatalogueToShellVisuals_IgnoringResourceReferences()
    {
        Title("9CAT", title: null);
        Title("9SHELL", title: null, config: GdkConfig("Game.exe", "9SHELL", display: "Shell Game"));
        Title("9RES", title: null, config: GdkConfig("Game.exe", "9RES", display: "ms-resource:AppName"));
        _tree.File(JsonSerializer.Serialize(new { titles = new[] { new { productId = "9CAT", title = "Catalogue Game" } } }),
            "px", "state", "partingx", "catalogue", "index.json");

        var names = Detect(Inputs()).Games.ToDictionary(g => g.XboxPackage!.StoreId!, g => g.Name);

        Assert.Equal("Catalogue Game", names["9CAT"]);
        Assert.Equal("Shell Game", names["9SHELL"]);
        Assert.Equal("Manifest Name", names["9RES"]);
    }

    [Fact]
    public void XodusOnlyInstall_InGames_IsAnXodusCard_PlayDisabledUntilAnXodusAppExists()
    {
        var dir = _tree.Dir("home", "Games", "blue-prince-xodus");
        _tree.File("msixvc", "home", "Games", "blue-prince-xodus", XboxPackageReader.StreamingFileName);
        _tree.File(GdkConfig("BLUE PRINCE.exe", "9BP", "Blue Prince"), "home", "Games", "blue-prince-xodus", "MicrosoftGame.config");
        _tree.Bytes(Encrypted, "home", "Games", "blue-prince-xodus", "BLUE PRINCE.exe");

        var game = Assert.Single(Detect(Inputs(userFolders: [])).Games);

        Assert.Equal("Blue Prince", game.Name);
        Assert.Equal(dir, game.InstallPath);
        Assert.Null(game.PrefixPath);
        Assert.Null(game.LaunchCommand);
        Assert.Equal(new XboxPackageInfo("9BP", "Xodus", null, null), game.XboxPackage);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));

        // Once the Xodus GUI is installed, Play opens it.
        var apps = _tree.Dir("apps");
        _tree.File("[Desktop Entry]\nName=Xodus\nExec=/opt/xodus/xodus-gui %U\nType=Application\n", "apps", "io.github.xodus-gaming.xodus.desktop");
        var withGui = Assert.Single(Detect(Inputs(userFolders: [], applicationDirs: [apps])).Games);
        Assert.Equal("/opt/xodus/xodus-gui", withGui.LaunchCommand);
        Assert.Equal("Xodus", withGui.ParentLauncherName);
    }

    [Fact]
    public void FolderSearch_SkipsLinksPrefixesAndDeepFolders_AndListsAPayloadOnce()
    {
        Title("9ONCE");
        void Package(params string[] parts)
        {
            _tree.File("m", [.. parts, XboxPackageReader.StreamingFileName]);
            _tree.File(GdkConfig("G.exe", null, string.Join('-', parts)), [.. parts, "MicrosoftGame.config"]);
            _tree.Bytes(Encrypted, [.. parts, "G.exe"]);
        }
        Package("home", "Games", "a", "b", "ok3");
        Package("home", "Games", "a", "b", "c", "toodeep");
        Package("home", "Games", "prefix", "drive_c", "inprefix");
        Package("elsewhere", "linked");
        _tree.Link(_tree.Path_("elsewhere"), "home", "Games", "link");

        // The payload folder is also searched as a folder of Xodus installs: still one card.
        var games = Detect(Inputs(userFolders: [_state, _payloads])).Games;

        Assert.Equal(["9ONCE", "home-Games-a-b-ok3"], games.Select(g => g.XboxPackage!.StoreId ?? g.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Settings_Folders_MissingOrWithoutSettings_Warn_WhileTheDefaultStateFolderStaysQuiet()
    {
        var empty = _tree.Dir("home", ".local", "state", "partingx");
        var missing = Path.Combine(_tree.Root, "gone");
        var noSettings = _tree.Dir("bare", "state", "partingx");
        _tree.File("{ broken", "bare", "state", "partingx", "settings.json");

        var (games, warnings) = Detect(Inputs(userFolders: [missing, _tree.Path_("bare")], defaults: [empty]));

        Assert.Empty(games);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains(missing, StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(noSettings, StringComparison.Ordinal));
    }

    [Fact]
    public void InstanceFolder_InSettings_IsReadAsItsStateFolder()
    {
        Title("9INST");

        Assert.Single(Detect(Inputs(userFolders: [_instance])).Games);
    }

    [Fact]
    public void Breadcrumb_FindsAnInstanceNobodyListed_AndItsLauncherOpensIt()
    {
        Title("9CRUMB");
        var launcher = _tree.File("", "px", "PartingX.AppImage");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var crumbs = _tree.Dir("data", "partingx", "instances");
        _tree.File(JsonSerializer.Serialize(new { schema = 1, stateDir = _state, launcher, launcherArgs = new[] { "--minimised" }, future = 1 }),
            "data", "partingx", "instances", "acceptance.json");
        _tree.File("{ not json", "data", "partingx", "instances", "broken.json");

        var game = Assert.Single(Detect(Inputs(userFolders: [], breadcrumbs: crumbs)).Games);

        Assert.Equal($"'{launcher}' '--minimised'", game.LaunchCommand);
    }

    [Fact]
    public void OwningApp_IsNotGuessed_ForAnInstanceThatIsNotTheDefault()
    {
        Title("9NOAPP");
        RunScript("#!/bin/sh\nexec something-else\n"); // a run.sh that does not start PartingX
        var apps = _tree.Dir("apps");
        _tree.File("[Desktop Entry]\nName=PartingX\nExec=/opt/PartingX.AppImage %u\n", "apps", "partingx.desktop");

        var game = Assert.Single(Detect(Inputs(applicationDirs: [apps])).Games);

        // The desktop entry starts PartingX on its default state, which is not this instance.
        Assert.Null(game.LaunchCommand);
        Assert.Null(game.ParentLauncherName);
        Assert.Equal("PartingX", game.XboxPackage!.OwningApp);

        // For the default state folder, the desktop entry is the way to open it.
        var asDefault = Assert.Single(Detect(Inputs(userFolders: [], applicationDirs: [apps], defaults: [_state])).Games);
        Assert.Equal("/opt/PartingX.AppImage", asDefault.LaunchCommand);
    }

    [Fact]
    public async Task WinePrefixDetector_NeverListsPartingXPrefixes()
    {
        // A PartingX environment folder under ~/Games: title prefixes (3 levels down) and runtime templates.
        var prefix = _tree.Dir("home", "Games", "envs", "default", "9P", "prefix");
        _tree.File("", "home", "Games", "envs", "default", "9P", "prefix", "system.reg");
        var template = _tree.Dir("home", "Games", "envs", ".templates", "rt1");
        _tree.File("", "home", "Games", "envs", ".templates", "rt1", "system.reg");
        _tree.Bytes(PlainPe, "home", "Games", "envs", ".templates", "rt1", "drive_c", "Program Files", "Tool", "Tool.exe");
        var standalone = _tree.Dir("home", "Games", "standalone");
        _tree.File("", "home", "Games", "standalone", "system.reg");
        _tree.Bytes(PlainPe, "home", "Games", "standalone", "drive_c", "Program Files", "Free", "Free.exe");

        var owned = LauncherOwnedPrefixes.Load(_home, []);
        var listed = await new WinePrefixDetector(_home, [_tree.Dir("lutris")]).DetectAsync();

        Assert.True(owned.Owns(prefix));
        Assert.True(owned.Owns(template));
        Assert.False(owned.Owns(standalone));
        Assert.Equal(["Free"], listed.Select(g => g.Name));
    }

    [Fact]
    public async Task ManualEntryAtThePayload_MergesWithTheXboxCard_AndPlayOpensPartingX()
    {
        var dir = Title("9HK", "Hollow Knight.exe", title: "Hollow Knight: Voidheart Edition");
        var script = RunScript();
        var store = new LinuxGameLibraryStore(_tree.Path_("game-library.json"));
        store.Save(new LinuxGameLibraryState
        {
            ManualGames = [new LinuxManualGameEntry { Name = "Hollow Knight", InstallPath = dir, ExecutablePath = Path.Combine(dir, "Hollow Knight.exe") }]
        });
        var scanner = new GameLibraryScanner([new ManualGameDetector(store), new XboxGameDetector(() => Inputs())]);

        var card = Assert.Single(await scanner.ScanAsync());
        LinuxGameIdentityService.ApplyDetectedIdentity(card);

        Assert.Equal("Hollow Knight", card.Name);
        Assert.True(card.IsManuallyAdded);
        Assert.Equal("9HK", card.XboxPackage!.StoreId);
        Assert.Equal($"'{script}'", card.LaunchCommand);
        Assert.Equal("PartingX", card.ParentLauncherName);
        Assert.Equal(Path.Combine(_envs, "default", "9HK", "prefix"), card.PrefixPath);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(card));
        Assert.Equal("Xbox", card.LaunchProvider);
    }

    [Fact]
    public void EncryptedExe_StaysThePatchTarget_UnderThePackagesBitness()
    {
        var dir = Title("9KEEP", "Game.exe");
        _tree.Bytes(PlainPe, "px", "payloads", "9KEEP", "Other", "BigOther.exe");
        var game = Assert.Single(Detect(Inputs()).Games);

        DetectionHelpers.ApplyRuntimeMetadata(game, GameFileIndexer.Build(dir));

        Assert.Equal(Path.Combine(dir, "Game.exe"), game.ExecutablePath);
        Assert.Equal(dir, game.BinaryDirectory);
    }

    [Theory]
    [InlineData("'/opt/PartingX.AppImage'", LaunchHandoff.Xbox)]
    [InlineData("partingx --play 9ABC", LaunchHandoff.Xbox)]
    [InlineData("gamemoderun /usr/bin/xodus-gui", LaunchHandoff.Xbox)]
    [InlineData("xdg-open partingx://play/9ABC", LaunchHandoff.Xbox)]
    [InlineData("flatpak run io.github.xodus-gaming.xodus", LaunchHandoff.Xbox)]
    [InlineData("xodus-cli run /g ~/.local/share/xodus-wine/bin/wine --exe 'BLUE PRINCE.exe'", LaunchHandoff.None)]
    public void Classifier_OwningApps_AreAnXboxHandoff_ButADirectXodusRunIsNot(string command, LaunchHandoff expected)
        => Assert.Equal(expected, LaunchHandoffClassifier.Classify(new DetectedGame { Name = "G", LaunchCommand = command }));

    [Fact]
    public void Classifier_AUserTypedCommandOnAnXboxCard_IsReadByItsWords()
    {
        var game = new DetectedGame
        {
            Name = "G", Source = "Xbox", LaunchCommand = "/my/wrapper.sh",
            XboxPackage = new XboxPackageInfo("9A", "PartingX", "'/px/run.sh'", null)
        };
        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(game));
        game.LaunchCommand = "'/px/run.sh'";
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
        game.LaunchCommand = null;
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    [Fact]
    public void JobGuard_RefusesWhilePartingXHasAJobForTheTitle()
    {
        var jobs = _tree.File(JsonSerializer.Serialize(new
        {
            queue = new[] { new { id = "j1", kind = "Install", titleId = "9BUSY", state = "Running" } },
            history = new[] { new { id = "j0", kind = "Launch", titleId = "9IDLE", state = "Done" } }
        }), "px", "state", "partingx", "jobs.json");
        DetectedGame Game(string id) => new() { Name = id, XboxPackage = new XboxPackageInfo(id, "PartingX", null, jobs) };

        Assert.Equal(XboxPackageGuard.BusyMessage, XboxPackageGuard.Refusal(Game("9BUSY")));
        Assert.Null(XboxPackageGuard.Refusal(Game("9IDLE")));
        Assert.Null(XboxPackageGuard.Refusal(new DetectedGame { Name = "Steam game" }));

        File.WriteAllText(jobs, "{ truncated");
        Assert.Equal(XboxPackageGuard.UnreadablePrefix + jobs, XboxPackageGuard.Refusal(Game("9IDLE")));
    }

    [Fact]
    public async Task JobGuard_IsTheFirstCheckOfEveryChange()
    {
        var jobs = _tree.File(JsonSerializer.Serialize(new { queue = new[] { new { kind = "Prepare", titleId = "9BUSY" } } }), "px", "state", "partingx", "jobs.json");
        var dir = Title("9BUSY");
        var game = new DetectedGame
        {
            Name = "Busy", InstallPath = dir, ExecutablePath = Path.Combine(dir, "Game.exe"), BinaryDirectory = dir,
            XboxPackage = new XboxPackageInfo("9BUSY", "PartingX", null, jobs)
        };
        var service = new LinuxManagedComponentService(new LinuxDeploymentRecordStore(_tree.Path_("deployments.json")), reshade: null!,
            addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!, relimiter: null!, displayCommander: null!,
            mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!,
            d3dCompilerTools: null!, ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!);

        var result = await service.ApplyReShadeAddonSelectionAsync(game, []);

        Assert.False(result.Success);
        Assert.Equal(XboxPackageGuard.BusyMessage, result.Message);
    }

    [Fact]
    public void ProtectedFiles_AreRefused_EverythingElseInThePackageIsNot()
    {
        var dir = Title("9PROT", "Bin/Game.exe");
        _tree.Bytes(PlainPe, "px", "payloads", "9PROT", "Bin", "Plain.exe");
        var system32 = _tree.Dir("px", "envs", "default", "9PROT", "prefix", "drive_c", "windows", "system32");

        foreach (var path in new[]
                 {
                     Path.Combine(dir, "Bin", "Game.exe"), Path.Combine(dir, ".xodus-streaming.msixvc"), Path.Combine(dir, "microsoftgame.config"),
                     Path.Combine(dir, "AppxManifest.xml"), Path.Combine(dir, ".partingx-owned"), Path.Combine(system32, "dxgi.dll"),
                     Path.Combine(system32, "nvapi64.dll")
                 })
            Assert.Equal(PackageFileProtection.RefusalPrefix + path, PackageFileProtection.Refusal(path));

        foreach (var path in new[]
                 {
                     Path.Combine(dir, "Bin", "dxgi.dll"), Path.Combine(dir, "Bin", "ReShade.ini"), Path.Combine(dir, "Bin", "Plain.exe"),
                     Path.Combine(dir, "Bin", "MicrosoftGame.config"), Path.Combine(system32, "d3dcompiler_47.dll"),
                     _tree.Path_("other", "MicrosoftGame.config")
                 })
            Assert.Null(PackageFileProtection.Refusal(path));
    }

    [Fact]
    public async Task ProtectedFile_IsRefusedWhenADeploymentTracksIt_BeforeTheWritePhase()
    {
        var dir = Title("9TRACK");
        var journal = new DeploymentJournal(_tree.Path_("journal"));
        await using var operation = await journal.BeginAsync("test");
        using var cancel = new OperationCancellation(CancellationToken.None);

        var error = await Assert.ThrowsAsync<ProtectedPackageFileException>(() => operation.TrackFileAsync(Path.Combine(dir, "Game.exe"), cancel.Token));

        Assert.StartsWith(PackageFileProtection.RefusalPrefix, error.Message);
        Assert.False(cancel.WritesStarted);
        await operation.TrackFileAsync(Path.Combine(dir, "dxgi.dll"));
    }

    [Fact]
    public void FolderStore_KeepsAbsoluteDistinctFolders()
    {
        var store = new XboxFolderStore(_tree.Path_("cfg", "xbox-folders.json"));
        Assert.Empty(store.Folders());

        store.Save(["/a/b/", " /a/b", "/c", ""]);

        Assert.Equal(["/a/b", "/c"], store.Folders());
    }

    [Fact]
    public void PlayTooltip_SaysToStartTheGameFromTheOwningApp()
    {
        Assert.Null(RHI.Desktop.ViewModels.XboxCardText.PlayUnavailable(new DetectedGame { Name = "Steam" }));
        var text = RHI.Desktop.ViewModels.XboxCardText.PlayUnavailable(new DetectedGame
        {
            Name = "G", XboxPackage = new XboxPackageInfo("9A", "PartingX", null, null)
        });
        Assert.Contains("PartingX", text);
        Assert.Null(RHI.Desktop.ViewModels.XboxCardText.PlayUnavailable(new DetectedGame
        {
            Name = "G", LaunchCommand = "'/px/run.sh'", XboxPackage = new XboxPackageInfo("9A", "PartingX", "'/px/run.sh'", null)
        }));
    }

    [Fact]
    public void ManualAdd_RecognisesAGdkFolder_WhateverTheConfigsCase()
    {
        var dir = _tree.Dir("manual", "Game");
        _tree.File("<Game />", "manual", "Game", "MicrosoftGame.Config");

        Assert.True(ManualGamePathInference.LooksLikeGameRoot(dir));
    }

    [Fact]
    public void HdrOnAnXboxCard_IsTurnedOnInTheOwningApp()
    {
        var plan = new RHI.Platform.Linux.Hdr.LinuxHdrLaunchPlan(true, true, new Dictionary<string, string>(), null, "%command%",
            default, Handoff: LaunchHandoff.Xbox);
        var game = new DetectedGame { Name = "Hollow Knight", XboxPackage = new XboxPackageInfo("9HK", "PartingX", null, null) };

        var text = RHI.Desktop.ViewModels.HdrLaunchText.ApplyInLauncher(plan, game);

        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Format("Scan.HdrApplyInOwningApp", "PartingX"), text);
    }

    [Fact]
    public void FoldersPresenter_SavesThroughTheHostsHelper_AndReportsChanges()
    {
        var store = new XboxFolderStore(_tree.Path_("cfg", "xbox-folders.json"));
        var saves = 0;
        string? refusal = null;
        var presenter = new RHI.Desktop.ViewModels.XboxFoldersPresenter(store, save =>
        {
            saves++;
            if (refusal is not null) return refusal;
            save();
            return null;
        });
        var changed = 0;
        presenter.Changed += () => changed++;

        Assert.Null(presenter.Add(_state + "/"));
        Assert.Null(presenter.Add(_state)); // already listed: nothing saved
        Assert.Equal([_state], presenter.Folders);
        Assert.Equal([_state], store.Folders());

        refusal = "refused";
        Assert.Equal("refused", presenter.Remove(_state));
        Assert.Equal([_state], presenter.Folders);

        refusal = null;
        Assert.Null(presenter.Remove(_state));
        Assert.Empty(store.Folders());
        Assert.Equal((3, 2), (saves, changed));
    }
}
