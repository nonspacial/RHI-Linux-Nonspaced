using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;
using RHI.Desktop.Theming;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>w14/ui-logic: the shell's part of the colour themes (#301) and the FPS limit (#310).</summary>
public partial class MainWindowViewModel
{
    private ThemeSettingsPresenter? _theme;
    private FrameRateLimitPresenter? _frameRateLimit;

    /// <summary>#310: the selected game's FPS Limit row (DLSS section).</summary>
    public FrameRateLimitPresenter FrameRateLimit => _frameRateLimit ??= new FrameRateLimitPresenter(this);

    internal LinuxGameLibraryState LibraryStateForUiLogic => _libraryState;

    /// <summary>#310: Settings' FPS limit (0 = Off).</summary>
    internal uint GlobalFpsLimit => _settings.GlobalFpsLimit;

    /// <summary>#310: ReLimiter or Display Commander is installed in the game by RHI (upstream's rule turns the cap off there).</summary>
    internal bool FrameLimiterInstalled(DetectedGame game)
        => _deploymentRecords.FindAll(game).Any(record => record.Component is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander);

    /// <summary>#310: the FPS cap a launch of <paramref name="game"/> gets (null = none).</summary>
    internal uint? FrameRateLimitFor(DetectedGame game)
        => RHI.Platform.Linux.Hdr.FrameRateLimit.Effective(FrameRateLimit.Saved(game), _settings.GlobalFpsLimit, FrameLimiterInstalled(game));

    /// <summary>#301: Settings › Colour theme (applied live and saved at once, as upstream).</summary>
    public ThemeSettingsPresenter Theme => _theme ??= new ThemeSettingsPresenter(() => _settings, SaveThemeSettings, ThemeResources.ApplyToApp);

    /// <summary>
    /// Saves only the theme fields: the file's settings are read, changed and written; the ones in use take the theme
    /// once it is on disk. The localised refusal (nothing changed), or null.
    /// </summary>
    private string? SaveThemeSettings(Action<LinuxAppSettings> change)
    {
        var settings = _settingsStore.Load();
        change(settings);
        var refused = TryPersist(() => _settingsStore.Save(settings), "Ui.WhatSettings", sessionOnly: false, reportStatus: false);
        if (refused is not null) return refused;
        settings.Normalize();
        _settings.ColourTheme = settings.ColourTheme;
        _settings.CustomThemeColours = settings.CustomThemeColours;
        return null;
    }

    /// <summary>Saved or reset settings: their theme is applied when it differs from the one showing.</summary>
    private void AdoptTheme()
    {
        var palette = ThemePresets.Resolve(_settings.ColourTheme, _settings.CustomThemeColours);
        if (palette != ThemeResources.Current) ThemeResources.ApplyToApp(palette);
        _theme?.Load();
        if (_frameRateLimit is not null)
        {
            _frameRateLimit.Load(); // #310: Global shows the new global limit
            Runtime.RaiseHdrPlanChanged();
        }
    }
}
