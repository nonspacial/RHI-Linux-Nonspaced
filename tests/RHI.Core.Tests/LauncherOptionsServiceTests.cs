using RHI.Core.Models;
using RHI.Platform.Linux.Launch.LauncherOptions;

namespace RHI.Core.Tests;

/// <summary>
/// #318: writing a game's launch arguments into Steam / Lutris / Heroic's own options on a fake home (fixtures from real
/// files): merge with the user's options, the record of what RHI added, the journal and its byte-exact copies, rollback and
/// crash recovery, the running-launcher refusal, and removal (original back, or only RHI's part).
/// </summary>
public sealed class LauncherOptionsServiceTests : IDisposable
{
    private const string Key = "game-key";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-lopt-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeProbe _probe = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "launcher-options", name));

    private string SteamRoot => Path.Combine(_root, "home", ".local", "share", "Steam");
    private string FlatpakSteamRoot => Path.Combine(_root, "home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    private string LutrisRoot => Path.Combine(_root, "home", ".local", "share", "lutris", "games");
    private string HeroicRoot => Path.Combine(_root, "home", ".config", "heroic");

    private string SteamUser(string root, string account, byte[] localconfig)
    {
        var path = Path.Combine(root, "userdata", account, "config", "localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, localconfig);
        return path;
    }

    private LauncherOptionsService Service(LauncherOptionsStore? store = null)
        => new(store ?? new LauncherOptionsStore(Path.Combine(_root, "config", "launcher-options.json")), Path.Combine(_root, "state"),
            new LauncherOptionsLocator(() => [SteamRoot, FlatpakSteamRoot], () => [LutrisRoot], () => [HeroicRoot]), _probe);

    private static DetectedGame SteamGame(int appId, bool flatpak = false) => new()
    {
        Name = "Steam Sample", Source = "Steam", SteamAppId = appId, InstallPath = "/games/steamapps/common/Sample",
        LaunchCommand = flatpak ? $"flatpak run com.valvesoftware.Steam -applaunch {appId}" : $"steam -applaunch {appId}"
    };

    private static DetectedGame LutrisGame(string slug) => new()
    {
        Name = "Lutris Sample", Source = "Lutris", InstallPath = "/games/lutris", LaunchCommand = $"lutris 'lutris:rungame/{slug}'"
    };

    private static DetectedGame HeroicGame(string app) => new()
    {
        Name = "Heroic Sample", Source = "Heroic (GOG)", InstallPath = "/games/heroic", HeroicAppName = app,
        LaunchCommand = $"xdg-open 'heroic://launch?appName={app}&runner=gog'"
    };

    // ── Steam ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Steam_MergesAfterCommand_RecordsWhatRhiAdded_AndKeepsAByteExactCopy()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var service = Service();

        var result = service.Apply(SteamGame(4000001), Key, "-dx11 \"+exec auto.cfg\"");

        Assert.Equal(LauncherOptionsOutcome.Written, result.Outcome);
        Assert.Equal("gamemoderun %command% -dx11 '+exec auto.cfg'", SteamLocalConfig.Read(File.ReadAllBytes(file), 4000001).LaunchOptions);
        var record = service.RecordFor(Key)!;
        Assert.Equal(LauncherKind.Steam, record.Launcher);
        Assert.Equal("-dx11 \"+exec auto.cfg\"", record.Arguments);
        var entry = Assert.Single(record.Files);
        Assert.Equal((file, true, "gamemoderun %command%", "-dx11 '+exec auto.cfg'"), (entry.Path, entry.OriginalPresent, entry.Original, entry.Added));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(service.BackupDirectory))));
        Assert.Empty(Directory.GetFiles(service.JournalDirectory));
    }

    [Fact]
    public void Steam_WhileRunning_IsRefused_AndNothingIsWritten()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        _probe.Running.Add(LauncherKind.Steam);
        var service = Service();

        Assert.Equal(LauncherOptionsOutcome.LauncherRunning, service.Apply(SteamGame(4000001), Key, "-dx11").Outcome);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Null(service.RecordFor(Key));
    }

    [Fact]
    public void Steam_StartedWhileRhiPrepared_IsRefused_AndNothingIsWritten()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var service = Service();
        service.BeforeFilesReplacedForTesting = () => _probe.Running.Add(LauncherKind.Steam);

        Assert.Equal(LauncherOptionsOutcome.LauncherRunning, service.Apply(SteamGame(4000001), Key, "-dx11").Outcome);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Null(service.RecordFor(Key));
        Assert.Empty(Directory.GetFiles(service.JournalDirectory));
    }

    [Fact]
    public void Remove_Unchanged_PutsTheOriginalFileBackByteForByte()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var service = Service();
        service.Apply(SteamGame(4000002), Key, "-dx11"); // an app without LaunchOptions: the key is added

        var removed = service.Remove(Key);

        Assert.Equal((LauncherOptionsOutcome.Removed, false), (removed.Outcome, removed.KeptUserChange));
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Null(service.RecordFor(Key));
    }

    [Fact]
    public void Remove_AfterTheUserChangedTheOptions_TakesOutOnlyRhisPart()
    {
        var file = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var service = Service();
        service.Apply(SteamGame(4000001), Key, "-dx11");
        // The user adds their own option in Steam afterwards.
        File.WriteAllBytes(file, SteamLocalConfig.Write(File.ReadAllBytes(file), 4000001, "mangohud gamemoderun %command% -dx11 -novid"));

        var removed = service.Remove(Key);

        Assert.True(removed.KeptUserChange);
        Assert.Equal("mangohud gamemoderun %command% -novid", SteamLocalConfig.Read(File.ReadAllBytes(file), 4000001).LaunchOptions);
    }

    [Fact]
    public void Remove_WhenTheUserTookRhisPartOutThemselves_LeavesTheOptionsAsTheyAre()
    {
        var file = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var service = Service();
        service.Apply(SteamGame(4000001), Key, "-dx11");
        File.WriteAllBytes(file, SteamLocalConfig.Write(File.ReadAllBytes(file), 4000001, "%command% -novid"));
        var before = File.ReadAllBytes(file);

        var removed = service.Remove(Key);

        Assert.Equal((LauncherOptionsOutcome.Removed, true), (removed.Outcome, removed.KeptUserChange));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Null(service.RecordFor(Key));
    }

    [Fact]
    public void Remove_WhileRunning_IsRefused_AndTheRecordStays()
    {
        var file = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var service = Service();
        service.Apply(SteamGame(4000001), Key, "-dx11");
        var written = File.ReadAllBytes(file);
        _probe.Running.Add(LauncherKind.Steam);

        Assert.Equal(LauncherOptionsOutcome.LauncherRunning, service.Remove(Key).Outcome);
        Assert.Equal(written, File.ReadAllBytes(file));
        Assert.NotNull(service.RecordFor(Key));
    }

    [Fact]
    public void NewArguments_ReplaceRhisPreviousPart_AndKeepTheOriginal()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var service = Service();
        service.Apply(SteamGame(4000001), Key, "-dx11");

        service.Apply(SteamGame(4000001), Key, "-fullscreen"); // a display mode switch, or an edit of the box

        Assert.Equal("gamemoderun %command% -fullscreen", SteamLocalConfig.Read(File.ReadAllBytes(file), 4000001).LaunchOptions);
        Assert.Equal("gamemoderun %command%", Assert.Single(service.RecordFor(Key)!.Files).Original);
        service.Remove(Key);
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void ArgumentsAlreadyInTheOptions_AreNotAddedTwice_AndRemovalLeavesThem()
    {
        var file = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        File.WriteAllBytes(file, SteamLocalConfig.Write(File.ReadAllBytes(file), 4000001, "%command% -dx11"));
        var before = File.ReadAllBytes(file);
        var service = Service();

        Assert.Equal(LauncherOptionsOutcome.Written, service.Apply(SteamGame(4000001), Key, "-dx11").Outcome);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal("", Assert.Single(service.RecordFor(Key)!.Files).Added);
        service.Remove(Key);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void AFailureBeforeTheRecord_RollsTheFileBack()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var service = Service();
        service.BeforeCommitForTesting = () => throw new IOException("disk full (test)");

        var result = service.Apply(SteamGame(4000001), Key, "-dx11");

        Assert.Equal(LauncherOptionsOutcome.Failed, result.Outcome);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Null(service.RecordFor(Key));
        Assert.Empty(Directory.GetFiles(service.JournalDirectory));
    }

    [Fact]
    public void ACrashBeforeTheRecord_IsPutBackAtTheNextStart()
    {
        var original = Fixture("steam-localconfig.vdf");
        var file = SteamUser(SteamRoot, "10000001", original);
        var crashing = Service();
        crashing.BeforeCommitForTesting = () => throw new SimulatedCrash();
        Assert.Throws<SimulatedCrash>(() => crashing.Apply(SteamGame(4000001), Key, "-dx11"));
        Assert.NotEqual(original, File.ReadAllBytes(file)); // the crash left RHI's write behind
        Assert.Single(Directory.GetFiles(crashing.JournalDirectory));

        var restored = Service().Recover();

        Assert.Equal(1, restored);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(crashing.JournalDirectory));
    }

    [Fact]
    public void Recovery_LeavesAFileTheUserChangedSince_AndACommittedWrite()
    {
        var file = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var crashing = Service();
        crashing.BeforeCommitForTesting = () => throw new SimulatedCrash();
        Assert.Throws<SimulatedCrash>(() => crashing.Apply(SteamGame(4000001), Key, "-dx11"));
        File.WriteAllBytes(file, SteamLocalConfig.Write(File.ReadAllBytes(file), 4000001, "user edit"));
        var userBytes = File.ReadAllBytes(file);

        Assert.Equal(0, Service().Recover());
        Assert.Equal(userBytes, File.ReadAllBytes(file));

        var committed = Service();
        committed.Apply(SteamGame(4000003), "other", "-a");
        var written = File.ReadAllBytes(file);
        Assert.Equal(0, Service().Recover());
        Assert.Equal(written, File.ReadAllBytes(file));
    }

    [Fact]
    public void Steam_WritesEveryAccountThatListsTheApp_ElseTheMostRecent()
    {
        var a = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var b = SteamUser(SteamRoot, "10000002", Fixture("steam-localconfig.vdf"));
        var locator = new LauncherOptionsLocator(() => [SteamRoot], () => [], () => []);
        Assert.Equal([a, b], locator.Locate(SteamGame(4000001))!.Files);

        // Neither lists 4000009: the MostRecent account (SteamID64 76561197970265730 = account 10000002).
        Assert.Null(locator.Locate(SteamGame(4000009)));
        Directory.CreateDirectory(Path.Combine(SteamRoot, "config"));
        File.WriteAllText(Path.Combine(SteamRoot, "config", "loginusers.vdf"),
            "\"users\"\n{\n\t\"76561197970265729\"\n\t{\n\t\t\"MostRecent\"\t\t\"0\"\n\t}\n\t\"76561197970265730\"\n\t{\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n");
        Assert.Equal([b], locator.Locate(SteamGame(4000009))!.Files);
    }

    [Fact]
    public void Steam_FlatpakLaunch_WritesFlatpakSteamsFile_NativeTheNativeOne()
    {
        var native = SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var flatpak = SteamUser(FlatpakSteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        var locator = new LauncherOptionsLocator(() => [SteamRoot, FlatpakSteamRoot], () => [], () => []);
        Assert.Equal([native], locator.Locate(SteamGame(4000001))!.Files);
        Assert.Equal([flatpak], locator.Locate(SteamGame(4000001, flatpak: true))!.Files);
    }

    [Fact]
    public void GamesRhiCannotWriteFor_HaveNoTarget()
    {
        SteamUser(SteamRoot, "10000001", Fixture("steam-localconfig.vdf"));
        Assert.Null(LauncherOptionsLocator.KindOf(new DetectedGame { Name = "S", Source = RHI.Platform.Linux.Detection.SteamGameDetector.ShortcutSource, SteamAppId = 4000001, LaunchCommand = "steam steam://rungameid/123" }));
        Assert.Null(LauncherOptionsLocator.KindOf(new DetectedGame { Name = "W", Source = "Wine", LaunchCommand = "wine Game.exe" }));
        Assert.Null(LauncherOptionsLocator.KindOf(new DetectedGame { Name = "P", Source = "Lutris", ParentLauncherName = "Battle.net", LaunchCommand = "lutris lutris:rungame/battlenet" }));
        var typed = SteamGame(4000001);
        typed.UserLaunchCommand = typed.LaunchCommand;
        Assert.Null(LauncherOptionsLocator.KindOf(typed));
        Assert.Equal(LauncherKind.Steam, LauncherOptionsLocator.KindOf(SteamGame(4000001)));
    }

    // ── Lutris and Heroic ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Lutris_FindsTheGamesConfigBySlug_AndRoundTrips()
    {
        Directory.CreateDirectory(LutrisRoot);
        var file = Path.Combine(LutrisRoot, "sample-racer-1763000001.yml");
        var original = Fixture("lutris-wrapped-args.yml");
        File.WriteAllBytes(file, original);
        File.WriteAllBytes(Path.Combine(LutrisRoot, "sample-racer-2-1763000002.yml"), Fixture("lutris-no-args.yml")); // another slug
        var service = Service();

        Assert.Equal(LauncherOptionsOutcome.Written, service.Apply(LutrisGame("sample-racer"), Key, "-dx11").Outcome);
        Assert.EndsWith(" -dx11", LutrisGameArgs.Read(File.ReadAllBytes(file)), StringComparison.Ordinal);
        Assert.StartsWith("-skipintro -language english", LutrisGameArgs.Read(File.ReadAllBytes(file)), StringComparison.Ordinal);

        _probe.Running.Add(LauncherKind.Lutris);
        Assert.Equal(LauncherOptionsOutcome.LauncherRunning, service.Remove(Key).Outcome);
        _probe.Running.Clear();
        Assert.Equal(LauncherOptionsOutcome.Removed, service.Remove(Key).Outcome);
        // The value comes back; Lutris' own line folding is not kept (RHI writes one quoted line).
        Assert.Equal(LutrisGameArgs.Read(original), LutrisGameArgs.Read(File.ReadAllBytes(file)));
    }

    [Fact]
    public void Lutris_TwoEntriesWithTheSameSlug_AreNotGuessedBetween()
    {
        Directory.CreateDirectory(LutrisRoot);
        File.WriteAllBytes(Path.Combine(LutrisRoot, "sky-1.yml"), Fixture("lutris-double-quoted-args.yml"));
        File.WriteAllBytes(Path.Combine(LutrisRoot, "sky-2.yml"), Fixture("lutris-double-quoted-args.yml"));
        Assert.Equal(LauncherOptionsOutcome.NotSupported, Service().Apply(LutrisGame("sky"), Key, "-dx11").Outcome);
    }

    [Fact]
    public void Heroic_AppendsToTheUsersGameArguments_AndRemovalPutsTheFileBack()
    {
        var dir = Directory.CreateDirectory(Path.Combine(HeroicRoot, "GamesConfig")).FullName;
        var file = Path.Combine(dir, "1400000001.json");
        var original = Fixture("heroic-gog-args.json");
        File.WriteAllBytes(file, original);
        var service = Service();

        _probe.Running.Add(LauncherKind.Heroic);
        Assert.Equal(LauncherOptionsOutcome.LauncherRunning, service.Apply(HeroicGame("1400000001"), Key, "-dx11").Outcome);
        _probe.Running.Clear();
        Assert.Equal(LauncherOptionsOutcome.Written, service.Apply(HeroicGame("1400000001"), Key, "-dx11").Outcome);
        Assert.Equal("-nointro -lang=pt-BR -dx11", HeroicGameArgs.Read(File.ReadAllBytes(file), "1400000001"));

        service.Remove(Key);
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Heroic_WithoutTheGamesSettingsFile_IsNotSupported()
        => Assert.Equal(LauncherOptionsOutcome.NotSupported, Service().Apply(HeroicGame("1400000001"), Key, "-dx11").Outcome);

    // ── the running probe ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_FindsLaunchersByProcessName_OrLutrisByItsScript()
    {
        var proc = Path.Combine(_root, "proc");
        void Process(int pid, string comm, string cmdline)
        {
            var dir = Directory.CreateDirectory(Path.Combine(proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
            File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline.Replace(' ', '\0'));
        }
        Process(10, "steamwebhelper", "steamwebhelper");
        Process(11, "python3", "/usr/bin/python3 /usr/bin/lutris");
        var probe = new ProcLauncherRunningProbe(proc);
        Assert.False(probe.IsRunning(LauncherKind.Steam));
        Assert.True(probe.IsRunning(LauncherKind.Lutris));
        Assert.False(probe.IsRunning(LauncherKind.Heroic));
        Process(12, "steam", "/home/u/.local/share/Steam/ubuntu12_32/steam");
        Process(13, "heroic", "/opt/Heroic/heroic");
        Assert.True(probe.IsRunning(LauncherKind.Steam));
        Assert.True(probe.IsRunning(LauncherKind.Heroic));
    }

    private sealed class SimulatedCrash : Exception;

    private sealed class FakeProbe : ILauncherRunningProbe
    {
        public HashSet<LauncherKind> Running { get; } = [];
        public bool IsRunning(LauncherKind kind) => Running.Contains(kind);
    }
}
