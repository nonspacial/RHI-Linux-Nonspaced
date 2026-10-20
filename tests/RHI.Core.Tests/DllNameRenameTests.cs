using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #196 (upstream DllOverrideService / Reset DLL Names): an installed ReShade proxy renamed in place in one journaled
/// operation — the file, its per-exe Wine override, the saved choice and the record move together, every refusal and
/// every failure leaves the game, the prefix and RHI's files byte for byte as they were, and Reset DLL names goes back to
/// the automatic name the same way.
/// </summary>
public sealed class DllNameRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dll-rename-" + Guid.NewGuid().ToString("N"));

    public DllNameRenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Rename_MovesFileOverrideChoiceAndRecordTogether_BackToAutomatic_AndRemovalLeavesUserRegAsBefore()
    {
        var fx = Fixture("round-trip");
        var userRegBefore = File.ReadAllBytes(fx.UserReg);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var dxgi = Path.Combine(fx.Bin, "dxgi.dll");
        var bytes = File.ReadAllBytes(dxgi);

        var renamed = await fx.Service.RenameProxyAsync(fx.Game, "winmm.dll");

        Assert.True(renamed.Success, renamed.Message);
        Assert.StartsWith(ProxyRename.RenamedPrefix, renamed.Message);
        var winmm = Path.Combine(fx.Bin, "winmm.dll");
        Assert.False(File.Exists(dxgi));
        Assert.Equal(bytes, File.ReadAllBytes(winmm));
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(winmm, ProxyPayloadLocator.ReShadePayload(record)!.Path);
        Assert.Equal(FileHash.Sha256Hex(winmm), ProxyPayloadLocator.ReShadePayload(record)!.Sha256, ignoreCase: true);
        Assert.Equal("winmm", record.WineOverride!.DllName);
        Assert.True(record.Metadata.ContainsKey(LinuxDeploymentRecovery.OperationIdMetadataKey)); // stamped: the commit point
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));
        Assert.Equal("winmm.dll", fx.Names.GetReShade(fx.Game));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));

        var back = await fx.Service.RenameProxyAsync(fx.Game, null);

        Assert.True(back.Success, back.Message);
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(winmm));
        Assert.Null(fx.Names.GetReShade(fx.Game));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));

        Assert.True((await fx.Service.RenameProxyAsync(fx.Game, "version.dll")).Success);
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(userRegBefore, File.ReadAllBytes(fx.UserReg));
        Assert.Equal([Path.Combine(fx.Bin, "Game.exe")], Directory.GetFiles(fx.Bin));
    }

    [Fact]
    public async Task Rename_LeavesReShadeIniBesideTheDll()
    {
        var fx = Fixture("ini-stays");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var ini = Path.Combine(fx.Bin, "ReShade.ini");
        var iniBytes = File.ReadAllBytes(ini);

        Assert.True((await fx.Service.RenameProxyAsync(fx.Game, "d3d11.dll")).Success);

        Assert.Equal(iniBytes, File.ReadAllBytes(ini));
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!;
        Assert.Contains(record.Files, file => file.Path == ini);
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(ini)); // still RHI's own file: removal takes it as before
    }

    public static TheoryData<string> Refusals => new()
    {
        "foreign-file", "other-record", "payload-changed", "wine-running", "dx9-translation", "optiscaler-chain", "not-offered", "xbox-busy"
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refused_LeavesEverythingByteForByte(string scenario)
    {
        var fx = Fixture("refused-" + scenario);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        fx.Names.SetReShade(fx.Game, "dxgi.dll");
        var service = fx.Service;
        var game = fx.Game;
        var target = "winmm.dll";
        string expected;
        switch (scenario)
        {
            case "foreign-file":
                File.WriteAllBytes(Path.Combine(fx.Bin, "WinMM.dll"), [0x4D, 0x5A, 1, 2, 3]);
                expected = ProxyRename.TargetTakenPrefix;
                break;
            case "other-record":
                fx.Records.Upsert(OtherRecord(fx, ManagedComponentKind.UltimateAsiLoader, Path.Combine(fx.Bin, "winmm.dll")));
                expected = ProxyRename.TargetHeldPrefix + "winmm.dll (Ultimate ASI Loader)";
                break;
            case "payload-changed":
                File.AppendAllText(Path.Combine(fx.Bin, "dxgi.dll"), "user change");
                expected = ProxyRename.ChangedPrefix;
                break;
            case "wine-running":
                var running = new WineDllOverrideService(new WineRegistryEditor(_ => true));
                service = fx.NewService(running);
                expected = running.EditRefusal(fx.Game.PrefixPath)!; // refused before anything changes, in its own words
                break;
            case "dx9-translation":
                var luma = OtherRecord(fx, ManagedComponentKind.LumaFramework, Path.Combine(fx.Bin, "luma.addon64"));
                luma.Metadata[LumaFrameworkDeploymentService.MetaDx9Migration] = "{\"OriginalPath\":\"d3d9.dll\"}";
                fx.Records.Upsert(luma);
                expected = ReShadeDeploymentService.RenameNotInPlacePrefix + "dxgi.dll";
                break;
            case "optiscaler-chain":
                fx.Records.Upsert(OtherRecord(fx, ManagedComponentKind.OptiScaler, Path.Combine(fx.Bin, "OptiScaler.ini")));
                target = "ReShade64.dll";
                expected = ReShadeDeploymentService.RenameNotInPlacePrefix + "dxgi.dll";
                break;
            case "not-offered":
                target = "ReShade32.dll"; // a 32-bit name on a 64-bit game
                expected = ReShadeDeploymentService.RenameNameNotOfferedPrefix + "ReShade32.dll";
                break;
            default: // xbox-busy: the installer app has a job queued for the title
                var jobs = Path.Combine(_root, scenario + "-jobs.json");
                File.WriteAllText(jobs, JsonSerializer.Serialize(new { queue = new[] { new { kind = "Launch", titleId = "9RN" } } }));
                var package = new XboxPackageInfo("9RN", XboxWriteGate.InstallerApp, null, jobs);
                XboxWriteGate.Register(package, fx.Game.InstallPath);
                game = Copy(fx.Game, package);
                expected = XboxWriteGate.BusyMessage;
                break;
        }
        var before = fx.Snapshot();

        var result = scenario == "xbox-busy"
            ? await Managed(fx, service).RenameReShadeProxyAsync(game, target)
            : await service.RenameProxyAsync(game, target);

        Assert.False(result.Success);
        Assert.StartsWith(expected, result.Message);
        fx.AssertUnchanged(before);
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    [Fact]
    public async Task CrashAtTheRecordSave_RecoveryPutsTheOldNameOverrideAndChoiceBack()
    {
        var fx = Fixture("crash");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var before = fx.Snapshot();
        var registryBefore = fx.RegistryShape();
        var recordBefore = File.ReadAllText(fx.LedgerPath);

        using (var death = new SimulatedDeath(fx.Journal))
        {
            fx.Records.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => fx.Service.RenameProxyAsync(fx.Game, "winmm.dll"));
            Assert.True(died);
            Assert.True(File.Exists(Path.Combine(fx.Bin, "winmm.dll"))); // renamed, override and choice written, record not
            Assert.Equal("winmm.dll", new DllNamePreferenceStore(fx.NamesPath).GetReShade(fx.Game));

            var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);

            Assert.Equal(1, report!.RolledBack);
            fx.AssertRolledBack(before, registryBefore);
            Assert.Equal(recordBefore, File.ReadAllText(fx.LedgerPath));
            Assert.Null(new DllNamePreferenceStore(fx.NamesPath).GetReShade(fx.Game));
        }
    }

    [Fact]
    public async Task CrashAfterTheRecordSave_RecoveryKeepsTheRenameTheStampedRecordDescribes()
    {
        // The record saved before the crash carries this operation's stamp, so recovery counts the rename committed and
        // keeps the new name; the record, the file and the override agree.
        var fx = Fixture("crash-committed");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        fx.Journal.CrashAtCommitForTesting = true;

        await Assert.ThrowsAnyAsync<Exception>(() => fx.Service.RenameProxyAsync(fx.Game, "winmm.dll"));
        fx.Journal.CrashAtCommitForTesting = false;
        await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);

        var record = new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade)!;
        var winmm = Path.Combine(fx.Bin, "winmm.dll");
        Assert.Equal(winmm, ProxyPayloadLocator.ReShadePayload(record)!.Path);
        Assert.True(File.Exists(winmm));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));
        Assert.Equal("winmm.dll", new DllNamePreferenceStore(fx.NamesPath).GetReShade(fx.Game));
    }

    [Fact]
    public async Task LedgerSaveFails_TheRenameIsRolledBack()
    {
        var fx = Fixture("ledger-blocked");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var before = fx.Snapshot();
        var registryBefore = fx.RegistryShape();

        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(fx.Records, fx.LedgerPath))
            result = await fx.Service.RenameProxyAsync(fx.Game, "winmm.dll");

        Assert.False(result.Success);
        Assert.StartsWith(ProxyRename.FailedPrefix, result.Message);
        fx.AssertRolledBack(before, registryBefore);
        Assert.Equal(Path.Combine(fx.Bin, "dxgi.dll"), ProxyPayloadLocator.ReShadePayload(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade))!.Path);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var fx = Fixture("cancel");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => fx.Service.RenameProxyAsync(fx.Game, "winmm.dll", ct), fx.Watched);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.Equal("winmm", fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!.WineOverride!.DllName);
    }

    [Fact]
    public async Task CancelAfterTheFirstWrite_ThenAFailure_RollsBackCompletely()
    {
        var fx = Fixture("cancel-rollback");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);

        var registryBefore = fx.RegistryShape();
        var run = await CancelAtWritePhase.RunAsync(ct => fx.Service.RenameProxyAsync(fx.Game, "winmm.dll", ct), fx.Watched,
            atWritePhase: () => fx.Records.SaveFaultForTesting = () => new IOException("disk full"));

        Assert.False(run.Result.Success);
        fx.AssertRolledBack(run.Before, registryBefore);
    }

    [Fact]
    public async Task ResetDllNames_PutsEveryChoiceBackAndRenamesTheInstalledProxy()
    {
        var fx = Fixture("reset");
        var managed = Managed(fx, fx.Service);
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        fx.Names.SetOptiScaler(fx.Game, "version.dll");
        fx.Names.SetRtxEncore(fx.Game, "dxgi.dll"); // not a Reset DLL names matter: kept
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        Assert.True(managed.HasDllNamesToReset(fx.Game));

        var reset = await managed.ResetDllNamesAsync(fx.Game);

        Assert.True(reset.Success, reset.Message);
        Assert.StartsWith(LinuxManagedComponentService.DllNamesResetMessage, reset.Message);
        Assert.Contains("winmm.dll → dxgi.dll", reset.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.Null(fx.Names.GetReShade(fx.Game));
        Assert.Null(fx.Names.GetOptiScaler(fx.Game));
        Assert.Equal("dxgi.dll", fx.Names.GetRtxEncore(fx.Game));
        Assert.False(managed.HasDllNamesToReset(fx.Game));
    }

    [Fact]
    public async Task HasDllNamesToReset_IsFalse_WithNothingChosenAndReShadeUnderItsAutomaticName()
    {
        var fx = Fixture("reset-nothing");
        var managed = Managed(fx, fx.Service);
        Assert.False(managed.HasDllNamesToReset(fx.Game));
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        Assert.False(managed.HasDllNamesToReset(fx.Game));
    }

    [Fact]
    public async Task RenameOptions_HideTakenNames_AndARefusalLocksTheName()
    {
        var fx = Fixture("options");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        File.WriteAllBytes(Path.Combine(fx.Bin, "version.dll"), [0x4D, 0x5A]);

        var (targets, refusal) = fx.Service.RenameOptions(fx.Game, ProxyDllNames.OfferedFor(false));

        Assert.Null(refusal);
        Assert.Contains("winmm.dll", targets);
        Assert.DoesNotContain("version.dll", targets);
        Assert.DoesNotContain("dxgi.dll", targets); // its own name

        File.AppendAllText(Path.Combine(fx.Bin, "dxgi.dll"), "changed");
        (targets, refusal) = fx.Service.RenameOptions(fx.Game, ProxyDllNames.OfferedFor(false));
        Assert.Empty(targets);
        Assert.StartsWith(ProxyRename.ChangedPrefix, refusal);
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private static DetectedGame Copy(DetectedGame game, XboxPackageInfo package) => new()
    {
        Name = game.Name, InstallPath = game.InstallPath, BinaryDirectory = game.BinaryDirectory, ExecutablePath = game.ExecutablePath,
        PrefixPath = game.PrefixPath, Runner = game.Runner, GraphicsApi = game.GraphicsApi, Source = game.Source, XboxPackage = package
    };

    private static LinuxDeploymentRecord OtherRecord(RenameFixture fx, ManagedComponentKind kind, string file) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(fx.Game), GameName = fx.Game.Name, Component = kind,
        BinaryDirectory = fx.Bin, Version = "1", Files = [new LinuxDeployedFile { Path = file }]
    };

    private static LinuxManagedComponentService Managed(RenameFixture fx, ReShadeDeploymentService reshade)
        => new(fx.Records, reshade: reshade, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    private sealed class RenameFixture(DetectedGame game, string bin, string prefix, string state, string journalDir, Func<WineDllOverrideService, ReShadeDeploymentService> build)
    {
        public DetectedGame Game { get; } = game;
        public string Bin { get; } = bin;
        public string UserReg { get; } = Path.Combine(prefix, "user.reg");
        public string LedgerPath { get; } = Path.Combine(state, "records.json");
        public string NamesPath { get; } = Path.Combine(state, "dll-names.json");
        public string JournalDir { get; } = journalDir;
        public string[] Watched { get; } = [bin, prefix, state];
        public LinuxDeploymentRecordStore Records { get; set; } = null!;
        public DllNamePreferenceStore Names { get; set; } = null!;
        public DeploymentJournal Journal { get; set; } = null!;
        public ReShadeDeploymentService Service { get; set; } = null!;
        public ReShadeDeploymentService NewService(WineDllOverrideService overrides) => build(overrides);
        public string Registry() => File.ReadAllText(UserReg);
        public IReadOnlyDictionary<string, string> Snapshot() => CancelAtWritePhase.Snapshot(Watched);
        public void AssertUnchanged(IReadOnlyDictionary<string, string> before) => CancelAtWritePhase.AssertUnchanged(before, Watched);

        /// <summary>
        /// After a rollback: every game and state file byte for byte; user.reg with the same keys and values (a Wine
        /// section RHI removed and put back carries a new timestamp, and RHI's lock file records its last write).
        /// </summary>
        public void AssertRolledBack(IReadOnlyDictionary<string, string> before, string registryBefore)
        {
            var prefix = Path.GetDirectoryName(UserReg)!;
            CancelAtWritePhase.AssertUnchanged(before.Where(entry => !entry.Key.StartsWith(prefix + "/", StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value), Watched.Where(dir => dir != prefix).ToArray());
            Assert.Equal(registryBefore, RegistryShape());
        }

        /// <summary>user.reg without Wine's section timestamps.</summary>
        public string RegistryShape()
            => string.Join("\n", Registry().Split('\n').Where(line => !line.StartsWith("#time=", StringComparison.Ordinal))
                .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\[.*\]) \d+$", "$1")));
    }

    private RenameFixture Fixture(string name)
    {
        var root = Path.Combine(_root, name, "Game");
        var bin = Path.Combine(root, "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        var state = Path.Combine(_root, name, "state");
        var journalDir = Path.Combine(_root, name, "journal");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(state);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game " + name, InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, Source = "Test"
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(state, "records.json"));
        var names = new DllNamePreferenceStore(Path.Combine(state, "dll-names.json"));
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        var cache = Path.Combine(_root, name, "cache");
        var http = new HttpClient(new DllNameTests.SetupHandler());
        ReShadeDeploymentService Build(WineDllOverrideService wine) => new(http, new TransactionalFileDeploymentService(journal), records, wine,
            new StandInExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal, dllNames: names);
        return new RenameFixture(game, bin, prefix, state, journalDir, Build)
        {
            Records = records, Names = names, Journal = journal, Service = Build(overrides)
        };
    }

    private sealed class StandInExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
        }
    }
}
