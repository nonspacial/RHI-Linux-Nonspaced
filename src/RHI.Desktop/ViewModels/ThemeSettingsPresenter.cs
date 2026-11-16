using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One of Custom's nine colours (#301): its label and the colour as #RRGGBB.</summary>
public sealed partial class ThemeColourRow(string key, string label, bool isStatus) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>A status colour (installed, update available, warning, error): shown in the second column, as upstream.</summary>
    public bool IsStatus { get; } = isStatus;
    [ObservableProperty] private string hex = "";
}

/// <summary>
/// #301 (upstream 2.9.3 <c>SettingsHandler.Theme</c> / <c>ThemeShare</c>): Settings › Colour theme. A built-in theme or
/// Custom, applied live (no restart) and saved at once, as upstream; Custom edits nine colours (the first time it starts
/// from the theme that was showing), warns when text would be hard to read (contrast under 4.5:1 against the background
/// or the panels), starts from a built-in theme, resets to the default colours, and shares or imports an
/// <c>RHI-THEME:</c> code (upstream's format). UI thread only.
/// </summary>
public sealed partial class ThemeSettingsPresenter : ObservableObject
{
    /// <summary>WCAG AA for normal text: below it the contrast warning shows (upstream's threshold).</summary>
    public const double MinimumContrast = 4.5;

    private readonly Func<LinuxAppSettings> _settings;
    private readonly Func<Action<LinuxAppSettings>, string?> _save;
    private readonly Action<ThemePalette> _apply;
    private bool _syncing;

    /// <param name="settings">The settings in use (read for the saved theme).</param>
    /// <param name="save">Changes and saves the settings in use; the localised refusal, or null when saved.</param>
    /// <param name="apply">Applies a palette to the running app.</param>
    public ThemeSettingsPresenter(Func<LinuxAppSettings> settings, Func<Action<LinuxAppSettings>, string?> save, Action<ThemePalette> apply)
    {
        _settings = settings;
        _save = save;
        _apply = apply;
        foreach (var (key, _) in ThemePalette.CustomKeys)
            CustomColours.Add(new ThemeColourRow(key, UiLocalizationService.Text(ColourLabelKey(key)),
                key is "Installed" or "Update" or "Warning" or "Error"));
        Load();
    }

    /// <summary>The theme list: the built-in themes in upstream's order, then Custom.</summary>
    public IReadOnlyList<string> ThemeNames { get; } = ThemePresets.All.Select(p => Label(p.Id)).Append(Label(ThemePresets.CustomId)).ToList();

    /// <summary>The built-in themes Custom can start from.</summary>
    public IReadOnlyList<string> PresetNames { get; } = ThemePresets.All.Select(p => Label(p.Id)).ToList();

    public ObservableCollection<ThemeColourRow> CustomColours { get; } = [];

    [ObservableProperty] private int selectedThemeIndex;
    [ObservableProperty] private bool isCustom;
    [ObservableProperty] private bool contrastWarning;
    [ObservableProperty] private string status = "";

    /// <summary>The saved theme id.</summary>
    public string ThemeId => _settings().ColourTheme;

    /// <summary>The saved Custom colours (Settings' Save writes them back as they are).</summary>
    public string? SavedCustomColours => _settings().CustomThemeColours;

    /// <summary>The colours Custom holds (saved ones over the default look).</summary>
    public ThemePalette CustomPalette => ThemePalette.ParseCustom(_settings().CustomThemeColours, ThemePalette.Default);

    /// <summary>The theme's localised name (upstream's names in English).</summary>
    public static string Label(string id) => UiLocalizationService.Text(id switch
    {
        "OledBlack" => "Theme.OledBlack",
        "MidnightBlue" => "Theme.MidnightBlue",
        "Ember" => "Theme.Ember",
        "Forest" => "Theme.Forest",
        "Kiro" => "Theme.Kiro",
        "Slate" => "Theme.Slate",
        "HighContrast" => "Theme.HighContrast",
        "ColourBlind" => "Theme.ColourBlind",
        ThemePresets.CustomId => "Win.Custom",
        _ => "Theme.Default"
    });

    private static string ColourLabelKey(string key) => key switch
    {
        "Background" => "Theme.ColourBackground",
        "Panel" => "Theme.ColourPanel",
        "Text" => "Theme.ColourText",
        "Accent" => "Theme.ColourAccent",
        "Buttons" => "Theme.ColourButtons",
        "Installed" => "Theme.ColourInstalled",
        "Update" => "Theme.ColourUpdate",
        "Warning" => "Theme.ColourWarning",
        _ => "Theme.ColourError"
    };

