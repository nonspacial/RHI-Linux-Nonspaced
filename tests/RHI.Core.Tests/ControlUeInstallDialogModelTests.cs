using System.Net;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #160 (upstream 2.8.6 <c>ae5603c</c>): before RenoDX is installed on Control Ultimate Edition, the confirmation lists
/// every in-game setting the RR fix needs, in English and Portuguese, and it is the text the shell actually shows.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ControlUeInstallDialogModelTests
{
    // Upstream's list: DLSS ON, SSAO ON, post-processing HIGH, SSR HIGH (turn RT off briefly to change it), RT
    // reflections ON, RT indirect diffuse ON, render resolution in even integers (renderer.ini).
    public static TheoryData<string, string[]> RequiredSettings => new()
    {
        { "en-US", ["DLSS on", "SSAO on", "Post-processing High", "Screen-space reflections High", "turn ray tracing off briefly",
            "RT reflections on", "RT indirect diffuse on", "Render resolution in even numbers", "renderer.ini"] },
        { "pt-BR", ["DLSS ativado", "SSAO ativado", "Pós-processamento em Alto", "Reflexos em espaço de tela em Alto",
            "desative o ray tracing por um momento", "Reflexos RT ativados", "Iluminação indireta difusa RT ativada",
            "Resolução de renderização em números pares", "renderer.ini"] },
    };

    [Theory]
    [MemberData(nameof(RequiredSettings))]
    public void Message_ListsEveryRequiredInGameSetting(string language, string[] settings)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var model = new ControlUeInstallDialogModel();

            Assert.Equal(7, model.RequiredSettings.Count);
            foreach (var setting in settings)
                Assert.Contains(setting, model.Message, StringComparison.Ordinal);
            Assert.Contains(model.RequiredSettingsHeading + "\n• ", model.Message, StringComparison.Ordinal);
            // The earlier parts of the confirmation are still there (not an HDR mod; the two strategies; what the install changes).
            Assert.StartsWith(model.Warning, model.Message, StringComparison.Ordinal);
            Assert.Contains("nvngx_dlssd.dll", model.Message, StringComparison.Ordinal);
            Assert.Contains("m_eHDRPreset", model.Message, StringComparison.Ordinal);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    [Fact]
    public async Task InstallingRenoDxOnControl_AsksWithTheFullSettingsList()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request =>
            request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                                                "<tr><td>Control Ultimate Edition</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr></table>")
                }
                : null);
        var game = fx.Game(ControlUePostInstallService.GameName);
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        for (var i = 0; i < 500 && (fx.Vm.IsBusy || fx.Vm.IsSelectionProbeBusy || fx.Vm.Session.Analysis is null); i++)
            await Task.Delay(10);
        Assert.NotNull(fx.Vm.Session.Analysis);
        Assert.Contains(fx.Vm.Session.Analysis!.Components, c => c.Kind == ManagedComponentKind.RenoDX && c.ActionAllowed);

        string? asked = null;
        fx.Vm.ConfirmAsync = (_, text, _) => { asked = text; return Task.FromResult(false); };
        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RenoDX);

        Assert.Equal(new ControlUeInstallDialogModel().Message, asked);
        Assert.Null(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.RenoDX));
    }
}
