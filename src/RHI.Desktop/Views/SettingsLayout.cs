using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #296 (upstream 2.8.9 Settings mock-up, Themes/DarkTheme.xaml Settings* styles): the one layout every Settings row uses —
/// cards with a title, setting rows with the name on the left and the control on the right, a short help line under the
/// row and the full explanation as the tooltip — plus upstream's red destructive buttons and their confirmations, which
/// default to Cancel. Colours come from the App.axaml resources (Border.settingsCard, TextBlock.settingsHelp, …), so
/// colour themes apply.
/// </summary>
internal static class SettingsLayout
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>A section's heading: title and one line saying what it holds.</summary>
    public static Control SectionHeader(string title, string description)
    {
        var header = new StackPanel { Spacing = 3, Classes = { "settingsSectionHeader" } };
        header.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Classes = { "settingsSectionTitle" } });
        header.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "settingsHelp" } });
        return header;
    }

    /// <summary>A card: title, optional description, then its rows.</summary>
    public static Border Card(string title, string? description, params Control?[] children)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Classes = { "settingsCardTitle" } });
        if (!string.IsNullOrWhiteSpace(description)) body.Children.Add(Help(description));
        foreach (var child in children)
            if (child is not null) body.Children.Add(child);
        return new Border { Classes = { "settingsCard" }, Child = body };
    }

    /// <summary>A heading inside a card ("Keys and screenshots").</summary>
    public static TextBlock SubHeading(string text, string? tip = null)
    {
        var heading = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Classes = { "settingsHead" } };
        Tip(heading, tip);
        return heading;
    }

    /// <summary>The short explanation under a setting.</summary>
    public static TextBlock Help(string text)
        => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "settingsHelp" } };

    /// <summary>
    /// One setting: name left, control right, the help line under both, the tooltip on the name and the control. A row is a
    /// Border (class settingsRow) so Find a setting can outline it.
    /// </summary>
    public static Border Row(string label, Control control, string? help = null, string? tip = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, RowSpacing = 3 };
        var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Classes = { "settingsLabel" } };
        grid.Children.Add(name);
        if (control is ComboBox { MinWidth: < 160 } combo) combo.MinWidth = 160; // every Off / On lines up
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        Grid.SetColumn(control, 1);
        Tip(name, tip ?? help);
        if (tip is not null && ToolTip.GetTip(control) is null) Tip(control, tip);
        if (!string.IsNullOrWhiteSpace(help))
        {
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            var line = Help(help);
            grid.Children.Add(line);
            Grid.SetRow(line, 1);
            Grid.SetColumnSpan(line, 2);
        }
        return new Border { Classes = { "settingsRow" }, Child = grid };
    }

    /// <summary>A block that is not a name/control pair (a list, a button row) kept as one outlined row.</summary>
    public static Border Block(Control content) => new() { Classes = { "settingsRow" }, Child = content };

    /// <summary>Buttons side by side, wrapping on a narrow window.</summary>
    public static WrapPanel Buttons(params Control[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    public static Button Button(string text, string? tip = null, string? name = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6) };
        if (name is not null) button.Name = name;
        Tip(button, tip);
        return button;
    }

    /// <summary>A primary action ("Write these to all games").</summary>
    public static Button Primary(string text, string? tip = null, string? name = null)
    {
        var button = Button(text, tip, name);
        button.Classes.Add("settingsPrimary");
        return button;
    }

    /// <summary>Upstream's red destructive button; pair it with <see cref="ConfirmAsync"/>.</summary>
    public static Button Danger(string text, string? tip = null, string? name = null)
    {
        var button = Button(text, tip, name);
        button.Classes.Add("rhi");
        button.Classes.Add("remove");
        return button;
    }

    public static void Tip(Control control, string? tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return;
        ToolTip.SetTip(control, tip);
        ToolTip.SetShowDelay(control, MiddleEllipsisText.TooltipDelay);
    }

    /// <summary>An Off / On dropdown (upstream's boolean settings), kept in step with <paramref name="get"/>.</summary>
    public static ComboBox OnOff(Func<bool> get, Action<bool> set, INotifyPropertyChanged? source = null, string? property = null, string? name = null)
        => Choice(OptionLists.OffOn.Items, () => OptionLists.OffOn.Display(get()), value => set(OptionLists.OffOn.Value(value)), source, property, name);

    /// <summary>A Show / Hide dropdown (#312), kept in step with <paramref name="get"/>.</summary>
    public static ComboBox ShowHide(Func<bool> get, Action<bool> set, INotifyPropertyChanged? source = null, string? property = null, string? name = null)
    {
        string[] items = [T("Set.Show"), T("Set.Hide")];
        return Choice(items, () => get() ? items[0] : items[1], value => set(value == items[0]), source, property, name);
    }

    /// <summary>A dropdown of <paramref name="items"/> that writes the picked item back and follows the source's changes.</summary>
    public static ComboBox Choice(IReadOnlyList<string> items, Func<string?> get, Action<string> set,
        INotifyPropertyChanged? source = null, string? property = null, string? name = null, double minWidth = 160)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedItem = get(), MinWidth = minWidth, MaxDropDownHeight = 320 };
        if (name is not null) combo.Name = name;
        var syncing = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (syncing || combo.SelectedItem is not string picked || picked == get()) return;
            set(picked);
        };
        Follow(combo, source, property, () =>
        {
            syncing = true;
            try { combo.SelectedItem = get(); }
            finally { syncing = false; }
        });
        return combo;
    }

    /// <summary>A text box committed on Enter or when it loses focus (a typed path is not saved on every key).</summary>
    public static TextBox Text(Func<string?> get, Action<string> set, INotifyPropertyChanged? source = null, string? property = null,
        string? placeholder = null, string? name = null, double minWidth = 300)
    {
        var box = new TextBox { Text = get() ?? "", PlaceholderText = placeholder, MinWidth = minWidth };
        if (name is not null) box.Name = name;
        void Commit()
        {
            var value = box.Text ?? "";
            if (value != (get() ?? "")) set(value);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        Follow(box, source, property, () => { if (!box.IsFocused) box.Text = get() ?? ""; });
        return box;
    }

    private static void Follow(Control control, INotifyPropertyChanged? source, string? property, Action sync)
    {
        if (source is null) return;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (property is null || e.PropertyName == property) Avalonia.Threading.Dispatcher.UIThread.Post(sync);
        };
        control.AttachedToVisualTree += (_, _) => { source.PropertyChanged -= handler; source.PropertyChanged += handler; };
        control.DetachedFromVisualTree += (_, _) => source.PropertyChanged -= handler;
    }

    /// <summary>
    /// Upstream's confirmation for a destructive action: the message, Cancel (the default: Enter and Esc both cancel) and
    /// the red action. True only when the action is clicked.
    /// </summary>
    public static Task<bool> ConfirmAsync(Window? owner, string title, string message, string accept)
    {
        if (owner is null) return Task.FromResult(false);
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7), IsDefault = true, IsCancel = true, Name = "ConfirmCancel" };
        var yes = Danger(accept, name: "ConfirmAccept");
        yes.Padding = new Thickness(14, 7);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(yes);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            MinWidth = 280,
            MaxWidth = Math.Max(280, owner.Bounds.Width - 40),
            MaxHeight = Math.Max(200, owner.Bounds.Height),
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        yes.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }
}
