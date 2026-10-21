using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// #214: an XNA game's ReShade runs from RHI's copy in the prefix's .NET assembly (GAC) folder, whose ReShade.ini names
/// the game folder as <c>[INSTALL] BasePath</c>. Repair after move re-points the records; right after its commit the
/// copy's BasePath is rewritten to the game's new folder (journaled on ReShade's record, the new hash recorded) while the
/// copy still holds RHI's last write. A copy the user changed is kept and the result line says what to set; while Wine
/// runs for the prefix the rewrite (not the repair) is refused with a plain note. A crash during the rewrite leaves the
/// old or the new file, never a mix, and the record agrees.
/// </summary>
public sealed class XnaGacIniAfterMoveTests : IDisposable
{
    private const string GacWindows = @"C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_4.0.0.0__842cf8be1de50553";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-xna-gac-move-" + Guid.NewGuid().ToString("N"));

    public XnaGacIniAfterMoveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task LibraryMoved_Repair_RewritesTheGacIniBasePath_AndRecordsItsHash()
    {
        var fx = await InstalledAsync("library");
        var before = File.ReadAllBytes(fx.GacIni);
        Assert.Equal(XnaGacReShade.WindowsPathOf(fx.Game.PrefixPath!, fx.Bin), BasePathIn(fx.GacIni));
        var moved = fx.MoveLibrary();

        var result = fx.Components().RepairAfterMove(moved.Game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1", result.Message);
        var newBase = XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!);
        Assert.Contains("lib2", newBase);
        Assert.Equal(newBase, BasePathIn(moved.GacIni));
        Assert.Equal(ReShadeIniManager.WithInstallBasePath(before, newBase), File.ReadAllBytes(moved.GacIni)); // only BasePath changed
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
        Assert.Equal(["ReShade.ini", "d3d9.dll"], Directory.GetFiles(moved.Gac).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task LibraryMoved_Repair_ThenUpdate_KeepsTheCopyRhis()
    {
        // The recorded hash is the rewritten copy's: a later update still counts the copy as RHI's and rewrites it.
        var fx = await InstalledAsync("library-update");
        var moved = fx.MoveLibrary();
        Assert.True(fx.Components().RepairAfterMove(moved.Game).Success);
        fx.Handler.Version = "6.9.1";

        var updated = await fx.Service.InstallAsync(moved.Game, new ReShadeInstallOptions());

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!), BasePathIn(moved.GacIni));
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixMovedOnItsOwn_Repair_BehavesTheSame(bool gameFolderMovedToo)
    {
        // A custom/Heroic/Lutris prefix moved apart from the game folder: the copy follows the prefix; its BasePath names
        // the game folder as the game sees it from there (unchanged when only the prefix moved).
        var fx = await InstalledAsync("prefix-" + gameFolderMovedToo);
        var before = File.ReadAllBytes(fx.GacIni);
        var moved = fx.MovePrefixApart(gameFolderMovedToo);

        var result = fx.Components().RepairAfterMove(moved.Game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1", result.Message);
        Assert.Equal(XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!), BasePathIn(moved.GacIni));
        if (!gameFolderMovedToo) Assert.Equal(before, File.ReadAllBytes(moved.GacIni)); // nothing to change
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
    }

    [Fact]
    public async Task CopyTheUserChanged_IsKept_AndTheLineSaysWhatToSet()
    {
        var fx = await InstalledAsync("changed");
        File.AppendAllText(fx.GacIni, "[OVERLAY]\nMine=1\n"); // e.g. ReShade saved a setting into its copy
        var changed = File.ReadAllBytes(fx.GacIni);
        var recordedHash = Record(fx.LedgerPath, fx.Game).Metadata[XnaGacReShade.IniSha256MetadataKey];
        var moved = fx.MoveLibrary();

        var result = fx.Components().RepairAfterMove(moved.Game);

        Assert.True(result.Success, result.Message); // the repair itself is done
        var newBase = XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!);
        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1" + XnaGacReShade.GacIniChangedAfterMovePrefix + newBase, result.Message);
        Assert.Equal(changed, File.ReadAllBytes(moved.GacIni));
        Assert.Equal(recordedHash, Record(fx.LedgerPath, moved.Game).Metadata[XnaGacReShade.IniSha256MetadataKey]); // still not RHI's
        Assert.DoesNotContain("still names the game's old folder", UiLocalizationService.PresentFor("pt-BR", result.Message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrefixMovedOnItsOwn_ACopyTheUserChanged_StillNamesTheRightFolder_NoNote()
    {
        // Only the prefix moved: the copy's BasePath (the game folder) is still right, so there is nothing to say.
        var fx = await InstalledAsync("changed-prefix");
        File.AppendAllText(fx.GacIni, "[OVERLAY]\nMine=1\n");
        var changed = File.ReadAllBytes(fx.GacIni);
        var moved = fx.MovePrefixApart(gameFolderMovedToo: false);

        var result = fx.Components().RepairAfterMove(moved.Game);

        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1", result.Message);
        Assert.Equal(changed, File.ReadAllBytes(moved.GacIni));
    }

    [Fact]
    public async Task WineRunningForThePrefix_TheRepairIsDone_TheRewriteIsRefusedWithANote()
    {
        var fx = await InstalledAsync("wine");
        var before = File.ReadAllBytes(fx.GacIni);
        var moved = fx.MoveLibrary();
        var running = fx.NewService(new WineDllOverrideService(new WineRegistryEditor(_ => true)));

        var result = fx.Components(running).RepairAfterMove(moved.Game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1" + XnaGacReShade.GacIniWineRunningAfterMoveMessage, result.Message);
        Assert.Equal(before, File.ReadAllBytes(moved.GacIni));
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
        Assert.False(new DeploymentRelocation(fx.Records).NeedsRepair(moved.Game)); // the records were repaired
        Assert.DoesNotContain("Wine is running", UiLocalizationService.PresentFor("pt-BR", result.Message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepairThatFails_RewritesNothing()
    {
        // The library was copied, not moved: the old folder still holds RHI's files, so Repair refuses; the old copy's
        // ReShade.ini (the record still names it) is not rewritten with the new folder.
        var fx = await InstalledAsync("refused");
        var before = File.ReadAllBytes(fx.GacIni);
        var copied = fx.CopyLibrary();

        var result = fx.Components().RepairAfterMove(copied.Game);

        Assert.False(result.Success);
        Assert.StartsWith(DeploymentRelocation.RepairOldFolderExistsPrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fx.GacIni));
        Assert.Equal(before, File.ReadAllBytes(copied.GacIni));
    }

    [Fact]
    public async Task TheRewritesLedgerSaveFails_TheRepairStands_TheCopyIsBack_AndTheLineSaysWhatToDo()
    {
        var fx = await InstalledAsync("blocked");
        var before = File.ReadAllBytes(fx.GacIni);
        var moved = fx.MoveLibrary();
        var components = fx.Components();

        RelocationRepairResult result;
        using (LedgerWriteBlocker.Block(fx.Records, fx.LedgerPath)) result = components.RepairAfterMove(moved.Game);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentRelocation.RepairDonePrefix + "1" + XnaGacReShade.GacIniNotUpdatedAfterMovePrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(moved.GacIni));
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    public static TheoryData<string> Crashes => new() { "dies-before-the-write", "write-lands-then-dies" };

    [Theory]
    [MemberData(nameof(Crashes))]
    public async Task CrashDuringTheRewrite_RecoveryLeavesTheOldOrTheNewFile_AndTheRecordAgrees(string shape)
    {
        var fx = await InstalledAsync("crash-" + shape);
        var before = File.ReadAllBytes(fx.GacIni);
        var moved = fx.MoveLibrary();
        var components = fx.Components();
        var newBase = XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!);

        using var death = new SimulatedDeath(fx.Journal);
        // The repair's own commit is a staged write; the rewrite's stamped record save is the next ledger save.
        if (shape == "dies-before-the-write") fx.Records.SaveFaultForTesting = () => death.Die();
        else fx.Records.SaveWrittenFaultForTesting = () => death.Die();
        var (died, _) = await death.RunUntilDeathAsync(() => Task.Run(() => components.RepairAfterMove(moved.Game)));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);
        var landed = shape == "write-lands-then-dies";
        Assert.Equal(landed ? JournalRecoveryOutcome.AlreadyCommitted : JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(landed ? ReShadeIniManager.WithInstallBasePath(before, newBase) : before, File.ReadAllBytes(moved.GacIni)); // whole, old or new
        AssertRecordMatchesTheCopy(fx.LedgerPath, moved);
        Assert.Equal(["ReShade.ini", "d3d9.dll"], Directory.GetFiles(moved.Gac).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static string? BasePathIn(string ini) => IniLines.Get(IniLines.Read(ini), "INSTALL", "BasePath");

    private static LinuxDeploymentRecord Record(string ledger, DetectedGame game)
        => new LinuxDeploymentRecordStore(ledger).Find(game, ManagedComponentKind.ReShade)!;

    // The record (a fresh read of the ledger) names the GAC folder the game uses now and the bytes of its ReShade.ini.
    private static void AssertRecordMatchesTheCopy(string ledger, Located moved)
    {
        var record = Record(ledger, moved.Game);
        Assert.Equal(moved.Gac, record.Metadata[XnaGacReShade.FolderMetadataKey]);
        Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(moved.GacIni), record.Metadata[XnaGacReShade.IniSha256MetadataKey]),
            "the record's GAC ReShade.ini hash is the file's");
    }

    private sealed record Located(DetectedGame Game, string Gac)
    {
        public string GacIni => Path.Combine(Gac, "ReShade.ini");
    }

    private sealed class Fixture
    {
        public required string Root { get; init; }
        public required DetectedGame Game { get; init; }
        public required string Bin { get; init; }
        public required string Prefix { get; init; }
        public required string Gac { get; init; }
        public required string JournalDir { get; init; }
        public required string LedgerPath { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required XnaGacReShadeTests.VersionHandler Handler { get; init; }
        public required ReShadeDeploymentService Service { get; init; }
        public required Func<WineDllOverrideService, ReShadeDeploymentService> NewService { get; init; }
        public string GacIni => Path.Combine(Gac, "ReShade.ini");

        public LinuxManagedComponentService Components(ReShadeDeploymentService? reshade = null)
            => new(Records, reshade: reshade ?? Service, addonManager: null!, renodx: null!, renodxUpgrade: null!,
                optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
                dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
                ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
                neuralVersionPins: new NeuralAddonVersionPreferenceStore(Path.Combine(Root, "neural-pins.json")));

        /// <summary>The whole Steam library folder moved (game folder and prefix together).</summary>
        public Located MoveLibrary()
        {
            Directory.Move(Path.Combine(Root, "lib1"), Path.Combine(Root, "lib2"));
            return At(Bin.Replace("/lib1/", "/lib2/", StringComparison.Ordinal), Prefix.Replace("/lib1/", "/lib2/", StringComparison.Ordinal));
        }

        /// <summary>The library copied, not moved (the old folder still holds RHI's files).</summary>
        public Located CopyLibrary()
        {
            CopyTree(Path.Combine(Root, "lib1"), Path.Combine(Root, "lib2"));
            return At(Bin.Replace("/lib1/", "/lib2/", StringComparison.Ordinal), Prefix.Replace("/lib1/", "/lib2/", StringComparison.Ordinal));
        }

        /// <summary>The prefix moved on its own (a custom prefix folder), optionally the game folder too.</summary>
        public Located MovePrefixApart(bool gameFolderMovedToo)
        {
            var prefix = Path.Combine(Root, "prefixes", "terraria");
            Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
            Directory.Move(Prefix, prefix);
            var bin = Bin;
            if (gameFolderMovedToo)
            {
                bin = Path.Combine(Root, "lib3", "steamapps", "common", "Terraria");
                Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
                Directory.Move(Bin, bin);
            }
            return At(bin, prefix);
        }

        private Located At(string bin, string prefix)
        {
            var game = new DetectedGame
            {
                Name = Game.Name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Terraria.exe"), PrefixPath = prefix,
                Runner = Game.Runner, GraphicsApi = Game.GraphicsApi, Source = Game.Source, SteamAppId = Game.SteamAppId, ManifestXnaGacFolder = GacWindows
            };
            return new Located(game, Gac.Replace(Prefix, prefix, StringComparison.Ordinal));
        }

        private static void CopyTree(string from, string to)
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
        }
    }

    // A Steam-layout library (game folder and prefix under one steamapps folder), ReShade installed for XNA by RHI.
    private async Task<Fixture> InstalledAsync(string name)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "lib1", "steamapps", "common", "Terraria")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "lib1", "steamapps", "compatdata", "105600", "pfx")).FullName;
        var gac = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows", "Microsoft.NET", "assembly", "GAC_32",
            "Microsoft.Xna.Framework.Graphics", "v4.0_4.0.0.0__842cf8be1de50553")).FullName;
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
        var handler = new XnaGacReShadeTests.VersionHandler();
        var http = new HttpClient(handler);
        var cache = Path.Combine(root, "cache");
        var templates = new UserIniTemplates(Path.Combine(state, "inis"));
        ReShadeDeploymentService Build(WineDllOverrideService wine)
            => new(http, new TransactionalFileDeploymentService(journal), records, wine, new StubExtractor(), cache,
                new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
                journal: journal, dllNames: new RHI.Platform.Linux.Paths.DllNamePreferenceStore(Path.Combine(state, "dll-names.json")))
            {
                IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan)
            };
        var service = Build(overrides);
        var installed = await service.InstallAsync(game, new ReShadeInstallOptions());
        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(gac, "ReShade.ini")));
        return new Fixture
        {
            Root = root, Game = game, Bin = bin, Prefix = prefix, Gac = gac, JournalDir = journalDir, LedgerPath = ledger, Records = records,
            Journal = journal, Handler = handler, Service = service, NewService = Build
        };
    }

    // DLLs carrying the setup's version, so an update brings other bytes.
    private sealed class StubExtractor : IReShadeArchiveExtractor
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
}
