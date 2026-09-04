using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly GameLibraryScanner _scanner;
    private readonly IGameLauncher _launcher;
    private readonly DlssManagerService _dlss;
    private readonly List<DetectedGame> _allGames = [];
    private DlssDetectionResult _dlssDetection = new();

    public ObservableCollection<DetectedGame> Games { get; } = [];
    public ObservableCollection<ComponentRowViewModel> Components { get; } = [];
    public ObservableCollection<string> DlssSrVersions { get; } = [];
    public ObservableCollection<string> DlssRrVersions { get; } = [];
    public ObservableCollection<string> DlssFgVersions { get; } = [];
    public ObservableCollection<string> DlssNrVersions { get; } = [];

    [ObservableProperty] private DetectedGame? selectedGame;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusText = "Starting RHI Linux…";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? selectedDlssSrVersion;
    [ObservableProperty] private string? selectedDlssRrVersion;
    [ObservableProperty] private string? selectedDlssFgVersion;
    [ObservableProperty] private string? selectedDlssNrVersion;

    public string GameCountText => $"{Games.Count} games";
    public string SelectedTitle => SelectedGame?.Name ?? "Select a game";
    public string SelectedSource => SelectedGame is null ? "" : $"{SelectedGame.Source}  •  {SelectedGame.Runner ?? "Runner unknown"}";
    public string SelectedInstallPath => SelectedGame?.InstallPath ?? "";
    public string SelectedPrefix => SelectedGame?.PrefixPath ?? "No Wine/Proton prefix detected";
    public string SelectedExecutable => SelectedGame?.ExecutablePath ?? "Executable not detected yet";
    public string SelectedApi => SelectedGame?.GraphicsApi.ToString() ?? "Unknown";
    public bool HasSelectedGame => SelectedGame is not null;
    public bool CanLaunch => SelectedGame is not null && _launcher.CanLaunch(SelectedGame);

    public bool DlssSrDetected => _dlssDetection.DlssPaths.Count > 0;
    public bool DlssRrDetected => _dlssDetection.DlssdPaths.Count > 0;
    public bool DlssFgDetected => _dlssDetection.DlssgPaths.Count > 0;
    public bool DlssNrDetected => _dlssDetection.DlssnrPaths.Count > 0;
    public string DlssSrPath => FirstOrNotFound(_dlssDetection.DlssPaths, "nvngx_dlss.dll");
    public string DlssRrPath => FirstOrNotFound(_dlssDetection.DlssdPaths, "nvngx_dlssd.dll");
    public string DlssFgPath => FirstOrNotFound(_dlssDetection.DlssgPaths, "nvngx_dlssg.dll");
    public string DlssNrPath => FirstOrNotFound(_dlssDetection.DlssnrPaths, "nvngx_dlssnr.dll");
    public string DlssStatusSummary => _dlssDetection.HasAny
        ? $"Found SR:{_dlssDetection.DlssPaths.Count}  RR:{_dlssDetection.DlssdPaths.Count}  FG:{_dlssDetection.DlssgPaths.Count}  NR:{_dlssDetection.DlssnrPaths.Count}  Streamline:{_dlssDetection.StreamlineFolders.Count}"
        : "No game-local DLSS/Streamline DLLs detected";

    public MainWindowViewModel(GameLibraryScanner scanner, IGameLauncher launcher, DlssManagerService dlss)
    {
        _scanner = scanner;
        _launcher = launcher;
        _dlss = dlss;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Loading the upstream DLSS manifest…";
            var manifest = await _dlss.FetchManifestAsync();
            FillVersions(DlssSrVersions, manifest.Dlss.Select(x => x.Version));
            FillVersions(DlssRrVersions, manifest.Dlssd.Select(x => x.Version));
            FillVersions(DlssFgVersions, manifest.Dlssg.Select(x => x.Version));
            FillVersions(DlssNrVersions, manifest.Dlssnr.Select(x => x.Version));
            SelectedDlssSrVersion = DlssSrVersions.FirstOrDefault();
            SelectedDlssRrVersion = DlssRrVersions.FirstOrDefault();
            SelectedDlssFgVersion = DlssFgVersions.FirstOrDefault();
            SelectedDlssNrVersion = DlssNrVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"DLSS manifest unavailable: {ex.Message}";
        }
        finally { IsBusy = false; }

        await RefreshAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedGameChanged(DetectedGame? value)
    {
        Components.Clear();
        _dlssDetection = new();
        if (value is not null)
        {
            foreach (var item in ComponentProbe.Probe(value)) Components.Add(new ComponentRowViewModel(item));
            _dlssDetection = _dlss.Detect(value.InstallPath);
        }
        NotifySelected();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Scanning Steam, Heroic, Lutris and Wine prefixes…";
        try
        {
            var currentKey = SelectedGame is null ? null : Key(SelectedGame);
            var found = await _scanner.ScanAsync();
            _allGames.Clear();
            _allGames.AddRange(found);
            ApplyFilter();
            SelectedGame = currentKey is null ? Games.FirstOrDefault() : Games.FirstOrDefault(g => Key(g) == currentKey) ?? Games.FirstOrDefault();
            StatusText = $"Scan complete — {_allGames.Count} games detected";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedGame is null) return;
        await _launcher.LaunchAsync(SelectedGame);
        StatusText = $"Launching {SelectedGame.Name}";
    }

    [RelayCommand]
    private async Task SwapDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out var version, out var paths)) return;
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusText = "Choose a DLSS version first.";
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = $"{component}: no existing target DLL found; nothing was changed.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = $"Downloading/applying {component} {version}…";
            var result = await _dlss.SwapAsync(kind, paths, version);
            StatusText = result.Success
                ? $"{component} {version} applied to {result.Results.Count} file(s); original preserved as .original."
                : $"{component} swap failed: {result.Error}";
            ReprobeSelectedGame();
        }
        catch (Exception ex)
        {
            StatusText = $"{component} swap failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out _, out var paths)) return;
        if (paths.Count == 0)
        {
            StatusText = $"{component}: no existing target DLL found.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _dlss.RestoreAsync(kind, paths);
            StatusText = result.Success
                ? $"{component}: original DLL restored."
                : $"{component}: restore unavailable — {result.Error}";
            ReprobeSelectedGame();
        }
        catch (Exception ex)
        {
            StatusText = $"{component}: restore failed — {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (SelectedGame is null) return;
        Process.Start(new ProcessStartInfo("xdg-open", SelectedGame.InstallPath) { UseShellExecute = false });
    }

    [RelayCommand]
    private void OpenPrefix()
    {
        if (SelectedGame?.PrefixPath is not { } prefix || !Directory.Exists(prefix)) return;
        Process.Start(new ProcessStartInfo("xdg-open", prefix) { UseShellExecute = false });
    }

    private bool TryResolveComponent(string? component, out DlssComponentKind kind, out string? version, out IReadOnlyList<string> paths)
    {
        switch (component?.ToUpperInvariant())
        {
            case "SR":
                kind = DlssComponentKind.SuperResolution; version = SelectedDlssSrVersion; paths = _dlssDetection.DlssPaths; return true;
            case "RR":
                kind = DlssComponentKind.RayReconstruction; version = SelectedDlssRrVersion; paths = _dlssDetection.DlssdPaths; return true;
            case "FG":
                kind = DlssComponentKind.FrameGeneration; version = SelectedDlssFgVersion; paths = _dlssDetection.DlssgPaths; return true;
            case "NR":
                kind = DlssComponentKind.NeuralRendering; version = SelectedDlssNrVersion; paths = _dlssDetection.DlssnrPaths; return true;
            default:
                kind = default; version = null; paths = []; return false;
        }
    }

    private void ReprobeSelectedGame()
    {
        if (SelectedGame is null) return;
        _dlssDetection = _dlss.Detect(SelectedGame.InstallPath);
        Components.Clear();
        foreach (var item in ComponentProbe.Probe(SelectedGame)) Components.Add(new ComponentRowViewModel(item));
        NotifyDlss();
    }

    private void ApplyFilter()
    {
        var selected = SelectedGame;
        Games.Clear();
        IEnumerable<DetectedGame> query = _allGames;
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(g => g.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                                     g.Source.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        foreach (var game in query) Games.Add(game);
        OnPropertyChanged(nameof(GameCountText));
        if (selected is not null && Games.Contains(selected)) SelectedGame = selected;
        else if (SelectedGame is null) SelectedGame = Games.FirstOrDefault();
    }

    private void NotifySelected()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedInstallPath));
        OnPropertyChanged(nameof(SelectedPrefix));
        OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(SelectedApi));
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(CanLaunch));
        LaunchCommand.NotifyCanExecuteChanged();
        NotifyDlss();
    }

    private void NotifyDlss()
    {
        OnPropertyChanged(nameof(DlssSrDetected));
        OnPropertyChanged(nameof(DlssRrDetected));
        OnPropertyChanged(nameof(DlssFgDetected));
        OnPropertyChanged(nameof(DlssNrDetected));
        OnPropertyChanged(nameof(DlssSrPath));
        OnPropertyChanged(nameof(DlssRrPath));
        OnPropertyChanged(nameof(DlssFgPath));
        OnPropertyChanged(nameof(DlssNrPath));
        OnPropertyChanged(nameof(DlssStatusSummary));
    }

    private static void FillVersions(ObservableCollection<string> target, IEnumerable<string> versions)
    {
        target.Clear();
        foreach (var version in versions) target.Add(version);
    }

    private static string FirstOrNotFound(IReadOnlyList<string> paths, string dll)
        => paths.FirstOrDefault() ?? $"{dll} not found";

    private static string Key(DetectedGame g) => g.SteamAppId is int id ? $"steam:{id}" : g.InstallPath;
}
