using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// #310: Settings' FPS Limit (upstream 2.8.9 Settings › FPS Limit): Off, upstream's VRR presets or Custom… (20–1000),
/// saved as it changes. Every game RHI launches through Wine/Proton gets it unless the game has its own FPS Limit or
/// ReLimiter / Display Commander (upstream's rule).
/// </summary>
public sealed class FpsLimitSettingsRow
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly ComboBox _choice;
    private readonly TextBox _custom;
    private readonly List<(string Label, uint Value)> _items;
    private const uint CustomMarker = uint.MaxValue;

    public FpsLimitSettingsRow(LinuxAppSettings settings)
    {
        _items = [(T("Fps.Off"), 0u)];
        _items.AddRange(FrameRateLimit.Presets.Select(p => (UiLocalizationService.Format("Fps.Preset", p.Fps, p.RefreshHz), p.Fps)));
        _items.Add((T("Fps.Custom"), CustomMarker));
        _choice = new ComboBox { Name = "GlobalFpsLimit", ItemsSource = _items.Select(i => i.Label).ToList(), MinWidth = 230, MaxDropDownHeight = 300 };
        _custom = new TextBox { Name = "GlobalFpsLimitCustom", PlaceholderText = $"{FrameRateLimit.Min}–{FrameRateLimit.Max}", Width = 90 };
        ToolTip.SetTip(_choice, T("Fps.GlobalTip"));
        ToolTip.SetShowDelay(_choice, MiddleEllipsisText.TooltipDelay);
        _choice.SelectionChanged += (_, _) => _custom.IsVisible = SelectedValue == CustomMarker;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_choice);
        row.Children.Add(_custom);
        Control = row; // Settings puts it in a row named Fps.Label with Fps.GlobalNote under it
        Set(settings.GlobalFpsLimit);
    }

    public Control Control { get; }

    private uint SelectedValue => _choice.SelectedIndex is var i && i >= 0 && i < _items.Count ? _items[i].Value : 0;

    /// <summary>The limit Save writes: 0 = Off; a Custom value outside 20–1000 keeps <paramref name="saved"/>.</summary>
    public uint Value(uint saved)
    {
        var selected = SelectedValue;
        if (selected != CustomMarker) return selected;
        return uint.TryParse(_custom.Text?.Trim(), out var fps) && FrameRateLimit.Valid(fps) is { } valid ? valid : saved;
    }

    public void Set(uint fps)
    {
        var at = _items.FindIndex(i => i.Value == fps && i.Value != CustomMarker);
        _choice.SelectedIndex = at >= 0 ? at : _items.Count - 1;
        _custom.Text = at >= 0 ? "" : fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _custom.IsVisible = at < 0;
    }
}
