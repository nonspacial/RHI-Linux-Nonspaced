using Avalonia.Controls;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/ui-settings (#297): the RenoDX ⚙'s first card in upstream's order (<c>AddRdxMainSettings</c>: UE-Extended, Max Nits,
/// then the Engine.ini rows from the view) on the shared row layout. UE-Extended is applied the moment it changes (RenoDX
/// is reinstalled with the other add-on) and goes back to what is installed when refused; the typed maximum nits keeps
/// its Set button, as upstream's typed values (#182 behaviour: every RenoDX preset of the game, or preset 1).
/// </summary>
internal static class RenoDxCogRows
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static IReadOnlyList<Control> Create(IniPresetsPresenter presenter, Action<string> show)
    {
        var rows = new List<Control>();
        if (presenter.OffersUeExtended()) rows.Add(UeExtendedRow(presenter, show));
        if (presenter.GameHasReShadeIni()) rows.Add(NitsRow(presenter, show)); // no ReShade.ini yet: no presets to write
        return rows;
    }

    private static CogRow NitsRow(IniPresetsPresenter presenter, Action<string> show)
    {
        var nits = new NumericUpDown
        {
            Name = "RenoDxGameNits", Minimum = 1, Maximum = RenoDxPeakNits.MaxNits, Increment = 10, FormatString = "0",
            Value = presenter.GameNits() ?? 1000
        };
        var set = CogDialog.StyledButton(T("Ini.PeakGameSetButton"), CogButtonKind.Secondary, "RenoDxGameNitsSet");
        set.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            set.IsEnabled = false;
            try { show(await presenter.SetGameNitsAsync((int)(nits.Value ?? 0))); }
            finally { set.IsEnabled = true; }
        }, nameof(RenoDxCogRows));
        return new CogRow(T("Ini.PeakGame"), CogDialog.Inline(nits, set), T("Ini.PeakGameTip"));
    }

    private static CogRow UeExtendedRow(IniPresetsPresenter presenter, Action<string> show)
    {
        var ue = CogDialog.MakeCombo(OptionLists.OffOn.Items, presenter.UeExtendedInstalled() ? 1 : 0, name: "RenoDxUeExtended");
        var reverting = false;
        ue.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (reverting || ue.SelectedIndex < 0) return;
            var on = ue.SelectedIndex == 1;
            if (on == presenter.UeExtendedInstalled()) return;
            ue.IsEnabled = false;
            try { show(await presenter.SetUeExtendedAsync(on)); }
            finally
            {
                reverting = true;
                ue.SelectedIndex = presenter.UeExtendedInstalled() ? 1 : 0;
                reverting = false;
                ue.IsEnabled = true;
            }
        }, nameof(RenoDxCogRows));
        return new CogRow("UE-Extended", ue, T("Ini.UeExtendedTip"));
    }
}
