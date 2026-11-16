using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #270 (upstream 2.9.0/2.9.2) over the real offline view model: Settings › Components › OptiScaler › Default version, the
/// ⚙ Release row (pin a game to a release of Stable, DLSS NR or F5) and the ⚙ version line (installed vs latest).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerVersionsViewModelTests
{
    private static HttpResponseMessage? Releases(HttpRequestMessage request)
    {
        var url = request.RequestUri?.AbsoluteUri;
        var fixture = url == OptiScalerPackageProvider.ReleaseListApiUrl(OptiScalerVariant.Stable) ? "releases-optiscaler-2026-11-16.json"
            : url == OptiScalerPackageProvider.ReleaseListApiUrl(OptiScalerVariant.F5) ? "releases-optiscaler-f5-dlssnr-multipass-2026-11-16.json"
            : null;
        return fixture is null ? null : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), Encoding.UTF8, "application/json")
        };
    }

    private static async Task<(ViewModelFixture Fx, DetectedGame Game)> StartAsync(string name)
    {
        UiLocalizationService.Apply("en-US");
        var fx = await ViewModelFixture.StartAsync([], httpResponse: Releases);
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new() { Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows" });
        Assert.True(added, message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        return (fx, fx.Vm.SelectedGame!);
    }

    private static void SaveDefault(ViewModelFixture fx, string version)
    {
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.OptiScalerDefaultVersion = version;
        fx.Vm.Settings.SaveLinuxSettings(settings);
    }

    [Fact]
    public async Task DefaultVersion_AppliesToAGameWithoutAChoice_NeverOverridesOne()
    {
        var (fx, game) = await StartAsync("Default Quest");
        using var _ = fx;
        SaveDefault(fx, "F5");

        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = game;
        Assert.Equal(OptiScalerVariant.F5, fx.Vm.Runtime.SelectedOptiScalerVariant.Value);

        // Stable picked by hand is the game's own choice: the default no longer applies to it.
        fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.Stable);
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = game;
        Assert.Equal(OptiScalerVariant.Stable, fx.Vm.Runtime.SelectedOptiScalerVariant.Value);
    }

    [Theory]
    [InlineData("F5", "F5")]
    [InlineData("dlssnr", "DlssNr")]
    [InlineData("Venom", "Venom")] // #271: a recommended fork
    [InlineData("NoSuchFork", "Stable")] // unknown: Stable
    [InlineData("", "Stable")]
    public void DefaultVersion_Setting_IsNormalised(string saved, string expected)
    {
        var settings = new RHI.Platform.Linux.Settings.LinuxAppSettings { OptiScalerDefaultVersion = saved };
        settings.Normalize();
        Assert.Equal(expected, settings.OptiScalerDefaultVersion);
        Assert.False(new RHI.Platform.Linux.Settings.LinuxAppSettings().OptiScalerRemoveOldNightlyBuilds); // off by default
    }

    [Fact]
    public async Task ReleaseRow_ListsTheChannelsReleases_SavesThePinWithItsChannel_AndIsHiddenForNightly()
    {
        var (fx, game) = await StartAsync("Release Quest");
        using var _ = fx;

        var nightly = fx.Vm.OptiScalerReleaseFor(OptiScalerVariant.Nightly)!;
        Assert.False(nightly.IsVisible);

        var row = fx.Vm.OptiScalerReleaseFor(OptiScalerVariant.F5)!;
        Assert.True(row.IsVisible);
        Assert.True(row.IsEnabled);
        await row.LoadAsync();
        Assert.Equal(["Latest (auto-update)", "v0.1.38", "v0.1.37.2", "v0.1.37"], row.Options.Take(4));
        Assert.Equal("Latest (auto-update)", row.Selected);

        row.Selected = "v0.1.37";
        Assert.Equal("v0.1.37", fx.Managed.GetOptiScalerReleasePin(game, OptiScalerVariant.F5));
        Assert.Null(fx.Managed.GetOptiScalerReleasePin(game, OptiScalerVariant.Stable)); // a pin is for its channel only
        Assert.Null(fx.Managed.GetOptiScalerNightlyChoice(game));
        Assert.Contains("pinned to release v0.1.37", fx.Vm.StatusText);
        Assert.Equal("v0.1.37", fx.Vm.OptiScalerReleaseFor(OptiScalerVariant.F5)!.Selected); // reopened: shows the pin

        row.Selected = null; // a list rebuild writing null into the binding is never a choice
        Assert.Equal("v0.1.37", fx.Managed.GetOptiScalerReleasePin(game, OptiScalerVariant.F5));
        row.Selected = "Latest (auto-update)";
        Assert.Null(fx.Managed.GetOptiScalerReleasePin(game, OptiScalerVariant.F5));
    }

    [Fact]
    public async Task VersionLine_NotInstalled_ShowsTheNewestRelease_OrThePin()
    {
        var (fx, game) = await StartAsync("Version Quest");
        using var _ = fx;
        var variant = OptiScalerVariant.Stable;
        var line = fx.Vm.OptiScalerVersionLineFor(() => variant)!;

        await line.CheckAsync();
        Assert.Equal("Installed: not installed · Latest: 0.9.4 (installs this release)", line.Text);
        Assert.False(line.CanUpdate);

        fx.Managed.SetOptiScalerReleasePin(game, OptiScalerVariant.Stable, "v0.9.2");
        await line.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("Installed: not installed · Latest: 0.9.4 (pinned to v0.9.2, so it isn't updated)", line.Text);

        variant = OptiScalerVariant.DlssNr; // no listing in this fixture: GitHub could not be asked
        await line.CheckAsync();
        Assert.StartsWith("Installed: not installed · Latest: couldn't check", line.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OptiScalerVersionState.UpToDate, "0.9.4", "0.9.4", null, "Installed: 0.9.4 · Latest: 0.9.4 (up to date)")]
    [InlineData(OptiScalerVersionState.UpdateAvailable, "0.9.3", "0.9.4", null, "Installed: 0.9.3 · Latest: 0.9.4 (update available)")]
    [InlineData(OptiScalerVersionState.Pinned, "20261004", "20261004", "20261004", "Installed: 20261004 · Latest: 20261004 (pinned to 20261004, so it isn't updated)")]
    public void VersionLine_Installed_SaysWhatUpdateWouldDo(OptiScalerVersionState state, string installed, string latest, string? pin, string expected)
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal(expected, OptiScalerVersionLinePresenter.Describe(new OptiScalerVersionStatus(installed, latest, pin, state)));
    }

    [Fact]
    public void VersionLine_PtBr()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Instalado: 0.9.3 · Mais recente: 0.9.4 (atualização disponível)",
                OptiScalerVersionLinePresenter.Describe(new OptiScalerVersionStatus("0.9.3", "0.9.4", null, OptiScalerVersionState.UpdateAvailable)));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
