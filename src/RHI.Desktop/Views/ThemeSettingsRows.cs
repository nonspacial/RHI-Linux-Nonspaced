using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #301: the Colour theme card in Settings (upstream 2.9.3 Settings › General › Colour theme): the theme list, and for
/// Custom the nine colours (a swatch and #RRGGBB each, Look and Status colours side by side), the contrast warning, Start
/// from a theme…, Reset to default, Share theme and Import theme…. Everything is applied and saved by
/// <see cref="ThemeSettingsPresenter"/> as it changes.
/// </summary>
public sealed class ThemeSettingsRows
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly ThemeSettingsPresenter _presenter;
    private readonly Func<string, Task<bool>> _copy;
    private readonly ComboBox _themes;
    private readonly StackPanel _custom;
    private readonly TextBlock _contrast;
    private readonly TextBlock _status;
    private readonly Dictionary<string, (Border Swatch, TextBox Hex)> _rows = new(StringComparer.Ordinal);
    private readonly StackPanel _import;
    private readonly TextBox _importBox;
    private readonly TextBlock _importNote;

    public ThemeSettingsRows(ThemeSettingsPresenter presenter, Func<string, Task<bool>> copy)
    {
        _presenter = presenter;
        _copy = copy;
        var panel = new StackPanel { Spacing = 6 };
        // The Settings card holds the title and the intro (Theme.Title, Theme.Intro).

        _themes = new ComboBox { Name = "ColourTheme", ItemsSource = presenter.ThemeNames, Width = 240 };
        ToolTip.SetTip(_themes, T("Theme.ListTip"));
        ToolTip.SetShowDelay(_themes, MiddleEllipsisText.TooltipDelay);
        _themes.SelectionChanged += (_, _) =>
        {
            if (_themes.SelectedIndex >= 0 && _themes.SelectedIndex != presenter.SelectedThemeIndex)
                presenter.SelectedThemeIndex = _themes.SelectedIndex;
        };
        panel.Children.Add(_themes);

        _custom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Name = "ColourThemeCustom" };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,24,Auto") };
        var look = new StackPanel { Spacing = 6 };
        var statusColours = new StackPanel { Spacing = 6 };
        look.Children.Add(new TextBlock { Text = T("Theme.Look"), FontWeight = FontWeight.SemiBold, Opacity = 0.8 });
        statusColours.Children.Add(new TextBlock { Text = T("Theme.StatusColours"), FontWeight = FontWeight.SemiBold, Opacity = 0.8 });
        foreach (var row in presenter.CustomColours)
            (row.IsStatus ? statusColours : look).Children.Add(ColourRow(row));
        Grid.SetColumn(statusColours, 2);
        columns.Children.Add(look);
        columns.Children.Add(statusColours);
        _custom.Children.Add(columns);

        _contrast = new TextBlock { Text = T("Theme.ContrastWarning"), TextWrapping = TextWrapping.Wrap, Classes = { "warningText" }, Name = "ColourThemeContrast" };
        _custom.Children.Add(_contrast);

        // Import theme: paste a code (upstream's dialog, here a row under the buttons).
        _importBox = new TextBox { PlaceholderText = ThemePalette.SharePrefix + "…", FontFamily = new FontFamily("monospace"), MinWidth = 320, Name = "ImportThemeCode" };
        _importNote = new TextBlock { Text = T("Theme.ImportPrompt"), FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var importApply = new Button { Classes = { "rhi" }, Content = T("Theme.ImportButton"), Name = "ImportThemeApply" };
        importApply.Click += (_, _) => Import();
        _importBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Import();
        };
        var importRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        importRow.Children.Add(_importBox);
        importRow.Children.Add(importApply);
        _import = new StackPanel { Spacing = 6, IsVisible = false };
        _import.Children.Add(_importNote);
        _import.Children.Add(importRow);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        var startFrom = Button("Theme.StartFrom", "Theme.StartFromTip");
        startFrom.Click += (_, _) => ShowStartFromMenu(startFrom);
        var reset = Button("Theme.ResetCustom", "Theme.ResetCustomTip");
        reset.Click += (_, _) => presenter.ResetCustom();
        var share = Button("Theme.Share", "Theme.ShareTip");
        share.Name = "ShareTheme";
        share.Click += (_, _) => UiExceptionGuard.Forget(ShareAsync(), "ShareTheme");
        var import = Button("Theme.Import", "Theme.ImportTip");
        import.Name = "ImportTheme";
        import.Click += (_, _) =>
        {
            _import.IsVisible = !_import.IsVisible;
            if (_import.IsVisible) _importBox.Focus();
        };
        buttons.Children.Add(startFrom);
        buttons.Children.Add(reset);
        buttons.Children.Add(share);
        buttons.Children.Add(import);
        _custom.Children.Add(buttons);

        _custom.Children.Add(_import);
        panel.Children.Add(_custom);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.8, Name = "ColourThemeStatus" };
        panel.Children.Add(_status);

        presenter.PropertyChanged += OnPresenterChanged;
        foreach (var row in presenter.CustomColours) row.PropertyChanged += OnRowChanged;
        // The presenter outlives the dialog: let go of it when the dialog closes.
        panel.DetachedFromVisualTree += (_, _) =>
        {
            presenter.PropertyChanged -= OnPresenterChanged;
            foreach (var row in presenter.CustomColours) row.PropertyChanged -= OnRowChanged;
        };
        Control = panel;
        Sync();
    }

    public Control Control { get; }

    private Control ColourRow(ThemeColourRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // A plain brush on purpose: the swatch shows the picked colour itself, not a themed version (upstream).
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        var hex = new TextBox { Width = 96, FontFamily = new FontFamily("monospace"), Name = "ThemeColour-" + row.Key, MaxLength = 7 };
        ToolTip.SetTip(hex, UiLocalizationService.Format("Theme.ColourTip", row.Label));
        ToolTip.SetShowDelay(hex, MiddleEllipsisText.TooltipDelay);
        void Commit()
        {
            if (!string.Equals(hex.Text?.Trim(), row.Hex, StringComparison.OrdinalIgnoreCase)) _presenter.SetColour(row.Key, hex.Text);
        }
        hex.LostFocus += (_, _) => Commit();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };
        line.Children.Add(swatch);
        line.Children.Add(hex);
        line.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 });
        _rows[row.Key] = (swatch, hex);
        return line;
    }

    private static Button Button(string textKey, string tipKey)
    {
        var button = new Button { Classes = { "rhi" }, Content = T(textKey) };
        ToolTip.SetTip(button, T(tipKey));
        ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
        return button;
    }

    private void ShowStartFromMenu(Control anchor)
    {
        var menu = new ContextMenu();
        for (var i = 0; i < _presenter.PresetNames.Count; i++)
        {
            var index = i;
            var item = new MenuItem { Header = _presenter.PresetNames[i] };
            item.Click += (_, _) => _presenter.StartFrom(index);
            menu.Items.Add(item);
        }
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    private async Task ShareAsync()
    {
        var code = _presenter.ShareCode;
        var copied = await _copy(code);
        // Without a clipboard the code is shown in the box, ready to copy by hand (upstream shows it in its dialog).
        _status.Text = copied ? T("Theme.Copied") : UiLocalizationService.Format("Theme.CopyByHand", code);
    }

    private void Import()
    {
        var (imported, message) = _presenter.Import(_importBox.Text);
        _importNote.Text = imported ? T("Theme.ImportPrompt") : message;
        if (!imported) return;
        _importBox.Text = "";
        _import.IsVisible = false;
    }

    private void OnPresenterChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ThemeColourRow row) ShowRow(row);
    }

    private void Sync()
    {
        if (_themes.SelectedIndex != _presenter.SelectedThemeIndex) _themes.SelectedIndex = _presenter.SelectedThemeIndex;
        _custom.IsVisible = _presenter.IsCustom;
        _contrast.IsVisible = _presenter.ContrastWarning;
        if (!string.IsNullOrEmpty(_presenter.Status)) _status.Text = _presenter.Status;
        foreach (var row in _presenter.CustomColours) ShowRow(row);
    }

    private void ShowRow(ThemeColourRow row)
    {
        if (!_rows.TryGetValue(row.Key, out var controls)) return;
        controls.Hex.Text = row.Hex;
        controls.Swatch.Background = ThemePalette.TryHex(row.Hex, out var colour)
            ? new SolidColorBrush(Theming.ThemeResources.ToAvalonia(colour))
            : Brushes.Transparent;
    }

    /// <summary>The Settings dialog was reset or reopened: the saved choice again.</summary>
    public void Reload()
    {
        _presenter.Load();
        Sync();
    }
}
