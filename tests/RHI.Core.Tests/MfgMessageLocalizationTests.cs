using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #236 / #237 / #238 (lane w12/mfg): the fixed English text of every new update-path message is gone from its PT-BR
/// presentation (the generic English-marker check in <see cref="WaveOneMessageLocalizationTests"/> does not know every
/// word, so a removed pair could pass it), and English stays as written.
/// </summary>
public sealed class MfgMessageLocalizationTests
{
    public static TheoryData<string, string> FixedTexts => new()
    {
        { "Universal MFG download/validation failed: " + Rtx40MfgStandaloneDeploymentService.NoAssetMessage, "download/validation failed" },
        { "Universal MFG download/validation failed: " + Rtx40MfgStandaloneDeploymentService.NoAssetMessage, "has neither an" },
        { "Universal MFG update check/download failed: " + Rtx40MfgStandaloneDeploymentService.SeveralAssetsPrefix + "MFGUnlocker-*.zip", "lists several assets" },
        { "Universal MFG update check/download failed: " + Rtx40MfgStandaloneDeploymentService.SeveralAssetsPrefix + "MFGUnlocker-*.zip", "check/download failed" },
        { "Universal MFG download/validation failed: " + Rtx40MfgStandaloneDeploymentService.SeveralDllsPrefix + "MFGUnlocker.dll", "refused as ambiguous" },
        { "Universal MFG download/validation failed: " + Rtx40MfgStandaloneDeploymentService.NoDllMessage, "holds neither" },
        { ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + "/jogos/exemplo/version.dll", "removed outside RHI" },
        { ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + "/jogos/exemplo/renodx-mfgunlock.addon64", "changed outside RHI" },
        // Correction round 1 (review F5)
        { ManagedUpdateFileGuard.NoRecordedHashPrefix + "/jogos/exemplo/winmm.dll", "has no hash" },
        { ManagedUpdateFileGuard.NoRecordedHashPrefix + "/jogos/exemplo/winmm.dll", "Install it again" },
    };

    [Theory]
    [MemberData(nameof(FixedTexts))]
    public void MfgMessage_LeavesNoEnglishFixedText_InPortuguese_AndStaysAsWrittenInEnglish(string message, string englishFixedText)
    {
        Assert.DoesNotContain(englishFixedText, UiLocalizationService.PresentFor("pt-BR", message), StringComparison.Ordinal);
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }

    // The path (the value) is kept as it is after the translated fixed text.
    [Fact]
    public void RemovedFileLine_KeepsThePathAfterThePortugueseText()
        => Assert.EndsWith("Remover apaga o registro dele no RHI: /jogos/exemplo/version.dll",
            UiLocalizationService.PresentFor("pt-BR", ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + "/jogos/exemplo/version.dll"));

    // Correction round 1: the values stay last, after the Portuguese text.
    [Fact]
    public void CorrectionLines_KeepTheirValuesAfterThePortugueseText()
    {
        Assert.EndsWith("instale-o de novo: /jogos/exemplo/winmm.dll",
            UiLocalizationService.PresentFor("pt-BR", ManagedUpdateFileGuard.NoRecordedHashPrefix + "/jogos/exemplo/winmm.dll"));
    }
}
