using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>What HSF's row shows for the selected game (#277).</summary>
/// <param name="Visible">False for a game HSF can never apply to (native Linux, no confirmed patch target).</param>
/// <param name="OffersUniversalMfg">RTX 20/30/40 and Universal MFG Unlocker not installed: the row offers it (upstream's offer).</param>
/// <param name="Warning">The anti-cheat warning before an install; the Vulkan/dxgi.dll warning when it applies.</param>
public sealed record HsfRowState(bool Visible, bool Installed, bool CanAct, string Status, bool UpdateAvailable, bool OffersUniversalMfg,
    string? Warning, HsfState? Settings);

/// <summary>
/// #277 (upstream 2.9.3 <c>DetailPanelBuilder.Extras.Hsf</c>): HSF's row in Extras, beside the frame-generation unlocks:
/// install (with iMMERSE Launchpad unless switched off), check and update, remove, the ⚙ (version, DirectX 12 mode,
/// Launchpad) and, on RTX 20/30/40 cards, Universal MFG Unlocker through its own install. Every action runs under the
/// shell's operation gate and reports one status line; the logic is <see cref="HsfDeploymentService"/>'s.
/// </summary>
public sealed class HsfPresenter(LinuxManagedComponentService components)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private bool? _updateAvailable;
    private string? _checkedFor;

    public HsfRowState StateFor(MainWindowViewModel vm) => StateFor(vm.SelectedGame, !vm.IsBusy && !vm.IsSelectionProbeBusy);

    public HsfRowState StateFor(DetectedGame? game, bool idle)
    {
        if (game is null || components.Hsf is not { } hsf || string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
            || !game.HasConfirmedPatchTarget)
            return new(false, false, false, "", false, false, null, null);
        if (_checkedFor != game.Name) (_updateAvailable, _checkedFor) = (null, game.Name);
        var record = hsf.GetRecord(game);
        if (record is null)
        {
            var refusal = hsf.EligibilityRefusal(game);
            return new(true, false, idle && refusal is null, refusal is null ? T("Hsf.Available") : UiLocalizationService.Present(refusal), false, false,
                UiLocalizationService.Present(HsfDeploymentService.AntiCheatWarning), null);
        }
        var settings = hsf.Read(game);
        var warning = HsfDeploymentService.VulkanDxgiWarning(game) is { } vulkan ? UiLocalizationService.Present(vulkan) : null;
        return new(true, true, idle, TF("Hsf.Installed", settings.Version ?? "?"), _updateAvailable == true,
            components.OffersUniversalMfgWithHsf(game), warning, settings);
    }

    public Task InstallAsync(MainWindowViewModel vm, bool launchpadOn = true)
        => RunAsync(vm, "hsf-install", "Hsf.Installing", (game, token) => components.InstallHsfAsync(game, launchpadOn, token), offerMfg: true);

    public Task RemoveAsync(MainWindowViewModel vm)
        => RunAsync(vm, "hsf-remove", "Hsf.Removing", (game, token) => components.RemoveHsfAsync(game, token));

    public Task UpdateAsync(MainWindowViewModel vm)
        => RunAsync(vm, "hsf-update", "Hsf.Updating", async (game, token) =>
        {
            var result = await components.UpdateHsfAsync(game, token);
            if (result.Success) _updateAvailable = false;
            if (result.Success && components.Hsf is { } hsf) await hsf.KeepLaunchpadFirstAsync(game, token);
            return result;
        });

    public Task InstallUniversalMfgAsync(MainWindowViewModel vm)
        => RunAsync(vm, "hsf-universal-mfg", "Hsf.InstallingMfg", (game, token) => components.InstallUniversalMfgForHsfAsync(game, token));

    /// <summary>The ⚙'s iMMERSE Launchpad row (#297: saved on change); whether it was saved (the line is the status text).</summary>
    public Task<bool> SetLaunchpadAsync(MainWindowViewModel vm, bool on)
        => RunAsync(vm, "hsf-launchpad", "Hsf.Saving", (game, token) => components.SetHsfLaunchpadAsync(game, on, token));

    /// <summary>The ⚙'s DirectX 12 mode row (#297: saved on change); whether it was saved (the line is the status text).</summary>
    public Task<bool> SetRouteAsync(MainWindowViewModel vm, HsfRoute route)
        => RunAsync(vm, "hsf-route", "Hsf.Saving", (game, token) => components.SetHsfRouteAsync(game, route, token));

    /// <summary>Check for an update (the row's Check); the status line says what it found.</summary>
    public async Task CheckAsync(MainWindowViewModel vm)
    {
        if (vm.SelectedGame is not { } game || components.Hsf is not { } hsf) return;
        using var op = vm.Operations.TryBegin("hsf-check", cancellable: false);
        if (op is null)
        {
            vm.StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return;
        }
        var check = await hsf.CheckUpdateAsync(game, op.Token);
        _updateAvailable = check.UpdateAvailable;
        _checkedFor = game.Name;
        vm.StatusText = UiLocalizationService.Present(check.Message);
    }

    /// <summary>The ⚙ opens: Launchpad is moved to the top of the preset where HSF listed it lower down.</summary>
    public async Task<string?> PrepareGearAsync(MainWindowViewModel vm)
    {
        if (vm.SelectedGame is not { } game || components.Hsf is not { } hsf) return null;
        var result = await hsf.KeepLaunchpadFirstAsync(game);
        return result.Success && result.Message == "moved" ? T("Hsf.LaunchpadMoved") : null;
    }

    private async Task<bool> RunAsync(MainWindowViewModel vm, string kind, string busyKey,
        Func<DetectedGame, CancellationToken, Task<ComponentOperationResult>> action, bool offerMfg = false)
    {
        if (vm.SelectedGame is not { } game) return false;
        using var op = vm.Operations.TryBegin(kind);
        if (op is null)
        {
            vm.StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return false;
        }
        vm.StatusText = TF(busyKey, game.Name);
        ComponentOperationResult result;
        try
        {
            result = await action(game, op.Token);
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            vm.StatusText = MainWindowViewModel.OperationCancelledText();
            return false;
        }
        var line = UiLocalizationService.Present(result.Message);
        // Upstream offers Universal MFG Unlocker on RTX 20/30/40 after installing HSF; here the row's button offers it.
        if (offerMfg && result.Success && components.OffersUniversalMfgWithHsf(game)) line += " " + T("Hsf.OfferMfg");
        vm.StatusText = line;
        await vm.ReprobeSelectedAsync(game);
        return result.Success;
    }
}
