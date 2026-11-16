using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// #311 (upstream 2.9.3 <c>MainWindow.DisplayModes</c> mode switch): one button per display mode (the game's own is
/// highlighted; clicking it again turns the mode off) and a ⚙ for the game's display mode settings; with more than four
/// modes one button names the current mode and opens a menu. A reusable view: today's game page hosts it beside the
/// HDR / resolution rows, and other page layouts (Tabs, Spotlight) can host it the same way — <see cref="Attach"/> it
/// to the presenter, a "which game" function and the ⚙'s action. Hidden while display modes are off.
/// </summary>
public sealed class DisplayModeSwitchView : Border
{
    private DisplayModesPresenter? _presenter;
    private Func<DetectedGame?> _game = () => null;
    private Func<DetectedGame, Task>? _openSettings;
    private readonly WrapPanel _row = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };

    public DisplayModeSwitchView()
    {
        Child = _row;
        IsVisible = false;
    }

    /// <summary>Most modes shown as separate buttons (upstream: more than four become one button with a menu).</summary>
    public const int MaxButtons = 4;

    /// <summary>Connects the view; it follows the presenter's changes from then on.</summary>
    public void Attach(DisplayModesPresenter presenter, Func<DetectedGame?> game, Func<DetectedGame, Task>? openSettings)
    {
        _presenter = presenter;
        _game = game;
        _openSettings = openSettings;
        presenter.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayModesPresenter.SelectedButtons)) Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        Rebuild();
    }

    /// <summary>The mode buttons as shown (the harness and tests read them).</summary>
    public IReadOnlyList<Button> Buttons => _row.Children.OfType<Button>().ToList();

    public void Rebuild()
    {
        _row.Children.Clear();
        var game = _game();
        if (_presenter is not { Enabled: true } presenter || game is null)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var buttons = presenter.Buttons(game);
        if (buttons.Count <= MaxButtons)
            foreach (var mode in buttons)
            {
                var b = Chip(mode.Name, mode.Active, "DisplayMode-" + mode.Id, mode.Tip);
                var target = mode.Target;
                b.Click += async (_, _) => await presenter.SwitchFromUiAsync(game, target);
                _row.Children.Add(b);
            }
        else
        {
            var active = buttons.FirstOrDefault(m => m.Active);
            var b = Chip(UiLocalizationService.Format("Dm.MenuButton", active?.Name ?? UiLocalizationService.Text("Dm.Off")), active is not null, "DisplayMode-Menu",
                UiLocalizationService.Text("Dm.MenuButtonTip"));
            b.Click += (_, _) => ShowMenu(b, presenter, game, buttons);
            _row.Children.Add(b);
        }
        if (_openSettings is { } open)
        {
            var cog = Chip("⚙", false, "DisplayModeSettings", UiLocalizationService.Text("Dm.CogTip"));
            cog.Click += async (_, _) => await open(game);
            _row.Children.Add(cog);
        }
    }

    private static Button Chip(string text, bool active, string name, string tip)
    {
        var b = new Button { Content = text, Name = name, Padding = new Thickness(10, 4), Classes = { "chip" } };
        if (active) b.Classes.Add("active");
        ToolTip.SetTip(b, tip);
        ToolTip.SetShowDelay(b, MiddleEllipsisText.TooltipDelay);
        Avalonia.Automation.AutomationProperties.SetName(b, text == "⚙" ? UiLocalizationService.Text("Dm.CogTip") : text);
        return b;
    }

    private static void ShowMenu(Control anchor, DisplayModesPresenter presenter, DetectedGame game, IReadOnlyList<DisplayModesPresenter.ModeButton> buttons)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.FlyoutPresenterClasses.Add("overflowMenu");
        var anyActive = buttons.Any(b => b.Active);
        var off = new MenuItem { Header = (anyActive ? "" : "✓ ") + UiLocalizationService.Text("Dm.MenuOff"), Classes = { "overflowMenu" } };
        off.Click += async (_, _) => await presenter.SwitchFromUiAsync(game, null);
        menu.Items.Add(off);
        foreach (var mode in buttons)
        {
            var item = new MenuItem { Header = (mode.Active ? "✓ " : "") + mode.Name, Classes = { "overflowMenu" } };
            var id = mode.Id;
            item.Click += async (_, _) => await presenter.SwitchFromUiAsync(game, id);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
}

