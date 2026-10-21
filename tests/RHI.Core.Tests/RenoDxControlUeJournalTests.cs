using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #205: the Control UE steps (DLSS DLL swaps with their <c>.original</c> and earlier copies, renderer.ini's HDR preset)
/// run inside RenoDX's own journaled operation: a failed record save or a crash at any ledger write of the install, an
/// update (restore, then apply afresh) or the removal puts every one of those files back with the add-on, and nothing
/// else (no second undo) restores them.
/// </summary>
public sealed class RenoDxControlUeJournalTests : IDisposable
{
    private const string RendererIni = "{\r\n  \"m_eHDRPreset\": 0,\r\n  \"m_fHDRMaxNitsOverride\": -1.0\r\n}\r\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-controlue-journal-" + Guid.NewGuid().ToString("N"));

    public RenoDxControlUeJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static TheoryData<string, string, int> EveryLedgerWrite()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var failure in new[] { "ledger-blocked", "crash" })
        {
            data.Add(failure, "install", 1);
            data.Add(failure, "update", 1);
            data.Add(failure, "remove", 2);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryLedgerWrite))]
    public async Task ControlUe_FailsOrDies_TheDllsTheirBackupsAndRendererIniAreBack(string failure, string operation, int saveThatFails)
    {
        var fx = Arrange($"{failure}-{operation}");
        File.WriteAllText(Path.Combine(fx.Bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(fx.Bin, "nvngx_dlss.dll"), "OLD-SR");
        if (operation != "install")
        {
            var installed = await fx.Service.InstallAsync(fx.Game, [fx.Game.ExecutablePath!]);
            Assert.True(installed.Success, installed.Message);
            Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(fx.Bin, "nvngx_dlss.dll")));
            File.WriteAllText(Path.Combine(fx.Sources, "nvngx_dlss.dll"), "NEWER-SR"); // the update brings another DLL
        }
        var before = CancelAtWritePhase.Snapshot(fx.Root);
        var recordBefore = RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
        Func<Task<ComponentOperationResult>> run = operation switch
        {
            "install" => () => fx.Service.InstallAsync(fx.Game, [fx.Game.ExecutablePath!]),
            "update" => () => fx.Service.UpdateAsync(fx.Game, [fx.Game.ExecutablePath!]),
            _ => () => fx.Service.RemoveAsync(fx.Game)
        };

        var saves = 0;
        if (failure == "ledger-blocked")
        {
            fx.Records.SaveFaultForTesting = () => ++saves == saveThatFails ? new IOException("disk full (test)") : null;
            var result = await run();
            fx.Records.SaveFaultForTesting = null;
            Assert.False(result.Success);
            AssertBack();
            return;
        }
        using var death = new SimulatedDeath(fx.Journal);
        fx.Records.SaveFaultForTesting = () => ++saves == saveThatFails ? death.Die() : null;
        var (died, _) = await death.RunUntilDeathAsync(run);
        Assert.True(died);
        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.LedgerPath);
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        AssertBack();

        void AssertBack()
        {
            CancelAtWritePhase.AssertUnchanged(before, fx.Root);
            Assert.Equal(recordBefore, RenoDxJournalTests.Describe(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.RenoDX)));
            Assert.Empty(Directory.GetFiles(fx.JournalDir));
        }
    }

    // A Control UE file that cannot be put back (a renderer.ini the user locked) undoes the whole removal: RenoDX stays with
    // every file and its record as they were; once the file can be written again a retry finishes.
    [Fact]
    public async Task Remove_AControlUeFileCannotBePutBack_KeepsRenoDxWhole_RetryFinishes()
    {
        var fx = Arrange("remove-locked-renderer");
        var renderer = Path.Combine(fx.Bin, "renderer.ini");
        File.WriteAllText(renderer, RendererIni);
        Assert.True((await fx.Service.InstallAsync(fx.Game, [fx.Game.ExecutablePath!])).Success);
        File.SetUnixFileMode(renderer, UnixFileMode.UserRead);
        var before = CancelAtWritePhase.Snapshot(fx.Root);
        var recordBefore = RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));

        var failed = await fx.Service.RemoveAsync(fx.Game);

        Assert.False(failed.Success, failed.Message);
        Assert.StartsWith(RenoDxDeploymentService.ControlUeRestoreIncompletePrefix, failed.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, fx.Root);
        Assert.Equal(recordBefore, RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)));

        File.SetUnixFileMode(renderer, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var retry = await fx.Service.RemoveAsync(fx.Game);
        Assert.True(retry.Success, retry.Message);
        Assert.Equal(RendererIni, File.ReadAllText(renderer));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "nvngx_dlssd.dll")));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
    }

    private sealed record ControlFixture(DetectedGame Game, string Root, string Bin, string Sources, string JournalDir, string LedgerPath,
        LinuxDeploymentRecordStore Records, DeploymentJournal Journal, RenoDxDeploymentService Service);

    private ControlFixture Arrange(string name)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Control")).FullName;
        var exe = Path.Combine(bin, "Control_DX12.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var sources = Directory.CreateDirectory(Path.Combine(_root, name + "-dlss-cache")).FullName;
        File.WriteAllText(Path.Combine(sources, "nvngx_dlss.dll"), "NEW-SR");
        File.WriteAllText(Path.Combine(sources, "nvngx_dlssd.dll"), "NEW-RR");
        var game = new DetectedGame
        {
            Name = ControlUePostInstallService.GameName, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName, Runner = "Proton", SteamAppId = 870780
        };
        var state = Directory.CreateDirectory(Path.Combine(_root, name + "-state")).FullName;
        var ledger = Path.Combine(state, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(state, "journal");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, records);
        var http = new HttpClient(new Handler());
        var srVersion = 0;
        var controlUe = new ControlUePostInstallService(new TransactionalFileDeploymentService(), (kind, _) =>
            Task.FromResult<(string, string)?>(kind == DlssComponentKind.SuperResolution
                ? (Path.Combine(sources, "nvngx_dlss.dll"), $"310.{++srVersion}.0")
                : (Path.Combine(sources, "nvngx_dlssd.dll"), "310.4.0")));
        var service = new RenoDxDeploymentService(http, records, new RenoDxCatalogService(http), Path.Combine(state, "cache"), controlUe: controlUe, journal: journal);
        return new ControlFixture(game, root, bin, sources, journalDir, ledger, records, journal, service);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                        "<tr><td>Control Ultimate Edition</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr></table>")
                });
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
