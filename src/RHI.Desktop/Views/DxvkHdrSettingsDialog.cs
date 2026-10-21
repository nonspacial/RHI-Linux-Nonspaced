using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #200 DX9 HDR ⚙ dialog: the dxvk.conf preset (upstream's six tiers, safest first) with an Apply button, a result line,
/// and — while the game's HDR launch is Off — a line naming the control that turns it on. Existing controls only; the
/// preset's meaning is in a 2 s tooltip.
/// </summary>
public static class DxvkHdrSettingsDialog
{
    public const double MinDialogWidth = 420;
    public const double MinDialogHeight = 260;

    public static Window Create(DxvkHdrPresenter presenter, string gameName)
    {
        static string T(string key) => UiLocalizationService.Text(key);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.DisplayName, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("Dxvk.DialogIntro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });

        var label = new TextBlock { Text = T("Dxvk.PresetLabel"), Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        var presets = new ComboBox
        {
            Name = "DxvkHdrPreset",
            ItemsSource = presenter.PresetItems,
            SelectedItem = presenter.CurrentPreset,
            MinWidth = 180
        };
        ToolTip.SetTip(presets, T("Dxvk.PresetTip"));
        ToolTip.SetShowDelay(presets, 2000);
        ToolTip.SetTip(label, T("Dxvk.PresetTip"));
        ToolTip.SetShowDelay(label, 2000);
        var apply = new Button { Name = "DxvkHdrApply", Classes = { "rhi" }, Content = T("Win.Apply"), Padding = new Thickness(12, 6) };
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 8 };
        row.Children.Add(label);
        row.Children.Add(presets);
        row.Children.Add(apply);
        panel.Children.Add(row);

        var hdrOff = new TextBlock { Name = "DxvkHdrHdrOff", Text = T("Dxvk.HdrOffHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.82, IsVisible = presenter.HdrLaunchOff };
        var result = new TextBlock { Name = "DxvkHdrResult", TextWrapping = TextWrapping.Wrap, Opacity = 0.82, IsVisible = false };
        panel.Children.Add(hdrOff);
        panel.Children.Add(result);

        apply.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            apply.IsEnabled = false;
            apply.Content = T("Dxvk.Applying");
            try
            {
                result.Text = await presenter.ApplyPresetAsync(presets.SelectedItem as string);
                result.IsVisible = true;
            }
            finally
            {
                apply.Content = T("Win.Apply");
                apply.IsEnabled = true;
                presets.SelectedItem = presenter.CurrentPreset;
            }
        }, nameof(DxvkHdrSettingsDialog));

        return new Window
        {
            Title = $"{RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.DisplayName} — {gameName}",
            Width = 560,
            Height = 320,
            MinWidth = MinDialogWidth,
            MinHeight = MinDialogHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
    }
}
