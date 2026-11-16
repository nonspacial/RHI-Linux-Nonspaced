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
                     "Ui.LibraryToggle", "Ui.ExecutionGame", "Ui.DlssStreamline", "Ui.ReShadeProfile", "Ui.ManagedComponents", "Ui.Extras",
                     "Ui.AvailableMods", "Ui.ShadersAddons", "Ui.BatchDlss", "Ui.UpdateAll", "Ui.UpdateHistory", "Ui.AddGame", "Ui.Refresh",
                     "Ui.Edit", "Ui.OpenFolder", "Ui.OpenPrefix", "Ui.Play", "Ui.RepairMove", "Ui.CopyLaunchProfile", "Ui.NrVersions",
                     "Shell.CopyReport", "Shell.ResToggle", "Nv.SettingsButton", "Shell.CopyLogs", "Nrc.Button"
                 })
        {
            var text = UiLocalizationService.TextFor(language, label);
            Assert.True(guide.Contains(text, StringComparison.OrdinalIgnoreCase), $"{label} ({text}) is not in the {language} guide");
        }
    }
}
