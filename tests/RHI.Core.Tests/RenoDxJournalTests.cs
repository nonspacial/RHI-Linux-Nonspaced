using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #205: RenoDX's install, update (a renamed add-on), local drop (#195) and removal each run as ONE journaled operation on
/// RenoDX's record: the add-on, its [renodx] keys in a ReShade.ini linked to the user's shared file, peak brightness, the
/// upstream Engine.ini template in a read-only Engine.ini and RHI's earlier add-on under another name. A failed ledger write
/// (rolled back in process) or a crash at any ledger write of the operation (rolled back by the next start) leaves the game,
/// the prefix and the shared file byte for byte as before, the link a link, the Engine.ini lock in place, and the ledger
/// describing the files on disk.
/// </summary>
public sealed class RenoDxJournalTests : IDisposable
{
    private const string EngineIniBefore = "[SystemSettings]\nUserKeep=42\n";
    private const UnixFileMode Locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-journal-" + Guid.NewGuid().ToString("N"));

    public RenoDxJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every ledger write of every operation: install 1 (the record), update 1, drop 1, removal 2 (stamp, record gone).</summary>
    public static TheoryData<string, string, int> EveryLedgerWrite()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var failure in new[] { "ledger-blocked", "crash" })
        {
            data.Add(failure, "install", 1);
            data.Add(failure, "update-renamed", 1);
            data.Add(failure, "drop", 1);
            data.Add(failure, "remove", 1);
            data.Add(failure, "remove", 2);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryLedgerWrite))]
    public async Task FailsOrDies_AtEveryLedgerWrite_TheTreeIsBackByteForByte_LinkAndLockKept_RecordsDescribeTheFiles(string failure, string operation, int saveThatFails)
    {
        var fx = Fixture($"{failure}-{operation}-{saveThatFails}");
        if (operation != "install")
        {
            var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);
            Assert.True(installed.Success, installed.Message);
        }
        var service = fx.Service;
        Func<Task<ComponentOperationResult>> run = operation switch
        {
            "install" => () => service.InstallAsync(fx.Game, fx.Files),
            "update-renamed" => () => fx.Renamed().UpdateAsync(fx.Game, fx.Files),
            "drop" => () => service.InstallLocalAddonAsync(fx.Game, fx.Dropped()),
            _ => () => service.RemoveAsync(fx.Game)
        };
        var before = fx.Snapshot();
        var sharedBefore = File.ReadAllBytes(fx.Shared);
        var recordBefore = Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));

        // A crash at a removal's first write (its stamp, before it landed) leaves an entry no record carries: recovery counts
        // it as done, and it tracked nothing. Every other crash is rolled back.
        var outcome = operation == "remove" && saveThatFails == 1 ? JournalRecoveryOutcome.AlreadyCommitted : JournalRecoveryOutcome.RolledBack;
        await FailAsync(fx, failure, saveThatFails, outcome, run, () =>
        {
            CancelAtWritePhase.AssertUnchanged(before, fx.Dirs);
            ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, sharedBefore);
            Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
            var ledger = new LinuxDeploymentRecordStore(fx.LedgerPath); // what the next start reads
            Assert.Equal(recordBefore, Describe(ledger.Find(fx.Game, ManagedComponentKind.RenoDX)));
            AssertRecordsDescribeTheFiles(ledger, fx);
        });
    }

    [Fact]
    public async Task InstallUpdateRenamedRemove_Succeed_TheSharedFileAndTheLockedEngineIniEndAsTheyStarted()
    {
        var fx = Fixture("happy");
        var sharedBefore = File.ReadAllBytes(fx.Shared);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);
        Assert.True(installed.Success, installed.Message);
        Assert.Contains("Set_Path=1", File.ReadAllText(fx.Shared)); // through the link, into the shared file
        Assert.Contains("ToneMapPeakNits=800", File.ReadAllText(fx.Shared));
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(fx.EngineIni));
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "renodx-journal-a.addon64")));

        var updated = await fx.Renamed().UpdateAsync(fx.Game, fx.Files);
        Assert.True(updated.Success, updated.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "renodx-journal-a.addon64")));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "renodx-journal-b.addon64")));
        AssertRecordsDescribeTheFiles(fx.Records, fx);

        var removed = await fx.Service.RemoveAsync(fx.Game);
        Assert.True(removed.Success, removed.Message);
        ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, sharedBefore);
        Assert.Equal(EngineIniBefore, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        Assert.Empty(Directory.GetFiles(fx.Bin, "renodx-*"));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    // Cancel pressed the moment the install starts writing: nothing changed before that, no download after it, and with
    // the record save failing the whole install is undone (lock and link kept).
    [Fact]
    public async Task Install_CancelAtTheWriteGate_NoDownloadAfterIt_AndAFailedSaveUndoesItAll()
    {
        var fx = Fixture("cancel", watch: true);
        var sharedBefore = File.ReadAllBytes(fx.Shared);

        var run = await CancelAtWritePhase.RunAsync(ct => fx.Service.InstallAsync(fx.Game, fx.Files, ct), fx.Dirs, atWritePhase: () =>
        {
            fx.Watch!.CloseGate();
            fx.Records.SaveFaultForTesting = () => new IOException("disk full (test)");
        });
        fx.Records.SaveFaultForTesting = null;

        Assert.False(run.Result.Success);
        Assert.Empty(fx.Watch!.LateRequests);
        CancelAtWritePhase.AssertUnchanged(run.Before, fx.Dirs);
        ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, sharedBefore);
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    // The service's own write gate (not only Track's): a Cancel at the gate is refused and the install completes.
    [Fact]
    public async Task Install_CancelAtTheWriteGate_RunsToCompletion()
    {
        var fx = Fixture("cancel-complete");

        var run = await CancelAtWritePhase.RunAsync(ct => fx.Service.InstallAsync(fx.Game, fx.Files, ct), fx.Dirs);

        Assert.True(run.Result.Success, run.Result.Message);
        AssertRecordsDescribeTheFiles(fx.Records, fx);
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(fx.EngineIni));
    }

    // The journal's rollback restores bytes; the mode step only puts a lock back on the bytes it saw before. A file the
    // user changed after a crash keeps whatever mode it has.
    [Fact]
    public void ModeStep_LeavesAFileSomeoneElseChanged_AsItIs()
    {
        var file = Path.Combine(_root, "Engine.ini");
        File.WriteAllText(file, EngineIniBefore);
        File.SetUnixFileMode(file, Locked);
        var step = UserFileModeCompensator.Describe(file)!;
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(file, "the user's own edit");

        Assert.Equal(CompensationStatus.Done, new UserFileModeCompensator().Compensate(step).Status);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        File.WriteAllText(file, EngineIniBefore);
        new UserFileModeCompensator().Compensate(step);
        Assert.Equal(Locked, File.GetUnixFileMode(file));
    }

    // #213 audit (RenoDX's own site): the ⚙ "Set maximum nits" journals the shared file a linked ReShade.ini points at.
    [Theory]
    [InlineData("ledger-blocked")]
    [InlineData("crash")]
    public async Task PeakNits_SetForGame_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBack(string failure)
    {
        var fx = Fixture("peak-" + failure);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        var sharedBefore = File.ReadAllBytes(fx.Shared);
        var peak = new RenoDxPeakNits(fx.Records, fx.Journal, _ => null);

        await FailAsync(fx, failure, 1, JournalRecoveryOutcome.RolledBack, () => peak.SetForGameAsync(fx.Game, 1500),
            () => ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, sharedBefore));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ReShadeLinkedIniJournalTests.FailAsync"/> with the recovery outcome to expect: the <paramref name="saveThatFails"/>-th
    /// ledger save fails (rolled back in process) or the process dies there (rolled back by the next start's recovery), then
    /// <paramref name="assertRolledBack"/> runs (inside the death scope for a crash).
    /// </summary>
    internal static async Task FailAsync(JournalFixture fx, string failure, int saveThatFails, JournalRecoveryOutcome outcome,
        Func<Task<ComponentOperationResult>> run, Action assertRolledBack)
    {
        var saves = 0;
        if (failure == "ledger-blocked")
        {
            fx.Records.SaveFaultForTesting = () => ++saves == saveThatFails ? new IOException("disk full (test)") : null;
            var result = await run();
            fx.Records.SaveFaultForTesting = null;
            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(fx.JournalDir));
            assertRolledBack();
            return;
        }
        using var death = new SimulatedDeath(fx.Journal);
        fx.Records.SaveFaultForTesting = () => ++saves == saveThatFails ? death.Die() : null;
        var (died, _) = await death.RunUntilDeathAsync(run);
        Assert.True(died);
        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);
        Assert.Equal(outcome, Assert.Single(report!.Entries).Outcome);
        assertRolledBack();
    }

    internal static string? Describe(LinuxDeploymentRecord? record)
    {
        if (record is null) return null;
        var copy = LinuxDeploymentRecordStore.Clone(record);
        copy.Metadata.Remove(LinuxDeploymentRecovery.OperationIdMetadataKey);
        copy.Metadata.Remove(LinuxDeploymentRecovery.RemovalOperationIdMetadataKey);
        copy.SavedAtUtc = default;
        return System.Text.Json.JsonSerializer.Serialize(copy);
    }

    /// <summary>The ledger names exactly the RenoDX add-ons on disk, each with the bytes it recorded.</summary>
    private static void AssertRecordsDescribeTheFiles(LinuxDeploymentRecordStore ledger, JournalFixture fx)
    {
        var record = ledger.Find(fx.Game, ManagedComponentKind.RenoDX);
        var listed = record?.Files.Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var file in record?.Files ?? [])
        {
            Assert.True(File.Exists(file.Path), $"recorded but missing: {file.Path}");
            Assert.Equal(file.Sha256, FileHash.Sha256Hex(file.Path), ignoreCase: true);
        }
        foreach (var addon in Directory.GetFiles(fx.Bin, "renodx-*"))
            Assert.True(listed.Contains(Path.GetFullPath(addon)), $"on disk but in no record: {addon}");
    }

    internal sealed class JournalFixture
    {
        public required DetectedGame Game { get; init; }
        public required string Root { get; init; }
        public required string Bin { get; init; }
        public required string Prefix { get; init; }
        public required string Shared { get; init; }
        public required string EngineIni { get; init; }
        public required string JournalDir { get; init; }
        public required string LedgerPath { get; init; }
        public required string CacheDir { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required RenoDxDeploymentService Service { get; init; }
        public required JournalHandler Handler { get; init; }
        public GateWatchingHandler? Watch { get; init; }
        public required string[] Files { get; init; }

        public string[] Dirs => [Path.Combine(Root, "Game"), Prefix, Path.GetDirectoryName(Shared)!];

        public IReadOnlyDictionary<string, string> Snapshot() => CancelAtWritePhase.Snapshot(Dirs);

        /// <summary>The same app after the catalogue renamed the game's add-on (a new manifest read).</summary>
        public RenoDxDeploymentService Renamed()
        {
            Handler.AddonName = "renodx-journal-b.addon64";
            Handler.Fill = 0x5B;
            return NewService(Handler, Records, Journal, CacheDir, Watch);
        }

        /// <summary>A RenoDX add-on file in a downloads folder, as dropped (#195).</summary>
        public string Dropped()
        {
            var path = Path.Combine(Root, "Downloads", "renodx-journal-dropped.addon64");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PeArchitectureDetectorTests.WritePe(path, 0x8664, 96 * 1024);
            return path;
        }
    }

    private JournalFixture Fixture(string name, bool watch = false)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "JournalProject", "Binaries", "Win64")).FullName;
        var prefix = Path.Combine(root, "pfx");
        var engineIni = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "JournalProject", "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engineIni)!);
        File.WriteAllText(engineIni, EngineIniBefore);
        File.SetUnixFileMode(engineIni, Locked); // the user locked it so the game cannot rewrite it
        var exe = Path.Combine(bin, "JournalProject-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var shared = ReShadeLinkedIniJournalTests.LinkSharedIni(bin);
        var game = new DetectedGame
        {
            Name = "Journal Game", InstallPath = Path.Combine(root, "Game"), BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", Source = "Steam", SteamAppId = 2050, EngineIniProjectOverride = "JournalProject", EngineIniFileOverride = "journal-template.ini"
        };
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var ledger = Path.Combine(state, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(root, "journal");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, records);
        var handler = new JournalHandler();
        var watching = watch ? new GateWatchingHandler(handler) : null;
        var cache = Path.Combine(state, "cache");
        return new JournalFixture
        {
            Game = game, Root = root, Bin = bin, Prefix = prefix, Shared = shared, EngineIni = engineIni, JournalDir = journalDir,
            LedgerPath = ledger, CacheDir = cache, Records = records, Journal = journal, Handler = handler, Watch = watching,
            Service = NewService(handler, records, journal, cache, watching), Files = [exe, Path.Combine(bin, "ReShade64.dll")]
        };
    }

    private static RenoDxDeploymentService NewService(JournalHandler handler, LinuxDeploymentRecordStore records, DeploymentJournal journal,
        string cache, GateWatchingHandler? watch)
    {
        var http = new HttpClient(watch ?? (HttpMessageHandler)handler);
        var manifest = new UpstreamRhiManifestService(http, JournalHandler.ManifestUrl);
        return new RenoDxDeploymentService(http, records, new RenoDxCatalogService(http, manifest), cache, manifest, journal: journal)
        {
            PeakNits = () => new PeakNitsSettings(800, true, [1])
        };
    }

    /// <summary>The upstream manifest (the game's add-on, a [renodx] key, an Engine.ini template), the template and the add-on.</summary>
    internal sealed class JournalHandler : HttpMessageHandler
    {
        public const string ManifestUrl = "https://github.com/rhi-tests/renodx-journal/raw/main/manifest.json";
        public string AddonName { get; set; } = "renodx-journal-a.addon64";
        public byte Fill { get; set; } = 0x4A;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url == ManifestUrl)
                return Ok(new StringContent($$"""
                    {
                      "version": 3,
                      "snapshotOverrides": { "Journal Game": "https://clshortfuse.github.io/renodx/{{AddonName}}" },
                      "renodxIniOverrides": { "Journal Game": { "Set_Path": "1" } },
                      "engineIniFiles": { "Journal Game": "journal-template.ini" }
                    }
                    """, Encoding.UTF8, "application/json"));
            if (url == UpstreamEngineIniFileManager.BaseUrl + "journal-template.ini")
                return Ok(new StringContent("[SystemSettings]\nr.HDR.EnableHDROutput=1\n", Encoding.UTF8, "text/plain"));
            if (url == "https://clshortfuse.github.io/renodx/" + AddonName)
            {
                var bytes = new byte[120 * 1024];
                Array.Fill(bytes, Fill, 512, bytes.Length - 512);
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
                return Ok(new ByteArrayContent(bytes));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(HttpContent content) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
