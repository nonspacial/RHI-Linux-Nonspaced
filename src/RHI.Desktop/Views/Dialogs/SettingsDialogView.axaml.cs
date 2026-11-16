using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>
/// #296 (upstream 2.8.9/2.9.3 Settings page): the eight sections in upstream's order, each made of cards of one row layout
/// (<see cref="SettingsLayout"/>); on the left the section index with "Find a setting" and the Tabs button. The index
/// follows the scroll, Settings reopens on the section left last, Find centres and outlines the setting it finds, and
/// Tabs shows one section at a time with a tab strip. Rows other lanes built (code-built controls) are put into their
/// cards here and registered with <see cref="SettingsRowsHost"/>; every change is saved shortly after it is made.
/// </summary>
public sealed partial class SettingsDialogView : UserControl
{
    private const double NavWidth = 244;
    private const double ContentMaxWidth = 944;
    private const double MinContentWidth = 560;

    private sealed record Section(string Key, StackPanel Panel, Button NavButton, Button TabButton);

    private readonly List<Section> _sections = [];
    private bool _composed;
    private SettingsDialogViewModel? _model;
    private ScrollViewer? _scroll;
    private StackPanel? _content;
    private StackPanel? _results;
    private Border? _nav;
    private StackPanel? _navList;
    private StackPanel? _searchPanel;
    private Border? _topHost;
    private StackPanel? _topStack;
    private WrapPanel? _tabStrip;
    private TextBox? _search;
    private ToggleButton? _tabsToggle;
    private ColumnDefinition? _navColumn;
    private bool _topLayout;
    private string? _current;
    private string? _pinnedKey;
    private double _pinnedOffset = double.NaN;
    private DispatcherTimer? _searchDebounce;
    private readonly List<(Visual Target, Control Adorner)> _flashes = [];
    private DispatcherTimer? _flashTimer;

