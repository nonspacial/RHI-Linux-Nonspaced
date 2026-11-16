using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

// w14/extras-addons (#284): the extras' DLL-name lists beside ASI Loader (Hooked names, names in use marked).
public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// The rows of an extra's DLL-name list on <paramref name="game"/> (#284): its names with ASI Loader's Hooked name after
    /// ASI Loader's, each marked with the RHI component using it; <paramref name="current"/> (the saved or installed name)
    /// is never marked. Empty for a component without a name choice.
    /// </summary>
    public IReadOnlyList<ExtraDllNameOption> ExtraDllNameOptions(DetectedGame game, ManagedComponentKind kind, string? current)
    {
        IEnumerable<string>? names = kind switch
        {
            ManagedComponentKind.Dlssg2030Unlock => Dlssg2030UnlockDeploymentService.ProxyNames,
            ManagedComponentKind.Rtx40MfgStandalone => Rtx40MfgStandaloneDeploymentService.SelectableProxyNames,
            ManagedComponentKind.DlssEnablerStandalone => StandaloneDlssEnablerDeploymentService.ProxyNames,
            ManagedComponentKind.RtxEncore => _rtxEncore?.CandidateNames(game),
            _ => null
        };
        if (names is null) return [];
        // RTX Encore's Bink names already use Hooked for the game's own Bink file.
        var options = ExtraDllNames.Options(_records, game, kind, names, current, kind == ManagedComponentKind.RtxEncore ? RtxEncoreNames.IsBink : null);
        // The saved/installed name is always listed, so the dropdown never blanks it.
        return current is null || options.Any(o => o.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
            ? options
            : [new ExtraDllNameOption(current, null, HookedDllNames.TryGetBase(current, out _)), .. options];
    }
}
