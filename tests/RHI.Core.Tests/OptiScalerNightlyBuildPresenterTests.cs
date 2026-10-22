using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#156: the OptiScaler ⚙ \"Nightly build\" row over the real offline view model.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerNightlyBuildPresenterTests
{
    private static HttpResponseMessage? NightlyList(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsoluteUri != OptiScalerPackageProvider.NightlyReleasesApi) return null;
        var releases = new[] { "nightly-20261013", "nightly-20261012", "nightly-20261004" }
            .Select(tag => new { tag_name = tag, draft = false, prerelease = true, assets = Array.Empty<object>() });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json") };
    }

    private static async Task<(ViewModelFixture Fx, DetectedGame Game)> StartAsync(Func<HttpRequestMessage, HttpResponseMessage?>? http = null, string lang = "en-US")
    {
        var fx = await ViewModelFixture.StartAsync([], lang, httpResponse: http);
        var game = fx.Game("Control");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new() { Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows" });
        Assert.True(added, message);
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || fx.Vm.SelectedGame is null); i++) await Task.Delay(25);
        return (fx, fx.Vm.SelectedGame!);
    }

    private static void ChooseNightly(ViewModelFixture fx)
        => fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.Nightly);

    // Merge review finding 1 (seam 5c5353c): the row follows the Runtime panel's variant choice by itself (it listens to
    // vm.Runtime), with no explicit refresh.
    [Fact]
    public async Task Row_FollowsTheRuntimePanelsVariantChoice_WithoutAnExplicitRefresh()
    {
        var (fx, _) = await StartAsync(NightlyList);
        using var _ = fx;
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;
        Assert.False(row.IsVisible);

        ChooseNightly(fx);
        for (var i = 0; i < 200 && !row.IsVisible; i++) await Task.Delay(10);
        Assert.True(row.IsVisible);

        fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.Stable);
        for (var i = 0; i < 200 && row.IsVisible; i++) await Task.Delay(10);
        Assert.False(row.IsVisible);
    }

    [Fact]
    public async Task Row_ShowsForNightlyOnly_ListsTheBuilds_AndSavesThePinPerGame()
    {
        var (fx, game) = await StartAsync(NightlyList);
        using var _ = fx;
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;
        await row.RefreshAsync(fetchList: true);
        Assert.False(row.IsVisible);

        ChooseNightly(fx);
        await row.RefreshAsync(fetchList: true);

        Assert.True(row.IsVisible);
        Assert.True(row.IsEnabled);
        Assert.Equal(["Latest (auto-update)", "2026-10-13", "2026-10-12", "2026-10-04"], row.Options);
        Assert.Equal("Latest (auto-update)", row.Selected);

        row.Selected = "2026-10-04";
        Assert.Equal("20261004", fx.Managed.GetOptiScalerNightlyBuild(game));
        Assert.Contains("pinned to build 2026-10-04", fx.Vm.StatusText);

        row.Selected = null; // a list rebuild writing null into the binding is never a choice
        Assert.Equal("20261004", fx.Managed.GetOptiScalerNightlyBuild(game));

        row.Selected = "Latest (auto-update)";
        Assert.Null(fx.Managed.GetOptiScalerNightlyBuild(game));
    }

    [Fact]
    public async Task Row_WhenTheListCannotLoad_OffersLatestAndTheSavedBuild_AndSaysSo()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        fx.Managed.SetOptiScalerNightlyBuild(game, "20261004");
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;

        await row.RefreshAsync(fetchList: true);

        Assert.Equal(["Latest (auto-update)", "2026-10-04"], row.Options);
        Assert.Equal("2026-10-04", row.Selected);
        Assert.Equal(UiLocalizationService.Text("Sync.OptiScalerNightlyListFailed"), row.Note);
    }

    // #232 (was "locked while installed", #156): an installed Nightly keeps the dropdown live; the saved choice shows, and
    // a choice other than the installed build offers Apply and says the two differ.
    [Fact]
    public async Task Row_WhileNightlyIsInstalled_StaysLive_ShowsTheChoice_AndOffersApplyOnlyForAnotherBuild()
    {
        var (fx, game) = await StartAsync(NightlyList);
        using var _ = fx;
        SeedInstalledNightly(fx, game, "20261004");
        fx.Managed.SetOptiScalerNightlyBuild(game, "20261012"); // chosen since the install, not applied yet
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;

        await row.RefreshAsync(fetchList: true);

        Assert.True(row.IsEnabled);
        Assert.Equal("2026-10-12", row.Selected);
        Assert.True(row.CanApply);
        Assert.Equal(UiLocalizationService.Format("Nsw.NightlyDiffers", "2026-10-04"), row.Note);

        row.Selected = "2026-10-04"; // back to the installed build: nothing to apply
        Assert.Equal("20261004", fx.Managed.GetOptiScalerNightlyChoice(game));
        Assert.False(row.CanApply);
        Assert.Equal(UiLocalizationService.Text("Sync.OptiScalerNightlyLocked"), row.Note);
        Assert.Contains("pinned to build 2026-10-04", fx.Vm.StatusText);

        row.Selected = "Latest (auto-update)";
        Assert.Null(fx.Managed.GetOptiScalerNightlyChoice(game));
        Assert.Equal("20261004", fx.Managed.GetInstalledOptiScalerNightlyBuild(game)); // choosing never changes what is installed
        Assert.True(row.CanApply);
        Assert.Equal(UiLocalizationService.Format("Nsw.ChosenLatest", game.Name), fx.Vm.StatusText);
        Assert.Equal(UiLocalizationService.Format("Nsw.NightlyDiffers", "2026-10-04"), row.Note);
    }

    // Control frame generation's build (#136) stays: locked, no Apply, the note says why (#232 decision).
    [Fact]
    public async Task Row_ForControlFrameGenerationsBuild_IsLocked_WithoutApply()
    {
        var fx = await ViewModelFixture.StartAsync([], "en-US", httpResponse: NightlyList);
        using var _ = fx;
        var control = fx.Game(ControlUePostInstallService.GameName);
        Assert.True((await fx.Vm.ManualGames.TryAddManualGameAsync(new() { Name = control.Name, InstallPath = control.InstallPath, ExecutablePath = control.ExecutablePath, Runner = "Manual Windows" })).Added);
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || fx.Vm.SelectedGame is null); i++) await Task.Delay(25);
        var game = fx.Vm.SelectedGame!;
        SeedInstalledNightly(fx, game, ControlUeFrameGenerationService.RequiredNightlyBuild, ControlUeFrameGenerationService.OptiScalerProxyName);
        fx.Managed.SetOptiScalerNightlyBuild(game, "20261012");
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;

        await row.RefreshAsync(fetchList: true);

        Assert.False(row.IsEnabled);
        Assert.False(row.CanApply);
        Assert.Equal("2026-10-04", row.Selected); // the build it is held on, not the saved choice
        Assert.Equal(UiLocalizationService.Text("Nsw.ControlUeHeld"), row.Note);
        row.Selected = "2026-10-13";
        Assert.Equal("20261012", fx.Managed.GetOptiScalerNightlyChoice(game)); // the locked row saved nothing
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Apply_SwitchesUnderTheBusyGate_AndTheLineSaysSo(string lang)
    {
        var (fx, game, row, lines) = await InstalledAndChosenAsync(lang);
        using var _ = fx;
        try
        {
            var busyDuringSwitch = false;
            string? asked = "unset";
            fx.Managed.NightlySwitchForTesting = (g, build, _) =>
            {
                busyDuringSwitch = fx.Vm.Operations.IsBusy && row.IsApplying && !row.CanApply;
                asked = build;
                SeedInstalledNightly(fx, g, build!); // what the real switch records
                return Task.FromResult(ComponentOperationResult.Ok(OptiScalerDeploymentService.NightlySwitchedPrefix + build));
            };

            Assert.True(await row.ApplyAsync());

            Assert.True(busyDuringSwitch);
            Assert.Equal("20261012", asked);
            Assert.False(fx.Vm.Operations.IsBusy);
            var switched = UiLocalizationService.Format("Nsw.Switched", game.Name, "2026-10-12");
            Assert.Contains(UiLocalizationService.Format("Nsw.Switching", game.Name, "2026-10-12"), lines);
            Assert.Contains(switched, lines);
            Assert.Equal(switched, row.Note); // also under the row: the status line is behind the ⚙ dialog
            Assert.False(row.CanApply);
            Assert.False(row.IsApplying);
            Assert.Equal("2026-10-12", row.Selected);
            if (lang == "pt-BR") Assert.Contains("agora está em 2026-10-12", switched);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Apply_Failed_ShowsWhyInTheLanguage_AndTheRowIsBackOnTheInstalledBuild(string lang)
    {
        var (fx, game, row, lines) = await InstalledAndChosenAsync(lang);
        using var _ = fx;
        try
        {
            var failure = OptiScalerDeploymentService.NightlySwitchFailedPrefix + OptiScalerDeploymentService.NightlyBuildUnavailablePrefix + "HTTP 404";
            fx.Managed.NightlySwitchForTesting = (g, _, _) =>
            {
                fx.Managed.SetOptiScalerNightlyBuild(g, "20261004"); // what the real switch does with the choice
                return Task.FromResult(ComponentOperationResult.Fail(failure));
            };

            Assert.False(await row.ApplyAsync());

            var shown = UiLocalizationService.Present(failure);
            Assert.Equal(shown, fx.Vm.StatusText);
            Assert.Equal(shown, row.Note);
            if (lang == "pt-BR") Assert.DoesNotContain("was not switched", shown);
            Assert.Equal("2026-10-04", row.Selected);
            Assert.False(row.CanApply);
            Assert.False(fx.Vm.Operations.IsBusy);
            Assert.DoesNotContain(lines, line => line == UiLocalizationService.Format("Nsw.Switched", game.Name, "2026-10-12"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Apply_CancelledWhileDownloading_SaysTheBuildStays_FromTheRowsOwnCancel(string lang)
    {
        var (fx, game, row, _) = await InstalledAndChosenAsync(lang);
        using var _fx = fx;
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fx.Managed.NightlySwitchForTesting = async (g, _, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct); // still downloading
                return ComponentOperationResult.Ok("");
            };

            var apply = row.ApplyAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(row.CanCancel);
            row.Operations.CancelCommand.Execute(null);
            Assert.False(await apply.WaitAsync(TimeSpan.FromSeconds(30)));

            var cancelled = UiLocalizationService.Format("Nsw.SwitchCancelled", game.Name);
            Assert.Equal(cancelled, fx.Vm.StatusText);
            Assert.Equal(cancelled, row.Note);
            Assert.False(row.CanCancel);
            Assert.False(row.IsApplying);
            if (lang == "pt-BR") Assert.Contains("troca cancelada", cancelled);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Apply_ThatLandedDespiteASaveError_KeepsTheLandedNote(string lang)
    {
        var (fx, game, row, _) = await InstalledAndChosenAsync(lang);
        using var _fx = fx;
        try
        {
            var landed = DeploymentScope.Landed("OptiScaler");
            fx.Managed.NightlySwitchForTesting = (g, build, _) =>
            {
                SeedInstalledNightly(fx, g, build!);
                return Task.FromResult(landed);
            };

            Assert.True(await row.ApplyAsync());

            Assert.Equal(UiLocalizationService.Format("Nsw.Switched", game.Name, "2026-10-12") + " " + UiLocalizationService.Present(landed.Message),
                fx.Vm.StatusText);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task Apply_WhileRhiIsBusy_ChangesNothing()
    {
        var (fx, _, row, _) = await InstalledAndChosenAsync("en-US");
        using var _fx = fx;
        var called = false;
        fx.Managed.NightlySwitchForTesting = (_, _, _) => { called = true; return Task.FromResult(ComponentOperationResult.Ok("")); };
        using var held = fx.Vm.Operations.TryBegin("test");
        Assert.NotNull(held);

        Assert.False(await row.ApplyAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.False(called);
        Assert.True(row.CanApply);
    }

    private static void SeedInstalledNightly(ViewModelFixture fx, DetectedGame game, string? build, string proxyName = "dxgi.dll")
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OptiScaler.Variant"] = "Nightly", ["OptiScaler.ProxyName"] = proxyName };
        if (build is not null) metadata["OptiScaler.NightlyBuild"] = build;
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!, Version = build ?? "20261013", Files = [], Metadata = metadata
        });
    }

    // OptiScaler Nightly 2026-10-04 installed, 2026-10-12 chosen since; every status line collected.
    private static async Task<(ViewModelFixture Fx, DetectedGame Game, OptiScalerNightlyBuildPresenter Row, List<string> Lines)> InstalledAndChosenAsync(string lang)
    {
        UiLocalizationService.Apply(lang);
        var (fx, game) = await StartAsync(NightlyList, lang);
        SeedInstalledNightly(fx, game, "20261004");
        fx.Managed.SetOptiScalerNightlyBuild(game, "20261012");
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;
        await row.RefreshAsync(fetchList: true);
        Assert.True(row.CanApply);
        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText)) lock (lines) lines.Add(fx.Vm.StatusText);
        };
        return (fx, game, row, lines);
    }

    [Fact]
    public async Task Row_WithReadOnlyPreferences_RevertsAndSaysNothingChanged()
    {
        var (fx, game) = await StartAsync(NightlyList);
        using var _ = fx;
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;
        await row.RefreshAsync(fetchList: true);
        var prefs = Path.Combine(fx.ConfigDir, "prefs");
        Directory.CreateDirectory(prefs);
        File.SetUnixFileMode(prefs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            row.Selected = "2026-10-12";

            Assert.StartsWith("Could not save this game's preferences:", fx.Vm.StatusText);
            Assert.Equal("Latest (auto-update)", row.Selected);
            Assert.Null(fx.Managed.GetOptiScalerNightlyBuild(game));
        }
        finally
        {
            File.SetUnixFileMode(prefs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Row_InPortuguese_LabelsLatest_AndKeepsTheDates()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            var (fx, _) = await StartAsync(NightlyList, "pt-BR");
            using var _fx = fx;
            ChooseNightly(fx);
            var row = fx.Vm.Runtime.OptiScalerNightlyBuild;
            await row.RefreshAsync(fetchList: true);

            Assert.Equal(["Mais recente (atualização automática)", "2026-10-13", "2026-10-12", "2026-10-04"], row.Options);
            row.Selected = "2026-10-12";
            Assert.Contains("fixado na build 2026-10-12", fx.Vm.StatusText);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
