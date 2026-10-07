using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;

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

    public UiExceptionHandlingTests()
    {
        UiLocalizationService.Apply("en-US");
        UiExceptionGuard.UseLog(new UiErrorLog(Path.Combine(_root, "logs")));
        UiExceptionGuard.SetReporter(_reports.Add);
    }

    public void Dispose()
    {
        UiExceptionGuard.SetReporter(null);
        UiExceptionGuard.UseLog(UiErrorLog.CreateDefault());
        UnlockAll(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ErrorLog_RotatesBySize_AndKeepsOnlyTheNewestFiles()
    {
        var log = new UiErrorLog(Path.Combine(_root, "rot"), maxBytes: 1024, keep: 2);
        for (var i = 0; i < 12; i++)
            Assert.True(log.TryWrite("ctx", $"entry {i:D2} " + new string('x', 300)));

        Assert.True(File.Exists(log.CurrentPath));
        Assert.True(File.Exists(log.RotatedPath(1)));
        Assert.True(File.Exists(log.RotatedPath(2)));
        Assert.False(File.Exists(log.RotatedPath(3)));
        Assert.True(new FileInfo(log.CurrentPath).Length <= 1024);
        Assert.Contains("entry 11", File.ReadAllText(log.CurrentPath));
        Assert.DoesNotContain("entry 00", string.Concat(new[] { log.CurrentPath, log.RotatedPath(1), log.RotatedPath(2) }.Select(File.ReadAllText)));
    }

    [Fact]
    public void ErrorLog_UnwritableDirectory_ReturnsFalseInsteadOfThrowing()
    {
        var dir = Path.Combine(_root, "ro");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.False(new UiErrorLog(Path.Combine(dir, "logs")).TryWrite("ctx", new IOException("disk full")));
    }

    [Fact]
    public async Task RunAsync_HandlerException_IsLoggedAndReported_NotRethrown()
    {
        await UiExceptionGuard.RunAsync(() => throw new UnauthorizedAccessException("Access to the path '/cfg/settings.json' is denied."), "Settings_Click");

        var report = Assert.Single(_reports);
        Assert.StartsWith("Something went wrong: Access to the path '/cfg/settings.json' is denied.", report);
        Assert.Contains(UiExceptionGuard.Log.CurrentPath, report);
        var logged = File.ReadAllText(UiExceptionGuard.Log.CurrentPath);
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
        UiExceptionGuard.UseLog(new UiErrorLog(Path.Combine(dir, "logs")));

        await UiExceptionGuard.RunAsync(() => throw new IOException("boom"), "x");

        Assert.Equal("Something went wrong: boom. RHI is still running, but the details could not be written to the log.", Assert.Single(_reports));
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

        Assert.StartsWith("Settings were not saved:", message);
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
        Assert.EndsWith("Nothing was changed.", fx.Vm.StatusText);
        Assert.Contains(fx.Vm.Games, g => g.Name == "Tunic");
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