/// <summary>
/// #311 (upstream <c>DisplayModeEditor</c>): the editor of one display mode value — a dropdown for choices (first entry
/// "not set"), a box with Set / Clear for numbers and text, key + Ctrl/Shift/Alt for a ReShade key. Saves on change.
/// </summary>
internal static class DisplayModeValueEditor
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <param name="set">Saves a value (null = not set); returns a problem to show, or null.</param>
    /// <param name="report">Shows a result line (text, is error).</param>
    public static Control Build(DisplayModeItem item, string? value, string unsetLabel, Func<string?, Task<string?>> set, Action<string, bool> report,
        IEnumerable<string>? resolutions = null)
    {
        var label = DisplayModeText.Label(item);
        async Task Save(string? v)
        {
            var problem = await set(v);
            if (problem is not null) report(problem, true);
            else report(TF("Dm.ValueSaved", label, v is null ? unsetLabel : DisplayModeText.Format(item, v)), false);
        }

        switch (item.Kind)
        {
            case DisplayModeValueKind.Choice:
            {
                var choices = new List<DisplayModeChoice?> { null };
                choices.AddRange(DisplayModeText.Choices(item, resolutions));
                if (value is not null && !choices.Any(c => c?.Value.Equals(value, StringComparison.OrdinalIgnoreCase) == true))
                    choices.Add(new DisplayModeChoice(value, DisplayModeText.Format(item, value)));
                var labels = choices.Select(c => c?.Label ?? unsetLabel).ToList();
                var index = value is null ? 0 : choices.FindIndex(c => c?.Value.Equals(value, StringComparison.OrdinalIgnoreCase) == true);
                var combo = CogDialog.MakeCombo(labels, index, name: "DmValue-" + item.Key);
                combo.MinWidth = 200;
                var last = index;
                combo.SelectionChanged += async (_, _) =>
                {
                    if (combo.SelectedIndex < 0 || combo.SelectedIndex == last) return;
                    last = combo.SelectedIndex;
                    await Save(choices[combo.SelectedIndex]?.Value);
                };
                Avalonia.Automation.AutomationProperties.SetName(combo, label);
                return combo;
            }
            case DisplayModeValueKind.ReShadeKey:
            {
                // Not set, or a key and its modifiers (ReShade's own form "vk,ctrl,shift,alt" is stored).
                var keys = new List<ReShadeHotkeyCodec.KeyChoice?> { null };
                keys.AddRange(ReShadeHotkeyCodec.CommonKeys);
                ReShadeHotkeyCodec.Binding? binding = value is not null && ReShadeHotkeyCodec.TryParse(value, out var parsed) ? parsed : null;
                if (binding is { } b0 && !keys.Any(k => k?.KeyCode == b0.KeyCode)) keys.Add(new ReShadeHotkeyCodec.KeyChoice(b0.KeyCode, T("Ui.UnknownExistingKey")));
                var combo = CogDialog.MakeCombo(keys.Select(k => k?.DisplayName ?? unsetLabel).ToList(),
                    binding is { } b1 ? keys.FindIndex(k => k?.KeyCode == b1.KeyCode) : 0, name: "DmValue-" + item.Key);
                combo.MinWidth = 180;
                var ctrl = new CheckBox { Content = "Ctrl", IsChecked = binding?.Ctrl == true };
                var shift = new CheckBox { Content = "Shift", IsChecked = binding?.Shift == true };
                var alt = new CheckBox { Content = "Alt", IsChecked = binding?.Alt == true };
                var syncing = false;
                async Task Commit()
                {
                    if (syncing) return;
                    var key = combo.SelectedIndex > 0 ? keys[combo.SelectedIndex] : null;
                    foreach (var box in new[] { ctrl, shift, alt }) box.IsEnabled = key is not null;
                    await Save(key is null ? null : new ReShadeHotkeyCodec.Binding(key.KeyCode, ctrl.IsChecked == true, shift.IsChecked == true, alt.IsChecked == true).ToConfigValue());
                }
                foreach (var box in new[] { ctrl, shift, alt }) box.IsEnabled = binding is not null;
                combo.SelectionChanged += async (_, _) => await Commit();
                ctrl.IsCheckedChanged += async (_, _) => await Commit();
                shift.IsCheckedChanged += async (_, _) => await Commit();
                alt.IsCheckedChanged += async (_, _) => await Commit();
                Avalonia.Automation.AutomationProperties.SetName(combo, label);
                var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 4 };
                row.Children.Add(combo);
                row.Children.Add(ctrl);
                row.Children.Add(shift);
                row.Children.Add(alt);
                return row;
            }
            default:
            {
                var placeholder = item.Kind switch
                {
                    DisplayModeValueKind.Number or DisplayModeValueKind.Decimal => item.AllowZero
                        ? $"{unsetLabel} ({item.Min:0}–{item.Max:0}, 0 = {T("DmChoice.Off")})" : $"{unsetLabel} ({item.Min:0}–{item.Max:0})",
                    DisplayModeValueKind.ReLimiterKey => T("Dm.KeyPlaceholder"),
                    _ => unsetLabel
                };
                var box = new TextBox { Text = value ?? "", PlaceholderText = placeholder, MinWidth = 180, Name = "DmValue-" + item.Key };
                Avalonia.Automation.AutomationProperties.SetName(box, label);
                var setButton = CogDialog.StyledButton(T("Dm.Set"), CogButtonKind.Secondary);
                var clear = CogDialog.StyledButton(T("Dm.Clear"), CogButtonKind.Secondary);
                ToolTip.SetTip(clear, unsetLabel);
                setButton.Click += async (_, _) =>
                {
                    var text = box.Text ?? "";
                    if (text.Trim().Length == 0) { report(TF("Dm.EnterValue", label), true); return; }
                    if (item.Normalize(text) is not { } normalized)
                    {
                        report(item.Kind == DisplayModeValueKind.ReLimiterKey ? TF("Dm.InvalidKey", label)
                            : item.Kind == DisplayModeValueKind.Text ? TF("Dm.EnterValue", label)
                            : TF(item.AllowZero ? "Dm.ValueRangeZero" : "Dm.ValueRange", label, item.Min, item.Max), true);
                        return;
                    }
                    box.Text = normalized;
                    await Save(normalized);
                };
                clear.Click += async (_, _) => { box.Text = ""; await Save(null); };
                return CogDialog.Inline(box, setButton, clear);
            }
        }
    }
}

