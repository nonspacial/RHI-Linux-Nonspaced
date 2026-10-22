using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #232 (w11/nightly-switch): the fixed English text of the in-place Nightly switch's messages, and of the update
/// transaction's refusals a switch now shows in the ⚙ dialog, is gone from their PT-BR presentation (the generic English
/// marker check does not know every word; same assertion as <c>WaveOneMessageLocalizationTests.IntegrationMessage_…</c>,
/// kept in its own file so the wave's lanes do not edit one list).
/// </summary>
public sealed class NightlySwitchLocalizationTests
{
    private const string P = "/jogos/exemplo/OptiScaler/libxess.dll";

    public static TheoryData<string, string> FixedTexts => new()
    {
        { OptiScalerDeploymentService.NightlySwitchedPrefix + "20261012", "switched in place" },
        { OptiScalerDeploymentService.NightlySwitchedToLatestPrefix + "20261013", "to Latest" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + OptiScalerDeploymentService.NightlyBuildUnavailablePrefix + "HTTP 404", "was not switched" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + OptiScalerDeploymentService.NightlyBuildUnavailablePrefix + "HTTP 404", "could not be downloaded" },
        { OptiScalerDeploymentService.NightlySwitchChoiceNotResetPrefix + "Access to the path '/jogos/exemplo/cfg/pins.json' is denied.", "could not be put back" },
        { OptiScalerDeploymentService.NightlySwitchNotNightlyMessage, "can change its build" },
        { OptiScalerDeploymentService.NightlySwitchSameBuildPrefix + "20261004", "already on the chosen build" },
        { OptiScalerDeploymentService.NightlySwitchControlUePrefix + "20261004", "frame generation" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + "Managed OptiScaler file was modified outside RHI and will not be updated: " + P, "will not be updated" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + "OptiScaler managed file is missing or has no ownership hash: " + P, "ownership hash" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + "OptiScaler update package layout is invalid: " + P, "layout is invalid" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + "OptiScaler update rolled back: New OptiScaler package file collides with an external/untracked file: " + P, "update rolled back" },
        { OptiScalerDeploymentService.NightlySwitchFailedPrefix + "OptiScaler update rolled back: New OptiScaler package file collides with an external/untracked file: " + P, "collides with" },
    };

    [Theory]
    [MemberData(nameof(FixedTexts))]
    public void NightlySwitchMessage_LeavesNoEnglishFixedText_InPortuguese(string message, string englishFixedText)
    {
        Assert.DoesNotContain(englishFixedText, UiLocalizationService.PresentFor("pt-BR", message), StringComparison.Ordinal);
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }
}
