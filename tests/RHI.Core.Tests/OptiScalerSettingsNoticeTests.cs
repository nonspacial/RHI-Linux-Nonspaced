using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #166: the OptiScaler ⚙ dialog showed "OptiScaler is not RHI-managed for this game…" twice (above and in place of the settings).
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerSettingsNoticeTests
{
    private const string NotManaged = "OptiScaler is not RHI-managed for this game. External OptiScaler.ini files are protected from modification.";

    private static readonly OptiScalerAdvancedSetting Setting =
        new("ForceReflex", "Reflex", "Force Reflex", "auto", [new OptiScalerAdvancedOption("auto", "Auto")]);

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void NotManaged_TheWarningIsShownOnce_InPlaceOfTheSettings(string language)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var (warning, message) = OptiScalerSettingsNotice.For(new OptiScalerAdvancedSnapshot(null, false, [], NotManaged));

            Assert.Null(warning);
            Assert.Equal(UiLocalizationService.Present(NotManaged), message);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void ManagedWithoutSettings_TheWarningIsTheOneLine()
    {
        UiLocalizationService.Apply("en-US");
        const string missing = "The RHI-managed OptiScaler record exists, but OptiScaler.ini is missing.";

        var (warning, message) = OptiScalerSettingsNotice.For(new OptiScalerAdvancedSnapshot("/x/OptiScaler.ini", true, [], missing));

        Assert.Null(warning);
        Assert.Equal(UiLocalizationService.Present(missing), message);
    }

    [Fact]
    public void NoWarningNoSettings_SaysThereAreNone()
    {
        UiLocalizationService.Apply("en-US");

        var (warning, message) = OptiScalerSettingsNotice.For(new OptiScalerAdvancedSnapshot(null, false, []));

        Assert.Null(warning);
        Assert.Equal(UiLocalizationService.Text("Win.NoRHIManagedOptiScalerSettingsAvailable"), message);
    }

    [Fact]
    public void ManagedWithSettings_TheWarningSitsAboveThem()
    {
        UiLocalizationService.Apply("en-US");

        var (warning, message) = OptiScalerSettingsNotice.For(new OptiScalerAdvancedSnapshot("/x/OptiScaler.ini", true, [Setting], "Note"));

        Assert.Equal(UiLocalizationService.Present("Note"), warning);
        Assert.Null(message);
    }
}
