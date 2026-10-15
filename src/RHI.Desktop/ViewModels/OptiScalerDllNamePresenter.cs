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
/// OptiScaler's names before it is installed, only the installed name (locked) while it is, and no name another RHI
/// component already uses on the game. The install takes the same answer
/// (<see cref="LinuxManagedComponentService.OptiScalerProxyNameFor"/>).
/// </summary>
public static class OptiScalerDllNamePresenter
{
    public static OptiScalerDllNameState For(LinuxManagedComponentService components, DetectedGame game, bool busy)
    {
        if (components.GetInstalledOptiScalerVariant(game) is not null)
        {
            var installed = components.OptiScalerProxyNameFor(game);
            var only = new DllNameOption(installed, installed);
            return new([only], only, false, UiLocalizationService.Format("Names.OptiScalerDllNameTipInstalled", installed));
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
}
