using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/ui-settings (#297; upstream 2.9.4 <c>SettingsHandler.UpliftKey</c> "SetUpliftKey", and 2.9.4's "They now save as soon
/// as you press the key"): the shared one-key capture box — click it and press a key (Backspace or ↺ sets None; Tab, Enter
/// and Escape keep their normal job; a modifier alone is not a key). The key is saved the moment it is pressed through
/// <c>save</c> (null = saved, else the refusal line): a refused key goes back to the saved one and the line is shown.
/// Keys are Windows virtual-key codes as Uplift reads them (<see cref="GitcUpliftPresenter.VirtualKey"/>).
/// </summary>
public sealed class KeyCaptureBox : Control
{
    private readonly Func<int, string?> _save;
    private readonly Action<string?> _show;
    private int _vk;

    public KeyCaptureBox(int key, Func<int, string?> save, Action<string?>? show = null, string? name = null, string? tip = null, string? clearName = null)
    {
        _save = save;
        _show = show ?? (_ => { });
        Box = new TextBox
        {
            Name = name, IsReadOnly = true, PlaceholderText = UiLocalizationService.Text("Uplift.KeyPress"),
            FontFamily = new FontFamily("monospace"), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        CogLayout.Tip(Box, tip);
        // Tunnel: the key is the box's before the TextBox handles it.
        Box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) && e.Key != Avalonia.Input.Key.Back) return;
            if (GitcUpliftPresenter.VirtualKey(e.Key) is not { } vk) return;
            Press(vk);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Clear = new Button { Content = "↺", Padding = new Thickness(10, 6), Name = clearName ?? (name is null ? null : name + "Clear"), Classes = { "rhi", "secondary" } };
        CogLayout.Tip(Clear, UiLocalizationService.Text("Uplift.KeyClearTip"));
        Clear.Click += (_, _) => Press(0);
        Show(key);
        var root = CogDialog.Inline(Box, Clear);
        LogicalChildren.Add(root);
        VisualChildren.Add(root);
    }

    public TextBox Box { get; }
    public Button Clear { get; }

    /// <summary>The saved key's virtual-key code (0 = None).</summary>
    public int Key => _vk;

    /// <summary>Shows <paramref name="vk"/> without saving it (e.g. Settings' Reset to the defaults).</summary>
    public void Show(int vk)
    {
        _vk = vk is > 0 and <= 255 ? vk : 0;
        Box.Text = _vk == 0 ? "" : GitcUpliftPresenter.KeyName(_vk);
    }

    /// <summary>A key press (or ↺): saved at once; a refused key goes back to the saved one.</summary>
    public void Press(int vk)
    {
        vk = vk is > 0 and <= 255 ? vk : 0;
        if (vk == _vk) return;
        if (_save(vk) is { } refused)
        {
            _show(refused);
            Show(_vk);
            return;
        }
        Show(vk);
        _show(null);
    }
}
