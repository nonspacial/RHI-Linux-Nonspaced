using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Xbox;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Refs #90: the installer app's real breadcrumb (schema 1, <c>reviews/w3-90 entry-point spec</c>) is the primary source,
/// Play is the installer app's <c>--play &lt;storeId&gt;</c> entry point and the card menu's "Show in the app that installed it" its <c>--show</c>,
/// both executed as an argument vector with the breadcrumb's environment and no shell. Synthetic breadcrumbs and a fake
/// launcher script that records its argv/env; the real run.sh and installer-app are never run.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class InstallerBreadcrumbTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private readonly string _crumbs;

    public InstallerBreadcrumbTests() => _crumbs = _tree.Dir("data", XboxInstallerApp.FolderName, "instances");

    public void Dispose() => _tree.Dispose();

    private static readonly byte[] Encrypted = [0x13, 0x37, .. Enumerable.Repeat((byte)0xA5, 4096)];

    private static string GdkConfig(string id)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?><Game configVersion=\"1\"><Identity Name=\"Pub.Game\" Version=\"1.0.0.0\" />" +
           "<ShellVisuals DefaultDisplayName=\"Shell Name\" /><ExecutableList><Executable Name=\"Game.exe\" Id=\"Game\" /></ExecutableList>" +
           $"<StoreId>{id}</StoreId></Game>";

    /// <summary>A finished installer-app title under <paramref name="payloadRoot"/>, with an environment on runtime rt1 under <paramref name="envRoot"/>.</summary>
    private string Title(string payloadRoot, string id, string? envRoot = null)
    {
        var dir = Path.Combine(payloadRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, XboxInstallerApp.OwnedMarker), JsonSerializer.Serialize(new { storeId = id, packageTitle = "Title " + id }));
        File.WriteAllText(Path.Combine(dir, ".xodus-streaming.msixvc"), "msixvc");
        File.WriteAllText(Path.Combine(dir, "MicrosoftGame.config"), GdkConfig(id));
        File.WriteAllBytes(Path.Combine(dir, "Game.exe"), Encrypted);
        if (envRoot is not null)
        {
            Directory.CreateDirectory(Path.Combine(envRoot, "default", id, "prefix", "drive_c"));
            File.WriteAllText(Path.Combine(envRoot, "default", id, XboxInstallerApp.EnvironmentFile), JsonSerializer.Serialize(new { selectedRuntime = "rt1" }));
        }
        return dir;
    }

    /// <summary>
    /// A fake installer-app launcher (in a folder whose name needs quoting): writes each argument on its own line, its
    /// environment and its parent's name, then exits with <paramref name="exitCode"/>.
    /// </summary>
    private (string Script, string Out) FakeLauncher(int exitCode = 0)
    {
        var dir = _tree.Dir("Part ing'X $(x)");
        var output = _tree.Dir("out");
        var script = Path.Combine(dir, "run.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{output}/argv'\n" +
            $"env > '{output}/env'\n" +
            $"cat /proc/$PPID/comm > '{output}/parent'\n" +
            $"exit {exitCode}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, output);
    }

    private object Crumb(string stateDir, string[] payloadRoots, string? launcher, string? envRoot = null, string? runtimeRoot = null,
        string? jobs = null, int schema = 1, string? updatedAt = "2026-10-14T12:57:59.289Z", string[]? play = null, Dictionary<string, string>? env = null,
        string? app = null)
        => new
        {
            schema, app = app ?? XboxInstallerApp.ProductTag, version = "0.1.0", updatedAt, stateDir,
            settingsFile = Path.Combine(stateDir, "settings.json"),
            jobsFile = jobs ?? Path.Combine(stateDir, "jobs.json"),
            payloadRoots, environmentRoot = envRoot, runtimeRoot,
            backupRoot = "/b",
            launch = new { argv = launcher is null ? Array.Empty<string>() : new[] { launcher }, env = env ?? new Dictionary<string, string> { ["XDG_STATE_HOME"] = "/px state/it's" } },
            entryPoints = new { play = play ?? ["--play", "{storeId}"], show = new[] { "--show", "{storeId}" } },
            future = new { field = 1 }
        };

    private void WriteCrumb(string name, object crumb) => _tree.File(JsonSerializer.Serialize(crumb), "data", XboxInstallerApp.FolderName, "instances", name);

    private (IReadOnlyList<DetectedGame> Games, IReadOnlyList<string> Warnings) Detect(IReadOnlyList<string>? userFolders = null, IReadOnlyList<string>? defaults = null)
        => DetectorDiagnostics.CollectAsync(() => Task.FromResult(XboxGameDetector.DetectIn(
            new XboxDiscoveryInputs(_tree.Dir("home"), defaults ?? [], userFolders ?? [], _crumbs, new OwningAppLocator([], [])),
            CancellationToken.None))).GetAwaiter().GetResult();

    [Fact]
    public void Schema1_IsThePrimarySource_ItsFoldersAndJobListDecide_AndPlayIsItsEntryPoint()
    {
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var first = _tree.Dir("px", "payloads");
        var second = _tree.Dir("disk2", "payloads");
        var envs = _tree.Dir("px", "envs");
        var runtimes = _tree.Dir("px", "runtimes");
        _tree.File("", "px", "runtimes", "rt1", "bin", "wine");
        var a = Title(first, "9AAA", envs);
        Title(second, "9BBB");
        // settings.json names another payload folder: with a breadcrumb it is not read.
        var other = _tree.Dir("settings-only");
        Title(other, "9SET");
        _tree.File(JsonSerializer.Serialize(new { payloadRoot = other }), "px", "state", XboxInstallerApp.FolderName, "settings.json");
        var jobs = _tree.Path_("elsewhere", "jobs.json");
        var (script, _) = FakeLauncher();
        WriteCrumb("355353fff774f03b.json", Crumb(state, [first, second], script, envs, runtimes, jobs));

        var (games, warnings) = Detect();

        Assert.Empty(warnings);
        Assert.Equal(["9AAA", "9BBB"], games.Select(g => g.XboxPackage!.StoreId!).Order(StringComparer.Ordinal));
        var game = games.Single(g => g.XboxPackage!.StoreId == "9AAA");
        Assert.Equal(a, game.InstallPath);
        Assert.Equal(Path.Combine(envs, "default", "9AAA", "prefix"), game.PrefixPath);
        Assert.Equal(Path.Combine(runtimes, "rt1", "bin", "wine"), game.WineBinaryPath);
        var package = game.XboxPackage!;
        Assert.Equal(jobs, package.JobsFile);
        Assert.Equal([script, "--play", "9AAA"], package.PlayArgv);
        Assert.Equal([script, "--show", "9AAA"], package.ShowArgv);
        Assert.Equal(new Dictionary<string, string> { ["XDG_STATE_HOME"] = "/px state/it's" }, package.LaunchEnvironment);
        // The card's command is the entry point as a shell line (Edit game's detected launch); the installer app starts the game.
        Assert.Equal($"env 'XDG_STATE_HOME=/px state/it'\\''s' {OwningAppLocator.Shell(script)} '--play' '9AAA'", game.LaunchCommand);
        Assert.Equal(package.OpenCommand, game.LaunchCommand);
        Assert.Null(game.ParentLauncherName);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));

        // The write gate reads the breadcrumb's job list.
        Directory.CreateDirectory(Path.GetDirectoryName(jobs)!);
        File.WriteAllText(jobs, JsonSerializer.Serialize(new { queue = new[] { new { kind = "Launch", titleId = "9AAA" } } }));
        Assert.Equal(XboxWriteGate.BusyMessage, XboxWriteGate.Refusal(game));
        Assert.Equal(XboxWriteGate.BusyMessage, XboxWriteGate.Refusal(Path.Combine(game.PrefixPath!, "drive_c", "x.dll")));
    }

    [Fact]
    public void NewerSchema_IsSkippedWithAScanWarning_GarbledAndForeignFilesQuietly()
    {
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var payloads = _tree.Dir("px", "payloads");
        Title(payloads, "9NEW");
        var (script, _) = FakeLauncher();
        WriteCrumb("a-newer.json", Crumb(state, [payloads], script, schema: 2));
        _tree.File("{ \"schema\": 1, \"stateDir\": ", "data", XboxInstallerApp.FolderName, "instances", "b-garbled.json");
        WriteCrumb("c-foreign.json", Crumb(_tree.Dir("other", "state"), [payloads], script, app: "SomethingElse"));
        _tree.File("{}", "data", XboxInstallerApp.FolderName, "instances", "d-noschema.json");

        var (games, warnings) = Detect();

        Assert.Empty(games);
        Assert.Equal([InstallerBreadcrumb.NewerSchemaPrefix + Path.Combine(_crumbs, "a-newer.json")], warnings);
    }

    [Fact]
    public void TwoFilesForOneStateFolder_TheNewestUpdatedAtWins()
    {
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var old = _tree.Dir("old-payloads");
        var current = _tree.Dir("new-payloads");
        Title(old, "9OLD");
        Title(current, "9CUR");
        var (script, _) = FakeLauncher();
        // File order and recency disagree: "a" is newer than "z".
        WriteCrumb("a.json", Crumb(state, [current], script, updatedAt: "2026-10-14T13:00:00Z"));
        WriteCrumb("z.json", Crumb(state + "/", [old], script, updatedAt: "2026-10-13T09:00:00Z"));

        var games = Detect().Games;

        Assert.Equal(["9CUR"], games.Select(g => g.XboxPackage!.StoreId!));
    }

    [Fact]
    public void StoreId_IsSubstitutedInWholeArgumentsOnly()
    {
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var payloads = _tree.Dir("px", "payloads");
        Title(payloads, "9SUB");
        var (script, _) = FakeLauncher();
        WriteCrumb("i.json", Crumb(state, [payloads], script, play: ["--play", "{storeId}", "--tag={storeId}"]));

        var game = Assert.Single(Detect().Games);

        Assert.Equal([script, "--play", "9SUB", "--tag={storeId}"], game.XboxPackage!.PlayArgv);
    }

    [Fact]
    public void ABreadcrumbWhoseLauncherIsGone_FallsBackToOpeningInstallerApp()
    {
        var instance = _tree.Dir("px");
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var payloads = _tree.Dir("px", "payloads");
        Title(payloads, "9GONE");
        var runSh = _tree.File("#!/bin/sh\nexec ./" + XboxInstallerApp.ProductTag + ".AppImage \"$@\"\n", "px", "run.sh");
        File.SetUnixFileMode(runSh, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        WriteCrumb("i.json", Crumb(state, [payloads], Path.Combine(instance, "moved", XboxInstallerApp.ProductTag + ".AppImage")));

        var game = Assert.Single(Detect().Games);

        Assert.Null(game.XboxPackage!.PlayArgv);
        Assert.Null(game.XboxPackage.ShowArgv);
        Assert.Equal(OwningAppLocator.Shell(runSh), game.LaunchCommand);
        Assert.Equal(XboxInstallerApp.DisplayName, game.ParentLauncherName); // opening the app: the user starts the game there
        Assert.Null(RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppLabel(game));
    }

    private DetectedGame BreadcrumbCard(string script, string id = "9RUN")
    {
        var state = _tree.Dir("px", "state", XboxInstallerApp.FolderName);
        var payloads = _tree.Dir("px", "payloads");
        Title(payloads, id);
        WriteCrumb("i.json", Crumb(state, [payloads], script, env: new() { ["XDG_STATE_HOME"] = "/px state/it's $(touch pwned)", ["RHI_TEST_MARK"] = "1" }));
        var game = Assert.Single(Detect().Games);
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        return game;
    }

    [Fact]
    public async Task Play_ExecutesTheEntryPointAsAnArgumentVector_WithItsEnvironment_AndNoShell()
    {
        var (script, output) = FakeLauncher();
        var game = BreadcrumbCard(script);
        var launcher = new LinuxGameLauncher(TimeSpan.FromSeconds(10), _tree.Dir("logs"));

        var psi = LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions());
        Assert.Equal(script, psi.FileName);
        Assert.Equal(["--play", "9RUN"], psi.ArgumentList);
        await launcher.LaunchAsync(game);

        Assert.Equal(["--play", "9RUN"], File.ReadAllLines(Path.Combine(output, "argv")));
        var env = File.ReadAllLines(Path.Combine(output, "env"));
        Assert.Contains("XDG_STATE_HOME=/px state/it's $(touch pwned)", env);
        Assert.Contains("RHI_TEST_MARK=1", env);
        // Started by RHI's own process, not through a shell; nothing in the values was run.
        Assert.DoesNotContain(File.ReadAllText(Path.Combine(output, "parent")).Trim(), new[] { "sh", "bash", "dash", "zsh" });
        Assert.False(File.Exists(Path.Combine(output, "pwned")));
        Assert.Empty(Directory.GetFiles(_tree.Path_("logs")));
    }

    [Fact]
    public async Task ShowInInstallerApp_ExecutesTheShowEntryPoint_FromTheCardMenu()
    {
        var (script, output) = FakeLauncher();
        var game = BreadcrumbCard(script, "9SHOW");
        var launcher = new LinuxGameLauncher(TimeSpan.FromSeconds(10), _tree.Dir("logs"));
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Mostrar no app que o instalou", RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppLabel(game));
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
        Assert.Equal("Show in the app that installed it", RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppLabel(game));
        Assert.Null(RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppLabel(new DetectedGame { Name = "Steam" }));

        Assert.Null(await RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppAsync(game, launcher));

        Assert.Equal(["--show", "9SHOW"], File.ReadAllLines(Path.Combine(output, "argv")));
        Assert.Contains("RHI_TEST_MARK=1", File.ReadAllLines(Path.Combine(output, "env")));
    }

    [Fact]
    public async Task AnEntryPointThatFailsAtOnce_IsReported()
    {
        var (script, _) = FakeLauncher(exitCode: 3);
        var game = BreadcrumbCard(script, "9FAIL");
        var launcher = new LinuxGameLauncher(TimeSpan.FromSeconds(10), _tree.Dir("logs"));

        var error = await Assert.ThrowsAsync<GameLaunchFailedException>(() => launcher.LaunchAsync(game));
        var shown = await RHI.Desktop.ViewModels.XboxCardActions.ShowInOwningAppAsync(game, launcher);

        Assert.Equal(3, error.ExitCode);
        Assert.Equal(UiLocalizationService.Format("Scan.ShowInOwningAppExited", UiLocalizationService.Text("Scan.InstallerAppName"), 3), shown);
    }

    [Fact]
    public void EditGame_ShowsTheEntryPointAsTheDetectedLaunch_AUserCommandRunsThroughTheShell_AndResetBringsItBack()
    {
        var (script, _) = FakeLauncher();
        var game = BreadcrumbCard(script, "9EDIT");
        var entry = game.XboxPackage!.OpenCommand;

        Assert.Equal(new DetectedLaunch(entry, null), game.DetectedLaunch);
        Assert.NotNull(LinuxGameLauncher.XboxEntryPoint(game));

        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { LaunchCommand = "/my/wrapper.sh" });
        Assert.Equal("/my/wrapper.sh", game.LaunchCommand);
        Assert.Null(LinuxGameLauncher.XboxEntryPoint(game));
        Assert.Equal("/bin/sh", LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).FileName);
        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(game));

        // The user types the entry point's line back as their own command: it is theirs (a shell runs it), and since its
        // program is the card's installer-app launcher it is still the installer app's hand-off (scan re-review minor 1).
        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { LaunchCommand = entry });
        Assert.Null(LinuxGameLauncher.XboxEntryPoint(game));
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));

        LinuxGameIdentityService.ApplyDetectedIdentity(game, null);
        Assert.Equal(entry, game.LaunchCommand);
        Assert.Equal(LaunchHandoff.Xbox, LaunchHandoffClassifier.Classify(game));
        Assert.Equal(script, LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).FileName);
    }

    [Fact]
    public async Task PrefixesUnderAKnownInstancesEnvironmentRoot_AreInstallerApps_WithoutItsTemplatesFolder()
    {
        // An environment root before the installer app's first template: no .templates, so only the breadcrumb tells.
        var home = _tree.Dir("home");
        var envs = _tree.Dir("home", "Games", "px-envs");
        var prefix = _tree.Dir("home", "Games", "px-envs", "default", "9P", "prefix");
        _tree.File("", "home", "Games", "px-envs", "default", "9P", "prefix", "system.reg");
        _tree.File("{}", "home", "Games", "px-envs", "default", "9P", XboxInstallerInstance.EnvironmentFile);
        _tree.Exe("home", "Games", "px-envs", "default", "9P", "prefix", "drive_c", "Program Files", "Tool", "Tool.exe");
        var settingsEnvs = _tree.Dir("home", "Games", "settings-envs");
        var settingsPrefix = _tree.Dir("home", "Games", "settings-envs", "default", "9Q", "prefix");
        _tree.File("", "home", "Games", "settings-envs", "default", "9Q", "prefix", "system.reg");
        _tree.File("{}", "home", "Games", "settings-envs", "default", "9Q", XboxInstallerInstance.EnvironmentFile);
        _tree.Exe("home", "Games", "settings-envs", "default", "9Q", "prefix", "drive_c", "Program Files", "Other", "Other.exe");
        _tree.File("", "home", "Games", "standalone", "system.reg");
        _tree.Exe("home", "Games", "standalone", "drive_c", "Program Files", "Free", "Free.exe");
        _tree.Dir("user-px", "state", XboxInstallerApp.FolderName);
        _tree.File(JsonSerializer.Serialize(new { payloadRoot = "/p", environmentRoot = settingsEnvs }), "user-px", "state", XboxInstallerApp.FolderName, "settings.json");
        WriteCrumb("i.json", Crumb(_tree.Dir("px", "state"), [_tree.Dir("px", "payloads")], null, envs));
        // A newer-schema file is the Xbox detector's scan warning; the Wine detector does not report it a second time.
        WriteCrumb("newer.json", Crumb(_tree.Dir("px2", "state"), [_tree.Dir("px2", "payloads")], null, schema: 2));

        var roots = XboxInstallerInstance.KnownEnvironmentRoots(_crumbs, [_tree.Path_("user-px")]);
        var owned = LauncherOwnedPrefixes.Load(home, [], installerAppEnvironments: roots);
        var (listed, warnings) = await DetectorDiagnostics.CollectAsync(() =>
            new WinePrefixDetector(home, [_tree.Dir("lutris")], _tree.Path_("data"), [_tree.Path_("user-px")]).DetectAsync());

        Assert.True(owned.Owns(prefix));
        Assert.True(owned.Owns(settingsPrefix)); // the user's folder names the instance folder itself
        Assert.Equal(["Free"], listed.Select(g => g.Name));
        Assert.Empty(warnings);
    }

    // Scan re-review nit 3: a breadcrumb naming a broad environment root (the whole ~/Games) hides only the installer app's own
    // prefixes there (its {app}.json beside the prefix, or a template), never the user's other Wine prefixes.
    [Fact]
    public async Task ABroadEnvironmentRoot_HidesOnlyPrefixesInstallerAppMarked()
    {
        var home = _tree.Dir("home");
        var games = _tree.Dir("home", "Games");
        var title = _tree.Dir("home", "Games", "default", "9P", "prefix");
        _tree.File("", "home", "Games", "default", "9P", "prefix", "system.reg");
        _tree.File("{}", "home", "Games", "default", "9P", XboxInstallerInstance.EnvironmentFile);
        _tree.Exe("home", "Games", "default", "9P", "prefix", "drive_c", "Program Files", "Tool", "Tool.exe");
        var template = _tree.Dir("home", "Games", ".templates", "rt1", "prefix");
        var cachedTemplate = _tree.Dir("home", "Games", "default", "9P", "cache", "rt1", "prefix");
        var own = _tree.Dir("home", "Games", "standalone");
        _tree.File("", "home", "Games", "standalone", "system.reg");
        _tree.Exe("home", "Games", "standalone", "drive_c", "Program Files", "Free", "Free.exe");
        WriteCrumb("broad.json", Crumb(_tree.Dir("px", "state"), [_tree.Dir("px", "payloads")], null, games));

        var owned = LauncherOwnedPrefixes.Load(home, [], installerAppEnvironments: XboxInstallerInstance.KnownEnvironmentRoots(_crumbs, []));
        var listed = await new WinePrefixDetector(home, [_tree.Dir("lutris")], _tree.Path_("data"), []).DetectAsync();

        Assert.True(owned.Owns(title));
        Assert.True(owned.Owns(template));
        Assert.True(owned.Owns(cachedTemplate));
        Assert.False(owned.Owns(own));
        Assert.Equal(["Free"], listed.Select(g => g.Name));
    }
}
