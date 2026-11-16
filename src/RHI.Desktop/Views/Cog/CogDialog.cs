using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/ui-settings (#297; upstream 2.8.9/2.9.0 <c>CogDialog.cs</c>, end state 2.9.4): builds a component ⚙ with the shared
/// layout — "&lt;Component&gt; Settings" title, scrolling body of cards, the setting names on the left with the option beside
/// them and the explanation under each, button rows, and the status line inside the ⚙ (results and errors show here, not
/// only in the main window's status line). Settings apply as soon as they change; the window has only Close, except slider
/// dialogs that opt into Apply / Cancel (<see cref="UseApplyButton"/>). A ⚙ with nothing added says it has no settings.
/// Add content top to bottom, then <see cref="Build"/> the window. The AXAML ⚙ views use the same pieces
/// (<see cref="CogCard"/>, <see cref="CogRow"/>, <see cref="CogCompactGrid"/>) inside <see cref="CogWindow"/>.
/// </summary>
public sealed class CogDialog
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly StackPanel _root = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { Name = "CogStatus", Classes = { "cogStatus" }, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    // The open section's card, or the untitled card for settings added before the first section.
    private CogCard? _section;
    private CogCard? _loose;
    private bool _hasContent;
    private bool _prepared;

    /// <param name="componentName">Display name, e.g. "ReShade"; the title becomes "ReShade Settings".</param>
    /// <param name="gameName">The selected game, shown in the window title.</param>
    public CogDialog(string componentName, string? gameName = null)
    {
        Title = TitleFor(componentName);
        GameName = gameName;
    }

    /// <summary>"&lt;Component&gt; Settings" (upstream's title rule; a name already ending in it is kept).</summary>
    public static string TitleFor(string componentName)
        => componentName.EndsWith(" Settings", StringComparison.Ordinal) ? componentName : UiLocalizationService.Format("Cog.Title", componentName);

    public string Title { get; }
    public string? GameName { get; }

    /// <summary>The scrolling body: cards and loose text, top to bottom (tests read it).</summary>
    public StackPanel Body => _root;

    /// <summary>The status line's text ("" when hidden) and tone.</summary>
    public string StatusText => _status.IsVisible ? _status.Text ?? "" : "";
    public CogStatusKind StatusKind { get; private set; }

    /// <summary>Apply / Cancel instead of Close (slider dialogs only); null for the usual Close.</summary>
    public Func<Task<bool>>? ApplyAction { get; private set; }

    // ── Text ───────────────────────────────────────────────────────────────────────

    /// <summary>Short explanation (grey), between cards or inside the open section.</summary>
    public TextBlock AddIntro(string text) => AddText(text, "cogIntro");

    /// <summary>Amber notice, e.g. "Install X on this game to change these settings."</summary>
    public TextBlock AddWarning(string text) => AddText(text, "cogWarning");

    /// <summary>Small grey note.</summary>
    public TextBlock AddNote(string text) => AddText(text, "cogNote");

    /// <summary>The body of a ⚙ with nothing to set (upstream <c>AddEmpty</c>): "This component has no settings." by default.</summary>
    public TextBlock AddEmpty(string? text = null) => AddText(text ?? T("Cog.NoSettings"), "cogEmpty");

    private TextBlock AddText(string text, string kind)
    {
        var block = CogLayout.Text(text, kind);
        Add(block);
        return block;
    }

    /// <summary>Starts a section: a card with a small uppercase heading. Everything added after it goes in the card.</summary>
    public CogCard AddSection(string header)
    {
        _loose = null;
        _section = new CogCard(header);
        _root.Children.Add(_section);
        _hasContent = true;
        return _section;
    }

    // ── Setting rows ───────────────────────────────────────────────────────────────

    /// <summary>A labelled row: name left, <paramref name="control"/> right, <paramref name="help"/> under it (and as the tooltip).</summary>
    public CogRow AddRow(string label, Control control, string? help = null)
    {
        var row = new CogRow(label, control, help);
        Add(row);
        return row;
    }

    /// <summary>
    /// A labelled dropdown that saves as soon as the user picks an item (never while the dialog is built): <paramref name="onChanged"/>
    /// gets the new index and returns whether it was saved. A refused pick, or one that throws, goes back to the previous
    /// item and the error shows in the status line.
    /// </summary>
    public ComboBox AddCombo(string label, string? help, IReadOnlyList<string> items, int selectedIndex, Func<int, Task<bool>> onChanged, bool isEnabled = true, string? name = null)
    {
        var combo = SavingCombo(label, items, selectedIndex, onChanged, isEnabled, name);
        AddRow(label, combo, help);
        return combo;
    }

    /// <summary>Synchronous <see cref="AddCombo(string, string?, IReadOnlyList{string}, int, Func{int, Task{bool}}, bool, string?)"/>.</summary>
    public ComboBox AddCombo(string label, string? help, IReadOnlyList<string> items, int selectedIndex, Func<int, bool> onChanged, bool isEnabled = true, string? name = null)
        => AddCombo(label, help, items, selectedIndex, i => Task.FromResult(onChanged(i)), isEnabled, name);

    /// <summary>A dropdown for the compact grid that saves on change, as <see cref="AddCombo(string, string?, IReadOnlyList{string}, int, Func{int, Task{bool}}, bool, string?)"/>.</summary>
    public CogCell CompactCombo(string label, string? help, IReadOnlyList<string> items, int selectedIndex, Func<int, Task<bool>> onChanged, bool isEnabled = true, string? name = null)
        => new(label, SavingCombo(label, items, selectedIndex, onChanged, isEnabled, name), help);

    /// <summary>Adds settings two per row (upstream <c>AddCompactGrid</c>); a null cell leaves its slot empty.</summary>
    public CogCompactGrid AddCompactGrid(params CogCell?[] cells)
    {
        var grid = new CogCompactGrid();
        foreach (var cell in cells) grid.Children.Add(cell ?? (Control)new Panel());
        Add(grid);
        return grid;
    }

    /// <summary>A dropdown with the shared look and no handler (for callers that wire it themselves).</summary>
    public static ComboBox MakeCombo(IReadOnlyList<string> items, int selectedIndex, bool isEnabled = true, string? name = null)
        => new() { Name = name, ItemsSource = items, SelectedIndex = selectedIndex, IsEnabled = isEnabled, HorizontalAlignment = HorizontalAlignment.Stretch };

    private ComboBox SavingCombo(string label, IReadOnlyList<string> items, int selectedIndex, Func<int, Task<bool>> onChanged, bool isEnabled, string? name)
    {
        var combo = MakeCombo(items, selectedIndex, isEnabled, name);
        var last = combo.SelectedIndex;
        var reverting = false;
        combo.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            var picked = combo.SelectedIndex;
            if (reverting || picked < 0 || picked == last) return;
            bool keep;
            combo.IsEnabled = false;
            try { keep = await onChanged(picked); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetError(UiLocalizationService.Format("Cog.ChangeFailed", label, UiLocalizationService.Present(ex.Message)));
                keep = false;
            }
            finally { combo.IsEnabled = isEnabled; }
            if (keep) { last = picked; return; }
            reverting = true;
            try { combo.SelectedIndex = last; }
            finally { reverting = false; }
        }, "CogDialog." + label);
        return combo;
    }

    /// <summary>A main control (stretched) with small buttons after it, for [TextBox][Set] or [ComboBox][Auto]; pass it to <see cref="AddRow"/>.</summary>
    public static Grid Inline(Control main, params Control[] after)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        main.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(main);
        for (var i = 0; i < after.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(after[i], i + 1);
            grid.Children.Add(after[i]);
        }
        return grid;
    }

    /// <summary>Custom content in the open section (counts as content, so the ⚙ is not "empty").</summary>
    public T AddContent<T>(T control) where T : Control
    {
        Add(control);
        return control;
    }

    // ── Buttons ────────────────────────────────────────────────────────────────────

    /// <summary>Button styling without a handler.</summary>
    public static Button StyledButton(string text, CogButtonKind kind, string? name = null)
    {
        var button = new Button { Name = name, Content = text, Classes = { "rhi" }, VerticalAlignment = VerticalAlignment.Center };
        if (kind == CogButtonKind.Secondary) button.Classes.Add("secondary");
        if (kind == CogButtonKind.Danger) button.Classes.Add("remove");
        return button;
    }

    /// <summary>A button in the shared style; a failing <paramref name="onClick"/> shows its error in the status line.</summary>
    public Button MakeButton(string text, CogButtonKind kind, Func<Task> onClick, string? tooltip = null, string? name = null)
    {
        var button = StyledButton(text, kind, name);
        CogLayout.Tip(button, tooltip);
        button.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            try { await onClick(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetError(UiLocalizationService.Format("Cog.ActionFailed", text, UiLocalizationService.Present(ex.Message)));
            }
        }, "CogDialog." + text);
        return button;
    }

    /// <summary>Synchronous <see cref="MakeButton(string, CogButtonKind, Func{Task}, string?, string?)"/>.</summary>
    public Button MakeButton(string text, CogButtonKind kind, Action onClick, string? tooltip = null, string? name = null)
        => MakeButton(text, kind, () => { onClick(); return Task.CompletedTask; }, tooltip, name);

    /// <summary>
    /// A grey "Open &lt;file&gt;" button (upstream <c>MakeOpenFileButton</c>): opens the file in the desktop's default app;
    /// disabled while the file is missing (its path is the 2 s tooltip). A file that went away, or an opener that fails,
    /// says so in the status line.
    /// </summary>
    public Button MakeOpenFileButton(string text, string? path, string? name = null)
    {
        var button = MakeButton(text, CogButtonKind.Secondary, () =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetError(UiLocalizationService.Format("Fg.FileNotFound", Path.GetFileName(path ?? text)));
                return;
            }
            var (opened, error) = ComponentFileButton.Open(path);
            if (!opened) SetError(UiLocalizationService.Present(error));
        }, path, name);
        button.Classes.Add("cogOpenFile");
        button.IsEnabled = !string.IsNullOrEmpty(path) && File.Exists(path);
        return button;
    }

    /// <summary>A row of buttons, left-aligned, wrapping onto a second line when needed.</summary>
    public WrapPanel AddButtons(params Button[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in buttons) row.Children.Add(button);
        Add(row);
        return row;
    }

    // ── Status line ────────────────────────────────────────────────────────────────

    /// <summary>Neutral status message inside the ⚙.</summary>
    public void SetStatus(string? text) => ShowStatus(text, CogStatusKind.Neutral);
    /// <summary>Green status message.</summary>
    public void SetSuccess(string? text) => ShowStatus(text, CogStatusKind.Success);
    /// <summary>Red status message.</summary>
    public void SetError(string? text) => ShowStatus(text, CogStatusKind.Error);
    /// <summary>Hides the status line.</summary>
    public void ClearStatus() => ShowStatus(null, CogStatusKind.Neutral);

    /// <summary>Shows a result line whose tone follows <paramref name="success"/>.</summary>
    public void SetResult(bool success, string? text) => ShowStatus(text, success ? CogStatusKind.Success : CogStatusKind.Error);

    private void ShowStatus(string? text, CogStatusKind kind)
    {
        StatusKind = kind;
        _status.Text = text;
        _status.IsVisible = !string.IsNullOrEmpty(text);
        _status.Classes.Set("success", kind == CogStatusKind.Success);
        _status.Classes.Set("error", kind == CogStatusKind.Error);
    }

    // ── Window ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply / Cancel instead of Close. Only for dialogs where applying on every change would be too expensive (upstream:
    /// RTX HDR and Auto HDR, whose sliders write to the driver). <paramref name="apply"/> returns whether to close.
    /// </summary>
    public void UseApplyButton(Func<Task<bool>> apply) => ApplyAction = apply;

    /// <summary>Finishes the body once: the "no settings" line when nothing was added, then the status line.</summary>
    public StackPanel Prepare()
    {
        if (_prepared) return _root;
        _prepared = true;
        if (!_hasContent) AddEmpty();
        _root.Children.Add(_status);
        return _root;
    }

    /// <summary>The ⚙ window (<see cref="CogWindow"/>); <paramref name="footer"/> stays pinned under the scrolling body.</summary>
    public Window Build(Control? footer = null, double height = CogWindow.DefaultHeight)
        => CogWindow.Create(Title, GameName, Prepare(), footer, ApplyAction, height);

    private void Add(Control control)
    {
        _hasContent = true;
        if (_section is not null)
        {
            _section.Children.Add(control);
            return;
        }
        // Before the first section: text between cards, settings in one untitled card until the next text.
        if (control is TextBlock)
        {
            _loose = null;
            _root.Children.Add(control);
            return;
        }
        if (_loose is null)
        {
            _loose = new CogCard();
            _root.Children.Add(_loose);
        }
        _loose.Children.Add(control);
    }
}

