using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/extras-fg-2 (#280, upstream 2.9.0 <c>ShowRtxEncoreSettingsAsync</c>): RTX Encore's ⚙ warns while the game is
/// running that changes apply at the next start and the in-game menu may overwrite them. Linux asks the same probe Update
/// All defers running games with (<see cref="LinuxGameRunningProbe"/>, the game's exe in /proc/&lt;pid&gt;/cmdline).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RtxEncoreRunningWarningTests
{
    private sealed class FixedProbe(bool running) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => running;
    }

    private static ComponentDialogLine Warning => ComponentDialogLine.Warning(UiLocalizationService.Text("Enc.GameRunning"));

    [Fact]
    public void Lines_WarnOnlyWhileTheGameRuns_AfterTheFileNotes()
    {
        var running = FgSettingsBuilder.RtxEncoreLines(true, false, null, null, gameRunning: true);
        var closed = FgSettingsBuilder.RtxEncoreLines(true, false, null, null, gameRunning: false);

        Assert.Contains(Warning, running);
        Assert.DoesNotContain(Warning, closed);
        // Upstream's order: the intro, the "no rtx-encore.jsonc yet" note, then the running warning.
        Assert.True(running.ToList().IndexOf(Warning) > running.ToList().IndexOf(ComponentDialogLine.Plain(UiLocalizationService.Text("Enc.NoFile"))));
    }

    [Fact]
    public void Warning_HasUpstreamsTextInEnglish_AndPortuguese()
    {
        Assert.Equal("The game is running. Changes apply the next time it starts, and RTX Encore's in-game menu may overwrite them while it's open.",
            UiLocalizationService.TextFor("en-US", "Enc.GameRunning"));
        Assert.NotEqual(UiLocalizationService.TextFor("en-US", "Enc.GameRunning"), UiLocalizationService.TextFor("pt-BR", "Enc.GameRunning"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dialog_AsksTheRunningGameProbe(bool running)
    {
        using var fx = await ViewModelFixture.StartAsync([], runningProbe: new FixedProbe(running));
        var game = fx.Game("Encore Quest");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));

        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.RtxEncore);

        Assert.Equal(running, dialog.Lines.Contains(Warning));
    }
}
