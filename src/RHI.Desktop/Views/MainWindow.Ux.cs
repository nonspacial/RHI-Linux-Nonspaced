using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RHI.Core;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using RHI.Platform.Linux.Ux;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    private static readonly HttpClient UxHttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly LinuxMotdService _motdService = new(UxHttpClient);
    private readonly LinuxAddonFileWatcher _addonWatcher = new();
    private TrayIcon? _trayIcon;
    private bool _forceClose;
    // Tray round trip (rule: the app never changes the user's window): the state and, for a normal window, the
    // position it had when it went to the tray, restored exactly on the way back.
    private bool _hiddenToTray;
    private WindowState _trayRestoreState = WindowState.Normal;
    private PixelPoint? _trayRestorePosition;
    private WindowState _lastUnminimisedState = WindowState.Normal;
    // After a restore from the tray: the state the window must keep while the window manager maps it (TrayMapGuard).
    private WindowState? _trayMapGuardState;
    private DispatcherTimer? _trayMapGuardTimer;
    /// <summary>How long after a restore from the tray a window manager change of the state is corrected.</summary>
    internal static readonly TimeSpan TrayMapGuard = TimeSpan.FromSeconds(1);
    private bool _startupMotdChecked;

    private void InitializeLinuxUx()
    {
        // The state to come back to from a minimised window (and from the tray when it was minimised first). Changes
        // while hidden in the tray are the window manager's, not the user's (e.g. dropping maximised on withdraw).
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && !_hiddenToTray && e.NewValue is WindowState state && state != WindowState.Minimized)
                _lastUnminimisedState = state;
            if (e.Property == WindowStateProperty && _trayMapGuardState is { } kept && e.NewValue is WindowState now
                && now != kept && now != WindowState.Minimized)
                Dispatcher.UIThread.Post(CorrectTrayMapState);
        };
        Activated += (_, _) => CorrectTrayMapState();

        _addonWatcher.CandidateDetected += candidate =>
            Dispatcher.UIThread.Post(() => RunUiSafe(() => HandleWatchedCandidateAsync(candidate)));

        DataContextChanged += (_, _) => AttachDetailScrollReset(DataContext as MainWindowViewModel);

        DataContextChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel confirmVm)
            {
                confirmVm.ConfirmAsync = (title, message, accept) => ShowConfirmationAsync(title, message, accept, height: 520);
                confirmVm.ConfirmControlUeAsync = ShowControlUeConfirmationAsync; // w3/dllnames hook (#136)
                confirmVm.ConfirmInstallWarningAsync = (title, sections, accept) => ShowConfirmationAsync(title, "", accept, height: double.NaN, sections: sections);
                confirmVm.ShowNotesAsync = ShowGameNotesAsync;
                confirmVm.Support.ShowReportDialogAsync = ShowGameReportDialogAsync; // #192 Copy game report
                if (confirmVm.UpdatePolicy is { } updatePolicy) updatePolicy.ShowInclusionAsync = ShowUpdateInclusionAsync; // w5/update-policy hook (#178)
            }
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        });

        Opened += (_, _) => RunUiSafe(async () =>
        {
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
            await ShowStartupMotdIfNeededAsync();
        });

        Closing += (_, e) =>
        {
            if (_forceClose) return;
            if (DataContext is MainWindowViewModel vm && vm.TrayIconEnabled && vm.CloseToTray && _trayIcon is not null)
            {
                e.Cancel = true;
                HideToTray();
            }
        };

        Closed += (_, _) =>
        {
            _addonWatcher.Dispose();
            RemoveTrayIcon();
        };
    }

    private MainWindowViewModel? _scrollResetVm;

    /// <summary>A different game selected: the detail pane goes back to its top (Refs #152).</summary>
    private void AttachDetailScrollReset(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _scrollResetVm)) return;
        if (_scrollResetVm is not null) _scrollResetVm.SelectedGameSwitched -= ScrollDetailToTop;
        _scrollResetVm = vm;
        if (vm is not null) vm.SelectedGameSwitched += ScrollDetailToTop;
    }

    private void ScrollDetailToTop()
    {
        if (this.FindControl<ScrollViewer>("DetailScroller") is { } scroller && scroller.Offset.Y != 0)
            scroller.Offset = new Vector(scroller.Offset.X, 0);
    }

    private void GameLibrary_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null) return;

        // SelectedGame can be restored programmatically after a refresh/rescan.
        // Defer until Avalonia has completed the new layout, then keep the
        // selected library entry inside the ListBox viewport instead of letting
        // a realized off-screen container paint below the sidebar/status bar.
        Dispatcher.UIThread.Post(() =>
        {
            if (list.SelectedItem is { } selected)
                list.ScrollIntoView(selected);
        });
    }

    private void ViewMode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.ToggleViewModeCommand.Execute(null);
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var compact = vm.IsCompactView;
        var filters = this.FindControl<Border>("LibraryFilterBar");
        var detail = this.FindControl<Grid>("DetailRoot");
        if (filters is not null)
            filters.IsVisible = !compact;
        if (detail is not null)
            detail.Margin = new Thickness(compact ? 12 : 24);

        // 640 × 480 minimum in both views. Switching view changes the layout only: the app never resizes the
        // user's window (no enlarge for Detailed, no shrink or cap for Compact).
        MinWidth = 640;
        MinHeight = 480;
        _libraryLayoutKey = null;
        ApplyLibraryLayout();
    }

    private async Task ShowAddGameDialogForExecutableAsync(string executable)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
        {
            await ShowSimpleMessageAsync(T("Win.AddGame"), T("Win.ExecutableNoLongerExists"));
            return;
        }

        // The layout is the view model's data template (Views/Dialogs/AddGameDialogView.axaml; epic #89).
        var model = new AddGameDialogViewModel(vm.ManualGames, executable, DialogPickers());
        var dialog = new Window
        {
            Title = model.WindowTitle,
            // Preferred size; ShowFittedDialogAsync caps it to the main window. The minimum stays below the 640x480
            // window floor (as for Edit game); the form scrolls.
            Width = 620,
            Height = 580,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = model }
        };
        model.CloseRequested += dialog.Close;
        await ShowFittedDialogAsync(dialog);
    }

    /// <summary>
    /// The pickers the Add / Edit game dialogs ask for: a folder (Add game's prefix asks the portal as it always did;
    /// Edit game's folders and executable only when the session can open files).
    /// </summary>
    private DialogPickers DialogPickers(bool onlyWhenAvailable = false) => new(
        async title => onlyWhenAvailable && !StorageProvider.CanOpen ? null : await PickFolderAsync(title),
        async title =>
        {
            if (!StorageProvider.CanOpen) return null;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [WindowsExecutableFileType]
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        });

    private void EditGame_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        // The layout is the view model's data template (Views/Dialogs/EditGameDialogView.axaml; epic #89).
        var model = new EditGameDialogViewModel(vm.ManualGames, vm.SelectedGame, DialogPickers(onlyWhenAvailable: true));
        var dialog = new Window
        {
            Title = model.WindowTitle,
            // Preferred size; ShowFittedDialogAsync caps it to the main window. The minimum stays below the 640x480
            // window floor so the dialog can always be made smaller than its owner; the form scrolls vertically. 860 high
            // so the whole form, footer included, shows at the preferred size with the launch command field (PT-BR wraps more);
            // 1120 since the name and bitness fields (Refs #190; PT-BR's form is 1074 high).
            Width = 790,
            Height = 1120,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = model,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
        model.CloseRequested += dialog.Close;
        await ShowFittedDialogAsync(dialog);
    });

    private void GameFiles_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files?.Any(file => LinuxDroppedFileClassifier.Classify(file.Path.LocalPath) != LinuxDroppedFileKind.Unsupported) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void GameFiles_Drop(object? sender, DragEventArgs e) => RunUiSafe(async () =>
    {
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            switch (LinuxDroppedFileClassifier.Classify(path))
            {
                case LinuxDroppedFileKind.WindowsGameExecutable:
                    await ShowAddGameDialogForExecutableAsync(path);
                    break;
                case LinuxDroppedFileKind.LumaAddon:
                    await PromptInstallLumaAddonAsync(path, T("Win.InstallLumaAdd"));
                    break;
                case LinuxDroppedFileKind.ReShadeAddon when DroppedRenoDxPresenter.Handles(path, watched: false): // w7/smalls (#195)
                    await PromptInstallDroppedRenoDxAsync(path, watchFolder: null);
                    break;
                case LinuxDroppedFileKind.ReShadeAddon:
                    await PromptImportCustomAddonAsync(path, T("Win.AddDroppedWindow"));
                    break;
                case LinuxDroppedFileKind.LumaArchive:
                    await PromptInstallLumaAddonAsync(path, T("Win.InstallLumaPackage"));
                    break;
                case LinuxDroppedFileKind.ModArchive:
                    if (DataContext is MainWindowViewModel vmArchive)
                        vmArchive.StatusText = TF("Win.ArchiveDetectedRHILinuxDoesNot", Path.GetFileName(path));
                    break;
                case LinuxDroppedFileKind.ReShadePreset: // w5/ini-presets hook (#180): Views/MainWindow.Presets.cs
                    await OfferDroppedPresetAsync(path);
                    break;
            }
        }
    });

    private void SyncAddonWatcherFromSettings()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.AddonWatcherEnabled)
        {
            _addonWatcher.Stop();
            return;
        }

        if (!_addonWatcher.Start(vm.AddonWatchFolder))
            vm.StatusText = TF("Win.AddWatcherCouldNotOpen", _addonWatcher.CurrentPath ?? vm.AddonWatchFolder);
    }

    private async Task HandleWatchedCandidateAsync(LinuxWatchedFileCandidate candidate)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.AddonWatcherEnabled || !_addonWatcher.IsRunning) return;
        if (candidate.Kind == LinuxWatchedFileKind.Addon)
        {
            if (LinuxDroppedFileClassifier.IsLikelyLumaAddon(candidate.Path))
                await PromptInstallLumaAddonAsync(candidate.Path, T("Win.LumaAddDetectedByWatcher"));
            else if (DroppedRenoDxPresenter.Handles(candidate.Path, watched: true)) // w7/smalls (#195)
                await PromptInstallDroppedRenoDxAsync(candidate.Path, _addonWatcher.CurrentPath);
            else
                await PromptImportCustomAddonAsync(candidate.Path, T("Win.AddDetectedByWatcher"));
            return;
        }

        if (candidate.Kind == LinuxWatchedFileKind.Archive && LinuxDroppedFileClassifier.IsLikelyLumaPackage(candidate.Path))
        {
            await PromptInstallLumaAddonAsync(candidate.Path, T("Win.LumaPackageDetectedByWatcher"));
            return;
        }

        vm.StatusText = TF("Win.WatcherDetectedGenericArchivesNeverInstalled", Path.GetFileName(candidate.Path));
    }

    private async Task PromptInstallLumaAddonAsync(string path, string title)
    {
        if (DataContext is not MainWindowViewModel vm || !File.Exists(path)) return;
        if (vm.SelectedGame is null)
        {
            vm.StatusText = T("Win.SelectGameWillReceiveLumaAdd");
            return;
        }

        var install = await ShowConfirmationAsync(
            title,
            TF("Win.InstallLumaPackageRHIWillUse", Path.GetFileName(path), vm.SelectedGame.Name),
            T("Win.InstallLuma"));
        if (!install) return;
        await vm.InstallLocalLumaPackageAsync(path);
    }

    /// <summary>
    /// #195: a dropped add-on or a watched RenoDX add-on: the game it goes into is confirmed (or chosen) first, then it is
    /// installed (a RenoDX add-on as that game's RenoDX mod). <paramref name="watchFolder"/>: the watch folder a watched
    /// file came from (removed after a successful install, as upstream).
    /// </summary>
    private async Task PromptInstallDroppedRenoDxAsync(string path, string? watchFolder)
    {
        if (DataContext is not MainWindowViewModel vm || !File.Exists(path)) return;
        var presenter = vm.DroppedRenoDx;
        if (presenter.OfferFor(vm, path, watchFolder) is not { } offer)
        {
            vm.StatusText = TF("Sm.DropNoGame", Path.GetFileName(path));
            return;
        }
        if (await ShowDroppedRenoDxDialogAsync(offer, presenter) is not { } game) return;
        await presenter.InstallAsync(vm, offer, game);
    }

    /// <summary>#195: the "Install (RenoDX) add-on" dialog: file, the game (matched or chosen), its folder, what it replaces.</summary>
    private async Task<RHI.Core.Models.DetectedGame?> ShowDroppedRenoDxDialogAsync(DroppedRenoDxOffer offer, DroppedRenoDxPresenter presenter)
    {
        var games = new ComboBox { ItemsSource = offer.GameNames, SelectedIndex = offer.MatchedIndex, HorizontalAlignment = HorizontalAlignment.Stretch };
        var folder = new MiddleEllipsisText { WrapNonPath = false, MiddleNonPath = true, Classes = { "muted" } }; // one line, full path in the 2 s tooltip
        var replaces = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var accept = new Button { Content = T("Vm.Install"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        void Follow()
        {
            var chosen = games.SelectedIndex >= 0 && games.SelectedIndex < offer.Games.Count ? offer.Games[games.SelectedIndex] : null;
            folder.FullText = chosen is null ? "" : TF("Sm.DropFolder", DroppedRenoDxPresenter.FolderFor(chosen));
            folder.IsVisible = chosen is not null;
            replaces.Text = chosen is null ? null : presenter.ReplacesFor(offer, chosen);
            replaces.IsVisible = !string.IsNullOrEmpty(replaces.Text);
            accept.IsEnabled = chosen is not null;
            ToolTip.SetTip(accept, chosen is null ? offer.ChooseTip : null); // the blocker is fixed in the list above
        }
        // A disabled button shows its tooltip only with ShowOnDisabled; the owner's ~2 s delay as elsewhere.
        ToolTip.SetShowOnDisabled(accept, true);
        ToolTip.SetShowDelay(accept, MiddleEllipsisText.TooltipDelay);
        games.SelectionChanged += (_, _) => Follow();
        Follow();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = offer.Intro, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(games);
        panel.Children.Add(folder);
        panel.Children.Add(replaces);
        panel.Children.Add(new TextBlock { Text = offer.Note, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = offer.Title,
            Width = 600,
            MinWidth = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        cancel.Click += (_, _) => dialog.Close(null);
        accept.Click += (_, _) => dialog.Close(games.SelectedIndex >= 0 && games.SelectedIndex < offer.Games.Count ? offer.Games[games.SelectedIndex] : null);
        return await ShowFittedDialogAsync<RHI.Core.Models.DetectedGame?>(dialog);
    }

    private async Task PromptImportCustomAddonAsync(string path, string title)
    {
        if (DataContext is not MainWindowViewModel vm || !File.Exists(path)) return;
        var import = await ShowConfirmationAsync(
            title,
            TF("Win.ImportStandaloneReShadeAddIntoRHI", Path.GetFileName(path)),
            T("Win.Import"));
        if (!import) return;
        vm.ImportCustomAddonFile(path);
    }

    /// <param name="height">Fixed height, or NaN to fit the content (manifest texts vary in length).</param>
    /// <param name="sections">Manifest install warnings shown after the message, each with an optional heading and its http(s)
    /// links as buttons (opened through the desktop handler only).</param>
    private async Task<bool> ShowConfirmationAsync(string title, string message, string acceptText, double height = 280, IReadOnlyList<InstallWarningSection>? sections = null)
    {
        var accept = new Button { Content = acceptText, Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        if (message.Length > 0)
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        foreach (var section in sections ?? [])
        {
            // Same rhythm as a plain confirmation inside a section; a heading sits closer to its text, and sections are set
            // further apart than the lines within them.
            var block = new StackPanel { Spacing = 14, Margin = new Thickness(0, panel.Children.Count > 0 ? 8 : 0, 0, 0) };
            if (!string.IsNullOrWhiteSpace(section.Heading))
                block.Children.Add(new TextBlock { Text = section.Heading, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 0, 0, -8) });
            if (section.Warning.Text.Length > 0)
                block.Children.Add(new TextBlock { Text = section.Warning.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            foreach (var link in section.Warning.Links)
            {
                var url = link.AbsoluteUri;
                // One line, trimmed to the dialog width; the full address is in the 2 s tooltip (long Discord thread links).
                var button = new Button
                {
                    Content = new MiddleEllipsisText { FullText = url, WrapNonPath = false },
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxWidth = 560,
                    Padding = new Thickness(10, 5)
                };
                button.Click += (_, _) => OpenProjectLink(url);
                block.Children.Add(button);
            }
            panel.Children.Add(block);
        }
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Width = 600,
            Height = height,
            SizeToContent = double.IsNaN(height) ? SizeToContent.Height : SizeToContent.Manual,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        return await ShowFittedDialogAsync<bool>(dialog);
    }

    // #192: Quick start opens Help (upstream's FAQ / Quick Start page): getting started, questions, credits, What's new.
    private void QuickStart_Click(object? sender, RoutedEventArgs e) => RunUiSafe(ShowHelpAsync);

    private void Links_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var links = new (string Label, string Url)[]
        {
            ("RHI-Linux", RhiProductInfo.RepositoryUrl),
            (T("Win.LinkOriginalRhi"), "https://github.com/RankFTW/RHI"),
            ("RenoDX", "https://github.com/clshortfuse/renodx"),
            ("ReShade", "https://reshade.me"),
            ("OptiScaler", "https://github.com/optiscaler/OptiScaler")
        };
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Win.ProjectLinks"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = T("Win.LinksOpenedByDefaultLinuxDesktop"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        foreach (var link in links)
        {
            var button = new Button { Content = link.Label, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 7) };
            button.Click += (_, _) => OpenProjectLink(link.Url);
            panel.Children.Add(button);
        }
        var close = new Button { Content = T("Win.Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7), Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(close);
        var dialog = new Window { Title = "RHI-Linux — Links", Width = 460, Height = 440, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
    });

    private void OpenProjectLink(string url)
    {
        if (LinuxDesktopOpener.TryOpenUri(url, out var error)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.StatusText = ErrorLineText.LinkNotOpened(url, error);
    }

    private void Motd_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var message = await _motdService.FetchAsync(includeSeen: true);
        if (message is null)
        {
            await ShowSimpleMessageAsync("RHI-Linux — MOTD", T("Win.NoMessageDayAvailableRightNow"));
            return;
        }
        await ShowMotdAsync(message);
    });

    private async Task ShowStartupMotdIfNeededAsync()
    {
        if (_startupMotdChecked || DataContext is not MainWindowViewModel vm || !vm.ShowMotdOnStartup) return;
        _startupMotdChecked = true;
        var message = await _motdService.FetchAsync();
        if (message is not null)
            await ShowMotdAsync(message);
    }

    private async Task ShowMotdAsync(LinuxMotdMessage message)
    {
        var close = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7) };
        var text = new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(18) };
        panel.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        panel.Children.Add(close); Grid.SetRow(close, 1);
        var dialog = new Window
        {
            Title = T("Win.RHILinuxMessageDay"),
            Width = 740,
            Height = 520,
            MinWidth = 520,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
        try { _motdService.MarkSeen(message); }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StatusText = TF("Win.MOTDWasShownButSeenState", UiLocalizationService.Present(ex.Message));
        }
    }

    private void SyncTrayIcon()
    {
        if (DataContext is not MainWindowViewModel vm || Application.Current is not { } app) return;
        if (!vm.TrayIconEnabled)
        {
            RemoveTrayIcon();
            return;
        }
        if (_trayIcon is not null)
        {
            // A language change while the icon exists relabels the menu in place.
            if (_trayIcon.Menu?.Items is [NativeMenuItem show, _, NativeMenuItem quit])
            {
                show.Header = T("Win.TrayShow");
                quit.Header = T("Win.TrayQuit");
            }
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://RHI/Assets/rhi-tray.png"));
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem(T("Win.TrayShow")) { Command = new RelayCommand(ShowMainWindowFromTray) });
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem(T("Win.TrayQuit")) { Command = new RelayCommand(ExitFromTray) });
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "RHI-Linux",
                Menu = menu,
                Command = new RelayCommand(ShowMainWindowFromTray),
                IsVisible = true
            };
            TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            RemoveTrayIcon();
            vm.StatusText = TF("Win.TrayIconUnavailableSession", UiLocalizationService.Present(ex.Message));
        }
    }

    private void RemoveTrayIcon()
    {
        if (Application.Current is { } app)
            TrayIcon.SetIcons(app, null);
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    // Remembers how the window was (maximised, full screen, or normal at its position; a minimised window counts as
    // the state it was minimised from) before hiding it.
    private void HideToTray()
    {
        _trayRestoreState = WindowState == WindowState.Minimized ? _lastUnminimisedState : WindowState;
        _trayRestorePosition = _trayRestoreState == WindowState.Normal ? Position : null;
        EndTrayMapGuard();
        _hiddenToTray = true;
        Hide();
    }

    private void ShowMainWindowFromTray() => Dispatcher.UIThread.Post(RestoreFromTray);

    // Brings the window back exactly as it went to the tray; nothing is written that already matches. The state is
    // applied before Show (an unmapped X11 window takes _NET_WM_STATE as its initial state) and checked again after
    // it (a window manager may drop the state while the window is withdrawn). On X11 the WindowState getter follows
    // PropertyNotify, so the check right after Show still sees the pre-map value: a window manager that drops
    // maximised/full screen on map is only seen later. For TrayMapGuard after Show, a state change away from the
    // remembered one (the window manager's) or the first activation re-applies it, at most once; the guard then
    // ends, so later changes by the user stay (a minimise is never corrected). Width/Height are never written. A
    // visible but minimised window returns to the state it was minimised from; a visible window is only activated.
    private void RestoreFromTray()
    {
        var fromTray = _hiddenToTray;
        _hiddenToTray = false;
        ShowInTaskbar = true;
        if (fromTray) ApplyTrayRestoreState();
        Show();
        if (fromTray)
        {
            ApplyTrayRestoreState();
            StartTrayMapGuard();
        }
        else if (WindowState == WindowState.Minimized) WindowState = _lastUnminimisedState;
        Activate();
    }

    private void StartTrayMapGuard()
    {
        _trayMapGuardState = _trayRestoreState;
        _trayMapGuardTimer?.Stop();
        _trayMapGuardTimer = new DispatcherTimer { Interval = TrayMapGuard };
        _trayMapGuardTimer.Tick += (_, _) => EndTrayMapGuard();
        _trayMapGuardTimer.Start();
    }

    private void EndTrayMapGuard()
    {
        _trayMapGuardState = null;
        _trayMapGuardTimer?.Stop();
        _trayMapGuardTimer = null;
    }

    // Once the window is mapped (activated, or the window manager reported a different state): put the remembered
    // state back if it was dropped. Corrects at most once per restore.
    private void CorrectTrayMapState()
    {
        if (_trayMapGuardState is not { } kept || !IsVisible) return;
        if (WindowState == kept || WindowState == WindowState.Minimized) return;
        EndTrayMapGuard();
        WindowState = kept;
    }

    private void ApplyTrayRestoreState()
    {
        if (WindowState != _trayRestoreState) WindowState = _trayRestoreState;
        if (_trayRestoreState == WindowState.Normal && _trayRestorePosition is { } position && Position != position)
            Position = position;
    }

    private void ExitFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _forceClose = true;
            Close();
        });
    }
}
