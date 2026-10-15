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

        var eligible = DlssBatchDialogViewModel.Eligible(candidates);
        if (eligible.Count == 0)
        {
            await ShowSimpleMessageAsync(T("Win.DLSSBatch"), T("Win.NoEligibleGameLocalDLSSStreamline"));
            return;
        }

        // The layout is the view model's data template (Views/Dialogs/DlssBatchDialogView.axaml; epic #89).
        var model = DlssBatchDialogViewModel.For(vm.Dlss, vm.Operations, eligible);
        var dialog = new Window
        {
            Title = model.WindowTitle,
            // Preferred size, capped to the main window; the minimum fits the 640x480 window floor (the body stacks
            // below 700 px and scrolls, the footer stays pinned).
            Width = 820,
            Height = 650,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
        dialog.Closing += (_, args) => args.Cancel |= model.IsRunning; // the window's own close button waits for the batch too
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
        // The layout is the view model's data template (Views/Dialogs/SettingsDialogView.axaml; epic #89); the rows and
        // tabs other lanes added are hosted by that view.
        var model = new SettingsDialogViewModel(vm,
            folder => TryOpenFolder(folder, out var error) ? null : ErrorLineText.FolderNotOpened(error),
            PickFolderAsync, CopyTextToClipboardAsync);
        model.Applied += () =>
        {
            ApplyViewMode();
            SyncAddonWatcherFromSettings();
            SyncTrayIcon();
        };
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 760,
            Height = 680,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
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
            snapshot = await vm.ReShadeProfile.GetShaderAddonManagerSnapshotAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = TF("Win.CouldNotLoadShadersAddOns", UiLocalizationService.Present(ex.Message));
            return;
        }

        if (snapshot is null) return;

        // The layout is the view model's data template (Views/Dialogs/ShaderAddonsDialogView.axaml; epic #89).
        var model = ShaderAddonsDialogViewModel.For(vm, snapshot,
            folder => (TryOpenFolder(folder, out var error), error),
            pack => ShowShaderPackFilesDialogAsync(vm, pack));
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 820,
            Height = 720,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };

        await ShowFittedDialogAsync(dialog);
    });

    private void BrowseD3DCompiler47_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var path = await PickDllAsync(T("Win.PickD3DCompiler47"));
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeProfile.ReShadeD3DCompiler47Path = path;
    });

    private void BrowseReShadeCustomDll_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var path = await PickDllAsync(T("Win.PickReShadeCustomDll"));
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
            vm.ReShadeProfile.ReShadeCustomDllPath = path;
    });

    private void ClearD3DCompiler47_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ReShadeProfile.ReShadeD3DCompiler47Path = string.Empty;
    }

    private void ClearReShadeCustomDll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ReShadeProfile.ReShadeCustomDllPath = string.Empty;
    }

    private async Task ShowShaderPackFilesDialogAsync(MainWindowViewModel vm, ReShadeShaderPackInfo pack)
    {
        var files = await vm.ReShadeProfile.GetShaderPackFilesAsync(pack.Id);
        // The layout is the view model's data template (Views/Dialogs/ShaderPackFilesDialogView.axaml; epic #89).
        var model = new ShaderPackFilesDialogViewModel(pack, files, vm.ReShadeProfile.SetShaderPackExcludedFiles);
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 720,
            Height = 680,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
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
        // The layout is the view model's data template (Views/Dialogs/RenoDxSettingsDialogView.axaml; epic #89).
        var model = RenoDxSettingsDialogViewModel.For(vm, snapshot);
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 660,
            Height = 620,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = model }
        };
        await ShowFittedDialogAsync(dialog);
    });

    private void OptiScalerSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasOptiScalerSettings) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // The layout is the view model's data template (Views/Dialogs/OptiScalerSettingsDialogView.axaml; epic #89).
        var model = new OptiScalerSettingsDialogViewModel(vm, vm.GetOptiScalerAdvancedSettings());
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 720,
            Height = 760,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = model }
        };
        // #187: after a preset is applied the dialog reopens, so every dropdown shows the applied values.
        model.ReopenRequested += () => { dialog.Close(); OptiScalerSettings_Click(sender, e); };
        dialog.Closed += (_, _) => model.Dispose();
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
