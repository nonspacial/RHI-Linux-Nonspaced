using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// #172: OptiScaler's DLL-name choice (the dropdown in its settings, like ReShade's #120). Automatic = dxgi.dll; a saved name
// is the one the install takes; a name another RHI component holds is not offered and never used; the installed name
// stays until removal.
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Fact]
    public async Task ChosenDllName_IsTheNameTheInstallTakes_AndStaysWhileInstalled()
    {
        var (game, store) = CreateGame("choice-install", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-install-package"), "choice-install");
        Assert.Equal("dxgi.dll", managed.OptiScalerProxyNameFor(game)); // Automatic

        names.SetOptiScaler(game, "VERSION.dll");
        Assert.Equal("version.dll", managed.OptiScalerProxyNameFor(game));
        var installed = await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null);

        Assert.True(installed.Success, installed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "version.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("version.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));

        names.SetOptiScaler(game, "winmm.dll"); // a later choice does not move an installed OptiScaler
        Assert.Equal("version.dll", managed.OptiScalerProxyNameFor(game));
    }

    [Fact]
    public void NameHeldByAnotherComponent_IsNotOffered_AndASavedChoiceOfItFallsBackToDxgi()
    {
        var (game, store) = CreateGame("choice-held", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store); // RHI's ReShade at dxgi.dll moves aside for OptiScaler: still offered
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllText(winmm, "MZ loader");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-held-package"), "choice-held");

        var offered = managed.OptiScalerProxyNamesFor(game);

        Assert.DoesNotContain("winmm.dll", offered);
        Assert.Contains("dxgi.dll", offered);
        Assert.Equal(OptiScalerDeploymentService.ProxyNames.Count - 1, offered.Count);
        names.SetOptiScaler(game, "winmm.dll");
        Assert.Equal("dxgi.dll", managed.OptiScalerProxyNameFor(game));
    }

    [Fact]
    public void DllNameStore_KeepsReShadesAndOptiScalersChoicesApart()
    {
        var (game, _) = CreateGame("choice-store", GraphicsApi.DirectX12);
        var names = new DllNamePreferenceStore(Path.Combine(_root, "choice-store", "dll-names.json"));

        names.SetReShade(game, "d3d11.dll");
        names.SetOptiScaler(game, "winhttp.dll");
        names.SetReShade(game, null);
        Assert.Equal("winhttp.dll", names.GetOptiScaler(game));
        names.SetReShade(game, "d3d11.dll");
        names.SetOptiScaler(game, null);
        Assert.Equal("d3d11.dll", names.GetReShade(game));
        Assert.Null(names.GetOptiScaler(game));
        Assert.Throws<ArgumentException>(() => names.SetOptiScaler(game, "opengl32.dll"));
        Assert.Throws<ArgumentException>(() => names.SetOptiScaler(game, "../dxgi.dll"));

        File.WriteAllText(Path.Combine(_root, "choice-store", "dll-names.json"),
            File.ReadAllText(Path.Combine(_root, "choice-store", "dll-names.json")).Replace("\"reshade\"", "\"optiscaler\": \"evil.dll\", \"reshade\"", StringComparison.Ordinal));
        Assert.Null(new DllNamePreferenceStore(Path.Combine(_root, "choice-store", "dll-names.json")).GetOptiScaler(game)); // hand-edited: automatic
    }

    // The dropdown (values, not wording: other test classes switch the UI language meanwhile).
    [Fact]
    public async Task Dropdown_DefaultsToAutomatic_HidesHeldNames_FollowsTheChoice_AndLocksWhileInstalled()
    {
        var (game, store) = CreateGame("choice-dropdown", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-dropdown-package"), "choice-dropdown");

        var fresh = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.Equal([null, .. OptiScalerDeploymentService.ProxyNames], fresh.Options.Select(o => o.Value));
        Assert.Null(fresh.Selected.Value);
        Assert.Contains("dxgi.dll", fresh.Selected.Display, StringComparison.Ordinal); // "Automatic (dxgi.dll)"
        Assert.True(fresh.CanChoose);
        Assert.False(RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: true).CanChoose);

        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllText(winmm, "MZ loader");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        names.SetOptiScaler(game, "d3d12.dll");
        var chosen = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.DoesNotContain(chosen.Options, o => o.Value == "winmm.dll");
        Assert.Equal("d3d12.dll", chosen.Selected.Value);
        Assert.Contains("winmm.dll", chosen.Hint, StringComparison.Ordinal);
        Assert.Contains("d3d12.dll", chosen.Hint, StringComparison.Ordinal);

        Assert.True((await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null)).Success);
        var installed = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.Equal("d3d12.dll", Assert.Single(installed.Options).Value);
        Assert.False(installed.CanChoose);
        Assert.Contains("d3d12.dll", installed.Hint, StringComparison.Ordinal);
    }

    internal static (LinuxManagedComponentService Managed, DllNamePreferenceStore Names) ManagedWithChoice(
        LinuxDeploymentRecordStore store, OptiScalerPackage package, string root)
        => ManagedWithChoice(store, new FakePackageProvider(package), Path.Combine(Path.GetTempPath(), "rhi-optiscaler-choice-" + root + "-" + Guid.NewGuid().ToString("N")));

    private static (LinuxManagedComponentService Managed, DllNamePreferenceStore Names) ManagedWithChoice(
        LinuxDeploymentRecordStore store, IOptiScalerPackageProvider provider, string scratch)
    {
        var names = new DllNamePreferenceStore(Path.Combine(scratch, "dll-names.json"));
        var http = new HttpClient(new NexusApiClientTests.FakeNexus(_ => throw new HttpRequestException("offline test")));
        var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), null!,
            Path.Combine(scratch, "reshade-cache"),
            new ReShadeShaderPackService(http, Path.Combine(scratch, "shader-packs"), customRoot: Path.Combine(scratch, "custom-shaders")),
            dllNames: names);
        var managed = new LinuxManagedComponentService(store, reshade: reshade, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: NewService(store, provider), relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            updateLog: new ComponentUpdateLogStore(Path.Combine(scratch, "update-log.json")));
        return (managed, names);
    }
}
