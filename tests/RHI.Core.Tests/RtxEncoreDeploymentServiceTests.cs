using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #158 RTX Encore as a managed component: name from the #120 store, foreign file kept as .original and put back, Wine
/// override per .dll name, journaled install/update/removal, NVIDIA's exact 310.8.0 Neural Rendering file under the
/// one-owner rule (#157), fail-closed download (GitHub digest + SHA256SUMS + RTX Encore's own version resource).
/// </summary>
public sealed partial class RtxEncoreDeploymentServiceTests : IDisposable
{
    private const string Rc3 = "1.0.0-rc.3";
    private const string Rc4 = "1.0.0-rc.4";
    private static readonly string NrAcceptedUrl = "https://github.com/rhi-tests/rhi-repo/releases/download/dlssnr-310.8.0/nvngx_dlssnr_310.8.0.zip";
    private static readonly string NrOtherUrl = "https://github.com/rhi-tests/rhi-repo/releases/download/dlssnr-310.8.3/nvngx_dlssnr_310.8.3.zip";
    private static readonly byte[] NrAccepted = Pe(0x31);
    private static readonly byte[] NrOther = Pe(0x32);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-encore-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin;
    private readonly string _prefix;
    private readonly DetectedGame _game;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DllNamePreferenceStore _names;

