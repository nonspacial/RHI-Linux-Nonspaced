using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #231 (owner decision 2026-10-22): before every user-started OptiScaler F5 DLSSNR install, RHI checks the NVIDIA driver
/// and the NR DLL F5 will load, and when either is a problem it says so through the existing confirm dialog ("Install
/// anyway" / Cancel) — never a refusal. The threshold is the Linux driver 615.71.09 (the Linux equivalent of F5's Windows
/// 616.56; <see cref="OptiScalerF5DriverPolicy"/>). Update All never asks; its status line carries the driver note.
/// </summary>
public sealed class F5DriverConfirmPresenter(MainWindowViewModel vm, LinuxManagedComponentService managed)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>The plain lines of an advice (driver first), in the current language.</summary>
    public static IReadOnlyList<string> Lines(F5DriverAdvice advice)
    {
        var lines = new List<string>();
        if (advice.DriverUnknown)
            lines.Add(TF("Ng.F5NoDriver", OptiScalerF5DriverPolicy.MinimumLinuxDriver, OptiScalerF5DriverPolicy.WindowsMinimum));
        else if (advice.DriverBelowMinimum)
            lines.Add(TF("Ng.F5DriverOld", OptiScalerF5DriverPolicy.MinimumLinuxDriver, OptiScalerF5DriverPolicy.WindowsMinimum, advice.DriverVersion));
        if (advice.NrDllFaults)
            lines.Add(TF("Ng.F5NrDllFaults", advice.NrDll));
        return lines;
    }

    /// <summary>
    /// Asks before an F5 install when the advice has a warning. True: go on (no warning, accepted, or no window that can
    /// ask — tests and headless runs answer through <see cref="MainWindowViewModel.ConfirmAsync"/>). False: declined, the
    /// status line says so and nothing changed.
    /// </summary>
    public async Task<bool> ConfirmInstallAsync(DetectedGame game, OptiScalerVariant variant)
    {
        if (variant != OptiScalerVariant.F5 || vm.ConfirmAsync is not { } confirm) return true;
        var advice = await managed.F5DriverAdviceAsync();
        if (!advice.AnyWarning) return true;
        var body = string.Join("\n\n", Lines(advice).Append(T("Ng.F5Ask")));
        if (await confirm(TF("Ng.F5ConfirmTitle", game.Name), body, T("Ng.F5InstallAnyway"))) return true;
        vm.StatusText = UiLocalizationService.Format("Vm.InstallationCancelled", "OptiScaler F5 DLSSNR");
        return false;
    }

    /// <summary>
    /// Update All's note (manual and background; never a question, never a refusal): the games Update All took F5 into
    /// while the driver is below 615.71.09 or unreadable; "" otherwise.
    /// </summary>
    public string UpdateAllNote(IEnumerable<DetectedGame> games, LinuxUpdateAllResult result)
    {
        var advice = managed.F5DriverAdvice();
        if (!advice.DriverWarning) return "";
        var names = result.Items
            .Where(item => item.Component == ManagedComponentKind.OptiScaler && !item.Excluded)
            .Select(item => item.GameName)
            .Distinct(StringComparer.Ordinal)
            .Where(name => games.FirstOrDefault(game => game.Name == name) is { } game && managed.HasOptiScalerF5(game))
            .ToArray();
        if (names.Length == 0) return "";
        return " " + (advice.DriverUnknown
            ? TF("Ng.F5UpdateAllNoDriver", string.Join(", ", names), OptiScalerF5DriverPolicy.MinimumLinuxDriver, OptiScalerF5DriverPolicy.WindowsMinimum)
            : TF("Ng.F5UpdateAllDriverOld", string.Join(", ", names), OptiScalerF5DriverPolicy.MinimumLinuxDriver, OptiScalerF5DriverPolicy.WindowsMinimum, advice.DriverVersion));
    }
}
