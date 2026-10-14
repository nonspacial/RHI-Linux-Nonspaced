using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: the file side of RE Framework's journal (#23) and in-process rollback through owned-file snapshots (#31).
public sealed partial class REFrameworkDeploymentServiceTests
{
    private sealed class JournalFixture : IDisposable
    {
        public required DetectedGame Game { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required string Ledger { get; init; }
        public required string JournalDir { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required REFrameworkDeploymentService Service { get; init; }
        public required WineDllOverrideService Overrides { get; init; }
        public required HttpClient Http { get; init; }
        public byte[] Payload { get; set; } = [];
        public string Dll => Path.Combine(Game.BinaryDirectory!, REFrameworkDeploymentService.DllFileName);
        public string? Override() => WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(Game.PrefixPath!, "user.reg")), "dinput8", Path.GetFileName(Game.ExecutablePath));
        // The next start: a fresh process, so a fresh ledger reader (the dead thread still holds the old store's lock).
        public Task<JournalRecoveryReport?> RecoverAsync()
        {
            var ledger = new LinuxDeploymentRecordStore(Ledger);
            return LinuxDeploymentRecovery.RecoverAsync(
                LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), JournalDir, _ => false, ledger), ledger);
        }
        public void Dispose() => Http.Dispose();
    }

    // Each release request serves the build in Payload (so an update can bring different bytes).
    private JournalFixture NewJournalFixture(string name, Func<string, bool>? prefixInUse = null)
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var ledger = Path.Combine(_root, name + "-records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(_root, name + "-journal");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(prefixInUse ?? (_ => false)));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, _ => false, records);
        JournalFixture? fixture = null;
        var version = 0;
        var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            var zip = ZipOf(fixture!.Payload);
            if (url.Contains("REFramework-nightly/releases", StringComparison.OrdinalIgnoreCase))
            {
                version++;
                return JsonResponse(ReleaseJson($"nightly-0{1300 + version}-deadbeef", $"https://github.com/rhi-tests/ref-{version}.zip",
                    Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()));
            }
            if (url.StartsWith("https://github.com/rhi-tests/ref-", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        fixture = new JournalFixture
        {
            Game = game, Records = records, Ledger = ledger, JournalDir = journalDir, Journal = journal, Http = http, Overrides = overrides,
            Service = new REFrameworkDeploymentService(http, records, overrides, Path.Combine(_root, "cache-" + name), journal),
            Payload = MakePePayload(0x8664, 300 * 1024)
        };
        return fixture;
    }

    private static byte[] ZipOf(byte[] dll)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(REFrameworkDeploymentService.DllFileName, CompressionLevel.NoCompression).Open())
            stream.Write(dll);
        return output.ToArray();
    }

    private static byte[] Variant(byte[] payload, byte marker)
    {
        var copy = (byte[])payload.Clone();
        copy[^1] = marker;
        return copy;
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesTheDllAndTheOverride()
    {
        using var fixture = NewJournalFixture("crash-install");
        using var death = new SimulatedDeath(fixture.Journal);
        fixture.Records.SaveFaultForTesting = death.Die;

        var (died, run) = await death.RunUntilDeathAsync(() => fixture.Service.InstallAsync(fixture.Game));
        Assert.True(died);
        Assert.True(File.Exists(fixture.Dll));
        Assert.Equal("native,builtin", fixture.Override());

        var report = await fixture.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(fixture.Dll)); // before #23's file side, the dll stayed and blocked every later install
        Assert.Null(fixture.Override());
        Assert.Empty(Directory.GetFiles(fixture.JournalDir));
        Assert.Empty(Directory.GetFiles(fixture.Game.BinaryDirectory!, "*.rhi-*"));
        fixture.Records.SaveFaultForTesting = null;
        death.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => run);
    }

    [Fact]
    public async Task Update_CrashWhileSavingTheRecord_NextStartPutsThePreviousDllBack()
    {
        using var fixture = NewJournalFixture("crash-update");
        Assert.True((await fixture.Service.InstallAsync(fixture.Game)).Success);
        var installed = File.ReadAllBytes(fixture.Dll);
        fixture.Payload = Variant(fixture.Payload, 0xA1);
        using var death = new SimulatedDeath(fixture.Journal);
        fixture.Records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => fixture.Service.UpdateAsync(fixture.Game));
        Assert.True(died);
        Assert.Equal(fixture.Payload, File.ReadAllBytes(fixture.Dll));

        var report = await fixture.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(fixture.Dll));
        Assert.Equal("native,builtin", fixture.Override());
        Assert.Empty(Directory.GetFiles(fixture.Game.BinaryDirectory!, "*.rhi-*"));
    }

    [Fact]
    public async Task Update_RecordSaveFails_PutsThePreviousDllBackAndLeavesNoCopy()
    {
        using var fixture = NewJournalFixture("update-fails");
        Assert.True((await fixture.Service.InstallAsync(fixture.Game)).Success);
        var installed = File.ReadAllBytes(fixture.Dll);
        fixture.Payload = Variant(fixture.Payload, 0xB2);
        fixture.Records.SaveFaultForTesting = () => new IOException("disk full");

        var update = await fixture.Service.UpdateAsync(fixture.Game);

        Assert.False(update.Success);
        Assert.StartsWith("RE Framework update rolled back: disk full", update.Message, StringComparison.Ordinal);
        Assert.Equal(installed, File.ReadAllBytes(fixture.Dll));
        Assert.Empty(Directory.GetFiles(fixture.Game.BinaryDirectory!, "*.rhi-*"));
        Assert.Empty(Directory.GetFiles(fixture.JournalDir));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheDllAndTheOverrideBack()
    {
        using var fixture = NewJournalFixture("crash-remove");
        Assert.True((await fixture.Service.InstallAsync(fixture.Game)).Success);
        var installed = File.ReadAllBytes(fixture.Dll);
        using var death = new SimulatedDeath(fixture.Journal);
        // The removal's stamp is already saved when the override is restored; the process dies at the next save
        // (taking the record away), after the dll is deleted.
        fixture.Overrides.Registry.InsideLockForTesting = _ => fixture.Records.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => fixture.Service.RemoveAsync(fixture.Game));
        Assert.True(died);
        Assert.False(File.Exists(fixture.Dll));
        Assert.Null(fixture.Override());

        var report = await fixture.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(fixture.Dll));
        Assert.Equal("native,builtin", fixture.Override());
        Assert.NotNull(new LinuxDeploymentRecordStore(fixture.Ledger).Find(fixture.Game, ManagedComponentKind.REFramework));
    }

    [Fact]
    public async Task Remove_CrashAfterTheRecordIsGone_NextStartKeepsItRemoved()
    {
        using var fixture = NewJournalFixture("crash-after-remove");
        Assert.True((await fixture.Service.InstallAsync(fixture.Game)).Success);
        // Dies when committing (the record is already gone).
        fixture.Journal.CrashAtCommitForTesting = true;

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.RemoveAsync(fixture.Game));

        var report = await fixture.RecoverAsync();
        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(fixture.Dll));
        Assert.Null(fixture.Override());
        Assert.Empty(Directory.GetFiles(fixture.Game.BinaryDirectory!, "*.rhi-*"));
    }
}
