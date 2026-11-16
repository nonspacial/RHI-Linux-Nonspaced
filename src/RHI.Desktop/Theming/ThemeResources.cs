using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RHI.Core.Services;

namespace RHI.Desktop.Theming;

/// <summary>
/// #301 (upstream 2.9.3 <c>ThemeService</c>): every colour RHI draws with is a named brush resource in the application's
/// resources, listed here with its default-look colour. Views use them through <c>{DynamicResource …}</c> (AXAML) or
/// <see cref="Bind"/> (code), so a theme change (<see cref="Apply"/>) replaces each brush with the theme's version
/// (<see cref="ThemeColorMapper"/>) and every window repaints at once, no restart. New views and the phase-2 layouts take
/// their colours from these keys too. UI thread only.
/// </summary>
public static class ThemeResources
{
    /// <summary>Resource key → the colour in RHI's default look. The single place the default colours are written.</summary>
    public static readonly IReadOnlyList<(string Key, string Hex)> Defaults =
    [
        // Surfaces
        ("RhiBackgroundBrush", "#121518"),
        ("RhiSurfaceBrush", "#141820"),
        ("RhiSidebarBrush", "#101419"),
        ("RhiToolbarBrush", "#0F1318"),
        ("RhiStatusBarBrush", "#0D1015"),
        ("RhiFilterBarBrush", "#131820"),
        ("RhiRowBrush", "#101820"),
        ("RhiPanelInsetBrush", "#0D141C"),
        ("RhiSelectedTabBrush", "#14212A"),
        ("RhiGameRowBrush", "#151C28"),
        ("RhiCardBrush", "#161C26"), // w14/ui-settings (#296/#297): Settings' and the ⚙ windows' cards
        // Lines
        ("RhiBorderBrush", "#1E2630"),
        ("RhiCardBorderBrush", "#1E2430"),
        ("RhiDividerBrush", "#2A3038"),
        ("RhiPanelInsetBorderBrush", "#223247"),
        ("RhiGameRowBorderBrush", "#1E2C40"),
        ("RhiExtrasBorderBrush", "#3A3348"),
        // Text
        ("RhiTextBrush", "#E8ECF2"),
        ("RhiSecondaryTextBrush", "#A0AABB"),
        ("RhiMutedTextBrush", "#6B7A8E"),
        ("RhiFaintTextBrush", "#404858"),
        ("RhiGameRowTextBrush", "#E2E8FF"),
        // Accent (teal) and buttons (blue)
        ("RhiAccentBrush", "#4DC9E6"),
        ("RhiAccentBorderBrush", "#1E4858"),
        ("RhiAccentStrongBrush", "#2A6378"),
        ("RhiAccentTintBrush", "#122830"),
        ("RhiTaglineBrush", "#2A7A90"),
        ("RhiBadgeBrush", "#102331"),
        ("RhiControlBrush", "#151A22"),
        ("RhiControlHoverBrush", "#1A2430"),
        ("RhiControlPressedBrush", "#102331"),
        ("RhiButtonBrush", "#182840"),
        ("RhiButtonBorderBrush", "#2A4468"),
        ("RhiButtonTextBrush", "#7AACDD"),
        ("RhiLinkBrush", "#6FB7E8"),
        ("RhiHeadingBrush", "#87CEFA"),
        // Chips (filter tabs, collections, custom filters)
        ("RhiChipBrush", "#1A2030"),
        ("RhiChipTextBrush", "#8898B0"),
        ("RhiChipHoverBrush", "#202A3A"),
        ("RhiChipHoverTextBrush", "#B8C9E0"),
        ("RhiChipActiveBrush", "#2B4A78"),
        ("RhiChipActiveTextBrush", "#FFFFFF"),
        ("RhiChipSaveTextBrush", "#8FB8F0"),
        // Disabled
        ("RhiDisabledBackgroundBrush", "#17191D"),
        ("RhiDisabledTextBrush", "#626A75"),
        ("RhiDisabledBorderBrush", "#292D34"),
        // Status colours
        ("RhiInstalledBrush", "#5ECB7D"),
        ("RhiInstalledTintBrush", "#122818"),
        ("RhiInstalledBorderBrush", "#1E4028"),
        ("RhiUpdateBrush", "#B898E8"),
        ("RhiWarningBrush", "#D4A856"),
        ("RhiWarningTextBrush", "#D7B65A"),
        // w14/layouts-a: the favourite star's amber (upstream 2.9.4 FavLook). Mapped like upstream's: it follows the theme's
        // Warning colour, so it stays #F0C35A in every theme whose Warning is the default amber.
        ("RhiFavouriteBrush", "#F0C35A"),
        ("RhiFavouriteTintBrush", "#221C10"),
        ("RhiFavouriteBorderBrush", "#4F4123"),
        // w14/layouts-a: the swatch outline in Settings › Colour theme (was Brushes.Gray).
        ("RhiSwatchBorderBrush", "#808080"),
        ("RhiErrorTextBrush", "#F08A8A"),
        ("RhiAlertBrush", "#FF4500"),
        ("RhiRemoveBackgroundBrush", "#281818"),
        ("RhiRemoveForegroundBrush", "#E07A7A"),
        ("RhiRemoveBorderBrush", "#563030"),
    ];

    /// <summary>The palette applied last (Default until a theme is applied).</summary>
    public static ThemePalette Current { get; private set; } = ThemePalette.Default;

    /// <summary>Raised after a theme is applied (for colours that are not brushes).</summary>
    public static event Action? Changed;

    /// <summary>Puts every brush, in <paramref name="palette"/>'s version, into <paramref name="resources"/>.</summary>
    public static void Apply(IResourceDictionary resources, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(palette);
        var mapper = new ThemeColorMapper(palette);
        foreach (var (key, hex) in Defaults)
            resources[key] = new SolidColorBrush(ToAvalonia(mapper.Map(ThemePalette.Hex(hex))));
        Current = palette;
        Changed?.Invoke();
    }

    /// <summary>Applies <paramref name="palette"/> to the running app's resources (nothing without an app, e.g. in tests).</summary>
    public static void ApplyToApp(ThemePalette palette)
    {
        if (Application.Current?.Resources is { } resources) Apply(resources, palette);
        else Current = palette;
    }

    /// <summary>
    /// Binds a code-built control's brush property to a theme resource, so it follows theme changes like
    /// <c>{DynamicResource key}</c> in AXAML.
    /// </summary>
    public static T Bind<T>(T control, AvaloniaProperty property, string key) where T : StyledElement
    {
        control[!property] = new DynamicResourceExtension(key);
        return control;
    }

    /// <summary>A style setter whose value follows a theme resource.</summary>
    public static Avalonia.Styling.Setter Setter(AvaloniaProperty property, string key)
        => new(property, new DynamicResourceExtension(key));

    public static Color ToAvalonia(ThemeColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
