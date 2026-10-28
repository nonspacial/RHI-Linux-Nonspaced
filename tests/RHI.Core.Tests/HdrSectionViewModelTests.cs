using System.ComponentModel;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #251 through the real view model (owner decision: shown unless the data says No, "safe, not sorry"): the HDR launch
/// section shows for Yes and Unknown, hides for No, and shows again for No with an HDR mod RHI installed (RenoDX). A hidden
/// section's saved profile stays in <c>game-library.json</c> untouched but is not applied at a direct launch and is not
/// offered as launch text. Why it shows is only in the label's 2 s tooltip, EN + PT-BR. HTTP fakes only.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HdrSectionViewModelTests : IDisposable
{
    private readonly LegacyHdrWsiViewModelTests _games = new();
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-section-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        _games.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("true", true, "HDR: listed as supported by PCGamingWiki.")]
    [InlineData("hackable", true, "HDR: PCGamingWiki lists it as possible with a fix.")]
    [InlineData("unknown", true, "HDR: not listed on PCGamingWiki, shown in case it applies.")]
    [InlineData("false", false, null)]
    public async Task TheSection_FollowsPcgwsAnswer(string hdr, bool shown, string? tip)
    {
        var game = _games.Game("Digging Quest", "direct");
        using var fx = await StartAsync(game, Wiki(game, hdr));

        Assert.Equal(shown, fx.Vm.Runtime.ShowHdrSection);
        Assert.Equal(tip, fx.Vm.Runtime.HdrSectionTip); // none while hidden
    }

    [Fact]
    public async Task OfflineOrNoService_ShowsTheSection()
    {
        var game = _games.Game("Digging Quest", "direct");
        var offline = Wiki(game, "false");
        offline.Fail = "offline";
        using (var fx = await StartAsync(game, offline))
        {
            Assert.True(fx.Vm.Runtime.ShowHdrSection);
            Assert.Equal("HDR: support could not be checked yet, shown in case it applies.", fx.Vm.Runtime.HdrSectionTip);
        }
        using (var fx = await LegacyHdrWsiViewModelTests.StartAsync([_games.Game("Plain Quest", "direct")], null, new LegacyHdrWsiViewModelTests.RecordingLauncher()))
            Assert.True(fx.Vm.Runtime.ShowHdrSection); // no resolver wired: nothing known, shown
    }

    [Fact]
    public async Task ANoGame_WithRenoDxInstalledByRhi_ShowsTheSection_AndSaysWhy()
    {
        var game = _games.Game("Digging Quest", "direct");
        using var fx = await StartAsync(game, Wiki(game, "false"));
        Assert.False(fx.Vm.Runtime.ShowHdrSection);

        InstallRenoDx(fx, fx.Vm.SelectedGame!);
        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
        await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);

        Assert.True(fx.Vm.Runtime.ShowHdrSection);
        Assert.StartsWith("HDR: an HDR mod is installed (RenoDX", fx.Vm.Runtime.HdrSectionTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHiddenSection_KeepsTheSavedProfile_ButADirectStartGetsNoHdr()
    {
        var game = _games.Game("Digging Quest", "direct");
        var launcher = new LegacyHdrWsiViewModelTests.RecordingLauncher();
        byte[]? seeded = null;
        using var fx = await StartAsync(game, Wiki(game, "false"), launcher, s => seeded = LegacyHdrWsiViewModelTests.Seed(s, game, LinuxHdrModes.Gamescope));
        var runtime = fx.Vm.Runtime;
        Assert.False(runtime.ShowHdrSection);

        Assert.Equal(LinuxHdrModes.Gamescope, runtime.SelectedHdrMode.Value); // the saved choice is still loaded
        Assert.False(runtime.SelectedHdrPlan.Enabled);
        Assert.False(runtime.ShowHdrLaunchProfile);
        Assert.Equal("%command%", runtime.GetSelectedHdrLaunchOptions());
        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Null(launch.Options); // started without a wrapper or HDR variables
        Assert.Equal(seeded, File.ReadAllBytes(fx.Library.LibraryPath));
        Assert.Equal(LinuxHdrModes.Gamescope, fx.Library.Load().HdrProfiles[LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!)].Mode);

        // The evidence changes (an HDR mod installed): the kept profile applies again.
        InstallRenoDx(fx, fx.Vm.SelectedGame!);
        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
        await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);
        Assert.True(runtime.ShowHdrSection);
        Assert.Equal("gamescope --hdr-enabled -- %command%", runtime.GetSelectedHdrLaunchOptions());
    }

    [Fact]
    public async Task TheAnswerArrivingLater_IsRaisedForTheView()
    {
        var game = _games.Game("Digging Quest", "direct");
        var wiki = Wiki(game, "false");
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], null, new LegacyHdrWsiViewModelTests.RecordingLauncher());
        var runtime = fx.Vm.Runtime;
        var raised = 0;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(runtime.ShowHdrSection)) Interlocked.Increment(ref raised);
        }
        runtime.PropertyChanged += OnChanged;
        try
        {
            runtime.HdrSupport = Service(wiki);
            Assert.NotNull(runtime.HdrSupportLookup);
            await runtime.HdrSupportLookup!.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
            Assert.False(runtime.ShowHdrSection);
            Assert.True(raised >= 2, $"raised {raised}"); // at the start (shown, not known yet) and when the answer landed
        }
        finally { runtime.PropertyChanged -= OnChanged; }
    }

    [Fact]
    public async Task AFullRefresh_AsksPcgwAgain()
    {
        var game = _games.Game("Digging Quest", "direct");
        var wiki = Wiki(game, "false");
        using var fx = await StartAsync(game, wiki);
        var asked = wiki.Requests.Count;

        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!); // a plain re-analysis does not
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        Assert.Null(fx.Vm.Runtime.HdrSupportLookup);
        Assert.Equal(asked, wiki.Requests.Count);

        wiki.Pages["Digging Quest"] = HdrSupportServiceTests.Wikitext("true");
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        if (fx.Vm.Runtime.HdrSupportLookup is { } lookup) await lookup.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        Assert.True(wiki.Requests.Count > asked);
        Assert.True(fx.Vm.Runtime.ShowHdrSection);
    }

    // ---- correction round 1, F1: "Change resolution while playing" is not HDR -------------------------------------------

    [Fact]
    public async Task AHiddenSection_KeepsTheResolutionToggle_AndADirectStartSwitchesTheDesktop()
    {
        // Fallout 4-like: PCGW says false, no HDR mod, a game RHI starts itself on KDE with kscreen-doctor installed.
        var game = _games.Game("Digging Quest", "direct");
        var launcher = new LegacyHdrWsiViewModelTests.RecordingLauncher();
        byte[]? seeded = null;
        using var fx = await StartOnKdeAsync(game, Wiki(game, "false"), launcher, s => seeded = SeedResolution(s, game));
        var runtime = fx.Vm.Runtime;
        var calls = new List<string>();
        var screen = new ResolutionToggleTests.FakeKScreen();
        fx.Vm.Resolution.Switcher = Switcher(calls, screen);

        Assert.False(runtime.ShowHdrSection);
        Assert.True(runtime.ShowResolutionToggle); // the toggle stays where a switch can apply
        Assert.True(runtime.ResolutionToggle);
        Assert.Equal(new ResolutionPlan(true, true, ResolutionPlanReason.KScreenDoctor, new DisplayResolution(2560, 1440), DisplayTool.KScreenDoctor),
            runtime.SelectedResolutionPlan);
        Assert.False(runtime.SelectedHdrPlan.Enabled); // the saved Gamescope HDR mode is not used

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.NotNull(launch.Options); // started through the display switch's launch shell…
        Assert.Null(launch.Options!.CommandPrefix); // …without gamescope or any HDR variable
        Assert.DoesNotContain(launch.Options.Environment ?? new Dictionary<string, string>(), pair => pair.Key.Contains("HDR", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.StartsWith("kscreen-doctor output.DP-1.mode.", StringComparison.Ordinal)); // switched
        Assert.Equal((2560, 1440), screen.CurrentSize);
        Assert.Equal(seeded, File.ReadAllBytes(fx.Library.LibraryPath)); // the saved profile is untouched
    }

    [Fact]
    public async Task AHiddenSection_OnAHandoffCard_KeepsTodaysToggleRule()
    {
        // A Steam card with gamescope installed: the toggle shows as before (#191's rule), and with HDR Off nothing can carry
        // the size into Steam, so the plan says so instead of switching.
        var game = _games.Game("Digging Quest", "steam");
        using var fx = await StartOnKdeAsync(game, Wiki(game, "false"), new LegacyHdrWsiViewModelTests.RecordingLauncher(), s => SeedResolution(s, game));
        var runtime = fx.Vm.Runtime;

        Assert.False(runtime.ShowHdrSection);
        Assert.Equal(DisplayModeSwitcher.CanEverApply(fx.Vm.SelectedGame!, runtime.NvidiaHost), runtime.ShowResolutionToggle);
        Assert.True(runtime.ShowResolutionToggle);
        Assert.Equal((true, false, ResolutionPlanReason.Handoff), (runtime.SelectedResolutionPlan.Enabled, runtime.SelectedResolutionPlan.CanApply, runtime.SelectedResolutionPlan.Reason));
        Assert.Equal("%command%", runtime.GetSelectedHdrLaunchOptions()); // no gamescope line to paste
    }

    // ---- correction round 1, F6 + F7 (R2): lookups for games the user has left ------------------------------------------

    [Fact]
    public async Task ArrowingThroughTheLibrary_CancelsTheLookupsOfGamesLeft()
    {
        // Five games nobody looked up yet; PCGamingWiki is slow (each request waits until the test lets it go, or until its
        // caller cancels). Each selection's first request arrives only once the earlier game's lookup has let the shared
        // request gate go — i.e. was cancelled; at the end only the last game's lookup asks anything more.
        const int count = 5;
        var games = Enumerable.Range(1, count).Select(i => _games.Game($"Sweep Quest {i}", "direct")).ToList();
        var wiki = new HdrSupportServiceTests.FakeWiki();
        foreach (var game in games)
        {
            wiki.Search[game.Name] = game.Name.Replace(' ', '_');
            wiki.Pages[game.Name] = HdrSupportServiceTests.Wikitext("false");
        }
        var slow = new SlowWiki(wiki);
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync(games, LegacyHdrWsiViewModelTests.Modern, new LegacyHdrWsiViewModelTests.RecordingLauncher());
        var runtime = fx.Vm.Runtime;
        fx.Vm.SelectedGame = null;
        runtime.HdrSupport = Service(slow);

        foreach (var game in games)
        {
            var arrived = slow.NextArrival();
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
            await arrived.WaitAsync(TimeSpan.FromSeconds(20)); // never arrives while a left lookup still holds the gate
        }
        var last = runtime.HdrSupportLookup!;
        slow.Release();
        await last.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);

        Assert.Equal(count - 1 + 2, slow.Requests); // one cancelled search per game left, then the last game's search + page
        Assert.Same(games[^1].Name, fx.Vm.SelectedGame!.Name);
        Assert.Equal(HdrSupportAnswer.No, runtime.HdrSupportKnown?.Answer);
        Assert.False(runtime.ShowHdrSection);
    }

    [Fact]
    public async Task ALateAnswer_ForAGameTheUserLeft_NeverLandsOnTheSelectedGame()
    {
        // B's answer (No: hidden) is known; A's lookup is slow. Select A, then B, then A's answer arrives: B stays hidden
        // with B's own answer.
        var a = _games.Game("Alpha Quest", "direct");
        var b = _games.Game("Beta Quest", "direct");
        var wiki = new HdrSupportServiceTests.FakeWiki
        {
            Search = { [a.Name] = "Alpha_Quest", [b.Name] = "Beta_Quest" },
            Pages = { [a.Name] = HdrSupportServiceTests.Wikitext("true"), [b.Name] = HdrSupportServiceTests.Wikitext("false") }
        };
        // A's server answers only when released, even after RHI cancelled the request (so A's late result lands after B's).
        var slow = new SlowWiki(wiki) { Holding = false, IgnoreCancel = true };
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync([b, a], LegacyHdrWsiViewModelTests.Modern, new LegacyHdrWsiViewModelTests.RecordingLauncher());
        var runtime = fx.Vm.Runtime;
        runtime.HdrSupport = Service(slow);
        await runtime.HdrSupportLookup!.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        Assert.False(runtime.ShowHdrSection); // B: No

        slow.Holding = true;
        var arrived = slow.NextArrival();
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == a.Name).Game;
        var lookupA = runtime.HdrSupportLookup!;
        await arrived.WaitAsync(TimeSpan.FromSeconds(20));
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == b.Name).Game; // B's answer from the cache
        Assert.Null(runtime.HdrSupportLookup);
        slow.Release();
        await lookupA.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard); // A's answer (or its cancelled Unknown) arrives now

        Assert.Equal(b.Name, fx.Vm.SelectedGame!.Name);
        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.No, HdrSupportSource.PcGamingWiki, b.Name), runtime.HdrSupportKnown);
        Assert.False(runtime.ShowHdrSection);
    }

    /// <summary>
    /// A slow PCGamingWiki in front of <see cref="HdrSupportServiceTests.FakeWiki"/>: while <see cref="Holding"/>, each request
    /// waits until <see cref="Release"/> or until its caller cancels (then it never reaches the wiki; unless
    /// <see cref="IgnoreCancel"/>). Counts every request.
    /// </summary>
    private sealed class SlowWiki(HdrSupportServiceTests.FakeWiki inner) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public volatile bool Holding = true;
        /// <summary>A held request ignores its caller's cancellation (a server that answers late whatever happens).</summary>
        public volatile bool IgnoreCancel;
        public int Requests => Volatile.Read(ref _requests);

        public Task NextArrival()
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _arrival, next);
            return next.Task;
        }

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Volatile.Read(ref _arrival).TrySetResult();
            if (Holding) await (IgnoreCancel ? _released.Task : _released.Task.WaitAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("en-US", "Resolution")]
    [InlineData("pt-BR", "Resolução")]
    public void TheResolutionRowLabel_IsInTheUiLanguage(string language, string label)
    {
        UiLocalizationService.Apply(language);
        try { Assert.Equal(label, UiLocalizationService.Text("Hw.ResolutionRow")); }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [InlineData("en-US", "HDR: listed as supported by PCGamingWiki.", "HDR: an HDR mod is installed (")]
    [InlineData("pt-BR", "HDR: listado como compatível pelo PCGamingWiki.", "HDR: um mod de HDR está instalado (")]
    public async Task TheTooltip_IsInTheUiLanguage(string language, string listed, string mod)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var yes = _games.Game("Control Quest", "direct");
            using (var fx = await StartAsync(yes, Wiki(yes, "true"), language: language))
                Assert.Equal(listed, fx.Vm.Runtime.HdrSectionTip);
            var no = _games.Game("Digging Quest", "direct");
            using (var fx = await StartAsync(no, Wiki(no, "false"), language: language))
            {
                InstallRenoDx(fx, fx.Vm.SelectedGame!);
                await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
                await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);
                Assert.StartsWith(mod, fx.Vm.Runtime.HdrSectionTip, StringComparison.Ordinal);
            }
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<ViewModelFixture> StartAsync(DetectedGame game, HdrSupportServiceTests.FakeWiki wiki,
        LegacyHdrWsiViewModelTests.RecordingLauncher? launcher = null, Action<LinuxGameLibraryStore>? seed = null, string language = "en-US")
    {
        var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], LegacyHdrWsiViewModelTests.Modern, launcher ?? new LegacyHdrWsiViewModelTests.RecordingLauncher(), seed, language);
        fx.Vm.Runtime.HdrSupport = Service(wiki);
        if (fx.Vm.Runtime.HdrSupportLookup is { } lookup) await lookup.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        return fx;
    }

    /// <summary>A KDE Wayland host with gamescope and kscreen-doctor on PATH (NVIDIA 615): where #191's switch applies.</summary>
    private async Task<ViewModelFixture> StartOnKdeAsync(DetectedGame game, HdrSupportServiceTests.FakeWiki wiki,
        LegacyHdrWsiViewModelTests.RecordingLauncher launcher, Action<LinuxGameLibraryStore>? seed)
    {
        var hdr = new LinuxHdrLaunchProfileService(
            environment: name => name switch { "XDG_SESSION_TYPE" => "wayland", "WAYLAND_DISPLAY" => "wayland-0", "XDG_CURRENT_DESKTOP" => "KDE", _ => null },
            commandExists: command => command is "gamescope" or DisplayModeSwitcher.KScreenDoctor,
            nvidiaVersion: () => LegacyHdrWsiViewModelTests.Modern,
            nvidiaGpu: () => NvidiaGpuFacts.None);
        var fx = await ViewModelFixture.StartAsync([game], seed: seed, launcher: launcher, hdrProfiles: hdr);
        await LegacyHdrWsiViewModelTests.SelectAsync(fx, game.Name);
        fx.Vm.Runtime.HdrSupport = Service(wiki);
        if (fx.Vm.Runtime.HdrSupportLookup is { } lookup) await lookup.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        return fx;
    }

    /// <summary>A saved Gamescope HDR profile with the resolution toggle on at 2560x1440; returns the file's bytes.</summary>
    private static byte[] SeedResolution(LinuxGameLibraryStore store, DetectedGame game)
    {
        var state = new LinuxGameLibraryState();
        state.HdrProfiles[LinuxGameLibraryStore.GameKey(game)] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, ResolutionToggle = true, Resolution = "2560x1440" };
        store.Save(state);
        return File.ReadAllBytes(store.LibraryPath);
    }

    private DisplayModeSwitcher Switcher(List<string> calls, ResolutionToggleTests.FakeKScreen screen)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "fakebin")).FullName;
        File.WriteAllText(Path.Combine(bin, DisplayModeSwitcher.KScreenDoctor), "#!/bin/sh\nexit 99\n"); // found on PATH, never run
        return new DisplayModeSwitcher(Path.Combine(_root, "state", "display-mode.json"), name => name == "PATH" ? bin : null,
            run: (tool, args, _) =>
            {
                lock (calls) calls.Add(Path.GetFileName(tool) + " " + string.Join(" ", args));
                return Task.FromResult(screen.Answer(args));
            },
            isRunning: (_, _) => false);
    }

    private HdrSupportService Service(HttpMessageHandler wiki)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        return new HdrSupportService(new PcGamingWikiGraphicsApiService(new HttpClient(wiki), Path.Combine(dir, "pcgw-api.json"), minRequestGap: TimeSpan.Zero),
            cachePath: Path.Combine(dir, "pcgw_hdr_cache.json"), overridesJson: """{"games":{}}""");
    }

    private static HdrSupportServiceTests.FakeWiki Wiki(DetectedGame game, string hdr)
        => new() { Search = { [game.Name] = game.Name.Replace(' ', '_') }, Pages = { [game.Name] = HdrSupportServiceTests.Wikitext(hdr) } };

    /// <summary>RenoDX as RHI installs it: its record and its add-on on disk (a row reads Installed only with both).</summary>
    private static void InstallRenoDx(ViewModelFixture fx, DetectedGame game)
    {
        var bin = game.BinaryDirectory!;
        var addon = Path.Combine(bin, "renodx-diggingquest.addon64");
        File.WriteAllBytes(addon, [0x4D, 0x5A, 0x90, 0x00]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = bin, Version = "1", Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        });
    }
}
