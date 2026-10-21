using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #199: NR Cost Scaler takes part in Update All (upstream re-stages and re-installs it in the background pass): the latest
/// xenmods release against the recorded version (could not check when the list cannot be read), an in-place, journaled
/// swap of the proxy and add-on that never touches the owner's runtime, keeps an INI the user changed, and rolls back as
/// a whole.
/// </summary>
public sealed class NrCostScalerUpdateTests : IDisposable
{
    private const string ApiUrl = NrCostScalerService.ReleaseApiUrl;
    private static readonly byte[] OwnerRuntime = Encoding.UTF8.GetBytes("MZ real NVIDIA neural rendering runtime");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-update-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal _journal;
    private readonly DetectedGame _game;
    private string _tag = "v1.0.6";
    private byte[] _zip = Package("v1.0.6");
    private bool _offline;

    private string Bin => Path.Combine(_root, "game", "bin");
    private string Proxy => Path.Combine(Bin, "nvngx_dlssnr.dll");
    private string Real => Path.Combine(Bin, "nvngx_dlssnr_real.dll");
    private string Ini => Path.Combine(Bin, "nvngx_dlssnr.ini");
    private string Addon => Path.Combine(Bin, "dlssnr-companion.addon64");

    public NrCostScalerUpdateTests()
    {
        Directory.CreateDirectory(Bin);
        var exe = Path.Combine(Bin, "Game.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        _game = new DetectedGame { Name = "Scaled", InstallPath = Path.Combine(_root, "game"), ExecutablePath = exe, BinaryDirectory = Bin, Source = "Manual" };
        _records = new LinuxDeploymentRecordStore(Path.Combine(_root, "data", "deployments.json"));
        _journal = new DeploymentJournal(Path.Combine(_root, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal static byte[] Package(string version, bool withAddon = true)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(content)); }
            Add("DLSSNR-Cost-Scaler/nvngx_dlssnr.dll", "MZ proxy " + version);
            Add("DLSSNR-Cost-Scaler/nvngx_dlssnr.ini", "[Scale]\nFactor=0.75\n; " + version + "\n");
            if (withAddon) Add("DLSSNR-Cost-Scaler/dlssnr-companion.addon64", "MZ addon " + version);
        }
        return buffer.ToArray();
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (_offline) throw new HttpRequestException("offline test");
        var asset = $"https://github.com/xenmods/DLSSNR-Cost-Scaler/releases/download/{_tag}/DLSSNR-Cost-Scaler-{_tag}.zip";
        if (request.RequestUri!.AbsoluteUri == ApiUrl)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"tag_name":"{{_tag}}","assets":[{"name":"DLSSNR-Cost-Scaler-{{_tag}}.zip","browser_download_url":"{{asset}}","digest":"sha256:{{Convert.ToHexString(SHA256.HashData(_zip)).ToLowerInvariant()}}"}]}""")
            };
        return request.RequestUri.AbsoluteUri == asset ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) } : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private (NrCostScalerService Service, GameLinkCatalogueTests.Fake Http) Build()
    {
        var http = new GameLinkCatalogueTests.Fake(Answer);
        return (new NrCostScalerService(new HttpClient(http), _records, _journal, Path.Combine(_root, "cache")), http);
    }

    /// <summary>ShortFuse owns the runtime; the scaler v1.0.6 is installed over it.</summary>
    private async Task<NrCostScalerService> InstalledAsync()
    {
        File.WriteAllBytes(Proxy, OwnerRuntime);
        _records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = ManagedComponentKind.ShortFuse, BinaryDirectory = Bin, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = Proxy, Sha256 = Convert.ToHexString(SHA256.HashData(OwnerRuntime)) }]
        });
        var (service, _) = Build();
        var installed = await service.InstallAsync(_game);
        Assert.True(installed.Success, installed.Message);
        return service;
    }

    [Fact]
    public async Task Check_ComparesTheLatestReleaseWithTheRecordedVersion_AndSaysCouldNotCheckWhenTheListCannotBeRead()
    {
        var service = await InstalledAsync();

        var current = await service.CheckUpdateAsync(_game);
        Assert.Equal((true, false, false), (current.Managed, current.UpdateAvailable, current.CouldNotCheck));
        Assert.Equal(NrCostScalerService.UpToDatePrefix + "v1.0.6", current.Message);

        _tag = "v1.0.7";
        var newer = await service.CheckUpdateAsync(_game);
        Assert.Equal((true, false), (newer.UpdateAvailable, newer.CouldNotCheck));
        Assert.Equal(("v1.0.6", "v1.0.7"), (newer.InstalledVersion, newer.LatestVersion));

        _offline = true;
        var offline = await service.CheckUpdateAsync(_game);
        Assert.Equal((true, false, true), (offline.Managed, offline.UpdateAvailable, offline.CouldNotCheck)); // never "current"
        Assert.StartsWith(NrCostScalerService.CouldNotCheckPrefix, offline.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SwapsProxyIniAndAddonInPlace_TheOwnersRuntimeStaysByteIdentical()
    {
        var service = await InstalledAsync();
        _tag = "v1.0.7";
        _zip = Package("v1.0.7");

        var result = await service.UpdateAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NrCostScalerService.UpdatedPrefix + "v1.0.7.", result.Message);
        Assert.Equal("MZ proxy v1.0.7", File.ReadAllText(Proxy));
        Assert.Equal("MZ addon v1.0.7", File.ReadAllText(Addon));
        Assert.Contains("v1.0.7", File.ReadAllText(Ini));
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Real));
        var record = service.GetRecord(_game)!;
        Assert.Equal("v1.0.7", record.Version);
        Assert.All(record.Files, f => Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(f.Path), f.Sha256!), f.Path));
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "journal"), "*.json"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "cache"), "staged-*"));
        // The owner is still held by the layer, and removal still puts its runtime back byte for byte.
        Assert.NotNull(NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.ShortFuse));
        Assert.True((await service.RemoveAsync(_game)).Success);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Proxy));
    }

    [Fact]
    public async Task Update_KeepsAnIniTheUserChanged()
    {
        var service = await InstalledAsync();
        File.WriteAllText(Ini, "[Scale]\nFactor=0.5\n"); // the user's own scale
        var recordedIni = service.GetRecord(_game)!.Files.Single(f => f.Path == Ini).Sha256;
        _tag = "v1.0.7";
        _zip = Package("v1.0.7");

        var result = await service.UpdateAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(NrCostScalerService.IniKeptNote, result.Message, StringComparison.Ordinal);
        Assert.Equal("[Scale]\nFactor=0.5\n", File.ReadAllText(Ini));
        Assert.Equal("MZ proxy v1.0.7", File.ReadAllText(Proxy));
        Assert.Equal(recordedIni, service.GetRecord(_game)!.Files.Single(f => f.Path == Ini).Sha256);
    }

    [Fact]
    public async Task Update_AProxyChangedSinceInstall_IsRefusedBeforeAnyDownload()
    {
        var service = await InstalledAsync();
        File.WriteAllText(Proxy, "someone else's proxy");
        _tag = "v1.0.7";
        _zip = Package("v1.0.7");
        var (fresh, http) = Build();
        var before = CancelAtWritePhase.Snapshot(Bin);

        var result = await fresh.UpdateAsync(_game);

        Assert.StartsWith(NrCostScalerService.NotUpdatedModifiedPrefix, result.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
        CancelAtWritePhase.AssertUnchanged(before, Bin);
    }

    [Fact]
    public async Task Update_ARecordSaveThatFailsAfterTheFirstWrite_RollsEverythingBack_EvenWithCancelPressed()
    {
        var service = await InstalledAsync();
        _tag = "v1.0.7";
        _zip = Package("v1.0.7", withAddon: false);

        var outcome = await CancelAtWritePhase.RunAsync(token => service.UpdateAsync(_game, token), [Bin],
            atWritePhase: () => _records.SaveFaultForTesting = () => new IOException("Simulated write failure: deployments.json"));
        _records.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        Assert.StartsWith(NrCostScalerService.FailedRolledBackPrefix, outcome.Result.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, Bin); // the add-on the release dropped is back too
        Assert.Equal("v1.0.6", service.GetRecord(_game)!.Version);
    }

    [Fact]
    public async Task Update_AReleaseWithoutTheAddon_TakesRhisCopyAway()
    {
        var service = await InstalledAsync();
        _tag = "v1.0.7";
        _zip = Package("v1.0.7", withAddon: false);

        Assert.True((await service.UpdateAsync(_game)).Success);

        Assert.False(File.Exists(Addon));
        Assert.DoesNotContain(service.GetRecord(_game)!.Files, f => f.Path == Addon);
    }

    [Fact]
    public async Task UpdateAll_UpdatesIt_WithAHistoryEntry_AndTheGamesInclusionCanLeaveItOut()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request => Answer(request));
        var game = new DetectedGame { Name = "Fixture Scaled", InstallPath = Path.GetDirectoryName(Bin)!, ExecutablePath = _game.ExecutablePath, BinaryDirectory = Bin, Source = "Manual" };
        File.WriteAllBytes(Proxy, OwnerRuntime);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ShortFuse, BinaryDirectory = Bin, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = Proxy, Sha256 = Convert.ToHexString(SHA256.HashData(OwnerRuntime)) }]
        });
        Assert.True((await fx.Managed.InstallNrCostScalerAsync(game)).Success);
        Assert.Contains(ManagedComponentKind.NrCostScaler, LinuxUpdateAllService.SupportedComponents);
        _tag = "v1.0.7";
        _zip = Package("v1.0.7");

        fx.Vm.UpdatePolicy!.Inclusion!.SetExcluded(game, [ManagedComponentKind.NrCostScaler], [ManagedComponentKind.NrCostScaler]);
        var leftOut = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);
        Assert.Equal(1, leftOut.ExcludedCount);
        Assert.Equal("MZ proxy v1.0.6", File.ReadAllText(Proxy));

        fx.Vm.UpdatePolicy.Inclusion.SetExcluded(game, [ManagedComponentKind.NrCostScaler], []);
        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        var item = Assert.Single(result.Items, i => i.Component == ManagedComponentKind.NrCostScaler);
        Assert.True(item.Changed, item.Message);
        Assert.Equal(1, result.HeldCount + result.CurrentCount + result.UncheckedCount); // ShortFuse: held by the scaler or offline, never failed
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("MZ proxy v1.0.7", File.ReadAllText(Proxy));
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Real));
        var history = new ComponentUpdateLogStore(Path.Combine(Path.GetDirectoryName(fx.Settings.SettingsPath)!, "prefs", "update-log.json")).GetAll();
        Assert.Contains(history, e => e.Component == ManagedComponentKind.NrCostScaler && e.Action == ComponentUpdateAction.Update
                                      && e.FromVersion == "v1.0.6" && e.ToVersion == "v1.0.7");
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