    public RtxEncoreDeploymentServiceTests()
    {
        _bin = Path.Combine(_root, "game", "bin");
        _prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(_bin);
        Directory.CreateDirectory(_prefix);
        File.WriteAllBytes(Path.Combine(_bin, "Game.exe"), Pe(0x11, 64 * 1024));
        File.WriteAllBytes(Path.Combine(_bin, "nvngx_dlss.dll"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        _game = new DetectedGame
        {
            Name = "Encore Test", InstallPath = Path.Combine(_root, "game"), Source = "Test", Runner = "Wine", PrefixPath = _prefix,
            ExecutablePath = Path.Combine(_bin, "Game.exe"), BinaryDirectory = _bin, GraphicsApi = GraphicsApi.DirectX12
        };
        _records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        Directory.CreateDirectory(Path.Combine(_root, "journal"));
        _names = new DllNamePreferenceStore(Path.Combine(_root, "dll-names.json"));
    }

    private string Ledger => Path.Combine(_root, "deployments.json");
    private string JournalDir => Path.Combine(_root, "journal");

    // Journaled, as the app builds it (Wine never running): installs and removals run through DeploymentScope operations.
    private RtxEncoreDeploymentService Service(EncoreHandler handler, string? requiredNrSha = null) => Service(handler, out _, requiredNrSha);

    private RtxEncoreDeploymentService Service(EncoreHandler handler, out DeploymentJournal journal, string? requiredNrSha = null,
        Action<string>? beforeStaging = null)
    {
        var (created, overrides) = SimulatedDeath.NewJournal(JournalDir, _records);
        journal = created;
        return new(new HttpClient(handler), _records, overrides, _names,
            new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache"), journal)
        {
            RequiredNeuralRenderingSha256 = requiredNrSha ?? FileHash.Sha256HexOf(NrAccepted),
            BeforeStagingForTesting = beforeStaging
        };
    }

    private string UserReg => File.ReadAllText(Path.Combine(_prefix, "user.reg"));

    [Fact]
    public async Task Install_DefaultName_PutsRtxEncoreAsVersionDll_WithItsWineOverride()
    {
        var handler = new EncoreHandler();
        var service = Service(handler);

        var result = await service.InstallAsync(_game);

        Assert.True(result.Success, result.Message);
        var target = Path.Combine(_bin, "version.dll");
        Assert.Equal(handler.Payload(Rc3), File.ReadAllBytes(target));
        var record = _records.Find(_game, ManagedComponentKind.RtxEncore)!;
        Assert.Equal(Rc3, record.Version);
        Assert.Equal(FileHash.Sha256HexOf(handler.Payload(Rc3)), record.Files.Single().Sha256, ignoreCase: true);
        Assert.Contains("\"version\"=\"native,builtin\"", UserReg);
        Assert.False(NeuralRenderingRuntimeOwnership.ComponentOwnsRuntime(_records, _game, ManagedComponentKind.RtxEncore));
        Assert.StartsWith(RtxEncoreDeploymentService.InstalledPrefix, result.Message);
    }

    [Fact]
    public async Task Install_ChosenNameFromTheNameStore_IsUsed()
    {
        var service = Service(new EncoreHandler());
        Assert.True(service.SetName(_game, "winmm.dll").Success);

        Assert.True((await service.InstallAsync(_game)).Success);

        Assert.True(File.Exists(Path.Combine(_bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
        Assert.Contains("\"winmm\"=\"native,builtin\"", UserReg);
    }

    [Fact]
    public async Task ForeignFileUnderTheName_IsKeptAsOriginal_AndPutBackOnRemoval()
    {
        var foreign = Encoding.ASCII.GetBytes("someone else's version.dll");
        File.WriteAllBytes(Path.Combine(_bin, "version.dll"), foreign);
        var service = Service(new EncoreHandler());

        Assert.True((await service.InstallAsync(_game)).Success);
        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(_bin, "version.dll.original")));

        var removed = await service.RemoveAsync(_game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(_bin, "version.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll.original")));
        Assert.Null(_records.Find(_game, ManagedComponentKind.RtxEncore));
        Assert.DoesNotContain("\"version\"=\"native,builtin\"", UserReg);
    }

    [Fact]
    public async Task KeptCopyInTheWay_IsRefusedBeforeAnyDownload()
    {
        File.WriteAllText(Path.Combine(_bin, "version.dll"), "foreign");
        File.WriteAllText(Path.Combine(_bin, "version.dll.original"), "an older kept copy");
        var handler = new EncoreHandler();

        var result = await Service(handler).InstallAsync(_game);

        Assert.False(result.Success);
        Assert.StartsWith(RtxEncoreDeploymentService.KeptCopyInTheWayPrefix, result.Message);
        Assert.Equal(0, handler.Requests);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(_bin, "version.dll")));
    }

    [Fact]
    public void OfferedNames_FollowTheGamesApi_SkipBink_AndOtherComponentsNames_AndAsiNeedsTheLoader()
    {
        var service = Service(new EncoreHandler());

        var dx12 = service.OfferedNames(_game);
        Assert.Contains("version.dll", dx12);
        Assert.Contains("dxgi.dll", dx12);
        Assert.Contains("d3d12.dll", dx12);
        Assert.DoesNotContain("d3d9.dll", dx12);
        Assert.DoesNotContain("d3d11.dll", dx12);
        Assert.DoesNotContain("binkw64.dll", dx12);
        Assert.DoesNotContain(RtxEncoreNames.AsiName, dx12);

        _game.GraphicsApi = GraphicsApi.Vulkan;
        Assert.DoesNotContain("dxgi.dll", service.OfferedNames(_game));
        _game.GraphicsApi = GraphicsApi.DirectX12;

        _records.Upsert(Record(ManagedComponentKind.Rtx40MfgStandalone, Path.Combine(_bin, "dinput8.dll")));
        _records.Upsert(Record(ManagedComponentKind.UltimateAsiLoader, Path.Combine(_bin, "winmm.dll")));
        var withOthers = service.OfferedNames(_game);
        Assert.DoesNotContain("dinput8.dll", withOthers);
        Assert.DoesNotContain("winmm.dll", withOthers);
        Assert.Contains(RtxEncoreNames.AsiName, withOthers);
    }

    [Fact]
    public async Task AsAnAsiPlugin_BesideRhisAsiLoader_ItNeedsNoWineOverride()
    {
        _records.Upsert(Record(ManagedComponentKind.UltimateAsiLoader, Path.Combine(_bin, "winmm.dll")));
        var service = Service(new EncoreHandler());
        Assert.True(service.SetName(_game, RtxEncoreNames.AsiName).Success);

        var installed = await service.InstallAsync(_game);
        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(_bin, RtxEncoreNames.AsiName)));
        Assert.Null(_records.Find(_game, ManagedComponentKind.RtxEncore)!.WineOverride);

