using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// The FG 20/30 unlock, standalone DLSS Enabler and Universal MFG dialogs as view models (epic #89 unit 7): choices
/// save through the shell and go back when refused; Check / Update run under the busy gate, and the dialog's own
/// Cancel (the overlay's is behind the modal) shows only while its update can still stop, and stops it.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ProxyComponentDialogTests
{
    [Fact]
    public async Task AChoice_IsSaved_AndARefusedSave_PutsTheDropdownBack()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.DlssEnabler);
        Assert.True(dialog.CanChoose);
        Assert.False(dialog.IsInstalled);
        var other = dialog.ProxyOptions.First(name => name != dialog.SelectedProxy);

        dialog.SelectedProxy = other;
        Assert.Equal(other, fx.Vm.GetStandaloneDlssEnablerPreferences().ProxyName);

        var stored = dialog.SelectedProxy;
        using (fx.Vm.Operations.TryBegin("test"))
            dialog.SelectedProxy = dialog.ProxyOptions.First(name => name != stored);
        Assert.Equal(stored, dialog.SelectedProxy); // refused while busy: back to what is saved
        Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), dialog.Message);
    }

    [Theory]
    [InlineData(ProxyComponentDialogKind.Dlssg2030, ManagedComponentKind.Dlssg2030Unlock, "Ui.Dlssg2030Title", true)]
    [InlineData(ProxyComponentDialogKind.DlssEnabler, ManagedComponentKind.DlssEnablerStandalone, "Ui.DlssEnablerStandaloneTitle", false)]
    [InlineData(ProxyComponentDialogKind.UniversalMfg, ManagedComponentKind.Rtx40MfgStandalone, "Ui.Rtx40MfgTitle", true)]
    public async Task EachDialog_ShowsItsComponent_AndLocksItsChoicesOnceInstalled(ProxyComponentDialogKind kind, ManagedComponentKind component, string titleKey, bool hasGpu)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddAsync(fx, "Alpha Quest");
        using (var fresh = new ProxyComponentDialogViewModel(fx.Vm, kind))
        {
            Assert.Equal((UiLocalizationService.Text(titleKey), hasGpu, true, false), (fresh.Title, fresh.HasGpu, fresh.CanChoose, fresh.IsInstalled));
            Assert.EndsWith("— Alpha Quest", fresh.WindowTitle, StringComparison.Ordinal);
        }
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = component, BinaryDirectory = game.InstallPath, Version = "1.0"
        });
        using var installed = new ProxyComponentDialogViewModel(fx.Vm, kind);
        Assert.Equal((false, true), (installed.CanChoose, installed.IsInstalled));
        Assert.False(string.IsNullOrWhiteSpace(installed.InstalledText));
    }

    [Fact]
    public async Task AnInstalledComponent_UpdatesUnderTheGate_AndTheDialogsOwnCancelStopsIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddAsync(fx, "Alpha Quest");
        var proxy = Path.Combine(game.InstallPath, "dxgi.dll");
        File.WriteAllBytes(proxy, [0x4D, 0x5A, 1, 2, 3]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DlssEnablerStandalone,
            BinaryDirectory = game.InstallPath, Version = "1.0",
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["DLSSEnabler.ProxyName"] = "dxgi.dll" },
            Files = [new LinuxDeployedFile { Path = proxy, ExistedBeforeInstall = false, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(proxy))) }]
        });
        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.DlssEnabler);
        Assert.False(dialog.ShowCancel);
        var stall = 1;
        fx.HangWhen = _ => Volatile.Read(ref stall) == 1;

        var update = dialog.UpdateCommand.ExecuteAsync(null);
        for (var i = 0; i < 400 && !dialog.ShowCancel && !update.IsCompleted; i++) await Task.Delay(25);
        Assert.True(dialog.ShowCancel, $"busy={fx.Vm.IsBusy} kind={fx.Vm.Operations.CurrentKind} msg={dialog.Message}");
        Assert.Equal("component-update", fx.Vm.Operations.CurrentKind);
        Volatile.Write(ref stall, 0);
        dialog.Operations.CancelCommand.Execute(null);
        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(dialog.ShowCancel);
        Assert.False(dialog.IsUpdating);
        Assert.False(fx.Vm.IsBusy);
        Assert.Equal(UiLocalizationService.Text("Vm.OperationCancelled"), dialog.Message);
    }

    private static async Task<DetectedGame> AddAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        return fx.Vm.SelectedGame!;
    }
}
