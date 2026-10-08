using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (ui-build finding 1): the DLSS manifest notice presents the platform reason in the UI language
/// (<c>MainWindowViewModel</c> put <c>ex.Message</c> into <c>Vm.DLSSManifestUnavailable</c> as is).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class StatusLineLocalizationTests
{
    [Fact]
    public async Task DlssManifestTimeout_InPortuguese_ShowsThePortugueseReason()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            // HttpClient reports its own timeout as a TaskCanceledException; with no cached copy the manager says so.
            using var fx = await ViewModelFixture.StartAsync([], "pt-BR", httpFailure: request =>
                request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl
                    ? new TaskCanceledException("A task was canceled.")
                    : new HttpRequestException("offline test"));

            var status = await WaitForStatusAsync(fx, "Manifesto de DLSS indisponível");

            Assert.Contains("Manifesto de DLSS indisponível: O manifesto de DLSS não respondeu e não há cópia em cache", status, StringComparison.Ordinal);
            Assert.DoesNotContain("did not answer", status, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static async Task<string> WaitForStatusAsync(ViewModelFixture fx, string fragment)
    {
        for (var i = 0; i < 200 && !fx.Vm.StatusText.Contains(fragment, StringComparison.Ordinal); i++)
            await Task.Delay(25);
        return fx.Vm.StatusText;
    }
}
