using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>w4/launchers review nits 6: wording of the unknown-instance refusal, and ~/Games kept on one line.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LauncherReviewWordingTests
{
    [Fact]
    public void UnknownInstanceRefusal_ReadsNaturally_InBothLanguages()
    {
        var refusal = XboxWriteGate.UnknownInstancePrefix + "/games/xbox/9ABC";

        Assert.StartsWith("This game was installed by a copy of the Xbox app RHI does not know, so it cannot tell", refusal, StringComparison.Ordinal);
        Assert.StartsWith("Este jogo foi instalado por uma cópia do app do Xbox que o RHI não conhece, então",
            UiLocalizationService.PresentFor("pt-BR", refusal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void XboxFoldersTab_KeepsTheGamesPathOnOneLine(string language)
    {
        // The PT-BR intro wrapped "~/ Games" across two lines at 640 px: a line may break after "/". A word joiner after
        // the slash removes that break opportunity; the text still reads "~/Games".
        UiLocalizationService.Apply(language);
        try
        {
            foreach (var text in new[] { XboxFoldersPresenter.Intro, XboxFoldersPresenter.ToolTip })
            {
                Assert.Contains("~/\u2060Games", text, StringComparison.Ordinal);
                Assert.DoesNotContain("~/Games", text, StringComparison.Ordinal);
                Assert.Equal(text.Replace("\u2060", "", StringComparison.Ordinal).Length + 1, text.Length);
            }
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
