using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review (platform A): the fixed English text of the messages its fixes added is gone from their PT-BR
/// presentation, and English stays as written (same assertion as <c>NightlySwitchLocalizationTests</c>).
/// </summary>
public sealed class ReviewPlatformALocalizationTests
{
    public static TheoryData<string, string> FixedTexts => new()
    {
        // m1: the version switch's pre-flight
        { LinuxManagedComponentService.SwitchNotStartedPrefix + "Could not obtain the official OptiScaler F5 DLSSNR package: " + OptiScalerPackageProvider.F5BackendMissingMessage, "was not switched" },
        { LinuxManagedComponentService.SwitchNotStartedPrefix + NeuralRenderingRuntimeOwnership.RefusalFor("DLSS5 Feeder"), "nothing changed" },
        { LinuxManagedComponentService.SwitchPreviousUnavailablePrefix + OptiScalerDeploymentService.ExactVersionUnknownMessage, "could not be prepared" },
        { LinuxManagedComponentService.SwitchPreviousUnavailablePrefix + OptiScalerDeploymentService.ExactVersionUnknownMessage, "cannot tell which release" },
        // M5: the ⚙ writes refused while the component is not installed
        { ComponentSettingsFile.NotInstalledPrefix + "MFG Unlock", "can only be changed while" },
        // m2: ANR FG across a switch
        { LinuxManagedComponentService.SwitchAnrFgCarriedNote, "was put back" },
        { LinuxManagedComponentService.SwitchAnrFgDroppedPrefix + OptiScalerDeploymentService.AnrFgUnsupportedPrefix + "Custom: B" + OptiScalerDeploymentService.AnrFgUnsupportedHint + LinuxManagedComponentService.SwitchAnrFgClearedNote, "could not be put back" },
        { LinuxManagedComponentService.SwitchAnrFgDroppedPrefix + OptiScalerDeploymentService.AnrFgUnsupportedPrefix + "Custom: B" + OptiScalerDeploymentService.AnrFgUnsupportedHint + LinuxManagedComponentService.SwitchAnrFgClearedNote, "does not look for it" },
    };

    [Theory]
    [MemberData(nameof(FixedTexts))]
    public void ReviewMessage_LeavesNoEnglishFixedText_InPortuguese(string message, string englishFixedText)
    {
        Assert.DoesNotContain(englishFixedText, UiLocalizationService.PresentFor("pt-BR", message), StringComparison.Ordinal);
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }
}
