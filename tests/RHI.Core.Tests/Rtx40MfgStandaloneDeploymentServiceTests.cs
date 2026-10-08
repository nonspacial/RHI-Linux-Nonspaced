using RHI.Core.Deployment;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class Rtx40MfgStandaloneDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-rtx40-mfg-tests-" + Guid.NewGuid().ToString("N"));

    public Rtx40MfgStandaloneDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpstreamContract_UsesSingleDllAndFullProxySet()
    {
        Assert.Equal("RTXMFG.dll", Rtx40MfgStandaloneDeploymentService.InnerDllName);
        Assert.Equal("https://github.com/dashdogy/RTX40MFG-Unlock", Rtx40MfgStandaloneDeploymentService.RepositoryUrl);
        Assert.Equal(
            new[]
            {
                "version.dll", "dinput8.dll", "winmm.dll", "d3d9.dll", "d3d10.dll",
                "d3d11.dll", "d3d12.dll", "dxgi.dll", "dsound.dll", "wininet.dll",
                "winhttp.dll", "binkw64.dll", "bink2w64.dll", "xinput1_1.dll",
                "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll",
                "xinputuap.dll"
            },
            Rtx40MfgStandaloneDeploymentService.ProxyNames);
        Assert.DoesNotContain("binkw64.dll", Rtx40MfgStandaloneDeploymentService.SelectableProxyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("bink2w64.dll", Rtx40MfgStandaloneDeploymentService.SelectableProxyNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_RequiresX64Dx12OrVulkanPrefixAndStreamlineFrameGeneration()
    {
        using var fixture = CreateGame("eligibility", 0x8664, GraphicsApi.DirectX12);
        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.GraphicsApi = GraphicsApi.Vulkan;
        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.GraphicsApi = GraphicsApi.DirectX11;
        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.GraphicsApi = GraphicsApi.DirectX12;
        File.Delete(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"));
        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game));

        File.WriteAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"), [1, 2, 3]);
        fixture.Game.ForcedArchitecture = PeArchitecture.X86;
        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game));
    }

    [Fact]
    public void Eligibility_FindsStreamlineFrameGenerationInInstallRootUnrealPluginTree()
    {
        using var fixture = CreateGame("nested-streamline", 0x8664, GraphicsApi.DirectX12);
        File.Delete(Path.Combine(fixture.Bin, "sl.interposer.dll"));
        File.Delete(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"));

        var streamline = Path.Combine(
            fixture.Root,
            "Engine", "Plugins", "Runtime", "Nvidia", "Streamline",
            "Binaries", "ThirdParty", "Win64");
        Directory.CreateDirectory(streamline);
        File.WriteAllText(Path.Combine(streamline, "sl.interposer.dll"), "streamline");
        File.WriteAllBytes(Path.Combine(streamline, "nvngx_dlssg.dll"), [1, 2, 3, 4]);

        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30));
    }

    [Fact]
    public void Eligibility_AcceptsFunctionalRhiManagedOptiScalerStreamlineDlssEnablerStack()
    {
        using var fixture = CreateGame("managed-opti-fg", 0x8664, GraphicsApi.DirectX12);
        File.Delete(Path.Combine(fixture.Bin, "sl.interposer.dll"));
        File.Delete(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"));

        var optiDir = Path.Combine(fixture.Bin, "OptiScaler");
        var streamlineDir = Path.Combine(optiDir, "Streamline");
        Directory.CreateDirectory(streamlineDir);
        File.WriteAllText(Path.Combine(streamlineDir, "sl.common.dll"), "managed-streamline");
        File.WriteAllText(Path.Combine(optiDir, OptiScalerDeploymentService.DlssEnablerHeadlessFileName), "managed-enabler");
        File.WriteAllText(Path.Combine(fixture.Bin, "OptiScaler.ini"), "[FrameGen]\nFGOutput=dlssg\nFGNvngxReplacement=Arturs\n");
        var record = ManagedOptiScalerRecord(fixture, OptiScalerVariant.Nightly);

        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx40, record));
        Assert.True(Rtx40MfgStandaloneDeploymentService.HasManagedOptiScalerFrameGeneration(record));
    }

    [Fact]
    public void Eligibility_RejectsManagedOptiScalerFilesWhenDlssgEnablerRouteIsNotConfigured()
    {
        using var fixture = CreateGame("managed-opti-not-functional", 0x8664, GraphicsApi.DirectX12);
        File.Delete(Path.Combine(fixture.Bin, "sl.interposer.dll"));
        File.Delete(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"));

        var optiDir = Path.Combine(fixture.Bin, "OptiScaler");
        var streamlineDir = Path.Combine(optiDir, "Streamline");
        Directory.CreateDirectory(streamlineDir);
        File.WriteAllText(Path.Combine(streamlineDir, "sl.common.dll"), "managed-streamline");
        File.WriteAllText(Path.Combine(optiDir, OptiScalerDeploymentService.DlssEnablerHeadlessFileName), "managed-enabler");
        // Since #135 the managed stack also carries nvngx_dlssg.dll; that must not pass as a native integration.
        File.WriteAllBytes(Path.Combine(streamlineDir, "nvngx_dlssg.dll"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(fixture.Bin, "OptiScaler.ini"), "[FrameGen]\nFGOutput=dlssg\nFGNvngxReplacement=None\n");
        var record = ManagedOptiScalerRecord(fixture, OptiScalerVariant.Nightly);

        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx40, record));
        Assert.False(Rtx40MfgStandaloneDeploymentService.HasManagedOptiScalerFrameGeneration(record));
    }

    [Fact]
    public void PreferenceStore_RoundTripsProxyPerGame()
    {
        using var fixture = CreateGame("prefs", 0x8664, GraphicsApi.DirectX12);
        var store = new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json"));
        store.Set(fixture.Game, new Rtx40MfgPreferences { ProxyName = "winhttp.dll" });
        Assert.Equal("winhttp.dll", store.Get(fixture.Game).ProxyName);
    }

    [Fact]
    public void UniversalMfgContract_ExposesRtx40AndRtx30Generations()
    {
        Assert.Equal(
            new[] { Rtx40MfgStandaloneDeploymentService.GpuGenRtx40, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30 },
            Rtx40MfgStandaloneDeploymentService.GpuGenerations);
        Assert.Equal("RTX 40 Series", Rtx40MfgStandaloneDeploymentService.GpuGenRtx40);
        Assert.Equal("RTX 30 Series", Rtx40MfgStandaloneDeploymentService.GpuGenRtx30);
    }

    [Fact]
    public void Eligibility_Rtx30IsDx12OnlyWhileRtx40AlsoAllowsExperimentalVulkan()
    {
        using var fixture = CreateGame("gpu-eligibility", 0x8664, GraphicsApi.Vulkan);

        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx40));
        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30));
        Assert.Contains("DirectX 12", Rtx40MfgStandaloneDeploymentService.EligibilityReason(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30), StringComparison.OrdinalIgnoreCase);

        fixture.Game.GraphicsApi = GraphicsApi.DirectX12;
        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30));
    }

    [Fact]
    public void PreferenceStore_RoundTripsGpuGenerationAndProxy()
    {
        using var fixture = CreateGame("gpu-prefs", 0x8664, GraphicsApi.DirectX12);
        var store = new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json"));
        store.Set(fixture.Game, new Rtx40MfgPreferences
        {
            GpuGeneration = Rtx40MfgStandaloneDeploymentService.GpuGenRtx30,
            ProxyName = "winhttp.dll"
        });

        var read = store.Get(fixture.Game);
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.GpuGenRtx30, read.GpuGeneration);
        Assert.Equal("winhttp.dll", read.ProxyName);
    }

    [Fact]
    public async Task Install_Rtx30PersistsUniversalGenerationMetadata()
    {
        using var fixture = CreateGame("rtx30-universal", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x39)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True(service.SetPreferences(
            fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30, "version.dll").Success);

        var result = await service.InstallAsync(fixture.Game);

        Assert.True(result.Success, result.Message);
        Assert.Contains("Universal MFG", result.Message, StringComparison.OrdinalIgnoreCase);
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.GpuGenRtx30, record.Metadata["UniversalMFG.GpuGeneration"]);
        Assert.Equal("version.dll", record.Metadata["UniversalMFG.ProxyName"]);
    }

    [Fact]
    public void LegacyV020RecordWithoutGpuGenerationDefaultsToRtx40()
    {
        using var fixture = CreateGame("legacy-v020", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "version.dll");
        File.WriteAllBytes(target, CreatePe(0x8664, 512 * 1024, 0x3A));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.Rtx40MfgStandalone,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = Sha256(target) }],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RTX40MFG.ProxyName"] = "version.dll"
            }
        });
        using var http = new HttpClient(Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x3B))))));
        var service = CreateService(fixture, http, records);

        var preferences = service.GetPreferences(fixture.Game);

        Assert.Equal(Rtx40MfgStandaloneDeploymentService.GpuGenRtx40, preferences.GpuGeneration);
        Assert.Equal("version.dll", preferences.ProxyName);
    }

    [Fact]
    public async Task ResolveLatest_UsesStableReleaseZipDigestAndChecksumAsset()
    {
        using var fixture = CreateGame("resolve", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.1", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x31)))),
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x32)))));
        handler.ActiveVersion = "1.3.2";
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("1.3.2", release.Version);
        Assert.Equal("v1.3.2", release.Tag);
        Assert.Equal("RTXMFG-v1.3.2.zip", release.AssetName);
        Assert.Equal(handler.DigestFor("1.3.2"), release.AssetSha256);
        Assert.Equal(Rtx40Handler.ChecksumsUrl("1.3.2"), release.ChecksumsUrl);
    }

    [Fact]
    public async Task ResolveLatest_AcceptsPublishedStableHotfixTagWithoutCollapsingIdentity()
    {
        using var fixture = CreateGame("resolve-hotfix", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.3-hotfix.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x33)))));
        handler.ActiveVersion = "1.3.3-hotfix.2";
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("1.3.3-hotfix.2", release.Version);
        Assert.Equal("v1.3.3-hotfix.2", release.Tag);
        Assert.Equal("RTXMFG-v1.3.3-hotfix.2.zip", release.AssetName);
        Assert.Equal(handler.DigestFor("1.3.3-hotfix.2"), release.AssetSha256);
    }

    [Theory]
    [InlineData("v1.3.3/hotfix.2")]
    [InlineData("v1.3.3 hotfix.2")]
    [InlineData("v../1.3.3")]
    [InlineData("vhotfix")]
    public void NormalizePublishedReleaseVersion_RejectsUnsafeOrNonVersionTags(string tag)
    {
        Assert.Throws<InvalidDataException>(() =>
            Rtx40MfgStandaloneDeploymentService.NormalizePublishedReleaseVersion(tag));
    }

    [Fact]
    public async Task InstallAndRemove_DeploysProxyOwnershipAndWineOverride()
    {
        using var fixture = CreateGame("lifecycle", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x41)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");
        Assert.True(install.Success, install.Message);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        Assert.True(File.Exists(target));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone);
        Assert.NotNull(record);
        Assert.Equal("1.3.2", record!.Version);
        Assert.Equal("winmm.dll", record.Metadata["RTX40MFG.ProxyName"]);
        Assert.Equal(Sha256(target), record.Files.Single().Sha256, ignoreCase: true);
        DownloadRecordAssert.Recorded(record, Sha256(target), VerifiedDownload.PublishedDigest);

        var remove = await service.RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // w2 wine review, minor 2 (N9): refused while Wine runs in the prefix, before the download or any change.
    [Fact]
    public async Task Install_WhileWineRunsInThePrefix_IsRefusedBeforeTheDownload()
    {
        using var fixture = CreateGame("wine-running", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x42)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var userReg = File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg"));
        var service = new Rtx40MfgStandaloneDeploymentService(http, records, new WineDllOverrideService(new WineRegistryEditor(_ => true)),
            new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.False(install.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, install.Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal(userReg, File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // w2 wine review, minor 2 (N6): a removal failing after it restored the override puts RHI's value back (the record stays).
    [Fact]
    public async Task Remove_FailingAfterTheOverrideWasRestored_PutsRhisOverrideBack()
    {
        using var fixture = CreateGame("remove-fails", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x43)))));
        using var http = new HttpClient(handler);
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var proxyBytes = File.ReadAllBytes(target);

        ComponentOperationResult remove;
        using (LedgerWriteBlocker.Block(records, ledger))
            remove = await service.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(proxyBytes, File.ReadAllBytes(target));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    [Fact]
    public async Task Install_UnreadablePreferenceFile_RefusesBeforeDownloadOrOverride()
    {
        // Refs #57: the preference save comes last; an unreadable file must stop the install up front.
        using var fixture = CreateGame("unreadable-prefs", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Root, "prefs.json"), "{ not json");
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x43)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.False(result.Success);
        Assert.Contains("prefs.json", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(fixture.Root, "prefs.json")));
    }

    [Fact]
    public async Task Install_BlocksExternalProxyBeforeNetwork()
    {
        using var fixture = CreateGame("foreign", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Bin, "dxgi.dll"), "foreign");
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x42)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "dxgi.dll");

        Assert.False(result.Success);
        Assert.Contains("não sobrescreve", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_BlocksExternalProxyCaseVariantBeforeNetwork()
    {
        using var fixture = CreateGame("foreign-case", 0x8664, GraphicsApi.DirectX12);
        var external = Path.Combine(fixture.Bin, "DxGi.DlL");
        File.WriteAllText(external, "foreign-case-variant");
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x52)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "dxgi.dll");

        Assert.False(result.Success);
        Assert.Contains("não sobrescreve", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(File.Exists(external));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "dxgi.dll")));
    }

    [Fact]
    public async Task Install_BlocksAnotherRhiManagedProxyBeforeNetwork()
    {
        using var fixture = CreateGame("owned", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "winhttp.dll");
        File.WriteAllText(target, "owned-by-other-rhi-component");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = "deadbeef" }]
        });
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x43)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winhttp.dll");

        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_BlocksRhiManagedProxyCaseVariantBeforeNetwork()
    {
        using var fixture = CreateGame("owned-case", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "WinHttp.DLL");
        File.WriteAllText(target, "owned-case-variant");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = Sha256(target) }]
        });
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x53)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winhttp.dll");

        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winhttp.dll")));
    }

    [Fact]
    public async Task Install_BlocksOtherFrameGenerationAuthoritiesBeforeNetwork()
    {
        foreach (var conflict in new[] { ManagedComponentKind.MfgAdaUnlock, ManagedComponentKind.Dlssg2030Unlock, ManagedComponentKind.ShortFuse })
        {
            using var fixture = CreateGame("conflict-" + conflict, 0x8664, GraphicsApi.DirectX12);
            var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
                GameName = fixture.Game.Name,
                Component = conflict,
                BinaryDirectory = fixture.Bin
            });
            using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x44)))));
            using var http = new HttpClient(handler);
            var service = CreateService(fixture, http, records);

            var result = await service.InstallAsync(fixture.Game, "version.dll");

            Assert.False(result.Success);
            Assert.Contains("Remova", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.RequestCount);
        }
    }

    [Fact]
    public async Task BinkProxy_IsUpstreamKnownButBlockedUntilChainingIsImplemented()
    {
        using var fixture = CreateGame("bink", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x45)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "binkw64.dll");

        Assert.False(result.Success);
        Assert.Contains("Bink", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_RejectsX86PayloadAndLeavesPrefixUntouched()
    {
        using var fixture = CreateGame("x86-payload", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x014c, 512 * 1024, 0x46)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("x64 PE", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    [Fact]
    public async Task Remove_ModifiedManagedProxyBlocksBeforeOverrideTeardown()
    {
        using var fixture = CreateGame("modified", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x47)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        File.AppendAllText(Path.Combine(fixture.Bin, "version.dll"), "external-change");

        var result = await service.RemoveAsync(fixture.Game);

        Assert.False(result.Success);
        Assert.Contains("modificada fora", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    [Fact]
    public async Task Update_ReplacesManagedPayloadKeepsProxyAndOverride()
    {
        using var fixture = CreateGame("update", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.1", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x48)))),
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x49)))));
        handler.ActiveVersion = "1.3.1";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "wininet.dll")).Success);
        var target = Path.Combine(fixture.Bin, "wininet.dll");
        var before = Sha256(target);

        handler.ActiveVersion = "1.3.2";
        var result = await service.UpdateAsync(fixture.Game);

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(before, Sha256(target));
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal("1.3.2", record.Version);
        Assert.Equal("wininet.dll", record.Metadata["RTX40MFG.ProxyName"]);
        DownloadRecordAssert.Recorded(record, Sha256(target), VerifiedDownload.PublishedDigest);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "wininet", "Game.exe"));
    }

    [Fact]
    public async Task Install_CoexistsWithStandaloneDlssEnablerWhenProxySlotsDiffer()
    {
        using var fixture = CreateGame("coexist", 0x8664, GraphicsApi.DirectX12);
        var dlssProxy = Path.Combine(fixture.Bin, "winhttp.dll");
        File.WriteAllText(dlssProxy, "dlss-enabler-owned");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.DlssEnablerStandalone,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = dlssProxy, Sha256 = Sha256(dlssProxy) }],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DLSSEnabler.ProxyName"] = "winhttp.dll" }
        });
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4A)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    [Fact]
    public async Task Install_BlocksSameProxyCollisionWithStandaloneDlssEnabler()
    {
        using var fixture = CreateGame("same-slot", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "version.dll");
        File.WriteAllText(target, "dlss-enabler-owned");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.DlssEnablerStandalone,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = Sha256(target) }]
        });
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4B)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Archive_MultipleRtxMfgDllEntriesFailsClosedBeforeGameMutation()
    {
        using var fixture = CreateGame("ambiguous", 0x8664, GraphicsApi.DirectX12);
        var pe = CreatePe(0x8664, 512 * 1024, 0x4C);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("a/RTXMFG.dll", pe), ("b/RTXMFG.dll", pe))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    [Fact]
    public async Task MissingPublishedChecksumAuthority_BlocksBeforeAssetDownload()
    {
        using var fixture = CreateGame("no-checksum-authority", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4F)))));
        handler.OmitDigest = true;
        handler.OmitChecksumsAsset = true;
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    [Fact]
    public async Task PublishedDigestMismatch_BlocksBeforeGameMutation()
    {
        using var fixture = CreateGame("digest", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4D)))));
        handler.DigestOverride = new string('0', 64);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    [Fact]
    public async Task Sha256SumsMismatch_BlocksBeforeGameMutation()
    {
        using var fixture = CreateGame("checksum", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4E)))));
        handler.ChecksumOverride = new string('f', 64);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("SHA256SUMS", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    private static LinuxDeploymentRecord ManagedOptiScalerRecord(GameFixture fixture, OptiScalerVariant variant)
        => new()
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = fixture.Bin,
            Version = "test",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OptiScalerDeploymentService.VariantMetadataKey] = variant.ToString(),
                [OptiScalerDeploymentService.FgStackEnabledMetadataKey] = "1"
            }
        };

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var fixture = CreateGame("cancel-install", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4A)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(fixture.Game, "winmm.dll", ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // w2/vmsplit re-review, minor 2: the removal closes the Cancel gate before its first change, so a Cancel pressed
    // while it writes is refused and its real outcome is reported (it used to be accepted and read "Operation cancelled.").
    [Fact]
    public async Task Remove_EntersItsWritePhaseBeforeTheFirstChange_AndRunsToTheEnd()
    {
        using var fixture = CreateGame("cancel-remove", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4D)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
    }

    [Fact]
    public async Task Update_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var fixture = CreateGame("cancel-update", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.1", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4B)))),
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x4C)))));
        handler.ActiveVersion = "1.3.1";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "wininet.dll")).Success);
        handler.ActiveVersion = "1.3.2";

        var run = await CancelAtWritePhase.RunAsync(ct => service.UpdateAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Equal("1.3.2", records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!.Version);
    }

    private Rtx40MfgStandaloneDeploymentService CreateService(GameFixture fixture, HttpClient http, LinuxDeploymentRecordStore records)
        => new(
            http,
            records,
            new WineDllOverrideService(),
            new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")),
            Path.Combine(fixture.Root, "cache"));

    private GameFixture CreateGame(string name, ushort machine, GraphicsApi api)
    {
        var root = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, CreatePe(machine, 64 * 1024, 0x11));
        File.WriteAllText(Path.Combine(bin, "sl.interposer.dll"), "streamline");
        File.WriteAllBytes(Path.Combine(bin, "nvngx_dlssg.dll"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return new GameFixture(root, bin, prefix, new DetectedGame
        {
            Name = "RTX40 MFG Test " + name,
            InstallPath = root,
            Source = "Test",
            Runner = "Wine",
            PrefixPath = prefix,
            ExecutablePath = exe,
            BinaryDirectory = bin,
            GraphicsApi = api
        });
    }

    // Final detect-deploy review, nit 5 (Refs #42): the Universal MFG (RTXMFG) payload leaves its downloaded ZIP through the checked
    // extraction, never ExtractToFile: the archive's Unix mode bits are not applied, and an entry declaring more than
    // the 256 MiB add-on cap is refused before anything is written.
    private static readonly int SetUidAndAllPermissions = Convert.ToInt32("104777", 8) << 16;
    private const UnixFileMode Dangerous = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                                           | UnixFileMode.SetUser | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite;

    [Fact]
    public async Task ArchivePayload_ModeBitsAreNotApplied()
    {
        using var fixture = CreateGame("archive-mode", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", DownloadedArchiveEntryTests.Zip("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x5A), SetUidAndAllPermissions)));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(result.Success, result.Message);
        var written = Directory.EnumerateFiles(Path.Combine(fixture.Root, "cache"), "*.dll", SearchOption.AllDirectories)
            .Append(Path.Combine(fixture.Bin, "winmm.dll")).ToList();
        Assert.All(written, file => Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & Dangerous));
    }

    [Fact]
    public async Task ArchivePayload_DeclaringMoreThanTheCap_IsRefusedBeforeWriting()
    {
        const long cap = 256L * 1024 * 1024;
        using var fixture = CreateGame("archive-cap", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", DownloadedArchiveEntryTests.Zip("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x5B), 0, declaredLength: cap + 1)));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.False(result.Success);
        Assert.Contains($"RTXMFG.dll: declared size over the limit ({cap + 1} > {cap} bytes)", result.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "cache"), "*.dll", SearchOption.AllDirectories));
    }

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = Enumerable.Repeat(marker, size).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] CreateZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes, 0, bytes.Length);
            }
        }
        return memory.ToArray();
    }

    private static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed record GameFixture(string Root, string Bin, string Prefix, DetectedGame Game) : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Rtx40Handler : HttpMessageHandler, IDisposable
    {
        private readonly Dictionary<string, byte[]> _assets;
        public string ActiveVersion { get; set; }
        public string? DigestOverride { get; set; }
        public string? ChecksumOverride { get; set; }
        public bool OmitDigest { get; set; }
        public bool OmitChecksumsAsset { get; set; }
        public int RequestCount { get; private set; }

        private Rtx40Handler(Dictionary<string, byte[]> assets, string activeVersion)
        {
            _assets = assets;
            ActiveVersion = activeVersion;
        }

        public static Rtx40Handler WithVersions(params (string Version, byte[] Asset)[] versions)
        {
            if (versions.Length == 0) throw new ArgumentException("At least one fake release is required.", nameof(versions));
            return new Rtx40Handler(
                versions.ToDictionary(item => item.Version, item => item.Asset, StringComparer.OrdinalIgnoreCase),
                versions[^1].Version);
        }

        public string DigestFor(string version) => Sha256(_assets[version]);
        public static string AssetUrl(string version) => $"https://github.com/rhi-tests/RTXMFG-v{version}.zip";
        public static string ChecksumsUrl(string version) => $"https://github.com/rhi-tests/v{version}/SHA256SUMS.txt";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();
            if (url == Rtx40MfgStandaloneDeploymentService.ReleasesApiUrl)
                return Task.FromResult(JsonResponse(BuildLatestRelease()));
            if (url == AssetUrl(ActiveVersion))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_assets[ActiveVersion]) });
            if (url == ChecksumsUrl(ActiveVersion))
            {
                var hash = ChecksumOverride ?? DigestFor(ActiveVersion);
                var text = $"{hash}  RTXMFG-v{ActiveVersion}.zip\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string BuildLatestRelease()
        {
            var digest = DigestOverride ?? DigestFor(ActiveVersion);
            var digestProperty = OmitDigest ? string.Empty : $",\n                  \"digest\":\"sha256:{digest}\"";
            var checksumAsset = OmitChecksumsAsset
                ? string.Empty
                : $$"""
                ,
                {
                  "name":"SHA256SUMS.txt",
                  "browser_download_url":"{{ChecksumsUrl(ActiveVersion)}}",
                  "digest":"sha256:{{new string('a', 64)}}"
                }
                """;
            return $$"""
            {
              "tag_name":"v{{ActiveVersion}}",
              "draft":false,
              "prerelease":false,
              "assets":[
                {
                  "name":"RTXMFG-v{{ActiveVersion}}.zip",
                  "browser_download_url":"{{AssetUrl(ActiveVersion)}}"{{digestProperty}}
                }{{checksumAsset}}
              ]
            }
            """;
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
