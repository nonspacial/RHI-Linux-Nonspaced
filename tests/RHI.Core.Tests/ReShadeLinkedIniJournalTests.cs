using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #207: a game's ReShade.ini may be a link to the user's shared file in <c>~/.config/ReShade</c> (the one ReShade.ini link
/// rule). ReShade's install, update and removal journal the file a write through the link replaces — the shared file —
/// never the link: a rollback (failed ledger write, or crash recovery at the next start) keeps the link a link and puts the
/// shared file back byte for byte, so no game linked to it is left with lines no record lists. A completed removal takes
/// RHI's lines back out of the shared file. The Vulkan layer's install: <c>ReShadeChannelTests.LinkedIni</c>.
/// </summary>
public sealed class ReShadeLinkedIniJournalTests : IDisposable
{
    internal const string SharedBefore = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\nTextureSearchPaths=.\\mine-tex\\**\n\n[OVERLAY]\nTutorialProgress=4\n";
    internal const string Template = "[GENERAL]\nEffectSearchPaths=.\\rhi-test\\**\nTextureSearchPaths=.\\rhi-test-tex\\**\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-linked-ini-" + Guid.NewGuid().ToString("N"));

    public ReShadeLinkedIniJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    public static TheoryData<string> Failures => new() { "ledger-blocked", "crash" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Install_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var fx = Fixture("install-" + failure);

        await FailAsync(fx, failure, saveThatFails: 1, () => fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true }),
            Encoding.UTF8.GetBytes(SharedBefore));

        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Update_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var fx = Fixture("update-" + failure);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var afterInstall = File.ReadAllBytes(fx.Shared);
        Assert.Contains(".\\rhi-test\\**", Encoding.UTF8.GetString(afterInstall)); // RHI's line went into the shared file
        fx.Template = "[GENERAL]\nEffectSearchPaths=.\\rhi-test-2\\**\n"; // the update writes another line
        fx.Handler.Version = "6.9.1";

        await FailAsync(fx, failure, saveThatFails: 1, () => fx.Service.UpdateAsync(fx.Game), afterInstall);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Removal_FailsOrDies_TheLinkStaysALink_AndTheSharedFileKeepsRhisLines(string failure)
    {
        var fx = Fixture("remove-" + failure);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var afterInstall = File.ReadAllBytes(fx.Shared);

        // The removal's stamp is the first ledger save; the commit write (the record removed) is the second.
        await FailAsync(fx, failure, saveThatFails: 2, () => fx.Service.RemoveAsync(fx.Game), afterInstall);

        Assert.NotNull(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task Removal_TakesRhisLinesOutOfTheSharedFile_ByteForByte_AndKeepsTheLink()
    {
        var fx = Fixture("remove-ok");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        Assert.NotEqual(SharedBefore, File.ReadAllText(fx.Shared));

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        AssertLinked(fx.Bin, fx.Shared, Encoding.UTF8.GetBytes(SharedBefore));
    }

    [Fact]
    public async Task Removal_OfAnIniRhiCreated_ThatTheUserLinkedSince_KeepsTheLinkAndTheSharedFile()
    {
        // RHI created the game's ReShade.ini; the user then replaced it with a link to a shared file of the same content.
        // A link is never RHI's file: removal leaves the link and the file it points to.
        var fx = Fixture("remove-created", link: false);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var ini = Path.Combine(fx.Bin, "ReShade.ini");
        var bytes = File.ReadAllBytes(ini);
        File.WriteAllBytes(fx.Shared, bytes);
        File.Delete(ini);
        File.CreateSymbolicLink(ini, fx.Shared);

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);

        AssertLinked(fx.Bin, fx.Shared, bytes);
    }

    /// <summary>
    /// Runs <paramref name="run"/> with the <paramref name="saveThatFails"/>-th ledger save failing (ledger-blocked: an
    /// IOException, the operation rolls back in process) or dying (crash: the next start's recovery rolls it back), then
    /// runs <paramref name="assertRolledBack"/> (inside the death scope for a crash: disposing it lets the parked operation
    /// run its own catch).
    /// </summary>
    internal static async Task FailAsync(LinuxDeploymentRecordStore records, DeploymentJournal journal, string journalDir, string ledgerPath,
        string failure, int saveThatFails, Func<Task<ComponentOperationResult>> run, Action assertRolledBack)
    {
        var saves = 0;
        if (failure == "ledger-blocked")
        {
            records.SaveFaultForTesting = () => ++saves == saveThatFails ? new IOException("disk full (test)") : null;
            var result = await run();
            records.SaveFaultForTesting = null;
            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(journalDir));
            assertRolledBack();
            return;
        }
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = () => ++saves == saveThatFails ? death.Die() : null;
        var (died, _) = await death.RunUntilDeathAsync(run);
        Assert.True(died);
        var report = await SimulatedDeath.RecoverAsync(journalDir, ledgerPath);
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        assertRolledBack();
    }

    private static Task FailAsync(LinkedFixture fx, string failure, int saveThatFails, Func<Task<ComponentOperationResult>> run, byte[] expected)
        => FailAsync(fx.Records, fx.Journal, fx.JournalDir, fx.LedgerPath, failure, saveThatFails, run, () => AssertLinked(fx.Bin, fx.Shared, expected));

    /// <summary>The game's ReShade.ini is still the link to <paramref name="shared"/>, which holds <paramref name="expected"/>.</summary>
    internal static void AssertLinked(string bin, string shared, byte[] expected)
    {
        Assert.Equal(shared, new FileInfo(Path.Combine(bin, "ReShade.ini")).LinkTarget); // still the link, to the same file
        Assert.Equal(Encoding.UTF8.GetString(expected), File.ReadAllText(shared));
        Assert.Equal(expected, File.ReadAllBytes(shared));
        Assert.DoesNotContain(Directory.GetFiles(bin), file => Path.GetFileName(file).StartsWith("ReShade.ini.", StringComparison.Ordinal));
    }

    /// <summary>A shared ReShade.ini in the (test-isolated) user ReShade folder, linked from <paramref name="bin"/>.</summary>
    internal static string LinkSharedIni(string bin, bool link = true)
    {
        var shared = Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.ConfigHome, "ReShade", Guid.NewGuid().ToString("N"), "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, SharedBefore);
        if (link) File.CreateSymbolicLink(Path.Combine(bin, "ReShade.ini"), shared);
        return shared;
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class LinkedFixture
    {
        public required DetectedGame Game { get; init; }
        public required string Bin { get; init; }
        public required string Shared { get; init; }
        public required string JournalDir { get; init; }
        public required string LedgerPath { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required XnaGacReShadeTests.VersionHandler Handler { get; init; }
        public required ReShadeDeploymentService Service { get; init; }
        public string Template { get; set; } = ReShadeLinkedIniJournalTests.Template;
    }

    private LinkedFixture Fixture(string name, bool link = true)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "bin")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var shared = LinkSharedIni(bin, link);
        var game = new DetectedGame
        {
            Name = "Linked " + name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, Source = "Test", SteamAppId = 4243
        };
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var ledger = Path.Combine(state, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(root, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        var handler = new XnaGacReShadeTests.VersionHandler();
        var http = new HttpClient(handler);
        var cache = Path.Combine(root, "cache");
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), records, overrides,
            new VersionedExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal, dllNames: new RHI.Platform.Linux.Paths.DllNamePreferenceStore(Path.Combine(state, "dll-names.json")));
        var fx = new LinkedFixture
        {
            Game = game, Bin = bin, Shared = shared, JournalDir = journalDir, LedgerPath = ledger, Records = records, Journal = journal,
            Handler = handler, Service = service
        };
        service.IniDefaults = (_, _) => new ReShadeIniDefaults { Template = Encoding.UTF8.GetBytes(fx.Template) };
        return fx;
    }

    // A stand-in extractor whose DLLs carry the setup's version, so an update brings other bytes.
    private sealed class VersionedExtractor : IReShadeArchiveExtractor
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
