using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #96 NR Cost Scaler: a journaled layer over the nvngx_dlssnr.dll owner (sync #157's one-owner rule): it needs an owner
/// whose file still matches its record, keeps the owner's runtime as nvngx_dlssnr_real.dll, rolls back as a whole, puts
/// the owner's file back byte for byte on removal, and blocks the owner's changes while installed.
/// </summary>
public sealed class NrCostScalerTests : IDisposable
{
    private const string ApiUrl = NrCostScalerService.ReleaseApiUrl;
    private const string AssetUrl = "https://github.com/xenmods/DLSSNR-Cost-Scaler/releases/download/v1.0.6/DLSSNR-Cost-Scaler-v1.0.6.zip";
    private static readonly byte[] OwnerRuntime = Encoding.UTF8.GetBytes("MZ real NVIDIA neural rendering runtime");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-scaler-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal _journal;
    private readonly DetectedGame _game;

    private string Bin => Path.Combine(_root, "game", "bin");
    private string Runtime => Path.Combine(Bin, "nvngx_dlssnr.dll");

    public NrCostScalerTests()
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

    private void InstallOwner(ManagedComponentKind kind = ManagedComponentKind.ShortFuse, DetectedGame? game = null, bool optiScalerDlssNr = false)
    {
        game ??= _game;
        var runtime = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(runtime, OwnerRuntime);
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = runtime, Sha256 = Convert.ToHexString(SHA256.HashData(OwnerRuntime)) }]
        };
        if (optiScalerDlssNr) record.Metadata[OptiScalerDeploymentService.VariantMetadataKey] = OptiScalerVariant.DlssNr.ToString();
        _records.Upsert(record);
    }

    private static DetectedGame SteamGame(string library)
    {
        var install = Path.Combine(library, "steamapps", "common", "Scaled");
        return new DetectedGame
        {
            Name = "Scaled", Source = "Steam", SteamAppId = 4242, InstallPath = install, BinaryDirectory = Path.Combine(install, "bin"),
            ExecutablePath = Path.Combine(install, "bin", "Game.exe"), PrefixPath = Path.Combine(library, "steamapps", "compatdata", "4242", "pfx"), Runner = "Proton"
        };
    }

    [Theory]
    [InlineData(ManagedComponentKind.ShortFuse)]
    [InlineData(ManagedComponentKind.Dlss5Feeder)]
    [InlineData(ManagedComponentKind.OptiScaler)]
    public async Task LibraryMove_RepairAfterMoveCarriesScalerAndOwner_ThenTheScalerComesOffAndTheOwnerIsFreeAgain(ManagedComponentKind owner)
    {
        var before = SteamGame(Path.Combine(_root, "libA"));
        Directory.CreateDirectory(before.BinaryDirectory!);
        File.WriteAllBytes(before.ExecutablePath!, [0x4D, 0x5A]);
        InstallOwner(owner, before, optiScalerDlssNr: owner == ManagedComponentKind.OptiScaler);
        var (service, _) = Build();
        var installed = await service.InstallAsync(before);
        Assert.True(installed.Success, installed.Message);

        Directory.Move(Path.Combine(_root, "libA"), Path.Combine(_root, "libB"));
        var moved = SteamGame(Path.Combine(_root, "libB"));
        var relocation = new DeploymentRelocation(_records);
        Assert.NotNull(relocation.Guard(moved)); // records still at the old folder: everything refused until repaired

        var repair = relocation.Repair(moved, [], out _);

        Assert.True(repair.Success, repair.Message);
        Assert.Equal(2, repair.Repaired);
        Assert.Null(relocation.Guard(moved));
        var runtime = Path.Combine(moved.BinaryDirectory!, "nvngx_dlssnr.dll");
        Assert.Equal(Path.Combine(moved.BinaryDirectory!, "nvngx_dlssnr_real.dll"), service.GetRecord(moved)!.Files[0].Path);

        var removed = await service.RemoveAsync(moved);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(runtime));
        Assert.Equal(["Game.exe", "nvngx_dlssnr.dll"], Directory.EnumerateFiles(moved.BinaryDirectory!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var ownerFile = Assert.Single(_records.Find(moved, owner)!.Files);
        Assert.Equal(runtime, ownerFile.Path);
        Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(runtime), ownerFile.Sha256!)); // what the owner's own removal checks
        Assert.Null(NrCostScalerService.OwnerChangeRefusal(_records, moved, owner));
        Assert.Null(relocation.Guard(moved));
    }

    private static byte[] Package(bool withIni = true)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(content)); }
            Add("DLSSNR-Cost-Scaler/nvngx_dlssnr.dll", "MZ proxy");
            if (withIni) Add("DLSSNR-Cost-Scaler/nvngx_dlssnr.ini", "[Scale]\nFactor=0.75\n");
            Add("DLSSNR-Cost-Scaler/dlssnr-companion.addon64", "MZ addon");
            Add("README.md", "docs");
        }
        return buffer.ToArray();
    }

    private (NrCostScalerService Service, Fake Http) Build(byte[]? package = null, string? digest = null)
    {
        var zip = package ?? Package();
        var http = new Fake(request => request.RequestUri!.AbsoluteUri == ApiUrl
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"tag_name":"v1.0.6","assets":[{"name":"DLSSNR-Cost-Scaler-v1.0.6.zip","browser_download_url":"{{AssetUrl}}","digest":"sha256:{{digest ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}}"}]}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        return (new NrCostScalerService(new HttpClient(http), _records, _journal, Path.Combine(_root, "cache")), http);
    }

    [Fact]
    public async Task Install_KeepsTheOwnersRuntimeAsReal_PlacesProxyIniAddon_AndRecordsItAll()
    {
        InstallOwner();
        var (service, _) = Build();

        var result = await service.InstallAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.Equal("NR Cost Scaler installed over ShortFuse (v1.0.6).", result.Message);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Path.Combine(Bin, "nvngx_dlssnr_real.dll")));
        Assert.Equal("MZ proxy", File.ReadAllText(Runtime));
        Assert.Equal("[Scale]\nFactor=0.75\n", File.ReadAllText(Path.Combine(Bin, "nvngx_dlssnr.ini")));
        Assert.Equal("MZ addon", File.ReadAllText(Path.Combine(Bin, "dlssnr-companion.addon64")));
        Assert.False(File.Exists(Path.Combine(Bin, "README.md")));
        var record = service.GetRecord(_game)!;
        Assert.Equal(4, record.Files.Count);
        Assert.Equal("v1.0.6", record.Version);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "journal"), "*.json"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "cache"), "staged-*"));
    }

    [Fact]
    public async Task NoOwner_IsRefused_BeforeAnyRequest()
    {
        var (service, http) = Build();

        var result = await service.InstallAsync(_game);

        Assert.Equal(NrCostScalerService.NoOwnerMessage, result.Message);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void OptiScalerStableIsNoOwner_DlssNrIs()
    {
        InstallOwner(ManagedComponentKind.OptiScaler);
        var (service, _) = Build();
        Assert.Null(service.FindOwner(_game));

        var record = _records.Find(_game, ManagedComponentKind.OptiScaler)!;
        record.Metadata[OptiScalerDeploymentService.VariantMetadataKey] = OptiScalerVariant.DlssNr.ToString();
        _records.Upsert(record);
        Assert.Equal("OptiScaler DLSS NR", service.FindOwner(_game)?.Owner);
    }

    [Fact]
    public async Task OwnersFileChanged_IsRefused_AndNothingChanges()
    {
        InstallOwner();
        File.WriteAllText(Runtime, "the user swapped the DLL by hand");
        var (service, http) = Build();
        var before = CancelAtWritePhase.Snapshot(Bin);

        var result = await service.InstallAsync(_game);

        Assert.Equal(NrCostScalerService.OwnerModifiedPrefix + "ShortFuse", result.Message);
        Assert.Empty(http.Requests);
        CancelAtWritePhase.AssertUnchanged(before, Bin);
    }

    [Fact]
    public async Task ForeignRealDllInTheWay_IsRefused()
    {
        InstallOwner();
        File.WriteAllText(Path.Combine(Bin, "nvngx_dlssnr_real.dll"), "not ours");
        var (service, _) = Build();

        var result = await service.InstallAsync(_game);

        Assert.StartsWith(NrCostScalerService.ForeignFilePrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Runtime));
    }

    [Fact]
    public async Task PackageWithoutIni_OrWrongDigest_ChangesNothing()
    {
        InstallOwner();
        var before = CancelAtWritePhase.Snapshot(Bin);

        var incomplete = await Build(Package(withIni: false)).Service.InstallAsync(_game);
        var mismatch = await Build(digest: new string('0', 64)).Service.InstallAsync(_game);

        Assert.StartsWith(NrCostScalerService.DownloadFailedPrefix + NrCostScalerService.PackageIncompleteMessage, incomplete.Message, StringComparison.Ordinal);
        Assert.False(mismatch.Success);
        CancelAtWritePhase.AssertUnchanged(before, Bin);
        Assert.Null(_records.Find(_game, ManagedComponentKind.NrCostScaler));
    }

    [Fact]
    public async Task RecordSaveFailsAfterTheFilesMoved_EverythingRollsBack_EvenWithCancelPressed()
    {
        InstallOwner();
        var (service, _) = Build();

        var outcome = await CancelAtWritePhase.RunAsync(token => service.InstallAsync(_game, token), [Bin],
            atWritePhase: () => _records.SaveFaultForTesting = () => new IOException("Simulated write failure: deployments.json"));
        _records.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        Assert.StartsWith(NrCostScalerService.FailedRolledBackPrefix, outcome.Result.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, Bin);
        Assert.Null(_records.Find(_game, ManagedComponentKind.NrCostScaler));
    }

    [Fact]
    public async Task Remove_PutsTheOwnersRuntimeBackByteForByte_TheUserEditedIniGoesToo()
    {
        InstallOwner();
        var (service, _) = Build();
        await service.InstallAsync(_game);
        File.WriteAllText(Path.Combine(Bin, "nvngx_dlssnr.ini"), "[Scale]\nFactor=0.5\n"); // the user's own scale

        var result = await service.RemoveAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Runtime));
        Assert.Equal(["Game.exe", "nvngx_dlssnr.dll"], Directory.EnumerateFiles(Bin).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Null(service.GetRecord(_game));
        Assert.NotNull(_records.Find(_game, ManagedComponentKind.ShortFuse)); // the owner is untouched and its hash matches again
    }

    [Fact]
    public async Task Remove_ProxyChangedSinceInstall_IsRefused_AndLeftAlone()
    {
        InstallOwner();
        var (service, _) = Build();
        await service.InstallAsync(_game);
        File.WriteAllText(Runtime, "someone replaced the proxy");
        var before = CancelAtWritePhase.Snapshot(Bin);

        var result = await service.RemoveAsync(_game);

        Assert.StartsWith(NrCostScalerService.ScalerModifiedPrefix, result.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, Bin);
        Assert.NotNull(service.GetRecord(_game));
    }

    [Fact]
    public async Task WhileInstalled_TheOwnerCannotBeChanged_AfterRemovalItCan()
    {
        InstallOwner(ManagedComponentKind.Dlss5Feeder);
        var (service, _) = Build();
        await service.InstallAsync(_game);

        Assert.Equal(NrCostScalerService.OwnerBlockedPrefix + "DLSS5 Feeder",
            NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.Dlss5Feeder));
        Assert.NotNull(NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.ShortFuse));
        Assert.NotNull(NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.OptiScaler));
        Assert.Null(NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.ReShade));

        await service.RemoveAsync(_game);
        Assert.Null(NrCostScalerService.OwnerChangeRefusal(_records, _game, ManagedComponentKind.Dlss5Feeder));
    }

    [Fact]
    public async Task ManagedService_RefusesTheOwnersRemovalAndUpdate_WhileTheScalerIsInstalled()
    {
        InstallOwner(ManagedComponentKind.Dlss5Feeder);
        var (service, _) = Build();
        await service.InstallAsync(_game);
        var managed = new LinuxManagedComponentService(_records, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, updateLog: new ComponentUpdateLogStore(Path.Combine(_root, "data", "update-log.json")), nrCostScaler: service);
        var game = _game; // a manual entry with its exe and binary folder: a confirmed patch target

        var remove = await managed.RemoveAsync(game, ManagedComponentKind.Dlss5Feeder);
        var update = await managed.UpdateManagedAsync(game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.Equal(NrCostScalerService.OwnerBlockedPrefix + "DLSS5 Feeder", remove.Message);
        Assert.Equal(NrCostScalerService.OwnerBlockedPrefix + "DLSS5 Feeder", update.Message);
        Assert.Equal("MZ proxy", File.ReadAllText(Runtime));
    }

    [Fact]
    public async Task InstallThroughTheManagedService_IsLoggedInTheUpdateHistoryUnderItsName()
    {
        InstallOwner();
        var (service, _) = Build();
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "data", "update-log.json"));
        var managed = new LinuxManagedComponentService(_records, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, updateLog: log, nrCostScaler: service);

        var result = await managed.InstallNrCostScalerAsync(_game);

        Assert.True(result.Success, result.Message);
        var entry = Assert.Single(log.GetAll());
        Assert.Equal("NR Cost Scaler", entry.ComponentName);
        Assert.Equal("v1.0.6", entry.ToVersion);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
