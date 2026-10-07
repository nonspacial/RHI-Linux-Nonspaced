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
            Dispatcher.UIThread.Post(() => _ = HandleWatchedCandidateAsync(candidate));

        DataContextChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel confirmVm)
                confirmVm.ConfirmAsync = (title, message, accept) => ShowConfirmationAsync(title, message, accept, height: 520);
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        });

        Opened += async (_, _) =>
        {
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
            await ShowStartupMotdIfNeededAsync();
        };

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
            await ShowSimpleMessageAsync(L("Adicionar jogo", "Add game"), L("O executável não existe mais.", "The executable no longer exists."));
            return;
        }

        var installPath = ManualGamePathInference.InferInstallRoot(executable);
        var inferredName = ManualGamePathInference.InferGameName(executable, installPath);
        var nameBox = new TextBox { Text = inferredName, PlaceholderText = L("Nome do jogo", "Game name"), MinWidth = 420 };
        var prefixBox = new TextBox { PlaceholderText = L("Prefixo Wine/Proton opcional, ex.: /caminho/para/pfx", "Optional Wine/Proton prefix, e.g. /path/to/pfx"), MinWidth = 420 };
        var launchBox = new TextBox { PlaceholderText = L("Comando de execução opcional", "Optional launch command"), MinWidth = 420 };
        var browsePrefix = new Button { Content = L("Procurar prefixo…", "Browse prefix…"), Padding = new Thickness(10, 5) };
        browsePrefix.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L("Selecionar prefixo Wine/Proton", "Select Wine/Proton prefix"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                prefixBox.Text = folders[0].TryGetLocalPath() ?? prefixBox.Text;
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var add = new Button { Content = L("Adicionar jogo", "Add game"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = L("Cancelar", "Cancel"), Padding = new Thickness(14, 7) };
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        form.Children.Add(new TextBlock { Text = L("Jogo manual", "Manual game"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = L("O RHI inferiu a raiz do jogo a partir do executável. A entrada passa a usar normalmente a detecção Linux, incluindo dados de compatibilidade remotos e inspeção do runtime.", "RHI inferred the game root from the executable. The entry now uses normal Linux detection, including remote compatibility data and runtime inspection."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        form.Children.Add(new TextBlock { Text = L("Nome", "Name"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = L("Executável", "Executable"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = executable, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        form.Children.Add(new TextBlock { Text = L("Raiz do jogo inferida", "Inferred game root"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = installPath, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        form.Children.Add(new TextBlock { Text = L("Prefixo Wine / Proton (opcional)", "Wine / Proton prefix (optional)"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var prefixRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        prefixRow.Children.Add(prefixBox); Grid.SetColumn(prefixBox, 0);
        prefixRow.Children.Add(browsePrefix); Grid.SetColumn(browsePrefix, 1);
        form.Children.Add(prefixRow);
        form.Children.Add(new TextBlock { Text = L("Comando de execução (opcional)", "Launch command (optional)"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(launchBox);
        form.Children.Add(new TextBlock
        {
            Text = L("Deixe o comando vazio se o RHI deve apenas gerenciar os arquivos do jogo. Para uma build personalizada do Wine, informe exatamente o comando que você já utiliza.", "Leave the command empty if RHI should only manage game files. For a custom Wine build, enter exactly the command you already use."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Opacity = 0.6
        });
        form.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        form.Children.Add(buttons);

        var dialog = new Window
        {
            Title = L("Adicionar jogo — RHI-Linux", "Add game — RHI-Linux"),
            Width = 620,
            Height = 580,
            MinWidth = 520,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = form }
        };
        cancel.Click += (_, _) => dialog.Close();
        add.Click += async (_, _) =>
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
                    LaunchCommand = string.IsNullOrWhiteSpace(launchBox.Text) ? null : launchBox.Text.Trim(),
                    Runner = prefix is null ? "Manual Windows" : "Manual Wine"
                };
                var (added, result) = await vm.TryAddManualGameAsync(entry);
                status.Text = result;
                if (added)
                    dialog.Close();
            }
            catch (Exception ex)
            {
                status.Text = LF("Não foi possível adicionar o jogo: {0}", "Could not add the game: {0}", ex.Message);
            }
            finally
            {
                add.IsEnabled = cancel.IsEnabled = true;
            }
        };
        await ShowFittedDialogAsync(dialog);
    }

    private async void EditGame_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        var game = vm.SelectedGame;

        var provider = new ComboBox
        {
            ItemsSource = LinuxGameIdentityService.LaunchProviderOptions,
            SelectedItem = LinuxGameIdentityService.LaunchProviderOptions.Contains(game.LaunchProvider ?? "", StringComparer.OrdinalIgnoreCase)
                ? game.LaunchProvider
                : "Automático",
            MinWidth = 220
        };

        var patchExecutable = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.ExecutablePath : "",
            PlaceholderText = L("Selecione o executável real do jogo, ex.: .../Game.exe", "Select the real game executable, e.g. .../Game.exe"),
            MinWidth = 200
        };
        var gameRoot = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.InstallPath : "",
            PlaceholderText = L("Raiz real do jogo", "Real game root"),
            MinWidth = 200
        };
        var binaryDirectory = new TextBox
        {
            Text = game.HasConfirmedPatchTarget ? game.BinaryDirectory : "",
            PlaceholderText = L("Diretório onde ficam o executável e os arquivos locais dos componentes", "Directory containing the executable and local component files"),
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

        var browseExecutable = new Button { Content = L("Procurar…", "Browse…"), Padding = new Thickness(10, 5) };
        browseExecutable.Click += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Selecionar o executável real do jogo", "Select the real game executable"),
                AllowMultiple = false,
                FileTypeFilter = [WindowsExecutableFileType]
            });
            var selected = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(selected)) return;
            patchExecutable.Text = selected;
            InferFromExecutable();
        };

        var detectRoot = new Button { Content = L("Detectar", "Detect"), Padding = new Thickness(10, 5) };
        detectRoot.Click += (_, _) => InferFromExecutable();

        var browseRoot = new Button { Content = L("Procurar…", "Browse…"), Padding = new Thickness(10, 5) };
        browseRoot.Click += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L("Selecionar a raiz real do jogo", "Select the real game root"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                gameRoot.Text = folders[0].TryGetLocalPath() ?? gameRoot.Text;
        };

        var detectBinary = new Button { Content = L("Detectar", "Detect"), Padding = new Thickness(10, 5) };
        detectBinary.Click += (_, _) =>
        {
            var path = patchExecutable.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                binaryDirectory.Text = Path.GetDirectoryName(path) ?? binaryDirectory.Text;
        };

        var browseBinary = new Button { Content = L("Procurar…", "Browse…"), Padding = new Thickness(10, 5) };
        browseBinary.Click += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L("Selecionar o diretório de binários/patches", "Select binaries/patches directory"),
                AllowMultiple = false
            });
            if (folders.Count > 0)
                binaryDirectory.Text = folders[0].TryGetLocalPath() ?? binaryDirectory.Text;
        };

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
        var save = new Button { Content = L("Salvar", "Save"), Padding = new Thickness(14, 7) };
        var reset = new Button { Content = L("Restaurar detecção automática", "Restore automatic detection"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = L("Cancelar", "Cancel"), Padding = new Thickness(14, 7) };

        var form = new StackPanel { Spacing = 9, Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = LF("Editar — {0}", "Edit — {0}", game.Name), FontSize = 21, FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = L("O executável/launcher usado para iniciar o jogo é independente do alvo que recebe detecção, ReShade, RenoDX, DLSS, OptiScaler e add-ons. Alterar o alvo abaixo não modifica a configuração de inicialização herdada do Steam/Heroic/Lutris.", "The executable/launcher used to start the game is independent from the target that receives detection, ReShade, RenoDX, DLSS, OptiScaler and add-ons. Changing the target below does not modify the launch configuration inherited from Steam/Heroic/Lutris."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        form.Children.Add(new TextBlock { Text = L("INICIALIZAÇÃO", "LAUNCH"), FontWeight = FontWeight.SemiBold, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(new TextBlock { Text = LF("Origem da biblioteca: {0}", "Library source: {0}", game.LibraryProvider ?? game.Source), FontSize = 11, Opacity = 0.72 });
        form.Children.Add(new TextBlock { Text = L("Launcher / plataforma", "Launcher / platform"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(provider);
        form.Children.Add(new TextBlock { Text = L("Executável de inicialização (preservado)", "Launch executable (preserved)"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = game.LaunchExecutablePath ?? L("Gerenciado pelo launcher sem executável local exposto.", "Managed by the launcher without an exposed local executable."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });
        if (!string.IsNullOrWhiteSpace(game.LaunchCommand))
        {
            form.Children.Add(new TextBlock { Text = L("Comando de inicialização herdado", "Inherited launch command"), FontWeight = FontWeight.SemiBold });
            form.Children.Add(new TextBlock { Text = game.LaunchCommand, TextWrapping = TextWrapping.Wrap, FontSize = 10, Opacity = 0.62 });
        }

        form.Children.Add(new TextBlock { Text = L("ALVO DO JOGO E MODIFICAÇÕES", "GAME AND MODIFICATION TARGET"), FontWeight = FontWeight.SemiBold, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 10, 0, 0) });
        form.Children.Add(new TextBlock
        {
            Text = L("Este é o destino usado para arquitetura, API gráfica, varredura de DLLs e instalação/remoção de componentes locais. O RHI nunca usa o launcher como substituto automático deste alvo quando reconhece um cliente compartilhado.", "This target is used for architecture, graphics API, DLL scanning and local component install/removal. RHI never automatically substitutes the launcher for this target when it recognizes a shared client."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.72
        });

        form.Children.Add(new TextBlock { Text = L("Executável alvo", "Target executable"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(patchExecutable, browseExecutable));
        form.Children.Add(new TextBlock { Text = L("Raiz do jogo", "Game root"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(gameRoot, detectRoot, browseRoot));
        form.Children.Add(new TextBlock { Text = L("Diretório de binários / patches", "Binaries / patches directory"), FontWeight = FontWeight.SemiBold });
        form.Children.Add(FieldRow(binaryDirectory, detectBinary, browseBinary));
        form.Children.Add(status);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(reset); Grid.SetColumn(reset, 0);
        footer.Children.Add(cancel); Grid.SetColumn(cancel, 2);
        footer.Children.Add(save); Grid.SetColumn(save, 3);
        form.Children.Add(footer);

        var dialog = new Window
        {
            Title = LF("Editar jogo — {0}", "Edit game — {0}", game.Name),
            Width = 790,
            Height = 760,
            MinWidth = 680,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = form }
        };

        cancel.Click += (_, _) => dialog.Close();
        reset.Click += async (_, _) =>
        {
            reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                status.Text = await vm.ResetSelectedGameEditAsync();
                dialog.Close();
            }
            catch (Exception ex)
            {
                status.Text = LF("Não foi possível restaurar a detecção automática: {0}", "Could not restore automatic detection: {0}", ex.Message);
            }
            finally
            {
                reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
            }
        };
        save.Click += async (_, _) =>
        {
            reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                var chosenProvider = provider.SelectedItem?.ToString();
                status.Text = await vm.SaveSelectedGameEditAsync(
                    patchExecutable.Text?.Trim() ?? "",
                    gameRoot.Text?.Trim() ?? "",
                    binaryDirectory.Text?.Trim() ?? "",
                    chosenProvider);
                if (status.Text.Contains("alvo de modificações salvo", StringComparison.OrdinalIgnoreCase) || status.Text.Contains("modification target saved", StringComparison.OrdinalIgnoreCase))
                    dialog.Close();
            }
            catch (Exception ex)
            {
                status.Text = LF("Não foi possível salvar: {0}", "Could not save: {0}", ex.Message);
            }
            finally
            {
                reset.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
            }
        };

        await ShowFittedDialogAsync(dialog);
    }

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

    private async void GameFiles_Drop(object? sender, DragEventArgs e)
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
                    await PromptInstallLumaAddonAsync(path, L("🌙 Instalar add-on Luma", "🌙 Install Luma add-on"));
                    break;
                case LinuxDroppedFileKind.ReShadeAddon:
                    await PromptImportCustomAddonAsync(path, L("Add-on solto na janela", "Add-on dropped on window"));
                    break;
                case LinuxDroppedFileKind.LumaArchive:
                    await PromptInstallLumaAddonAsync(path, L("🌙 Instalar pacote Luma", "🌙 Install Luma package"));
                    break;
                case LinuxDroppedFileKind.ModArchive:
                    if (DataContext is MainWindowViewModel vmArchive)
                        vmArchive.StatusText = LF("Arquivo compactado detectado: {0}. O RHI-Linux não instala arquivos compactados automaticamente ao arrastar e soltar; use o instalador do componente ou o gerenciador de Shaders/Add-ons.", "Archive detected: {0}. RHI-Linux does not automatically install archives on drag-and-drop; use the component installer or the Shaders/Add-ons manager.", Path.GetFileName(path));
                    break;
            }
        }
    }

    private void SyncAddonWatcherFromSettings()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.AddonWatcherEnabled)
        {
            _addonWatcher.Stop();
            return;
        }

        if (!_addonWatcher.Start(vm.AddonWatchFolder))
            vm.StatusText = LF("O monitor de add-ons não conseguiu abrir: {0}", "The add-on watcher could not open: {0}", _addonWatcher.CurrentPath ?? vm.AddonWatchFolder);
    }

    private async Task HandleWatchedCandidateAsync(LinuxWatchedFileCandidate candidate)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.AddonWatcherEnabled || !_addonWatcher.IsRunning) return;
        if (candidate.Kind == LinuxWatchedFileKind.Addon)
        {
            if (LinuxDroppedFileClassifier.IsLikelyLumaAddon(candidate.Path))
                await PromptInstallLumaAddonAsync(candidate.Path, L("🌙 Add-on Luma detectado pelo monitor", "🌙 Luma add-on detected by watcher"));
            else
                await PromptImportCustomAddonAsync(candidate.Path, L("Add-on detectado pelo monitor", "Add-on detected by watcher"));
            return;
        }

        if (candidate.Kind == LinuxWatchedFileKind.Archive && LinuxDroppedFileClassifier.IsLikelyLumaPackage(candidate.Path))
        {
            await PromptInstallLumaAddonAsync(candidate.Path, L("🌙 Pacote Luma detectado pelo monitor", "🌙 Luma package detected by watcher"));
            return;
        }

        vm.StatusText = LF("O monitor detectou {0}. Arquivos compactados genéricos nunca são instalados nem apagados automaticamente; abra o fluxo do componente correspondente para aplicá-lo.", "The watcher detected {0}. Generic archives are never installed or deleted automatically; open the corresponding component flow to apply it.", Path.GetFileName(candidate.Path));
    }

    private async Task PromptInstallLumaAddonAsync(string path, string title)
    {
        if (DataContext is not MainWindowViewModel vm || !File.Exists(path)) return;
        if (vm.SelectedGame is null)
        {
            vm.StatusText = L("Selecione o jogo que receberá o add-on Luma e solte o arquivo novamente.", "Select the game that will receive the Luma add-on and drop the file again.");
            return;
        }

        var install = await ShowConfirmationAsync(
            title,
            LF("{0}\n\nInstalar este pacote Luma em {1}?\n\nO RHI aplicará o mesmo fluxo gerenciado de Luma (ReShade, proteção/restauração de arquivos e dgVoodoo2 quando o jogo DX9 exigir). O arquivo de origem será preservado.", "{0}\n\nInstall this Luma package in {1}?\n\nRHI will use the same managed Luma flow (ReShade, file protection/restore and dgVoodoo2 when required by a DX9 game). The source file will be preserved.", Path.GetFileName(path), vm.SelectedGame.Name),
            L("Instalar Luma", "Install Luma"));
        if (!install) return;
        await vm.InstallLocalLumaPackageAsync(path);
    }

    private async Task PromptImportCustomAddonAsync(string path, string title)
    {
        if (DataContext is not MainWindowViewModel vm || !File.Exists(path)) return;
        var import = await ShowConfirmationAsync(
            title,
            LF("{0}\n\nImportar este add-on independente do ReShade para o catálogo Add-ons personalizados do RHI?\n\nIsso não instala o add-on em um jogo, não sobrescreve outro add-on diferente e não apaga o arquivo de origem.", "{0}\n\nImport this standalone ReShade add-on into the RHI Custom Add-ons catalog?\n\nThis does not install the add-on into a game, does not overwrite a different add-on and does not delete the source file.", Path.GetFileName(path)),
            L("Importar", "Import"));
        if (!import) return;
        vm.ImportCustomAddonFile(path);
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message, string acceptText, double height = 280)
    {
        var accept = new Button { Content = acceptText, Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = L("Cancelar", "Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Width = 600,
            Height = height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        return await ShowFittedDialogAsync<bool>(dialog);
    }

    private async void QuickStart_Click(object? sender, RoutedEventArgs e)
    {
        await ShowSimpleMessageAsync(
            L("RHI-Linux — Início rápido", "RHI-Linux — Quick Start"),
            L(
                "1. Use Atualizar para detectar Steam, Heroic, Lutris, Wine e jogos manuais salvos.\n\n" +
                "2. Selecione um jogo e deixe o RHI inspecionar o prefixo Wine/Proton real e os arquivos locais antes de instalar um componente.\n\n" +
                "3. Use as linhas de componentes para ReShade, RenoDX, OptiScaler, Luma e outros componentes gerenciados pelo RHI. Alterações de DLSS/Streamline continuam locais ao jogo e preservam arquivos .original.\n\n" +
                "4. DXVK/VKD3D-Proton, drivers, tradução Vulkan e HDR do compositor pertencem ao Wine/Proton e ao desktop — o RHI-Linux não os substitui.\n\n" +
                "5. Arraste um .exe do Windows para qualquer área do RHI para adicioná-lo manualmente. Arquivos .addon32/.addon64 arrastados só podem ser importados para Add-ons personalizados após confirmação.",
                "1. Use Refresh to detect Steam, Heroic, Lutris, Wine and saved manual games.\n\n" +
                "2. Select a game and let RHI inspect the real Wine/Proton prefix and local files before installing a component.\n\n" +
                "3. Use component rows for ReShade, RenoDX, OptiScaler, Luma and other RHI-managed components. DLSS/Streamline changes remain game-local and preserve .original files.\n\n" +
                "4. DXVK/VKD3D-Proton, drivers, Vulkan translation and compositor HDR belong to Wine/Proton and the desktop — RHI-Linux does not replace them.\n\n" +
                "5. Drag a Windows .exe anywhere in RHI to add it manually. Dropped .addon32/.addon64 files can only be imported into Custom Add-ons after confirmation."));
    }

    private async void Links_Click(object? sender, RoutedEventArgs e)
    {
        var links = new (string Label, string Url)[]
        {
            ("RHI-Linux", RhiProductInfo.RepositoryUrl),
            ("RHI original", "https://github.com/RankFTW/RHI"),
            ("RenoDX", "https://github.com/clshortfuse/renodx"),
            ("ReShade", "https://reshade.me"),
            ("OptiScaler", "https://github.com/optiscaler/OptiScaler")
        };
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = L("Links do projeto", "Project links"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = L("Os links são abertos pelo manipulador padrão do desktop Linux (xdg-open/gio/KDE).", "Links are opened by the default Linux desktop handler (xdg-open/gio/KDE)."), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        foreach (var link in links)
        {
            var button = new Button { Content = link.Label, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 7) };
            button.Click += (_, _) => OpenProjectLink(link.Url);
            panel.Children.Add(button);
        }
        var close = new Button { Content = L("Fechar", "Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7), Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(close);
        var dialog = new Window { Title = "RHI-Linux — Links", Width = 460, Height = 440, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
    }

    private void OpenProjectLink(string url)
    {
        if (LinuxDesktopOpener.TryOpenUri(url, out var error)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.StatusText = error ?? LF("Não foi possível abrir {0}", "Could not open {0}", url);
    }

    private async void Motd_Click(object? sender, RoutedEventArgs e)
    {
        var message = await _motdService.FetchAsync(includeSeen: true);
        if (message is null)
        {
            await ShowSimpleMessageAsync("RHI-Linux — MOTD", L("Nenhuma mensagem do dia está disponível agora. Falhas de rede não interrompem o programa.", "No message of the day is available right now. Network failures do not interrupt the program."));
            return;
        }
        await ShowMotdAsync(message);
    }

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
            Title = L("RHI-Linux — Mensagem do dia", "RHI-Linux — Message of the day"),
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
                vm.StatusText = LF("O MOTD foi exibido, mas não foi possível salvar o estado de visualização: {0}", "The MOTD was shown, but the seen-state could not be saved: {0}", ex.Message);
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
        if (_trayIcon is not null) return;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://RHI/Assets/rhi-tray.png"));
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("Mostrar RHI") { Command = new RelayCommand(ShowMainWindowFromTray) });
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem("Sair") { Command = new RelayCommand(ExitFromTray) });
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
            vm.StatusText = LF("O ícone de bandeja não está disponível nesta sessão: {0}", "The tray icon is unavailable in this session: {0}", ex.Message);
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
