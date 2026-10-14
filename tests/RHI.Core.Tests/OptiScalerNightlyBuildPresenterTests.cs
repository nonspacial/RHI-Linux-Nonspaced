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

    [Fact]
    public async Task Row_WhileOptiScalerIsInstalled_ShowsTheInstalledBuild_AndIsLocked()
    {
        var (fx, game) = await StartAsync(NightlyList);
        using var _ = fx;
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!, Version = "20261004", Files = [],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["OptiScaler.Variant"] = "Nightly", ["OptiScaler.NightlyBuild"] = "20261004" }
        });
        fx.Managed.SetOptiScalerNightlyBuild(game, "20261012"); // a later preference never overrides what is installed
        ChooseNightly(fx);
        var row = fx.Vm.Runtime.OptiScalerNightlyBuild;

        await row.RefreshAsync(fetchList: true);

        Assert.False(row.IsEnabled);
        Assert.Equal("2026-10-04", row.Selected);
        Assert.Equal(UiLocalizationService.Text("Sync.OptiScalerNightlyLocked"), row.Note);
        row.Selected = "2026-10-13";
        Assert.Equal("20261004", fx.Managed.GetOptiScalerNightlyBuild(game));
        fx.Records.Remove(game, ManagedComponentKind.OptiScaler);
        Assert.Equal("20261012", fx.Managed.GetOptiScalerNightlyBuild(game)); // the locked row saved nothing
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
