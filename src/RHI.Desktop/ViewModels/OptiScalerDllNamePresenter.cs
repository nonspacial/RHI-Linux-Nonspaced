using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>What OptiScaler's DLL-name dropdown shows for a game (#172).</summary>
/// <param name="CanChoose">False while RHI's OptiScaler is installed (the name is fixed until removal) or RHI is busy.</param>
/// <param name="Hint">The 2 s tooltip: which name OptiScaler uses and why.</param>
public sealed record OptiScalerDllNameState(IReadOnlyList<DllNameOption> Options, DllNameOption Selected, bool CanChoose, string Hint);

/// <summary>
/// OptiScaler's "DLL name" dropdown in its settings (#172), like ReShade's (#120): Automatic (dxgi.dll, or the next free name
/// while another RHI component holds dxgi.dll) by default, any of
/// OptiScaler's names before it is installed; while it is installed, its name and every free name (a choice renames it in
/// place, #196), or only its name, locked, while RHI's ReShade is chained to it; never a name another RHI component
/// already uses on the game. The install takes the same answer
/// (<see cref="LinuxManagedComponentService.OptiScalerProxyNameFor"/>).
/// </summary>
public static class OptiScalerDllNamePresenter
{
    public static OptiScalerDllNameState For(LinuxManagedComponentService components, DetectedGame game, bool busy)
    {
        if (components.GetInstalledOptiScalerVariant(game) is not null)
        {
            var installed = components.OptiScalerProxyNameFor(game);
            var (targets, refusal) = components.OptiScalerRenameOptions(game);
            if (refusal is not null)
            {
                // The ReShade chain keeps the name, or the file changed: only the installed name, locked, and why.
                var only = new DllNameOption(installed, installed);
                return new([only], only, false, UiLocalizationService.Present(refusal));
            }
            // #196: Automatic (when its name is free or is this one), the installed name and every free name; a choice
            // renames OptiScaler in place (RenameAsync).
            var renameTo = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase) { installed };
            var automaticName = components.OptiScalerAutomaticProxyName(game);
            var installedOptions = new List<DllNameOption>();
            if (renameTo.Contains(automaticName))
                installedOptions.Add(new DllNameOption(null, UiLocalizationService.Format("Names.DllNameAutomatic", automaticName)));
            installedOptions.AddRange(OptiScalerDeploymentService.ProxyNames.Where(renameTo.Contains).Select(name => new DllNameOption(name, name)));
            var automaticSelected = components.ReShadeDllNames.GetOptiScaler(game) is null
                                    && string.Equals(installed, automaticName, StringComparison.OrdinalIgnoreCase);
            var current = installedOptions.FirstOrDefault(option => automaticSelected
                ? option.Value is null
                : string.Equals(option.Value, installed, StringComparison.OrdinalIgnoreCase)) ?? installedOptions[0];
            return new(installedOptions, current, !busy, UiLocalizationService.Format("Rx.OptiScalerDllNameTipRename", installed));
        }

        var offered = components.OptiScalerProxyNamesFor(game);
        // "Automatic" names the name the install would take: dxgi.dll, or the next free one while another RHI component holds it.
        var automatic = new DllNameOption(null, UiLocalizationService.Format("Names.DllNameAutomatic", components.OptiScalerAutomaticProxyName(game)));
        var options = new List<DllNameOption> { automatic };
        options.AddRange(offered.Select(name => new DllNameOption(name, name)));
        var chosen = components.ReShadeDllNames.GetOptiScaler(game);
        var selected = options.FirstOrDefault(option => option.Value is not null && string.Equals(option.Value, chosen, StringComparison.OrdinalIgnoreCase)) ?? automatic;
        var hint = UiLocalizationService.Format("Names.OptiScalerDllNameTip", components.OptiScalerProxyNameFor(game));
        var hidden = OptiScalerDeploymentService.ProxyNames.Except(offered, StringComparer.OrdinalIgnoreCase).ToArray();
        if (hidden.Length > 0) hint += UiLocalizationService.Format("Names.OptiScalerDllNameTipHidden", string.Join(", ", hidden));
        return new(options, selected, !busy, hint);
    }

    /// <summary>
    /// #196: renames RHI's installed OptiScaler on the selected game to <paramref name="name"/> (null = Automatic) in place
    /// as a shell operation (busy gate, status line, the game analysed again). True when it was renamed (or kept its name
    /// and the choice was saved); false when refused or failed (the status line says why).
    /// </summary>
    public static async Task<bool> RenameAsync(MainWindowViewModel shell, LinuxManagedComponentService components, string? name)
    {
        IPanelHost host = shell;
        if (host.SelectedGame is not { } game) return false;
        using var op = host.BeginOperation("dll-rename");
        if (op is null) return false;
        try
        {
            host.StatusText = UiLocalizationService.Text("Rx.Renaming");
            var result = await components.RenameOptiScalerProxyAsync(game, name, op.Token).ConfigureAwait(true);
            host.StatusText = !result.Success && op.CancelRequested
                ? MainWindowViewModel.OperationCancelledText()
                : UiLocalizationService.Present(result.Message);
            if (result.Success) await host.ReprobeKeepingStatusAsync(game).ConfigureAwait(true);
            return result.Success;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            host.StatusText = MainWindowViewModel.OperationCancelledText();
            return false;
        }
    }
}
