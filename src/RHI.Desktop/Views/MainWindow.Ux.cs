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

        var installPath = ManualGamePathInference.InferInstallRoot(executable);
        var inferredName = ManualGamePathInference.InferGameName(executable, installPath);
        var nameBox = new TextBox { Text = inferredName, PlaceholderText = T("Win.GameName") };
        var prefixBox = new TextBox { PlaceholderText = T("Win.OptionalWineProtonPrefixEG") };
        var launch = LaunchCommandField(null, "Win.LeaveCommandEmptyIfRHIShould");
        var browsePrefix = new Button { Content = T("Win.BrowsePrefix"), Padding = new Thickness(10, 5) };
        browsePrefix.Click += (_, _) => RunUiSafe(async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("Win.SelectWineProtonPrefix"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                prefixBox.Text = folders[0].TryGetLocalPath() ?? prefixBox.Text;
        });

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var add = new Button { Content = T("Win.AddGame"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        form.Children.Add(new TextBlock { Text = T("Win.ManualGame"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = T("Win.RHIInferredGameRootFromExecutable"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        form.Children.Add(new TextBlock { Text = T("Win.Name"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = T("Win.Executable"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = executable, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        form.Children.Add(new TextBlock { Text = T("Win.InferredGameRoot"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = installPath, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        form.Children.Add(new TextBlock { Text = T("Win.WineProtonPrefixOptional"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var prefixRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        prefixRow.Children.Add(prefixBox); Grid.SetColumn(prefixBox, 0);
        prefixRow.Children.Add(browsePrefix); Grid.SetColumn(browsePrefix, 1);
        form.Children.Add(prefixRow);
        launch.AddTo(form);
        form.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        form.Children.Add(buttons);

        var dialog = new Window
        {
            Title = T("Win.AddGameRHILinux"),
            // Preferred size; ShowFittedDialogAsync caps it to the main window. The minimum stays below the 640x480
            // window floor (as for Edit game); the form scrolls.
            Width = 620,
            Height = 580,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = form }
        };
        cancel.Click += (_, _) => dialog.Close();
        add.Click += (_, _) => RunUiSafe(async () =>
        {
            add.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var prefix = string.IsNullOrWhiteSpace(prefixBox.Text) ? null : prefixBox.Text.Trim();
                var entry = new LinuxManualGameEntry
                {
                    Name = nameBox.Text?.Trim() ?? "",
                    InstallPath = installPath,
                    ExecutablePath = executable,
                    PrefixPath = prefix,
                    LaunchCommand = LinuxGameEditOverride.NormalizeLaunchCommand(launch.Box.Text),
                    Runner = prefix is null ? "Manual Windows" : "Manual Wine"
                };
                // Closes as soon as the entry is saved: a refresh that waits for a running operation
                // reports to the main status line, not to a disabled dialog with no message.
                var (added, result) = await vm.ManualGames.TryAddManualGameAsync(entry, saved: dialog.Close);
                if (!added) status.Text = result;
            }
            catch (Exception ex)
            {
                status.Text = TF("Win.CouldNotAddGame", UiLocalizationService.Present(ex.Message));
            }
            finally
            {
                add.IsEnabled = cancel.IsEnabled = true;
            }
        });
        await ShowFittedDialogAsync(dialog);
    }

    /// <summary>
    /// Add game's launch command field (label, text box, hint below), also used by Edit game so the two read, look and
    /// validate the same (<see cref="LinuxGameEditOverride.NormalizeLaunchCommand"/>).
    /// </summary>
    private sealed record LaunchCommandFieldControls(TextBlock Label, TextBox Box, TextBlock Hint)
    {
        public void AddTo(StackPanel form)
        {
            form.Children.Add(Label);
            form.Children.Add(Box);
            form.Children.Add(Hint);
        }
    }

    private static LaunchCommandFieldControls LaunchCommandField(string? command, string hintKey) => new(
        new TextBlock { Text = T("Win.LaunchCommandOptional"), FontWeight = FontWeight.SemiBold },
        new TextBox { Text = command, PlaceholderText = T("Win.OptionalLaunchCommand") },
        new TextBlock { Text = T(hintKey), TextWrapping = TextWrapping.Wrap, FontSize = 10, Opacity = 0.6 });

    private void EditGame_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        var game = vm.SelectedGame;

        // Stored provider values stay the canonical tokens; only the labels are localised.
        var providerValues = OptionLists.LaunchProvider.Values;
        var providerIndex = providerValues.ToList().FindIndex(value => string.Equals(value, game.LaunchProvider, StringComparison.OrdinalIgnoreCase));
        var provider = new ComboBox
        {
            ItemsSource = OptionLists.LaunchProvider.Items,
            SelectedIndex = providerIndex >= 0 ? providerIndex : 0,
            MinWidth = 220
        };

        var patchExecutable = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.ExecutablePath : "",
            PlaceholderText = T("Win.SelectRealGameExecutableEG"),
            MinWidth = 200
        };
        var gameRoot = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.InstallPath : "",
            PlaceholderText = T("Win.RealGameRoot"),
            MinWidth = 200
        };
        var binaryDirectory = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.BinaryDirectory : "",
            PlaceholderText = T("Win.DirectoryContainingExecutableLocalComponentFiles"),
            MinWidth = 200
        };

        void InferFromExecutable()
        {
            var path = patchExecutable.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            gameRoot.Text = ManualGamePathInference.InferInstallRoot(full);
            binaryDirectory.Text = Path.GetDirectoryName(full) ?? gameRoot.Text;
        }

        var browseExecutable = new Button { Content = T("Win.Browse"), Padding = new Thickness(10, 5) };
        browseExecutable.Click += (_, _) => RunUiSafe(async () =>
        {
            if (!StorageProvider.CanOpen) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Win.SelectRealGameExecutable"),
                AllowMultiple = false,
                FileTypeFilter = [WindowsExecutableFileType]
            });
            var selected = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(selected)) return;
            patchExecutable.Text = selected;
            InferFromExecutable();
        });

        var detectRoot = new Button { Content = T("Win.Detect"), Padding = new Thickness(10, 5) };
        detectRoot.Click += (_, _) => InferFromExecutable();

        var browseRoot = new Button { Content = T("Win.Browse"), Padding = new Thickness(10, 5) };
        browseRoot.Click += (_, _) => RunUiSafe(async () =>
        {
            if (!StorageProvider.CanOpen) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("Win.SelectRealGameRoot"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                gameRoot.Text = folders[0].TryGetLocalPath() ?? gameRoot.Text;
        });

        var detectBinary = new Button { Content = T("Win.Detect"), Padding = new Thickness(10, 5) };
        detectBinary.Click += (_, _) =>
        {
            var path = patchExecutable.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                binaryDirectory.Text = Path.GetDirectoryName(path) ?? binaryDirectory.Text;
        };

        var browseBinary = new Button { Content = T("Win.Browse"), Padding = new Thickness(10, 5) };
        browseBinary.Click += (_, _) => RunUiSafe(async () =>
        {
            if (!StorageProvider.CanOpen) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("Win.SelectBinariesPatchesDirectory"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                binaryDirectory.Text = folders[0].TryGetLocalPath() ?? binaryDirectory.Text;
        });

        static Grid FieldRow(Control field, params Control[] buttons)
        {
            var columns = "*";
            for (var i = 0; i < buttons.Length; i++) columns += ",Auto";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 8 };
            row.Children.Add(field);
            Grid.SetColumn(field, 0);
            for (var i = 0; i < buttons.Length; i++)
            {
                row.Children.Add(buttons[i]);
                Grid.SetColumn(buttons[i], i + 1);
            }
            return row;
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        var save = new Button { Content = T("Win.Save"), Padding = new Thickness(14, 7) };
        var reset = new Button { Content = T("Win.RestoreAutomaticDetection"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };

        var form = new StackPanel { Spacing = 9, Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = TF("Win.Edit", game.Name), FontSize = 21, FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = T("Win.ExecutableLauncherUsedStartGameIndependent"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        form.Children.Add(new TextBlock { Text = T("Win.LaunchHeading"), FontWeight = FontWeight.SemiBold, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(new TextBlock { Text = TF("Vm.LibrarySource", game.LibraryProvider ?? game.Source), FontSize = 11, Opacity = 0.72 });
        form.Children.Add(new TextBlock { Text = T("Win.LauncherPlatform"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(provider);
        form.Children.Add(new TextBlock { Text = T("Win.LaunchExecutablePreserved"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = game.LaunchExecutablePath ?? T("Win.ManagedByLauncherWithoutExposedLocal"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });
        // The command that runs on Play (the user's, else the detected one), editable as in Add game; clearing it goes
        // back to the detected command.
        var launchCommand = LaunchCommandField(game.LaunchCommand, "Win.EditLaunchCommandHint");
        launchCommand.AddTo(form);

        form.Children.Add(new TextBlock { Text = T("Win.GameModificationTargetHeading"), FontWeight = FontWeight.SemiBold, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 10, 0, 0) });
        form.Children.Add(new TextBlock
        {
            Text = T("Win.TargetUsedArchitectureGraphicsAPIDLL"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.72
        });

        form.Children.Add(new TextBlock { Text = T("Win.TargetExecutable"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(patchExecutable, browseExecutable));
        form.Children.Add(new TextBlock { Text = T("Win.GameRoot"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(gameRoot, detectRoot, browseRoot));
        form.Children.Add(new TextBlock { Text = T("Win.BinariesPatchesDirectory"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(binaryDirectory, detectBinary, browseBinary));
        form.Children.Add(status);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(reset); Grid.SetColumn(reset, 0);
        footer.Children.Add(cancel); Grid.SetColumn(cancel, 2);
        footer.Children.Add(save); Grid.SetColumn(save, 3);
        form.Children.Add(footer);

        var dialog = new Window
        {
            Title = TF("Win.EditGame", game.Name),
            // Preferred size; ShowFittedDialogAsync caps it to the main window. The minimum stays below the 640x480
            // window floor so the dialog can always be made smaller than its owner; the form scrolls vertically. 860 high
            // so the whole form, footer included, shows at the preferred size with the launch command field (PT-BR wraps more).
            Width = 790,
            Height = 860,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = form,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };

        cancel.Click += (_, _) => dialog.Close();
        reset.Click += (_, _) => RunUiSafe(async () =>
        {
            reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                // Closes once the reset is saved; a refusal (managed components present) stays visible here.
                var (reset, message) = await vm.ManualGames.ResetSelectedGameEditAsync(saved: dialog.Close);
                if (!reset) status.Text = message;
            }
            catch (Exception ex)
            {
                status.Text = TF("Win.CouldNotRestoreAutomaticDetection", UiLocalizationService.Present(ex.Message));
            }
            finally
            {
                reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
            }
        });
        save.Click += (_, _) => RunUiSafe(async () =>
        {
            reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var chosenProvider = provider.SelectedIndex >= 0 ? providerValues[provider.SelectedIndex] : null;
                // Closes as soon as the edit is saved; the outcome goes to the main status line.
                var (saved, message) = await vm.ManualGames.SaveSelectedGameEditAsync(
                    patchExecutable.Text?.Trim() ?? "",
                    gameRoot.Text?.Trim() ?? "",
                    binaryDirectory.Text?.Trim() ?? "",
                    chosenProvider,
                    launchCommand.Box.Text,
                    saved: dialog.Close);
                if (!saved) status.Text = message;
            }
            catch (Exception ex)
            {
                status.Text = TF("Win.CouldNotSave", UiLocalizationService.Present(ex.Message));
            }
            finally
            {
                reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
            }
        });

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

    private void QuickStart_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        await ShowSimpleMessageAsync(
            T("Win.RHILinuxQuickStart"),
            T("Win.N1UseRefreshDetectSteamHeroic"));
    });

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
