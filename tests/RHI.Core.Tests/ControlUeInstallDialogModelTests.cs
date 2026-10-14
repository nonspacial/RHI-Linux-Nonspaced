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
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
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

    // #136 (upstream 2.8.1): "Using HDR?" is open only with OptiScaler FG = Yes and goes back to No with FG.
    [Fact]
    public void FrameGenerationChoices_HdrOnlyWithFg_AndResetWhenFgGoesBackToNo()
    {
        var model = new ControlUeInstallDialogModel();
        Assert.False(model.InstallOptiScalerFg);
        Assert.False(model.CanChooseHdr);
        model.UseHdr = true;
        Assert.False(model.UseHdr);

        model.InstallOptiScalerFg = true;
        model.UseHdr = true;
        Assert.True(model.CanChooseHdr);
        Assert.True(model.UseHdr);

        model.InstallOptiScalerFg = false;
        Assert.False(model.UseHdr);
        Assert.False(model.CanChooseHdr);
    }

    public static TheoryData<string, string, string, string, string> ChoiceTexts => new()
    {
        { "en-US", "Install OptiScaler FG", "Using HDR?", "No", "Yes" },
        { "pt-BR", "Instalar FG do OptiScaler", "Usando HDR?", "Não", "Sim" },
    };

    [Theory]
    [MemberData(nameof(ChoiceTexts))]
    public void FrameGenerationChoices_AreLocalised_AndSayWhatTheyInstall(string language, string fg, string hdr, string no, string yes)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var model = new ControlUeInstallDialogModel();
            Assert.Equal(fg, model.FrameGenerationLabel);
            Assert.Equal(hdr, model.HdrLabel);
            Assert.Equal([no, yes], model.Choices);
            Assert.Contains("2026-10-04", model.FrameGenerationTip);
            Assert.Contains("winmm.dll", model.FrameGenerationTip);
            Assert.Contains("FgSlHdr10=1", model.HdrTip);
            Assert.Contains(model.InstallAlso, line => line.Contains("winmm.dll") && line.Contains("2026-10-04"));
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    private static async Task<(ViewModelFixture Fx, DetectedGame Game)> ControlFixtureAsync(bool withReShade, Func<HttpRequestMessage, HttpResponseMessage?>? extra = null)
    {
        var addon = new byte[128 * 1024]; // RenoDX refuses a download under 100 KiB as an error page
        addon[0] = (byte)'M'; addon[1] = (byte)'Z';
        var fx = await ViewModelFixture.StartAsync([], httpResponse: request =>
            request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                                                "<tr><td>Control Ultimate Edition</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr></table>")
                }
                : request.RequestUri?.AbsoluteUri == "https://clshortfuse.github.io/renodx/renodx-control-rr.addon64"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(addon) }
                    : extra?.Invoke(request));
        var game = fx.Game(ControlUePostInstallService.GameName);
        PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x8664);
        if (withReShade)
        {
            var bytes = new byte[4096];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            System.Text.Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(bytes, 128);
            File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), bytes);
        }
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        for (var i = 0; i < 500 && (fx.Vm.IsBusy || fx.Vm.IsSelectionProbeBusy || fx.Vm.Session.Analysis is null); i++)
            await Task.Delay(10);
        Assert.NotNull(fx.Vm.Session.Analysis);
        Assert.Contains(fx.Vm.Session.Analysis!.Components, c => c.Kind == ManagedComponentKind.RenoDX && c.ActionAllowed);
        return (fx, game);
    }

    [Fact]
    public async Task InstallingRenoDxOnControl_WithFgChosen_RunsTheFgStepAfterRenoDx_AndKeepsRenoDxWhenItFails()
    {
        var (fx, _) = await ControlFixtureAsync(withReShade: true);
        using var _fx = fx;
        ControlUeInstallDialogModel? shown = null;
        fx.Vm.ConfirmAsync = (_, _, _) => throw new InvalidOperationException("the FG dialog must be used");
        fx.Vm.ConfirmControlUeAsync = model =>
        {
            shown = model;
            model.InstallOptiScalerFg = true;
            model.UseHdr = true;
            return Task.FromResult(true);
        };

        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RenoDX);

        Assert.NotNull(shown);
        Assert.True(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.RenoDX) is not null, fx.Vm.StatusText);
        // The outcome line (the analysis that follows may replace it): RenoDX installed, then the FG step's result. A
        // manual entry without a Wine prefix: OptiScaler refuses, which the line says; RenoDX stays installed.
        Assert.Contains(lines, line => line.StartsWith("RenoDX installed: ", StringComparison.Ordinal)
                                       && line.Contains(" · " + ControlUeFrameGenerationService.NotSetUpPrefix, StringComparison.Ordinal));
        Assert.Null(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task InstallingRenoDxOnControl_WithFgNo_NeverRunsTheFgStep()
    {
        var (fx, _) = await ControlFixtureAsync(withReShade: true);
        using var _fx = fx;
        fx.Vm.ConfirmControlUeAsync = _ => Task.FromResult(true);

        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RenoDX);

        Assert.True(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.RenoDX) is not null, fx.Vm.StatusText);
        Assert.Contains(lines, line => line.StartsWith("RenoDX installed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("frame generation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallingRenoDxOnControl_RenoDxFails_TheFgStepNeverRuns()
    {
        var (fx, _) = await ControlFixtureAsync(withReShade: false); // RenoDX needs ReShade: refused
        using var _fx = fx;
        fx.Vm.ConfirmControlUeAsync = model => { model.InstallOptiScalerFg = true; return Task.FromResult(true); };
        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RenoDX);

        Assert.Null(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.RenoDX));
        Assert.Contains(lines, line => line.StartsWith("Could not automatically install the dependency ReShade", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("frame generation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallingRenoDxOnControl_DeclinedInTheFgDialog_ChangesNothing()
    {
        var (fx, _) = await ControlFixtureAsync(withReShade: true);
        using var _fx = fx;
        fx.Vm.ConfirmControlUeAsync = model => { model.InstallOptiScalerFg = true; return Task.FromResult(false); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RenoDX);

        Assert.Null(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.RenoDX));
        Assert.Null(fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.OptiScaler));
    }
}
