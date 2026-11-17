using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// #309 (upstream 2.8.9 "The Quick Start guide has been rewritten to match the current app"): the guide names this app's
/// own buttons and sections, in both languages, so a renamed button shows up here instead of in a stale guide.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class QuickStartGuideTests
{
    private static string Guide(string language)
        => string.Join("\n", QuickStartGuide.Steps.SelectMany(step => new[] { step.TitleKey, step.BodyKey, step.TipKey })
            .OfType<string>().Select(key => UiLocalizationService.TextFor(language, key)));

    [Fact]
    public void FollowsUpstreamsOrder_WelcomeAndTheGitHubTokenFirst()
    {
        Assert.Equal("Qs.WelcomeTitle", QuickStartGuide.Steps[0].TitleKey);
        Assert.Contains("GitHub", UiLocalizationService.TextFor("en-US", QuickStartGuide.Steps[0].TipKey!));
        Assert.Equal(["★", "1", "2", "3", "4", "5", "6", "N", "⚙", "X", "H", "+", "↑", "↻"], QuickStartGuide.Steps.Select(s => s.Badge));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void EveryStep_HasItsTextInBothLanguages(string language)
    {
        foreach (var key in QuickStartGuide.Steps.SelectMany(step => new[] { step.TitleKey, step.BodyKey, step.TipKey }).OfType<string>())
        {
            var text = UiLocalizationService.TextFor(language, key);
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.NotEqual(key, text);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void NamesTheAppsOwnButtonsAndSections(string language)
    {
        var guide = Guide(language);
        foreach (var label in new[]
                 {
                     // w14/layouts-a (#303/#304): upstream's sections, top bar and Management names
                     "Ui.LibraryToggle", "Sec.Components", "Sec.GameOverrides", "Sec.NeuralRendering", "Sec.Dlss", "Sec.Driver", "Sec.Management", "Sec.Extras",
                     "Ui.AvailableMods", "Ui.ShadersAddons", "Top.GlobalShaders", "Top.GlobalAddons", "Ui.UpdateAll", "Help.UpdateLog", "Ui.AddGame", "Ui.Refresh",
                     "Mgmt.ChangeFolder", "Removed.Remove", "Lib.Filters",
                     "Ui.Edit", "Ui.OpenFolder", "Ui.OpenPrefix", "Ui.Play", "Ui.RepairMove", "Ui.CopyLaunchProfile", "Ui.NrVersions",
                     "Shell.CopyReport", "Shell.ResToggle", "Nv.SettingsButton", "Shell.CopyLogs", "Nrc.Button"
                 })
        {
            var text = UiLocalizationService.TextFor(language, label);
            Assert.True(guide.Contains(text, StringComparison.OrdinalIgnoreCase), $"{label} ({text}) is not in the {language} guide");
        }
    }

    // ---- w14 final review m8 ----------------------------------------------------------------------------------------

    [Fact]
    public void ThePortugueseGuide_UsesTheLabelsThePortugueseInterfaceShows()
    {
        var pt = (string key) => UiLocalizationService.TextFor("pt-BR", key);
        // The ReShade card's channels and the OptiScaler ⚙'s versions, as their dropdowns read in Portuguese.
        foreach (var label in new[] { pt("Ui.OptionStable"), pt("Ui.OptionNightly"), pt("Ui.OptionLegacy"), pt("Names.OptionNoAddons"), pt("Ui.OptionCustom") })
            Assert.Contains(label, pt("Qs.ReShadeTip"));
        Assert.Contains($"{pt("Ui.OptionStable")}, {pt("Ui.OptionNightly")}, DLSS NR, F5 DLSSNR", pt("Qs.OptiScalerBody"));
        // The DLSS / Streamline section's names.
        foreach (var name in new[] { "Ui.SuperResolution", "Ui.RayReconstruction", "Ui.FrameGeneration", "Ui.NeuralRendering" })
            Assert.Contains(pt(name), pt("Qs.DlssBody"), StringComparison.OrdinalIgnoreCase);
        foreach (var english in new[] { "Stable", "Nightly", "Legacy", "Super Resolution", "Ray Reconstruction", "Frame Generation" })
            Assert.DoesNotContain(english, pt("Qs.ReShadeTip") + pt("Qs.DlssBody") + pt("Qs.OptiScalerBody"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void TheGuideNamesSpotlight_AndGivesASettingsSection_WhereItSaysSettings(string language)
    {
        Assert.Contains("Spotlight", UiLocalizationService.TextFor(language, "Qs.SelectBody"));
        var settings = language == "en-US" ? "Settings" : "Configurações";
        // The Settings page, not a section of the game page (Driver Settings) or a ⚙ (NVIDIA settings…).
        var page = language == "en-US" ? @"(?<!Driver |NVIDIA )\bSettings(?! ›)" : @"\bConfigurações(?! ›)(?! do driver)(?! NVIDIA)";
        foreach (var key in QuickStartGuide.Steps.SelectMany(step => new[] { step.BodyKey, step.TipKey }).OfType<string>())
        {
            var text = UiLocalizationService.TextFor(language, key);
            // Every "Settings" is followed by a section (SettingsPathsTests checks that the path is real); the top bar's ⚙
            // and the Help menu's own words aside.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, page))
                Assert.Fail($"{key} ({language}) says only \"{settings}\": …{text[Math.Max(0, m.Index - 40)..Math.Min(text.Length, m.Index + 40)]}…");
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void HelpTexts_NameWhereThingsAreNow(string language)
    {
        string T(string key) => UiLocalizationService.TextFor(language, key);
        Assert.DoesNotContain(language == "en-US" ? "compact" : "compacta", T("Ui.XdgSettingsNote")); // the view mode row left (#303)
        Assert.DoesNotContain(language == "en-US" ? "ReShade profile" : "perfil do ReShade", T("Ui.D3DCompilerNote"));
        Assert.Contains(language == "en-US" ? "Components" : "Componentes", T("Tip.SectionExtras")); // the FG unlocks are there
        Assert.DoesNotContain("ReShade profile", RHI.Platform.Linux.Deployment.ReShadeDeploymentService.NoAddonsBlocksAddonPrefix);
        Assert.DoesNotContain("ReShade profile", RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ShaderPacksNotInstalledNote);
    }
}
