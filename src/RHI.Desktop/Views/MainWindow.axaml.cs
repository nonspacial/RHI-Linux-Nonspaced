using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private static string ShaderCategoryDisplay(string category) => category switch
    {
        "Essential" => T("Win.Essential"),
        "Recommended" => T("Win.Recommended"),
        "Custom" => T("Win.Custom"),
        _ => T("Win.Extra")
    };

    private static string CatalogDescription(string? description, string displayName, bool shaderPack)
    {
        if (string.IsNullOrWhiteSpace(description))
            return shaderPack
                ? TF("Win.ReShadeShaderPack", displayName)
                : TF("Win.ReShadeAdd", displayName);

        var presented = UiLocalizationService.Present(description);
        if (UiLocalizationService.CurrentLanguage == "pt-BR" && string.Equals(presented, description, StringComparison.Ordinal))
            return shaderPack
                ? TF("Win.ReShadeShaderPack", displayName)
                : TF("Win.ReShadeAdd", displayName);
        return presented;
    }

    private static FilePickerFileType WindowsDllFileType => new(T("Win.WindowsDLL"))
    {
        Patterns = ["*.dll", "*.DLL"],
    };

    private static FilePickerFileType WindowsExecutableFileType => new(T("Win.WindowsExecutable"))
    {
        Patterns = ["*.exe", "*.EXE"],
    };

    private static FilePickerFileType DeepFriedChickenArchiveFileType => new(T("Win.DeepFriedChickenPackageZIP7Z"))
    {
        Patterns = ["*.zip", "*.ZIP", "*.7z", "*.7Z"],
    };

    /// <summary>The "Do not change" version option, in either language (a dialog can outlive a language switch).</summary>
    private static bool IsDoNotChange(string value) => OptionLists.IsDoNotChange(value);

    /// <summary>Event-handler body runner: nothing a handler throws reaches the dispatcher unreported (UI-05).</summary>
    private static async void RunUiSafe(Func<Task> work, [CallerMemberName] string context = "")
        => await UiExceptionGuard.RunAsync(work, context);

    /// <summary>Creates (if needed) and opens a folder; a failure to create it is reported like a failure to open it.</summary>
    private static bool TryOpenFolder(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        return LinuxDesktopOpener.TryOpenDirectory(path, out error);
    }

    private readonly DetailSectionLayoutStore _detailLayoutStore;
    private DetailSectionLayoutSettings _detailLayout;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _detailLayoutStore = new DetailSectionLayoutStore();
        _detailLayout = _detailLayoutStore.Load();
        ApplyDetailSectionLayout();
        InitializeLinuxUx();
        InitializeResponsive();
        InitializeSectionSummaries();
        // The full title and subtitle are only offered as a tooltip when the header had to trim them.
        foreach (var name in new[] { "SelectedTitleText", "SelectedSourceText" })
            if (this.FindControl<TextBlock>(name) is { } trimmed)
                ToolTip.AddToolTipOpeningHandler(trimmed, (_, e) =>
                    e.Cancel = !trimmed.TextLayout.TextLines.Any(line => line.HasCollapsed));
    }

    private void CopyHdrLaunchOptions_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var text = vm.Runtime.GetSelectedHdrLaunchOptions();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            vm.StatusText = T("Win.ClipboardUnavailableSession");
            return;
        }
        await clipboard.SetTextAsync(text);
        vm.StatusText = HdrLaunchText.Copied(vm.Runtime.SelectedHdrPlan, vm.SelectedGame);
    });


    private void AddGame_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel) return;
        if (!StorageProvider.CanOpen)
        {
            await ShowSimpleMessageAsync(T("Win.AddGame"), T("Win.SessionPortalDoesNotProvideFile"));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Win.SelectGameWindowsExecutable"),
            AllowMultiple = false,
            FileTypeFilter = [WindowsExecutableFileType],
        });
        if (files.Count == 0) return;
        var executable = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            await ShowSimpleMessageAsync(T("Win.AddGame"), T("Win.SelectedExecutableNotAvailableAsLocal"));
            return;
        }

        await ShowAddGameDialogForExecutableAsync(executable);
    });

    private void BatchDlss_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanOpenDlssBatch) return;

        IReadOnlyList<LinuxDlssBatchCandidate> candidates;
        try
        {
            // Null: another operation is running or the user cancelled the analysis; the status line says which.
            if (await vm.Dlss.GetDlssBatchCandidatesAsync() is not { } found) return;
            candidates = found;
        }
        catch (Exception ex)
        {
            await ShowSimpleMessageAsync(T("Win.DLSSBatch"), TF("Win.CouldNotAnalyzeLibrary", UiLocalizationService.Present(ex.Message)));
            return;
        }

        var eligible = candidates.Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (eligible.Count == 0)
        {
            await ShowSimpleMessageAsync(T("Win.DLSSBatch"), T("Win.NoEligibleGameLocalDLSSStreamline"));
            return;
        }

        static ComboBox VersionCombo(IEnumerable<string> versions, string current)
        {
            var items = new List<string> { OptionLists.DoNotChangeDisplay() };
            items.AddRange(versions.Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, StringComparer.OrdinalIgnoreCase))
                items.Add(current); // preserve an offline/persisted default even when the manifest is temporarily unavailable
            var selected = string.IsNullOrWhiteSpace(current)
                ? items[0]
                : items.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
            return new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 220 };
        }

        static string SelectedVersion(ComboBox combo)
        {
            var value = combo.SelectedItem?.ToString() ?? "";
            return IsDoNotChange(value) ? "" : value;
        }

        var defaults = vm.Dlss.DlssVersionDefaults;
        var sr = VersionCombo(vm.Dlss.DlssSrVersions, defaults.SuperResolution);
        var rr = VersionCombo(vm.Dlss.DlssRrVersions, defaults.RayReconstruction);
        var fg = VersionCombo(vm.Dlss.DlssFgVersions, defaults.FrameGeneration);
        var nr = VersionCombo(vm.Dlss.DlssNrVersions, defaults.NeuralRendering);
        var sl = VersionCombo(vm.Dlss.StreamlineVersions, defaults.Streamline);

        var rows = new List<(LinuxDlssBatchCandidate Candidate, CheckBox Check)>();
        var gamesPanel = new StackPanel { Spacing = 4 };
        foreach (var candidate in eligible)
        {
            var check = new CheckBox { IsChecked = false };
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = candidate.Game.Name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = UiLocalizationService.Present(candidate.Reason), FontSize = 10, Opacity = 0.65 });
            check.Content = text;
            rows.Add((candidate, check));
            gamesPanel.Children.Add(check);
        }

        var selectAll = new Button { Content = T("Win.SelectAll"), Padding = new Avalonia.Thickness(10, 5) };
        var clearAll = new Button { Content = T("Win.Clear"), Padding = new Avalonia.Thickness(10, 5) };
        selectAll.Click += (_, _) => { foreach (var row in rows) row.Check.IsChecked = true; };
        clearAll.Click += (_, _) => { foreach (var row in rows) row.Check.IsChecked = false; };
        var selectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        selectRow.Children.Add(selectAll);
        selectRow.Children.Add(clearAll);

        static StackPanel LabeledCombo(string label, ComboBox combo)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 11 });
            panel.Children.Add(combo);
            return panel;
        }

        var left = new StackPanel { Spacing = 8, MinWidth = 350 };
        left.Children.Add(new TextBlock { Text = TF("Win.EligibleGames", eligible.Count), FontSize = 16, FontWeight = FontWeight.SemiBold });
        left.Children.Add(selectRow);
        left.Children.Add(new ScrollViewer { Content = gamesPanel, MaxHeight = 440, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var right = new StackPanel { Spacing = 11, MinWidth = 270 };
        right.Children.Add(new TextBlock { Text = T("Win.Versions"), FontSize = 16, FontWeight = FontWeight.SemiBold });
        right.Children.Add(new TextBlock
        {
            Text = T("Win.FilledFromSettingsDoNotChange"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
            FontSize = 11
        });
        right.Children.Add(LabeledCombo(T("Win.SuperResolution"), sr));
        right.Children.Add(LabeledCombo(T("Win.RayReconstruction"), rr));
        right.Children.Add(LabeledCombo(T("Win.FrameGeneration"), fg));
        right.Children.Add(LabeledCombo(T("Win.NeuralRendering"), nr));
        right.Children.Add(LabeledCombo("Streamline", sl));

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        var deploy = new Button { Content = T("Win.Apply"), Padding = new Avalonia.Thickness(14, 7) };
        var restore = new Button { Content = T("Win.RestoreSelected"), Padding = new Avalonia.Thickness(14, 7) };
        var close = new Button { Content = T("Win.Close"), Padding = new Avalonia.Thickness(14, 7) };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 18, Margin = new Avalonia.Thickness(16) };
        body.Children.Add(left); Grid.SetColumn(left, 0);
        var divider = new Border { Width = 1, Background = new SolidColorBrush(Color.Parse("#2A3038")) };
        body.Children.Add(divider); Grid.SetColumn(divider, 1);
        body.Children.Add(right); Grid.SetColumn(right, 2);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Avalonia.Thickness(16) };
        footer.Children.Add(status); Grid.SetColumn(status, 0);
        footer.Children.Add(restore); Grid.SetColumn(restore, 1);
        footer.Children.Add(deploy); Grid.SetColumn(deploy, 2);
        footer.Children.Add(close); Grid.SetColumn(close, 3);

        // Narrow dialogs (capped to the main window) put the versions under the games instead of beside them.
        void ArrangeBody(double width)
        {
            var stacked = width < 700;
            body.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,Auto,*");
            body.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto,Auto" : "*");
            body.RowSpacing = stacked ? 14 : 0;
            left.MinWidth = stacked ? 0 : 350;
            right.MinWidth = stacked ? 0 : 270;
            Grid.SetColumn(divider, stacked ? 0 : 1); Grid.SetRow(divider, stacked ? 1 : 0);
            divider.Width = stacked ? double.NaN : 1;
            divider.Height = stacked ? 1 : double.NaN;
            Grid.SetColumn(right, stacked ? 0 : 2); Grid.SetRow(right, stacked ? 2 : 0);
        }

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var bodyScroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Children.Add(bodyScroll); Grid.SetRow(bodyScroll, 0);
        root.Children.Add(footer); Grid.SetRow(footer, 1);

        var dialog = new Window
        {
            Title = T("Win.DLSSStreamlineBatchOperation"),
            // Preferred size, capped to the main window; the minimum fits the 640x480 window floor (the body stacks
            // below 700 px and scrolls, the footer stays pinned).
            Width = 820,
            Height = 650,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        dialog.SizeChanged += (_, args) => ArrangeBody(args.NewSize.Width);

        IReadOnlyList<DetectedGame> SelectedGames() => rows
            .Where(row => row.Check.IsChecked == true)
            .Select(row => row.Candidate.Game)
            .ToArray();

        var running = false;
        async Task RunAsync(bool restoreMode)
        {
            var selected = SelectedGames();
            if (selected.Count == 0)
            {
                status.Text = T("Win.SelectAtLeastOneGame");
                return;
            }

            var chosen = new LinuxDlssVersionDefaults(
                SelectedVersion(sr), SelectedVersion(rr), SelectedVersion(fg), SelectedVersion(nr), SelectedVersion(sl));
            if (!restoreMode && !chosen.HasAny)
            {
                status.Text = T("Win.SelectAtLeastOneVersionUsing");
                return;
            }

            // While the batch runs, Close becomes Cancel: the main window's overlay is behind this dialog (Refs #77).
            deploy.IsEnabled = restore.IsEnabled = false;
            running = true;
            close.Content = T("Win.Cancel");
            try
            {
                status.Text = restoreMode ? T("Win.RestoringSelectedGames") : T("Win.ApplyingSelectedGames");
                var result = restoreMode
                    ? await vm.Dlss.RestoreDlssBatchAsync(selected)
                    : await vm.Dlss.ApplyDlssBatchAsync(selected, chosen);
                status.Text = UiLocalizationService.Present(result.Summary);
            }
            catch (OperationRefusedException refused)
            {
                status.Text = refused.Message;
            }
            catch (OperationCanceledException)
            {
                status.Text = T("Vm.OperationCancelled");
            }
            catch (Exception ex)
            {
                status.Text = TF("Win.BatchOperationFailed", UiLocalizationService.Present(ex.Message));
            }
            finally
            {
                running = false;
                close.Content = T("Win.Close");
                deploy.IsEnabled = restore.IsEnabled = close.IsEnabled = true;
            }
        }

        deploy.Click += (_, _) => RunUiSafe(() => RunAsync(restoreMode: false));
        restore.Click += (_, _) => RunUiSafe(() => RunAsync(restoreMode: true));
        close.Click += (_, _) =>
        {
            if (!running) { dialog.Close(); return; }
            close.IsEnabled = false;
            vm.Operations.Cancel();
        };
        dialog.Closing += (_, args) => args.Cancel |= running; // the window's own close button waits for the batch too
        await ShowFittedDialogAsync(dialog);
    });

    private async Task ShowSimpleMessageAsync(string title, string message)
    {
        var close = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Avalonia.Thickness(14, 7) };
        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        panel.Children.Add(close);
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var snapshot = vm.Settings.GetLinuxSettingsSnapshot();
        var settings = snapshot.Settings;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };

        // Labels and values come from the one mapping the view model uses (OptionLists, Refs #87).
        var uiLanguage = new ComboBox
        {
            ItemsSource = OptionLists.UiLanguage.Items,
            SelectedItem = OptionLists.UiLanguage.Display(settings.UiLanguage),
            Width = 240
        };
        var channel = new ComboBox
        {
            ItemsSource = OptionLists.ReShadeChannel.Items,
            SelectedItem = OptionLists.ReShadeChannel.Display(settings.ReShadeChannel),
            Width = 230
        };
        var manageIni = new CheckBox { Content = T("Win.ManageReShadeIniSearchPaths"), IsChecked = settings.ReShadeManageIni };
        var screenshotPath = new TextBox { Text = settings.ScreenshotPath, PlaceholderText = T("Win.OptionalScreenshotFolder"), MinWidth = 430 };
        var perGameScreenshots = new CheckBox { Content = T("Win.CreateScreenshotSubfolderEachGame"), IsChecked = settings.PerGameScreenshotFolders };
        var overlayHotkey = new ReShadeHotkeyEditor(settings.OverlayHotkey, "36,0,0,0");
        var screenshotHotkey = new ReShadeHotkeyEditor(settings.ScreenshotHotkey, "44,0,0,0");
        var addonWatchFolder = new TextBox { Text = settings.AddonWatchFolder, PlaceholderText = T("Win.OptionalWatchedFolderDefaultDownloads"), MinWidth = 430 };
        var cacheAllShaders = new CheckBox { Content = T("Win.KeepDownloadedShaderPacksCached"), IsChecked = settings.CacheAllShaders };
        var verboseLogging = new CheckBox { Content = T("Win.VerboseLogging"), IsChecked = settings.VerboseLogging };
        var autoUpdateComponents = new CheckBox
        {
            Content = T("Win.AutomaticallyUpdateRHIManagedComponentsBackgroun"),
            IsChecked = settings.AutoUpdateComponents
        };
        var addonWatcherEnabled = new CheckBox
        {
            Content = T("Win.WatchSelectedFolderDownloadedAddOns"),
            IsChecked = settings.AddonWatcherEnabled
        };
        var viewMode = new ComboBox
        {
            ItemsSource = OptionLists.ViewMode.Items,
            SelectedItem = OptionLists.ViewMode.Display(settings.ViewMode),
            Width = 190
        };
        var trayIconEnabled = new CheckBox
        {
            Content = T("Win.EnableSystemTrayIconStatusNotifierItemAppIndicat"),
            IsChecked = settings.TrayIconEnabled
        };
        var closeToTray = new CheckBox
        {
            Content = T("Win.HideTrayWhenClosingMainWindow"),
            IsChecked = settings.CloseToTray,
            IsEnabled = settings.TrayIconEnabled
        };
        trayIconEnabled.Click += (_, _) =>
        {
            closeToTray.IsEnabled = trayIconEnabled.IsChecked == true;
            if (trayIconEnabled.IsChecked != true) closeToTray.IsChecked = false;
        };
        var showMotdOnStartup = new CheckBox
        {
            Content = T("Win.ShowEachNewMOTDOnceAt"),
            IsChecked = settings.ShowMotdOnStartup
        };

        static ComboBox BuildDlssDefaultCombo(IEnumerable<string> versions, string current)
        {
            var items = new List<string> { OptionLists.DoNotChangeDisplay() };
            foreach (var version in versions.Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase))
                items.Add(version);
            if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, StringComparer.OrdinalIgnoreCase))
                items.Add(current); // keep the saved default visible/offline instead of silently resetting it
            var selected = string.IsNullOrWhiteSpace(current)
                ? items[0]
                : items.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
            return new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 230 };
        }

        var defaultDlss = BuildDlssDefaultCombo(vm.Dlss.DlssSrVersions, settings.DefaultDlssVersion);
        var defaultDlssd = BuildDlssDefaultCombo(vm.Dlss.DlssRrVersions, settings.DefaultDlssdVersion);
        var defaultDlssg = BuildDlssDefaultCombo(vm.Dlss.DlssFgVersions, settings.DefaultDlssgVersion);
        var defaultDlssnr = BuildDlssDefaultCombo(vm.Dlss.DlssNrVersions, settings.DefaultDlssnrVersion);
        var defaultStreamline = BuildDlssDefaultCombo(vm.Dlss.StreamlineVersions, settings.DefaultStreamlineVersion);

        var browseScreenshots = new Button { Content = T("Win.Browse"), Padding = new Avalonia.Thickness(12, 6) };
        browseScreenshots.Click += (_, _) => RunUiSafe(async () =>
        {
            var chosen = await PickFolderAsync(T("Win.SelectScreenshotFolder"));
            if (!string.IsNullOrWhiteSpace(chosen)) screenshotPath.Text = chosen;
        });
        var browseWatch = new Button { Content = T("Win.Browse"), Padding = new Avalonia.Thickness(12, 6) };
        browseWatch.Click += (_, _) => RunUiSafe(async () =>
        {
            var chosen = await PickFolderAsync(T("Win.SelectWatchedAddFolder"));
            if (!string.IsNullOrWhiteSpace(chosen)) addonWatchFolder.Text = chosen;
        });

        var screenshotRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        screenshotRow.Children.Add(screenshotPath);
        screenshotRow.Children.Add(browseScreenshots);
        Grid.SetColumn(browseScreenshots, 1);

        var watchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        watchRow.Children.Add(addonWatchFolder);
        watchRow.Children.Add(browseWatch);
        Grid.SetColumn(browseWatch, 1);

        static StackPanel Labeled(string label, Control control, string? note = null)
        {
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
            panel.Children.Add(control);
            if (!string.IsNullOrWhiteSpace(note))
                panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        var reshadeTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        reshadeTab.Children.Add(new TextBlock { Text = T("Win.ReShadeDefaults"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        reshadeTab.Children.Add(new TextBlock
        {
            Text = T("Win.NativeLinuxPersistenceRHICompatibleSettings"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        reshadeTab.Children.Add(Labeled(T("Win.DefaultReShadeChannel"), channel));
        reshadeTab.Children.Add(manageIni);
        reshadeTab.Children.Add(Labeled(T("Win.ScreenshotFolder"), screenshotRow, T("Win.EmptyKeepsSavePathAlreadyConfigured")));
        reshadeTab.Children.Add(perGameScreenshots);
        reshadeTab.Children.Add(Labeled(T("Win.OverlayHotkey"), overlayHotkey.Control, T("Win.DefaultHomeRHIStoresHotkeyFormat")));
        reshadeTab.Children.Add(Labeled(T("Win.ScreenshotHotkey"), screenshotHotkey.Control, T("Win.DefaultPrintScreenRHIStoresHotkey")));

        var managerTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        managerTab.Children.Add(new TextBlock { Text = "Shaders / Add-ons", FontSize = 20, FontWeight = FontWeight.SemiBold });
        managerTab.Children.Add(Labeled(T("Win.ShaderCache"), cacheAllShaders, T("Win.KeepsDownloadedShaderPacksRHIXDG")));
        managerTab.Children.Add(addonWatcherEnabled);
        managerTab.Children.Add(Labeled(T("Win.WatchedAddFolder"), watchRow,
            T("Win.NativeLinuxWatcherNeverDeletesSource")));
        managerTab.Children.Add(Labeled(T("Win.VerboseLogging"), verboseLogging, T("Win.PreferencePersistedComponentLogsRemainUnder")));

        var dlssTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        dlssTab.Children.Add(new TextBlock { Text = T("Win.DLSSStreamlineDefaults"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        dlssTab.Children.Add(new TextBlock
        {
            Text = T("Win.QuickApplyBatchApplyReplaceOnly"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        dlssTab.Children.Add(Labeled(T("Win.SuperResolutionNvngxDlssDll"), defaultDlss));
        dlssTab.Children.Add(Labeled(T("Win.RayReconstructionNvngxDlssdDll"), defaultDlssd));
        dlssTab.Children.Add(Labeled(T("Win.FrameGenerationNvngxDlssgDll"), defaultDlssg));
        dlssTab.Children.Add(Labeled(T("Win.NeuralRenderingNvngxDlssnrDll"), defaultDlssnr));
        dlssTab.Children.Add(Labeled("Streamline", defaultStreamline));
        var optiScalerTemplates = new OptiScalerTemplateSettingsRows(settings, Path.Combine(snapshot.ConfigRoot, "inis"), // w3/dllnames hook (#97)
            folder => TryOpenFolder(folder, out var error) ? null : ErrorLineText.FolderNotOpened(error), line => status.Text = line);
        dlssTab.Children.Add(optiScalerTemplates.Control); // w3/dllnames hook (#97)
        // w5 lane rows: Settings rows (build your row object here, add it to its tab)
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        var iniRows = new ReShadeIniSettingsRows(settings, vm.IniPresets, current => // #181: effect list, apply to all, INI templates
        {
            current.ScreenshotPath = screenshotPath.Text ?? "";
            current.PerGameScreenshotFolders = perGameScreenshots.IsChecked == true;
            current.OverlayHotkey = overlayHotkey.Value;
            current.ScreenshotHotkey = screenshotHotkey.Value;
        }, folder => TryOpenFolder(folder, out var error) ? null : ErrorLineText.FolderNotOpened(error), line => status.Text = line);
        reshadeTab.Children.Add(iniRows.Control); // #181
        // w5/shell-launch rows
        // w5/reshade-core rows

        var pathGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 8 };
        void AddPathRow(int row, string label, string value)
        {
            var a = new TextBlock { Text = label, Opacity = 0.72 };
            var b = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            pathGrid.Children.Add(a); Grid.SetRow(a, row); Grid.SetColumn(a, 0);
            pathGrid.Children.Add(b); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
        }
        AddPathRow(0, "settings.json", snapshot.SettingsPath);
        AddPathRow(1, T("Win.Configuration"), snapshot.ConfigRoot);
        AddPathRow(2, "Cache", snapshot.CacheRoot);
        AddPathRow(3, T("Win.Data"), snapshot.DataRoot);

        var openConfig = new Button { Content = T("Win.OpenConfiguration"), Padding = new Avalonia.Thickness(12, 6) };
        var openCache = new Button { Content = T("Win.OpenCache"), Padding = new Avalonia.Thickness(12, 6) };
        var openData = new Button { Content = T("Win.OpenData"), Padding = new Avalonia.Thickness(12, 6) };
        void OpenFolder(string path)
        {
            if (!TryOpenFolder(path, out var error)) status.Text = ErrorLineText.FolderNotOpened(error);
        }
        openConfig.Click += (_, _) => OpenFolder(snapshot.ConfigRoot);
        openCache.Click += (_, _) => OpenFolder(snapshot.CacheRoot);
        openData.Click += (_, _) => OpenFolder(snapshot.DataRoot);

        var purgeCache = new Button { Content = T("Win.ClearCache"), Padding = new Avalonia.Thickness(12, 6) };
        purgeCache.Click += (_, _) => RunUiSafe(async () =>
        {
            purgeCache.IsEnabled = false;
            try { status.Text = await vm.Settings.PurgeLinuxCacheAsync(); }
            finally { purgeCache.IsEnabled = true; }
        });
        var reset = new Button { Content = UiLocalizationService.Text("Ui.ResetSettings"), Padding = new Avalonia.Thickness(12, 6) };
        reset.Click += (_, _) =>
        {
            var defaults = new LinuxAppSettings();
            uiLanguage.SelectedItem = OptionLists.UiLanguage.Display(defaults.UiLanguage);
            channel.SelectedItem = OptionLists.ReShadeChannel.Display(defaults.ReShadeChannel);
            manageIni.IsChecked = defaults.ReShadeManageIni;
            screenshotPath.Text = defaults.ScreenshotPath;
            perGameScreenshots.IsChecked = defaults.PerGameScreenshotFolders;
            overlayHotkey.SetValue(defaults.OverlayHotkey);
            screenshotHotkey.SetValue(defaults.ScreenshotHotkey);
            addonWatchFolder.Text = defaults.AddonWatchFolder;
            addonWatcherEnabled.IsChecked = defaults.AddonWatcherEnabled;
            viewMode.SelectedItem = OptionLists.ViewMode.Display(defaults.ViewMode);
            trayIconEnabled.IsChecked = defaults.TrayIconEnabled;
            closeToTray.IsChecked = defaults.CloseToTray;
            closeToTray.IsEnabled = defaults.TrayIconEnabled;
            showMotdOnStartup.IsChecked = defaults.ShowMotdOnStartup;
            cacheAllShaders.IsChecked = defaults.CacheAllShaders;
            verboseLogging.IsChecked = defaults.VerboseLogging;
            autoUpdateComponents.IsChecked = defaults.AutoUpdateComponents;
            defaultDlss.SelectedItem = OptionLists.DoNotChangeDisplay();
            defaultDlssd.SelectedItem = OptionLists.DoNotChangeDisplay();
            defaultDlssg.SelectedItem = OptionLists.DoNotChangeDisplay();
            defaultDlssnr.SelectedItem = OptionLists.DoNotChangeDisplay();
            defaultStreamline.SelectedItem = OptionLists.DoNotChangeDisplay();
            optiScalerTemplates.Reset(defaults); // w3/dllnames hook (#97)
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            iniRows.Reset(defaults); // #181
            // w5/shell-launch rows
            // w5/reshade-core rows
            status.Text = vm.Settings.ResetLinuxSettings();
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        };

        var uxTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        uxTab.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ui.DesktopExperience"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        uxTab.Children.Add(new TextBlock
        {
            Text = T("Win.NativeDesktopConveniencesLiveHereRHI"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        uxTab.Children.Add(Labeled(UiLocalizationService.Text("Ui.Language"), uiLanguage, UiLocalizationService.Text("Ui.LanguageNote")));
        uxTab.Children.Add(Labeled(UiLocalizationService.Text("Ui.ViewMode"), viewMode,
            T("Win.CompactViewKeepsSelectedGameWorkflow")));
        uxTab.Children.Add(trayIconEnabled);
        uxTab.Children.Add(closeToTray);
        uxTab.Children.Add(new TextBlock
        {
            Text = T("Win.TraySupportDependsDesktopEnvironmentKDE"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.65
        });
        uxTab.Children.Add(showMotdOnStartup);

        var maintenance = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        maintenance.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ui.Maintenance"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        maintenance.Children.Add(Labeled(T("Win.BackgroundComponentUpdates"), autoUpdateComponents,
            T("Win.DisabledByDefaultWhenEnabledRHI")));
        maintenance.Children.Add(pathGrid);
        var pathButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathButtons.Children.Add(openConfig); pathButtons.Children.Add(openCache); pathButtons.Children.Add(openData);
        maintenance.Children.Add(pathButtons);
        var maintenanceButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        maintenanceButtons.Children.Add(purgeCache); maintenanceButtons.Children.Add(reset);
        maintenance.Children.Add(maintenanceButtons);
        maintenance.Children.Add(new TextBlock
        {
            Text = T("Win.ClearCacheRemovesOnlyRHILinux"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68
        });

        LinuxAppSettings ReadControls() => new()
        {
            UiLanguage = OptionLists.UiLanguage.Value(uiLanguage.SelectedItem?.ToString()),
            ReShadeChannel = OptionLists.ReShadeChannel.Value(channel.SelectedItem?.ToString()),
            ReShadeManageIni = manageIni.IsChecked == true,
            ScreenshotPath = screenshotPath.Text ?? "",
            OverlayHotkey = overlayHotkey.Value,
            ScreenshotHotkey = screenshotHotkey.Value,
            PerGameScreenshotFolders = perGameScreenshots.IsChecked == true,
            AddonWatchFolder = addonWatchFolder.Text ?? "",
            AddonWatcherEnabled = addonWatcherEnabled.IsChecked == true,
            ViewMode = OptionLists.ViewMode.Value(viewMode.SelectedItem?.ToString()),
            TrayIconEnabled = trayIconEnabled.IsChecked == true,
            CloseToTray = trayIconEnabled.IsChecked == true && closeToTray.IsChecked == true,
            ShowMotdOnStartup = showMotdOnStartup.IsChecked == true,
            CacheAllShaders = cacheAllShaders.IsChecked == true,
            VerboseLogging = verboseLogging.IsChecked == true,
            AutoUpdateComponents = autoUpdateComponents.IsChecked == true,
            UpdateCheckIntervalHours = 4,
            DefaultDlssVersion = ReadDlssDefault(defaultDlss),
            DefaultDlssdVersion = ReadDlssDefault(defaultDlssd),
            DefaultDlssgVersion = ReadDlssDefault(defaultDlssg),
            DefaultDlssnrVersion = ReadDlssDefault(defaultDlssnr),
            DefaultStreamlineVersion = ReadDlssDefault(defaultStreamline),
            OptiScalerGpuType = optiScalerTemplates.GpuType, // w3/dllnames hook (#97)
            OptiScalerDlssInputs = optiScalerTemplates.DlssInputs, // w3/dllnames hook (#97)
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            ReShadeVariableListUseTabs = iniRows.UseTabs, // #181
            // w5/shell-launch rows
            // w5/reshade-core rows
        };

        static string ReadDlssDefault(ComboBox combo)
        {
            var value = combo.SelectedItem?.ToString() ?? "";
            return IsDoNotChange(value) ? "" : value;
        }

        var save = new Button { Content = UiLocalizationService.Text("Ui.Save"), Padding = new Avalonia.Thickness(14, 7) };
        save.Click += (_, _) =>
        {
            status.Text = vm.Settings.SaveLinuxSettings(ReadControls());
            save.Content = UiLocalizationService.Text("Ui.Save");
            reset.Content = UiLocalizationService.Text("Ui.ResetSettings");
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        };
        var applyGame = new Button
        {
            Content = T("Win.ApplyReShadeDefaultsSelectedGame"),
            Padding = new Avalonia.Thickness(14, 7),
            IsEnabled = vm.HasSelectedGame
        };
        applyGame.Click += (_, _) =>
        {
            try
            {
                var current = ReadControls();
                vm.Settings.SaveLinuxSettings(current);
                ApplyViewMode();
                SyncAddonWatcherFromSettings();
                SyncTrayIcon();
                status.Text = vm.ApplyGlobalReShadeSettingsToSelectedGame(current);
            }
            catch (Exception ex) { status.Text = TF("Win.CouldNotApplyReShadeDefaults", UiLocalizationService.Present(ex.Message)); }
        };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Avalonia.Thickness(12) };
        footer.Children.Add(status); Grid.SetColumn(status, 0);
        footer.Children.Add(applyGame); Grid.SetColumn(applyGame, 1);
        footer.Children.Add(save); Grid.SetColumn(save, 2);

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "ReShade", Content = new ScrollViewer { Content = reshadeTab } },
                new TabItem { Header = "Shaders/Add-ons", Content = new ScrollViewer { Content = managerTab } },
                new TabItem { Header = "DLSS/Streamline", Content = new ScrollViewer { Content = dlssTab } },
                new TabItem { Header = UiLocalizationService.Text("Ui.UxTab"), Content = new ScrollViewer { Content = uxTab } },
                new TabItem { Header = UiLocalizationService.Text("Ui.MaintenanceTab"), Content = new ScrollViewer { Content = maintenance } },
                XboxFoldersSettingsTab.Create(vm.Settings.XboxFolders, status, PickFolderAsync), // w3/scan Settings row (#90)
                ParitySettingsTab.Create(vm.Settings.Parity), // w3/parity Settings rows (#100 #163 #99)
                // w5/update-policy rows
                // w5/catalogue-info rows
                // w5/ini-presets rows
                // w5/shell-launch rows
                // w5/reshade-core rows
            }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(tabs); Grid.SetRow(tabs, 0);
        root.Children.Add(footer); Grid.SetRow(footer, 1);

        var dialog = new Window
        {
            Title = UiLocalizationService.Text("Ui.SettingsWindowTitle"),
            Width = 760,
            Height = 680,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        await ShowFittedDialogAsync(dialog);
    });

    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    private void ShadersAddons_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null)
            return;
        if (!vm.HasConfirmedPatchTarget)
        {
            vm.StatusText = T("Win.SetTargetExecutableEditGameBefore");
            return;
        }

        vm.StatusText = T("Win.LoadingShadersAddOnsCatalog");
        ShaderAddonManagerUiSnapshot? snapshot;
        try
        {
            snapshot = await vm.GetShaderAddonManagerSnapshotAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = TF("Win.CouldNotLoadShadersAddOns", UiLocalizationService.Present(ex.Message));
            return;
        }

        if (snapshot is null) return;

        var selectedShaders = snapshot.SelectedShaderPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedAddons = snapshot.SelectedAddonIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shaderChecks = new List<(string Id, CheckBox Box)>();
        var addonChecks = new List<(string Id, CheckBox Box)>();

        var shaderRoot = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12) };
        foreach (var group in snapshot.ShaderPacks.GroupBy(x => x.Category))
        {
            shaderRoot.Children.Add(new TextBlock
            {
                Text = ShaderCategoryDisplay(group.Key).ToUpperInvariant(),
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#4DC9E6")),
                Margin = new Avalonia.Thickness(0, 8, 0, 2)
            });

            foreach (var pack in group.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var check = new CheckBox
                {
                    Content = pack.DisplayName,
                    IsChecked = selectedShaders.Contains(pack.Id),
                    Margin = new Avalonia.Thickness(0, 2)
                };
                var dependency = pack.Requires is { Count: > 0 }
                    ? TF("Win.Requires", string.Join(", ", pack.Requires))
                    : "";
                ToolTip.SetTip(check, $"{CatalogDescription(pack.Description, pack.DisplayName, shaderPack: true)}{dependency}\nID: {pack.Id}");
                shaderChecks.Add((pack.Id, check));
                var packRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                packRow.Children.Add(check);
                Grid.SetColumn(check, 0);
                var filesButton = new Button
                {
                    Content = T("Win.Files"),
                    Padding = new Avalonia.Thickness(10, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(filesButton, T("Win.SelectShadersTexturesIndividuallySelectionSaved"));
                filesButton.Click += (_, _) => RunUiSafe(async () =>
                {
                    try { await ShowShaderPackFilesDialogAsync(vm, pack); }
                    catch (Exception ex) { vm.StatusText = TF("Win.CouldNotOpenFiles", pack.DisplayName, UiLocalizationService.Present(ex.Message)); }
                });
                packRow.Children.Add(filesButton);
                Grid.SetColumn(filesButton, 1);
                shaderRoot.Children.Add(packRow);
            }
        }

        shaderRoot.Children.Insert(0, ShaderProfilePanel.Create(vm.Parity, () => shaderChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray(), ids => { foreach (var (id, box) in shaderChecks) box.IsChecked = ids.Contains(id, StringComparer.OrdinalIgnoreCase); })); // w3/parity hook (#93)

        var addonRoot = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12) };
        foreach (var addon in snapshot.Addons.OrderBy(x => x.ManagedByComponent).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var arch = $"{(addon.Supports32Bit ? "x86" : "")}{(addon.Supports32Bit && addon.Supports64Bit ? "/" : "")}{(addon.Supports64Bit ? "x64" : "")}";
            var label = addon.ManagedByComponent
                ? TF("Win.ComponentManaged", addon.DisplayName)
                : addon.IsCustom
                    ? TF("Win.Custom2", addon.DisplayName)
                    : addon.DisplayName;

            var check = new CheckBox
            {
                Content = label,
                IsChecked = selectedAddons.Contains(addon.Id),
                IsEnabled = !addon.ManagedByComponent,
                Margin = new Avalonia.Thickness(0, 2)
            };
            ToolTip.SetTip(check, TF("Win.ArchitecturesID", CatalogDescription(addon.Description, addon.DisplayName, shaderPack: false), arch, addon.Id));
            addonChecks.Add((addon.Id, check));
            addonRoot.Children.Add(check);
        }

        var status = new TextBlock
        {
            Text = TF("Win.CatalogManagedShaderPackSLocal", snapshot.ShaderPacks.Count(x => !x.IsCustom), snapshot.Addons.Count),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        };

        var apply = new Button
        {
            Content = T("Win.ApplySelectedGame"),
            Padding = new Avalonia.Thickness(14, 7),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var openCustomShaders = new Button { Content = T("Win.OpenCustomShaders"), Padding = new Avalonia.Thickness(12, 6) };
        var openCustomAddons = new Button { Content = T("Win.OpenCustomAddOns"), Padding = new Avalonia.Thickness(12, 6) };

        openCustomShaders.Click += (_, _) =>
        {
            if (!TryOpenFolder(snapshot.CustomShaderRoot, out var error))
                status.Text = ErrorLineText.CustomShadersNotOpened(error);
        };

        openCustomAddons.Click += (_, _) =>
        {
            if (!TryOpenFolder(snapshot.CustomAddonRoot, out var error))
                status.Text = ErrorLineText.CustomAddonsNotOpened(error);
        };

        apply.Click += (_, _) => RunUiSafe(async () =>
        {
            apply.IsEnabled = false;
            try
            {
                var shaders = shaderChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray();
                var addons = addonChecks
                    .Where(x => x.Box.IsEnabled && x.Box.IsChecked == true)
                    .Select(x => x.Id)
                    .ToArray();

                status.Text = TF("Win.ApplyingShaderPackSAddS", shaders.Length, addons.Length);
                var result = await vm.ApplyShaderAddonManagerAsync(shaders, addons);
                status.Text = UiLocalizationService.Present(result.Message);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        });

        // #165: the buttons share one line (the two folder buttons wrap below each other when narrow, Apply stays right)
        // and the status line has a row of its own across the full width; squeezed between the buttons it was 10 px
        // wide at the 620x480 minimum in PT-BR and grew taller than the dialog.
        var folderButtons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        folderButtons.Children.Add(openCustomShaders);
        folderButtons.Children.Add(openCustomAddons);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        bottom.Children.Add(folderButtons);
        Grid.SetColumn(folderButtons, 0);
        apply.VerticalAlignment = VerticalAlignment.Top;
        bottom.Children.Add(apply);
        Grid.SetColumn(apply, 1);
        bottom.Children.Add(status);
        Grid.SetRow(status, 1);
        Grid.SetColumnSpan(status, 2);

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = T("Win.ShaderPacks"),
                    Content = new ScrollViewer { Content = shaderRoot }
                },
                new TabItem
                {
                    Header = T("Win.ReShadeAddOns"),
                    Content = new ScrollViewer { Content = addonRoot }
                }
            }
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(8)
        };
        layout.Children.Add(tabs);
        Grid.SetRow(tabs, 0);
        layout.Children.Add(bottom);
        Grid.SetRow(bottom, 1);

        var dialog = new Window
        {
            Title = $"Shaders/Add-ons — {vm.SelectedGame.Name}",
            Width = 820,
            Height = 720,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        await ShowFittedDialogAsync(dialog);
    });

    private void BrowseD3DCompiler47_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var path = await PickDllAsync(T("Win.PickD3DCompiler47"));
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeD3DCompiler47Path = path;
    });

    private void BrowseReShadeCustomDll_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var path = await PickDllAsync(T("Win.PickReShadeCustomDll"));
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeCustomDllPath = path;
    });

    private void ClearD3DCompiler47_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ReShadeD3DCompiler47Path = string.Empty;
    }

    private void ClearReShadeCustomDll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ReShadeCustomDllPath = string.Empty;
    }

    private async Task ShowShaderPackFilesDialogAsync(MainWindowViewModel vm, ReShadeShaderPackInfo pack)
    {
        var files = await vm.GetShaderPackFilesAsync(pack.Id);
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = pack.DisplayName,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = files.Count == 0
                ? T("Win.NoUsableShaderTextureWasFound")
                : T("Win.CheckedDeployedUncheckedRemainsExcludedFuture"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var checks = new List<(ReShadeShaderPackFileInfo File, CheckBox Box)>();
        foreach (var group in files.GroupBy(x => x.IsTexture ? T("Win.Textures") : "Shaders"))
        {
            panel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#4DC9E6")),
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            });
            foreach (var file in group.OrderBy(x => x.DisplayPath, StringComparer.OrdinalIgnoreCase))
            {
                var box = new CheckBox
                {
                    Content = file.DisplayPath,
                    IsChecked = !file.IsExcluded,
                    Margin = new Avalonia.Thickness(0, 2)
                };
                checks.Add((file, box));
                panel.Children.Add(box);
            }
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        var save = new Button
        {
            Content = T("Win.SaveSelection"),
            Padding = new Avalonia.Thickness(14, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = files.Count > 0
        };
        Window? dialog = null;
        save.Click += (_, _) =>
        {
            var excluded = checks.Where(x => x.Box.IsChecked != true).Select(x => x.File.Selector).ToArray();
            if (vm.SetShaderPackExcludedFiles(pack.Id, excluded) is { } notSaved)
            {
                status.Text = notSaved; // keep the dialog open: nothing was saved
                return;
            }
            status.Text = TF("Win.SelectionSavedActiveFileS", checks.Count - excluded.Length, checks.Count);
            dialog?.Close();
        };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Avalonia.Thickness(14, 4, 14, 14) };
        footer.Children.Add(status);
        Grid.SetColumn(status, 0);
        footer.Children.Add(save);
        Grid.SetColumn(save, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = panel };
        root.Children.Add(scroll);
        Grid.SetRow(scroll, 0);
        root.Children.Add(footer);
        Grid.SetRow(footer, 1);

        dialog = new Window
        {
            Title = TF("Win.ShaderTexturesTitle", pack.DisplayName),
            Width = 720,
            Height = 680,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        await ShowFittedDialogAsync(dialog);
    }

    private void BrowseDlss5FeederConsumer_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var path = await PickDeepFriedChickenArchiveAsync(T("Win.PickDeepFriedChickenPackage"));
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.Dlss5FeederConsumerPackagePath = path;
    });

    private void ClearDlss5FeederConsumer_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Dlss5FeederConsumerPackagePath = string.Empty;
    }

    private static string BuildRenoDxCompatibilityTooltip(RenoDxCompatibilitySetting setting)
    {
        var description = setting.Key switch
        {
            "Upgrade_R10G10B10A2_TYPELESS" => T("Win.N10BitPerChannelRGBResources"),
            "Upgrade_R10G10B10A2_UNORM" => T("Win.Normalized10BitRGBCommonlyUsed"),
            "Upgrade_R11G11B10_FLOAT" => T("Win.CompactFloatingPointRGBFormatCommonly"),
            "Upgrade_R16G16B16A16_TYPELESS" => T("Win.HighPrecisionRGBAResourcesWhoseFinal"),
            "Upgrade_R8G8B8A8_TYPELESS" => T("Win.N8BitRGBAResourcesWhoseFinal"),
            "Upgrade_R8G8B8A8_UNORM" => T("Win.Normalized8BitRGBAColorWithout"),
            "Upgrade_R8G8B8A8_UNORM_SRGB" => T("Win.N8BitRGBAColorSRGBConversion"),
            "Upgrade_CopyDestinations" => T("Win.AllowsUpgradeLogicApplyResourcesUsed"),
            "Upgrade_UseSCRGB" => T("Win.ControlsRenoDXUseScRGBOutputPath"),
            "Upgrade_SwapChainCompatibility" => T("Win.ControlsSwapchainUpgradeCompatibilityMode"),
            "Set_Path" => T("Win.SelectsHDRSDRProcessingPathUsed"),
            "DumpLUTShaders" => T("Win.ControlsLUTShaderExportDiagnostics"),
            _ when setting.ManifestDriven => T("Win.CompatibilitySettingProvidedByRHI"),
            _ => T("Win.AdvancedRenoDXCompatibilitySetting")
        };

        var format = setting.Key.StartsWith("Upgrade_R", StringComparison.OrdinalIgnoreCase)
            ? setting.Key["Upgrade_".Length..]
            : null;
        var technical = format is null
            ? TF("Win.RenoDXKey", setting.Key)
            : TF("Win.DirectXFormatRenoDXKey", format, setting.Key);
        return $"{description}\n\n{technical}";
    }

    private void Dlssg2030Settings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasDlssg2030Settings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        await ShowComponentDialogAsync(new ProxyComponentDialogViewModel(vm, ProxyComponentDialogKind.Dlssg2030));
    });

    private void StandaloneDlssEnablerSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasStandaloneDlssEnablerSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        await ShowComponentDialogAsync(new ProxyComponentDialogViewModel(vm, ProxyComponentDialogKind.DlssEnabler));
    });

    // w3/journal hook (#159): RE Framework ⚙ dialog (Views/ReFrameworkSettingsDialog.cs).
    private void ReFrameworkSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(() => DataContext is MainWindowViewModel { SelectedGame: { } game } vm ? ShowFittedDialogAsync(ReFrameworkSettingsDialog.Create(vm.ReFrameworkStorage, game.Name)) : Task.CompletedTask);

    // w5/ini-presets hook (#181): the ReShade / ReLimiter / Display Commander INI ⚙ (Views/ComponentIniDialog.cs).
    private void IniSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(() => sender is Button { DataContext: ComponentRowViewModel { HasIniSettings: true } row } && DataContext is MainWindowViewModel { SelectedGame: not null } vm ? ShowFittedDialogAsync(ComponentIniDialog.Create(vm, row.Kind, row.Name, folder => TryOpenFolder(folder, out var error) ? null : ErrorLineText.FolderNotOpened(error))) : Task.CompletedTask);

    private void Rtx40MfgSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasRtx40MfgSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        await ShowComponentDialogAsync(new ProxyComponentDialogViewModel(vm, ProxyComponentDialogKind.UniversalMfg));
    });

    // w4/encore (#158)
    private void RtxEncoreSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasRtxEncoreSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        await ShowComponentDialogAsync(new ProxyComponentDialogViewModel(vm, ProxyComponentDialogKind.RtxEncore));
    });

    /// <summary>A component dialog: the window's content is its view model, laid out by its data template (App.axaml).</summary>
    private async Task ShowComponentDialogAsync(ProxyComponentDialogViewModel model)
    {
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = model.Width,
            Height = model.Height,
            MinWidth = model.MinWidth,
            MinHeight = model.MinHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = model }
        };
        dialog.Closed += (_, _) => model.Dispose();
        await ShowFittedDialogAsync(dialog);
    }

    private void RenoDxSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasRenoDxSettings) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var snapshot = await vm.GetRenoDxCompatibilitySettingsAsync();
        var panel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(18)
        };

        panel.Children.Add(new TextBlock
        {
            Text = T("Win.CompatibilitySettings"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = T("Win.ChangesWrittenImmediatelySelectedGameRenodx"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        if (!string.IsNullOrWhiteSpace(snapshot.Warning))
        {
            panel.Children.Add(new TextBlock
            {
                Text = UiLocalizationService.Present(snapshot.Warning),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });
        }

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };

        if (!snapshot.HasRenoDxSection)
        {
            message.Text = string.IsNullOrWhiteSpace(snapshot.Warning)
                ? T("Win.RunGameOnceRenoDXInstalledGenerate")
                : UiLocalizationService.Present(snapshot.Warning);
            panel.Children.Add(message);
        }
        else if (snapshot.Settings.Count == 0)
        {
            message.Text = T("Win.RenodxSectionExistsButNoCompatibility");
            panel.Children.Add(message);
        }
        else
        {
            foreach (var setting in snapshot.Settings)
            {
                var options = setting.Options.ToArray();
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12
                };
                var label = new TextBlock
                {
                    Text = UiLocalizationService.Present(setting.Label),
                    Width = 360,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                ToolTip.SetTip(label, BuildRenoDxCompatibilityTooltip(setting));

                var combo = new ComboBox
                {
                    Width = 210,
                    ItemsSource = options.Select(x => UiLocalizationService.Present(x.Name)).ToArray()
                };
                var selectedIndex = Array.FindIndex(options, x => string.Equals(x.Value, setting.CurrentValue, StringComparison.Ordinal));
                combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                var acceptedIndex = combo.SelectedIndex;
                var reverting = false;
                combo.SelectionChanged += (_, _) =>
                {
                    if (reverting || combo.SelectedIndex < 0 || combo.SelectedIndex >= options.Length) return;
                    var selected = options[combo.SelectedIndex];
                    var result = vm.SetRenoDxCompatibilitySetting(setting.Key, selected.Value);
                    message.Text = UiLocalizationService.Present(result.Message);
                    if (result.Success)
                    {
                        acceptedIndex = combo.SelectedIndex;
                        return;
                    }

                    reverting = true;
                    combo.SelectedIndex = acceptedIndex;
                    reverting = false;
                };

                rowPanel.Children.Add(label);
                rowPanel.Children.Add(combo);
                panel.Children.Add(rowPanel);
            }
            panel.Children.Add(message);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.IniPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = snapshot.IniPath,
                FontSize = 10,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new Window
        {
            Title = TF("Win.RenoDxCompatibilityTitle", vm.SelectedGame?.Name ?? T("Win.Game")),
            Width = 660,
            Height = 620,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await ShowFittedDialogAsync(dialog);
    });

    private void OptiScalerSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasOptiScalerSettings) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var snapshot = vm.GetOptiScalerAdvancedSettings();
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = T("Win.AdvancedOptiScalerSettings"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = T("Win.ChooseChannelBeforeInstallingStableDefault"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var variantRow = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
        // #174: the label names every channel (F5 DLSSNR included) so none hides inside the closed list; each entry and
        // the line under the list say in plain words what the channel is (2 s tooltips, the owner's rule).
        var variantLabel = new TextBlock
        {
            Text = T("Win.ChannelVariant"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        ToolTip.SetTip(variantLabel, T("Win.StableUsesOfficialStableReleaseNightly"));
        ToolTip.SetShowDelay(variantLabel, MiddleEllipsisText.TooltipDelay);
        var variantCombo = new ComboBox
        {
            ItemsSource = vm.Runtime.OptiScalerVariantOptions,
            SelectedItem = vm.Runtime.SelectedOptiScalerVariant,
            MinWidth = 260,
            IsEnabled = !snapshot.ManagedByRhi,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OptionItem<OptiScalerVariant>>((item, _) =>
            {
                var text = new TextBlock { Text = item?.Display };
                if (item is not null)
                {
                    ToolTip.SetTip(text, RuntimePanelViewModel.OptiScalerChannelText(item.Value));
                    ToolTip.SetShowDelay(text, MiddleEllipsisText.TooltipDelay);
                }
                return text;
            })
        };
        ToolTip.SetShowOnDisabled(variantCombo, true);
        ToolTip.SetShowDelay(variantCombo, MiddleEllipsisText.TooltipDelay);
        var channelText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        void ShowChannel()
        {
            var chosen = (variantCombo.SelectedItem as OptionItem<OptiScalerVariant>)?.Value ?? OptiScalerVariant.Stable;
            channelText.Text = RuntimePanelViewModel.OptiScalerChannelText(chosen);
            ToolTip.SetTip(variantCombo, channelText.Text);
        }
        ShowChannel();
        variantCombo.SelectionChanged += (_, _) =>
        {
            if (variantCombo.SelectedItem is OptionItem<OptiScalerVariant> value)
                vm.Runtime.SelectedOptiScalerVariant = value;
            ShowChannel();
        };
        Grid.SetColumn(variantLabel, 0);
        Grid.SetColumn(variantCombo, 1);
        variantRow.Children.Add(variantLabel);
        variantRow.Children.Add(variantCombo);
        panel.Children.Add(variantRow);
        panel.Children.Add(channelText);
        panel.Children.Add(new TextBlock
        {
            Text = snapshot.ManagedByRhi
                ? T("Win.InstalledVariantSelectorStaysLockedUntil")
                : T("Win.SelectedVariantSavedPerGameWill"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.58
        });
        panel.Children.Add(new OptiScalerNightlyBuildRow(vm.Runtime.OptiScalerNightlyBuild)); // w3/sync #156 hook

        // #172: OptiScaler's DLL name, like ReShade's (#120): Automatic (dxgi.dll, or the next free name while it is held),
        // locked while installed, names another RHI component uses left out; the 2 s tooltip says which name the install
        // takes and why.
        if (vm.OptiScalerDllName() is { } dllName)
        {
            var dllRow = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
            var dllLabel = new TextBlock { Text = T("Names.DllNameLabel"), VerticalAlignment = VerticalAlignment.Center };
            var dllCombo = new ComboBox { ItemsSource = dllName.Options, SelectedItem = dllName.Selected, MinWidth = 260, IsEnabled = dllName.CanChoose };
            ToolTip.SetTip(dllCombo, dllName.Hint);
            ToolTip.SetShowOnDisabled(dllCombo, true);
            ToolTip.SetShowDelay(dllCombo, MiddleEllipsisText.TooltipDelay);
            var acceptedDll = dllName.Selected;
            var revertingDll = false;
            dllCombo.SelectionChanged += (_, _) =>
            {
                if (revertingDll || dllCombo.SelectedItem is not DllNameOption chosen || ReferenceEquals(chosen, acceptedDll)) return;
                if (vm.SaveOptiScalerDllName(chosen.Value))
                {
                    acceptedDll = chosen;
                    if (vm.OptiScalerDllName() is { } refreshed) ToolTip.SetTip(dllCombo, refreshed.Hint);
                    return;
                }
                revertingDll = true;
                dllCombo.SelectedItem = acceptedDll;
                revertingDll = false;
            };
            Grid.SetColumn(dllLabel, 0);
            Grid.SetColumn(dllCombo, 1);
            dllRow.Children.Add(dllLabel);
            dllRow.Children.Add(dllCombo);
            panel.Children.Add(dllRow);
        }

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Margin = new Avalonia.Thickness(0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "OptiScaler.ini",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = T("Win.ControlsBelowEditOnlyOptiScalerIni"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        // The main window's overlay Cancel is behind this modal dialog: a setting change that is still downloading
        // or waiting can be stopped here; hidden otherwise, and once it starts changing files (Refs #89 unit 7).
        var cancel = new Button
        {
            Classes = { "rhi" }, Content = T("Ui.CancelOperation"), Command = vm.Operations.CancelCommand,
            HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false
        };
        var applying = false;
        void SyncCancel() => cancel.IsVisible = applying && vm.Operations.CanCancel;
        void OnOperationsChanged(object? s, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(OperationCoordinator.CanCancel)) SyncCancel();
        }
        vm.Operations.PropertyChanged += OnOperationsChanged;
        if (!string.IsNullOrWhiteSpace(snapshot.Warning))
        {
            panel.Children.Add(new TextBlock
            {
                Text = UiLocalizationService.Present(snapshot.Warning),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }

        if (!snapshot.ManagedByRhi || snapshot.Settings.Count == 0)
        {
            message.Text = string.IsNullOrWhiteSpace(snapshot.Warning)
                ? T("Win.NoRHIManagedOptiScalerSettingsAvailable")
                : UiLocalizationService.Present(snapshot.Warning);
            panel.Children.Add(message);
        }
        else
        {
            string? lastGroup = null;
            foreach (var setting in snapshot.Settings)
            {
                if (!string.Equals(lastGroup, setting.Group, StringComparison.Ordinal))
                {
                    if (lastGroup is not null)
                        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Margin = new Avalonia.Thickness(0, 4) });
                    panel.Children.Add(new TextBlock
                    {
                        Text = UiLocalizationService.Present(setting.Group),
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Avalonia.Thickness(0, 4, 0, 0)
                    });
                    lastGroup = setting.Group;
                }

                var options = setting.Options.ToArray();
                var settingRow = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
                var label = new TextBlock
                {
                    Text = UiLocalizationService.Present(setting.Label),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                if (!string.IsNullOrWhiteSpace(setting.Help))
                    ToolTip.SetTip(label, UiLocalizationService.Present(setting.Help));

                var combo = new ComboBox { ItemsSource = options.Select(option => UiLocalizationService.Present(option.Name)).ToArray(), MinWidth = 260 };
                var selectedIndex = Array.FindIndex(options,
                    option => option.Value.Equals(setting.CurrentValue, StringComparison.OrdinalIgnoreCase));
                combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                var acceptedIndex = combo.SelectedIndex;
                var reverting = false;
                combo.SelectionChanged += (_, _) => RunUiSafe(async () =>
                {
                    if (reverting || combo.SelectedIndex < 0 || combo.SelectedIndex >= options.Length) return;
                    var requestedIndex = combo.SelectedIndex;
                    var selected = options[requestedIndex];
                    combo.IsEnabled = false;
                    ComponentOperationResult result;
                    string shown;
                    applying = true;
                    SyncCancel();
                    try
                    {
                        result = await vm.SetOptiScalerAdvancedSettingAsync(setting.Id, selected.Value);
                        shown = UiLocalizationService.Present(result.Message);
                    }
                    catch (Exception ex)
                    {
                        // Report returns an already-localised sentence; presenting it again could swap it for the
                        // generic text because it embeds the (English) .NET message (review nit 6).
                        result = ComponentOperationResult.Fail(UiExceptionGuard.Report(ex, "optiscaler-advanced-setting"));
                        shown = result.Message;
                    }
                    finally
                    {
                        combo.IsEnabled = true;
                        applying = false;
                        SyncCancel();
                    }
                    message.Text = shown;
                    if (result.Success)
                    {
                        acceptedIndex = requestedIndex;
                        return;
                    }
                    reverting = true;
                    combo.SelectedIndex = acceptedIndex;
                    reverting = false;
                });

                Grid.SetColumn(label, 0);
                Grid.SetColumn(combo, 1);
                settingRow.Children.Add(label);
                settingRow.Children.Add(combo);
                panel.Children.Add(settingRow);
            }
            panel.Children.Add(cancel);
            panel.Children.Add(message);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.IniPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = snapshot.IniPath,
                FontSize = 10,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new Window
        {
            Title = TF("Win.OptiScalerAdvancedTitle", vm.SelectedGame?.Name ?? T("Win.Game")),
            Width = 720,
            Height = 760,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        dialog.Closed += (_, _) => vm.Operations.PropertyChanged -= OnOperationsChanged;
        await ShowFittedDialogAsync(dialog);
    });

    private void ToggleSection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sectionKey }) return;
        var body = FindSectionBody(sectionKey);
        var toggle = FindSectionToggle(sectionKey);
        if (body is null || toggle is null) return;

        var collapse = body.IsVisible;
        body.IsVisible = !collapse;
        toggle.Content = collapse ? "▶" : "▼";

        _detailLayout.CollapsedSections.RemoveAll(x => x.Equals(sectionKey, StringComparison.OrdinalIgnoreCase));
        if (collapse)
            _detailLayout.CollapsedSections.Add(sectionKey);
        SaveDetailSectionLayout();
        UpdateSectionSummaries();
    }

    private void SectionDragHandle_PointerPressed(object? sender, PointerPressedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Control { Tag: string sectionKey }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(sectionKey));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    });

    private void Section_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control { Tag: string targetKey } ||
            !DetailSectionLayoutStore.DefaultSectionOrder.Contains(targetKey, StringComparer.OrdinalIgnoreCase) ||
            !e.DataTransfer.Formats.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
    }

    private void Section_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { Tag: string targetKey }) return;
        var sourceKey = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Equals(targetKey, StringComparison.OrdinalIgnoreCase))
            return;

        var order = _detailLayout.SectionOrder;
        var sourceIndex = order.FindIndex(x => x.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        var targetIndex = order.FindIndex(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0) return;

        var canonicalSource = order[sourceIndex];
        order.RemoveAt(sourceIndex);
        targetIndex = order.FindIndex(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        targetIndex = Math.Clamp(targetIndex, 0, order.Count);
        order.Insert(targetIndex, canonicalSource);

        _detailLayout = DetailSectionLayoutStore.Normalize(_detailLayout);
        ApplyDetailSectionLayout();
        SaveDetailSectionLayout();
        e.DragEffects = DragDropEffects.Move;
    }

    private void ApplyDetailSectionLayout()
    {
        _detailLayout = DetailSectionLayoutStore.Normalize(_detailLayout);
        var panel = this.FindControl<StackPanel>("DetailSectionsPanel");
        if (panel is null) return;

        var orderedControls = _detailLayout.SectionOrder
            .Select(FindSectionContainer)
            .Where(x => x is not null)
            .Cast<Control>()
            .ToArray();

        panel.Children.Clear();
        foreach (var section in orderedControls)
            panel.Children.Add(section);

        var collapsed = _detailLayout.CollapsedSections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DetailSectionLayoutStore.DefaultSectionOrder)
        {
            var body = FindSectionBody(key);
            var toggle = FindSectionToggle(key);
            if (body is null || toggle is null) continue;
            var isCollapsed = collapsed.Contains(key);
            body.IsVisible = !isCollapsed;
            toggle.Content = isCollapsed ? "▶" : "▼";
        }
        UpdateSectionSummaries();
    }

    private Control? FindSectionContainer(string key) => key switch
    {
        "Runtime" => this.FindControl<Border>("RuntimeSection"),
        "DlssStreamline" => this.FindControl<Border>("DlssStreamlineSection"),
        "ReShadeProfile" => this.FindControl<Border>("ReShadeProfileSection"),
        "ManagedComponents" => this.FindControl<Border>("ManagedComponentsSection"),
        "Extras" => this.FindControl<Border>("ExtrasSection"),
        _ => null
    };

    private Control? FindSectionBody(string key) => key switch
    {
        "Runtime" => this.FindControl<LabelValuePanel>("RuntimeSectionBody"),
        "DlssStreamline" => this.FindControl<StackPanel>("DlssStreamlineSectionBody"),
        "ReShadeProfile" => this.FindControl<StackPanel>("ReShadeProfileSectionBody"),
        "ManagedComponents" => this.FindControl<ItemsControl>("ManagedComponentsSectionBody"),
        "Extras" => this.FindControl<StackPanel>("ExtrasSectionBody"),
        _ => null
    };

    private Button? FindSectionToggle(string key) => key switch
    {
        "Runtime" => this.FindControl<Button>("RuntimeSectionToggle"),
        "DlssStreamline" => this.FindControl<Button>("DlssStreamlineSectionToggle"),
        "ReShadeProfile" => this.FindControl<Button>("ReShadeProfileSectionToggle"),
        "ManagedComponents" => this.FindControl<Button>("ManagedComponentsSectionToggle"),
        "Extras" => this.FindControl<Button>("ExtrasSectionToggle"),
        _ => null
    };

    private void SaveDetailSectionLayout()
    {
        try
        {
            _detailLayoutStore.Save(_detailLayout);
        }
        catch (Exception ex)
        {
            UiExceptionGuard.Log.TryWrite("save section layout", ex);
            if (DataContext is MainWindowViewModel vm)
                vm.StatusText = ex is JsonStoreUnavailableException
                    ? MainWindowViewModel.StoreSaveFailedText(ex)
                    : TF("Win.CouldNotSaveSectionLayout", UiLocalizationService.Present(ex.Message));
        }
    }

    private async Task<string?> PickDllAsync(string title)
    {
        if (!StorageProvider.CanOpen)
            return null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [WindowsDllFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickDeepFriedChickenArchiveAsync(string title)
    {
        if (!StorageProvider.CanOpen)
            return null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [DeepFriedChickenArchiveFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
