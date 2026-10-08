using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// UI-05 (#73): unexpected exceptions from UI handlers, background tasks and store writes are logged to a rotating
/// file and reported as a localised status line; they never reach the dispatcher and terminate the app.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UiExceptionHandlingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-errors-{Guid.NewGuid():N}");
    private readonly List<string> _reports = [];
    private readonly ManualClock _clock = new();

    public UiExceptionHandlingTests()
    {
        UiLocalizationService.Apply("en-US");
        UiExceptionGuard.ResetThrottle(_clock);
        UiExceptionGuard.UseLog(new AppLog(Path.Combine(_root, "logs", "rhi.log")));
        UiExceptionGuard.SetReporter(_reports.Add);
    }

    public void Dispose()
    {
        UiExceptionGuard.SetReporter(null);
        UiExceptionGuard.UseLog(null);
        UiExceptionGuard.ResetThrottle();
        UnlockAll(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ErrorLog_RotatesBySize_AndKeepsOnlyTheNewestFiles()
    {
        var log = new AppLog(Path.Combine(_root, "rot", "rhi.log"), maxBytes: 1024, keep: 2);
        for (var i = 0; i < 12; i++)
            Assert.True(log.TryWrite("ctx", $"entry {i:D2} " + new string('x', 300)));

        Assert.True(File.Exists(log.FilePath));
        Assert.True(File.Exists(log.RotatedPath(1)));
        Assert.True(File.Exists(log.RotatedPath(2)));
        Assert.False(File.Exists(log.RotatedPath(3)));
        Assert.True(new FileInfo(log.FilePath).Length <= 1024);
        Assert.Contains("entry 11", File.ReadAllText(log.FilePath));
        Assert.DoesNotContain("entry 00", string.Concat(new[] { log.FilePath, log.RotatedPath(1), log.RotatedPath(2) }.Select(File.ReadAllText)));
    }

    [Fact]
    public void ErrorLog_UnwritableDirectory_ReturnsFalseInsteadOfThrowing()
    {
        var dir = Path.Combine(_root, "ro");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.False(new AppLog(Path.Combine(dir, "logs", "rhi.log")).TryWrite("ctx", new IOException("disk full")));
    }

    [Fact]
    public async Task RunAsync_HandlerException_IsLoggedAndReported_NotRethrown()
    {
        await UiExceptionGuard.RunAsync(() => throw new UnauthorizedAccessException("Access to the path '/cfg/settings.json' is denied."), "Settings_Click");

        var report = Assert.Single(_reports);
        Assert.StartsWith("Something went wrong: Access to the path '/cfg/settings.json' is denied.", report);
        Assert.Contains(UiExceptionGuard.Log.FilePath, report);
        var logged = File.ReadAllText(UiExceptionGuard.Log.FilePath);
        Assert.Contains("[Settings_Click]", logged);
        Assert.Contains("UnauthorizedAccessException", logged);
    }

    [Fact]
    public async Task RunAsync_PlainCancellationIsOnlyLogged_TimeoutIsReported()
    {
        await UiExceptionGuard.RunAsync(() => throw new OperationCanceledException(), "dialog");
        Assert.Empty(_reports);

        await UiExceptionGuard.RunAsync(() => throw new TaskCanceledException("timed out", new TimeoutException()), "download");
        Assert.Single(_reports);
    }

    [Fact]
    public void UiThreadException_IsMarkedHandled_AndReportedInPortugueseForPtBr()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.True(UiExceptionGuard.HandleUiThreadException(new IOException("No space left on device")));
            Assert.StartsWith("Algo deu errado: No space left on device.", Assert.Single(_reports));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void UnobservedTaskException_ReportsTheInnerFailure()
    {
        UiExceptionGuard.HandleBackgroundException(new AggregateException(new InvalidOperationException("probe failed")));
        Assert.StartsWith("Something went wrong: probe failed.", Assert.Single(_reports));
    }

    [Fact]
    public async Task UnwritableLog_StillReports_AndSaysTheDetailsWereNotKept()
    {
        var dir = Path.Combine(_root, "ro-log");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        UiExceptionGuard.UseLog(new AppLog(Path.Combine(dir, "logs", "rhi.log")));

        await UiExceptionGuard.RunAsync(() => throw new IOException("boom"), "x");

        Assert.Equal("Something went wrong: boom. RHI is still running, but the details could not be written to the log.", Assert.Single(_reports));
    }

    /// <summary>Review m1: a failure on every frame is logged once per minute and shown once, not per pass.</summary>
    [Fact]
    public void RepeatedIdenticalException_IsLoggedOncePerMinute_AndShownOnce()
    {
        for (var i = 0; i < 10; i++) FailOnLayoutPass();

        Assert.Single(_reports);
        Assert.Equal(1, LogEntries("[ui-thread]"));

        _clock.Advance(UiExceptionGuard.RepeatWindow + TimeSpan.FromSeconds(1));
        FailOnLayoutPass();

        Assert.Equal(2, _reports.Count);
        Assert.Equal(2, LogEntries("[ui-thread]"));
        Assert.Contains("(repeated 9 more time(s) after its previous entry)", File.ReadAllText(UiExceptionGuard.Log.FilePath));
    }

    /// <summary>A user retrying the same failing action a few seconds later still sees why; the log is not repeated.</summary>
    [Fact]
    public void SameFailureRetriedLater_IsShownAgain_ButNotLoggedAgain()
    {
        FailOnLayoutPass();
        _clock.Advance(UiExceptionGuard.StatusRepeatInterval);
        FailOnLayoutPass();

        Assert.Equal(2, _reports.Count);
        Assert.Equal(1, LogEntries("[ui-thread]"));
    }

    /// <summary>Review m1: a burst of failures says once that RHI should be restarted, then stops reporting.</summary>
    [Fact]
    public void FailureBurst_SaysOnceToRestart_ThenStopsReporting()
    {
        for (var i = 0; i < UiExceptionGuard.BurstLimit + 5; i++) FailOnLayoutPass();
        UiExceptionGuard.HandleBackgroundException(new InvalidOperationException("a different failure"));

        Assert.Equal(2, _reports.Count);
        Assert.StartsWith("RHI hit repeated errors and stopped showing them; the details are in ", _reports[1]);
        Assert.Equal(1, LogEntries("[repeated-errors]"));
        Assert.Equal(1, LogEntries("[background-task]"));
    }

    private static void FailOnLayoutPass()
    {
        try { ThrowLayoutFailure(); }
        catch (InvalidOperationException ex) { Assert.True(UiExceptionGuard.HandleUiThreadException(ex)); }
    }

    private static void ThrowLayoutFailure() => throw new InvalidOperationException("Measure failed");

    private static int LogEntries(string marker)
        => File.ReadAllText(UiExceptionGuard.Log.FilePath).Split(marker).Length - 1;

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task ToggleFavourite_WithReadOnlyLibrary_DoesNotThrow_AndSaysTheChangeWasNotSaved()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        fx.Vm.ToggleFavouriteCommand.Execute(null);
        Assert.StartsWith("Could not save the game library:", fx.Vm.StatusText);
        Assert.True(fx.Vm.SelectedGame!.IsFavourite);

        fx.Vm.ToggleHiddenCommand.Execute(null);
        Assert.StartsWith("Could not save the game library:", fx.Vm.StatusText);
    }

    [Fact]
    public async Task SaveLinuxSettings_WithReadOnlyConfig_DoesNotThrow_AndChangesNothing()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settingsBefore = File.ReadAllText(fx.Settings.SettingsPath);
        File.SetUnixFileMode(fx.ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var changed = fx.Vm.GetLinuxSettingsSnapshot().Settings;
        changed.UiLanguage = "pt-BR";

        var message = fx.Vm.SaveLinuxSettings(changed);

        Assert.StartsWith("Could not save the settings:", message);
        Assert.EndsWith("Nothing was changed.", message);
        Assert.Equal("en-US", UiLocalizationService.CurrentLanguage);
        Assert.Equal(settingsBefore, File.ReadAllText(fx.Settings.SettingsPath));

        fx.Vm.ToggleViewModeCommand.Execute(null);
        Assert.StartsWith("Could not save the settings:", fx.Vm.StatusText);
    }

    [Fact]
    public async Task RemoveManualGame_WithReadOnlyLibrary_KeepsTheGame_AndSaysNothingChanged()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await fx.Vm.RemoveManualGameCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not save the game library:", fx.Vm.StatusText);
        // A pending one-shot notice (the offline fixture's DLSS manifest failure) may ride on the line.
        Assert.Contains("Nothing was changed.", fx.Vm.StatusText);
        Assert.Contains(fx.Vm.Games, g => g.Name == "Tunic");
    }

    /// <summary>Review m4: the preference store is the authority, so a refused write reverts the dropdown.</summary>
    [Fact]
    public async Task GameOverride_WithReadOnlyPreferences_RevertsTheDropdown_AndSaysNothingChanged()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = fx.Vm.SelectedGraphicsApiOverride;
        LockPreferences(fx);
        var other = fx.Vm.GraphicsApiOverrideOptions.Last(option => option != before);

        fx.Vm.SelectedGraphicsApiOverride = other;

        Assert.StartsWith("Could not save this game's preferences:", fx.Vm.StatusText);
        // A pending one-shot notice (the offline fixture's DLSS manifest failure) may ride on the line.
        Assert.Contains("Nothing was changed.", fx.Vm.StatusText);
        Assert.Equal(before, fx.Vm.SelectedGraphicsApiOverride);
        Assert.Null(fx.Vm.SelectedGame!.UserGraphicsApiOverride);
    }

    /// <summary>
    /// Integration w1 review (stores minor 3): a store that refuses to overwrite its unreadable file
    /// (<see cref="RHI.Platform.Linux.Persistence.JsonStoreUnavailableException"/>) is reported through TryPersist's own
    /// branch: the line names the file and the copy set aside, and the dropdown reverts.
    /// </summary>
    [Fact]
    public async Task GameOverride_WithUnreadablePreferencesFile_RevertsTheDropdown_AndNamesTheCopy()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = fx.Vm.SelectedGraphicsApiOverride;
        var prefs = Path.Combine(fx.ConfigDir, "prefs");
        Directory.CreateDirectory(prefs);
        var file = Path.Combine(prefs, "api.json");
        File.WriteAllText(file, "{ not json");
        var other = fx.Vm.GraphicsApiOverrideOptions.Last(option => option != before);

        fx.Vm.SelectedGraphicsApiOverride = other;

        var copy = Assert.Single(Directory.GetFiles(prefs, "api.json.corrupt-*"));
        Assert.StartsWith($"Not saved: api.json could not be read (a copy is at {copy}).", fx.Vm.StatusText);
        Assert.Equal(before, fx.Vm.SelectedGraphicsApiOverride);
        Assert.Null(fx.Vm.SelectedGame!.UserGraphicsApiOverride);
        Assert.Equal("{ not json", File.ReadAllText(file));
    }

    /// <summary>
    /// Integration w1 review (stores minor 6): the DLSS Enabler, RTX 20 FG and Universal MFG dialog saves go through
    /// TryPersist. A store failure other than a refused unreadable file (here a read-only folder) used to escape the
    /// dropdown handler to the global guard; now it is a failed result saying nothing changed, and the stored
    /// preferences (what the dialog reverts its dropdowns to) are untouched.
    /// </summary>
    [Theory]
    [InlineData("dlssg2030")]
    [InlineData("enabler")]
    [InlineData("mfg")]
    public async Task DialogPreferenceSave_WithReadOnlyPreferences_FailsSayingNothingChanged(string dialog)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        string Stored() => dialog switch
        {
            "dlssg2030" => fx.Vm.GetDlssg2030Preferences().ProxyName,
            "enabler" => fx.Vm.GetStandaloneDlssEnablerPreferences().ProxyName,
            _ => $"{fx.Vm.GetRtx40MfgPreferences().GpuGeneration}|{fx.Vm.GetRtx40MfgPreferences().ProxyName}"
        };
        var before = Stored();
        LockPreferences(fx);

        var result = dialog switch
        {
            "dlssg2030" => fx.Vm.SetDlssg2030Preferences(Dlssg2030UnlockDeploymentService.GpuGenRtx20,
                Dlssg2030UnlockDeploymentService.ProxyNames.First(p => p != before)),
            "enabler" => fx.Vm.SetStandaloneDlssEnablerPreferences(StandaloneDlssEnablerDeploymentService.ProxyNames.First(p => p != before)),
            _ => fx.Vm.SetRtx40MfgPreferences(Rtx40MfgStandaloneDeploymentService.GpuGenRtx30,
                Rtx40MfgStandaloneDeploymentService.SelectableProxyNames.First(p => !before.EndsWith("|" + p, StringComparison.Ordinal)))
        };

        Assert.False(result.Success);
        Assert.StartsWith("Could not save this game's preferences:", result.Message);
        Assert.Contains("Nothing was changed.", result.Message);
        Assert.StartsWith("Could not save this game's preferences:", fx.Vm.StatusText);
        Assert.Equal(before, Stored());
    }

    [Fact]
    public async Task LumaMode_WithReadOnlyPreferences_RevertsTheDropdown_AndSaysNothingChanged()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = fx.Vm.SelectedLumaModePreference;
        LockPreferences(fx);

        fx.Vm.SelectedLumaModePreference = fx.Vm.LumaModeOptions.Last(option => option != before);

        Assert.StartsWith("Could not save this game's preferences:", fx.Vm.StatusText);
        // A pending one-shot notice (the offline fixture's DLSS manifest failure) may ride on the line.
        Assert.Contains("Nothing was changed.", fx.Vm.StatusText);
        Assert.Equal(before, fx.Vm.SelectedLumaModePreference);
    }

    [Fact]
    public async Task OptiScalerVariant_WithReadOnlyPreferences_RevertsTheDropdown_AndSaysNothingChanged()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = fx.Vm.SelectedOptiScalerVariant;
        LockPreferences(fx);

        fx.Vm.SelectedOptiScalerVariant = fx.Vm.OptiScalerVariantOptions.Last(option => option != before);

        Assert.StartsWith("Could not save this game's preferences:", fx.Vm.StatusText);
        // A pending one-shot notice (the offline fixture's DLSS manifest failure) may ride on the line.
        Assert.Contains("Nothing was changed.", fx.Vm.StatusText);
        Assert.Equal(before, fx.Vm.SelectedOptiScalerVariant);
    }

    /// <summary>Review m2: a refused edit must not linger in memory and ride along with the next successful save.</summary>
    [Fact]
    public async Task RefusedGameEdit_IsNotPersisted_ByALaterSuccessfulSave()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        await AddAndSelectAsync(fx, game);
        LockLibrary(fx);

        var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(game.ExecutablePath!, game.InstallPath, game.BinaryDirectory!, null);

        Assert.False(saved);
        Assert.StartsWith("Could not save the game library:", message);
        Assert.EndsWith("Nothing was changed.", message);
        UnlockAll(fx.DataDir);
        fx.Vm.ToggleFavouriteCommand.Execute(null);
        Assert.Empty(fx.Library.Load().GameEdits);
        Assert.Single(fx.Library.Load().FavouriteGames);
    }

    [Fact]
    public async Task RefusedReset_IsNotPersisted_ByALaterSuccessfulSave()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        await AddAndSelectAsync(fx, game);
        var (saved, saveMessage) = await fx.Vm.SaveSelectedGameEditAsync(game.ExecutablePath!, game.InstallPath, game.BinaryDirectory!, null);
        Assert.True(saved, saveMessage);
        LockLibrary(fx);

        var (reset, message) = await fx.Vm.ResetSelectedGameEditAsync();

        Assert.False(reset);
        Assert.EndsWith("Nothing was changed.", message);
        UnlockAll(fx.DataDir);
        fx.Vm.ToggleFavouriteCommand.Execute(null);
        Assert.Single(fx.Library.Load().GameEdits);
    }

    [Fact]
    public async Task RefusedManualAdd_IsNotPersisted_ByALaterSuccessfulSave()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var other = fx.Game("Hades");
        LockLibrary(fx);

        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = other.Name, InstallPath = other.InstallPath, ExecutablePath = other.ExecutablePath, Runner = "Manual Windows"
        });

        Assert.False(added);
        Assert.EndsWith("Nothing was changed.", message);
        Assert.DoesNotContain(fx.Vm.Games, g => g.Name == "Hades");
        UnlockAll(fx.DataDir);
        fx.Vm.ToggleFavouriteCommand.Execute(null);
        Assert.Equal(["Tunic"], fx.Library.Load().ManualGames.Select(g => g.Name));
    }

    private static void LockLibrary(ViewModelFixture fx)
        => File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    private static void LockPreferences(ViewModelFixture fx)
    {
        var prefs = Path.Combine(fx.ConfigDir, "prefs");
        Directory.CreateDirectory(prefs);
        File.SetUnixFileMode(prefs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static async Task AddAndSelectAsync(ViewModelFixture fx, DetectedGame game)
    {
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
    }

    private static void UnlockAll(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
