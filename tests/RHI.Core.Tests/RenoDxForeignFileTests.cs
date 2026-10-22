using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// DEP-10 (#29): RenoDX never overwrites, and so never later deletes, a same-named add-on it does not own.
public sealed class RenoDxForeignFileTests : IDisposable
{
    // A third-party build linked by a manifest snapshot override: its name does not start with "renodx".
    private const string OverrideUrl = "https://github.com/someone/ue-extended/releases/download/snapshot/foo.addon64";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-foreign-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxDeploymentRecordStore _store;
    private readonly RenoDxDeploymentService _service;
    private readonly DetectedGame _game;
    private readonly string[] _files;
    private readonly byte[] _userBytes = Encoding.ASCII.GetBytes("MZ the user's own foo add-on, installed by hand");

    public RenoDxForeignFileTests()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Foo.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var reshade = Path.Combine(bin, "ReShade64.dll");
        File.WriteAllText(reshade, "MZ ReShade crosire");
        _game = new DetectedGame { Name = "Foo Game", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton" };
        _files = [exe, reshade];
        var http = new HttpClient(new OverrideHandler());
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        _store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        _service = new RenoDxDeploymentService(http, _store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest);
    }

    private string Target => Path.Combine(_game.BinaryDirectory!, "foo.addon64");

    [Fact]
    public async Task OverrideNamedLikeAnExistingUserAddon_LeavesItsBytesIntactThroughInstallAndRemove()
    {
        File.WriteAllBytes(Target, _userBytes);

        var install = await _service.InstallAsync(_game, _files);
        var remove = await _service.RemoveAsync(_game);

        Assert.False(install.Success);
        Assert.EndsWith(Target, install.Message, StringComparison.Ordinal);
        Assert.False(remove.Success);
        Assert.Equal(_userBytes, File.ReadAllBytes(Target));
        Assert.Null(_store.Find(_game, ManagedComponentKind.RenoDX));
        Assert.StartsWith("Instalação do RenoDX recusada:", UiLocalizationService.PresentFor("pt-BR", install.Message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseVariantOfTheAddonName_IsTreatedAsTheSameFile()
    {
        var variant = Path.Combine(_game.BinaryDirectory!, "Foo.ADDON64");
        File.WriteAllBytes(variant, _userBytes);

        var install = await _service.InstallAsync(_game, _files);

        Assert.False(install.Success);
        Assert.False(File.Exists(Target));
        Assert.Equal(_userBytes, File.ReadAllBytes(variant));
    }

    [Fact]
    public async Task OwnedAddonReplacedByTheUser_IsNotOverwrittenOnReinstallNorDeletedOnRemove()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        File.WriteAllBytes(Target, _userBytes);

        var reinstall = await _service.InstallAsync(_game, _files);
        var remove = await _service.RemoveAsync(_game);

        Assert.False(reinstall.Success);
        Assert.Contains("modified outside RHI", reinstall.Message, StringComparison.Ordinal);
        Assert.False(remove.Success);
        Assert.Equal(_userBytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task OwnedUnchangedAddon_ReinstallsAndRemoves()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);

        var reinstall = await _service.InstallAsync(_game, _files);
        var record = _store.Find(_game, ManagedComponentKind.RenoDX);
        var remove = await _service.RemoveAsync(_game);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.False(record!.Files.Single().ExistedBeforeInstall);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(Target));
    }

    // w10 follow-up D15, the catalogue install: a game held because the undo of its own earlier RenoDX add-on could not
    // finish (the add-on is still in the folder) gets the hold's restart text, not the foreign-file refusal.
    [PermissionDenialFact]
    public async Task OnAGameHeldByItsOwnRenoDxUndo_TheInstallGetsTheRestartText_NotTheForeignFileRefusal()
    {
        var journal = SimulatedDeath.NewJournal(Path.Combine(_root, "journal"), _store).Journal;
        var http = new HttpClient(new OverrideHandler());
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var service = new RenoDxDeploymentService(http, _store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest, journal: journal);
        var bin = _game.BinaryDirectory!;
        var earlier = Path.Combine(bin, "renodx-foogame.addon64");
        var (opened, refusal) = await RHI.Platform.Linux.Deployment.DeploymentScope.BeginAsync(journal, "earlier RenoDX change", _game, ManagedComponentKind.RenoDX, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(earlier, _userBytes);
            _store.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(bin);
                return new IOException("Simulated write failure: records.json");
            };
            try
            {
                Assert.NotNull(Record.Exception(() => _store.Upsert(scope.Stamp(new LinuxDeploymentRecord
                {
                    GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = ManagedComponentKind.RenoDX,
                    BinaryDirectory = bin, Files = [new LinuxDeployedFile { Path = earlier, Sha256 = RHI.Core.Deployment.FileHash.Sha256HexOf(_userBytes) }]
                }))));
            }
            finally
            {
                _store.SaveFaultForTesting = null;
            }
            Assert.False((await scope.RollbackAsync()).Success); // held
        }
        try
        {
            Assert.True(File.Exists(earlier));
            var install = await service.InstallAsync(_game, _files);
            Assert.False(install.Success);
            Assert.Equal(RHI.Core.Deployment.DeploymentJournal.EarlierUndoFailedMessage, install.Message);
            Assert.False(File.Exists(Target));
        }
        finally
        {
            PermissionDenial.MakeWritable(bin);
        }
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class OverrideHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://manifest.test/manifest.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{ "version": 2, "snapshotOverrides": { "Foo Game": "{{OverrideUrl}}" } }""", Encoding.UTF8, "application/json")
                });
            if (url != OverrideUrl) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
