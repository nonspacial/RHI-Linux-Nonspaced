using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #292 (upstream 2.8.9 "Automatic update checks now run at least every 4 hours, even if you restart RHI"; 2.9.2 "With a
/// GitHub token … every hour"): the automatic pass runs every 4 h, hourly with a token, and the last pass's time is kept
/// between launches so a restart neither adds a pass nor pushes the next one out.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class UpdateCadenceTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-cadence-" + Guid.NewGuid().ToString("N"));

    public UpdateCadenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Interval_IsFourHours_HourlyWithAToken()
    {
        Assert.Equal(TimeSpan.FromHours(4), UpdateCadence.Interval(hasGitHubToken: false));
        Assert.Equal(TimeSpan.FromHours(1), UpdateCadence.Interval(hasGitHubToken: true));
    }

    [Theory]
    [InlineData(null, 0)] // never checked: at once
    [InlineData(-60, 180)] // checked an hour ago: the rest of the 4 h
    [InlineData(-239, 1)]
    [InlineData(-240, 0)] // due
    [InlineData(-600, 0)] // overdue (RHI was closed): at once, never a whole interval more
    [InlineData(30, 240)] // in the future (the clock went back): a whole interval
    public void FirstDelay_CountsFromTheLastPass_AcrossRestarts(int? lastMinutesFromNow, int expectedMinutes)
    {
        DateTimeOffset? last = lastMinutesFromNow is { } minutes ? Noon.AddMinutes(minutes) : null;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UpdateCadence.FirstDelay(last, Noon, TimeSpan.FromHours(4)));
    }

    [Fact]
    public async Task Loop_WaitsTheFirstDelay_ThenReadsTheIntervalBeforeEachWait_AndReportsEachPass()
    {
        using var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var passes = 0;
        var succeeded = 0;
        var hasToken = false;

        await BackgroundUpdateLoop.RunAsync(
            _ =>
            {
                passes++;
                hasToken = true; // a token was added after the first pass
                if (passes == 2) cts.Cancel();
                return Task.CompletedTask;
            },
            TimeSpan.FromHours(4), runImmediate: true, (_, _) => { }, cts.Token,
            (wait, token) => { token.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; },
            firstDelay: TimeSpan.FromMinutes(90),
            currentInterval: () => UpdateCadence.Interval(hasToken),
            passSucceeded: () => succeeded++).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([TimeSpan.FromMinutes(90), TimeSpan.FromHours(1)], waits);
        Assert.Equal(2, succeeded);
    }

    [Fact]
    public void Stamp_IsKeptBetweenLaunches_AndAnUnreadableFileReadsAsNeverChecked()
    {
        var path = Path.Combine(_root, "update-check.json");
        Assert.Null(new UpdateCheckStampStore(path).LastCheck());

        new UpdateCheckStampStore(path).Record(Noon);
        Assert.Equal(Noon, new UpdateCheckStampStore(path).LastCheck());

        File.WriteAllText(path, "{ not json");
        var store = new UpdateCheckStampStore(path);
        Assert.Null(store.LastCheck());
        store.Record(Noon.AddHours(1)); // a cache: replaced
        Assert.Equal(Noon.AddHours(1), store.LastCheck());
    }

    [Fact]
    public async Task ViewModel_AfterARestart_WaitsWhatIsLeftOfTheInterval_AndAPassIsStamped()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var stamps = new UpdateCheckStampStore(Path.Combine(_root, "update-check.json"));
        stamps.Record(Noon.AddHours(-1));
        var updates = fx.Vm.Updates;
        var waits = new List<TimeSpan>();
        var passRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.CheckStamps = stamps;
        updates.Now = () => Noon;
        updates.LoopDelay = (wait, token) =>
        {
            lock (waits) waits.Add(wait);
            if (waits.Count == 1) return Task.CompletedTask; // the first wait "passes": the pass runs
            passRan.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        };
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AutoUpdateComponents = true;
        fx.Settings.Save(settings);
        fx.Vm.AdoptSettings(settings, restartLoop: false, runImmediate: false);

        updates.RestartBackgroundUpdateLoop(runImmediate: false);
        await passRan.Task.WaitAsync(TimeSpan.FromSeconds(30));
        updates.StopBackgroundLoop();

        Assert.Equal(TimeSpan.FromHours(3), waits[0]);
        Assert.Equal(TimeSpan.FromHours(4), waits[1]);
        Assert.Equal(Noon, stamps.LastCheck());
    }

    [Fact]
    public void SettingsSave_RunsAtOnceOnlyWhenAutomaticUpdatesWereJustTurnedOn()
    {
        var on = new LinuxAppSettings { AutoUpdateComponents = true };
        Assert.True(UpdatesViewModel.RunsImmediatelyAfterSave(new LinuxAppSettings(), on));
        Assert.False(UpdatesViewModel.RunsImmediatelyAfterSave(on, new LinuxAppSettings { AutoUpdateComponents = true }));
    }

    // "Refresh fully rescans the selected game" (upstream 2.9.3 5b0054b3): here Refresh was always the full refresh, so
    // DLSS a game update added shows at once. Pinned.
    [Fact]
    public async Task Refresh_RescansTheSelectedGame_SoDllsAGameUpdateAddedShowAtOnce()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "refresh-game")).FullName;
        var exe = Path.Combine(dir, "RefreshGame.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "Refresh Game", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(((IPanelHost)fx.Vm).Session.CachedFiles(fx.Vm.SelectedGame!)!, file => file.EndsWith("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));

        File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.4.0")); // the game's update
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        fx.Vm.SelectedGame ??= fx.Vm.AllLibraryGames.Single();
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(((IPanelHost)fx.Vm).Session.CachedFiles(fx.Vm.SelectedGame!)!, file => file.EndsWith("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
    }
}
