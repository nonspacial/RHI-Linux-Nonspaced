using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #92: ReShade as a Vulkan layer under Wine/Proton (spike reviews/w5-92-spike.md). Offered only on Vulkan games, only on
// request; the Khronos loader beside the exe, the layer in the prefix's ProgramData, registered in system.reg (HKLM).
public sealed partial class ReShadeChannelTests
{
    private const string SystemRegText = "WINE REGISTRY Version 2\n;; All keys relative to \\\\Machine\n\n#arch=win64\n\n[Software\\\\Wine] 1700000000\n#time=1d000000000000\n\"Version\"=\"win10\"\n";

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateVulkanGame(string name, bool is32 = false, string? protonMarker = "GE-Proton10-18")
    {
        var (game, store) = CreateGame(name);
        if (is32) PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x014c);
        game.GraphicsApi = GraphicsApi.Vulkan;
        game.GraphicsApis = [GraphicsApi.Vulkan];
        game.GraphicsApiConfidence = GraphicsApiConfidence.High;
        File.WriteAllText(Path.Combine(game.PrefixPath!, "system.reg"), SystemRegText);
        if (protonMarker is not null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(game.PrefixPath!)!, "version"), protonMarker + "\n");
        return (game, store);
    }

    private ReShadeDeploymentService NewVulkanService(LinuxDeploymentRecordStore store, LunarGHandler handler, string cache, WineDllOverrideService? overrides = null, DeploymentJournal? journal = null)
    {
        var http = new HttpClient(handler);
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, overrides ?? new WineDllOverrideService(),
            new FakeExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);
    }

    private static ReShadeInstallOptions LayerOptions() => new() { VulkanLayer = true };

    private static string LayerFolder(DetectedGame game) => ReShadeVulkanLayer.LayerFolder(game).LinuxPath;

    [Fact]
    public async Task VulkanLayer_Install_PlacesLoaderLayerManifestAndIni_RegistersInSystemRegAndOverridesTheLoader()
    {
        var (game, store) = CreateVulkanGame("vk-install");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-install-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(ReShadeVulkanLayer.InstalledPrefix, result.Message, StringComparison.Ordinal);
        var loader = Path.Combine(game.BinaryDirectory!, "vulkan-1.dll");
        Assert.Equal(LunarGHandler.Loader(x86: false), File.ReadAllBytes(loader));
        var folder = LayerFolder(game);
        Assert.StartsWith(Path.Combine(game.PrefixPath!, "drive_c", "ProgramData", "RHI-Linux", "ReShadeVulkan") + "/", folder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "ReShade64.dll")));
        var manifest = File.ReadAllText(Path.Combine(folder, "ReShade64.json"));
        Assert.Contains("\"library_path\": \".\\\\ReShade64.dll\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"VK_LAYER_reshade\"", manifest, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));

        var id = Path.GetFileName(folder);
        var systemReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg"));
        Assert.Contains("[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers] ", systemReg, StringComparison.Ordinal);
        Assert.Contains($"\"C:\\\\ProgramData\\\\RHI-Linux\\\\ReShadeVulkan\\\\{id}\\\\ReShade64.json\"=dword:00000000", systemReg, StringComparison.Ordinal);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), "vulkan-1", "Game.exe"));

        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(record));
        Assert.Equal("1.4.363.0", record.Metadata[ReShadeVulkanLayer.MetaLoaderVersion]);
        Assert.Equal(VerifiedDownload.RecordedOnly, record.Metadata["Download.VulkanLoader.Integrity"]);
        Assert.Single(record.RegistryValues);
        Assert.Equal(WineRegistryValueService.SystemHive, record.RegistryValues[0].Hive);
        Assert.True(service.GetInstalledOptions(game).VulkanLayer);
    }

    [Fact]
    public async Task VulkanLayer_Install_32BitGame_UsesWow6432NodeReShade32AndTheX86Loader()
    {
        var (game, store) = CreateVulkanGame("vk-32", is32: true);
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-32-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.True(result.Success, result.Message);
        Assert.Equal(LunarGHandler.Loader(x86: true), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        var folder = LayerFolder(game);
        Assert.True(File.Exists(Path.Combine(folder, "ReShade32.dll")));
        Assert.Contains("\".\\\\ReShade32.dll\"", File.ReadAllText(Path.Combine(folder, "ReShade32.json")), StringComparison.Ordinal);
        var systemReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg"));
        Assert.Contains("[Software\\\\Wow6432Node\\\\Khronos\\\\Vulkan\\\\ImplicitLayers] ", systemReg, StringComparison.Ordinal);
        Assert.DoesNotContain("[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers]", systemReg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VulkanLayer_Remove_TakesEverythingAway_AndGivesBothHivesBackByteForByte()
    {
        var (game, store) = CreateVulkanGame("vk-remove");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-remove-cache"));
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));
        var userBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(ReShadeVulkanLayer.RemovedMessage, removed.Message);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
        Assert.Equal(userBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        // The folders RHI created in the prefix go too, up to drive_c it found missing.
        Assert.False(Directory.Exists(Path.Combine(game.PrefixPath!, "drive_c")));
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task VulkanLayer_Remove_KeepsARegistrationTheUserChangedAndSaysSo()
    {
        var (game, store) = CreateVulkanGame("vk-keep");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-keep-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        var systemReg = Path.Combine(game.PrefixPath!, "system.reg");
        File.WriteAllText(systemReg, File.ReadAllText(systemReg).Replace("=dword:00000000", "=dword:00000001", StringComparison.Ordinal));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.Contains("=dword:00000001", File.ReadAllText(systemReg), StringComparison.Ordinal);
        Assert.Contains("= 'dword:00000001'.", removed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VulkanLayer_Install_FailingAfterTheFirstWrite_RollsEverythingBack()
    {
        var (game, store) = CreateVulkanGame("vk-fail");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-fail-cache"));
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));
        var userBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg"));
        service.AfterVulkanLayerPlacedForTesting = () => throw new IOException("disk full");

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.StartsWith(ReShadeVulkanLayer.FailedRolledBackPrefix + "disk full", result.Message, StringComparison.Ordinal);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
        Assert.Equal(userBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        Assert.False(Directory.Exists(Path.Combine(game.PrefixPath!, "drive_c")));
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task VulkanLayer_Install_FailingAfterTheRegistryWrites_RollsTheHivesBack()
    {
        var (game, store) = CreateVulkanGame("vk-fail-late");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-fail-late-cache"));
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));
        var userBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg"));
        store.SaveFaultForTesting = () => new IOException("ledger write failed");

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
        Assert.Equal(userBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
    }

    public static TheoryData<string, string?, string?> RefusedGames => new()
    {
        // case, Proton marker, expected message start (null = NotVulkanTarget)
        { "multi-renderer", "GE-Proton10-18", null },
        { "old-proton", "GE-Proton7-55", ReShadeVulkanLayer.RunnerTooOldPrefix + "GE-Proton7-55" },
        { "unknown-runner", null, ReShadeVulkanLayer.UnknownRunnerMessage },
        { "prefix-not-started", "GE-Proton10-18", ReShadeVulkanLayer.PrefixNotStartedMessage },
        { "native", "GE-Proton10-18", null },
    };

    [Theory]
    [MemberData(nameof(RefusedGames))]
    public async Task VulkanLayer_Install_RefusesWithAPlainReason_BeforeAnyDownloadOrChange(string scenario, string? marker, string? expected)
    {
        var (game, store) = CreateVulkanGame("vk-refused-" + scenario, protonMarker: marker);
        if (scenario == "multi-renderer")
        {
            game.GraphicsApis = [GraphicsApi.Vulkan, GraphicsApi.DirectX11];
            game.GraphicsApiConfidence = GraphicsApiConfidence.Medium;
        }
        if (scenario == "prefix-not-started") File.Delete(Path.Combine(game.PrefixPath!, "system.reg"));
        if (scenario == "native") game.Runner = "Native Linux";
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-refused-" + scenario + "-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        if (scenario == "native") Assert.Equal("ReShade DLL deployment is only for Windows games running through Wine/Proton.", result.Message);
        else Assert.Equal(expected ?? ReShadeVulkanLayer.NotVulkanTargetMessage, result.Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task VulkanLayer_UserApiOverrideToVulkan_MakesAMultiRendererGameEligible()
    {
        var (game, store) = CreateVulkanGame("vk-override");
        game.GraphicsApi = GraphicsApi.DirectX11;
        game.GraphicsApis = [GraphicsApi.DirectX11, GraphicsApi.Vulkan];
        game.GraphicsApiConfidence = GraphicsApiConfidence.Medium;
        game.UserGraphicsApiOverride = GraphicsApi.Vulkan;
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-override-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task VulkanLayer_Install_RefusesAGameThatShipsItsOwnLoader()
    {
        var (game, store) = CreateVulkanGame("vk-own-loader");
        var own = Path.Combine(game.BinaryDirectory!, "Vulkan-1.dll");
        File.WriteAllText(own, "the game's own loader");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-own-loader-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.Equal(ReShadeVulkanLayer.GameShipsLoaderPrefix + own, result.Message);
        Assert.Equal("the game's own loader", File.ReadAllText(own));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task VulkanGame_PlainInstall_IsNeverTheLayer_ItAsksForTheExplicitChoice()
    {
        // A dependency install (RenoDX, Luma, ShortFuse) asks without the layer choice: never installed by default.
        var (game, store) = CreateVulkanGame("vk-plain");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-plain-cache"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Equal(ReShadeVulkanLayer.NotOnRequestMessage, result.Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task VulkanLayer_IsRefused_WhileADllReShadeIsInstalled()
    {
        var (game, store) = CreateVulkanGame("vk-one-reshade");
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-one-reshade-cache"));
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        game.UserGraphicsApiOverride = GraphicsApi.Vulkan;

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.Equal(ReShadeVulkanLayer.DllReShadeInstalledMessage, result.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
    }

    [Fact]
    public async Task VulkanLayer_Update_KeepsTheLayerPath_EvenWhenTheApiNoLongerSaysVulkan()
    {
        var (game, store) = CreateVulkanGame("vk-update-path");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-update-path-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        game.UserGraphicsApiOverride = GraphicsApi.DirectX12;
        handler.Inner.StableVersion = "10.0.0";

        var update = await service.UpdateAsync(game);

        Assert.True(update.Success, update.Message);
        Assert.StartsWith(ReShadeVulkanLayer.UpdatedPrefix + "10.0.0", update.Message, StringComparison.Ordinal);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(store.Find(game, ManagedComponentKind.ReShade)));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    [Fact]
    public async Task VulkanLayer_AnInstallAskedWithoutTheLayerChoice_KeepsAnInstalledLayerALayer()
    {
        // Any caller that passes plain options (a dependency install, a tool) updates the installed layer in place.
        var (game, store) = CreateVulkanGame("vk-plain-update");
        game.UserGraphicsApiOverride = GraphicsApi.Vulkan;
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-plain-update-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        var result = await service.InstallAsync(game, new ReShadeInstallOptions());

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(ReShadeVulkanLayer.UpdatedPrefix, result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    [Fact]
    public async Task VulkanLayer_Update_SwapsTheLoaderOnlyWhenLunarGListsANewerOne()
    {
        var (game, store) = CreateVulkanGame("vk-loader-update");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-loader-update-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        var loader = Path.Combine(game.BinaryDirectory!, "vulkan-1.dll");
        handler.Inner.StableVersion = "10.0.0";

        var same = await service.UpdateAsync(game);
        Assert.True(same.Success, same.Message);
        Assert.DoesNotContain(ReShadeVulkanLayer.LoaderUpdatedNote, same.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.ZipRequests);

        handler.LatestVersion = "1.4.370.0";
        handler.Inner.StableVersion = "10.0.1";
        var newer = await service.UpdateAsync(game);

        Assert.True(newer.Success, newer.Message);
        Assert.Contains(ReShadeVulkanLayer.LoaderUpdatedNote, newer.Message, StringComparison.Ordinal);
        Assert.Equal(LunarGHandler.Loader(x86: false, "1.4.370.0"), File.ReadAllBytes(loader));
        Assert.Equal("1.4.370.0", store.Find(game, ManagedComponentKind.ReShade)!.Metadata[ReShadeVulkanLayer.MetaLoaderVersion]);

        handler.LatestUnreachable = true;
        handler.Inner.StableVersion = "10.0.2";
        var offline = await service.UpdateAsync(game);
        Assert.True(offline.Success, offline.Message);
        Assert.Contains(ReShadeVulkanLayer.LoaderCheckSkippedNote, offline.Message, StringComparison.Ordinal);
        Assert.Equal(LunarGHandler.Loader(x86: false, "1.4.370.0"), File.ReadAllBytes(loader));
    }

    [Fact]
    public async Task VulkanLayer_Install_RefusesWhileWineRunsInThePrefix()
    {
        var (game, store) = CreateVulkanGame("vk-wine-running");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-wine-running-cache"), new WineDllOverrideService(new WineRegistryEditor(_ => true)));
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, result.Message);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task VulkanLayer_Install_RefusesALoaderThatIsNotTheVulkanLoader()
    {
        var (game, store) = CreateVulkanGame("vk-bad-loader");
        var handler = new LunarGHandler { LoaderDescription = "Something Else" };
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-bad-loader-cache"));

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.False(result.Success);
        Assert.Contains(VulkanRuntimeProvider.LoaderInvalidPrefix, result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
    }

    [Fact]
    public void VulkanLayer_LunarGPolicy_AllowsOnlyLunarGHostsOverHttps()
    {
        var policy = DownloadSources.LunarGVulkanRuntime;
        Assert.True(policy.IsTrusted(VulkanRuntimeProvider.RuntimeUrl("1.4.363.0")));
        Assert.True(policy.IsTrusted(VulkanRuntimeProvider.LatestUrl));
        Assert.False(policy.IsTrusted("http://sdk.lunarg.com/sdk/download/1.4.363.0/windows/x.zip"));
        Assert.False(policy.IsTrusted("https://github.com/KhronosGroup/Vulkan-Loader/releases/x.zip"));
        Assert.Equal(128L * 1024 * 1024, policy.MaxBytes);
    }

    [Fact]
    public void VulkanRow_OnAVulkanGame_IsTheExplicitLayerChoice_AndSaysWhyWhenItCannotBeOffered()
    {
        var (game, store) = CreateVulkanGame("vk-row");
        var components = ManagedComponents(store, NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-row-cache")));
        // Only the ReShade row: the fixture service has no RenoDX/OptiScaler/... services to decorate the others.
        IReadOnlyList<ManagedComponent> Rows(params string[] files)
        {
            var rows = ComponentProbe.Probe(game, files).Where(c => c.Kind == ManagedComponentKind.ReShade).ToList();
            components.Decorate(game, rows);
            return rows;
        }
        var row = Rows().Single();
        Assert.Equal(ReShadeVulkanLayer.RowName, row.DisplayName);
        Assert.True(row.ActionAllowed);
        Assert.Equal(ReShadeVulkanLayer.RowNote, row.Note);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(game.PrefixPath!)!, "version"), "GE-Proton7-55\n");
        row = Rows().Single();
        Assert.False(row.ActionAllowed);
        Assert.Equal(ReShadeVulkanLayer.RunnerTooOldPrefix + "GE-Proton7-55", row.Note);

        // A DirectX game keeps the plain ReShade row.
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;
        Assert.Equal("ReShade", Rows().Single().DisplayName);
    }

    [Fact]
    public async Task VulkanRow_InstalledLayer_ShowsInstalledByRhi_AndShortFuseOrOptiScalerAreRefused()
    {
        var (game, store) = CreateVulkanGame("vk-row-installed");
        var reshade = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-row-installed-cache"));
        var components = ManagedComponents(store, reshade);
        Assert.True((await components.InstallAsync(game, ManagedComponentKind.ReShade, [])).Success);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(store.Find(game, ManagedComponentKind.ReShade)));

        // The record decides (the layer itself sits in the prefix): Installed even with no footprint in the probe's index.
        var rows = ComponentProbe.Probe(game, []).Where(c => c.Kind == ManagedComponentKind.ReShade).ToList();
        components.Decorate(game, rows);
        var row = rows.Single();
        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(row.ManagedByRhi);
        Assert.Equal(ReShadeVulkanLayer.RowName, row.DisplayName);

        var shortFuse = await components.InstallAsync(game, ManagedComponentKind.ShortFuse, []);
        Assert.False(shortFuse.Success);
        Assert.StartsWith(ReShadeVulkanLayer.NeedsDllReShadePrefix, shortFuse.Message, StringComparison.Ordinal);
        var optiScaler = await components.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, null);
        Assert.False(optiScaler.Success);
        Assert.StartsWith(ReShadeVulkanLayer.NeedsDllReShadePrefix, optiScaler.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VulkanGame_ProfileApply_IsTheExplicitChoiceToo_AndNeedsNoD3DCompiler()
    {
        var (game, store) = CreateVulkanGame("vk-apply");
        var cache = Path.Combine(_root, "vk-apply-cache");
        SeedShaderCache(cache, "CrosireMaster");
        // The fixture service has no D3DCompiler helper: a DirectX-style compiler step would fail the apply.
        var components = ManagedComponents(store, NewVulkanService(store, new LunarGHandler(), cache));

        var result = await components.ApplyReShadeAsync(game, new ReShadeInstallOptions { ShaderPackIds = ["CrosireMaster"] });

        Assert.True(result.Success, result.Message);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(store.Find(game, ManagedComponentKind.ReShade)));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "D3DCompiler_47.dll")));
        Assert.True(Directory.Exists(Path.Combine(game.BinaryDirectory!, "reshade-shaders")));
    }

    [Fact]
    public void ComponentProbe_SeesTheLayerFootprint_OnlyWithTheLoaderReShadeIniAndThisGamesLayerInThePrefix()
    {
        var (game, _) = CreateVulkanGame("vk-probe");
        var bin = game.BinaryDirectory!;
        bool ReShadeFound(DetectedGame target, params string[] names)
            => ComponentProbe.Probe(target, names.Select(name => Path.Combine(bin, name)).ToArray())
                .Single(c => c.Kind == ManagedComponentKind.ReShade).Availability == ComponentAvailability.Installed;

        // The loader and ReShade.ini alone are not ReShade: a game may ship its own vulkan-1.dll and keep a ReShade.ini.
        Assert.False(ReShadeFound(game, "vulkan-1.dll", "ReShade.ini"));

        // Another game's layer in the same (shared) prefix does not count for this one.
        var other = game.ShallowClone();
        other.SteamAppId = 4343;
        Directory.CreateDirectory(LayerFolder(other));
        File.WriteAllText(Path.Combine(LayerFolder(other), "ReShade64.json"), "{}");
        Assert.False(ReShadeFound(game, "vulkan-1.dll", "ReShade.ini"));
        Assert.True(ReShadeFound(other, "vulkan-1.dll", "ReShade.ini"));

        Directory.CreateDirectory(LayerFolder(game));
        File.WriteAllText(Path.Combine(LayerFolder(game), "ReShade32.json"), "{}");
        Assert.True(ReShadeFound(game, "vulkan-1.dll", "ReShade.ini"));
        Assert.False(ReShadeFound(game, "vulkan-1.dll"));
        Assert.False(ReShadeFound(game, "ReShade.ini"));
        Assert.False(ComponentProbe.Probe(game, [Path.Combine(bin, "sub", "vulkan-1.dll"), Path.Combine(bin, "ReShade.ini")])
            .Single(c => c.Kind == ManagedComponentKind.ReShade).Availability == ComponentAvailability.Installed);
    }

    [Fact]
    public void ComponentProbe_ADirectXGameWithItsOwnVulkanLoader_IsNotLockedByTheReShadeIniItKeptAfterARemoval()
    {
        // Review w5 finding 1: a DX11 game shipping vulkan-1.dll; the DLL ReShade is removed and its edited ReShade.ini kept.
        var (game, store) = CreateGame("vk-dx-own-loader");
        game.GraphicsApi = GraphicsApi.DirectX11;
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll"), [0x4d, 0x5a, 1, 2]);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\mine\n");

        var row = ComponentProbe.Probe(game, GameFileIndexer.Build(game.InstallPath)).Single(c => c.Kind == ManagedComponentKind.ReShade);

        Assert.NotEqual(ComponentAvailability.Installed, row.Availability);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task ComponentProbe_SeesALayerTheInstallLeft_WhenItsRecordIsGone()
    {
        var (game, store) = CreateVulkanGame("vk-probe-orphan");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-probe-orphan-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        store.Remove(game, ManagedComponentKind.ReShade);

        var row = ComponentProbe.Probe(game, GameFileIndexer.Build(game.InstallPath)).Single(c => c.Kind == ManagedComponentKind.ReShade);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
    }

    public static TheoryData<string?, string?, int?> RunnerNames => new()
    {
        { "GE-Proton10-18", null, 10 },
        { "9.0-203", null, 9 },
        { "10.1000-200", null, 10 },
        { "experimental-10.0-20250930", null, 10 },
        { "GE-Proton7-55", null, 7 },
        { null, "wine-ge-8-26-x86_64", 8 },
        { null, "lutris-GE-Proton8-26-x86_64", 8 },
        { null, "wine-11.19", 11 },
        { null, "Proton 9.0", 9 },
        { null, "system", null },
        { null, null, null },
    };

    [Theory]
    [MemberData(nameof(RunnerNames))]
    public void VulkanLayer_RunnerRule_ReadsTheMajorVersion(string? marker, string? tool, int? major)
    {
        var refusal = ReShadeVulkanLayer.RunnerRefusal(marker, tool);
        if (major is null) Assert.Equal(ReShadeVulkanLayer.UnknownRunnerMessage, refusal);
        else if (major >= 8) Assert.Null(refusal);
        else Assert.Equal(ReShadeVulkanLayer.RunnerTooOldPrefix + (marker ?? tool), refusal);
        Assert.Equal(major, ReShadeVulkanLayer.MajorVersion(marker ?? tool));
    }

    [Fact]
    public void VulkanLayer_RunnerRule_ThePrefixMarkerWinsOverTheLaunchersName()
    {
        Assert.Equal(ReShadeVulkanLayer.RunnerTooOldPrefix + "GE-Proton7-55", ReShadeVulkanLayer.RunnerRefusal("GE-Proton7-55", "GE-Proton10-18"));
        Assert.Null(ReShadeVulkanLayer.RunnerRefusal("GE-Proton10-18", "wine-7.0"));
    }

    [Fact]
    public void VulkanLayer_Target_IsTheUsersOverrideOrVulkanAloneAtHighConfidence()
    {
        var game = new DetectedGame { Name = "G", InstallPath = "/g", GraphicsApi = GraphicsApi.Vulkan, GraphicsApis = [GraphicsApi.Vulkan], GraphicsApiConfidence = GraphicsApiConfidence.High };
        Assert.True(ReShadeVulkanLayer.IsVulkanTarget(game));
        game.GraphicsApiConfidence = GraphicsApiConfidence.Medium;
        Assert.False(ReShadeVulkanLayer.IsVulkanTarget(game));
        game.GraphicsApiConfidence = GraphicsApiConfidence.High;
        game.GraphicsApis = [GraphicsApi.Vulkan, GraphicsApi.DirectX12];
        Assert.False(ReShadeVulkanLayer.IsVulkanTarget(game));
        game.UserGraphicsApiOverride = GraphicsApi.Vulkan;
        Assert.True(ReShadeVulkanLayer.IsVulkanTarget(game));
        game.UserGraphicsApiOverride = GraphicsApi.DirectX12;
        game.GraphicsApis = [GraphicsApi.Vulkan];
        Assert.False(ReShadeVulkanLayer.IsVulkanTarget(game));
        game.UserGraphicsApiOverride = null;
        game.ManifestGraphicsApiOverride = GraphicsApi.DirectX11;
        Assert.False(ReShadeVulkanLayer.IsVulkanTarget(game));
    }

    /// <summary>LunarG's version list and runtime zip, everything else through the ReShade fixture handler.</summary>
    private sealed class LunarGHandler : DelegatingHandler
    {
        public LunarGHandler() : base(new MutableHandler()) { }
        public MutableHandler Inner => (MutableHandler)InnerHandler!;
        public string LatestVersion { get; set; } = "1.4.363.0";
        public bool LatestUnreachable { get; set; }
        public string LoaderDescription { get; set; } = "Vulkan Loader";
        public int RequestCount { get; private set; }
        public int ZipRequests { get; private set; }

        internal static byte[] Loader(bool x86, string version = "1.4.363.0", string description = "Vulkan Loader")
        {
            var pe = PeVersionReaderTests.BuildVersionedPe(version, version, 8192, extraStrings: [("FileDescription", description), ("ProductName", "Vulkan Runtime")]);
            if (x86) BitConverter.GetBytes((ushort)0x014c).CopyTo(pe, 0x84);
            return pe;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var uri = request.RequestUri!;
            if (uri.Host == "vulkan.lunarg.com")
            {
                if (LatestUnreachable) throw new HttpRequestException("Connection refused");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"linux\":\"{LatestVersion}\",\"windows\":\"{LatestVersion}\"}}") });
            }
            if (uri.Host == "sdk.lunarg.com")
            {
                ZipRequests++;
                var version = uri.Segments[3].TrimEnd('/');
                using var memory = new MemoryStream();
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    foreach (var (arch, x86) in new[] { ("x64", false), ("x86", true) })
                    {
                        var entry = archive.CreateEntry($"VulkanRT-X64-{version}-Components/{arch}/vulkan-1.dll");
                        entry.LastWriteTime = new DateTimeOffset(2026, 9, 27, 16, 12, 0, TimeSpan.Zero);
                        using var stream = entry.Open();
                        stream.Write(Loader(x86, version, LoaderDescription));
                    }
                    var license = archive.CreateEntry($"VulkanRT-X64-{version}-Components/VulkanRT-License.txt");
                    license.LastWriteTime = new DateTimeOffset(2026, 9, 27, 16, 12, 0, TimeSpan.Zero);
                    using (var stream = license.Open()) stream.Write(Encoding.ASCII.GetBytes("Apache-2.0"));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
