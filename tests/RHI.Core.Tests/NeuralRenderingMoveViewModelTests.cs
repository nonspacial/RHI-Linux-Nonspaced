using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #230 view-model rows (EN + PT-BR): the Borderlands 3 case through the shell's Update All — an RTX 4090 whose DLSS5
/// Feeder Full holds the 50xx-only NR DLL from before #229, its record without the NR version — is moved and counted as
/// updated; a hand-picked NR version made for another card stays and its row says so.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralRenderingMoveViewModelTests : IDisposable
{
    private const string FeederRelease = "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-move-vm-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingMoveViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    private static HttpResponseMessage? Answer(HttpRequestMessage request)
        => request.RequestUri!.AbsoluteUri == FeederRelease
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"tag_name":"v0.13.1-beta.1","assets":[{"name":"DLSS5-Feeder-0.13.1-beta.1.zip","browser_download_url":"https://github.com/rhi-tests/feeder.zip"}]}]""")
            }
            : NeuralRenderingGpuViewModelTests.Answer(request);

    private (DetectedGame Game, string Nr) Borderlands()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Borderlands 3")).FullName;
        var exe = Path.Combine(dir, "Borderlands3.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var nr = Path.Combine(dir, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(For50));
        return (new DetectedGame { Name = "Borderlands 3", Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir }, nr);
    }

    private static void SeedFeeder(ViewModelFixture fx, DetectedGame game, string nr)
    {
        var listed = fx.Vm.Games.Single(row => row.Name == game.Name).Game;
        NrGpuGames.SeedOwner(fx.Records, listed, ManagedComponentKind.Dlss5Feeder, nr, version: "0.13.1-beta.1"); // no NR version recorded
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task UpdateAll_MovesTheFeedersLatestNrDll_AndCountsItUpdated(string language)
    {
        var (game, nr) = Borderlands();
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer,
            hdrProfiles: Hdr(Facts(RtxGeneration.Rtx40)), nvidiaGpu: () => Facts(RtxGeneration.Rtx40));
        SeedFeeder(fx, game, nr);
        UiLocalizationService.Apply(language);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.StartsWith(language == "pt-BR"
                ? "Atualizar tudo: 1 atualizado(s) · 0 em dia · 0 adiado(s) · 0 com falha"
                : "Update All: 1 updated · 0 current · 0 deferred · 0 failed",
            fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(nr));
        Assert.Equal(ForAll, fx.Records.Find(game, ManagedComponentKind.Dlss5Feeder)!.Metadata[NeuralRenderingDllSwap.MetaDllVersion]);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task AHandPickedNrDllForAnotherCard_StaysAndItsRowSaysSo(string language)
    {
        var (game, nr) = Borderlands();
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer,
            hdrProfiles: Hdr(Facts(RtxGeneration.Rtx40)), nvidiaGpu: () => Facts(RtxGeneration.Rtx40));
        SeedFeeder(fx, game, nr);
        fx.Managed.SetNeuralRenderingDllVersionPin(game, For50);
        UiLocalizationService.Apply(language);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        var component = new ManagedComponent { Kind = ManagedComponentKind.Dlss5Feeder, DisplayName = "DLSS5 Feeder", Availability = ComponentAvailability.Installed };
        fx.Managed.Decorate(game, [component]);
        var row = new ComponentRowViewModel(component, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

        Assert.Equal(Dll(For50), File.ReadAllBytes(nr));
        Assert.EndsWith(language == "pt-BR"
                ? "A DLL do Neural Rendering escolhida para este jogo é feita para outra geração de placa de vídeo; o Atualizar tudo a mantém como escolhida: " + For50
                : "The Neural Rendering DLL picked for this game is made for another graphics card generation; Update All leaves it as picked: " + For50,
            row.Note, StringComparison.Ordinal);
    }
}
