using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>What the NR Cost Scaler row shows for the selected game.</summary>
/// <param name="Visible">False when the game has no Neural Rendering method RHI installed and no scaler (the row is hidden).</param>
public sealed record NrCostScalerRowState(bool Visible, bool Installed, bool CanAct, string Status);

/// <summary>
/// The NR Cost Scaler row under the Neural Rendering versions (#96): install it over the game's Neural Rendering method,
/// or remove it. Upstream deploys it automatically with the next NR method install when its toggle was on; on Linux the
/// scaler is a layer over an installed method (one-owner rule, sync #157), so it is installed and removed on its own
/// explicit button, through the shell's operation gate and component history.
/// </summary>
public sealed class NrCostScalerPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly LinuxManagedComponentService _components;

    public NrCostScalerPresenter(LinuxManagedComponentService components) => _components = components;

    public NrCostScalerRowState StateFor(MainWindowViewModel vm)
    {
        if (vm.SelectedGame is not { } game || _components.NrCostScaler is not { } scaler) return new(false, false, false, "");
        var canAct = game.HasConfirmedPatchTarget && !vm.IsBusy && !vm.IsSelectionProbeBusy;
        if (scaler.GetRecord(game) is { } record)
            return new(true, true, canAct, TF("Parity.NrScalerInstalled", record.Version ?? "?"));
        return scaler.FindOwner(game) is { } owner
            ? new(true, false, canAct, TF("Parity.NrScalerAvailable", owner.Owner))
            : new(false, false, false, "");
    }

    public Task InstallAsync(MainWindowViewModel vm) => RunAsync(vm, install: true);

    public Task RemoveAsync(MainWindowViewModel vm) => RunAsync(vm, install: false);

    private async Task RunAsync(MainWindowViewModel vm, bool install)
    {
        if (vm.SelectedGame is not { } game) return;
        using var op = vm.Operations.TryBegin(install ? "nr-cost-scaler-install" : "nr-cost-scaler-remove");
        if (op is null)
        {
            vm.StatusText = T("Parity.NrScalerBusy");
            return;
        }
        vm.StatusText = TF(install ? "Parity.NrScalerInstalling" : "Parity.NrScalerRemoving", game.Name);
        ComponentOperationResult result;
        try
        {
            result = install
                ? await _components.InstallNrCostScalerAsync(game, op.Token)
                : await _components.RemoveNrCostScalerAsync(game, op.Token);
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            vm.StatusText = T("Parity.NrScalerCancelled");
            return;
        }
        vm.StatusText = UiLocalizationService.Present(result.Message);
        await vm.ReprobeSelectedAsync(game);
    }
}
