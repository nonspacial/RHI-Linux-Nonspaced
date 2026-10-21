using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #201 (upstream AuxInstallService.GacSymlink, manifest gacSymlinkGames): ReShade for an XNA game goes into the game
/// folder as d3d9.dll and, as copies, into the .NET assembly (GAC) folder of the game's own prefix, with ReShade.ini's
/// [INSTALL] BasePath pointing at the game folder and the per-exe d3d9 override; update swaps both copies, removal puts
/// the prefix and the game folder back byte for byte, and anything RHI did not place there refuses the install.
/// </summary>
public sealed class XnaGacReShadeTests : IDisposable
{
    private const string GacWindows = @"C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_4.0.0.0__842cf8be1de50553";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-xna-gac-" + Guid.NewGuid().ToString("N"));

    public XnaGacReShadeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Install_PutsD3d9InTheGameAndGacFolders_WithBasePath_AndTheExesOverride()
    {
        var fx = Fixture("install");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains(XnaGacReShade.ModeSuffix, result.Message);
        var dll = Path.Combine(fx.Bin, "d3d9.dll");
        var gacDll = Path.Combine(fx.Gac, "d3d9.dll");
        Assert.Equal(0x014c, PeMachine(dll)); // GAC_32: the 32-bit ReShade
        Assert.Equal(File.ReadAllBytes(dll), File.ReadAllBytes(gacDll));
        var ini = File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")); // ManageIni is forced on: the GAC copy needs BasePath
        var gacIni = File.ReadAllText(Path.Combine(fx.Gac, "ReShade.ini"));
        Assert.Contains("[INSTALL]\nBasePath=Z:" + fx.Bin.Replace('/', '\\'), gacIni);
        Assert.DoesNotContain("BasePath", ini); // only RHI's GAC copy carries it (review 2)
        Assert.Equal(ini.TrimEnd('\n'), gacIni.Replace("\n[INSTALL]\nBasePath=Z:" + fx.Bin.Replace('/', '\\'), "").TrimEnd('\n'));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "d3d9", "Terraria.exe"));
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!;
        Assert.Contains(record.Files, file => file.Path == gacDll);
        Assert.Equal(fx.Gac, record.Metadata[XnaGacReShade.FolderMetadataKey]);
        Assert.False(Directory.Exists(Path.Combine(_root, "install", "elsewhere")));
    }

    [Fact]
    public async Task Install_LinkedSharedReShadeIni_GetsNoBasePath()
    {
        // Review 2: the game folder's ReShade.ini may be a link to the user's shared ~/.config/ReShade file, which every
        // game reads; Terraria's folder must not become their base. BasePath goes into RHI's own GAC copy only.
        var fx = Fixture("linked-ini");
        var shared = Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.ConfigHome, "ReShade", Guid.NewGuid().ToString("N"), "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "[GENERAL]\nPresetPath=.\\Mine.ini\n");
        var link = Path.Combine(fx.Bin, "ReShade.ini");
        File.CreateSymbolicLink(link, shared);

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions());

        Assert.True(result.Success, result.Message);
        Assert.Equal(shared, new FileInfo(link).LinkTarget); // still the link
        Assert.DoesNotContain("BasePath", File.ReadAllText(shared));
        Assert.DoesNotContain("[INSTALL]", File.ReadAllText(shared));
        var gacIni = File.ReadAllText(Path.Combine(fx.Gac, "ReShade.ini"));
        Assert.Contains("[INSTALL]\nBasePath=Z:" + fx.Bin.Replace('/', '\\'), gacIni);
        Assert.Contains("PresetPath=.\\Mine.ini", gacIni);
        Assert.Null(new FileInfo(Path.Combine(fx.Gac, "ReShade.ini")).LinkTarget); // RHI's copy, not a link
    }

    [Fact]
    public async Task Removal_PutsThePrefixAndTheGameFolderBackByteForByte()
    {
        var fx = Fixture("remove");
        var before = CancelAtWritePhase.Snapshot(fx.Bin, fx.Prefix);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions())).Success);

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        // Every file as before, user.reg and system.reg byte for byte; only the registry editor's own bookkeeping beside
        // user.reg (its lock-file hash and the backup it keeps of a hive RHI did not write) is new.
        var after = CancelAtWritePhase.Snapshot(fx.Bin, fx.Prefix);
        Assert.Equal(before, after.Where(entry => !entry.Key.StartsWith(fx.UserReg + ".rhi-", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value));
        Assert.Equal(before[fx.UserReg], after[fx.UserReg]);
        Assert.Empty(Directory.GetFileSystemEntries(fx.Gac));
    }

    [Fact]
    public async Task Update_SwapsBothCopies_AndKeepsAGacIniTheUserChanged()
    {
        var fx = Fixture("update");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions())).Success);
        var first = File.ReadAllBytes(Path.Combine(fx.Gac, "d3d9.dll"));
        var gacIni = Path.Combine(fx.Gac, "ReShade.ini");
        File.AppendAllText(gacIni, "\n[MINE]\nKeepMe=1\n"); // ReShade (or the user) wrote the GAC copy

        fx.Handler.Version = "6.9.1";
        var updated = await fx.Service.UpdateAsync(fx.Game);

        Assert.True(updated.Success, updated.Message);
        var gacDll = File.ReadAllBytes(Path.Combine(fx.Gac, "d3d9.dll"));
        Assert.NotEqual(first, gacDll);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fx.Bin, "d3d9.dll")), gacDll);
        Assert.Contains("KeepMe=1", File.ReadAllText(gacIni));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(Path.Combine(fx.Gac, "d3d9.dll")));
        Assert.Contains("KeepMe=1", File.ReadAllText(gacIni)); // never discard what someone else wrote
    }

    public static TheoryData<string> Refusals => new()
    {
        "foreign-dll", "foreign-ini", "gac-missing", "no-prefix", "climbs-out", "other-drive", "symlinked-folder", "wine-running", "native-linux"
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refused_NothingChanges(string scenario)
    {
        var fx = Fixture("refused-" + scenario);
        var game = fx.Game;
        var service = fx.Service;
        string expected;
        switch (scenario)
        {
            case "foreign-dll":
                File.WriteAllBytes(Path.Combine(fx.Gac, "D3D9.dll"), [0x4D, 0x5A, 9]);
                expected = XnaGacReShade.ForeignFilePrefix;
                break;
            case "foreign-ini":
                File.WriteAllText(Path.Combine(fx.Gac, "ReShade.ini"), "[GENERAL]\n");
                expected = XnaGacReShade.ForeignFilePrefix;
                break;
            case "gac-missing":
                Directory.Delete(fx.Gac);
                expected = XnaGacReShade.FolderMissingPrefix;
                break;
            case "no-prefix":
                game = Copy(fx.Game, prefix: null);
                expected = XnaGacReShade.PrefixUnknownMessage;
                break;
            case "climbs-out":
                game = Copy(fx.Game, gac: @"C:\Windows\..\..\..\elsewhere");
                expected = XnaGacReShade.OutsidePrefixPrefix;
                break;
            case "other-drive":
                // A drive mapped outside the prefix (d: -> a folder beside it): its folder is never written.
                var drive = Directory.CreateDirectory(Path.Combine(_root, scenario, "elsewhere", "assembly")).FullName;
                Directory.CreateDirectory(Path.Combine(fx.Prefix, "dosdevices"));
                File.CreateSymbolicLink(Path.Combine(fx.Prefix, "dosdevices", "d:"), Path.GetDirectoryName(drive)!);
                game = Copy(fx.Game, gac: @"D:\assembly");
                expected = XnaGacReShade.OutsidePrefixPrefix;
                break;
            case "symlinked-folder":
                // A folder on the way that is a link out of the prefix: never followed into another folder.
                var outside = Directory.CreateDirectory(Path.Combine(_root, scenario, "elsewhere", "GAC_32")).FullName;
                var assembly = Path.Combine(fx.Prefix, "drive_c", "windows", "Microsoft.NET", "assembly");
                Directory.Delete(Path.Combine(assembly, "GAC_32"), recursive: true);
                Directory.CreateSymbolicLink(Path.Combine(assembly, "GAC_32"), outside);
                Directory.CreateDirectory(Path.Combine(outside, "Microsoft.Xna.Framework.Graphics", "v4.0_4.0.0.0__842cf8be1de50553"));
                expected = XnaGacReShade.OutsidePrefixPrefix;
                break;
            case "wine-running":
                service = fx.NewService(new WineDllOverrideService(new WineRegistryEditor(_ => true)));
                expected = new WineDllOverrideService(new WineRegistryEditor(_ => true)).EditRefusal(fx.Prefix)!;
                break;
            default: // native-linux
                game = Copy(fx.Game, runner: "Native Linux");
                expected = XnaGacReShade.NativeLinuxNote;
                break;
        }
        var before = CancelAtWritePhase.Snapshot(fx.Bin, fx.Prefix, Path.Combine(_root, scenario));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions());

        Assert.False(result.Success);
        Assert.StartsWith(expected, result.Message);
        CancelAtWritePhase.AssertUnchanged(before, fx.Bin, fx.Prefix, Path.Combine(_root, scenario));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task CancelAfterTheFirstWrite_TheInstallRunsToCompletion()
    {
        var fx = Fixture("cancel");

        var run = await CancelAtWritePhase.RunAsync(ct => fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions(), ct), [fx.Bin, fx.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.True(File.Exists(Path.Combine(fx.Gac, "d3d9.dll")));
        Assert.True(File.Exists(Path.Combine(fx.Gac, "ReShade.ini")));
    }

    [Fact]
    public async Task CrashBeforeTheRecord_RecoveryTakesBothCopiesBack()
    {
        var fx = Fixture("crash");
        var before = CancelAtWritePhase.Snapshot(fx.Bin, fx.Gac);
        using var death = new SimulatedDeath(fx.Journal);
        fx.Records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions()));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fx.Gac, "d3d9.dll")));
        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);

        Assert.Equal(1, report!.RolledBack);
        CancelAtWritePhase.AssertUnchanged(before, fx.Bin, fx.Gac);
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "d3d9", "Terraria.exe"));
    }

    [Fact]
    public void Folder_MapsUnderTheGamesOwnPrefix_CaseInsensitively_AndTheGameFolderAsTheGameSeesIt()
    {
        var fx = Fixture("map", dosdevices: true);
        var (folder, refusal) = XnaGacReShade.ResolveFolder(fx.Game);

        Assert.Null(refusal);
        Assert.Equal(fx.Gac, folder); // "Windows" in the manifest, "windows" in the prefix
        // The drive whose link covers the longest part of the path (d: -> the library root here, z: -> / always).
        Assert.Equal(@"D:\steamapps\common\Terraria", XnaGacReShade.WindowsPathOf(fx.Prefix, fx.Bin));
        Assert.Equal("Z:" + _root.Replace('/', '\\'), XnaGacReShade.WindowsPathOf(fx.Prefix, _root));
        Assert.Equal(PeArchitecture.X86, XnaGacReShade.ArchitectureOf(fx.Game));
        Assert.Null(XnaGacReShade.OfferRefusal(fx.Game));
        Assert.Equal(XnaGacReShade.NativeLinuxNote, XnaGacReShade.OfferRefusal(Copy(fx.Game, runner: "Native Linux")));
    }

    [Fact]
    public async Task Manifest_gacSymlinkGames_IsBoundToTheListedGameOnly()
    {
        const string manifest = """
            { "version": 3, "gacSymlinkGames": { "Terraria": "C:\\Windows\\Microsoft.NET\\assembly\\GAC_32\\Microsoft.Xna.Framework.Graphics\\v4.0_4.0.0.0__842cf8be1de50553" } }
            """;
        using var http = new HttpClient(new ManifestHandler(manifest));
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, "https://raw.githubusercontent.com/rhi-tests/rhi/main/manifest.json"));
        var terraria = Directory.CreateDirectory(Path.Combine(_root, "policy", "Terraria")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "policy", "Other")).FullName;

        var applied = await policy.ApplyAsync([
            new DetectedGame { Name = "Terraria", InstallPath = terraria, Source = "Steam" },
            new DetectedGame { Name = "Other", InstallPath = other, Source = "Steam" }]);

        Assert.Equal(GacWindows, applied.Single(g => g.Name == "Terraria").ManifestXnaGacFolder);
        Assert.Null(applied.Single(g => g.Name == "Other").ManifestXnaGacFolder);
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ushort PeMachine(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pe = BitConverter.ToInt32(bytes, 0x3C);
        return BitConverter.ToUInt16(bytes, pe + 4);
    }

    private static DetectedGame Copy(DetectedGame game, string? prefix = "keep", string? gac = null, string? runner = null) => new()
    {
        Name = game.Name, InstallPath = game.InstallPath, BinaryDirectory = game.BinaryDirectory, ExecutablePath = game.ExecutablePath,
        PrefixPath = prefix == "keep" ? game.PrefixPath : prefix, Runner = runner ?? game.Runner, GraphicsApi = game.GraphicsApi,
        Source = game.Source, ManifestXnaGacFolder = gac ?? game.ManifestXnaGacFolder
    };

    private sealed class XnaFixture
    {
        public required DetectedGame Game { get; init; }
        public required string Bin { get; init; }
        public required string Prefix { get; init; }
        public required string Gac { get; init; }
        public required string JournalDir { get; init; }
        public required string LedgerPath { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required VersionHandler Handler { get; init; }
        public required ReShadeDeploymentService Service { get; init; }
        public required Func<WineDllOverrideService, ReShadeDeploymentService> NewService { get; init; }
        public string UserReg => Path.Combine(Prefix, "user.reg");
        public string Registry() => File.ReadAllText(UserReg);
    }

    // dosdevices links only where asked: z: -> / would make a recursive snapshot of the prefix walk the whole disk.
    private XnaFixture Fixture(string name, bool dosdevices = false)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Terraria")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "steamapps", "compatdata", "105600", "pfx")).FullName;
        var gac = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows", "Microsoft.NET", "assembly", "GAC_32",
            "Microsoft.Xna.Framework.Graphics", "v4.0_4.0.0.0__842cf8be1de50553")).FullName;
        if (dosdevices)
        {
            Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
            File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "c:"), "../drive_c");
            File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
            File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "d:"), root);
        }
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(bin, "Terraria.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x014c);
        var game = new DetectedGame
        {
            Name = "Terraria", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", GraphicsApi = GraphicsApi.DirectX9, Source = "Steam", SteamAppId = 105600, ManifestXnaGacFolder = GacWindows
        };
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var ledger = Path.Combine(state, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(root, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        var handler = new VersionHandler();
        var http = new HttpClient(handler);
        var cache = Path.Combine(root, "cache");
        var templates = new UserIniTemplates(Path.Combine(state, "inis"));
        ReShadeDeploymentService Build(WineDllOverrideService wine)
        {
            var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), records, wine,
                new CountingExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
                journal: journal, dllNames: new RHI.Platform.Linux.Paths.DllNamePreferenceStore(Path.Combine(state, "dll-names.json")));
            service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);
            return service;
        }
        return new XnaFixture
        {
            Game = game, Bin = bin, Prefix = prefix, Gac = gac, JournalDir = journalDir, LedgerPath = ledger, Records = records,
            Journal = journal, Handler = handler, Service = Build(overrides), NewService = Build
        };
    }

    internal sealed class VersionHandler : HttpMessageHandler
    {
        public string Version { get; set; } = "6.9.0";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "reshade.me" && uri.AbsolutePath == "/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent($"<a href=\"/downloads/ReShade_Setup_{Version}_Addon.exe\">Download</a>") });
            var setup = new byte[600_000];
            setup[0] = (byte)'M';
            setup[1] = (byte)'Z';
            Encoding.ASCII.GetBytes(Version).CopyTo(setup, 4096);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) });
        }
    }

    // A stand-in extractor whose DLLs carry the setup's version, so an update brings other bytes.
    private sealed class CountingExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var version = File.ReadAllBytes(setupExe).Skip(4096).Take(16).ToArray();
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664, version);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c, version);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine, byte[] version)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
            stream.Position = 2048;
            stream.Write(version);
        }
    }

    private sealed class ManifestHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
