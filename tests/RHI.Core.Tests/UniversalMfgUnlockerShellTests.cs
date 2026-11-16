using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #278 at the shell: Universal MFG Unlocker's ReShade warning before an install (upstream 2.8.9, "Don't show this
/// again"), its ⚙ sections, and the OptiScaler removal moving a plugin install back beside the game.
/// </summary>
public sealed class UniversalMfgUnlockerShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-umfg-shell-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private DetectedGame Game(string name, GraphicsApi api = GraphicsApi.DirectX12)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, name, "bin")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(_root, name, "pfx")).FullName;
        PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        return new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
            PrefixPath = prefix, Runner = "Proton", Source = "Test", GraphicsApi = api
        };
    }

    private static void AddRecord(ViewModelFixture fx, DetectedGame game, ManagedComponentKind kind, string file, Dictionary<string, string>? metadata = null)
        => fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, file), Sha256 = File.Exists(Path.Combine(game.BinaryDirectory!, file)) ? FileHash.Sha256Hex(Path.Combine(game.BinaryDirectory!, file)) : "00" }],
            Metadata = metadata ?? new(StringComparer.OrdinalIgnoreCase)
        });

    [Fact]
    public async Task WithReShade_InstallAsksAboutXeMfg_UntilDismissed()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game("WithReShade");
        AddRecord(fx, game, ManagedComponentKind.ReShade, "dxgi.dll");
        var asked = new List<DismissibleNotice>();
        fx.Vm.ConfirmDismissibleAsync = notice => { asked.Add(notice); return Task.FromResult(new DismissibleNoticeAnswer(false, true)); };

        Assert.False(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG Unlocker"));
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG Unlocker"));

        var notice = Assert.Single(asked);
        Assert.Equal("UmfgReShadeWarningDismissed", notice.Key);
        Assert.Equal(UiLocalizationService.Text("Notice.UmfgReShadeTitle"), notice.Title);
        Assert.Equal(UiLocalizationService.Text("Vm.Continue"), notice.AcceptText);
        Assert.Equal("XeMFG and ReShade", UiLocalizationService.TextFor("en-US", "Notice.UmfgReShadeTitle"));
    }

    // No ReShade, a Vulkan game (ReShade's layer), or an install that goes into OptiScaler: no warning.
    [Fact]
    public async Task WithoutReShade_OrAsAnOptiScalerPlugin_NothingIsAsked()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.Vm.ConfirmDismissibleAsync = _ => throw new InvalidOperationException("must not ask");
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("NoReShade"), ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG Unlocker"));

        var vulkan = Game("Vulkan", GraphicsApi.Vulkan);
        AddRecord(fx, vulkan, ManagedComponentKind.ReShade, "ReShade64.dll");
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(vulkan, ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG Unlocker"));

        var plugin = Game("Plugin");
        AddRecord(fx, plugin, ManagedComponentKind.ReShade, "dxgi.dll");
        Directory.CreateDirectory(Path.Combine(plugin.BinaryDirectory!, "OptiScaler"));
        AddRecord(fx, plugin, ManagedComponentKind.OptiScaler, "OptiScaler.dll",
            new(StringComparer.OrdinalIgnoreCase) { [OptiScalerDeploymentService.VariantMetadataKey] = "Nightly" });
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(plugin, ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG Unlocker"));
    }

    [Fact]
    public void Settings_ShowXeMfg_PluginAndDxgiSections()
    {
        var proxy = ProxyComponentDialogViewModel.UniversalMfgLines(new UniversalMfgStatus(false, false, true, "version.dll"), installed: true);
        Assert.Equal(
            [ComponentDialogLine.Heading(T("Ui.UmfgXessHeading")), ComponentDialogLine.Plain(T("Ui.UmfgXessStandalone")),
             ComponentDialogLine.Warning(T("Ui.UmfgXessMissing")), ComponentDialogLine.Warning(T("Ui.UmfgReShadeDxgi"))],
            proxy);

        var installed = ProxyComponentDialogViewModel.UniversalMfgLines(new UniversalMfgStatus(false, true, false, "version.dll"), installed: true);
        Assert.Contains(ComponentDialogLine.Plain(T("Ui.UmfgXessInstalled")), installed);

        var plugin = ProxyComponentDialogViewModel.UniversalMfgLines(new UniversalMfgStatus(true, null, false, Rtx40MfgStandaloneDeploymentService.PluginRelativePath), installed: true);
        Assert.Equal(
            [ComponentDialogLine.Heading(T("Ui.UmfgPluginHeading")), ComponentDialogLine.Plain(T("Ui.UmfgPluginText")), ComponentDialogLine.Plain(T("Ui.UmfgPluginNote")),
             ComponentDialogLine.Heading(T("Ui.UmfgXessHeading")), ComponentDialogLine.Plain(T("Ui.UmfgXessPlugin"))],
            plugin);
        Assert.Equal(T("Ui.UmfgPluginBeforeInstall"),
            ProxyComponentDialogViewModel.UniversalMfgLines(new UniversalMfgStatus(true, null, false, null), installed: false)[1].Text);
    }

    // Removing RHI's OptiScaler first moves a plugin install back beside the game (upstream BeforeOptiScalerFolderRemovedHook).
    [Fact]
    public async Task RemovingOptiScaler_MovesThePluginBack()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game("MoveBack");
        var plugin = Path.Combine(game.BinaryDirectory!, "OptiScaler", "plugins", "MFGUnlocker.asi");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        PeArchitectureDetectorTests.WritePe(plugin, 0x8664, 512 * 1024);
        AddRecord(fx, game, ManagedComponentKind.OptiScaler, "OptiScaler.dll",
            new(StringComparer.OrdinalIgnoreCase) { [OptiScalerDeploymentService.VariantMetadataKey] = "Nightly" });
        AddRecord(fx, game, ManagedComponentKind.Rtx40MfgStandalone, "OptiScaler/plugins/MFGUnlocker.asi", new(StringComparer.OrdinalIgnoreCase)
        {
            ["UniversalMFG.ProxyName"] = Rtx40MfgStandaloneDeploymentService.PluginRelativePath,
            ["UniversalMFG.Placement"] = "OptiScalerPlugin",
            ["UniversalMFG.PluginFromProxy"] = "winmm.dll"
        });

        var result = await fx.Managed.RemoveAsync(game, ManagedComponentKind.OptiScaler);

        Assert.Contains(Rtx40MfgStandaloneDeploymentService.MovedOutOfOptiScalerPrefix + "winmm.dll.", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "winmm.dll")));
        Assert.False(File.Exists(plugin));
        var record = fx.Records.Find(game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal("winmm.dll", record.Metadata["UniversalMFG.ProxyName"]);
    }

    private static string T(string key) => UiLocalizationService.Text(key);
}
