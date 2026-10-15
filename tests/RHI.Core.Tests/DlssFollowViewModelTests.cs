using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #177 through the real view model: the Settings toggles are kept by Save (snapshot), turning one on starts the
/// background loop at once and its pass follows the newest DLSS, and every Full Refresh asks for a pass.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DlssFollowViewModelTests : IDisposable
{
    private readonly string _games = Path.Combine(Path.GetTempPath(), "rhi-follow-vm-" + Guid.NewGuid().ToString("N"));

    public DlssFollowViewModelTests() => Directory.CreateDirectory(_games);

    public void Dispose()
    {
        try { Directory.Delete(_games, true); } catch { }
    }

    [Fact]
    public async Task TurningFollowOn_RunsAPassAtOnce_ThatSwapsFilesOnThePreviousNewest()
    {
        var game = Game("follow-vm");
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: DlssAutoFollowServiceTests.ManifestHandler.Answer);
        await fx.Vm.Updates.RequestFollowNewest(); // start-up's pass (toggles off) is over before the baseline is written
        var store = new DlssAutoFollowStore(Path.Combine(fx.ConfigDir, "prefs", "dlss-follow.json"));
        store.Save(new(StringComparer.OrdinalIgnoreCase)
        {
            ["dlss"] = new() { Newest = "310.4.0" }, ["dlssd"] = new() { Newest = "310.4.0" }, ["dlssg"] = new() { Newest = "310.4.0" }
        });
        var summary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText) && fx.Vm.StatusText.StartsWith("Follow newest DLSS/Streamline:", StringComparison.Ordinal))
                summary.TrySetResult(fx.Vm.StatusText);
        };

        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.FollowNewestDlss = true;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        var line = await summary.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal("Follow newest DLSS/Streamline: 1 file(s) updated.", line);
        Assert.Equal("310.5.2.0", DlssAutoFollowService.PeFileVersion(Path.Combine(game.InstallPath, "nvngx_dlss.dll")));
        Assert.Equal("310.5.2", store.Load()!["dlss"].Newest);
        Assert.True(fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings.FollowNewestDlss); // kept by the snapshot Save rebuilds from
        Assert.False(fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings.FollowNewestStreamline);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    [Fact]
    public async Task FullRefresh_AsksForAFollowPass()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("refresh-vm")]);
        await fx.Vm.Updates.RequestFollowNewest();
        var store = new DlssAutoFollowStore(Path.Combine(fx.ConfigDir, "prefs", "dlss-follow.json"));
        store.Save(new(StringComparer.OrdinalIgnoreCase) { ["dlss"] = new() { Newest = "310.4.0" } });
        var before = fx.Vm.Updates.LastFollowRequest;

        await fx.Vm.RefreshCommand.ExecuteAsync(null);

        var pass = fx.Vm.Updates.LastFollowRequest;
        Assert.NotNull(pass);
        Assert.NotSame(before, pass);
        await pass.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(store.Load()!); // the toggles are off: that pass forgets the baseline
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void BackgroundLoop_RunsForComponentUpdatesOrEitherFollowToggle(bool components, bool dlss, bool streamline, bool wanted)
        => Assert.Equal(wanted, UpdatesViewModel.LoopWanted(new LinuxAppSettings
        {
            AutoUpdateComponents = components, FollowNewestDlss = dlss, FollowNewestStreamline = streamline
        }));

    [Fact]
    public void SettingsSave_RestartsTheLoop_ForAFollowToggle_AndRunsAtOnceOnlyWhenOneIsTurnedOn()
    {
        var off = new LinuxAppSettings();
        var dlssOn = new LinuxAppSettings { FollowNewestDlss = true };
        var streamlineOn = new LinuxAppSettings { FollowNewestStreamline = true };
        Assert.True(UpdatesViewModel.LoopSettingsChanged(off, dlssOn));
        Assert.True(UpdatesViewModel.LoopSettingsChanged(off, streamlineOn));
        Assert.True(UpdatesViewModel.LoopSettingsChanged(dlssOn, off));
        Assert.False(UpdatesViewModel.LoopSettingsChanged(dlssOn, new LinuxAppSettings { FollowNewestDlss = true }));
        Assert.True(UpdatesViewModel.RunsImmediatelyAfterSave(off, dlssOn));
        Assert.True(UpdatesViewModel.RunsImmediatelyAfterSave(off, streamlineOn));
        Assert.False(UpdatesViewModel.RunsImmediatelyAfterSave(dlssOn, off));
        Assert.False(UpdatesViewModel.RunsImmediatelyAfterSave(dlssOn, new LinuxAppSettings { FollowNewestDlss = true }));
    }

    [Fact]
    public void FollowSummary_IsQuietUnlessAFileChanged_Failed_OrWaited()
    {
        Assert.Null(UpdatePolicyFeatures.FollowSummary(new DlssFollowResult(DlssFollowOutcome.Seeded, [])));
        Assert.Null(UpdatePolicyFeatures.FollowSummary(new DlssFollowResult(DlssFollowOutcome.CouldNotCheck, [])));
        var swaps = new[]
        {
            new DlssFollowSwap("G", "DLSS SR (nvngx_dlss.dll)", "310.4.0", "310.5.2", true, null),
            new DlssFollowSwap("H", "DLSS SR (nvngx_dlss.dll)", "310.4.0", "310.5.2", false, "no")
        };
        Assert.Equal("Follow newest DLSS/Streamline: 1 file(s) updated · 1 failed and were kept as they were (details in Updates) · 2 running game(s) left for the next check.",
            UpdatePolicyFeatures.FollowSummary(new DlssFollowResult(DlssFollowOutcome.Followed, swaps, DeferredGames: 2)));
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_games, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.4.0"));
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }
}
