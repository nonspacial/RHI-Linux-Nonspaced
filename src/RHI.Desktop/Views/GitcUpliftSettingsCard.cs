using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/nr (#274): the "GITC Uplift" settings card (upstream 2.9.3 Settings › Components, right after the OptiScaler key):
/// the NR on/off key list and "Write key to all Uplift games". The dialog's Save stores the key (<see cref="Key"/>); the
/// button saves it too, then writes it into every Uplift game (<see cref="GitcUpliftPresenter"/>). Linux deviation: the key
/// is picked from a list (as the OptiScaler key here) instead of pressed into the box.
/// </summary>
internal sealed class GitcUpliftSettingsCard
{
    private readonly ComboBox _key;

    public GitcUpliftSettingsCard(LinuxAppSettings settings, GitcUpliftPresenter presenter, SettingsViewModel settingsVm, Action<string> showStatus)
    {
        _key = new ComboBox
        {
            Name = "GitcUpliftEnableKey",
            ItemsSource = GitcUpliftPresenter.Keys.Select(k => GitcUpliftPresenter.KeyName(k.Vk)).ToArray(),
            SelectedItem = GitcUpliftPresenter.KeyName(settings.UpliftEnableKey),
            MinWidth = 230
        };
        ToolTip.SetTip(_key, UiLocalizationService.Text("Uplift.KeyTip"));
        ToolTip.SetShowDelay(_key, MiddleEllipsisText.TooltipDelay);
        var write = new Button { Content = UiLocalizationService.Text("Uplift.WriteAll"), Padding = new Thickness(12, 6), Name = "GitcUpliftWriteAll" };
        ToolTip.SetTip(write, UiLocalizationService.Text("Uplift.WriteAllTip"));
        ToolTip.SetShowDelay(write, MiddleEllipsisText.TooltipDelay);
        write.Click += (_, _) => UiExceptionGuard.Forget(WriteAsync(), "GitcUpliftWriteAll");
        async Task WriteAsync()
        {
            write.IsEnabled = false;
            try { showStatus(await presenter.WriteKeyToAllAsync(Key, settingsVm.SaveUpliftEnableKey)); }
            finally { write.IsEnabled = true; }
        }

        var title = new TextBlock { Text = UiLocalizationService.Text("Uplift.CardTitle"), FontWeight = FontWeight.SemiBold, FontSize = 15 };
        var label = new TextBlock { Text = UiLocalizationService.Text("Uplift.KeyLabel"), FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(label, UiLocalizationService.Text("Uplift.KeyTip"));
        ToolTip.SetShowDelay(label, MiddleEllipsisText.TooltipDelay);
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        row.Children.Add(_key);
        row.Children.Add(write);
        var panel = new StackPanel { Spacing = 6, Name = "GitcUpliftCard" };
        panel.Children.Add(title);
        panel.Children.Add(label);
        panel.Children.Add(row);
        Control = panel;
    }

    public Control Control { get; }

    /// <summary>The chosen key's virtual-key code (0 = None).</summary>
    public int Key => GitcUpliftPresenter.Keys.FirstOrDefault(k => GitcUpliftPresenter.KeyName(k.Vk) == _key.SelectedItem as string).Vk;

    public void Reset(LinuxAppSettings defaults) => _key.SelectedItem = GitcUpliftPresenter.KeyName(defaults.UpliftEnableKey);
}
