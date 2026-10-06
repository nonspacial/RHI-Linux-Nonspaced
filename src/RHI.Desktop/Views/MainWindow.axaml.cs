using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private static string L(string ptBr, string enUs) => UiLocalizationService.Choose(ptBr, enUs);
    private static string LF(string ptBr, string enUs, params object?[] args) => UiLocalizationService.ChooseFormat(ptBr, enUs, args);

    private static string ShaderCategoryDisplay(string category) => category switch
    {
        "Essential" => L("Essencial", "Essential"),
        "Recommended" => L("Recomendado", "Recommended"),
        "Custom" => L("Personalizado", "Custom"),
        _ => L("Extra", "Extra")
    };

    private static string CatalogDescription(string? description, string displayName, bool shaderPack)
    {
        if (string.IsNullOrWhiteSpace(description))
            return shaderPack
                ? LF("Pacote de shaders ReShade: {0}.", "ReShade shader pack: {0}.", displayName)
                : LF("Add-on do ReShade: {0}.", "ReShade add-on: {0}.", displayName);

        var presented = UiLocalizationService.Present(description);
        if (UiLocalizationService.CurrentLanguage == "pt-BR" && string.Equals(presented, description, StringComparison.Ordinal))
            return shaderPack
                ? LF("Pacote de shaders ReShade: {0}.", "ReShade shader pack: {0}.", displayName)
                : LF("Add-on do ReShade: {0}.", "ReShade add-on: {0}.", displayName);
        return presented;
    }

    private static FilePickerFileType WindowsDllFileType => new(L("DLL do Windows", "Windows DLL"))
    {
        Patterns = ["*.dll", "*.DLL"],
    };

    private static FilePickerFileType WindowsExecutableFileType => new(L("Executável do Windows", "Windows executable"))
    {
        Patterns = ["*.exe", "*.EXE"],
    };

    private static FilePickerFileType DeepFriedChickenArchiveFileType => new(L("Pacote Deep Fried Chicken (ZIP/7Z)", "Deep Fried Chicken package (ZIP/7Z)"))
    {
        Patterns = ["*.zip", "*.ZIP", "*.7z", "*.7Z"],
    };

    private readonly DetailSectionLayoutStore _detailLayoutStore;
    private DetailSectionLayoutSettings _detailLayout;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _detailLayoutStore = new DetailSectionLayoutStore();
        _detailLayout = _detailLayoutStore.Load();
        ApplyDetailSectionLayout();
        InitializeLinuxUx();
        InitializeSectionSummaries();
    }

    private async void CopyHdrLaunchOptions_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var text = vm.GetSelectedHdrLaunchOptions();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            vm.StatusText = L("A área de transferência não está disponível nesta sessão.", "The clipboard is unavailable in this session.");
            return;
        }
        await clipboard.SetTextAsync(text);
        vm.StatusText = L("Perfil de inicialização HDR copiado. O Steam pode usar a forma %command% exibida; no Heroic/Lutris, use as variáveis ou o wrapper na configuração do inicializador, sem tratar %command% como sintaxe universal.", "HDR launch profile copied. Steam can use the displayed %command% form; in Heroic/Lutris, use the variables or wrapper in launcher settings without treating %command% as universal syntax.");
    }


    private async void AddGame_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        if (!StorageProvider.CanOpen)
        {
            await ShowSimpleMessageAsync(L("Adicionar jogo", "Add game"), L("O portal desta sessão não disponibiliza um seletor de arquivos.", "This session portal does not provide a file picker."));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Selecione o executável Windows do jogo", "Select the game Windows executable"),
            AllowMultiple = false,
            FileTypeFilter = [WindowsExecutableFileType],
        });
        if (files.Count == 0) return;
        var executable = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            await ShowSimpleMessageAsync(L("Adicionar jogo", "Add game"), L("O executável selecionado não está disponível como arquivo local.", "The selected executable is not available as a local file."));
            return;
        }

        await ShowAddGameDialogForExecutableAsync(executable);
    }

    private async void BatchDlss_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanOpenDlssBatch) return;

        IReadOnlyList<LinuxDlssBatchCandidate> candidates;
        try
        {
            candidates = await vm.GetDlssBatchCandidatesAsync();
        }
        catch (Exception ex)
        {
            await ShowSimpleMessageAsync(L("DLSS em lote", "DLSS batch"), LF("Não foi possível analisar a biblioteca: {0}", "Could not analyze the library: {0}", ex.Message));
            return;
        }

        var eligible = candidates.Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (eligible.Count == 0)
        {
            await ShowSimpleMessageAsync(L("DLSS em lote", "DLSS batch"), L("Nenhum jogo elegível com DLLs locais de DLSS/Streamline foi detectado.", "No eligible game with local DLSS/Streamline DLLs was detected."));
            return;
        }

        static ComboBox VersionCombo(IEnumerable<string> versions, string current)
        {
            var items = new List<string> { L("Não alterar", "Do not change") };
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
            return string.Equals(value, "Não alterar", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Do not change", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        var defaults = vm.DlssVersionDefaults;
        var sr = VersionCombo(vm.DlssSrVersions, defaults.SuperResolution);
        var rr = VersionCombo(vm.DlssRrVersions, defaults.RayReconstruction);
        var fg = VersionCombo(vm.DlssFgVersions, defaults.FrameGeneration);
        var nr = VersionCombo(vm.DlssNrVersions, defaults.NeuralRendering);
        var sl = VersionCombo(vm.StreamlineVersions, defaults.Streamline);

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

        var selectAll = new Button { Content = L("Selecionar todos", "Select all"), Padding = new Avalonia.Thickness(10, 5) };
        var clearAll = new Button { Content = L("Limpar", "Clear"), Padding = new Avalonia.Thickness(10, 5) };
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
        left.Children.Add(new TextBlock { Text = LF("Jogos elegíveis ({0})", "Eligible games ({0})", eligible.Count), FontSize = 16, FontWeight = FontWeight.SemiBold });
        left.Children.Add(selectRow);
        left.Children.Add(new ScrollViewer { Content = gamesPanel, MaxHeight = 440, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var right = new StackPanel { Spacing = 11, MinWidth = 270 };
        right.Children.Add(new TextBlock { Text = L("Versões", "Versions"), FontSize = 16, FontWeight = FontWeight.SemiBold });
        right.Children.Add(new TextBlock
        {
            Text = L("Preenchido a partir das Configurações. ‘Não alterar’ ignora o componente. Nenhum novo caminho de destino para DLL é criado.", "Filled from Settings. ‘Do not change’ skips the component. No new DLL target path is created."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
            FontSize = 11
        });
        right.Children.Add(LabeledCombo(L("Super-resolução", "Super Resolution"), sr));
        right.Children.Add(LabeledCombo(L("Reconstrução de raios", "Ray Reconstruction"), rr));
        right.Children.Add(LabeledCombo(L("Geração de quadros", "Frame Generation"), fg));
        right.Children.Add(LabeledCombo(L("Renderização neural", "Neural Rendering"), nr));
        right.Children.Add(LabeledCombo("Streamline", sl));

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        var deploy = new Button { Content = L("Aplicar", "Apply"), Padding = new Avalonia.Thickness(14, 7) };
        var restore = new Button { Content = L("Restaurar selecionados", "Restore selected"), Padding = new Avalonia.Thickness(14, 7) };
        var close = new Button { Content = L("Fechar", "Close"), Padding = new Avalonia.Thickness(14, 7) };

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

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(body); Grid.SetRow(body, 0);
        root.Children.Add(footer); Grid.SetRow(footer, 1);

        var dialog = new Window
        {
            Title = L("Aplicação em lote de DLSS e Streamline", "DLSS and Streamline batch operation"),
            Width = 820,
            Height = 650,
            MinWidth = 700,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        IReadOnlyList<DetectedGame> SelectedGames() => rows
            .Where(row => row.Check.IsChecked == true)
            .Select(row => row.Candidate.Game)
            .ToArray();

        async Task RunAsync(bool restoreMode)
        {
            var selected = SelectedGames();
            if (selected.Count == 0)
            {
                status.Text = L("Selecione pelo menos um jogo.", "Select at least one game.");
                return;
            }

            var chosen = new LinuxDlssVersionDefaults(
                SelectedVersion(sr), SelectedVersion(rr), SelectedVersion(fg), SelectedVersion(nr), SelectedVersion(sl));
            if (!restoreMode && !chosen.HasAny)
            {
                status.Text = L("Selecione pelo menos uma versão; usar ‘Não alterar’ em todas as opções não aplicará nenhuma alteração.", "Select at least one version; using ‘Do not change’ for every option will not apply any change.");
                return;
            }

            deploy.IsEnabled = restore.IsEnabled = close.IsEnabled = false;
            try
            {
                status.Text = restoreMode ? L("Restaurando jogos selecionados…", "Restoring selected games…") : L("Aplicando aos jogos selecionados…", "Applying to selected games…");
                var result = restoreMode
                    ? await vm.RestoreDlssBatchAsync(selected)
                    : await vm.ApplyDlssBatchAsync(selected, chosen);
                status.Text = UiLocalizationService.Present(result.Summary);
            }
            catch (Exception ex)
            {
                status.Text = LF("Falha na operação em lote: {0}", "Batch operation failed: {0}", ex.Message);
            }
            finally
            {
                deploy.IsEnabled = restore.IsEnabled = close.IsEnabled = true;
            }
        }

        deploy.Click += async (_, _) => await RunAsync(restoreMode: false);
        restore.Click += async (_, _) => await RunAsync(restoreMode: true);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

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
        await dialog.ShowDialog(this);
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var snapshot = vm.GetLinuxSettingsSnapshot();
        var settings = snapshot.Settings;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };

        static string UiLanguageDisplay(string value) => value == "en-US" ? "English (United States)" : "Português (Brasil)";
        static string UiLanguageValue(string? display) => string.Equals(display, "English (United States)", StringComparison.Ordinal) ? "en-US" : "pt-BR";
        var uiLanguage = new ComboBox
        {
            ItemsSource = new[] { "Português (Brasil)", "English (United States)" },
            SelectedItem = UiLanguageDisplay(settings.UiLanguage),
            Width = 240
        };

        static string ReShadeChannelDisplay(string value) => value switch
        {
            "Nightly" => UiLocalizationService.Text("Ui.OptionNightly"),
            "Legacy" => UiLocalizationService.Text("Ui.OptionLegacy"),
            "Custom" => UiLocalizationService.Text("Ui.OptionCustom"),
            _ => UiLocalizationService.Text("Ui.OptionStable")
        };
        static string ReShadeChannelValue(string? display)
        {
            if (string.Equals(display, UiLocalizationService.Text("Ui.OptionNightly"), StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Desenvolvimento (Nightly)", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Nightly", StringComparison.OrdinalIgnoreCase)) return "Nightly";
            if (string.Equals(display, UiLocalizationService.Text("Ui.OptionLegacy"), StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Legado", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Legacy", StringComparison.OrdinalIgnoreCase)) return "Legacy";
            if (string.Equals(display, UiLocalizationService.Text("Ui.OptionCustom"), StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Personalizado", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "Custom", StringComparison.OrdinalIgnoreCase)) return "Custom";
            return "Stable";
        }
        var channel = new ComboBox
        {
            ItemsSource = new[] { UiLocalizationService.Text("Ui.OptionStable"), UiLocalizationService.Text("Ui.OptionNightly"), UiLocalizationService.Text("Ui.OptionLegacy"), UiLocalizationService.Text("Ui.OptionCustom") },
            SelectedItem = ReShadeChannelDisplay(settings.ReShadeChannel),
            Width = 230
        };
        var manageIni = new CheckBox { Content = L("Gerenciar caminhos de busca do ReShade.ini", "Manage ReShade.ini search paths"), IsChecked = settings.ReShadeManageIni };
        var screenshotPath = new TextBox { Text = settings.ScreenshotPath, PlaceholderText = L("Pasta opcional para capturas de tela", "Optional screenshot folder"), MinWidth = 430 };
        var perGameScreenshots = new CheckBox { Content = L("Criar uma subpasta de capturas para cada jogo", "Create a screenshot subfolder for each game"), IsChecked = settings.PerGameScreenshotFolders };
        var overlayHotkey = new ReShadeHotkeyEditor(settings.OverlayHotkey, "36,0,0,0");
        var screenshotHotkey = new ReShadeHotkeyEditor(settings.ScreenshotHotkey, "44,0,0,0");
        var addonWatchFolder = new TextBox { Text = settings.AddonWatchFolder, PlaceholderText = L("Pasta opcional monitorada (padrão: ~/Downloads)", "Optional watched folder (default: ~/Downloads)"), MinWidth = 430 };
        var cacheAllShaders = new CheckBox { Content = L("Manter pacotes de shaders baixados em cache", "Keep downloaded shader packs cached"), IsChecked = settings.CacheAllShaders };
        var verboseLogging = new CheckBox { Content = L("Registro detalhado", "Verbose logging"), IsChecked = settings.VerboseLogging };
        var autoUpdateComponents = new CheckBox
        {
            Content = L("Atualizar automaticamente em segundo plano os componentes gerenciados pelo RHI", "Automatically update RHI-managed components in the background"),
            IsChecked = settings.AutoUpdateComponents
        };
        var addonWatcherEnabled = new CheckBox
        {
            Content = L("Monitorar a pasta selecionada por add-ons baixados / arquivos RenoDX-Luma", "Watch the selected folder for downloaded add-ons / RenoDX-Luma files"),
            IsChecked = settings.AddonWatcherEnabled
        };
        static string ViewModeDisplay(string value) =>
            value.Equals("Compact", StringComparison.OrdinalIgnoreCase) ? L("Compacta", "Compact") : L("Detalhada", "Detailed");
        static string ViewModeValue(string? display) =>
            string.Equals(display, L("Compacta", "Compact"), StringComparison.OrdinalIgnoreCase) ? "Compact" : "Detail";
        var viewMode = new ComboBox
        {
            ItemsSource = new[] { L("Detalhada", "Detailed"), L("Compacta", "Compact") },
            SelectedItem = ViewModeDisplay(settings.ViewMode),
            Width = 190
        };
        var trayIconEnabled = new CheckBox
        {
            Content = L("Ativar ícone na bandeja do sistema (StatusNotifierItem/AppIndicator)", "Enable system tray icon (StatusNotifierItem/AppIndicator)"),
            IsChecked = settings.TrayIconEnabled
        };
        var closeToTray = new CheckBox
        {
            Content = L("Ocultar na bandeja ao fechar a janela principal", "Hide to tray when closing the main window"),
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
            Content = L("Mostrar cada novo MOTD uma vez ao iniciar", "Show each new MOTD once at startup"),
            IsChecked = settings.ShowMotdOnStartup
        };

        static ComboBox BuildDlssDefaultCombo(IEnumerable<string> versions, string current)
        {
            var items = new List<string> { L("Não alterar", "Do not change") };
            foreach (var version in versions.Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase))
                items.Add(version);
            if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, StringComparer.OrdinalIgnoreCase))
                items.Add(current); // keep the saved default visible/offline instead of silently resetting it
            var selected = string.IsNullOrWhiteSpace(current)
                ? items[0]
                : items.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
            return new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 230 };
        }

        var defaultDlss = BuildDlssDefaultCombo(vm.DlssSrVersions, settings.DefaultDlssVersion);
        var defaultDlssd = BuildDlssDefaultCombo(vm.DlssRrVersions, settings.DefaultDlssdVersion);
        var defaultDlssg = BuildDlssDefaultCombo(vm.DlssFgVersions, settings.DefaultDlssgVersion);
        var defaultDlssnr = BuildDlssDefaultCombo(vm.DlssNrVersions, settings.DefaultDlssnrVersion);
        var defaultStreamline = BuildDlssDefaultCombo(vm.StreamlineVersions, settings.DefaultStreamlineVersion);

        var browseScreenshots = new Button { Content = L("Procurar…", "Browse…"), Padding = new Avalonia.Thickness(12, 6) };
        browseScreenshots.Click += async (_, _) =>
        {
            var chosen = await PickFolderAsync(L("Selecionar pasta de capturas de tela", "Select screenshot folder"));
            if (!string.IsNullOrWhiteSpace(chosen)) screenshotPath.Text = chosen;
        };
        var browseWatch = new Button { Content = L("Procurar…", "Browse…"), Padding = new Avalonia.Thickness(12, 6) };
        browseWatch.Click += async (_, _) =>
        {
            var chosen = await PickFolderAsync(L("Selecionar pasta monitorada de add-ons", "Select watched add-on folder"));
            if (!string.IsNullOrWhiteSpace(chosen)) addonWatchFolder.Text = chosen;
        };

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
        reshadeTab.Children.Add(new TextBlock { Text = L("Padrões do ReShade", "ReShade defaults"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        reshadeTab.Children.Add(new TextBlock
        {
            Text = L("Persistência nativa no Linux para configurações compatíveis com o RHI. Escolha a tecla principal do atalho e, se quiser uma combinação, marque Ctrl, Shift e/ou Alt.", "Native Linux persistence for RHI-compatible settings. Choose the main hotkey and, if you want a combination, enable Ctrl, Shift and/or Alt."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        reshadeTab.Children.Add(Labeled(L("Canal padrão do ReShade", "Default ReShade channel"), channel));
        reshadeTab.Children.Add(manageIni);
        reshadeTab.Children.Add(Labeled(L("Pasta de capturas de tela", "Screenshot folder"), screenshotRow, L("Vazio mantém inalterado o caminho de salvamento já configurado pelo ReShade.", "Empty keeps the save path already configured by ReShade unchanged.")));
        reshadeTab.Children.Add(perGameScreenshots);
        reshadeTab.Children.Add(Labeled(L("Atalho da sobreposição", "Overlay hotkey"), overlayHotkey.Control, L("Padrão: Home (Início). O RHI salva esse atalho no formato exigido pelo ReShade.", "Default: Home. RHI stores this hotkey in the format required by ReShade.")));
        reshadeTab.Children.Add(Labeled(L("Atalho de captura de tela", "Screenshot hotkey"), screenshotHotkey.Control, L("Padrão: Print Screen. O RHI salva esse atalho no formato exigido pelo ReShade.", "Default: Print Screen. RHI stores this hotkey in the format required by ReShade.")));

        var managerTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        managerTab.Children.Add(new TextBlock { Text = "Shaders / Add-ons", FontSize = 20, FontWeight = FontWeight.SemiBold });
        managerTab.Children.Add(Labeled(L("Cache de shaders", "Shader cache"), cacheAllShaders, L("Mantém os pacotes de shaders baixados no cache XDG do RHI para reutilização.", "Keeps downloaded shader packs in the RHI XDG cache for reuse.")));
        managerTab.Children.Add(addonWatcherEnabled);
        managerTab.Children.Add(Labeled(L("Pasta monitorada de add-ons", "Watched add-on folder"), watchRow,
            L("Monitor nativo do Linux. Nunca apaga os arquivos de origem nem instala diretamente em um jogo. Arquivos .addon32/.addon64 exigem confirmação e são importados apenas para Add-ons personalizados; arquivos compactados geram somente uma notificação.", "Native Linux watcher. It never deletes source files or installs directly into a game. .addon32/.addon64 files require confirmation and are imported only into Custom Add-ons; archives only generate a notification.")));
        managerTab.Children.Add(Labeled(L("Registro detalhado", "Verbose logging"), verboseLogging, L("A preferência é persistida; os logs dos componentes permanecem nos caminhos XDG de dados/configuração do RHI.", "The preference is persisted; component logs remain under RHI XDG data/config paths.")));

        var dlssTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        dlssTab.Children.Add(new TextBlock { Text = L("Padrões de DLSS / Streamline", "DLSS / Streamline defaults"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        dlssTab.Children.Add(new TextBlock
        {
            Text = L("Aplicação rápida e aplicação em lote substituem apenas DLLs locais que já existem no jogo. ‘Não alterar’ ignora o componente. Predefinições de perfil do driver NVIDIA e escala de renderização dependem de NVAPI e não são emuladas no Linux.", "Quick apply and batch apply replace only local DLLs that already exist in the game. “Do not change” skips the component. NVIDIA driver profile presets and render scaling depend on NVAPI and are not emulated on Linux."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        dlssTab.Children.Add(Labeled(L("Super-resolução (nvngx_dlss.dll)", "Super Resolution (nvngx_dlss.dll)"), defaultDlss));
        dlssTab.Children.Add(Labeled(L("Reconstrução de raios (nvngx_dlssd.dll)", "Ray Reconstruction (nvngx_dlssd.dll)"), defaultDlssd));
        dlssTab.Children.Add(Labeled(L("Geração de quadros (nvngx_dlssg.dll)", "Frame Generation (nvngx_dlssg.dll)"), defaultDlssg));
        dlssTab.Children.Add(Labeled(L("Renderização neural (nvngx_dlssnr.dll)", "Neural Rendering (nvngx_dlssnr.dll)"), defaultDlssnr));
        dlssTab.Children.Add(Labeled("Streamline", defaultStreamline));

        var pathGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 8 };
        void AddPathRow(int row, string label, string value)
        {
            var a = new TextBlock { Text = label, Opacity = 0.72 };
            var b = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            pathGrid.Children.Add(a); Grid.SetRow(a, row); Grid.SetColumn(a, 0);
            pathGrid.Children.Add(b); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
        }
        AddPathRow(0, "settings.json", snapshot.SettingsPath);
        AddPathRow(1, L("Configuração", "Configuration"), snapshot.ConfigRoot);
        AddPathRow(2, "Cache", snapshot.CacheRoot);
        AddPathRow(3, L("Dados", "Data"), snapshot.DataRoot);

        var openConfig = new Button { Content = L("Abrir configuração", "Open configuration"), Padding = new Avalonia.Thickness(12, 6) };
        var openCache = new Button { Content = L("Abrir cache", "Open cache"), Padding = new Avalonia.Thickness(12, 6) };
        var openData = new Button { Content = L("Abrir dados", "Open data"), Padding = new Avalonia.Thickness(12, 6) };
        void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            if (!LinuxDesktopOpener.TryOpenDirectory(path, out var error)) status.Text = LF("Não foi possível abrir a pasta: {0}", "Could not open folder: {0}", error);
        }
        openConfig.Click += (_, _) => OpenFolder(snapshot.ConfigRoot);
        openCache.Click += (_, _) => OpenFolder(snapshot.CacheRoot);
        openData.Click += (_, _) => OpenFolder(snapshot.DataRoot);

        var purgeCache = new Button { Content = L("Limpar cache", "Clear cache"), Padding = new Avalonia.Thickness(12, 6) };
        purgeCache.Click += (_, _) => status.Text = vm.PurgeLinuxCache();
        var reset = new Button { Content = UiLocalizationService.Text("Ui.ResetSettings"), Padding = new Avalonia.Thickness(12, 6) };
        reset.Click += (_, _) =>
        {
            var defaults = new LinuxAppSettings();
            uiLanguage.SelectedItem = UiLanguageDisplay(defaults.UiLanguage);
            channel.SelectedItem = ReShadeChannelDisplay(defaults.ReShadeChannel);
            manageIni.IsChecked = defaults.ReShadeManageIni;
            screenshotPath.Text = defaults.ScreenshotPath;
            perGameScreenshots.IsChecked = defaults.PerGameScreenshotFolders;
            overlayHotkey.SetValue(defaults.OverlayHotkey);
            screenshotHotkey.SetValue(defaults.ScreenshotHotkey);
            addonWatchFolder.Text = defaults.AddonWatchFolder;
            addonWatcherEnabled.IsChecked = defaults.AddonWatcherEnabled;
            viewMode.SelectedItem = ViewModeDisplay(defaults.ViewMode);
            trayIconEnabled.IsChecked = defaults.TrayIconEnabled;
            closeToTray.IsChecked = defaults.CloseToTray;
            closeToTray.IsEnabled = defaults.TrayIconEnabled;
            showMotdOnStartup.IsChecked = defaults.ShowMotdOnStartup;
            cacheAllShaders.IsChecked = defaults.CacheAllShaders;
            verboseLogging.IsChecked = defaults.VerboseLogging;
            autoUpdateComponents.IsChecked = defaults.AutoUpdateComponents;
            defaultDlss.SelectedItem = L("Não alterar", "Do not change");
            defaultDlssd.SelectedItem = L("Não alterar", "Do not change");
            defaultDlssg.SelectedItem = L("Não alterar", "Do not change");
            defaultDlssnr.SelectedItem = L("Não alterar", "Do not change");
            defaultStreamline.SelectedItem = L("Não alterar", "Do not change");
            status.Text = vm.ResetLinuxSettings();
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        };

        var uxTab = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        uxTab.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ui.DesktopExperience"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        uxTab.Children.Add(new TextBlock
        {
            Text = L("Aqui ficam apenas conveniências nativas do desktop. O RHI não instala extensões do shell nem substitui serviços de sessão do KDE/GNOME.", "Native desktop conveniences live here. RHI does not install shell extensions or replace KDE/GNOME session services."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        uxTab.Children.Add(Labeled(UiLocalizationService.Text("Ui.Language"), uiLanguage, UiLocalizationService.Text("Ui.LanguageNote")));
        uxTab.Children.Add(Labeled(UiLocalizationService.Text("Ui.ViewMode"), viewMode,
            L("A visualização compacta mantém o fluxo do jogo selecionado, reduzindo barras, filtros e o espaço ocupado pela janela.", "Compact view keeps the selected-game workflow while reducing toolbars, filters and window space.")));
        uxTab.Children.Add(trayIconEnabled);
        uxTab.Children.Add(closeToTray);
        uxTab.Children.Add(new TextBlock
        {
            Text = L("O suporte à bandeja depende do ambiente. O KDE oferece StatusNotifierItem nativamente; algumas sessões GNOME exigem AppIndicator. Ocultar na bandeja só é usado quando o ícone foi realmente criado.", "Tray support depends on the desktop environment. KDE provides StatusNotifierItem natively; some GNOME sessions require AppIndicator. Hide to tray is used only when the icon was actually created."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.65
        });
        uxTab.Children.Add(showMotdOnStartup);

        var maintenance = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18) };
        maintenance.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ui.Maintenance"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        maintenance.Children.Add(Labeled(L("Atualizações de componentes em segundo plano", "Background component updates"), autoUpdateComponents,
            L("Desativado por padrão. Quando ativado, o RHI verifica as oito famílias de componentes do Atualizar tudo após iniciar e a cada 4 horas. Jogos em execução ficam para uma verificação posterior.", "Disabled by default. When enabled, RHI checks the component families used by Update all after startup and every 4 hours. Running games are deferred to a later check.")));
        maintenance.Children.Add(pathGrid);
        var pathButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathButtons.Children.Add(openConfig); pathButtons.Children.Add(openCache); pathButtons.Children.Add(openData);
        maintenance.Children.Add(pathButtons);
        var maintenanceButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        maintenanceButtons.Children.Add(purgeCache); maintenanceButtons.Children.Add(reset);
        maintenance.Children.Add(maintenanceButtons);
        maintenance.Children.Add(new TextBlock
        {
            Text = L("Limpar cache remove somente os dados de cache do RHI-Linux em XDG_CACHE_HOME/rhi-linux. Arquivos dos jogos e prefixos Wine/Proton não são alterados.", "Clear cache removes only RHI-Linux cache data under XDG_CACHE_HOME/rhi-linux. Game files and Wine/Proton prefixes are not changed."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68
        });

        LinuxAppSettings ReadControls() => new()
        {
            UiLanguage = UiLanguageValue(uiLanguage.SelectedItem?.ToString()),
            ReShadeChannel = ReShadeChannelValue(channel.SelectedItem?.ToString()),
            ReShadeManageIni = manageIni.IsChecked == true,
            ScreenshotPath = screenshotPath.Text ?? "",
            OverlayHotkey = overlayHotkey.Value,
            ScreenshotHotkey = screenshotHotkey.Value,
            PerGameScreenshotFolders = perGameScreenshots.IsChecked == true,
            AddonWatchFolder = addonWatchFolder.Text ?? "",
            AddonWatcherEnabled = addonWatcherEnabled.IsChecked == true,
            ViewMode = ViewModeValue(viewMode.SelectedItem?.ToString()),
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
            DefaultStreamlineVersion = ReadDlssDefault(defaultStreamline)
        };

        static string ReadDlssDefault(ComboBox combo)
        {
            var value = combo.SelectedItem?.ToString() ?? "";
            return string.Equals(value, "Não alterar", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Do not change", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        var save = new Button { Content = UiLocalizationService.Text("Ui.Save"), Padding = new Avalonia.Thickness(14, 7) };
        save.Click += (_, _) =>
        {
            status.Text = vm.SaveLinuxSettings(ReadControls());
            save.Content = UiLocalizationService.Text("Ui.Save");
            reset.Content = UiLocalizationService.Text("Ui.ResetSettings");
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        };
        var applyGame = new Button
        {
            Content = L("Aplicar padrões do ReShade ao jogo selecionado", "Apply ReShade defaults to selected game"),
            Padding = new Avalonia.Thickness(14, 7),
            IsEnabled = vm.HasSelectedGame
        };
        applyGame.Click += (_, _) =>
        {
            try
            {
                var current = ReadControls();
                vm.SaveLinuxSettings(current);
                ApplyViewMode();
                SyncAddonWatcherFromSettings();
                SyncTrayIcon();
                status.Text = vm.ApplyGlobalReShadeSettingsToSelectedGame(current);
            }
            catch (Exception ex) { status.Text = LF("Não foi possível aplicar os padrões do ReShade: {0}", "Could not apply ReShade defaults: {0}", ex.Message); }
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
                new TabItem { Header = UiLocalizationService.Text("Ui.MaintenanceTab"), Content = new ScrollViewer { Content = maintenance } }
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
        await dialog.ShowDialog(this);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    private async void ShadersAddons_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null)
            return;
        if (!vm.HasConfirmedPatchTarget)
        {
            vm.StatusText = L("Defina o executável alvo em Editar jogo antes de abrir o gerenciador de Shaders/Add-ons.", "Set the target executable in Edit game before opening the Shaders/Add-ons manager.");
            return;
        }

        vm.StatusText = L("Carregando catálogo de Shaders/Add-ons…", "Loading Shaders/Add-ons catalog…");
        ShaderAddonManagerUiSnapshot? snapshot;
        try
        {
            snapshot = await vm.GetShaderAddonManagerSnapshotAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = LF("Não foi possível carregar o catálogo de Shaders/Add-ons: {0}", "Could not load the Shaders/Add-ons catalog: {0}", ex.Message);
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
                    ? LF("\nRequer: {0}", "\nRequires: {0}", string.Join(", ", pack.Requires))
                    : "";
                ToolTip.SetTip(check, $"{CatalogDescription(pack.Description, pack.DisplayName, shaderPack: true)}{dependency}\nID: {pack.Id}");
                shaderChecks.Add((pack.Id, check));
                var packRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                packRow.Children.Add(check);
                Grid.SetColumn(check, 0);
                var filesButton = new Button
                {
                    Content = L("Arquivos…", "Files…"),
                    Padding = new Avalonia.Thickness(10, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(filesButton, L("Selecione shaders e texturas individualmente. A seleção fica salva por pacote.", "Select shaders and textures individually. The selection is saved per pack."));
                filesButton.Click += async (_, _) =>
                {
                    try { await ShowShaderPackFilesDialogAsync(vm, pack); }
                    catch (Exception ex) { vm.StatusText = LF("Não foi possível abrir os arquivos de {0}: {1}", "Could not open files for {0}: {1}", pack.DisplayName, ex.Message); }
                };
                packRow.Children.Add(filesButton);
                Grid.SetColumn(filesButton, 1);
                shaderRoot.Children.Add(packRow);
            }
        }

        var addonRoot = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12) };
        foreach (var addon in snapshot.Addons.OrderBy(x => x.ManagedByComponent).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var arch = $"{(addon.Supports32Bit ? "x86" : "")}{(addon.Supports32Bit && addon.Supports64Bit ? "/" : "")}{(addon.Supports64Bit ? "x64" : "")}";
            var label = addon.ManagedByComponent
                ? LF("{0}  · gerenciado pelo componente", "{0}  · component-managed", addon.DisplayName)
                : addon.IsCustom
                    ? LF("{0}  · personalizado", "{0}  · custom", addon.DisplayName)
                    : addon.DisplayName;

            var check = new CheckBox
            {
                Content = label,
                IsChecked = selectedAddons.Contains(addon.Id),
                IsEnabled = !addon.ManagedByComponent,
                Margin = new Avalonia.Thickness(0, 2)
            };
            ToolTip.SetTip(check, LF("{0}\nArquiteturas: {1}\nID: {2}", "{0}\nArchitectures: {1}\nID: {2}", CatalogDescription(addon.Description, addon.DisplayName, shaderPack: false), arch, addon.Id));
            addonChecks.Add((addon.Id, check));
            addonRoot.Children.Add(check);
        }

        var status = new TextBlock
        {
            Text = LF("Catálogo: {0} pacote(s) de shaders gerenciados + Personalizados locais; {1} entrada(s) de add-ons.", "Catalog: {0} managed shader pack(s) + local Custom entries; {1} add-on entries.", snapshot.ShaderPacks.Count(x => !x.IsCustom), snapshot.Addons.Count),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        };

        var apply = new Button
        {
            Content = L("Aplicar ao jogo selecionado", "Apply to selected game"),
            Padding = new Avalonia.Thickness(14, 7),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var openCustomShaders = new Button { Content = L("Abrir Shaders personalizados", "Open Custom Shaders"), Padding = new Avalonia.Thickness(12, 6) };
        var openCustomAddons = new Button { Content = L("Abrir Add-ons personalizados", "Open Custom Add-ons"), Padding = new Avalonia.Thickness(12, 6) };

        openCustomShaders.Click += (_, _) =>
        {
            Directory.CreateDirectory(snapshot.CustomShaderRoot);
            if (!LinuxDesktopOpener.TryOpenDirectory(snapshot.CustomShaderRoot, out var error))
                status.Text = LF("Não foi possível abrir Shaders personalizados: {0}", "Could not open Custom Shaders: {0}", error);
        };

        openCustomAddons.Click += (_, _) =>
        {
            Directory.CreateDirectory(snapshot.CustomAddonRoot);
            if (!LinuxDesktopOpener.TryOpenDirectory(snapshot.CustomAddonRoot, out var error))
                status.Text = LF("Não foi possível abrir Add-ons personalizados: {0}", "Could not open Custom Add-ons: {0}", error);
        };

        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                var shaders = shaderChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray();
                var addons = addonChecks
                    .Where(x => x.Box.IsEnabled && x.Box.IsChecked == true)
                    .Select(x => x.Id)
                    .ToArray();

                status.Text = LF("Aplicando {0} pacote(s) de shaders e {1} add-on(s)…", "Applying {0} shader pack(s) and {1} add-on(s)…", shaders.Length, addons.Length);
                var result = await vm.ApplyShaderAddonManagerAsync(shaders, addons);
                status.Text = UiLocalizationService.Present(result.Message);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        bottom.Children.Add(openCustomShaders);
        Grid.SetColumn(openCustomShaders, 0);
        bottom.Children.Add(openCustomAddons);
        Grid.SetColumn(openCustomAddons, 1);
        bottom.Children.Add(status);
        Grid.SetColumn(status, 2);
        bottom.Children.Add(apply);
        Grid.SetColumn(apply, 3);

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = L("Pacotes de shaders", "Shader packs"),
                    Content = new ScrollViewer { Content = shaderRoot }
                },
                new TabItem
                {
                    Header = L("Add-ons do ReShade", "ReShade add-ons"),
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

        await dialog.ShowDialog(this);
    }

    private async void BrowseD3DCompiler47_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDllAsync("Selecionar Microsoft D3DCompiler_47.dll");
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeD3DCompiler47Path = path;
    }

    private async void BrowseReShadeCustomDll_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDllAsync("Selecionar DLL personalizada do ReShade");
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeCustomDllPath = path;
    }

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
                ? L("Nenhum shader/textura utilizável foi encontrado neste pacote.", "No usable shader/texture was found in this package.")
                : L("Marcado = será implantado. Desmarcado = permanece excluído nas próximas operações de instalar/atualizar. Shaders e texturas usam caminhos relativos estáveis.", "Checked = deployed. Unchecked = remains excluded on future install/refresh operations. Shaders and textures use stable relative paths."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var checks = new List<(ReShadeShaderPackFileInfo File, CheckBox Box)>();
        foreach (var group in files.GroupBy(x => x.IsTexture ? L("Texturas", "Textures") : "Shaders"))
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
            Content = L("Salvar seleção", "Save selection"),
            Padding = new Avalonia.Thickness(14, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = files.Count > 0
        };
        Window? dialog = null;
        save.Click += (_, _) =>
        {
            var excluded = checks.Where(x => x.Box.IsChecked != true).Select(x => x.File.Selector).ToArray();
            vm.SetShaderPackExcludedFiles(pack.Id, excluded);
            status.Text = LF("Seleção salva: {0}/{1} arquivo(s) ativos.", "Selection saved: {0}/{1} active file(s).", checks.Count - excluded.Length, checks.Count);
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
            Title = $"Shaders/Textures — {pack.DisplayName}",
            Width = 720,
            Height = 680,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        await dialog.ShowDialog(this);
    }

    private async void BrowseDlss5FeederConsumer_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickDeepFriedChickenArchiveAsync("Selecionar pacote oficial Deep Fried Chicken");
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.Dlss5FeederConsumerPackagePath = path;
    }

    private void ClearDlss5FeederConsumer_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Dlss5FeederConsumerPackagePath = string.Empty;
    }

    private static string BuildRenoDxCompatibilityTooltip(RenoDxCompatibilitySetting setting)
    {
        var description = setting.Key switch
        {
            "Upgrade_R10G10B10A2_TYPELESS" => L("Recursos RGB de 10 bits por canal + alpha de 2 bits sem interpretação fixa.", "10-bit-per-channel RGB resources with 2-bit alpha and no fixed interpretation."),
            "Upgrade_R10G10B10A2_UNORM" => L("RGB 10-bit normalizado, comum em buffers de maior precisão.", "Normalized 10-bit RGB, commonly used in higher-precision buffers."),
            "Upgrade_R11G11B10_FLOAT" => L("Formato RGB de ponto flutuante compacto, comum em buffers de iluminação e alta faixa dinâmica.", "Compact floating-point RGB format commonly used in lighting and high-dynamic-range buffers."),
            "Upgrade_R16G16B16A16_TYPELESS" => L("Recursos RGBA de alta precisão cujo tipo final é definido pela view.", "High-precision RGBA resources whose final type is defined by the view."),
            "Upgrade_R8G8B8A8_TYPELESS" => L("Recursos RGBA de 8 bits cujo tipo final é definido pela view.", "8-bit RGBA resources whose final type is defined by the view."),
            "Upgrade_R8G8B8A8_UNORM" => L("Cor RGBA de 8 bits normalizada sem conversão sRGB.", "Normalized 8-bit RGBA color without sRGB conversion."),
            "Upgrade_R8G8B8A8_UNORM_SRGB" => L("Cor RGBA de 8 bits com conversão sRGB.", "8-bit RGBA color with sRGB conversion."),
            "Upgrade_CopyDestinations" => L("Permite aplicar a lógica de upgrade também a recursos usados como destino de operações de cópia.", "Allows upgrade logic to apply to resources used as copy-operation destinations."),
            "Upgrade_UseSCRGB" => L("Controla o uso do caminho de saída scRGB pelo RenoDX.", "Controls RenoDX use of the scRGB output path."),
            "Upgrade_SwapChainCompatibility" => L("Controla o modo de compatibilidade de upgrade da swapchain.", "Controls the swapchain upgrade compatibility mode."),
            "Set_Path" => L("Seleciona o caminho de tratamento HDR/SDR usado pelo RenoDX.", "Selects the HDR/SDR processing path used by RenoDX."),
            "DumpLUTShaders" => L("Controla a exportação de shaders LUT para diagnóstico.", "Controls LUT shader export for diagnostics."),
            _ when setting.ManifestDriven => L("Configuração de compatibilidade fornecida pelo RHI.", "Compatibility setting provided by RHI."),
            _ => L("Configuração avançada de compatibilidade do RenoDX.", "Advanced RenoDX compatibility setting.")
        };

        var format = setting.Key.StartsWith("Upgrade_R", StringComparison.OrdinalIgnoreCase)
            ? setting.Key["Upgrade_".Length..]
            : null;
        var technical = format is null
            ? LF("Chave RenoDX: {0}", "RenoDX key: {0}", setting.Key)
            : LF("Formato DirectX: {0}\nChave RenoDX: {1}", "DirectX format: {0}\nRenoDX key: {1}", format, setting.Key);
        return $"{description}\n\n{technical}";
    }

    private async void Dlssg2030Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasDlssg2030Settings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;

        var preferences = vm.GetDlssg2030Preferences();
        var record = vm.GetDlssg2030Record();
        var installed = record is not null;
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Dlssg2030Title"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Dlssg2030Intro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Dlssg2030RouteNote"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#D7B65A"))
        });

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        var gpuCombo = new ComboBox
        {
            ItemsSource = Dlssg2030UnlockDeploymentService.GpuGenerations,
            SelectedItem = preferences.GpuGeneration,
            MinWidth = 260,
            IsEnabled = !installed
        };
        var proxyCombo = new ComboBox
        {
            ItemsSource = Dlssg2030UnlockDeploymentService.ProxyNames,
            SelectedItem = preferences.ProxyName,
            MinWidth = 220,
            IsEnabled = !installed
        };

        static Grid SettingsRow(string labelText, Control control)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
            grid.Children.Add(new TextBlock
            {
                Text = labelText,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        panel.Children.Add(SettingsRow(UiLocalizationService.Text("Ui.Dlssg2030Gpu"), gpuCombo));
        panel.Children.Add(SettingsRow(UiLocalizationService.Text("Ui.Dlssg2030Proxy"), proxyCombo));

        if (installed)
        {
            var installedGpu = record!.Metadata.GetValueOrDefault("FG2030.GpuGeneration") ?? preferences.GpuGeneration;
            var installedProxy = record.Metadata.GetValueOrDefault("FG2030.ProxyName") ?? preferences.ProxyName;
            panel.Children.Add(new TextBlock
            {
                Text = LF("Instalado: {0} · {1}. Para mudar geração/proxy, remova e reinstale o componente; o RHI não troca a autoridade de um slot DLL no meio de uma instalação gerenciada.", "Installed: {0} · {1}. To change generation/proxy, remove and reinstall the component; RHI does not change DLL slot authority during a managed installation.", installedGpu, installedProxy),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.74
            });
        }

        void SavePreferences()
        {
            if (installed) return;
            var gpu = gpuCombo.SelectedItem?.ToString() ?? Dlssg2030UnlockDeploymentService.GpuGenRtx20;
            var proxy = proxyCombo.SelectedItem?.ToString() ?? Dlssg2030UnlockDeploymentService.DefaultProxyName;
            var result = vm.SetDlssg2030Preferences(gpu, proxy);
            message.Text = UiLocalizationService.Present(result.Message);
        }
        gpuCombo.SelectionChanged += (_, _) => SavePreferences();
        proxyCombo.SelectionChanged += (_, _) => SavePreferences();

        if (installed)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var check = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.Dlssg2030Check") };
            var update = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.Dlssg2030Update"), IsEnabled = false };
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                try
                {
                    var status = await vm.CheckDlssg2030UpdateAsync();
                    message.Text = UiLocalizationService.Present(status.Message);
                    update.IsEnabled = status.Success && status.HasUpdate;
                }
                finally { check.IsEnabled = true; }
            };
            update.Click += async (_, _) =>
            {
                update.IsEnabled = false;
                var result = await vm.UpdateDlssg2030Async();
                message.Text = UiLocalizationService.Present(result.Message);
            };
            buttons.Children.Add(check);
            buttons.Children.Add(update);
            panel.Children.Add(buttons);
        }

        panel.Children.Add(message);
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Dlssg2030CompatibilityNote"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.65
        });

        var dialog = new Window
        {
            Title = $"{UiLocalizationService.Text("Ui.Dlssg2030Title")} — {vm.SelectedGame.Name}",
            Width = 690,
            Height = 560,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await dialog.ShowDialog(this);
    }

    private async void StandaloneDlssEnablerSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasStandaloneDlssEnablerSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;

        var preferences = vm.GetStandaloneDlssEnablerPreferences();
        var record = vm.GetStandaloneDlssEnablerRecord();
        var installed = record is not null;
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.DlssEnablerStandaloneTitle"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.DlssEnablerStandaloneIntro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        var proxyCombo = new ComboBox
        {
            ItemsSource = StandaloneDlssEnablerDeploymentService.ProxyNames,
            SelectedItem = preferences.ProxyName,
            MinWidth = 240,
            IsEnabled = !installed
        };

        var settingsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
        settingsRow.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.DlssEnablerStandaloneProxy"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(proxyCombo, 1);
        settingsRow.Children.Add(proxyCombo);
        panel.Children.Add(settingsRow);

        if (installed)
        {
            var installedProxy = record!.Metadata.GetValueOrDefault("DLSSEnabler.ProxyName") ?? preferences.ProxyName;
            var installedVersion = record.Version ?? "?";
            panel.Children.Add(new TextBlock
            {
                Text = LF("Instalado: {0} · {1}. Para trocar o proxy, remova e reinstale; o RHI não muda a autoridade de um slot DLL durante uma instalação gerenciada.", "Installed: {0} · {1}. To change the proxy, remove and reinstall; RHI does not change DLL slot authority during a managed installation.", installedVersion, installedProxy),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.74
            });
        }

        proxyCombo.SelectionChanged += (_, _) =>
        {
            if (installed) return;
            var proxy = proxyCombo.SelectedItem?.ToString() ?? StandaloneDlssEnablerDeploymentService.DefaultProxyName;
            var result = vm.SetStandaloneDlssEnablerPreferences(proxy);
            message.Text = UiLocalizationService.Present(result.Message);
        };

        if (installed)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var check = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.DlssEnablerStandaloneCheck") };
            var update = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.DlssEnablerStandaloneUpdate"), IsEnabled = false };

            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                try
                {
                    var status = await vm.CheckStandaloneDlssEnablerUpdateAsync();
                    message.Text = UiLocalizationService.Present(status.Message);
                    update.IsEnabled = status.Success && status.HasUpdate;
                }
                finally { check.IsEnabled = true; }
            };

            update.Click += async (_, _) =>
            {
                update.IsEnabled = false;
                var result = await vm.UpdateStandaloneDlssEnablerAsync();
                message.Text = UiLocalizationService.Present(result.Message);
            };

            buttons.Children.Add(check);
            buttons.Children.Add(update);
            panel.Children.Add(buttons);
        }

        panel.Children.Add(message);
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.DlssEnablerStandaloneCompatibilityNote"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#D7B65A")),
            Opacity = 0.78
        });

        var dialog = new Window
        {
            Title = $"{UiLocalizationService.Text("Ui.DlssEnablerStandaloneTitle")} — {vm.SelectedGame.Name}",
            Width = 660,
            Height = 470,
            MinWidth = 540,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await dialog.ShowDialog(this);
    }

    private async void Rtx40MfgSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasRtx40MfgSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;

        var preferences = vm.GetRtx40MfgPreferences();
        var record = vm.GetRtx40MfgRecord();
        var installed = record is not null;
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgTitle"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgIntro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        var gpuCombo = new ComboBox
        {
            ItemsSource = Rtx40MfgStandaloneDeploymentService.GpuGenerations,
            SelectedItem = preferences.GpuGeneration,
            MinWidth = 240,
            IsEnabled = !installed
        };
        var proxyCombo = new ComboBox
        {
            ItemsSource = Rtx40MfgStandaloneDeploymentService.SelectableProxyNames,
            SelectedItem = preferences.ProxyName,
            MinWidth = 240,
            IsEnabled = !installed
        };

        var gpuRow = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
        gpuRow.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgGpuGeneration"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(gpuCombo, 1);
        gpuRow.Children.Add(gpuCombo);
        panel.Children.Add(gpuRow);

        var settingsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
        settingsRow.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgProxy"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(proxyCombo, 1);
        settingsRow.Children.Add(proxyCombo);
        panel.Children.Add(settingsRow);

        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgCompatibility"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.74
        });

        if (installed)
        {
            var installedGpu = record!.Metadata.GetValueOrDefault("UniversalMFG.GpuGeneration") ?? Rtx40MfgStandaloneDeploymentService.GpuGenRtx40;
            var installedProxy = record.Metadata.GetValueOrDefault("UniversalMFG.ProxyName")
                                 ?? record.Metadata.GetValueOrDefault("RTX40MFG.ProxyName")
                                 ?? preferences.ProxyName;
            var installedVersion = record.Version ?? "?";
            panel.Children.Add(new TextBlock
            {
                Text = LF("Instalado: {0} · {1} · {2}. Para trocar GPU/proxy, remova e reinstale; o RHI não troca o owner de um slot DLL durante uma instalação gerenciada.", "Installed: {0} · {1} · {2}. To change GPU/proxy, remove and reinstall; RHI does not change a DLL slot owner during a managed installation.", installedVersion, installedGpu, installedProxy),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.74
            });
        }

        void SaveUniversalMfgPreferences()
        {
            if (installed) return;
            var gpu = gpuCombo.SelectedItem?.ToString() ?? Rtx40MfgStandaloneDeploymentService.GpuGenRtx40;
            var proxy = proxyCombo.SelectedItem?.ToString() ?? Rtx40MfgStandaloneDeploymentService.DefaultProxyName;
            var result = vm.SetRtx40MfgPreferences(gpu, proxy);
            message.Text = UiLocalizationService.Present(result.Message);
        }

        gpuCombo.SelectionChanged += (_, _) => SaveUniversalMfgPreferences();
        proxyCombo.SelectionChanged += (_, _) => SaveUniversalMfgPreferences();

        if (installed)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var check = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.Rtx40MfgCheck") };
            var update = new Button { Classes = { "rhi" }, Content = UiLocalizationService.Text("Ui.Rtx40MfgUpdate"), IsEnabled = false };

            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                try
                {
                    var status = await vm.CheckRtx40MfgUpdateAsync();
                    message.Text = UiLocalizationService.Present(status.Message);
                    update.IsEnabled = status.Success && status.HasUpdate;
                }
                finally { check.IsEnabled = true; }
            };

            update.Click += async (_, _) =>
            {
                update.IsEnabled = false;
                var result = await vm.UpdateRtx40MfgAsync();
                message.Text = UiLocalizationService.Present(result.Message);
            };

            buttons.Children.Add(check);
            buttons.Children.Add(update);
            panel.Children.Add(buttons);
        }

        panel.Children.Add(message);
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Ui.Rtx40MfgCompatibilityNote"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#D7B65A")),
            Opacity = 0.78
        });

        var dialog = new Window
        {
            Title = $"{UiLocalizationService.Text("Ui.Rtx40MfgTitle")} — {vm.SelectedGame.Name}",
            Width = 700,
            Height = 520,
            MinWidth = 560,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await dialog.ShowDialog(this);
    }

    private async void RenoDxSettings_Click(object? sender, RoutedEventArgs e)
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
            Text = L("Configurações de compatibilidade", "Compatibility settings"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = L("As alterações são gravadas imediatamente na seção [renodx] do jogo selecionado. Opções de compatibilidade mantêm os identificadores exigidos pelo componente e recebem nomes amigáveis na interface.", "Changes are written immediately to the selected game [renodx] section. Compatibility options keep the identifiers required by the component and receive friendly names in the UI."),
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
                ? L("Execute o jogo uma vez com o RenoDX instalado para gerar as configurações.", "Run the game once with RenoDX installed to generate the settings.")
                : UiLocalizationService.Present(snapshot.Warning);
            panel.Children.Add(message);
        }
        else if (snapshot.Settings.Count == 0)
        {
            message.Text = L("A seção [renodx] existe, mas nenhuma configuração de compatibilidade está disponível no momento.", "The [renodx] section exists, but no compatibility setting is currently available.");
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
            Title = $"RenoDX — Compatibilidade — {vm.SelectedGame?.Name ?? L("Jogo", "Game")}",
            Width = 660,
            Height = 620,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await dialog.ShowDialog(this);
    }

    private async void OptiScalerSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasOptiScalerSettings) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var snapshot = vm.GetOptiScalerAdvancedSettings();
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = L("Configurações avançadas do OptiScaler", "Advanced OptiScaler settings"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = L("Escolha o canal antes de instalar. Stable é o padrão; Nightly e DLSS NR usam caches de download separados. Depois da instalação, remova o OptiScaler antes de trocar de variante.", "Choose the channel before installing. Stable is the default; Nightly and DLSS NR use separate download caches. After installation, remove OptiScaler before changing variants."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var variantRow = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
        var variantLabel = new TextBlock
        {
            Text = L("Canal / variante", "Channel / variant"),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(variantLabel, L("Stable usa o release oficial estável; Nightly usa OptiScaler-nightly; DLSS NR usa o fork com Renderização Neural e o runtime NR gerenciado pelo RHI.", "Stable uses the official stable release; Nightly uses OptiScaler-nightly; DLSS NR uses the Neural Rendering fork and the RHI-managed NR runtime."));
        var variantCombo = new ComboBox
        {
            ItemsSource = vm.OptiScalerVariantOptions,
            SelectedItem = vm.SelectedOptiScalerVariant,
            MinWidth = 260,
            IsEnabled = !snapshot.ManagedByRhi
        };
        variantCombo.SelectionChanged += (_, _) =>
        {
            if (variantCombo.SelectedItem is string value)
                vm.SelectedOptiScalerVariant = value;
        };
        Grid.SetColumn(variantLabel, 0);
        Grid.SetColumn(variantCombo, 1);
        variantRow.Children.Add(variantLabel);
        variantRow.Children.Add(variantCombo);
        panel.Children.Add(variantRow);
        panel.Children.Add(new TextBlock
        {
            Text = snapshot.ManagedByRhi
                ? L("Variante instalada: o seletor fica bloqueado até a remoção para evitar mistura de arquivos entre canais.", "Installed variant: the selector stays locked until removal to prevent mixing files between channels.")
                : L("A variante selecionada é salva por jogo e será usada no próximo Install.", "The selected variant is saved per game and will be used on the next install."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.58
        });

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Margin = new Avalonia.Thickness(0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "OptiScaler.ini",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = L("Os controles abaixo editam somente o OptiScaler.ini. As alterações são imediatas e atômicas; arquivos de proxy/encadeamento gerenciados pelo RHI não são alterados.", "The controls below edit only OptiScaler.ini. Changes are immediate and atomic; RHI-managed proxy/chaining files are not changed."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
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
                ? L("Nenhuma configuração do OptiScaler gerenciada pelo RHI está disponível para este jogo.", "No RHI-managed OptiScaler settings are available for this game.")
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
                combo.SelectionChanged += async (_, _) =>
                {
                    if (reverting || combo.SelectedIndex < 0 || combo.SelectedIndex >= options.Length) return;
                    var requestedIndex = combo.SelectedIndex;
                    var selected = options[requestedIndex];
                    combo.IsEnabled = false;
                    var result = await vm.SetOptiScalerAdvancedSettingAsync(setting.Id, selected.Value);
                    combo.IsEnabled = true;
                    message.Text = UiLocalizationService.Present(result.Message);
                    if (result.Success)
                    {
                        acceptedIndex = requestedIndex;
                        return;
                    }
                    reverting = true;
                    combo.SelectedIndex = acceptedIndex;
                    reverting = false;
                };

                Grid.SetColumn(label, 0);
                Grid.SetColumn(combo, 1);
                settingRow.Children.Add(label);
                settingRow.Children.Add(combo);
                panel.Children.Add(settingRow);
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
            Title = $"OptiScaler — Avançado — {vm.SelectedGame?.Name ?? L("Jogo", "Game")}",
            Width = 720,
            Height = 760,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        await dialog.ShowDialog(this);
    }

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

    private async void SectionDragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string sectionKey }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(sectionKey));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

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
        "Runtime" => this.FindControl<Grid>("RuntimeSectionBody"),
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
            if (DataContext is MainWindowViewModel vm)
                vm.StatusText = LF("Não foi possível salvar o layout das seções: {0}", "Could not save section layout: {0}", ex.Message);
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
