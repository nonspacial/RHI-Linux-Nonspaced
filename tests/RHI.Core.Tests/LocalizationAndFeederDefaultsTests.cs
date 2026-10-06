using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using System.Windows.Input;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LocalizationAndFeederDefaultsTests
{
    [Fact]
    public void Feeder_DefaultConsumer_IsRenoDxDlss5()
        => Assert.Equal(Dlss5FeederConsumer.RenoDxDlss5, Dlss5FeederFullDeploymentService.DefaultConsumer);

    [Fact]
    public void Localization_Dictionaries_HaveKeyParity()
        => Assert.True(UiLocalizationService.HasKeyParity);

    [Theory]
    [InlineData("pt-BR", "Ui.OptionStable", "Estável")]
    [InlineData("en-US", "Ui.OptionStable", "Stable")]
    [InlineData("pt-BR", "Ui.OptionLatest", "Mais recente")]
    [InlineData("en-US", "Ui.OptionLatest", "Latest")]
    [InlineData("pt-BR", "Ui.OptionNightly", "Desenvolvimento")]
    [InlineData("en-US", "Ui.OptionNightly", "Nightly")]
    [InlineData("pt-BR", "Ui.HdrOff", "Desativado")]
    [InlineData("en-US", "Ui.HdrNativeWayland", "Native Wayland")]
    [InlineData("pt-BR", "Ui.StreamlineUpdate", "Streamline — Atualização")]
    [InlineData("en-US", "Ui.StreamlineUpdate", "Streamline — Update")]
    [InlineData("pt-BR", "Ui.StatusNotDetected", "Não detectado")]
    [InlineData("en-US", "Ui.StatusNotDetected", "Not detected")]
    public void Localization_CommonOptions_AreLanguageSpecific(string language, string key, string expected)
        => Assert.Equal(expected, UiLocalizationService.TextFor(language, key));

    [Theory]
    [InlineData("pt-BR", "RenoDX HDR requires ReShade with full add-on support; RHI will automatically install ReShade before RenoDX.", "RenoDX HDR exige ReShade com suporte completo a add-ons; o RHI instalará ReShade automaticamente antes do RenoDX.")]
    [InlineData("en-US", "RenoDX HDR exige ReShade com suporte completo a add-ons; o RHI instalará ReShade automaticamente antes do RenoDX.", "RenoDX HDR requires ReShade with full add-on support; RHI will automatically install ReShade before RenoDX.")]
    [InlineData("pt-BR", "DirectX 11 upscaler", "Upscaler DirectX 11")]
    [InlineData("en-US", "RGB 10-bit + Alpha — Genérico", "RGB 10-bit + Alpha — Generic")]
    public void Localization_PresentationBoundary_NormalizesPlatformText(string language, string source, string expected)
        => Assert.Equal(expected, UiLocalizationService.PresentFor(language, source));

    [Theory]
    [InlineData("pt-BR", "This legacy platform message is still available but has not been mapped for localization yet.", "O componente retornou uma mensagem técnica ainda não localizada para PT-BR. A operação foi preservada; consulte os detalhes técnicos/logs se necessário.")]
    [InlineData("en-US", "Esta mensagem antiga do componente ainda não possui tradução para a interface selecionada.", "The component returned a technical message that is not yet localized for EN-US. The operation state was preserved; consult technical details/logs if needed.")]
    public void Localization_PresentationBoundary_FailsClosedInsteadOfMixingLanguages(string language, string source, string expected)
        => Assert.Equal(expected, UiLocalizationService.PresentFor(language, source));

    [Theory]
    [InlineData("ShortFuse in-place swap failed; the previous add-on was kept: Managed file was modified outside RHI and will not be replaced: /g/zzz_renodx-dlss.addon64",
        "Falha na troca no local do ShortFuse; o add-on anterior foi mantido: O arquivo gerenciado foi modificado fora do RHI e não será substituído: /g/zzz_renodx-dlss.addon64")]
    [InlineData("DLSS5 Feeder in-place swap failed and the previous files could not be restored: The previous file could not be restored. Its backup is kept at /g/a.addon64.rhi-swap-old; move it back over the original name before retrying. Cause: disk full",
        "Falha na troca no local do DLSS5 Feeder e os arquivos anteriores não puderam ser restaurados: O arquivo anterior não pôde ser restaurado. A cópia de segurança foi mantida em /g/a.addon64.rhi-swap-old; mova-a de volta para o nome original antes de tentar novamente. Causa: disk full")]
    [InlineData("nvngx_dlssnr.dll 309.9 is no longer published; using 310.2. ShortFuse: nvngx_dlssnr.dll swapped in place to 310.2. Nothing else was changed.",
        "nvngx_dlssnr.dll 309.9 não é mais publicado; usando 310.2. ShortFuse: nvngx_dlssnr.dll trocado no local para 310.2. Nada mais foi alterado.")]
    [InlineData("ShortFuse: nvngx_dlssnr.dll swap failed; the previous DLL was kept: Swap source is missing.",
        "ShortFuse: falha na troca do nvngx_dlssnr.dll; a DLL anterior foi mantida: A origem da troca está ausente.")]
    public void Localization_NrSwapMessages_HavePtBrAndEnPairs(string en, string pt)
    {
        Assert.Equal(pt, UiLocalizationService.PresentFor("pt-BR", en));
        Assert.Equal(en, UiLocalizationService.PresentFor("en-US", pt));
    }

    [Fact]
    public void Streamline_ComponentRow_IsInformational_NotInstallable()
    {
        UiLocalizationService.Apply("en-US");
        var component = new ManagedComponent
        {
            Kind = ManagedComponentKind.Streamline,
            DisplayName = "Streamline",
            Availability = ComponentAvailability.Available,
            ActionAllowed = false
        };

        var row = new ComponentRowViewModel(component, new NoOpCommand());

        Assert.False(row.HasAction);
        Assert.Equal(string.Empty, row.Action);
        Assert.Equal("Streamline — Update", row.Name);
        Assert.Equal("Not detected", row.Status);
        Assert.Contains("Native Streamline was not detected", row.Note, StringComparison.Ordinal);
        Assert.Contains("does not create a native integration", row.Note, StringComparison.Ordinal);
        Assert.Contains("OptiScaler Nightly/DLSS NR", row.Note, StringComparison.Ordinal);
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
