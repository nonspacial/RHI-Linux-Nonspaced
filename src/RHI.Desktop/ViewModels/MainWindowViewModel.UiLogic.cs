using RHI.Core.Services;
using RHI.Desktop.Theming;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>w14/ui-logic: the shell's part of the colour themes (#301).</summary>
public partial class MainWindowViewModel
{
    private ThemeSettingsPresenter? _theme;

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
    }
}
