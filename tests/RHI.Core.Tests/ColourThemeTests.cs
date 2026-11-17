using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.Theming;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #301 (upstream 2.9.3 "Colour themes"): the Linux side — every colour is a named Avalonia resource a theme replaces
/// (so the phase-2 layouts pick them up), Settings › Colour theme applies and saves at once, Custom starts from the theme
/// that was showing and warns on low contrast, Share / Import use upstream's <c>RHI-THEME:</c> codes.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ColourThemeTests : IDisposable
{
    // A code upstream's own tests pin (ThemeShareCodeTests at RHI-2.9.4): what an upstream user shares.
    private const string UpstreamCode = "RHI-THEME:180213-23141B-9BE1F2-C5ACE6-D2ACDD-3CCB85-B898E8-D4A856-E07070";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-theme-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- resources ---------------------------------------------------------------------------------------------

    [Fact]
    public void Default_PutsEveryBrushInItsDefaultColour()
    {
        var resources = new ResourceDictionary();
        ThemeResources.Apply(resources, ThemePalette.Default);
        foreach (var (key, hex) in ThemeResources.Defaults)
            Assert.Equal(Color.Parse(hex), Assert.IsType<SolidColorBrush>(resources[key]).Color);
    }

    [Fact]
    public void ATheme_ReplacesTheBrushes_WithItsVersion()
    {
        var resources = new ResourceDictionary();
        ThemeResources.Apply(resources, ThemePresets.Find("OledBlack")!.Palette);
        Assert.Equal(Color.Parse("#000000"), Brush(resources, "RhiBackgroundBrush"));
        Assert.Same(ThemePresets.Find("OledBlack")!.Palette, ThemeResources.Current);

        ThemeResources.Apply(resources, ThemePresets.Find("ColourBlind")!.Palette);
        var installed = Brush(resources, "RhiInstalledBrush");
        Assert.True(installed.B > installed.G, $"installed green should turn blue, got {installed}");
        ThemeResources.Apply(resources, ThemePalette.Default);
        Assert.Equal(Color.Parse("#5ECB7D"), Brush(resources, "RhiInstalledBrush"));
    }

    [Fact]
    public void EveryResourceTheViewsUse_IsInTheCatalogue_AndNoViewHoldsALiteralColour()
    {
        var desktop = Path.Combine(RepoRoot(), "src", "RHI.Desktop");
        var keys = ThemeResources.Defaults.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var colourProperties = new[] { "Foreground", "Background", "BorderBrush", "Fill", "Stroke", "SelectionBrush" };
        foreach (var file in Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(file);
            foreach (var attribute in xml.Descendants().Attributes())
            {
                var value = attribute.Value.Trim();
                var property = attribute.Name.LocalName == "Value"
                    ? attribute.Parent?.Attribute("Property")?.Value ?? ""
                    : attribute.Name.LocalName;
                if (!colourProperties.Contains(property)) continue;
                Assert.False(value.StartsWith('#') || (value.Length > 0 && char.IsLetter(value[0]) && value != "Transparent"),
                    $"{Path.GetFileName(file)}: {property}=\"{value}\" — use a theme resource ({{DynamicResource Rhi…Brush}})");
            }
        }
        var used = Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Rhi[A-Za-z]+Brush").Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(used.Except(keys));
    }

    /// <summary>
    /// w14/layouts-a: code-built views follow the theme too. No <c>Color.Parse</c> / <c>Color.From…</c>, no named
    /// <c>Colors.X</c> / <c>Brushes.X</c> (Transparent excepted) and no hex colour literal anywhere in RHI.Desktop's C#,
    /// except the theme palette definitions themselves.
    /// </summary>
    [Fact]
    public void NoCodeBuiltView_HoldsALiteralColour()
    {
        var desktop = Path.Combine(RepoRoot(), "src", "RHI.Desktop");
        var allowed = new[] { Path.Combine("Theming", "ThemeResources.cs") };
        var literal = new Regex(@"""#[0-9A-Fa-f]{3,8}""|\bColor\.(Parse|TryParse|FromRgb|FromArgb|FromUInt32|FromHsl|FromHsv)\s*\(|\bColors\.[A-Z]\w*|\bBrushes\.(?!Transparent\b)[A-Z]\w*");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(desktop, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                allowed.Contains(relative)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i];
                var comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0 && !code[..comment].Contains('"')) code = code[..comment];
                if (literal.Match(code) is { Success: true } m) found.Add($"{relative}:{i + 1}: {m.Value}");
            }
        }
        Assert.True(found.Count == 0, "literal colours in code-built views (use ThemeResources.Bind or an App.axaml class):\n" + string.Join("\n", found));
    }

    /// <summary>w14/layouts-a (#301 owner decision): each Custom colour has a real colour picker showing the colour.</summary>
    [Fact]
    public void CustomColours_EachHaveAColourPicker_ShowingTheirColour()
    {
        var (theme, _, _) = NewPresenter(new LinuxAppSettings { ColourTheme = "Forest" });
        theme.SelectedThemeIndex = theme.ThemeNames.Count - 1; // Custom
        var rows = new RHI.Desktop.Views.ThemeSettingsRows(theme, _ => Task.FromResult(false));
        var pickers = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(rows.Control).OfType<ColorPicker>().ToList();
        Assert.Equal(theme.CustomColours.Count, pickers.Count);
        var accent = pickers.Single(p => p.Name == "ThemeColourPicker-Accent");
        Assert.Equal("#7FD6C2", RHI.Desktop.Views.ThemeSettingsRows.PickedHex(accent.Color));
        Assert.False(accent.IsAlphaEnabled);

        theme.SetColour("Accent", "#FF9A4D");
        Assert.Equal("#FF9A4D", RHI.Desktop.Views.ThemeSettingsRows.PickedHex(accent.Color));
        Assert.Equal("#0A0B0C", RHI.Desktop.Views.ThemeSettingsRows.PickedHex(Color.FromArgb(0x80, 0x0A, 0x0B, 0x0C)));
    }

    /// <summary>
    /// w14/layouts-a: the favourite amber has its own key and maps like upstream's FavLook brushes: it stays #F0C35A in
    /// every theme whose Warning colour is the default amber, and follows Warning where a theme changes it.
    /// </summary>
    [Fact]
    public void FavouriteAmber_StaysAmber_ExceptWhereTheThemeChangesWarning()
    {
        var defaultWarning = ThemePalette.Default.Warning;
        foreach (var preset in ThemePresets.All)
        {
            var resources = new ResourceDictionary();
            ThemeResources.Apply(resources, preset.Palette);
            var favourite = Brush(resources, "RhiFavouriteBrush");
            if (preset.Palette.Warning == defaultWarning)
                Assert.True(Close(favourite, Color.Parse("#F0C35A")), $"{preset.Id}: favourite should stay amber, got {favourite}");
            else
                Assert.False(Close(favourite, Color.Parse("#F0C35A")), $"{preset.Id}: favourite should follow the theme's Warning");
        }
        ThemeResources.Apply(new ResourceDictionary(), ThemePalette.Default);

        static bool Close(Color a, Color b) => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
    }

    // ---- Settings › Colour theme --------------------------------------------------------------------------------

    private (ThemeSettingsPresenter Presenter, LinuxSettingsStore Store, List<ThemePalette> Applied) NewPresenter(LinuxAppSettings? start = null)
    {
        var store = new LinuxSettingsStore(Path.Combine(_root, "config", "settings.json"), Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        var settings = start ?? new LinuxAppSettings();
        store.Save(settings);
        var applied = new List<ThemePalette>();
        string? Save(Action<LinuxAppSettings> change)
        {
            var copy = store.Load();
            change(copy);
            store.Save(copy);
            settings.ColourTheme = copy.ColourTheme;
            settings.CustomThemeColours = copy.CustomThemeColours;
            return null;
        }
        return (new ThemeSettingsPresenter(() => settings, Save, applied.Add), store, applied);
    }

    [Fact]
    public void PickingATheme_AppliesItLive_AndSavesIt()
    {
        var (theme, store, applied) = NewPresenter();
        Assert.Equal(["Default", "OLED Black", "Midnight Blue", "Ember", "Forest", "Kiro", "Slate", "High Contrast", "Colour-blind Friendly", "Custom"],
            theme.ThemeNames);
        Assert.Equal(0, theme.SelectedThemeIndex);
        Assert.False(theme.IsCustom);

        theme.SelectedThemeIndex = 3;

        Assert.Equal("Ember", store.Load().ColourTheme);
        Assert.Equal(ThemePresets.Find("Ember")!.Palette, Assert.Single(applied));
        Assert.False(theme.IsCustom);
    }

    [Fact]
    public void Custom_FirstTime_StartsFromTheThemeShowing_AndKeepsItsColoursAfterwards()
    {
        var (theme, store, applied) = NewPresenter(new LinuxAppSettings { ColourTheme = "Forest" });
        theme.SelectedThemeIndex = theme.ThemeNames.Count - 1; // Custom

        Assert.True(theme.IsCustom);
        Assert.Equal("Custom", store.Load().ColourTheme);
        Assert.Equal(ThemePresets.Find("Forest")!.Palette.ToCustomString(), store.Load().CustomThemeColours);
        Assert.Equal("#7FD6C2", theme.CustomColours.Single(r => r.Key == "Accent").Hex);

        Assert.True(theme.SetColour("Accent", "ff9a4d"));
        theme.SelectedThemeIndex = 0;
        theme.SelectedThemeIndex = theme.ThemeNames.Count - 1; // back to Custom: the colours picked, not Default's
        Assert.Equal("#FF9A4D", theme.CustomColours.Single(r => r.Key == "Accent").Hex);
        Assert.Equal(ThemePalette.Hex("#FF9A4D"), applied[^1].Accent);
    }

    [Fact]
    public void ATypedColourThatIsNotAColour_IsRefused_AndNothingChanges()
    {
        var (theme, store, _) = NewPresenter(new LinuxAppSettings { ColourTheme = "Custom", CustomThemeColours = ThemePalette.Default.ToCustomString() });
        var before = store.Load().CustomThemeColours;
        Assert.False(theme.SetColour("Text", "#12345"));
        Assert.Equal("\"#12345\" isn't a colour. Type six hex digits, like #4DC9E6.", theme.Status);
        Assert.False(theme.SetColour("Text", "#GGGGGG"));
        Assert.Equal(before, store.Load().CustomThemeColours);
        Assert.Equal("#E8ECF2", theme.CustomColours.Single(r => r.Key == "Text").Hex);
    }

    [Fact]
    public void LowContrastText_Warns()
    {
        var (theme, _, _) = NewPresenter(new LinuxAppSettings { ColourTheme = "Custom", CustomThemeColours = ThemePalette.Default.ToCustomString() });
        Assert.False(theme.ContrastWarning);
        Assert.True(theme.SetColour("Text", "#2A2E35")); // dark grey text on the dark background
        Assert.True(theme.ContrastWarning);
        Assert.True(theme.ResetCustom());
        Assert.False(theme.ContrastWarning);
        Assert.False(ThemeSettingsPresenter.IsLowContrast(ThemePresets.Find("HighContrast")!.Palette));
    }

    [Fact]
    public void ShareAndImport_UseUpstreamsCodes()
    {
        UiLocalizationService.Apply("en-US"); // w14 final review m11: its messages are English whatever ran before
        var (theme, store, applied) = NewPresenter();
        var (imported, message) = theme.Import("Try my theme! " + UpstreamCode + " enjoy");
        Assert.True(imported, message);
        Assert.Equal("Theme imported and applied.", message);
        Assert.Equal("Custom", store.Load().ColourTheme);
        Assert.True(theme.IsCustom);
        Assert.Equal(UpstreamCode, theme.ShareCode); // our code for the same colours is upstream's
        Assert.Equal(ThemePalette.Hex("#180213"), applied[^1].Background);

        var (refused, why) = theme.Import("RHI-THEME:180213-23141B");
        Assert.False(refused);
        Assert.Equal("That isn't a theme code. Copy the whole code, from RHI-THEME: to the last colour.", why);
        Assert.Equal(UpstreamCode, theme.ShareCode);
    }

    [Fact]
    public void StartFrom_ReplacesCustomColoursWithAPresets()
    {
        var (theme, store, _) = NewPresenter(new LinuxAppSettings { ColourTheme = "Custom", CustomThemeColours = ThemePalette.Default.ToCustomString() });
        Assert.True(theme.StartFrom(theme.PresetNames.ToList().IndexOf("Kiro")));
        Assert.Equal(ThemePresets.Find("Kiro")!.Palette.ToCustomString(), store.Load().CustomThemeColours);
    }

    [Theory]
    [InlineData("Violet", "Kiro")] // upstream renamed it
    [InlineData("ember", "Ember")]
    [InlineData("custom", "Custom")]
    [InlineData("Neon", "Default")]
    [InlineData(null, "Default")]
    public void SavedThemeId_IsNormalised(string? saved, string expected)
        => Assert.Equal(expected, new LinuxAppSettings { ColourTheme = saved! }.Normalize().ColourTheme);

    [Fact]
    public async Task Shell_ResetSettings_GoesBackToTheDefaultTheme()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.Vm.Theme.SelectedThemeIndex = 1; // OLED Black
        Assert.Equal("OledBlack", fx.Settings.Load().ColourTheme);
        Assert.Equal(ThemePresets.Find("OledBlack")!.Palette, ThemeResources.Current);

        fx.Vm.Settings.ResetLinuxSettings();

        Assert.Equal(ThemePalette.Default, ThemeResources.Current);
        Assert.Equal(0, fx.Vm.Theme.SelectedThemeIndex);
    }

    [Fact]
    public void PortugueseUi_NamesTheThemes()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Preto OLED", ThemeSettingsPresenter.Label("OledBlack"));
            Assert.Equal("Amigável para daltônicos", ThemeSettingsPresenter.Label("ColourBlind"));
            Assert.Equal("Compartilhar tema", UiLocalizationService.Text("Theme.Share"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static Color Brush(ResourceDictionary resources, string key) => Assert.IsType<SolidColorBrush>(resources[key]).Color;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "RHI-Linux.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
