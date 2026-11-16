using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One FPS Limit entry: <see cref="Value"/> null = Global, 0 = Off, else a cap; <see cref="AsksForValue"/> = "Custom…".</summary>
public sealed record FpsChoice(string Label, uint? Value, bool AsksForValue = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// #310 (upstream 2.8.9 Driver Settings › FPS Limit, per game): Global (follows Settings), Off, upstream's VRR presets, the
/// game's own custom value, and Custom… (20–1000). On Linux the cap is handed to DXVK and VKD3D-Proton at launch
/// (<see cref="FrameRateLimit"/>). Shown in the DLSS section beside NVIDIA settings… for every Wine/Proton game whose
/// launcher takes launch variables. The game's choice is saved in the library file (<see cref="LinuxGameLibraryState.FpsLimits"/>).
/// UI thread only.
/// </summary>
public sealed partial class FrameRateLimitPresenter : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private DetectedGame? _game;
    private bool _loading;

    internal FrameRateLimitPresenter(MainWindowViewModel shell)
    {
        _shell = shell;
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame)) Load();
        };
        Load();
    }

    [ObservableProperty] private IReadOnlyList<FpsChoice> choices = [];
    [ObservableProperty] private FpsChoice? selected;
    /// <summary>"Custom…" was picked: the value box shows.</summary>
    [ObservableProperty] private bool isCustomEntry;
    [ObservableProperty] private string customText = "";
    [ObservableProperty] private string tip = "";

    /// <summary>The row applies to the selected game (a Wine/Proton game whose launcher takes launch variables).</summary>
    public bool IsShown => _game is not null && _shell.Runtime.SelectedHdrPlan.FrameRate.Shown;

    /// <summary>
    /// The list for <paramref name="perGame"/> (upstream <c>BuildFpsItems</c>): Global (with the global value), Off, the
    /// presets, a custom per-game value just above Custom…, then Custom….
    /// </summary>
    public static IReadOnlyList<FpsChoice> ChoicesFor(uint global, uint? perGame)
    {
        var list = new List<FpsChoice>
        {
            new(FrameRateLimit.Valid(global) is { } g ? UiLocalizationService.Format("Fps.GlobalValue", g) : UiLocalizationService.Text("Fps.GlobalOff"), null),
            new(UiLocalizationService.Text("Fps.Off"), FrameRateLimit.Off)
        };
        list.AddRange(FrameRateLimit.Presets.Select(p => new FpsChoice(UiLocalizationService.Format("Fps.Preset", p.Fps, p.RefreshHz), p.Fps)));
        if (FrameRateLimit.Valid(perGame) is { } own && FrameRateLimit.Presets.All(p => p.Fps != own))
            list.Add(new FpsChoice(UiLocalizationService.Format("Fps.CustomValue", own), own));
        list.Add(new FpsChoice(UiLocalizationService.Text("Fps.Custom"), null, AsksForValue: true));
        return list;
    }

    /// <summary>The game's saved choice: null = Global, 0 = Off, else its cap.</summary>
    internal uint? Saved(DetectedGame game)
        => _shell.LibraryStateForUiLogic.FpsLimits?.TryGetValue(LinuxGameLibraryStore.GameKey(game), out var fps) == true ? fps : null;

    /// <summary>Shows the selected game's choice (selection, a save, Settings' global limit changed).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            _game = _shell.SelectedGame;
            var perGame = _game is null ? null : Saved(_game);
            Choices = ChoicesFor(_shell.GlobalFpsLimit, perGame);
            Selected = Choices.First(c => !c.AsksForValue && c.Value == perGame);
            IsCustomEntry = false;
            CustomText = "";
            Tip = TipFor(_game);
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(IsShown));
    }

    partial void OnSelectedChanged(FpsChoice? value)
    {
        if (_loading || value is null || _game is null) return;
        if (value.AsksForValue)
        {
            IsCustomEntry = true;
            return;
        }
        IsCustomEntry = false;
        Save(_game, value.Value);
    }

    /// <summary>The Custom… box: 20–1000, else the choice goes back and the status line says why (upstream drops it).</summary>
    [RelayCommand]
    private void ApplyCustom()
    {
        if (_game is null) return;
        if (!uint.TryParse(CustomText?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fps)
            || FrameRateLimit.Valid(fps) is null)
        {
            _shell.StatusText = UiLocalizationService.Format("Fps.CustomInvalid", FrameRateLimit.Min, FrameRateLimit.Max);
            Load();
            return;
        }
        Save(_game, fps);
    }

    /// <summary>Saves a game's choice (null = Global) and rebuilds its launch plan.</summary>
    internal bool Save(DetectedGame game, uint? fps)
    {
        var library = (ILibraryEditHost)_shell;
        if (library.LibraryRepairRunning)
        {
            _shell.StatusText = UiLocalizationService.Text("Vm.WaitForRepairAfterMove");
            Load();
            return false;
        }
        var state = library.WorkingLibraryCopy();
        var map = state.FpsLimits ?? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var key = LinuxGameLibraryStore.GameKey(game);
        if (fps is { } value) map[key] = value;
        else map.Remove(key);
        state.FpsLimits = map.Count == 0 ? null : map;
        if (library.SaveLibraryCopy(state) is { } refused)
        {
            _shell.StatusText = refused;
            Load();
            return false;
        }
        library.AdoptLibraryCopy(state);
        _shell.Runtime.RaiseHdrPlanChanged();
        Load();
        _shell.StatusText = fps switch
        {
            null => UiLocalizationService.Format("Fps.SavedGlobal", game.DisplayName()),
            FrameRateLimit.Off => UiLocalizationService.Format("Fps.SavedOff", game.DisplayName()),
            _ => UiLocalizationService.Format("Fps.Saved", game.DisplayName(), fps)
        };
        return true;
    }

    private string TipFor(DetectedGame? game)
    {
        var tip = UiLocalizationService.Text("Fps.Tip");
        return game is not null && _shell.FrameLimiterInstalled(game) ? tip + "\n\n" + UiLocalizationService.Text("Fps.TipFrameLimiter") : tip;
    }
}

/// <summary>#310: the status and launcher lines when only the FPS limit is applied at launch (HDR Off, no NVIDIA settings).</summary>
public static class FrameRateLimitText
{
    public static string Launching(DetectedGame game, uint? limit)
        => UiLocalizationService.Format("Fps.Launching", game.Name, limit ?? 0);

    /// <summary>The launcher that starts the game, and where the variables go.</summary>
    public static string ApplyInLauncher(RHI.Platform.Linux.Hdr.LinuxHdrLaunchPlan plan, DetectedGame game)
    {
        var entry = string.IsNullOrWhiteSpace(game.ParentLauncherName) ? game.Name : XboxCardText.AppName(game.ParentLauncherName)!;
        var variables = string.Join("; ", plan.LaunchVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return plan.Handoff switch
        {
            RHI.Platform.Linux.Launch.LaunchHandoff.Steam => UiLocalizationService.Format("Fps.ApplyInSteam", plan.LaunchOptions),
            RHI.Platform.Linux.Launch.LaunchHandoff.Heroic => UiLocalizationService.Format("Fps.ApplyInHeroic", entry, variables),
            RHI.Platform.Linux.Launch.LaunchHandoff.Lutris => UiLocalizationService.Format("Fps.ApplyInLutris", entry, variables),
            _ => UiLocalizationService.Format("Fps.ApplyInOtherLauncher", variables)
        };
    }
}