        var removed = await service.RemoveAsync(_game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(_bin, RtxEncoreNames.AsiName)));
    }

    [Fact]
    public async Task NameHeldByAnotherComponent_IsRefused_BeforeAnyDownload()
    {
        _names.SetRtxEncore(_game, "dinput8.dll");
        _records.Upsert(Record(ManagedComponentKind.Rtx40MfgStandalone, Path.Combine(_bin, "dinput8.dll")));
        var handler = new EncoreHandler();

        var result = await Service(handler).InstallAsync(_game);

        Assert.False(result.Success);
        Assert.Equal(RtxEncoreDeploymentService.NameHeldPrefix + "dinput8.dll", result.Message);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("x86")]
    [InlineData("dx9")]
    [InlineData("opengl")]
    [InlineData("no-dlss")]
    public void IneligibleGames_HaveAReason(string variant)
    {
        var dlss = variant != "no-dlss";
        if (variant == "native") _game.Runner = "Native Linux";
        if (variant == "x86") File.WriteAllBytes(_game.ExecutablePath!, Pe(0x11, 64 * 1024, 0x014C));
        if (variant == "dx9") _game.GraphicsApi = GraphicsApi.DirectX9;
        if (variant == "opengl") _game.GraphicsApi = GraphicsApi.OpenGL;

        Assert.NotNull(RtxEncoreDeploymentService.IneligibleReason(_game, dlss));
    }

    [Fact]
    public void EligibleGame_HasNoReason_AndShipsDlss()
    {
        Assert.True(RtxEncoreDeploymentService.HasGameDlss(_game));
        Assert.Null(RtxEncoreDeploymentService.IneligibleReason(_game, gameHasDlss: true));
        File.Delete(Path.Combine(_bin, "nvngx_dlss.dll"));
        Assert.False(RtxEncoreDeploymentService.HasGameDlss(_game));
    }

    [Fact]
    public async Task GameWithoutDlss_IsRefusedBeforeAnyDownload()
    {
        File.Delete(Path.Combine(_bin, "nvngx_dlss.dll"));
        var handler = new EncoreHandler();

        var result = await Service(handler).InstallAsync(_game);

        Assert.Equal(RtxEncoreDeploymentService.NeedsDlssMessage, result.Message);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task WithNeuralRendering_TheExact310_8_0File_IsPlaced_AndRtxEncoreOwnsTheRuntime()
    {
        File.WriteAllText(Path.Combine(_bin, "nvngx_dlssnr.dll"), "the game's own runtime");
        var service = Service(new EncoreHandler());
        Assert.True(service.SetNeuralRenderingPreference(_game, true).Success);

        var result = await service.InstallAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NrAccepted, File.ReadAllBytes(Path.Combine(_bin, "nvngx_dlssnr.dll")));
        Assert.Equal("the game's own runtime", File.ReadAllText(Path.Combine(_bin, "nvngx_dlssnr.dll.original")));
        Assert.Equal("RTX Encore", NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, _game, ManagedComponentKind.ShortFuse));
        Assert.True(NeuralRenderingRuntimeOwnership.ComponentOwnsRuntime(_records, _game, ManagedComponentKind.RtxEncore));

        Assert.True((await service.RemoveAsync(_game)).Success);
        Assert.Equal("the game's own runtime", File.ReadAllText(Path.Combine(_bin, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
    }

    [Fact]
    public async Task NeuralRendering_IsRefused_WhileAnotherComponentOwnsTheRuntime_BeforeAnyDownload()
    {
        _records.Upsert(Record(ManagedComponentKind.ShortFuse, Path.Combine(_bin, "nvngx_dlssnr.dll")));
        _names.SetRtxEncoreNeuralRendering(_game, true);
        var handler = new EncoreHandler();
        var service = Service(handler);

        var install = await service.InstallAsync(_game);
        var preference = service.SetNeuralRenderingPreference(_game, true);

        Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("ShortFuse"), install.Message);
        Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("ShortFuse"), preference.Message);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task NeuralRendering_AnyFileButTheAcceptedOne_IsRefused_AndNothingChanges()
    {
        _names.SetRtxEncoreNeuralRendering(_game, true);
        var service = Service(new EncoreHandler(), requiredNrSha: FileHash.Sha256HexOf(NrOther));

        var result = await service.InstallAsync(_game);

        Assert.Equal(RtxEncoreDeploymentService.NeuralRenderingMismatchMessage, result.Message);
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "nvngx_dlssnr.dll")));
    }

    [Fact]
    public async Task NeuralRendering_NotOfferedAnyMore_IsRefused_NeverAnotherVersion()
    {
        _names.SetRtxEncoreNeuralRendering(_game, true);
        var service = Service(new EncoreHandler { OfferAccepted = false });

        var result = await service.InstallAsync(_game);

        Assert.Equal(RtxEncoreDeploymentService.NeuralRenderingUnavailableMessage, result.Message);
        Assert.False(File.Exists(Path.Combine(_bin, "nvngx_dlssnr.dll")));
    }

    [Theory]
    [InlineData("310.8.0", true)]
    [InlineData("310.8.0 (50xx)", true)]
    [InlineData("310.8.01", false)]
    [InlineData("310.8.3 (50xx)", false)]
    public void RequiredNeuralRenderingVersion_IsExact(string version, bool accepted)
        => Assert.Equal(accepted, RtxEncoreDeploymentService.IsRequiredNeuralRenderingVersion(version));

    [Fact]
    public async Task NeuralRendering_OnAndOff_WhileInstalled_IsJournaled_AndPutsAKeptFileBack()
    {
        File.WriteAllText(Path.Combine(_bin, "nvngx_dlssnr.dll"), "kept runtime");
        var service = Service(new EncoreHandler());
        Assert.True((await service.InstallAsync(_game)).Success);
        Assert.Equal("kept runtime", File.ReadAllText(Path.Combine(_bin, "nvngx_dlssnr.dll")));

        var on = await service.SetNeuralRenderingAsync(_game, true);
        Assert.True(on.Success, on.Message);
        Assert.Equal(NrAccepted, File.ReadAllBytes(Path.Combine(_bin, "nvngx_dlssnr.dll")));
        Assert.True(service.NeuralRenderingChosen(_game));

        var off = await service.SetNeuralRenderingAsync(_game, false);
        Assert.True(off.Success, off.Message);
        Assert.Equal("kept runtime", File.ReadAllText(Path.Combine(_bin, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "nvngx_dlssnr.dll.original")));
        Assert.False(service.NeuralRenderingChosen(_game));
        Assert.Single(_records.Find(_game, ManagedComponentKind.RtxEncore)!.Files);
    }

    [Fact]
    public async Task NrCostScaler_RefusesToWrapRtxEncoresRuntime()
    {
        _names.SetRtxEncoreNeuralRendering(_game, true);
        Assert.True((await Service(new EncoreHandler()).InstallAsync(_game)).Success);
        var handler = new EncoreHandler();
        var scaler = new NrCostScalerService(new HttpClient(handler), _records, new DeploymentJournal(Path.Combine(_root, "scaler-journal")), Path.Combine(_root, "scaler-cache"));
        var before = handler.Requests;

        var result = await scaler.InstallAsync(_game);

        Assert.Equal(NrCostScalerService.RtxEncoreOwnerMessage, result.Message);
        Assert.Equal(before, handler.Requests);
        Assert.Equal(NrAccepted, File.ReadAllBytes(Path.Combine(_bin, "nvngx_dlssnr.dll")));
    }

    [Fact]
    public async Task Update_ReplacesTheFileUnderTheSameName()
    {
        var handler = new EncoreHandler();
        var service = Service(handler);
        _names.SetRtxEncore(_game, "winmm.dll");
        Assert.True((await service.InstallAsync(_game)).Success);
        handler.Latest = Rc4;

        var check = await service.CheckManagedUpdateAsync(_game);
        var result = await service.UpdateAsync(_game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.True(result.Success, result.Message);
        Assert.Equal(handler.Payload(Rc4), File.ReadAllBytes(Path.Combine(_bin, "winmm.dll")));
        Assert.Equal(Rc4, _records.Find(_game, ManagedComponentKind.RtxEncore)!.Version);
    }

    [Fact]
    public async Task ChangedFile_BlocksUpdateAndRemoval_AndIsLeftAsItIs()
    {
        var handler = new EncoreHandler();
        var service = Service(handler);
        Assert.True((await service.InstallAsync(_game)).Success);
        File.WriteAllText(Path.Combine(_bin, "version.dll"), "user replaced it");
        handler.Latest = Rc4;

        var update = await service.UpdateAsync(_game);
        var remove = await service.RemoveAsync(_game);

        Assert.StartsWith(RtxEncoreDeploymentService.ModifiedPrefix, update.Message);
        Assert.StartsWith(RtxEncoreDeploymentService.ModifiedPrefix, remove.Message);
        Assert.Equal("user replaced it", File.ReadAllText(Path.Combine(_bin, "version.dll")));
    }

    [Fact]
    public async Task Offline_Check_CouldNotCheck()
    {
        var handler = new EncoreHandler();
        var service = Service(handler);
        Assert.True((await service.InstallAsync(_game)).Success);
        handler.Offline = true;

        var check = await service.CheckManagedUpdateAsync(_game);

        Assert.True(check.CouldNotCheck);
        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task DownloadWhoseZipDoesNotMatchSha256Sums_IsRefused_AndNothingIsPlaced()
    {
        var handler = new EncoreHandler { CorruptSums = true };

        var result = await Service(handler).InstallAsync(_game);

        Assert.StartsWith(RtxEncoreDeploymentService.DownloadFailedPrefix, result.Message);
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
    }

    // Review minor 2: GitHub's digest is checked on its own (a release without SHA256SUMS).
    [Fact]
    public async Task ZipNotMatchingGitHubsDigest_WithoutSha256Sums_IsRefused_AndNothingIsPlaced()
    {
        var handler = new EncoreHandler { NoSums = true, WrongDigest = true };

        var result = await Service(handler).InstallAsync(_game);

        Assert.StartsWith(RtxEncoreDeploymentService.DownloadFailedPrefix, result.Message);
        Assert.Contains(RHI.Core.Deployment.AtomicFile.ShaMismatchPrefix, result.Message);
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
        Assert.Null(_records.Find(_game, ManagedComponentKind.RtxEncore));
    }

    [Fact]
    public async Task GitHubsDigestAlone_IsEnough()
    {
        var result = await Service(new EncoreHandler { NoSums = true }).InstallAsync(_game);

        Assert.True(result.Success, result.Message);
    }

    // Review minor 2: unticking the Neural Rendering file never deletes a runtime the user changed since.
    [Fact]
    public async Task UntickingNeuralRendering_RefusesWhenTheFileChanged_AndLeavesItAsItIs()
    {
        _names.SetRtxEncoreNeuralRendering(_game, true);
        var service = Service(new EncoreHandler());
        Assert.True((await service.InstallAsync(_game)).Success);
        var nr = Path.Combine(_bin, "nvngx_dlssnr.dll");
        File.WriteAllText(nr, "the user's own runtime");

        var off = await service.SetNeuralRenderingAsync(_game, false);

        Assert.False(off.Success);
        Assert.Equal(RtxEncoreDeploymentService.ModifiedPrefix + nr, off.Message);
        Assert.Equal("the user's own runtime", File.ReadAllText(nr));
        Assert.True(NeuralRenderingRuntimeOwnership.ListsRuntime(_records.Find(_game, ManagedComponentKind.RtxEncore)!));
        Assert.True(_names.GetRtxEncoreNeuralRendering(_game));
    }

    // Review minor 2: the update checks the bytes it is about to place, not only the cache entry it resolved.
    [Fact]
    public async Task Update_RefusesAPayloadThatIsNotRtxEncore_AtStaging_AndKeepsTheInstalledOne()
    {
        var handler = new EncoreHandler();
        Assert.True((await Service(handler).InstallAsync(_game)).Success);
        handler.Latest = Rc4;
        var swapped = 0;
        var service = Service(handler, out _, beforeStaging: cached => { swapped++; File.WriteAllBytes(cached, Pe(0x44, 400 * 1024)); });

        var result = await service.UpdateAsync(_game);

        Assert.Equal(1, swapped);
        Assert.StartsWith(RtxEncoreDeploymentService.FailedRolledBackPrefix, result.Message);
        Assert.Contains(RtxEncoreDeploymentService.NoVersionResourceMessage, result.Message);
        Assert.Equal(handler.Payload(Rc3), File.ReadAllBytes(Path.Combine(_bin, "version.dll")));
        Assert.Equal(Rc3, _records.Find(_game, ManagedComponentKind.RtxEncore)!.Version);
    }

    [Fact]
    public async Task PayloadWithoutRtxEncoresVersionResource_IsRefused()
    {
        var handler = new EncoreHandler { PayloadOverride = Pe(0x44, 400 * 1024) };

        var result = await Service(handler).InstallAsync(_game);

        Assert.StartsWith(RtxEncoreDeploymentService.DownloadFailedPrefix, result.Message);
        Assert.Equal(RtxEncoreDeploymentService.DownloadFailedPrefix + RtxEncoreDeploymentService.NoVersionResourceMessage, result.Message);
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
    }

    [Fact]
    public void ReleaseList_TakesTheNewestNonDraft_PrereleasesIncluded_AndNeedsADigest()
    {
        var json = """
        [
          {"tag_name":"v1.0.0-rc.5","draft":true,"prerelease":true,"assets":[{"name":"RTX-Encore-v1.0.0-rc.5.zip","browser_download_url":"https://github.com/x/a.zip","digest":"sha256:aa"}]},
          {"tag_name":"v1.0.0-rc.4","draft":false,"prerelease":true,"assets":[{"name":"SHA256SUMS","browser_download_url":"https://github.com/x/SHA256SUMS"},{"name":"RTX-Encore-v1.0.0-rc.4.zip","browser_download_url":"https://github.com/x/b.zip"}]}
        ]
        """;
        var release = RtxEncoreDeploymentService.ParseReleases(json);
        Assert.Equal("1.0.0-rc.4", release.Version);
        Assert.Equal("https://github.com/x/SHA256SUMS", release.ChecksumsUrl);

        var noDigest = """[{"tag_name":"v1.0.0","assets":[{"name":"RTX-Encore-v1.0.0.zip","browser_download_url":"https://github.com/x/c.zip"}]}]""";
        Assert.Equal(RtxEncoreDeploymentService.NoPublishedDigestPrefix + "v1.0.0",
            Assert.Throws<InvalidDataException>(() => RtxEncoreDeploymentService.ParseReleases(noDigest)).Message);
        // Review minor 4: every refusal is one of the service's own (translated) messages, never a raw parser text.
        foreach (var broken in new[] { "not json", "{\"message\":\"API rate limit exceeded\"}", "[{\"tag_name\":7,\"assets\":[]}]" })
            Assert.Equal(RtxEncoreDeploymentService.ReleaseListShapeMessage,
                Assert.Throws<InvalidDataException>(() => RtxEncoreDeploymentService.ParseReleases(broken)).Message);
        Assert.Equal(RtxEncoreDeploymentService.NoReleaseMessage,
            Assert.Throws<InvalidDataException>(() => RtxEncoreDeploymentService.ParseReleases("[]")).Message);
        var badTag = """[{"tag_name":"nightly","assets":[{"name":"RTX-Encore-x.zip","browser_download_url":"https://github.com/x/c.zip","digest":"sha256:aa"}]}]""";
        Assert.Equal(RtxEncoreDeploymentService.TagNotAVersionPrefix + "nightly",
            Assert.Throws<InvalidDataException>(() => RtxEncoreDeploymentService.ParseReleases(badTag)).Message);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var service = Service(new EncoreHandler());

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(_game, ct), [_bin, _prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(_records.Find(_game, ManagedComponentKind.RtxEncore));
    }

    [Fact]
    public async Task Install_FailingRecordSave_RollsEverythingBack_IncludingTheKeptFile()
    {
        File.WriteAllText(Path.Combine(_bin, "version.dll"), "foreign");
        var before = CancelAtWritePhase.Snapshot(_bin);
        var service = Service(new EncoreHandler());
        _records.SaveFaultForTesting = () => new IOException("disk full");

        var result = await service.InstallAsync(_game);

        Assert.StartsWith(RtxEncoreDeploymentService.FailedRolledBackPrefix, result.Message);
        _records.SaveFaultForTesting = null;
        CancelAtWritePhase.AssertUnchanged(before, _bin);
        Assert.Null(WineDllOverrideService.TryReadOverride(UserReg, "version", "Game.exe"));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_RunsToTheEnd()
    {
        var service = Service(new EncoreHandler());
        Assert.True((await service.InstallAsync(_game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(_game, ct), [_bin, _prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(_records.Find(_game, ManagedComponentKind.RtxEncore));
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartPutsTheForeignFileBack_AndTheOverrideAway()
    {
        File.WriteAllText(Path.Combine(_bin, "version.dll"), "foreign");
        var service = Service(new EncoreHandler(), out var journal);
        using var death = new SimulatedDeath(journal);
        _records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(_game));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(_bin, "version.dll.original")));

        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(_bin, "version.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll.original")));
        Assert.Null(WineDllOverrideService.TryReadOverride(UserReg, "version", "Game.exe"));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsRtxEncoreBack()
    {
        File.WriteAllText(Path.Combine(_bin, "version.dll"), "foreign");
        var handler = new EncoreHandler();
        var service = Service(handler, out var journal);
        Assert.True((await service.InstallAsync(_game)).Success);
        using var death = new SimulatedDeath(journal);
        var saves = 0;
        _records.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null; // the removal stamps the record, then dies taking it away

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(_game));
        Assert.True(died);
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(handler.Payload(Rc3), File.ReadAllBytes(Path.Combine(_bin, "version.dll")));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(_bin, "version.dll.original")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg, "version", "Game.exe"));
    }

    [Fact]
    public void NameStore_KeepsReShadeAndRtxEncoreChoicesApart()
    {
        _names.SetReShade(_game, "dxgi.dll");
        _names.SetRtxEncore(_game, "winmm.dll");
        _names.SetRtxEncoreNeuralRendering(_game, true);
        _names.SetReShade(_game, null);

        Assert.Equal("winmm.dll", _names.GetRtxEncore(_game));
        Assert.True(_names.GetRtxEncoreNeuralRendering(_game));
        _names.SetRtxEncore(_game, null);
        _names.SetRtxEncoreNeuralRendering(_game, false);
        Assert.Null(_names.GetReShade(_game));
        Assert.Throws<ArgumentException>(() => _names.SetRtxEncore(_game, "../evil.dll"));
    }

    [Fact]
    public async Task NameCannotChange_WhileInstalled()
    {
        var service = Service(new EncoreHandler());
        Assert.True((await service.InstallAsync(_game)).Success);

        Assert.Equal(RtxEncoreDeploymentService.NameLockedMessage, service.SetName(_game, "winmm.dll").Message);
    }

    private LinuxDeploymentRecord Record(ManagedComponentKind kind, string file) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = kind, BinaryDirectory = _bin,
        Files = [new LinuxDeployedFile { Path = file, Sha256 = "00" }]
    };

    internal static byte[] Pe(byte marker, int size = 80 * 1024, ushort machine = 0x8664)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(marker + i * 7)).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    internal static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes, 0, bytes.Length);
            }
        return memory.ToArray();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>GitHub release list + zip + SHA256SUMS for RTX Encore, and the DLSS list with two NR runtimes.</summary>
    internal sealed class EncoreHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _payloads = new() { [Rc3] = RtxEncoreIdentityTests.RtxEncoreDll(Rc3), [Rc4] = RtxEncoreIdentityTests.RtxEncoreDll(Rc4, 300_100) };
        public string Latest { get; set; } = Rc3;
        public bool Offline { get; set; }
        public bool CorruptSums { get; set; }
        /// <summary>The release lists no SHA256SUMS: GitHub's digest is the only check.</summary>
        public bool NoSums { get; set; }
        /// <summary>GitHub's digest names other bytes than the zip served.</summary>
        public bool WrongDigest { get; set; }
        public bool OfferAccepted { get; set; } = true;
        public byte[]? PayloadOverride { get; set; }
        public int Requests { get; private set; }

        public byte[] Payload(string version) => PayloadOverride ?? _payloads[version];
        private static string ZipUrl(string version) => $"https://github.com/rhi-tests/rtx-encore/releases/download/v{version}/RTX-Encore-v{version}.zip";
        private static string SumsUrl(string version) => $"https://github.com/rhi-tests/rtx-encore/releases/download/v{version}/SHA256SUMS";
        private byte[] ZipFor(string version) => Zip(("rtx-encore.dll", Payload(version)), ("alternative-proxies/version.dll", Payload(version)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new HttpRequestException("offline");
            var url = request.RequestUri!.ToString();
            byte[]? body = null;
            if (url == RtxEncoreDeploymentService.ReleasesApiUrl)
            {
                var zip = ZipFor(Latest);
                var digest = WrongDigest ? new string('a', 64) : Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
                var sums = NoSums ? "" : $$""",{"name":"SHA256SUMS","browser_download_url":"{{SumsUrl(Latest)}}"}""";
                body = Encoding.UTF8.GetBytes($$"""
                [{"tag_name":"v{{Latest}}","draft":false,"prerelease":true,"assets":[
                  {"name":"RTX-Encore-v{{Latest}}.zip","browser_download_url":"{{ZipUrl(Latest)}}","digest":"sha256:{{digest}}"}{{sums}}]}]
                """);
            }
            else if (url == ZipUrl(Latest)) body = ZipFor(Latest);
            else if (url == SumsUrl(Latest) && !NoSums)
                body = Encoding.ASCII.GetBytes((CorruptSums ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(ZipFor(Latest))).ToLowerInvariant())
                                               + "  RTX-Encore-v" + Latest + ".zip\n");
            else if (url == DlssManagerService.ManifestUrl)
            {
                var accepted = OfferAccepted ? $$""",{"version":"310.8.0 (50xx)","url":"{{NrAcceptedUrl}}"}""" : "";
                body = Encoding.UTF8.GetBytes($$"""{"dlss":[],"dlssd":[],"dlssg":[],"streamline":[],"dlssnr":[{"version":"310.8.3 (50xx)","url":"{{NrOtherUrl}}"}{{accepted}}]}""");
            }
            else if (url == NrAcceptedUrl) body = Zip(("nvngx_dlssnr.dll", NrAccepted));
            else if (url == NrOtherUrl) body = Zip(("nvngx_dlssnr.dll", NrOther));
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }
}
