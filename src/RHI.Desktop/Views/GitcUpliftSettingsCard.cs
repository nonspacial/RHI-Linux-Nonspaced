using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// button saves it too, then writes it into every Uplift game (<see cref="GitcUpliftPresenter"/>). w14/nr-2: the key is
/// captured as upstream does — click the box and press a key (Backspace or ↺ sets None) — and stored as before (the virtual-
/// key code).
/// </summary>
internal sealed class GitcUpliftSettingsCard
{
    private readonly TextBox _key;
    private int _vk;

    public GitcUpliftSettingsCard(LinuxAppSettings settings, GitcUpliftPresenter presenter, SettingsViewModel settingsVm, Action<string> showStatus)
    {
        _key = new TextBox
        {
            Name = "GitcUpliftEnableKey",
            IsReadOnly = true,
            PlaceholderText = UiLocalizationService.Text("Uplift.KeyPress"),
            FontFamily = new FontFamily("monospace"),
            MinWidth = 180
        };
        ToolTip.SetTip(_key, UiLocalizationService.Text("Uplift.KeyTip"));
        ToolTip.SetShowDelay(_key, MiddleEllipsisText.TooltipDelay);
        // Tunnel: the key is the card's before the box handles it (Tab / Enter / Escape keep their normal job).
        _key.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) && e.Key != Avalonia.Input.Key.Back) return;
            if (GitcUpliftPresenter.VirtualKey(e.Key) is not { } vk) return;
            SetKey(vk);
            KeyChanged?.Invoke(); // #297 (upstream 2.9.2): saved the moment the key is pressed
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        var clear = new Button { Content = "↺", Padding = new Thickness(10, 6), Name = "GitcUpliftKeyClear" };
        ToolTip.SetTip(clear, UiLocalizationService.Text("Uplift.KeyClearTip"));
        ToolTip.SetShowDelay(clear, MiddleEllipsisText.TooltipDelay);
        clear.Click += (_, _) => { SetKey(0); KeyChanged?.Invoke(); };
        SetKey(settings.UpliftEnableKey);
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

        // #296 (upstream SettingsHandler.UpliftKey): Components › GITC Uplift card — [NR on/off key | box ↺], then the button.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        row.Children.Add(_key);
        row.Children.Add(clear);
        write.Classes.Add("settingsPrimary");
        var panel = new StackPanel { Spacing = 8, Name = "GitcUpliftCard" };
        panel.Children.Add(SettingsLayout.Row(UiLocalizationService.Text("Uplift.KeyLabel"), row, tip: UiLocalizationService.Text("Uplift.KeyTip")));
        panel.Children.Add(SettingsLayout.Block(write));
        Control = SettingsLayout.Card(UiLocalizationService.Text("Uplift.CardTitle"), null, panel);
    }

    public Control Control { get; }

    /// <summary>The user pressed a key or cleared it (Settings saves it straight away).</summary>
    public event Action? KeyChanged;

    /// <summary>The chosen key's virtual-key code (0 = None).</summary>
    public int Key => _vk;

    public void Reset(LinuxAppSettings defaults) => SetKey(defaults.UpliftEnableKey);

    /// <summary>Takes <paramref name="vk"/> (0 = None) and shows its name; None shows the "Press a key…" prompt.</summary>
    internal void SetKey(int vk)
    {
        _vk = vk is > 0 and <= 255 ? vk : 0;
        _key.Text = _vk == 0 ? "" : GitcUpliftPresenter.KeyName(_vk);
    }
}
