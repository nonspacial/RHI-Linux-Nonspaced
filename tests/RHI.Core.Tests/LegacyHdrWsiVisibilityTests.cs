using System.ComponentModel;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// #250: the Legacy HDR WSI tick is offered only where the probed host needs the layer (NVIDIA driver older than 595):
/// hidden — not disabled — on a newer or unread driver and while the host is not probed yet, shown once a probe says it
/// is needed (the view re-reads it). The plan's note says plainly when a saved tick is not used (EN + PT-BR).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LegacyHdrWsiVisibilityTests : IDisposable
{
    private readonly LegacyHdrWsiViewModelTests _games = new();

    public void Dispose() => _games.Dispose();

    [Theory]
    [InlineData("580.95.05", true)]
    [InlineData("594.99", true)]
    [InlineData("595.44.02", false)]
    [InlineData("615.78.08", false)]
    [InlineData(null, false)]
    public void TheHost_DecidesWhetherTheTickIsOffered(string? driver, bool offered)
    {
        var service = LegacyHdrWsiPlanTests.Service(driver);
        Assert.Equal(offered, LinuxHdrLaunchProfileService.OffersLegacyWsi(service.ProbeHost()));
        Assert.False(LinuxHdrLaunchProfileService.OffersLegacyWsi(LinuxHdrHostSnapshot.NotProbed));
        // Probed=false wins even if a snapshot claimed the layer is needed.
        Assert.False(LinuxHdrLaunchProfileService.OffersLegacyWsi(LinuxHdrHostSnapshot.NotProbed with { LegacyWsiLikelyRequired = true }));
    }

    [Theory]
    [InlineData("580.95.05", true, LinuxHdrLegacyWsiNote.Enabled)]
    [InlineData("580.95.05", false, LinuxHdrLegacyWsiNote.OlderNvidiaDriver)]
    [InlineData("615.78.08", true, LinuxHdrLegacyWsiNote.NotUsed)]
    [InlineData("615.78.08", false, LinuxHdrLegacyWsiNote.Off)]
    [InlineData(null, true, LinuxHdrLegacyWsiNote.NotUsed)]
    [InlineData(null, false, LinuxHdrLegacyWsiNote.Off)]
    public void TheNote_NeverSaysEnabled_WhereTheTickIsNotUsed(string? driver, bool tick, LinuxHdrLegacyWsiNote expected)
    {
        var service = LegacyHdrWsiPlanTests.Service(driver);
        foreach (var mode in new[] { LinuxHdrModes.NativeWayland, LinuxHdrModes.Gamescope })
            foreach (var launch in new[] { "direct", "steam", "xbox" })
            {
                var plan = service.BuildPlan(LegacyHdrWsiPlanTests.Game(launch), new LinuxHdrGameProfile { Mode = mode, EnableLegacyWsi = tick }, service.ProbeHost());
                Assert.Equal(expected, plan.LegacyWsi);
            }
    }

    public static TheoryData<string?, string, string> PastedTexts()
    {
        var data = new TheoryData<string?, string, string>();
        foreach (var driver in new[] { LegacyHdrWsiViewModelTests.Modern, LegacyHdrWsiViewModelTests.Older, null })
            foreach (var (source, command) in new[]
                     {
                         ("Steam", "steam -applaunch 1449560"),
                         ("Heroic/Epic", "xdg-open 'heroic://launch?appName=Fortnite&runner=legendary'"),
                         ("Lutris", "lutris 'lutris:rungame/game'"),
                         ("Manual", "flatpak run com.usebottles.bottles -b Games -p Game")
                     })
                data.Add(driver, source, command);
        return data;
    }

    [Theory]
    [MemberData(nameof(PastedTexts))]
    public void EveryTextShownForPasting_FollowsTheHost(string? driver, string source, string command)
    {
        // Coordinator decision 4: every line RHI shows for pasting into a launcher (the copied launch options, the hand-off
        // steps of Steam, Heroic, Lutris or another launcher, the NVIDIA dialog's line) follows the same rule.
        var service = LegacyHdrWsiPlanTests.Service(driver);
        var game = LegacyHdrWsiPlanTests.Game("direct");
        game.Source = source;
        game.LaunchCommand = command;
        var plan = service.BuildPlan(game, new LinuxHdrGameProfile
        {
            Mode = LinuxHdrModes.Gamescope, EnableLegacyWsi = true, Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 11 }
        }, service.ProbeHost() with { Gpu = new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 4090") }, new NvidiaDlssFamilies(true, false, false));
        var used = driver == LegacyHdrWsiViewModelTests.Older;

        Assert.NotEqual(RHI.Platform.Linux.Launch.LaunchHandoff.None, plan.Handoff);
        foreach (var language in new[] { "en-US", "pt-BR" })
        {
            UiLocalizationService.Apply(language);
            try
            {
                foreach (var text in new[] { plan.LaunchOptions, HdrLaunchText.HandoffNotice(plan, game) ?? "", HdrLaunchText.ApplyInLauncher(plan, game),
                             HdrLaunchText.Copied(plan, game), NvidiaLaunchText.ApplyInLauncher(plan, game) })
                    Assert.True(used == text.Contains("ENABLE_HDR_WSI", StringComparison.Ordinal), $"{language}: '{text}'");
            }
            finally { UiLocalizationService.Apply("en-US"); }
        }
    }

    [Theory]
    [InlineData(LegacyHdrWsiViewModelTests.Modern, false)]
    [InlineData(LegacyHdrWsiViewModelTests.Older, true)]
    [InlineData(null, false)]
    public async Task TheTick_ShowsOnlyWhereTheHostNeedsIt(string? driver, bool shown)
    {
        var game = _games.Game("Metro Quest", "steam");
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], driver, new LegacyHdrWsiViewModelTests.RecordingLauncher(),
            s => LegacyHdrWsiViewModelTests.Seed(s, game, LinuxHdrModes.Gamescope));

        Assert.Equal(shown, fx.Vm.Runtime.ShowHdrLegacyWsi);
        Assert.True(fx.Vm.Runtime.HdrLegacyWsi); // hidden or not, the control holds the saved tick
        Assert.True(fx.Vm.Runtime.CanEditHdrProfile); // the rest of the HDR section is unchanged (#251 is not this ticket)

        fx.Vm.SelectedGame = null;
        Assert.False(fx.Vm.Runtime.ShowHdrLegacyWsi);
    }

    [Fact]
    public async Task TheTick_IsHiddenUntilTheHostIsProbed_AndShowsWhenTheProbeArrives()
    {
        // The second card's analysis probes the host on a worker; the probe is held until the test lets it go.
        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var hold = false;
        string? Driver()
        {
            if (Volatile.Read(ref hold))
            {
                held.Set();
                release.Wait(LegacyHdrWsiViewModelTests.HangGuard);
            }
            return LegacyHdrWsiViewModelTests.Older;
        }
        var first = _games.Game("Control Quest", "direct");
        var second = _games.Game("Metro Quest", "steam");
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync([first, second], driver: null, new LegacyHdrWsiViewModelTests.RecordingLauncher(),
            s => LegacyHdrWsiViewModelTests.Seed(s, second, LinuxHdrModes.Gamescope), nvidiaVersion: Driver);
        var runtime = fx.Vm.Runtime;
        Assert.True(runtime.ShowHdrLegacyWsi); // the start-up probe said the layer is needed

        var raised = 0;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(runtime.ShowHdrLegacyWsi)) Interlocked.Increment(ref raised);
        }
        runtime.PropertyChanged += OnChanged;
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == second.Name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            // Forget the start-up probe: the second card has no analysis yet, so nothing about the host is known.
            runtime.LastHdrHost = null;
            Volatile.Write(ref hold, true);
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == second.Name).Game;
            Assert.True(held.Wait(LegacyHdrWsiViewModelTests.HangGuard), "the analysis never probed the host");
            Assert.False(runtime.ShowHdrLegacyWsi);
            Assert.True(runtime.HdrLegacyWsi); // loaded from the profile, not cleared while hidden
            Assert.DoesNotContain("ENABLE_HDR_WSI", runtime.HdrLaunchOptions, StringComparison.Ordinal);

            raised = 0;
            Volatile.Write(ref hold, false);
            release.Set();
            await analysed.Task.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
            await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);

            Assert.True(runtime.ShowHdrLegacyWsi);
            Assert.True(Volatile.Read(ref raised) > 0, "the view was never told the tick now shows");
            Assert.StartsWith("ENABLE_HDR_WSI=1 ", runtime.HdrLaunchOptions, StringComparison.Ordinal);
        }
        finally
        {
            runtime.PropertyChanged -= OnChanged;
            fx.Vm.Session.Completed -= OnCompleted;
            release.Set();
        }
    }

    [Theory]
    [InlineData("en-US", "The saved Legacy WSI tick is not used with this driver, which does not need it.", "explicitly enabled")]
    [InlineData("pt-BR", "A marcação de WSI legado salva não é usada com este driver, que não precisa dela.", "ativada explicitamente")]
    public async Task TheHint_SaysPlainlyASavedTickIsNotUsed(string language, string note, string enabled)
    {
        var game = _games.Game("Metro Quest", "steam");
        UiLocalizationService.Apply(language);
        try
        {
            using var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], LegacyHdrWsiViewModelTests.Modern,
                new LegacyHdrWsiViewModelTests.RecordingLauncher(), s => LegacyHdrWsiViewModelTests.Seed(s, game, LinuxHdrModes.Gamescope), language);
            var hint = fx.Vm.Runtime.HdrProfileHint;

            Assert.Contains(note, hint, StringComparison.Ordinal);
            Assert.DoesNotContain(enabled, hint, StringComparison.Ordinal);
            Assert.DoesNotContain("ENABLE_HDR_WSI", hint, StringComparison.Ordinal);
            if (language == "pt-BR") Assert.DoesNotContain("Legacy WSI", hint, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [InlineData("en-US", "older than 595")]
    [InlineData("pt-BR", "anterior ao 595")]
    public void TheTooltip_SaysWhyTheTickIsShown(string language, string why)
    {
        UiLocalizationService.Apply(language);
        try { Assert.Contains(why, UiLocalizationService.Text("Ui.LegacyWsiTip"), StringComparison.Ordinal); }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