/// <summary>
/// The ⚙ window frame every component ⚙ uses (code-built <see cref="CogDialog"/> and the AXAML ⚙ views alike): the
/// "&lt;Component&gt; Settings" heading, the scrolling body, an optional footer pinned under it, and the button bar —
/// Close, or Apply / Cancel for slider dialogs. The window title adds the game's name.
/// </summary>
public static class CogWindow
{
    public const double DefaultWidth = 700;
    public const double DefaultHeight = 640;

    public static Window Create(string title, string? gameName, Control body, Control? footer = null, Func<Task<bool>>? apply = null,
        double height = DefaultHeight, double width = DefaultWidth)
    {
        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(gameName) ? title : $"{title} — {gameName}",
            Width = width,
            Height = height,
            MinWidth = 520,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.Content = Frame(title, body, footer, apply, close => window.Close(close));
        return window;
    }

    /// <summary>The window's content (tests read it without a windowing platform): heading, scroll, footer, button bar.</summary>
    public static Grid Frame(string title, Control body, Control? footer, Func<Task<bool>>? apply, Action<bool> close)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Name = "CogFrame" };
        grid.Children.Add(new TextBlock { Text = title, Classes = { "cogTitle" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 18, 22, 10) });
        var scroll = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(22, 0, 22, 14),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        if (footer is not null)
        {
            footer.Margin = new Thickness(22, 0, 22, 12);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (apply is not null)
        {
            var applyButton = new Button { Name = "CogApply", Classes = { "rhi" }, Content = UiLocalizationService.Text("Win.Apply"), MinWidth = 96 };
            applyButton.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
            {
                applyButton.IsEnabled = false;
                try { if (await apply()) close(true); }
                finally { applyButton.IsEnabled = true; }
            }, "CogWindow.Apply");
            buttons.Children.Add(applyButton);
        }
        var closeButton = new Button
        {
            Name = "CogClose", Classes = { "rhi", "secondary" }, MinWidth = 96,
            Content = UiLocalizationService.Text(apply is null ? "Win.Close" : "Win.Cancel")
        };
        closeButton.Click += (_, _) => close(false);
        buttons.Children.Add(closeButton);
        var bar = new Border { Classes = { "cogButtonBar" }, Padding = new Thickness(22, 12), Child = buttons };
        Grid.SetRow(bar, 3);
        grid.Children.Add(bar);
        return grid;
    }
}
