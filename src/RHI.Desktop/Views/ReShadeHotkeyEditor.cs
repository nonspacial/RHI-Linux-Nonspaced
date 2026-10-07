using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Platform.Linux.Settings;
using RHI.Desktop.Localization;

namespace RHI.Desktop.Views;

/// <summary>
/// User-facing editor for ReShade shortcuts. The numeric four-integer format stays
/// internal; users choose a primary key and optional Ctrl/Shift/Alt modifiers.
/// </summary>
internal sealed class ReShadeHotkeyEditor
{
    private readonly List<ReShadeHotkeyCodec.KeyChoice> _choices;
    private readonly ComboBox _key;
    private readonly CheckBox _ctrl;
    private readonly CheckBox _shift;
    private readonly CheckBox _alt;
    private readonly TextBlock _summary;
    private readonly StackPanel _root;
    private readonly string _fallback;

    public ReShadeHotkeyEditor(string value, string fallback)
    {
        _fallback = fallback;
        _choices = ReShadeHotkeyCodec.CommonKeys.Select(LocalizeChoice).ToList();
        _key = new ComboBox
        {
            ItemsSource = _choices,
            MinWidth = 220,
            MaxWidth = 280,
        };
        _ctrl = new CheckBox { Content = "Ctrl", VerticalAlignment = VerticalAlignment.Center };
        _shift = new CheckBox { Content = "Shift", VerticalAlignment = VerticalAlignment.Center };
        _alt = new CheckBox { Content = "Alt", VerticalAlignment = VerticalAlignment.Center };
        _summary = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        controls.Children.Add(_key);
        controls.Children.Add(_ctrl);
        controls.Children.Add(_shift);
        controls.Children.Add(_alt);

        _root = new StackPanel { Spacing = 6 };
        _root.Children.Add(controls);
        _root.Children.Add(_summary);

        _key.SelectionChanged += (_, _) => UpdatePresentation();
        _ctrl.Click += (_, _) => UpdatePresentation();
        _shift.Click += (_, _) => UpdatePresentation();
        _alt.Click += (_, _) => UpdatePresentation();

        SetValue(value);
    }

    public Control Control => _root;

    public string Value
    {
        get
        {
            var selected = _key.SelectedItem as ReShadeHotkeyCodec.KeyChoice;
            var keyCode = selected?.KeyCode ?? ReShadeHotkeyCodec.ParseOrDefault(_fallback, "36,0,0,0").KeyCode;
            return new ReShadeHotkeyCodec.Binding(
                keyCode,
                _ctrl.IsChecked == true,
                _shift.IsChecked == true,
                _alt.IsChecked == true).ToConfigValue();
        }
    }

    public void SetValue(string? value)
    {
        var binding = ReShadeHotkeyCodec.ParseOrDefault(value, _fallback);
        var choice = _choices.FirstOrDefault(item => item.KeyCode == binding.KeyCode);
        if (choice is null)
        {
            choice = new ReShadeHotkeyCodec.KeyChoice(binding.KeyCode, UiLocalizationService.Text("Ui.UnknownExistingKey"));
            _choices.Insert(0, choice);
            _key.ItemsSource = null;
            _key.ItemsSource = _choices;
        }

        _key.SelectedItem = choice;
        _ctrl.IsChecked = binding.Ctrl;
        _shift.IsChecked = binding.Shift;
        _alt.IsChecked = binding.Alt;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        var binding = ReShadeHotkeyCodec.ParseOrDefault(Value, _fallback);
        var selected = _key.SelectedItem as ReShadeHotkeyCodec.KeyChoice;
        var keyName = selected?.DisplayName ?? UiLocalizationService.Text("Ui.UnknownExistingKey");
        var parts = new List<string>(4);
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Alt) parts.Add("Alt");
        parts.Add(keyName);
        _summary.Text = UiLocalizationService.Format("Ui.CurrentHotkey", string.Join(" + ", parts));
        ToolTip.SetTip(_root, UiLocalizationService.Format("Ui.ReShadeInternalFormat", binding.ToConfigValue()));
    }

    private static ReShadeHotkeyCodec.KeyChoice LocalizeChoice(ReShadeHotkeyCodec.KeyChoice choice)
    {
        var display = choice.KeyCode switch
        {
            19 => UiLocalizationService.Text("Key.Pause"),
            32 => UiLocalizationService.Text("Key.Space"),
            35 => UiLocalizationService.Text("Key.End"),
            36 => UiLocalizationService.Text("Key.Home"),
            37 => UiLocalizationService.Text("Key.LeftArrow"),
            38 => UiLocalizationService.Text("Key.UpArrow"),
            39 => UiLocalizationService.Text("Key.RightArrow"),
            40 => UiLocalizationService.Text("Key.DownArrow"),
            45 => UiLocalizationService.Text("Key.Insert"),
            46 => UiLocalizationService.Text("Key.Delete"),
            110 => UiLocalizationService.Text("Key.NumpadDecimal"),
            _ => choice.DisplayName
        };
        return new ReShadeHotkeyCodec.KeyChoice(choice.KeyCode, display);
    }
}
