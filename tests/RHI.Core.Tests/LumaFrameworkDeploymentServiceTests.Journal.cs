using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #169: the Luma install and removal are one journaled operation each (package files, ReShade.ini, Engine.ini and the DX9
// translation with dgVoodoo2); a crash at any ledger write is undone at the next start, or kept when it committed.
public sealed partial class LumaFrameworkDeploymentServiceTests
{
    [Fact]
    public async Task Install_CrashAtTheRecordSave_NextStartLeavesTheGameAndEngineIniAsBefore()
    {
        var fx = CreateLumaCancelFixture("crash-install", journaled: true);
        using var http = fx.Http;
        var before = CancelAtWritePhase.Snapshot(fx.Root);
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(fx.Journal!))
        {
            fx.Records.SaveFaultForTesting = death.Die; // the install's only ledger write is its commit point
            (var died, run) = await death.RunUntilDeathAsync(() => fx.Service.InstallAsync(fx.Game));
            Assert.True(died);
            Assert.Contains("r.Luma.Metadata=1", File.ReadAllText(fx.EngineIni));

            var report = await SimulatedDeath.RecoverAsync(fx.JournalDirectory, fx.RecordsPath);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            CancelAtWritePhase.AssertUnchanged(before, fx.Root);
        }
        await run;
        fx.Records.SaveFaultForTesting = null;
        Assert.Null(new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.LumaFramework));
    }

    // Checked before the dead thread is let go: in a real crash nothing runs after the commit write.
    [Fact]
    public async Task Install_CrashRightAfterTheRecordSave_IsKept()
    {
        var fx = CreateLumaCancelFixture("crash-install-committed", journaled: true);
        using var http = fx.Http;
        using var death = new SimulatedDeath(fx.Journal!);
        fx.Records.SaveWrittenFaultForTesting = death.Die;
        var (died, _) = await death.RunUntilDeathAsync(() => fx.Service.InstallAsync(fx.Game));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(fx.JournalDirectory, fx.RecordsPath);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        var record = new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.LumaFramework)!;
        Assert.All(record.Files, file => Assert.True(File.Exists(file.Path), file.Path));
        Assert.Contains("r.Luma.Metadata=1", File.ReadAllText(fx.EngineIni));
    }

    [Theory]
    [InlineData(1)] // the removal's stamp
    [InlineData(2)] // the commit point
    public async Task Remove_CrashAtAnyLedgerWrite_NextStartPutsEverythingBack_AndItRemovesAgain(int crashAtSave)
    {
        var fx = CreateLumaCancelFixture("crash-remove-" + crashAtSave, journaled: true);
        using var http = fx.Http;
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        var installed = CancelAtWritePhase.Snapshot(fx.Root);
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(fx.Journal!))
        {
            var saves = 0;
            fx.Records.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null;
            (var died, run) = await death.RunUntilDeathAsync(() => fx.Service.RemoveAsync(fx.Game));
            Assert.True(died);

            await SimulatedDeath.RecoverAsync(fx.JournalDirectory, fx.RecordsPath);

            CancelAtWritePhase.AssertUnchanged(installed, fx.Root);
        }
        await run;
        fx.Records.SaveFaultForTesting = null;
        Assert.NotNull(new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.LumaFramework));
        var again = await fx.Service.RemoveAsync(fx.Game);
        Assert.True(again.Success, again.Message);
        Assert.DoesNotContain("r.Luma.Metadata", File.ReadAllText(fx.EngineIni));
    }

    // A DX9 Luma package (dgVoodoo2 at d3d9.dll, RHI's ReShade moved to dxgi.dll): ReShade's changed record is saved in
    // Luma's commit write, so a crash at any ledger write of the install leaves it where its file is.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dx9Install_CrashAtAnyLedgerWrite_LeavesReShadesRecordWhereItsFileIs(int crashAtSave)
    {
        var (service, records, game, root, journal, journalDirectory, recordsPath, package) = CreateDx9LumaFixture("crash-dx9-" + crashAtSave);
        var before = CancelAtWritePhase.Snapshot(root);
        bool died;
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(journal))
        {
            var saves = 0;
            records.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null;
            (died, run) = await death.RunUntilDeathAsync(() => service.InstallLocalPackageAsync(game, package));
            if (died)
            {
                await SimulatedDeath.RecoverAsync(journalDirectory, recordsPath);
                var fresh = new LinuxDeploymentRecordStore(recordsPath);
                var reShade = fresh.Find(game, ManagedComponentKind.ReShade)!;
                Assert.All(reShade.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                Assert.Null(fresh.Find(game, ManagedComponentKind.LumaFramework));
                CancelAtWritePhase.AssertUnchanged(before, root);
                Assert.Equal(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), Assert.Single(reShade.Files).Path);
            }
        }
        var result = await run;
        records.SaveFaultForTesting = null;
        if (!died)
        {
            Assert.True(result.Success, result.Message);
            var reShade = new LinuxDeploymentRecordStore(recordsPath).Find(game, ManagedComponentKind.ReShade)!;
            Assert.Equal(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Assert.Single(reShade.Files).Path);
            Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dgVoodoo.conf")));
        }
    }

    private (LumaFrameworkDeploymentService Service, LinuxDeploymentRecordStore Records, DetectedGame Game, string Root,
        DeploymentJournal Journal, string JournalDirectory, string RecordsPath, string Package) CreateDx9LumaFixture(string name)
    {
        var root = Path.Combine(_root, name);
        var binary = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(binary, "Vanquish.exe");
        File.WriteAllBytes(exe, MakePePayload(0x8664, 4096));
        var game = new DetectedGame
        {
            Name = "Vanquish", InstallPath = binary, BinaryDirectory = binary, ExecutablePath = exe, PrefixPath = prefix,
            Source = "Steam", Runner = "Proton", GraphicsApi = GraphicsApi.DirectX9
        };
        File.WriteAllText(Path.Combine(binary, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");
        var reshade = MakePePayload(0x8664, 256 * 1024);
        var d3d9 = Path.Combine(binary, "d3d9.dll");
        File.WriteAllBytes(d3d9, reshade);
        var recordsPath = Path.Combine(_root, name + "-records.json");
        var records = new LinuxDeploymentRecordStore(recordsPath);
        var journalDirectory = Path.Combine(_root, name + "-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDirectory, records);
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = binary, Version = "6.5.1",
            WineOverride = new WineDllOverrideService().EnsureNativeBuiltin(prefix, "d3d9", "Vanquish.exe"),
            Files = [new LinuxDeployedFile { Path = d3d9, Sha256 = Sha256(reshade) }]
        });
        var dgVoodooZip = ZipOf(("MS/x64/D3D9.dll", MakePePayload(0x8664, 128 * 1024)), ("MS/x86/D3D9.dll", MakePePayload(0x014c, 128 * 1024)));
        const string dgVoodooUrl = "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.86/dgVoodoo2_86.zip";
        var releaseJson = JsonSerializer.Serialize(new[] { new { tag_name = "v2.86",
            assets = new[] { new { name = "dgVoodoo2_86.zip", browser_download_url = dgVoodooUrl, digest = "sha256:" + Sha256(dgVoodooZip) } } } });
        var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/repos/dege-diosg/dgVoodoo2/releases/latest" => JsonResponse(releaseJson),
            dgVoodooUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dgVoodooZip) },
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var package = Path.Combine(_root, name + "-Luma-Vanquish.zip");
        File.WriteAllBytes(package, MakeLumaZip());
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-" + name),
            dgVoodoo: new DgVoodooDeploymentService(http, Path.Combine(_root, "cache-dg-" + name)),
            dx9ReShade: new Dx9TranslationReShadeCoordinator(records, overrides, journal), journal: journal);
        return (service, records, game, root, journal, journalDirectory, recordsPath, package);
    }

    private static byte[] ZipOf(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (entryName, bytes) in entries)
            {
                using var output = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                output.Write(bytes);
            }
        return stream.ToArray();
    }
}