/// <summary>#311: the display mode dialogs — a mode's values (Edit…), a mode name, and a game's display mode settings (⚙).</summary>
internal static class DisplayModeDialogs
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>A mode's values: one row per item, grouped (upstream <c>ShowModeDefaultsAsync</c>).</summary>
    public static CogDialog ModeValues(DisplayModesPresenter presenter, string modeId, IEnumerable<string>? resolutions = null)
    {
        var name = DisplayModeText.ModeName(modeId, presenter.Store.NameOf(modeId));
        var cog = new CogDialog(TF("Dm.ModeTitle", name));
        cog.AddIntro(TF("Dm.ModeIntro", name));
        cog.AddNote(T("Dm.LinuxNote"));
        void Report(string text, bool error) { if (error) cog.SetError(text); else cog.SetSuccess(text); }
        foreach (var group in DisplayModeItems.Groups)
        {
            cog.AddSection(DisplayModeText.Group(group));
            foreach (var item in DisplayModeItems.All.Where(i => i.Group == group))
            {
                var key = item.Key;
                var editor = DisplayModeValueEditor.Build(item, presenter.Store.ModeDefault(modeId, key), T("Dm.NotSet"),
                    v => { presenter.SetModeValue(modeId, key, v); return Task.FromResult<string?>(null); }, Report, resolutions);
                var row = cog.AddRow(DisplayModeText.Label(item), editor, DisplayModeText.Help(item));
                row.Name = "DmRow-" + key;
            }
        }
        return cog;
    }

    /// <summary>Asks for a mode name, checked as it is typed. Null when cancelled.</summary>
    public static async Task<string?> PromptNameAsync(Window owner, DisplayModesPresenter presenter, string title, string prompt, string accept, string initial, string? renamingId)
    {
        var box = new TextBox { Text = initial, MaxLength = DisplayModeStore.MaxNameLength, PlaceholderText = T("Dm.NamePlaceholder"), Name = "DisplayModeName" };
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Classes = { "cogStatus", "error" }, IsVisible = false };
        var ok = SettingsLayout.Primary(accept, name: "DisplayModeNameAccept");
        ok.IsDefault = true;
        var cancel = SettingsLayout.Button(T("Win.Cancel"), name: "DisplayModeNameCancel");
        cancel.IsCancel = true;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Width = 420, MinWidth = 280, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel
        };
        void Check(bool showEmpty)
        {
            var problem = presenter.Store.ValidateName(box.Text, renamingId);
            ok.IsEnabled = problem is null;
            error.Text = problem is { } p ? DisplayModeText.NameProblem(p) : "";
            error.IsVisible = problem is not null && (showEmpty || !string.IsNullOrWhiteSpace(box.Text));
        }
        box.TextChanged += (_, _) => Check(false);
        ok.Click += (_, _) => { if (presenter.Store.ValidateName(box.Text, renamingId) is null) dialog.Close(true); else Check(true); };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        Check(false);
        return await dialog.ShowDialog<bool>(owner) ? box.Text!.Trim() : null;
    }

    /// <summary>
    /// The game's display mode settings (upstream <c>ShowDisplayModeGameDialogAsync</c>): its current mode, and a Custom
    /// mode just for this game (create, tick what it changes and their values, delete).
    /// </summary>
    public static async Task<CogDialog> GameSettingsAsync(DisplayModesPresenter presenter, DetectedGame game, bool running, IEnumerable<string>? resolutions = null)
    {
        var key = DisplayModesPresenter.Key(game);
        var store = presenter.Store;
        var items = DisplayModeItems.All;
        var reasons = items.ToDictionary(i => i.Key, i => presenter.UnavailableReason(game, i), StringComparer.OrdinalIgnoreCase);
        var inGame = await presenter.ReadCurrentAsync(game);

        var cog = new CogDialog(T("Dm.GameComponent"), game.Name);
        cog.AddIntro(TF("Dm.GameIntro", game.Name));
        if (!presenter.Enabled) cog.AddWarning(T("Dm.OffWarning"));
        if (running) cog.AddWarning(T("Dm.RunningWarning"));
        void Report(string text, bool error) { if (error) cog.SetError(text); else cog.SetSuccess(text); }

        // ── Mode ──
        cog.AddSection(T("Dm.SectionMode"));
        var modes = store.ModesFor(key).ToList();
        var rebuilding = false;
        List<string> Choices() => new[] { T("Dm.Off") }.Concat(modes.Select(m => DisplayModeText.ModeName(m.Id, m.Name))).ToList();
        int ActiveIndex() => modes.FindIndex(m => m.Id.Equals(store.ActiveModeId(key) ?? "", StringComparison.OrdinalIgnoreCase)) + 1;
        var customPanel = new StackPanel { Spacing = 8, Name = "DmCustomPanel" };
        ComboBox? modeCombo = null;
        modeCombo = cog.AddCombo(T("Dm.CurrentMode"), T("Dm.CurrentModeHelp"), Choices(), ActiveIndex(), async i =>
        {
            if (rebuilding) return true;
            if (!presenter.Enabled) { cog.SetError(T("Dm.TurnOnFirst")); return false; }
            var line = await presenter.SwitchAsync(game, i == 0 ? null : modes[i - 1].Id);
            cog.SetStatus(line);
            inGame = await presenter.ReadCurrentAsync(game);
            BuildCustom();
            return true;
        }, name: "DmCurrentMode");

        void RefreshModeCombo()
        {
            modes = store.ModesFor(key).ToList();
            if (modeCombo is null) return;
            rebuilding = true;
            modeCombo.ItemsSource = Choices();
            modeCombo.SelectedIndex = ActiveIndex();
            rebuilding = false;
        }

        // ── Custom mode for this game ──
        cog.AddSection(TF("Dm.SectionCustom", game.Name));
        cog.AddContent(customPanel);

        void BuildCustom()
        {
            customPanel.Children.Clear();
            if (!store.HasCustom(key))
            {
                customPanel.Children.Add(CogLayout.Text(T("Dm.CustomIntroNone"), "cogNote"));
                customPanel.Children.Add(cog.MakeButton(T("Dm.CreateCustom"), CogButtonKind.Primary, () =>
                {
                    presenter.CreateCustom(game);
                    RefreshModeCombo();
                    BuildCustom();
                    cog.SetSuccess(T("Dm.CustomCreated"));
                }, name: "DmCreateCustom"));
                return;
            }
            var inCustom = DisplayModeStore.IsCustom(store.ActiveModeId(key));
            customPanel.Children.Add(CogLayout.Text(T(inCustom ? "Dm.CustomInIntro" : "Dm.CustomOutIntro"), "cogNote"));
            foreach (var group in DisplayModeItems.Groups)
            {
                customPanel.Children.Add(new TextBlock { Text = DisplayModeText.Group(group), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0), Classes = { "cogHeading" } });
                foreach (var item in items.Where(i => i.Group == group)) customPanel.Children.Add(CustomRow(item));
            }
            var delete = cog.MakeButton(T("Dm.DeleteCustom"), CogButtonKind.Danger, async () =>
            {
                var line = await presenter.DeleteCustomAsync(game);
                RefreshModeCombo();
                inGame = await presenter.ReadCurrentAsync(game);
                BuildCustom();
                cog.SetSuccess(line ?? T("Dm.CustomDeleted"));
            }, T("Dm.DeleteCustomTip"), "DmDeleteCustom");
            delete.Margin = new Thickness(0, 8, 0, 0);
            customPanel.Children.Add(delete);
        }

        // One setting: tick | name | its value (only while ticked)
        Control CustomRow(DisplayModeItem item)
        {
            var on = store.InCustom(key, item.Key);
            var reason = reasons.GetValueOrDefault(item.Key);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{CogLayout.LabelColumnWidth + 60},*"), ColumnSpacing = CogLayout.ColumnGap, Name = "DmCustom-" + item.Key };
            var tick = new CheckBox { Content = new TextBlock { Text = DisplayModeText.Label(item), FontSize = 12, TextWrapping = TextWrapping.Wrap }, IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            CogLayout.Tip(tick, reason is null ? DisplayModeText.Help(item) : DisplayModeText.Help(item) + "\n\n" + TF("Dm.NotAvailableNow", reason));
            tick.IsCheckedChanged += async (_, _) => await TickAsync(item, tick.IsChecked == true);
            grid.Children.Add(tick);
            Control right;
            if (on)
                right = DisplayModeValueEditor.Build(item, store.CustomValue(key, item.Key), T("Dm.UnsetNormal"),
                    v => presenter.SetCustomItemAsync(game, item, true, v), Report, resolutions);
            else
                right = new TextBlock
                {
                    Text = reason is not null ? TF("Dm.NotAvailableNow", reason)
                        : inGame.TryGetValue(item.Key, out var now) && now is not null ? TF("Dm.NotChangedNow", DisplayModeText.Format(item, now))
                        : T("Dm.NotChanged"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Classes = { "cogHelp" }
                };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        async Task TickAsync(DisplayModeItem item, bool on)
        {
            // A newly ticked setting starts at what the game has now, so ticking alone changes nothing (upstream).
            var value = store.CustomValue(key, item.Key) ?? inGame.GetValueOrDefault(item.Key);
            if (await presenter.SetCustomItemAsync(game, item, on, value) is { } problem) cog.SetError(problem);
            BuildCustom();
        }

        BuildCustom();
        return cog;
    }
}

/// <summary>
/// #311 (upstream 2.9.3 Settings › Display › "Display modes" card): Show on game pages, the modes (Edit… / Rename / Move
/// up / Move down / Delete, Add mode) and Switch all games, on the Settings card and row layout (<see cref="SettingsLayout"/>).
/// Rebuilt when the modes change.
/// </summary>
internal sealed class DisplayModesSettingsCard
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly DisplayModesPresenter _presenter;
    private readonly Func<Window?> _owner;
    private readonly Func<IEnumerable<string>?> _resolutions;
    private readonly StackPanel _body = new() { Spacing = 10, Name = "DisplayModesBody" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false, Name = "DisplayModesStatus", Classes = { "cogStatus" } };
    private bool _busy;

    public DisplayModesSettingsCard(DisplayModesPresenter presenter, Func<Window?> owner, Func<IEnumerable<string>?>? resolutions = null)
    {
        _presenter = presenter;
        _owner = owner;
        _resolutions = resolutions ?? (() => null);
        var note = new TextBlock { Text = T("Dm.CardNote"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "cogWarning" } }; // upstream's amber note
        Control = SettingsLayout.Card(T("Dm.CardTitle"), T("Dm.CardDesc"), note, _body, _status);
        Control.Name = "DisplayModesCard";
        Action rebuild = () => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        Control.AttachedToVisualTree += (_, _) => { presenter.Changed -= rebuild; presenter.Changed += rebuild; };
        Control.DetachedFromVisualTree += (_, _) => presenter.Changed -= rebuild;
        Rebuild();
    }

    public Border Control { get; }

    public void Rebuild()
    {
        _body.Children.Clear();
        var store = _presenter.Store;
        var modes = store.Modes;

        // ── On / off ──
        _body.Children.Add(SettingsLayout.Row(T("Dm.ShowOnPages"), SettingsLayout.OnOff(() => _presenter.Enabled, v => _presenter.Enabled = v,
            _presenter, nameof(DisplayModesPresenter.Enabled), "DisplayModesEnabled"), tip: T("Dm.ShowOnPagesTip")));

        // ── Modes ──
        _body.Children.Add(SettingsLayout.SubHeading(T("Dm.Modes")));
        for (var i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            var id = m.Id;
            var name = m.Name;
            var set = DisplayModeItems.All.Count(it => store.ModeDefault(id, it.Key) is not null);
            var label = set == 0 ? TF("Dm.ModeNothingSet", name) : set == 1 ? TF("Dm.ModeOneSetting", name) : TF("Dm.ModeSettings", name, set);
            var edit = Action(SettingsLayout.Button(T("Dm.Edit"), TF("Dm.EditTip", name), "DmEdit-" + id), async () =>
            {
                if (_owner() is not { } owner) return;
                var window = DisplayModeDialogs.ModeValues(_presenter, id, _resolutions()).Build();
                await window.ShowDialog(owner);
            });
            var rename = Action(SettingsLayout.Button(T("Dm.Rename"), TF("Dm.RenameTip", name), "DmRename-" + id), async () =>
            {
                if (_owner() is not { } owner) return;
                var newName = await DisplayModeDialogs.PromptNameAsync(owner, _presenter, T("Dm.RenameTitle"), TF("Dm.RenamePrompt", name), T("Dm.Rename"), name, id);
                if (newName is not null && _presenter.RenameMode(id, newName)) Status(TF("Dm.Renamed", name, newName), false);
            });
            var up = Action(SettingsLayout.Button(T("Dm.MoveUp"), T("Dm.MoveUpTip"), "DmUp-" + id), () => { _presenter.MoveMode(id, -1); return Task.CompletedTask; });
            up.IsEnabled = i > 0;
            var down = Action(SettingsLayout.Button(T("Dm.MoveDown"), T("Dm.MoveDownTip"), "DmDown-" + id), () => { _presenter.MoveMode(id, 1); return Task.CompletedTask; });
            down.IsEnabled = i < modes.Count - 1;
            var buttons = new List<Control> { edit, rename, up, down };
            if (modes.Count > 1)
                buttons.Add(Action(SettingsLayout.Danger(T("Dm.Delete"), TF("Dm.DeleteTip", name), "DmDelete-" + id), async () =>
                {
                    if (!await SettingsLayout.ConfirmAsync(_owner(), T("Dm.DeleteTitle"), TF("Dm.DeleteConfirm", name), T("Dm.Delete"))) return;
                    var (deleted, off) = await _presenter.DeleteModeAsync(id);
                    if (deleted) Status(off > 0 ? TF("Dm.DeletedOff", name, off) : TF("Dm.Deleted", name), false);
                }));
            _body.Children.Add(SettingsLayout.Row(label, SettingsLayout.Buttons([.. buttons])));
        }
        var add = Action(SettingsLayout.Primary(T("Dm.AddMode"), TF("Dm.AddModeTip", DisplayModeStore.MaxModes), "DmAdd"), async () =>
        {
            if (_owner() is not { } owner) return;
            var newName = await DisplayModeDialogs.PromptNameAsync(owner, _presenter, T("Dm.AddTitle"), T("Dm.AddPrompt"), T("Dm.Add"), "", null);
            if (newName is not null && _presenter.AddMode(newName) is not null) Status(TF("Dm.Added", newName), false);
        });
        add.IsEnabled = modes.Count < DisplayModeStore.MaxModes;
        _body.Children.Add(SettingsLayout.Block(SettingsLayout.Buttons(add)));

        // ── Switch all games ──
        _body.Children.Add(SettingsLayout.SubHeading(T("Dm.SwitchAll")));
        var names = modes.Select(m => m.Name).Append(T("Dm.Off")).ToList(); // Off last: turns every game's mode off
        var all = new ComboBox { ItemsSource = names, SelectedIndex = 0, MinWidth = 160, Name = "DmSwitchAllMode" };
        Avalonia.Automation.AutomationProperties.SetName(all, T("Dm.SwitchEveryGameTo"));
        var go = Action(SettingsLayout.Primary(T("Dm.Switch"), T("Dm.SwitchTip"), "DmSwitchAll"), async () =>
        {
            var i = all.SelectedIndex;
            if (i < 0 || i > modes.Count) return;
            await SwitchAllAsync(i == modes.Count ? null : modes[i].Id, i == modes.Count ? T("Dm.Off") : modes[i].Name);
        });
        go.IsEnabled = _presenter.Enabled;
        _body.Children.Add(SettingsLayout.Row(T("Dm.SwitchEveryGameTo"), SettingsLayout.Buttons(all, go),
            _presenter.Enabled ? T("Dm.SwitchAllHelp") : T("Dm.SwitchAllHelpOff")));
    }

    private async Task SwitchAllAsync(string? modeId, string modeName)
    {
        var off = modeId is null;
        if (!await SettingsLayout.ConfirmAsync(_owner(), T(off ? "Dm.TurnOffAllTitle" : "Dm.SwitchAllTitle"),
                off ? T("Dm.TurnOffAllConfirm") : TF("Dm.SwitchAllConfirm", modeName), T(off ? "Dm.TurnOffAction" : "Dm.Switch"))) return;
        var progress = new Progress<(string Line, double Percent)>(p => Status(p.Line, false));
        var (switched, running) = await _presenter.SwitchAllAsync(modeId, progress);
        var line = off ? TF("Dm.TurnedOffAll", switched) : TF("Dm.SwitchedAll", switched, modeName);
        if (running > 0) line += " " + TF("Dm.RunningSkipped", running);
        Status(line, false);
    }

    private Button Action(Button button, Func<Task> click)
    {
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try { await click(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                RHI.Core.Services.AppLog.Warn("display-modes", "A display mode action failed", ex);
                Status(UiLocalizationService.Present(ex.Message), true);
            }
            finally { _busy = false; }
        };
        return button;
    }

    private void Status(string text, bool error)
    {
        _status.Text = text;
        _status.Classes.Set("error", error);
        _status.Classes.Set("success", !error);
        _status.IsVisible = true;
    }
}
