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
/// Refs #90: Xbox / Game Pass installs from Xodus's package format and the installer app's records (synthetic fixtures written
/// here; an encrypted exe is random bytes without <c>MZ</c>). Owner decisions: unfinished downloads hidden, Play opens
/// the owning app (never the game), folders from Settings and from an installer-app breadcrumb, nothing hard-coded.
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
        _state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        _payloads = _tree.Dir("px", "payloads");
        _envs = _tree.Dir("px", "envs");
        _tree.Dir("px", "envs", ".templates", "rt1", "pfx", "drive_c");
        _runtimes = _tree.Dir("px", "runtimes");
        _tree.File(JsonSerializer.Serialize(new
        {
            payloadRoot = _payloads, environmentRoot = _envs, runtimeRoot = _runtimes, backupRoot = "/b",
            xodusBinary = "/x/xodus-cli"
        }), "px", "state", XboxInstallerApp.FolderName, "settings.json");
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

    /// <summary>A finished installer-app title: marker, msixvc, config + manifest, encrypted exe, environment on runtime rt1.</summary>
    private string Title(string id, string exe = "Game.exe", string? title = "Marker Title", string configFile = "MicrosoftGame.Config",
        string? config = null, bool environment = true, string? marker = null)
    {
        var dir = _tree.Dir("px", "payloads", id);
        _tree.File(marker ?? JsonSerializer.Serialize(new { storeId = id, installedAt = 1, packageTitle = title }), "px", "payloads", id, XboxInstallerApp.OwnedMarker);
        _tree.File("msixvc", "px", "payloads", id, XboxPackageReader.StreamingFileName);
        _tree.File(config ?? GdkConfig(exe, id), "px", "payloads", id, configFile);
        _tree.File(Manifest(exe), "px", "payloads", id, "appxmanifest.xml");
        _tree.Bytes(Encrypted, ["px", "payloads", id, .. exe.Split('/')]);
        if (environment)
        {
            _tree.Dir("px", "envs", "default", id, "prefix", "drive_c");
            _tree.File(JsonSerializer.Serialize(new { selectedRuntime = "rt1" }), "px", "envs", "default", id, XboxInstallerApp.EnvironmentFile);
        }
        return dir;
    }

    private string RunScript(string? content = null)
    {
        var script = _tree.File(content ?? "#!/bin/sh\nexport XDG_STATE_HOME=x\nexec ./" + XboxInstallerApp.ProductTag + ".AppImage \"$@\"\n", "px", "run.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private XboxDiscoveryInputs Inputs(IReadOnlyList<string>? userFolders = null, string? breadcrumbs = null,
        IReadOnlyList<string>? applicationDirs = null, IReadOnlyList<string>? pathDirs = null, IReadOnlyList<string>? defaults = null)
        => new(_home, defaults ?? [Path.Combine(_home, ".local", "state", XboxInstallerApp.FolderName)], userFolders ?? [_state], breadcrumbs,
            new OwningAppLocator(applicationDirs ?? [], pathDirs ?? []));

    private static (IReadOnlyList<DetectedGame> Games, IReadOnlyList<string> Warnings) Detect(XboxDiscoveryInputs inputs)
        => DetectorDiagnostics.CollectAsync(() => Task.FromResult(XboxGameDetector.DetectIn(inputs, CancellationToken.None))).GetAwaiter().GetResult();

    [Fact]
    public void InstallerAppTitle_IsAFullCard_OpeningItsInstanceThroughRunSh()
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
        // Encrypted exe: the card claims no bitness until #167 reads it from the package.
        Assert.Null(game.ForcedArchitecture);
        Assert.Equal(Path.Combine(_envs, "default", "9ABC", "prefix"), game.PrefixPath);
        Assert.Equal(Path.Combine(_runtimes, "rt1", "bin", "wine"), game.WineBinaryPath);
        Assert.Equal(Path.Combine(_runtimes, "rt1", "bin", "wineserver"), game.WineServerPath);
        Assert.Equal("wine", game.CompatibilityLayerType);
        Assert.Equal($"{XboxInstallerApp.DisplayName} · rt1", game.Runner);
        // Play opens the installer app (its instance script), never the game.
        Assert.Equal($"'{script}'", game.LaunchCommand);
        Assert.Equal(XboxInstallerApp.DisplayName, game.ParentLauncherName);
        Assert.Equal(new XboxPackageInfo("9ABC", XboxInstallerApp.DisplayName, $"'{script}'", Path.Combine(_state, "jobs.json"), Architecture: PeArchitecture.X64), game.XboxPackage);
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
        _tree.File("", "px", "payloads", "9PART", XboxInstallerApp.PartialMarker);
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
    public void LegacyLauncherPackage_NeedsAnExplicitTarget_AndClaimsNoBitness()
    {
        Title("9F3", "Launcher.exe", configFile: "MicrosoftGame.config", config: "<Game configVersion=\"0\"><Identity Name=\"Beth.F3\" /></Game>");
        File.WriteAllText(Path.Combine(_payloads, "9F3", "appxmanifest.xml"), Manifest("Launcher.exe", "x86"));

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(_payloads, "9F3", "Launcher.exe"), game.ExecutablePath);
        Assert.True(game.RequiresExplicitPatchTarget);
        Assert.False(game.HasConfirmedPatchTarget);
        Assert.Null(game.ForcedArchitecture);
    }

    [Fact]
    public void UserRecipeExecutable_WinsOverThePackages()
    {
        Title("9EMU", "EmuMenu/EmuMenu.exe");
        _tree.Bytes(Encrypted, "px", "payloads", "9EMU", "Emu.exe");
        _tree.File(JsonSerializer.Serialize(new { schema = 1, store_id = "9EMU", executable = "Emu.exe" }), "px", "state", XboxInstallerApp.FolderName, "recipes", "9EMU.json");

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(_payloads, "9EMU", "Emu.exe"), game.ExecutablePath);
        Assert.False(game.RequiresExplicitPatchTarget);
    }

    [Fact]
    public void AppxPackage_LauncherCfgNamesTheEntryExe_PlainPeKeepsPeBitness()
    {
        var dir = _tree.Dir("px", "payloads", "9APPX");
        _tree.File(JsonSerializer.Serialize(new { storeId = "9APPX", packageTitle = "Broken" }), "px", "payloads", "9APPX", XboxInstallerApp.OwnedMarker);
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
        _tree.File(JsonSerializer.Serialize(new { external = new { winePath = wine } }), "px", "envs", "default", "9EXT", XboxInstallerApp.EnvironmentFile);

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(wine, game.WineBinaryPath);
        Assert.Null(game.WineServerPath);
    }

    [Fact]
    public void Name_IsTheInstalledProduct_NotTheSharedBasePackage()
    {
        // #175: Control Ultimate Edition (9P7LVXNVQ6K7) installs Control's base package, whose marker and package config
        // say "Control Standard Edition". The payload folder is the product the user installed; its catalogue record names it.
        Title("9ULT", title: "Control Standard Edition", config: GdkConfig("Game.exe", "9BASE", display: "Control Standard Edition"),
            marker: JsonSerializer.Serialize(new { storeId = "9ULT", installedAt = 1, packageProductId = "9BASE", packageTitle = "Control Standard Edition" }));
        _tree.File(JsonSerializer.Serialize(new
        {
            ProductId = "9ULT",
            LocalizedProperties = new[] { new { Language = "fr", ProductTitle = "Control Édition Ultime" }, new { Language = "en", ProductTitle = "Control Ultimate Edition" } }
        }), "px", "state", XboxInstallerApp.FolderName, "catalogue", "products", "9ULT.json");
        // No catalogue record: the newest job the installer app ran for the product names it.
        Title("9JOB", title: "Base Game");
        Title("9KEEP", title: "Marker Title");
        _tree.File(JsonSerializer.Serialize(new
        {
            queue = Array.Empty<object>(),
            history = new object[]
            {
                new { id = "j1", kind = "Install", titleId = "9JOB", title = "Old Name", state = "Done", created = 1 },
                new { id = "j2", kind = "Launch", titleId = "9JOB", title = "Job Game Deluxe", state = "Cancelled", created = 5 },
                new { id = "j3", kind = "Launch", titleId = "9OTHER", title = "Elsewhere", state = "Done", created = 9 }
            }
        }), "px", "state", XboxInstallerApp.FolderName, "jobs.json");

        var games = Detect(Inputs()).Games.ToDictionary(g => g.XboxPackage!.StoreId!);

        Assert.Equal("Control Ultimate Edition", games["9ULT"].Name);
        Assert.Equal("Job Game Deluxe", games["9JOB"].Name);
        Assert.Equal("Marker Title", games["9KEEP"].Name);
        // The Control UE features (RenoDX confirmation with the OptiScaler FG offer, post-install) key on that name.
        Assert.True(RHI.Platform.Linux.Deployment.ControlUePostInstallService.IsControlUltimateEdition(games["9ULT"]));
    }

    [Fact]
    public void Name_FallsBackFromMarkerToCatalogueToShellVisuals_IgnoringResourceReferences()
    {
        Title("9CAT", title: null);
        Title("9SHELL", title: null, config: GdkConfig("Game.exe", "9SHELL", display: "Shell Game"));
        Title("9RES", title: null, config: GdkConfig("Game.exe", "9RES", display: "ms-resource:AppName"));
        _tree.File(JsonSerializer.Serialize(new { titles = new[] { new { productId = "9CAT", title = "Catalogue Game" } } }),
            "px", "state", XboxInstallerApp.FolderName, "catalogue", "index.json");

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
        Assert.Equal(new XboxPackageInfo("9BP", "Xodus", null, null, Architecture: PeArchitecture.X64), game.XboxPackage);
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
        var empty = _tree.Dir("home", ".local", "state", XboxInstallerApp.FolderName);
        var missing = Path.Combine(_tree.Root, "gone");
        var noSettings = _tree.Dir("bare", "state", XboxInstallerApp.FolderName);
        _tree.File("{ broken", "bare", "state", XboxInstallerApp.FolderName, "settings.json");

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
    public void Breadcrumb_FindsAnInstanceNobodyListed_AndPlayIsItsPlayEntryPoint()
    {
        Title("9CRUMB");
        var launcher = _tree.File("", "px", XboxInstallerApp.ProductTag + ".AppImage");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var crumbs = _tree.Dir("data", XboxInstallerApp.FolderName, "instances");
        _tree.File(JsonSerializer.Serialize(new
            {
                schema = 1, app = XboxInstallerApp.ProductTag, stateDir = _state, payloadRoots = new[] { _payloads }, environmentRoot = _envs, runtimeRoot = _runtimes,
                launch = new { argv = new[] { launcher }, env = new { } },
                entryPoints = new { play = new[] { "--play", "{storeId}" }, show = new[] { "--show", "{storeId}" } }, future = 1
            }),
            "data", XboxInstallerApp.FolderName, "instances", "acceptance.json");
        _tree.File("{ not json", "data", XboxInstallerApp.FolderName, "instances", "broken.json");

        var game = Assert.Single(Detect(Inputs(userFolders: [], breadcrumbs: crumbs)).Games);

        Assert.Equal($"'{launcher}' '--play' '9CRUMB'", game.LaunchCommand);
        Assert.Equal([launcher, "--play", "9CRUMB"], game.XboxPackage!.PlayArgv);
    }

    [Fact]
    public void OwningApp_IsNotGuessed_ForAnInstanceThatIsNotTheDefault()
    {
        Title("9NOAPP");
        RunScript("#!/bin/sh\nexec something-else\n"); // a run.sh that does not start the installer app
        var apps = _tree.Dir("apps");
        _tree.File($"[Desktop Entry]\nName={XboxInstallerApp.ProductTag}\nExec=/opt/{XboxInstallerApp.ProductTag}.AppImage %u\n", "apps", XboxInstallerApp.FileTag + ".desktop");

        var game = Assert.Single(Detect(Inputs(applicationDirs: [apps])).Games);

        // The desktop entry starts installer-app on its default state, which is not this instance.
        Assert.Null(game.LaunchCommand);
        Assert.Null(game.ParentLauncherName);
        Assert.Equal(XboxInstallerApp.DisplayName, game.XboxPackage!.OwningApp);

        // For the default state folder, the desktop entry is the way to open it.
        var asDefault = Assert.Single(Detect(Inputs(userFolders: [], applicationDirs: [apps], defaults: [_state])).Games);
        Assert.Equal("/opt/" + XboxInstallerApp.ProductTag + ".AppImage", asDefault.LaunchCommand);
    }

    [Fact]
    public void DesktopEntries_ThatStartOneTitle_AreNeverTakenForTheApp()
    {
        Title("9DESK");
        var apps = _tree.Dir("apps");
        // A per-title shortcut (the installer app's or RHI's), and the installer app's id with a show argument: neither opens the app.
        _tree.File($"[Desktop Entry]\nName=Blue Prince\nExec=/opt/{XboxInstallerApp.ProductTag}.AppImage --play 9N431PX143P8\n", "apps", XboxInstallerApp.FileTag + "-blue-prince.desktop");
        _tree.File($"[Desktop Entry]\nName={XboxInstallerApp.ProductTag}\nExec=/opt/{XboxInstallerApp.ProductTag}.AppImage --show 9N431PX143P8\n", "apps", XboxInstallerApp.FileTag + ".desktop");
        _tree.File($"[Desktop Entry]\nName={XboxInstallerApp.ProductTag}\nExec=/opt/{XboxInstallerApp.ProductTag}.AppImage {XboxInstallerApp.FileTag}://play/9N431PX143P8\n", "apps", "io.example." + XboxInstallerApp.ProductTag + ".desktop");
        _tree.File($"[Desktop Entry]\nName={XboxInstallerApp.ProductTag} Helper\nExec=/opt/helper\n", "apps", XboxInstallerApp.FileTag + "-helper.desktop");

        var none = Assert.Single(Detect(Inputs(userFolders: [], applicationDirs: [apps], defaults: [_state])).Games);
        Assert.Null(none.LaunchCommand);

        _tree.File($"[Desktop Entry]\nName={XboxInstallerApp.ProductTag}\nExec=/opt/{XboxInstallerApp.ProductTag}.AppImage %u\n", "apps", "zz-app.desktop");
        var app = Assert.Single(Detect(Inputs(userFolders: [], applicationDirs: [apps], defaults: [_state])).Games);
        Assert.Equal("/opt/" + XboxInstallerApp.ProductTag + ".AppImage", app.LaunchCommand);
    }

    [Fact]
    public void TheAppBuildsOneXboxFolderStore_ForSettingsAndBothDetectors()
    {
        using var http = new HttpClient(new JsonHandler("{}"));
        var wine = new WineDllOverrideService();
        var app = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(_tree.Path_("d.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine, _tree.Path_("journal")));
        var detectors = (IGameDetector[])typeof(GameLibraryScanner)
            .GetField("_detectors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app.Scanner)!;

        Assert.Same(app.XboxFolders, detectors.OfType<XboxGameDetector>().Single().FolderStore);
        Assert.Same(app.XboxFolders, detectors.OfType<WinePrefixDetector>().Single().XboxFolderStore);
    }

    [Fact]
    public async Task WinePrefixDetector_NeverListsInstallerAppPrefixes()
    {
        // An installer-app environment folder under ~/Games: title prefixes (3 levels down) and runtime templates.
        var prefix = _tree.Dir("home", "Games", "envs", "default", "9P", "prefix");
        _tree.File("", "home", "Games", "envs", "default", "9P", "prefix", "system.reg");
        _tree.File("{}", "home", "Games", "envs", "default", "9P", XboxInstallerInstance.EnvironmentFile);
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
    public async Task ManualEntryAtThePayload_MergesWithTheXboxCard_AndPlayOpensInstallerApp()
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
        Assert.Equal(XboxInstallerApp.DisplayName, card.ParentLauncherName);
        Assert.Equal(Path.Combine(_envs, "default", "9HK", "prefix"), card.PrefixPath);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(card));
        Assert.Equal("Xbox", card.LaunchProvider);
    }

    [Fact]
    public void EncryptedExe_StaysThePatchTarget()
    {
        var dir = Title("9KEEP", "Game.exe");
        // A readable x64 PE elsewhere in the package: re-picking "an exe of the stated bitness" would choose it.
        var pe = new byte[8192];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z'; pe[0x3C] = 0x40;
        pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E'; pe[0x44] = 0x64; pe[0x45] = 0x86;
        _tree.Bytes(pe, "px", "payloads", "9KEEP", "Other", "BigOther.exe");
        var game = Assert.Single(Detect(Inputs()).Games);

        DetectionHelpers.ApplyRuntimeMetadata(game, GameFileIndexer.Build(dir));

        Assert.Equal(Path.Combine(dir, "Game.exe"), game.ExecutablePath);
        Assert.Equal(dir, game.BinaryDirectory);
    }

    [Theory]
    [InlineData("'/opt/{Tag}.AppImage'", LaunchHandoff.Xbox)]
    [InlineData("{tag} --play 9ABC", LaunchHandoff.Xbox)]
    [InlineData("gamemoderun /usr/bin/xodus-gui", LaunchHandoff.Xbox)]
    [InlineData("xdg-open {tag}://play/9ABC", LaunchHandoff.Xbox)]
    [InlineData("flatpak run io.github.xodus-gaming.xodus", LaunchHandoff.Xbox)]
    [InlineData("xodus-cli run /g ~/.local/share/xodus-wine/bin/wine --exe 'BLUE PRINCE.exe'", LaunchHandoff.None)]
    public void Classifier_OwningApps_AreAnXboxHandoff_ButADirectXodusRunIsNot(string command, LaunchHandoff expected)
        => Assert.Equal(expected, LaunchHandoffClassifier.Classify(new DetectedGame { Name = "G", LaunchCommand = InstallerTags(command) }));

    /// <summary>The installer app's own spellings, which theory data (compile-time constants) cannot name.</summary>
    private static string InstallerTags(string text)
        => text.Replace("{Tag}", XboxInstallerApp.ProductTag, StringComparison.Ordinal).Replace("{tag}", XboxInstallerApp.FileTag, StringComparison.Ordinal);

    [Fact]
    public void Classifier_AUserTypedCommandOnAnXboxCard_IsReadByItsWords()
    {
        var game = new DetectedGame
        {
            Name = "G", Source = "Xbox", LaunchCommand = "/my/wrapper.sh",
            XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, "'/px/run.sh'", null)
        };
        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(game));
        game.LaunchCommand = "'/px/run.sh'";
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
        game.LaunchCommand = null;
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    // Scan re-review minor 1: a user-edited line whose program is the card's installer-app launcher (the breadcrumb's argv[0],
    // here a run.sh) is the installer app's hand-off, so launch profiles and HDR are refused/redirected as for the card's own command.
    [Theory]
    [InlineData("/px/run.sh --play 9A", LaunchHandoff.Xbox)]
    [InlineData("env 'XDG_STATE_HOME=/s' '/px/run.sh' --show 9A", LaunchHandoff.Xbox)]
    [InlineData("gamemoderun /px/run.sh --play 9A", LaunchHandoff.Xbox)]
    [InlineData("sh -c '/px/run.sh --play 9A'", LaunchHandoff.Xbox)]
    [InlineData("/px/other.sh --play 9A", LaunchHandoff.None)]
    [InlineData("/elsewhere/run.sh --play 9A", LaunchHandoff.None)]
    public void Classifier_AUserCommandRunningTheCardsInstallerAppLauncher_IsAnXboxHandoff(string command, LaunchHandoff expected)
    {
        var game = new DetectedGame
        {
            Name = "G", Source = "Xbox", LaunchCommand = command, UserLaunchCommand = command,
            XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, "env 'XDG_STATE_HOME=/s' '/px/run.sh' --play 9A", null,
                PlayArgv: ["/px/run.sh", "--play", "9A"], ShowArgv: ["/px/run.sh", "--show", "9A"])
        };
        Assert.Equal(expected, LaunchHandoffClassifier.Classify(game));
        // A card whose package has no launcher of its own learns nothing from another card's.
        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(new DetectedGame { Name = "Other", LaunchCommand = "/px/run.sh --play 9A" }));
    }

    // Scan re-review nit 2: Edit game's line, typed back and run by a shell, does what the vector does even when env would
    // misread it (a variable name starting with '-', a launcher path holding '=').
    [Fact]
    public void RenderedEntryPoint_RunByAShell_PassesTheSameProgramArgumentsAndEnvironment()
    {
        var dir = _tree.Dir("odd=dir");
        var record = _tree.Path_("record.txt");
        var script = _tree.File($"#!/bin/sh\nprintf '%s\\n' \"$0\" \"$@\" > '{record}'\nenv | grep -E '^(-DASH|PLAIN)=' | sort >> '{record}'\n", "odd=dir", "run.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var line = OwningAppLocator.Render([script, "--play", "9A B"], new Dictionary<string, string> { ["-DASH"] = "1", ["PLAIN"] = "x y" });

        var shell = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(line);
        using (var run = System.Diagnostics.Process.Start(shell)!)
        {
            var stderr = run.StandardError.ReadToEnd();
            run.WaitForExit();
            Assert.True(run.ExitCode == 0, $"{line}: {stderr}");
        }

        Assert.Equal([script, "--play", "9A B", "-DASH=1", "PLAIN=x y"], File.ReadAllLines(record));
        Assert.StartsWith(dir, script);
    }

    // The entry point's launcher counts even when the rendered card command starts it through `sh -c` (a path with '=').
    [Fact]
    public void Classifier_TheEntryPointsLauncher_CountsWhenTheCardCommandWrapsIt()
    {
        string[] play = ["/px=1/run.sh", "--play", "9A"];
        var open = OwningAppLocator.Render(play, new Dictionary<string, string> { ["K"] = "V" });
        var game = new DetectedGame
        {
            Name = "G", LaunchCommand = "/px=1/run.sh --play 9A", UserLaunchCommand = "/px=1/run.sh --play 9A",
            XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, open, null, PlayArgv: play)
        };
        Assert.Contains("sh -c", open, StringComparison.Ordinal);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    // The opener's program counts only when it is a launcher file of its own: an opener going through a generic program
    // (xdg-open, flatpak) must not turn every user command using that program into the installer app's hand-off.
    [Theory]
    [InlineData("/usr/bin/xdg-open {tag}://open", "/usr/bin/xdg-open https://example.org/guide")]
    [InlineData("/usr/bin/flatpak run io.github.{tag}.{Tag}", "/usr/bin/flatpak run org.example.Tool")]
    [InlineData("/bin/sh -c '{tag}'", "/bin/sh /home/u/mine.sh")]
    public void Classifier_AGenericOpenerProgram_DoesNotMakeEveryCommandAnXboxHandoff(string opener, string command)
    {
        var game = new DetectedGame
        {
            Name = "G", LaunchCommand = command, UserLaunchCommand = command,
            XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, InstallerTags(opener), null)
        };
        Assert.NotEqual(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    [Fact]
    public void Classifier_AUserCommandRunningTheOpenersProgram_IsAnXboxHandoff()
    {
        var game = new DetectedGame
        {
            Name = "G", LaunchCommand = "'/px/run.sh' --verbose", UserLaunchCommand = "'/px/run.sh' --verbose",
            XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, "'/px/run.sh'", null)
        };
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
    }

    [Fact]
    public void JobRule_RefusesWhileInstallerAppHasAJobForTheTitle()
    {
        var jobs = _tree.File(JsonSerializer.Serialize(new
        {
            queue = new[] { new { id = "j1", kind = "Install", titleId = "9BUSY", state = "Running" } },
            history = new[] { new { id = "j0", kind = "Launch", titleId = "9IDLE", state = "Done" } }
        }), "px", "state", XboxInstallerApp.FolderName, "jobs.json");
        DetectedGame Game(string id) => new() { Name = id, XboxPackage = new XboxPackageInfo(id, XboxInstallerApp.DisplayName, null, jobs) };

        Assert.Equal(XboxWriteGate.BusyMessage, XboxWriteGate.Refusal(Game("9BUSY")));
        Assert.Null(XboxWriteGate.Refusal(Game("9IDLE")));
        Assert.Null(XboxWriteGate.Refusal(new DetectedGame { Name = "Steam game" }));

        File.WriteAllText(jobs, "{ truncated");
        Assert.Equal(XboxWriteGate.UnreadablePrefix + jobs, XboxWriteGate.Refusal(Game("9IDLE")));
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
                     Path.Combine(dir, "AppxManifest.xml"), Path.Combine(dir, XboxInstallerApp.OwnedMarker), Path.Combine(system32, "dxgi.dll"),
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
        Detect(Inputs()); // the scan registers the title with its (idle) instance
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
            Name = "G", XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, null, null)
        });
        Assert.Contains(RHI.Desktop.Localization.UiLocalizationService.Text("Scan.InstallerAppName"), text);
        Assert.DoesNotContain(XboxInstallerApp.ProductTag, text!, StringComparison.OrdinalIgnoreCase); // the app is never named
        Assert.Null(RHI.Desktop.ViewModels.XboxCardText.PlayUnavailable(new DetectedGame
        {
            Name = "G", LaunchCommand = "'/px/run.sh'", XboxPackage = new XboxPackageInfo("9A", XboxInstallerApp.DisplayName, "'/px/run.sh'", null)
        }));
    }

    [Fact]
    public void AnEncryptedXboxExe_TakesItsBitnessFromThePackage()
    {
        Title("9ENC");
        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(PeArchitecture.X64, game.XboxPackage!.Architecture);
        Assert.Null(game.ForcedArchitecture); // the RHI manifest's override stays the manifest's
        Assert.Equal(PeArchitecture.X64, GameArchitecture.Of(game));
        Assert.Null(XboxWriteGate.InstallRefusal(game));
    }

    [Fact]
    public async Task InstallOnAnEncryptedXboxExe_WhosePackageDeclaresNoArchitecture_RefusesWithThePlainReason()
    {
        Title("9ENC", config: GdkConfig("Game.exe", "9ENC", arch: "neutral"));
        _tree.File(Manifest("Game.exe", arch: "neutral"), "px", "payloads", "9ENC", "appxmanifest.xml");
        var game = Assert.Single(Detect(Inputs()).Games);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Null(game.XboxPackage!.Architecture);
        var service = new LinuxManagedComponentService(new LinuxDeploymentRecordStore(_tree.Path_("deployments.json")), reshade: null!,
            addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!, relimiter: null!, displayCommander: null!,
            mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!,
            d3dCompilerTools: null!, ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!);

        var results = new[]
        {
            await service.InstallAsync(game, ManagedComponentKind.ReShade, []),
            await service.ApplyReShadeAsync(game, new ReShadeInstallOptions()),
            await service.InstallOptiScalerAsync(game, default),
            await service.InstallUltimateAsiLoaderAsync(game, "dinput8.dll"),
            await service.InstallShortFuseAsync(game),
            await service.InstallD3DCompilerWithWinetricksAsync(game),
            await service.InstallD3DCompilerWithProtontricksAsync(game),
            await service.InstallLocalLumaPackageAsync(game, _tree.Path_("luma.zip")),
            await service.InstallDlss5FeederAsync(game, Dlss5FeederConsumer.RenoDxDlss5, null, null)
        };

        Assert.All(results, result => Assert.Equal((false, XboxWriteGate.EncryptedExecutableMessage), (result.Success, result.Message)));
        // Only installs ask: a change to what is already there is not refused for the bitness.
        Assert.Null(XboxWriteGate.Refusal(game));
    }

    [Fact]
    public void AReadableXboxExe_IsNotRefusedForItsBitness()
    {
        var dir = _tree.Dir("px", "payloads", "9PE");
        _tree.File(JsonSerializer.Serialize(new { storeId = "9PE" }), "px", "payloads", "9PE", XboxInstallerApp.OwnedMarker);
        _tree.File("{}", "px", "payloads", "9PE", XboxPackageReader.AppxRecordFileName);
        _tree.File(Manifest("Game.exe"), "px", "payloads", "9PE", "AppxManifest.xml");
        var pe = new byte[8192];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z'; pe[0x3C] = 0x40;
        pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E'; pe[0x44] = 0x64; pe[0x45] = 0x86;
        _tree.Bytes(pe, "px", "payloads", "9PE", "Game.exe");

        var game = Assert.Single(Detect(Inputs()).Games);

        Assert.Equal(Path.Combine(dir, "Game.exe"), game.ExecutablePath);
        Assert.Null(XboxWriteGate.InstallRefusal(game));
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
        var game = new DetectedGame { Name = "Hollow Knight", XboxPackage = new XboxPackageInfo("9HK", XboxInstallerApp.DisplayName, null, null) };

        var text = RHI.Desktop.ViewModels.HdrLaunchText.ApplyInLauncher(plan, game);

        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Format("Scan.HdrApplyInOwningApp", RHI.Desktop.Localization.UiLocalizationService.Text("Scan.InstallerAppName")), text);
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

    [Fact]
    public async Task ManifestEntry_KeepsTheEncryptedExeTheTarget_AndClaimsNoBitness()
    {
        Title("9MAN", "Game.exe", title: "Hollow Knight");
        var game = Assert.Single(Detect(Inputs()).Games);
        const string manifestJson = "{ \"version\": 2, \"engineHintOverrides\": { \"Hollow Knight\": \"Unity\" } }";
        using var http = new HttpClient(new JsonHandler(manifestJson));
        var probe = new LinuxGameRuntimeProbe(new UpstreamRhiManifestService(http, "https://example.test/manifest.json"));

        await probe.ProbeAsync(game, GameFileIndexer.Build(game.InstallPath));

        Assert.Equal("Unity", game.EngineHint); // the manifest entry applied
        Assert.Null(game.ForcedArchitecture);
        Assert.Equal(Path.Combine(game.InstallPath, "Game.exe"), game.ExecutablePath);
    }

    [Fact]
    public async Task ManifestSplitChildren_KeepTheXboxCardsOwningAppAndBitness()
    {
        // Upstream splits "DOOM + DOOM II" into its two games; an installer-app collection split that way stays an Xbox card.
        var dir = Title("9DOOM", title: "DOOM + DOOM II");
        Directory.CreateDirectory(Path.Combine(dir, "rerelease"));
        var game = Assert.Single(Detect(Inputs()).Games);
        game.ForcedArchitecture = PeArchitecture.X64; // a bitness set before the split (manifest or #167) travels too
        const string manifestJson = "{ \"version\": 3, \"splitGames\": { \"DOOM + DOOM II\": [ { \"name\": \"DOOM (Rerelease)\", \"subPath\": \"rerelease\" } ] } }";
        using var http = new HttpClient(new JsonHandler(manifestJson));
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, "https://example.test/manifest.json"));

        var child = Assert.Single(await policy.ApplyAsync([game]));

        Assert.Equal("DOOM (Rerelease)", child.Name);
        Assert.Same(game.XboxPackage, child.XboxPackage);
        Assert.Equal(PeArchitecture.X64, child.ForcedArchitecture);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(child));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
