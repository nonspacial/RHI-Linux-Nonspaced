using Avalonia.Controls;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// #200 DX9 HDR ⚙ dialog on the shared ⚙ layout (#297; upstream DXVK ⚙'s "Lilium Preset" row): the dxvk.conf preset
/// (upstream's six tiers, safest first), written the moment it changes as upstream ("Changing it rewrites dxvk.conf") — no
/// Apply button; a refused write goes back to the preset the file holds. The result shows in the ⚙'s status line, and —
/// while the game's HDR launch is Off — an amber line names the control that turns it on.
/// </summary>
public static class DxvkHdrSettingsDialog
{
    public static Window Create(DxvkHdrPresenter presenter, string gameName) => Build(presenter, gameName).Build(height: 400);

    /// <summary>The ⚙'s content (tests read it without a window).</summary>
    internal static CogDialog Build(DxvkHdrPresenter presenter, string gameName)
    {
        static string T(string key) => UiLocalizationService.Text(key);
        var cog = new CogDialog(DxvkHdrDeploymentService.DisplayName, gameName);
        cog.AddIntro(T("Dxvk.DialogIntro"));
        if (presenter.HdrLaunchOff) cog.AddWarning(T("Dxvk.HdrOffHint")).Name = "DxvkHdrHdrOff";
        cog.AddSection("DXVK");
        var items = presenter.PresetItems;
        var current = presenter.CurrentPreset;
        var index = Math.Max(0, items.ToList().IndexOf(current));
        cog.AddCombo(T("Dxvk.PresetLabel"), T("Dxvk.PresetTip"), items, index, async i =>
        {
            cog.SetStatus(await presenter.ApplyPresetAsync(items[i]));
            return presenter.CurrentPreset == items[i];
        }, name: "DxvkHdrPreset");
        return cog;
    }
}