    /// <summary>Shows the saved choice (opening Settings, after Reset).</summary>
    public void Load()
    {
        _syncing = true;
        try
        {
            var id = ThemeId;
            var index = id == ThemePresets.CustomId ? ThemePresets.All.Count : IndexOf(id);
            SelectedThemeIndex = index < 0 ? 0 : index;
            IsCustom = id == ThemePresets.CustomId;
            ShowCustom(CustomPalette);
        }
        finally { _syncing = false; }
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_syncing || value < 0) return;
        var custom = value >= ThemePresets.All.Count;
        var id = custom ? ThemePresets.CustomId : ThemePresets.All[value].Id;
        var previous = ThemeId;
        if (id == previous) return;
        var refused = _save(s =>
        {
            // First time on Custom: start from the theme that was showing (upstream).
            if (custom && string.IsNullOrWhiteSpace(s.CustomThemeColours))
                s.CustomThemeColours = ThemePresets.Resolve(previous, null).ToCustomString();
            s.ColourTheme = id;
        });
        if (refused is not null)
        {
            Status = refused;
            Load();
            return;
        }
        IsCustom = custom;
        ShowCustom(CustomPalette);
        Status = "";
        ApplySaved();
    }

    /// <summary>A typed custom colour (#RRGGBB, the # optional). False with a message when it is not a colour.</summary>
    public bool SetColour(string key, string? text)
    {
        var typed = text?.Trim() ?? "";
        if (typed.Length > 0 && typed[0] != '#') typed = "#" + typed;
        if (typed.Length != 7 || !ThemePalette.TryHex(typed, out var colour))
        {
            Status = UiLocalizationService.Format("Theme.NotAColour", text ?? "");
            ShowCustom(CustomPalette);
            return false;
        }
        return SaveCustom(CustomPalette.With(key, colour));
    }

    /// <summary>"Start from a theme…": replaces Custom's colours with a built-in theme's.</summary>
    public bool StartFrom(int presetIndex)
        => presetIndex >= 0 && presetIndex < ThemePresets.All.Count && SaveCustom(ThemePresets.All[presetIndex].Palette);

    /// <summary>"Reset to default": Custom's colours back to RHI's default colours.</summary>
    public bool ResetCustom() => SaveCustom(ThemePalette.Default);

    /// <summary>Share theme: the code for Custom's colours (upstream's <c>RHI-THEME:</c> format).</summary>
    public string ShareCode => CustomPalette.ToShareCode();

    /// <summary>Import theme: a pasted code (text around it is fine) becomes Custom's colours and the theme.</summary>
    public (bool Imported, string Message) Import(string? pasted)
    {
        if (!ThemePalette.TryParseShareCode(pasted, out var palette))
            return (false, UiLocalizationService.Text("Theme.ImportNotACode"));
        if (!SaveCustom(palette)) return (false, Status);
        Status = UiLocalizationService.Text("Theme.Imported");
        return (true, Status);
    }

    private bool SaveCustom(ThemePalette palette)
    {
        var refused = _save(s =>
        {
            s.CustomThemeColours = palette.ToCustomString();
            s.ColourTheme = ThemePresets.CustomId;
        });
        if (refused is not null)
        {
            Status = refused;
            ShowCustom(CustomPalette);
            return false;
        }
        Load();
        Status = "";
        ApplySaved();
        return true;
    }

    private void ApplySaved()
    {
        var settings = _settings();
        _apply(ThemePresets.Resolve(settings.ColourTheme, settings.CustomThemeColours));
    }

    private void ShowCustom(ThemePalette palette)
    {
        foreach (var row in CustomColours) row.Hex = ThemePalette.ToHex(palette.Get(row.Key));
        ContrastWarning = IsLowContrast(palette);
    }

    /// <summary>Upstream's warning: text under 4.5:1 against the background or the panels.</summary>
    public static bool IsLowContrast(ThemePalette palette)
        => ThemeColorMapper.Contrast(palette.Text, palette.Background) < MinimumContrast
           || ThemeColorMapper.Contrast(palette.Text, palette.Panel) < MinimumContrast;

    private static int IndexOf(string id)
    {
        for (var i = 0; i < ThemePresets.All.Count; i++)
            if (string.Equals(ThemePresets.All[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
