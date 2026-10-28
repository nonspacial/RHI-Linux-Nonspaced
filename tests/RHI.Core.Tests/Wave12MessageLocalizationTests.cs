using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12 integration (message sweep): every user-reachable message the wave-12 lanes added in platform code (check results,
/// refusals, row notes) has an EN + PT-BR presentation. English-source messages: the PT-BR presentation is not the generic
/// fallback, carries none of the message's fixed English text, and English stays as is. Portuguese-source messages: the
/// English presentation is not the fallback and carries none of the Portuguese fixed text.
/// </summary>
public sealed class Wave12MessageLocalizationTests
{
    private const string P = "/jogos/Exemplo/dinput8.dll";

    public static TheoryData<string, string> EnglishSourced() => new()
    {
        // mfg (#236–#238)
        { ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + P, "removed outside RHI" },
        { ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + P, "changed outside RHI" },
        { ManagedUpdateFileGuard.NoRecordedHashPrefix + P, "has no hash" },
        { Rtx40MfgStandaloneDeploymentService.SeveralAssetsPrefix + "MFGUnlocker-v1.7.0.zip", "several assets" },
        { Rtx40MfgStandaloneDeploymentService.NoAssetMessage, "neither" },
        { Rtx40MfgStandaloneDeploymentService.SeveralDllsPrefix + "MFGUnlocker.dll", "several entries" },
        { Rtx40MfgStandaloneDeploymentService.NoDllMessage, "neither" },
        { UltimateAsiLoaderDeploymentService.ArchitectureChangedPrefix + "x86 → x64", "no longer has the architecture" },
        { ManagedUpdateFileGuard.MissingProxyNameMessage, "has no proxy DLL name" }, // w12 follow-up (re-review mfg N2)
        { "Ultimate ASI Loader is not RHI-managed.", "is not RHI-managed" },
        { "RTX 20 FG Unlock is not RHI-managed.", "is not RHI-managed" },
        // shaders (#239–#241)
        { ReShadeShaderPackService.ProtectedShaderFilePrefix + "/jogos/Exemplo/reshade-shaders/RHI/Prod80/Shaders/x.fx", "outside RHI" },
        { ReShadeShaderPackService.LinkedShaderFolderPrefix + "/jogos/Exemplo/reshade-shaders/RHI", "is a link" },
        { ReShadeShaderPackService.ProtectedCoreHeaderPrefix + "/jogos/Exemplo/reshade-shaders/RHI/_Core/Shaders/ReShade.fxh", "core header" }, // follow-up N2
        { ReShadeShaderPackService.PackFolderClashPrefix + "/jogos/Exemplo/reshade-shaders/RHI/Pack_One/Shaders/Same.fx", "would place the same file" }, // N3
        // pins (#249)
        { LinuxDlssDefaultsService.BatchDeployFailedPrefix + "E/S negada", "Batch deploy failed" },
        { LinuxDlssDefaultsService.BatchRestoreFailedPrefix + "E/S negada", "Batch restore failed" },
        // w12 follow-up (re-review pins M1): the reasons the DLSS result line names after a failed game
        { LinuxDlssDefaultsService.CouldNotApplyMessage("Super Resolution", "310.4.0"), "Could not apply" },
        { LinuxDlssDefaultsService.CouldNotApplyMessage("Ray Reconstruction", "310.4.0"), "Could not apply" },
        { LinuxDlssDefaultsService.CouldNotApplyMessage("Frame Generation", "310.4.0"), "Could not apply" },
        { LinuxDlssDefaultsService.CouldNotApplyMessage("Neural Rendering", "310.4.0"), "Could not apply" },
        { LinuxDlssDefaultsService.CouldNotApplyMessage("Streamline", "2.9.0"), "Could not apply" },
        { RHI.Core.Services.DlssManagerService.NoMatchingStreamlineMessage, "matched the downloaded set" },
        { RHI.Core.Services.DlssManagerService.NoStreamlineBackupsMessage, "backups were found" },
        // luma (#245)
        { LumaFrameworkDeploymentService.NexusNewerPrefix + "2026-10-01", "A newer Luma file" },
        { LumaFrameworkDeploymentService.NexusCurrentMessage, "No newer Luma file" },
        { LumaFrameworkDeploymentService.NexusOnlyUpdatePrefix + "https://www.nexusmods.com/x/mods/1", "published on Nexus Mods only" },
        { LumaFrameworkDeploymentService.NexusOnlyInstallMessage, "published on Nexus Mods only" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.NoKeyMessage, "with a connected Nexus account" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.FailedPrefix + "E/S negada", "could not be asked" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.KeyRefusedMessage, "refused the saved Nexus key" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.NotAModPageMessage, "is not a Nexus Mods mod page" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.NoDatedFileMessage, "no dated main file" },
        { RHI.Platform.Linux.Network.NexusModUpdateCheck.ModNotShownMessage, "would not show this mod's files" }, // follow-up B25
    };

    public static TheoryData<string, string> PortugueseSourced() => new()
    {
        { ManagedUpdateFileGuard.RemovedOutsideRhiRowNote, "foi removido fora do RHI" },
    };

    // w12 follow-up (re-review pins M1): every component the DLSS batch can name has its pair, so a failed game's reason
    // reaches the PT-BR result line in Portuguese ("Falha em 1 jogo(s) • Jogo: Não foi possível aplicar o …").
    [Fact]
    public void EveryDlssComponent_CouldNotApply_IsPortugueseOnTheResultLine()
    {
        foreach (var component in LinuxDlssDefaultsService.ComponentDisplayNames)
        {
            var pt = UiLocalizationService.PresentFor("pt-BR", LinuxDlssDefaultsService.CouldNotApplyMessage(component, "310.4.0"));
            Assert.Equal("Não foi possível aplicar o " + component + " 310.4.0.", pt);
        }
    }

    [Theory]
    [MemberData(nameof(EnglishSourced))]
    public void EnglishMessage_HasAPortuguesePresentation_WithNoEnglishFixedText(string message, string englishFixedText)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);

        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        Assert.DoesNotContain(englishFixedText, pt, StringComparison.Ordinal);
        var english = UiLocalizationService.EnglishMarkersIn(pt).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {pt}");
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }

    [Theory]
    [MemberData(nameof(PortugueseSourced))]
    public void PortugueseMessage_HasAnEnglishPresentation_WithNoPortugueseFixedText(string message, string portugueseFixedText)
    {
        var en = UiLocalizationService.PresentFor("en-US", message);

        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("en-US"), en);
        Assert.DoesNotContain(portugueseFixedText, en, StringComparison.Ordinal);
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), UiLocalizationService.PresentFor("pt-BR", message));
    }
}
