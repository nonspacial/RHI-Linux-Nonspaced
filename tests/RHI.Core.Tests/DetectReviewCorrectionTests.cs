using System.Runtime.Versioning;
using System.Text;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>w1/detect review findings 3, 5, 6, 7, 8, 12 and 14 (detector robustness, diagnostics, HDR family).</summary>
[SupportedOSPlatform("linux")]
public sealed class DetectReviewCorrectionTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ---- 3: one external library listed by two Steam roots (one via a symlink) is walked once ----

    [Fact]
    public void ALibraryListedByTwoSteamRoots_IsWalkedOnce()
    {
        using var tree = new DetectionTestTree();
        var native = tree.Dir("home", ".local", "share", "Steam");
        var flatpak = tree.Dir("home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        var library = tree.Dir("disk", "SteamLibrary");
        var alias = tree.Link(library, "home", "lib-alias");
        tree.File(SteamGameDetectorTests.Manifest(20, "Shared Game", "Shared"), "disk", "SteamLibrary", "steamapps", "appmanifest_20.acf");
        tree.Exe("disk", "SteamLibrary", "steamapps", "common", "Shared", "Shared.exe");
        tree.File($"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{library}\" }} }}", "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        tree.File($"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{alias}\" }} }}",
            "home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");

        var games = SteamGameDetector.DetectIn([native, flatpak], CancellationToken.None);

        var game = Assert.Single(games);
        Assert.Equal(20, game.SteamAppId);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Shared"), game.InstallPath);
    }

    // ---- 6: one bad appinfo entry loses only that app; nesting is capped ----

    private static readonly string[] Strings = ["appinfo", "common", "type", "oslist", "name"];

    private static byte[] Kv(string type, string osList)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        void Str(string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }
        w.Write((byte)0); w.Write(Array.IndexOf(Strings, "appinfo"));
        w.Write((byte)0); w.Write(Array.IndexOf(Strings, "common"));
        w.Write((byte)1); w.Write(Array.IndexOf(Strings, "type")); Str(type);
        w.Write((byte)1); w.Write(Array.IndexOf(Strings, "oslist")); Str(osList);
        w.Write((byte)8); w.Write((byte)8); w.Write((byte)8);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Version-29 appinfo.vdf whose entries carry the given raw key/value bytes.</summary>
    private static byte[] RawV29(params (int AppId, byte[] Kv)[] apps)
    {
        using var body = new MemoryStream();
        using var w = new BinaryWriter(body, Encoding.UTF8);
        foreach (var (appId, kv) in apps)
        {
            w.Write((uint)appId);
            w.Write((uint)(60 + kv.Length));
            w.Write(new byte[60]); // infoState … binary SHA-1
            w.Write(kv);
        }
        w.Write(0u);
        w.Flush();
        using var file = new MemoryStream();
        using var f = new BinaryWriter(file, Encoding.UTF8);
        f.Write(0x07564429u);
        f.Write(1u);
        f.Write((long)(16 + body.Length));
        f.Write(body.ToArray());
        f.Write((uint)Strings.Length);
        foreach (var s in Strings) { f.Write(Encoding.UTF8.GetBytes(s)); f.Write((byte)0); }
        f.Flush();
        return file.ToArray();
    }

    [Fact]
    public async Task AppInfo_ABadEntry_DoesNotStopTheAppsAfterIt_AndIsReported()
    {
        using var tree = new DetectionTestTree();
        var bad = Kv("Tool", "windows");
        bad[0] = 0x09; // unknown value type
        var path = tree.Bytes(RawV29((1, Kv("Game", "windows")), (2, bad), (3, Kv("Tool", "linux"))), "appinfo.vdf");

        var (read, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(SteamAppInfo.Read(path, new HashSet<int> { 1, 2, 3 })));

        Assert.Equal([1, 3], read.Keys.Order());
        Assert.Equal(new SteamAppInfo.Entry("Tool", "linux"), read[3]);
        Assert.Contains(warnings, w => w.Contains("entry 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AppInfo_NestingDeeperThanTheCap_IsRefusedForThatEntryOnly()
    {
        using var tree = new DetectionTestTree();
        var deep = new List<byte>();
        for (var i = 0; i < 100; i++) { deep.Add(0x00); deep.AddRange(BitConverter.GetBytes(Array.IndexOf(Strings, "appinfo"))); }
        for (var i = 0; i < 101; i++) deep.Add(0x08);
        var path = tree.Bytes(RawV29((1, deep.ToArray()), (2, Kv("Game", "linux"))), "appinfo.vdf");

        var read = SteamAppInfo.Read(path, new HashSet<int> { 1, 2 });

        Assert.Equal([2], read.Keys);
    }

    // ---- 8: Steam's per-app GE-Proton mapping makes native Wayland HDR use the GE contract ----

    [Fact]
    public void SteamGameMappedToGeProton_GetsTheGeHdrContract_StockProtonStaysBlocked()
    {
        using var tree = new DetectionTestTree();
        var steam = tree.Dir("Steam");
        foreach (var (id, dir) in new[] { (100, "GeGame"), (200, "StockGame") })
        {
            tree.File(SteamGameDetectorTests.Manifest(id, dir, dir), "Steam", "steamapps", $"appmanifest_{id}.acf");
            tree.Exe("Steam", "steamapps", "common", dir, dir + ".exe");
            tree.Dir("Steam", "steamapps", "compatdata", id.ToString(), "pfx", "drive_c");
        }
        tree.File("""
                  "InstallConfigStore" { "Software" { "Valve" { "Steam" { "CompatToolMapping" {
                    "100" { "name" "GE-Proton11-7-x86_64" }
                    "0" { "name" "proton_9" }
                  } } } } }
                  """, "Steam", "config", "config.vdf");
        var hdr = new LinuxHdrLaunchProfileService(
            environment: name => name switch { "XDG_SESSION_TYPE" => "wayland", "WAYLAND_DISPLAY" => "wayland-0", _ => null },
            commandExists: _ => false,
            nvidiaVersion: () => null);
        var profile = new LinuxHdrGameProfile { Mode = LinuxHdrModes.NativeWayland };

        var games = SteamGameDetector.DetectIn([steam], CancellationToken.None).ToDictionary(g => g.SteamAppId!.Value);

        Assert.Equal("GE-Proton11-7-x86_64", games[100].CompatibilityTool);
        Assert.Equal("GE", LinuxHdrLaunchProfileService.RunnerFamily(games[100]));
        var ge = hdr.BuildPlan(games[100], profile);
        Assert.True(ge.CanApply);
        Assert.Equal("1", ge.Environment["PROTON_ENABLE_HDR"]);
        Assert.Equal("1", ge.Environment["PROTON_ENABLE_WAYLAND"]);

        // No per-app mapping and no appinfo platform record: Steam's default tool is not assumed.
        Assert.Null(games[200].CompatibilityTool);
        Assert.Equal("Proton", games[200].Runner);
        Assert.Equal("Proton", LinuxHdrLaunchProfileService.RunnerFamily(games[200]));
        Assert.False(hdr.BuildPlan(games[200], profile).CanApply);
    }

    // ---- 7 and 5: pga.db in WAL mode with pending changes, and damaged files, fall back with a warning ----

    private static string WalDatabase(DetectionTestTree tree)
    {
        var bytes = File.ReadAllBytes(Fixture("lutris-pga.db"));
        bytes[18] = 2;
        bytes[19] = 2;
        return tree.Bytes(bytes, "lutris", "pga.db");
    }

    [Fact]
    public async Task PgaInWalModeWithPendingChanges_IsNotTrusted()
    {
        using var tree = new DetectionTestTree();
        var db = WalDatabase(tree);

        Assert.Equal(304, SqliteTableReader.ReadTable(db, "games").Count); // no -wal file
        tree.Bytes([], "lutris", "pga.db-wal");
        Assert.Equal(304, SqliteTableReader.ReadTable(db, "games").Count); // checkpointed, empty -wal

        tree.Bytes(new byte[4096], "lutris", "pga.db-wal");
        var (rows, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(SqliteTableReader.ReadTable(db, "games")));
        Assert.Empty(rows);
        Assert.Contains(warnings, w => w.Contains(db, StringComparison.Ordinal) && w.Contains("WAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DamagedPga_IsReported_NotSilentlyIgnored()
    {
        using var tree = new DetectionTestTree();
        var garbage = tree.Bytes([.. "SQLite format 3\0"u8, .. new byte[600]], "bad.db");

        var (rows, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(SqliteTableReader.ReadTable(garbage, "games")));

        Assert.Empty(rows);
        Assert.Contains(warnings, w => w.Contains(garbage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableSteamManifest_IsReported()
    {
        using var tree = new DetectionTestTree();
        var steam = tree.Dir("Steam");
        // An unreadable manifest: reading it throws, and the app is skipped with a warning.
        var manifest = tree.File(SteamGameDetectorTests.Manifest(7, "Locked", "Locked"), "Steam", "steamapps", "appmanifest_7.acf");
        File.SetUnixFileMode(manifest, UnixFileMode.None);

        var (games, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(SteamGameDetector.DetectIn([steam], CancellationToken.None)));

        Assert.Empty(games);
        Assert.Contains(warnings, w => w.Contains("appmanifest_7.acf", StringComparison.Ordinal));
    }

    // ---- 12: the Wine detector's owned-prefix list resolves $GAMEDIR from pga.db in a custom data home ----

    [Fact]
    public void OwnedPrefixes_ReadPgaFromTheDataHome()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var configs = tree.Dir("home", ".config", "lutris", "games");
        var dataHome = tree.Dir("xdg-data");
        tree.Dir("xdg-data", "lutris");
        File.Copy(Fixture("lutris-pga.db"), tree.Path_("xdg-data", "lutris", "pga.db"));
        tree.File("game:\n  exe: drive_c/Battle.net/Battle.net Launcher.exe\n  prefix: $GAMEDIR\n", "home", ".config", "lutris", "games", "battlenet-1774722738.yml");

        Assert.True(LauncherOwnedPrefixes.Load(home, [configs], dataHome).Owns("/games/battlenet"));
        Assert.False(LauncherOwnedPrefixes.Load(home, [configs]).Owns("/games/battlenet"));
    }

    // ---- 14: folder symlinks in the custom shader folder are named, not silently skipped ----

    [Fact]
    public void CustomShaderFolder_NamesTheFolderLinksItDoesNotDeploy()
    {
        using var tree = new DetectionTestTree();
        var custom = tree.Dir("Custom", "Shaders");
        tree.File("// fx", "Custom", "Shaders", "Shaders", "Own.fx");
        var shared = tree.Dir("shared-collection");
        tree.File("// fx", "shared-collection", "Shared.fx");
        var link = tree.Link(shared, "Custom", "Shaders", "Shaders", "Shared");
        File.CreateSymbolicLink(tree.Path_("Custom", "Shaders", "Shaders", "Linked.fx"), Path.Combine(shared, "Shared.fx"));

        Assert.Equal([link], ReShadeShaderPackService.LinkedFoldersNotDeployed(custom));
        Assert.Empty(ReShadeShaderPackService.LinkedFoldersNotDeployed(tree.Path_("missing")));
    }

    [Fact]
    public async Task CustomShaderFolder_LinkWarning_ReachesTheAppLog()
    {
        // Trace output is invisible in Release; the warning must be in rhi.log (integration w1 detect review 5).
        using var tree = new DetectionTestTree();
        var custom = tree.Dir("Custom", "Shaders");
        var shared = tree.Dir("shared-collection");
        tree.File("// fx", "shared-collection", "Shared.fx");
        var link = tree.Link(shared, "Custom", "Shaders", "Shaders", "Shared");
        using var http = new HttpClient();
        var service = new ReShadeShaderPackService(http, tree.Dir("cache"), customRoot: custom);
        var log = new AppLog(tree.Path_("state", "rhi.log"));

        using (AppLog.UseInThisFlow(log))
            await service.ListPackFilesAsync(service.AvailablePacks.Single(p => p.IsCustom).Id);

        var text = await File.ReadAllTextAsync(log.FilePath);
        Assert.Contains("[shaders]", text);
        Assert.Contains(link, text);
    }

    [Fact]
    public void StateHome_FollowsXdgStateHome()
        => Assert.Equal(Environment.GetEnvironmentVariable("XDG_STATE_HOME"), LinuxPaths.StateHome);
}