    public SettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (!_composed && DataContext is SettingsDialogViewModel model) Compose(model);
        };
    }

    private static string T(string key) => UiLocalizationService.Text(key);

    private void Compose(SettingsDialogViewModel model)
    {
        _composed = true;
        _model = model;
        model.BeginHosting(this); // a rebuilt template's rows replace this model's earlier ones (w6/vmsplit review 2)
        model.UseSaveScheduler(run => DispatcherTimer.RunOnce(run, TimeSpan.FromMilliseconds(250)));

        var sections = BuildSections(model);
        BuildPage(model, sections);
        model.Composed();

        // Save on change (upstream 2.8.9): every edit gesture inside the page asks for a save; the model writes only when
        // a setting really changed. The search box and the page's own controls are not settings.
        AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) => { if (IsSetting(e.Source)) model.SaveSoon(); }, RoutingStrategies.Bubble);
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, e) => { if (IsSetting(e.Source)) model.SaveSoon(); }, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, (_, e) => { if (IsSetting(e.Source)) model.SaveSoon(); }, RoutingStrategies.Bubble);
        AddHandler(NumericUpDown.ValueChangedEvent, (_, e) => { if (IsSetting(e.Source)) model.SaveSoon(); }, RoutingStrategies.Bubble);
    }

    private bool IsSetting(object? source) => source is Control control && !ReferenceEquals(control, _search) && !ReferenceEquals(control, _tabsToggle)
                                              && _content is not null && control.GetLogicalAncestors().Contains(_content);

    // ── Sections and their cards ─────────────────────────────────────────────

    private Dictionary<string, List<Control>> BuildSections(SettingsDialogViewModel model)
    {
        var vm = model.Shell;
        var settings = model.Settings;
        void Show(string line) => model.Status = line;
        var cards = SettingsSections.Order.ToDictionary(key => key, _ => new List<Control>());

        // Rows other lanes built: their values reach the saved settings through Host.
        var overlayHotkey = new ReShadeHotkeyEditor(settings.OverlayHotkey, "36,0,0,0");
        var screenshotHotkey = new ReShadeHotkeyEditor(settings.ScreenshotHotkey, "44,0,0,0");
        model.Host(new(
            current => { current.OverlayHotkey = overlayHotkey.Value; current.ScreenshotHotkey = screenshotHotkey.Value; },
            defaults => { overlayHotkey.SetValue(defaults.OverlayHotkey); screenshotHotkey.SetValue(defaults.ScreenshotHotkey); }));
        var optiScalerTemplates = new OptiScalerTemplateSettingsRows(settings, Path.Combine(model.Snapshot.ConfigRoot, "inis"), // w3/dllnames hook (#97)
            model.OpenFolder, Show);
        model.Host(new(current =>
        {
            current.OptiScalerGpuType = optiScalerTemplates.GpuType;
            current.OptiScalerDlssInputs = optiScalerTemplates.DlssInputs;
            current.OptiScalerDefaultVersion = optiScalerTemplates.DefaultVersion; // #270
            current.OptiScalerRemoveOldNightlyBuilds = optiScalerTemplates.RemoveOldNightlyBuilds;
        }, optiScalerTemplates.Reset));
        // w14/ui-logic rows
        var themeRows = new ThemeSettingsRows(vm.Theme, model.CopyText); // #301 colour theme (applied and saved as it changes; Read() keeps it)
        var fpsRow = new FpsLimitSettingsRow(settings); // #310 global FPS limit (saved on change)
        model.Host(new(current => current.GlobalFpsLimit = fpsRow.Value(settings.GlobalFpsLimit), defaults => fpsRow.Set(defaults.GlobalFpsLimit)));
        var updatePolicyRows = new UpdatePolicySettingsRows(settings); // #177 Follow newest DLSS / Streamline, #178 Update All includes
        model.Host(new(current =>
        {
            current.FollowNewestDlss = updatePolicyRows.FollowDlss;
            current.FollowNewestStreamline = updatePolicyRows.FollowStreamline;
            current.GlobalSkipReShadeUpdates = updatePolicyRows.Skips(ManagedComponentKind.ReShade);
            current.GlobalSkipRenoDxUpdates = updatePolicyRows.Skips(ManagedComponentKind.RenoDX);
            current.GlobalSkipReLimiterUpdates = updatePolicyRows.Skips(ManagedComponentKind.ReLimiter);
            current.GlobalSkipDisplayCommanderUpdates = updatePolicyRows.Skips(ManagedComponentKind.DisplayCommander);
            current.GlobalSkipOptiScalerUpdates = updatePolicyRows.Skips(ManagedComponentKind.OptiScaler);
            current.GlobalSkipReFrameworkUpdates = updatePolicyRows.Skips(ManagedComponentKind.REFramework);
        }, updatePolicyRows.Reset));
        var iniRows = new ReShadeIniSettingsRows(settings, vm.IniPresets, current => // #181: effect list, apply to all, INI templates
        {
            model.WriteReShadeFields(current);
            current.OverlayHotkey = overlayHotkey.Value;
            current.ScreenshotHotkey = screenshotHotkey.Value;
        }, model.OpenFolder, Show);
        model.Host(new(current => current.ReShadeVariableListUseTabs = iniRows.UseTabs, iniRows.Reset));
        var relimiterRows = new ReLimiterSettingsRows(settings, vm.IniPresets, Show); // #188
        model.Host(new(current =>
        {
            current.ReLimiterOsdHotkey = relimiterRows.OsdHotkey;
            current.ReLimiterSharedPresets = relimiterRows.SharedPresets;
            current.ReLimiterDlssHooks = relimiterRows.DlssHooks;
            current.ReLimiterTargetFps = relimiterRows.TargetFps;
        }, relimiterRows.Reset));
        var peakRows = new PeakBrightnessSettingsRows(settings, vm.IniPresets, Show); // #182
        model.Host(new(current =>
        {
            current.PeakNits = peakRows.Nits;
            current.PeakNitsEnabled = peakRows.Enabled;
            current.PeakNitsPresets = peakRows.Presets;
        }, peakRows.Reset));
        var optiScalerMenuKey = new OptiScalerMenuKeyRow(settings, vm.OptiScalerPresets, vm.Settings, Show); // #187
        model.Host(new(current => current.OptiScalerMenuKey = optiScalerMenuKey.Key, optiScalerMenuKey.Reset));
        var shaderSets = new ShaderSetsSettingsRows(vm, Show); // #179 global shader mode, set and add-ons (apply at once, not on Save)
        var gitcUplift = new GitcUpliftSettingsCard(settings, vm.GitcUplift, vm.Settings, Show); // #274 NR on/off key
        gitcUplift.KeyChanged += model.SaveSoon; // #297 (upstream): saved the moment the key is pressed
        model.Host(new(current => current.UpliftEnableKey = gitcUplift.Key, gitcUplift.Reset));
        // w14 lane rows: Settings rows (build the row, add it to its card below, Host its values)

        // ── General ── (upstream: Updates; Windows and Admin Mode)
        var parity = vm.Parity;
        // #298 (upstream SettingsHandler.UpdateCountdown): "Next check in" and "Last checked", ticking once a second while
        // Settings is open.
        var nextCheck = new TextBlock { Name = "SettingsNextCheck", FontFamily = new FontFamily("monospace") };
        var lastCheck = new TextBlock { Name = "SettingsLastCheck" };
        void Tick()
        {
            var now = vm.StatusBar.Now;
            nextCheck.Text = StatusBarText.Countdown(vm.StatusBar.NextCheck, now);
            lastCheck.Text = StatusBarText.LastChecked(vm.StatusBar.LastCheck, now);
        }
        Tick();
        var countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        countdown.Tick += (_, _) => Tick();
        nextCheck.AttachedToVisualTree += (_, _) => countdown.Start();
        nextCheck.DetachedFromVisualTree += (_, _) => countdown.Stop();
        cards[SettingsSections.General].Add(SettingsLayout.Card(T("Set.CardUpdates"), null,
            SettingsLayout.Row(T("Set.NextCheckIn"), nextCheck, tip: T("Set.CheckCadenceTip")),
            SettingsLayout.Row(T("Set.LastChecked"), lastCheck, tip: T("Set.CheckCadenceTip")),
            parity is null ? null : SettingsLayout.Block(ParitySettingsTab.AppUpdateSection(parity.AppUpdate)),
            updatePolicyRows.InclusionControl,
            SettingsLayout.SubHeading(T("Set.AutomaticUpdates")),
            SettingsLayout.Row(T("Set.AutoComponents"), SettingsLayout.OnOff(() => model.AutoUpdateComponents, v => model.AutoUpdateComponents = v, model,
                nameof(model.AutoUpdateComponents), "AutoUpdateComponents"), T("Set.AutoComponentsHelp"), T("Win.DisabledByDefaultWhenEnabledRHI")),
            updatePolicyRows.FollowControl,
            SettingsLayout.Help(T("Set.OwnUpdatesNote"))));
        var closeToTray = SettingsLayout.OnOff(() => model.CloseToTray, v => model.CloseToTray = v, model, nameof(model.CloseToTray), "CloseToTray");
        void SyncTray()
        {
            closeToTray.IsEnabled = model.CloseToTrayEnabled;
            SettingsLayout.Tip(closeToTray, model.CloseToTrayEnabled ? T("Set.CloseToTrayTip") : T("Set.CloseToTrayGreyed")); // #302: greyed says why
        }
        ToolTip.SetShowOnDisabled(closeToTray, true);
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.CloseToTrayEnabled)) SyncTray(); };
        SyncTray();
        // w14/layouts-a (#306, upstream 2.9.0–2.9.3 Windows and Admin Mode): Interface size, Top bar size and Game list size,
        // 50–200% in 5% steps, applied and saved as they change (the window's Ctrl + mouse wheel and edge drags set them too).
        ComboBox SizeChoice(string name, Func<int> get, Func<int, string?> set)
        {
            var labels = InterfaceSize.Options.Select(InterfaceSize.Label).ToList();
            var combo = new ComboBox { Name = name, ItemsSource = labels, MinWidth = 160, MaxDropDownHeight = 320 };
            combo.SelectedIndex = InterfaceSize.Options.ToList().IndexOf(InterfaceSize.Nearest(get()));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var percent = InterfaceSize.Options[combo.SelectedIndex];
                if (percent == InterfaceSize.Nearest(get())) return;
                if (set(percent) is { } refused) Show(refused);
            };
            return combo;
        }
        cards[SettingsSections.General].Add(SettingsLayout.Card(T("Set.CardWindows"), null,
            SettingsLayout.Row(T("Size.Interface"), SizeChoice("UiScale", () => vm.UiScale, vm.SetUiScale), tip: T("Size.InterfaceTip")),
            SettingsLayout.Row(T("Size.TopBar"), SizeChoice("TopBarScale", () => vm.TopBarScale, vm.SetTopBarScale), tip: T("Size.TopBarTip")),
            SettingsLayout.Row(T("Size.GameList"), SizeChoice("GameListScale", () => vm.GameListScale, vm.SetGameListScale), tip: T("Size.GameListTip")),
            SettingsLayout.Row(T("Set.TrayIcon"), SettingsLayout.OnOff(() => model.TrayIconEnabled, v => { model.TrayIconEnabled = v; model.TrayIconClicked(); },
                model, nameof(model.TrayIconEnabled), "TrayIcon"), T("Win.TraySupportDependsDesktopEnvironmentKDE"), T("Win.EnableSystemTrayIconStatusNotifierItemAppIndicat")),
            SettingsLayout.Row(T("Set.CloseToTray"), closeToTray, tip: T("Set.CloseToTrayTip")),
            SettingsLayout.Row(T("Ui.Language"), SettingsLayout.Choice(model.UiLanguages, () => model.UiLanguage, v => model.UiLanguage = v, model, nameof(model.UiLanguage), "UiLanguage"),
                T("Ui.LanguageNote")),
            // w14/layouts-a (#303): the View mode row (Compact view) left: the top bar's layout switch replaced it, as upstream.
            SettingsLayout.Row(T("Set.MotdAtStart"), SettingsLayout.OnOff(() => model.ShowMotdOnStartup, v => model.ShowMotdOnStartup = v, model,
                nameof(model.ShowMotdOnStartup), "ShowMotd"), tip: T("Win.ShowEachNewMOTDOnceAt"))));
        // #301 (upstream 2.9.3 Settings › General › Colour theme): the theme, Custom's colours, Share / Import theme.
        cards[SettingsSections.General].Add(SettingsLayout.Card(T("Theme.Title"), T("Theme.Intro"), SettingsLayout.Block(themeRows.Control)));

        // ── Accounts & Data ── (upstream: Accounts and mod data — GitHub token, Nexus Mods)
        if (parity is not null)
            cards[SettingsSections.Accounts].Add(SettingsLayout.Card(T("Set.CardAccounts"), null,
                SettingsLayout.Block(ParitySettingsTab.GitHubTokenSection(parity.GitHubToken)),
                SettingsLayout.Block(ParitySettingsTab.NexusSection(parity.NexusAccount))));

        // ── ReShade & RenoDX ── (upstream: Keys, screenshots and brightness; Shaders and addons)
        var screenshotFolder = SettingsLayout.Text(() => model.ScreenshotPath, v => model.ScreenshotPath = v, model, nameof(model.ScreenshotPath),
            T("Win.OptionalScreenshotFolder"), "ScreenshotPath");
        var browseScreenshots = SettingsLayout.Button(T("Win.Browse"), T("Set.ScreenshotBrowseTip"));
        browseScreenshots.Command = model.BrowseScreenshotsCommand;
        var applyToGame = SettingsLayout.Button(T("Set.ApplyToSelectedGame"), T("Set.ApplyToSelectedGameTip"), "ApplyReShadeDefaultsToGame");
        applyToGame.Command = model.ApplyToGameCommand;
        applyToGame.IsEnabled = model.CanApplyToGame;
        if (!model.CanApplyToGame)
        {
            ToolTip.SetShowOnDisabled(applyToGame, true);
            SettingsLayout.Tip(applyToGame, T("Set.ApplyToSelectedGameGreyed")); // #302
        }
        cards[SettingsSections.ReShade].Add(SettingsLayout.Card(T("Set.CardKeys"), null,
            SettingsLayout.SubHeading(T("Set.KeysAndScreenshots"), T("Set.KeysAndScreenshotsTip")),
            SettingsLayout.Row(T("Set.OverlayKey"), overlayHotkey.Control, T("Win.DefaultHomeRHIStoresHotkeyFormat"), T("Set.OverlayKeyTip")),
            SettingsLayout.Row(T("Set.ScreenshotKey"), screenshotHotkey.Control, T("Win.DefaultPrintScreenRHIStoresHotkey"), T("Set.ScreenshotKeyTip")),
            SettingsLayout.Row(T("Win.ScreenshotFolder"), SettingsLayout.Buttons(screenshotFolder, browseScreenshots), T("Win.EmptyKeepsSavePathAlreadyConfigured"), T("Set.ScreenshotFolderTip")),
            SettingsLayout.Row(T("Set.FolderPerGame"), SettingsLayout.OnOff(() => model.PerGameScreenshots, v => model.PerGameScreenshots = v, model,
                nameof(model.PerGameScreenshots), "PerGameScreenshots"), tip: T("Win.CreateScreenshotSubfolderEachGame")),
            iniRows.KeysControl,
            SettingsLayout.Block(applyToGame),
            peakRows.Control));
        var watchFolder = SettingsLayout.Text(() => model.AddonWatchFolder, v => model.AddonWatchFolder = v, model, nameof(model.AddonWatchFolder),
            T("Win.OptionalWatchedFolderDefaultDownloads"), "AddonWatchFolder");
        var browseWatch = SettingsLayout.Button(T("Win.Browse"), T("Set.WatchBrowseTip"));
        browseWatch.Command = model.BrowseWatchFolderCommand;
        cards[SettingsSections.ReShade].Add(SettingsLayout.Card(T("Set.CardShaders"), null,
            SettingsLayout.Block(shaderSets.Control),
            SettingsLayout.Row(T("Set.DownloadAllShaders"), SettingsLayout.OnOff(() => model.CacheAllShaders, v => model.CacheAllShaders = v, model,
                nameof(model.CacheAllShaders), "CacheAllShaders"), T("Win.KeepsDownloadedShaderPacksRHIXDG"), T("Set.DownloadAllShadersTip")),
            SettingsLayout.Row(T("Set.WatchAddons"), SettingsLayout.OnOff(() => model.AddonWatcherEnabled, v => model.AddonWatcherEnabled = v, model,
                nameof(model.AddonWatcherEnabled), "AddonWatcher"), tip: T("Win.WatchSelectedFolderDownloadedAddOns")),
            SettingsLayout.Row(T("Set.AddonWatchFolder"), SettingsLayout.Buttons(watchFolder, browseWatch), T("Win.NativeLinuxWatcherNeverDeletesSource"), T("Set.AddonWatchFolderTip"))));
        // Linux: the default ReShade channel and ReShade.ini search paths (upstream keeps the channel per game only).
        cards[SettingsSections.ReShade].Add(SettingsLayout.Card(T("Set.CardReShadeDefaults"), T("Win.NativeLinuxPersistenceRHICompatibleSettings"),
            SettingsLayout.Row(T("Win.DefaultReShadeChannel"), SettingsLayout.Choice(model.ReShadeChannels, () => model.ReShadeChannel, v => model.ReShadeChannel = v, model,
                nameof(model.ReShadeChannel), "ReShadeChannel"), tip: T("Set.DefaultChannelTip")),
            SettingsLayout.Row(T("Set.ManageIni"), SettingsLayout.OnOff(() => model.ManageIni, v => model.ManageIni = v, model, nameof(model.ManageIni), "ManageIni"),
                tip: T("Win.ManageReShadeIniSearchPaths"))));

        // ── Display & HDR ── (upstream: Display › When a game starts; #312 the HDR / Resolution buttons)
        cards[SettingsSections.Display].Add(SettingsLayout.Card(T("Set.CardDisplay"), null,
            SettingsLayout.SubHeading(T("Set.WhenAGameStarts")),
            SettingsLayout.Help(T("Set.WhenAGameStartsHelp")),
            SettingsLayout.Row(T("Set.HdrButton"), SettingsLayout.ShowHide(() => model.ShowHdrLaunchButton, v => model.ShowHdrLaunchButton = v, model,
                nameof(model.ShowHdrLaunchButton), "ShowHdrLaunchButton"), tip: T("Set.HdrButtonTip")),
            SettingsLayout.Row(T("Set.ResButton"), SettingsLayout.ShowHide(() => model.ShowResLaunchButton, v => model.ShowResLaunchButton = v, model,
                nameof(model.ShowResLaunchButton), "ShowResLaunchButton"), tip: T("Set.ResButtonTip"))));
        // w14/display-modes (#311, upstream 2.9.3 Settings › Display › Display modes): after the When a game starts card.
        cards[SettingsSections.Display].Add(new DisplayModesSettingsCard(vm.DisplayModes, () => TopLevel.GetTopLevel(this) as Window).Control);

        // ── DLSS / Streamline ── (upstream: Defaults; Indicator, frame generation and batch deploy)
        Control Version(string label, VersionChoice choice) => SettingsLayout.Row(label,
            SettingsLayout.Choice(choice.Items, () => choice.Selected, v => choice.Selected = v, choice, nameof(VersionChoice.Selected), minWidth: 200));
        Control Preset(PresetChoice choice)
        {
            var combo = new ComboBox { ItemsSource = choice.Items, SelectedItem = choice.Selected, MinWidth = 200, MaxDropDownHeight = 300 };
            combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OptionItem>((item, _) => new TextBlock { Text = item?.Display });
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is OptionItem<uint?> item) choice.Selected = item; };
            choice.PropertyChanged += (_, _) => combo.SelectedItem = choice.Selected;
            return SettingsLayout.Row(choice.Label, combo, tip: choice.Tip);
        }
        cards[SettingsSections.Dlss].Add(SettingsLayout.Card(T("Set.CardDefaults"), T("Win.QuickApplyBatchApplyReplaceOnly"),
            Version(T("Win.SuperResolutionNvngxDlssDll"), model.DefaultDlss),
            Version(T("Win.RayReconstructionNvngxDlssdDll"), model.DefaultDlssd),
            Version(T("Win.FrameGenerationNvngxDlssgDll"), model.DefaultDlssg),
            Version(T("Win.NeuralRenderingNvngxDlssnrDll"), model.DefaultDlssnr),
            Version("Streamline", model.DefaultStreamline),
            SettingsLayout.SubHeading(T("Dp.DefaultPresets"), model.DefaultPresetsTip),
            Preset(model.DefaultSrPreset),
            Preset(model.DefaultRrPreset),
            Preset(model.DefaultFgPreset),
            SettingsLayout.Help(T("Set.AutoDlssWhere"))));
        var batch = SettingsLayout.Primary(T("Set.BatchDeploy"), T("Ui.BatchDlssTip"), "SettingsBatchDeploy");
        batch.Click += (_, _) => model.OpenDlssBatch?.Invoke();
        batch.IsEnabled = model.OpenDlssBatch is not null && vm.CanOpenDlssBatch;
        ToolTip.SetShowOnDisabled(batch, true);
        if (!batch.IsEnabled) SettingsLayout.Tip(batch, T("Set.BatchDeployGreyed")); // #302
        cards[SettingsSections.Dlss].Add(SettingsLayout.Card(T("Set.CardBatch"), null,
            SettingsLayout.Help(T("Set.BatchDeployHelp")),
            SettingsLayout.Block(batch)));

        // ── Driver Settings ── (upstream: NVIDIA driver profile defaults; Linux: per-game launch settings, see the note)
        // #310 (upstream 2.8.9 Driver Settings › FPS Limit): on Linux the global cap is a launch variable (DXVK_FRAME_RATE /
        // VKD3D_FRAME_RATE), as the card's note describes for the per-game NVIDIA settings.
        cards[SettingsSections.Driver].Add(SettingsLayout.Card(T("Set.CardDriverDefaults"), T("Set.DriverDefaultsLinux"),
            SettingsLayout.Row(T("Fps.Label"), fpsRow.Control, T("Fps.GlobalNote"), T("Fps.GlobalTip"))));

        // ── Components ── (upstream: ReLimiter and OptiScaler; GITC Uplift; Custom OptiScaler builds)
        cards[SettingsSections.Components].Add(SettingsLayout.Card(T("Set.CardReLimiterOptiScaler"), null,
            relimiterRows.Control,
            optiScalerTemplates.Control,
            optiScalerMenuKey.Control));
        cards[SettingsSections.Components].Add(gitcUplift.Control);
        cards[SettingsSections.Components].Add(SettingsLayout.Card(T("Osc.Title"), null,
            Untitled(new OptiScalerCustomBuildsSettingsRows(vm.OptiScalerCustomBuilds).Control))); // #271 (applies at once)

        // ── Tools & Maintenance ── (upstream: Custom game folders first, Tools, Troubleshooting)
        cards[SettingsSections.Tools].Add(SettingsLayout.Card(T("CustomFolders.Tab"), null,
            Unwrap(CustomGameFoldersSettingsTab.Create(vm, Show, model.PickFolder)))); // #293
        if (Unwrap(XboxFoldersSettingsTab.Create(vm.Settings.XboxFolders, Show, model.PickFolder)) is { } xbox) // w3/scan (#90)
            cards[SettingsSections.Tools].Add(SettingsLayout.Card(XboxFoldersPresenter.Label, null, xbox));
        var paths = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 6 };
        void PathRow(int row, string label, string path)
        {
            var name = new TextBlock { Text = label, Classes = { "settingsHelp" } };
            var value = new SelectableTextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            Grid.SetRow(name, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            paths.Children.Add(name);
            paths.Children.Add(value);
        }
        PathRow(0, "settings.json", model.Snapshot.SettingsPath);
        PathRow(1, T("Win.Configuration"), model.Snapshot.ConfigRoot);
        PathRow(2, "Cache", model.Snapshot.CacheRoot);
        PathRow(3, T("Win.Data"), model.Snapshot.DataRoot);
        Button Folder(string text, string tip, System.Windows.Input.ICommand command)
        {
            var button = SettingsLayout.Button(text, tip);
            button.Command = command;
            return button;
        }
        cards[SettingsSections.Tools].Add(SettingsLayout.Card(T("Set.CardTools"), null,
            SettingsLayout.SubHeading(T("Set.DeployToMany")),
            iniRows.Control,
            SettingsLayout.SubHeading(T("Set.FoldersAndLogs"), T("Set.FoldersAndLogsTip")),
            SettingsLayout.Block(paths),
            SettingsLayout.Block(SettingsLayout.Buttons(
                Folder(T("Win.OpenConfiguration"), T("Set.OpenConfigurationTip"), model.OpenConfigurationCommand),
                Folder(T("Win.OpenCache"), T("Set.OpenCacheTip"), model.OpenCacheCommand),
                Folder(T("Win.OpenData"), T("Set.OpenDataTip"), model.OpenDataCommand))),
            Untitled(SupportSettingsTab.Create(vm.Support, Show, model.OpenFolder, model.CopyText).Content as Control), // #192 Copy logs, Open logs folder, Export game data
            SettingsLayout.Row(T("Win.VerboseLogging"), SettingsLayout.OnOff(() => model.VerboseLogging, v => model.VerboseLogging = v, model,
                nameof(model.VerboseLogging), "VerboseLogging"), T("Win.PreferencePersistedComponentLogsRemainUnder"))));
        var clearCache = SettingsLayout.Danger(T("Win.ClearCache"), T("Win.ClearCacheRemovesOnlyRHILinux"), "SettingsClearCache");
        clearCache.Click += async (_, _) =>
        {
            if (await SettingsLayout.ConfirmAsync(TopLevel.GetTopLevel(this) as Window, T("Win.ClearCache"), T("Set.ClearCacheAsk"), T("Win.ClearCache")))
                await model.ClearCacheCommand.ExecuteAsync(null);
        };
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.CanClearCache)) clearCache.IsEnabled = model.CanClearCache; };
        var reset = SettingsLayout.Danger(T("Ui.ResetSettings"), T("Set.ResetSettingsTip"), "SettingsReset");
        reset.Click += async (_, _) =>
        {
            if (await SettingsLayout.ConfirmAsync(TopLevel.GetTopLevel(this) as Window, T("Ui.ResetSettings"), T("Set.ResetSettingsAsk"), T("Ui.ResetSettings")))
                model.ResetCommand.Execute(null);
        };
        cards[SettingsSections.Tools].Add(SettingsLayout.Card(T("Set.CardTroubleshooting"), T("Set.TroubleshootingDesc"),
            SettingsLayout.Block(SettingsLayout.Buttons(clearCache, reset)),
            SettingsLayout.Help(T("Win.ClearCacheRemovesOnlyRHILinux")),
            SettingsLayout.SubHeading(T("Removed.Tab")),
            Unwrap(RemovedGamesSettingsTab.Create(vm, Show)))); // #293 Removed games
        // w14 lane rows: Settings cards (add to cards[SettingsSections.<Key>])
        return cards;
    }

    /// <summary>A former Settings tab's content as card rows: the tab's own big heading is left out (the card has the title).</summary>
    private static Control? Unwrap(TabItem tab)
    {
        if (!tab.IsVisible) return null;
        var content = tab.Content as Control;
        tab.Content = null;
        return Untitled(content);
    }

    private static Control? Untitled(Control? content)
    {
        if (content is ScrollViewer scroll)
        {
            var inner = scroll.Content as Control;
            scroll.Content = null;
            content = inner;
        }
        if (content is StackPanel panel)
        {
            panel.Margin = default;
            if (panel.Children.Count > 0 && panel.Children[0] is TextBlock { FontSize: >= 15 } title) panel.Children.Remove(title);
            foreach (var text in panel.Children.OfType<TextBlock>()) text.Classes.Add("settingsHelp");
        }
        return content;
    }

    // ── The page: index, Find a setting, Tabs, sections ──────────────────────

    private void BuildPage(SettingsDialogViewModel model, Dictionary<string, List<Control>> cards)
    {
        _content = new StackPanel { Spacing = 28, Margin = new Thickness(28, 24, 24, 40), MaxWidth = ContentMaxWidth, HorizontalAlignment = HorizontalAlignment.Left };
        _results = new StackPanel { Spacing = 10, IsVisible = false, Name = "SettingsSearchResults" };
        _content.Children.Add(_results);
        _navList = new StackPanel { Spacing = 2, Margin = new Thickness(24, 24, 0, 24) };
        _tabStrip = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, IsVisible = false };

        _search = new TextBox { PlaceholderText = T("Set.FindPlaceholder"), Name = "SettingsFind", MinHeight = 34 };
        SettingsLayout.Tip(_search, T("Set.FindTip"));
        _search.TextChanged += (_, _) => { _searchDebounce?.Stop(); _searchDebounce?.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _searchDebounce?.Stop(); ApplySearch(); e.Handled = true; }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.Handled = true; }
        };
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplySearch(); };
        _tabsToggle = new ToggleButton { Content = T("Set.Tabs"), IsChecked = model.Tabbed, Name = "SettingsTabsToggle", Padding = new Thickness(10, 6) };
        SettingsLayout.Tip(_tabsToggle, T("Set.TabsTip"));
        _tabsToggle.IsCheckedChanged += (_, _) =>
        {
            model.Tabbed = _tabsToggle.IsChecked == true;
            UpdateLayoutMode(Bounds.Width);
            ShowSection(_current ?? model.LastSection, scroll: true);
        };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        searchRow.Children.Add(_search);
        Grid.SetColumn(_tabsToggle, 1);
        searchRow.Children.Add(_tabsToggle);
        _searchPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        _searchPanel.Children.Add(new TextBlock { Text = T("Set.FindLabel"), FontSize = 11, Margin = new Thickness(4, 0), Classes = { "settingsHelp" } });
        _searchPanel.Children.Add(searchRow);
        _navList.Children.Add(_searchPanel);

        foreach (var key in SettingsSections.Order)
        {
            var panel = new StackPanel { Spacing = 12, Name = "SettingsSection" + key, Tag = "Section:" + key };
            panel.Children.Add(SettingsLayout.SectionHeader(SettingsSections.Title(key), SettingsSections.Description(key)));
            foreach (var card in cards[key]) panel.Children.Add(card);
            _content.Children.Add(panel);
            var nav = new Button { Content = SettingsSections.Title(key), Classes = { "settingsNav" }, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "SettingsNav" + key };
            nav.Click += (_, _) => { ClearSearch(); ShowSection(key, scroll: true); };
            _navList.Children.Add(nav);
            var tab = new Button { Content = SettingsSections.Title(key), Classes = { "settingsNav" }, Name = "SettingsTab" + key };
            tab.Click += (_, _) => { ClearSearch(); ShowSection(key, scroll: true); };
            _tabStrip.Children.Add(tab);
            _sections.Add(new(key, panel, nav, tab));
        }

        _scroll = new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Name = "SettingsScroll" };
        _scroll.ScrollChanged += (_, _) => UpdateHighlight();
        _nav = new Border
        {
            Width = NavWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "settingsNavColumn" },
            Child = new ScrollViewer { Content = _navList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        _topStack = new StackPanel { Spacing = 12 };
        _topStack.Children.Add(_tabStrip);
        _topHost = new Border { Padding = new Thickness(24, 16, 24, 0), MaxWidth = ContentMaxWidth, IsVisible = false, Child = _topStack, HorizontalAlignment = HorizontalAlignment.Left };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        _navColumn = new ColumnDefinition(NavWidth, GridUnitType.Pixel);
        root.ColumnDefinitions.Add(_navColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Grid.SetRowSpan(_nav, 2);
        Grid.SetColumn(_topHost, 1);
        Grid.SetColumn(_scroll, 1);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_nav);
        root.Children.Add(_topHost);
        root.Children.Add(_scroll);
        root.SizeChanged += (_, e) => UpdateLayoutMode(e.NewSize.Width);
        if (this.FindControl<Panel>("PageHost") is { } host) host.Children.Add(root);

        _current = model.LastSection;
        SetHighlight(_current);
        // Reopens where it was left (upstream SettingsLastSection): once laid out, the last section is scrolled to.
        AttachedToVisualTree += (_, _) => RestoreWhenLaidOut(0);
    }

    private void RestoreWhenLaidOut(int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_scroll is null || _model is null) return;
            if (_scroll.Extent.Height <= 0 || _scroll.Viewport.Height <= 0)
            {
                if (attempt < 20) RestoreWhenLaidOut(attempt + 1);
                return;
            }
            ShowSection(_model.LastSection, scroll: true);
        }, DispatcherPriority.Background);
    }

    /// <summary>Search on top and no index on a narrow window (or with Tabs, where the tab strip replaces the index).</summary>
    private void UpdateLayoutMode(double width)
    {
        if (_nav is null || _navColumn is null || _topHost is null || _searchPanel is null || _navList is null || _topStack is null || _content is null
            || _tabStrip is null || width <= 0) return;
        var tabbed = _model?.Tabbed == true;
        var top = width < NavWidth + MinContentWidth || tabbed;
        if (!top)
        {
            var spare = Math.Max(0, width - NavWidth - ContentMaxWidth - 52);
            _navColumn.Width = new GridLength(NavWidth + Math.Floor(spare / 2)); // the index and the cards sit together, centred
        }
        _tabStrip.IsVisible = tabbed;
        foreach (var section in _sections) section.Panel.IsVisible = (!tabbed || section.Key == _current) && !(_results?.IsVisible ?? false);
        if (top == _topLayout) return;
        _topLayout = top;
        if (_searchPanel.Parent is Panel parent) parent.Children.Remove(_searchPanel);
        if (top)
        {
            _searchPanel.Margin = default;
            _topStack.Children.Insert(0, _searchPanel);
            _topHost.IsVisible = true;
            _nav.IsVisible = false;
            _navColumn.Width = new GridLength(0);
        }
        else
        {
            _searchPanel.Margin = new Thickness(0, 0, 0, 10);
            _navList.Children.Insert(0, _searchPanel);
            _topHost.IsVisible = false;
            _nav.IsVisible = true;
        }
    }

    /// <summary>Goes to a section: with Tabs, shows only that one; otherwise scrolls it to the top.</summary>
    private void ShowSection(string key, bool scroll)
    {
        if (_scroll is null || _content is null) return;
        _current = SettingsSections.Restore(key);
        var tabbed = _model?.Tabbed == true;
        foreach (var section in _sections) section.Panel.IsVisible = !tabbed || section.Key == _current;
        SetHighlight(_current);
        if (!scroll) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_sections.FirstOrDefault(s => s.Key == _current) is not { } target) return;
            var top = tabbed ? 0 : target.Panel.TranslatePoint(default, _content)?.Y ?? 0;
            var offset = Math.Clamp(top, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
            _pinnedKey = _current;
            _pinnedOffset = offset;
            _scroll.Offset = new Vector(0, offset);
        }, DispatcherPriority.Background);
    }

    private void UpdateHighlight()
    {
        if (_scroll is null || _content is null || _results?.IsVisible == true) return;
        if (_model?.Tabbed == true) { SetHighlight(_current); return; }
        var tops = _sections.Where(s => s.Panel.IsVisible)
            .Select(s => (s.Key, Top: s.Panel.TranslatePoint(default, _content)?.Y ?? 0)).ToList();
        var key = SettingsScrollSpy.Highlight(tops, _scroll.Offset.Y, _scroll.Viewport.Height, _scroll.Extent.Height, _pinnedKey, _pinnedOffset);
        if (key is null) return;
        if (_pinnedKey is not null && key != _pinnedKey) _pinnedKey = null;
        _current = key;
        SetHighlight(key);
    }

    private void SetHighlight(string? key)
    {
        foreach (var section in _sections)
        {
            section.NavButton.Classes.Set("active", section.Key == key);
            section.TabButton.Classes.Set("active", section.Key == key);
        }
        if (key is not null && _model is not null) _model.LastSection = key; // remembered on close
    }

    // ── Find a setting ───────────────────────────────────────────────────────

    private void ClearSearch()
    {
        if (_search is not null && !string.IsNullOrEmpty(_search.Text)) _search.Text = "";
        _searchDebounce?.Stop();
        if (_results?.IsVisible == true) ApplySearch();
    }

    private void ApplySearch()
    {
        if (_results is null || _search is null) return;
        var query = _search.Text?.Trim() ?? "";
        _results.Children.Clear();
        if (query.Length == 0)
        {
            _results.IsVisible = false;
            UpdateLayoutMode(Bounds.Width);
            ShowSection(_current ?? SettingsSections.General, scroll: true);
            return;
        }
        var matches = SettingsSearch.Match(Index(), query);
        _results.IsVisible = true;
        foreach (var section in _sections) section.Panel.IsVisible = false;
        _results.Children.Add(new TextBlock { Text = T("Set.FindResults"), FontSize = 18, FontWeight = FontWeight.Bold });
        _results.Children.Add(new TextBlock { Text = SettingsSearch.CountText(matches.Count, query), TextWrapping = TextWrapping.Wrap, Classes = { "settingsHelp" } });
        foreach (var match in matches.Take(SettingsSearch.MaxResults))
        {
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(new TextBlock { Text = match.Display, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = match.Entry.Context, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "settingsResultContext" } });
            var row = new Button { Content = body, Classes = { "settingsResult" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            row.Click += (_, _) => GoTo(match.Entry);
            _results.Children.Add(row);
        }
        _scroll?.ScrollToHome();
    }

    /// <summary>Leaves the results, shows the setting's section, centres the setting and outlines it and its card.</summary>
    private void GoTo(SettingsSearchEntry entry)
    {
        if (_scroll is null || _content is null || _search is null) return;
        _search.Text = "";
        _searchDebounce?.Stop();
        if (_results is not null) { _results.Children.Clear(); _results.IsVisible = false; }
        ShowSection(entry.SectionKey, scroll: false);
        UpdateLayoutMode(Bounds.Width);
        var target = entry.Target as Control;
        var row = target is null ? null : FindRow(target, entry.Card as Control);
        Dispatcher.UIThread.Post(() =>
        {
            var anchor = row ?? target ?? entry.Card as Control;
            if (anchor?.TranslatePoint(default, _content) is not { } point) return;
            var offset = SettingsScrollSpy.CentreOffset(point.Y, anchor.Bounds.Height, _scroll.Viewport.Height, _scroll.Extent.Height);
            _pinnedKey = entry.SectionKey;
            _pinnedOffset = offset;
            _scroll.Offset = new Vector(0, offset);
            Flash(entry.Card as Control, row ?? target);
        }, DispatcherPriority.Background);
    }

    /// <summary>The setting's row (a settings row border), or the control itself when it is not in one.</summary>
    private static Control? FindRow(Control target, Control? card)
    {
        for (Control? node = target; node is not null && !ReferenceEquals(node, card); node = node.Parent as Control)
            if (node is Border border && border.Classes.Contains("settingsRow")) return border;
        return target;
    }

    private void Flash(Control? card, Control? row)
    {
        EndFlash();
        foreach (var (visual, thickness, pad) in new (Control? Visual, double Thickness, double Pad)[] { (card, 1.5, 0), (row, 1.5, 4) })
        {
            if (visual is null || ReferenceEquals(visual, card) && ReferenceEquals(row, card) && pad > 0) continue;
            var adorner = new Border
            {
                BorderThickness = new Thickness(thickness),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(-pad),
                IsHitTestVisible = false,
                Name = "SettingsFindOutline"
            };
            Theming.ThemeResources.Bind(adorner, Border.BorderBrushProperty, "RhiAccentBrush"); // #301: follows the colour theme
            AdornerLayer.SetAdorner(visual, adorner);
            _flashes.Add((visual, adorner));
        }
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _flashTimer.Tick -= FlashEnded;
        _flashTimer.Tick += FlashEnded;
        _flashTimer.Start();
    }

    private void FlashEnded(object? sender, EventArgs e) => EndFlash();

    private void EndFlash()
    {
        _flashTimer?.Stop();
        foreach (var (visual, _) in _flashes) AdornerLayer.SetAdorner(visual, null);
        _flashes.Clear();
    }

    /// <summary>Find a setting's results for <paramref name="query"/> over the page as built (tests).</summary>
    internal IReadOnlyList<SettingsSearchResult> Find(string query) => SettingsSearch.Match(Index(), query);

    /// <summary>
    /// Everything Find a setting searches, in page order: card titles, headings, setting names (with their help line and
    /// tooltip), button and check box texts with their tooltips. Built on each search, so rows that fill in later (folder
    /// lists, accounts) are found too.
    /// </summary>
    private List<SettingsSearchEntry> Index()
    {
        var entries = new List<SettingsSearchEntry>();
        foreach (var section in _sections)
        {
            var title = SettingsSections.Title(section.Key);
            Border? card = null;
            string? cardTitle = null, subHeading = null;
            // Where a setting is, as upstream's results show it: "Section › Card › Sub-heading".
            string Context() => string.Join(" › ", new[] { title, cardTitle, subHeading }.Where(t => !string.IsNullOrWhiteSpace(t)));
            void Walk(ILogical node)
            {
                if (node is Control { IsVisible: false }) return;
                switch (node)
                {
                    case Border border when border.Classes.Contains("settingsCard"):
                        card = border;
                        cardTitle = subHeading = null;
                        break;
                    case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                        if (text.Classes.Contains("settingsHelp"))
                        {
                            // A help line explains the setting before it: it is searched as that setting's description.
                            if (entries.Count > 0 && entries[^1].SectionKey == section.Key)
                                entries[^1] = entries[^1] with { Tooltip = string.Join("\n", new[] { entries[^1].Tooltip, text.Text }.Where(t => !string.IsNullOrWhiteSpace(t))) };
                            return;
                        }
                        if (text.Classes.Contains("settingsCardTitle"))
                        {
                            entries.Add(new(text.Text!, ToolTip.GetTip(text) as string, section.Key, title, text, card));
                            cardTitle = text.Text;
                            return;
                        }
                        if (text.Classes.Contains("settingsHead"))
                        {
                            subHeading = null;
                            entries.Add(new(text.Text!, ToolTip.GetTip(text) as string, section.Key, Context(), text, card));
                            subHeading = text.Text;
                            return;
                        }
                        entries.Add(new(text.Text!, ToolTip.GetTip(text) as string, section.Key, Context(), text, card));
                        return;
                    case ContentControl { Content: string label } control when control is Button or CheckBox or ToggleButton:
                        entries.Add(new(label, ToolTip.GetTip(control) as string, section.Key, Context(), control, card));
                        return;
                    case ItemsControl:
                        return; // never the items of a list or dropdown
                }
                foreach (var child in node.LogicalChildren) Walk(child);
            }
            foreach (var child in section.Panel.Children.Skip(1)) Walk(child); // [0] = the section's heading
        }
        return entries;
    }
}
