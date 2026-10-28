using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #246 (upstream 2.8.8 <c>230ac6a</c> removed <c>lumaDefaultGames</c>; no upstream consumer since ≤ 2.7.5): Luma mode has
/// no manifest default, whichever manifest answers (the live one without the key, the pinned 2.8.7 one with it, or none),
/// so online and offline agree; the user's explicit Enabled / Disabled choice is kept exactly.
/// </summary>
public sealed class LumaModeNoManifestDefaultTests : IDisposable
{
    private const string WithDefaults = """{ "version":2, "lumaDefaultGames":["Default Game"], "lumaRenodxCompat":["Default Game"] }""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-mode-" + Guid.NewGuid().ToString("N"));

    public LumaModeNoManifestDefaultTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData(WithDefaults)]                 // the pinned 2.8.7 manifest still lists the game
    [InlineData("""{ "version":2 }""")]        // the live manifest no longer has the key
    [InlineData(null)]                         // no manifest at all (offline, nothing cached)
    public async Task NoChoice_ModeIsOff_WhicheverManifestAnswers_AndNothingNamesLumaDefaultGames(string? manifest)
    {
        var service = Build(manifest);
        var game = Game("Default Game");

        var primed = await service.PrimeAsync(game);
        var unprimed = Build(manifest).GetCachedOrDefault(game);

        Assert.False(primed.EffectiveModeEnabled);
        Assert.Equal(LumaModePreference.Auto, primed.UserPreference);
        Assert.Equal("automatic/no default", primed.Authority);
        Assert.DoesNotContain("lumaDefaultGames", primed.Summary, StringComparison.Ordinal);
        Assert.Equal(primed.EffectiveModeEnabled, unprimed.EffectiveModeEnabled);
    }

    [Theory]
    [InlineData(LumaModePreference.Enabled, true, "user enabled")]
    [InlineData(LumaModePreference.Disabled, false, "user disabled")]
    public async Task AnExplicitChoice_IsKept_AcrossManifestsAndRestarts(LumaModePreference choice, bool enabled, string authority)
    {
        var store = Path.Combine(_root, "prefs.json");
        var game = Game("Default Game");
        var first = Build(WithDefaults, store);
        await first.PrimeAsync(game);
        Assert.Equal(enabled, first.SetPreference(game, choice).EffectiveModeEnabled);

        foreach (var manifest in new[] { WithDefaults, """{ "version":2 }""", null })
        {
            var policy = await Build(manifest, store).PrimeAsync(game);
            Assert.Equal(choice, policy.UserPreference);
            Assert.Equal(enabled, policy.EffectiveModeEnabled);
            Assert.Equal(authority, policy.Authority);
        }
    }

    [Fact]
    public async Task BackToAutomatic_IsOff_EvenWhereThePinnedManifestListedTheGame()
    {
        var service = Build(WithDefaults);
        var game = Game("Default Game");
        await service.PrimeAsync(game);
        service.SetPreference(game, LumaModePreference.Enabled);

        var policy = service.SetPreference(game, LumaModePreference.Auto);

        Assert.False(policy.EffectiveModeEnabled);
        Assert.Equal("automatic/no default", policy.Authority);
        Assert.True(policy.RenoDxCompatible); // lumaRenodxCompat is still read
    }

    private LumaPolicyService Build(string? manifest, string? store = null)
    {
        var http = new HttpClient(new Handler(manifest));
        var upstream = new UpstreamRhiManifestService(http, "https://github.com/rhi-tests/manifest.json", fallbackManifestUrl: null,
            cachePath: Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"));
        return new LumaPolicyService(upstream, new LumaModePreferenceStore(store ?? Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json")));
    }

    private DetectedGame Game(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return new DetectedGame { Name = name, InstallPath = path, Source = "Test", Runner = "Wine" };
    }

    private sealed class Handler(string? json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => json is null
                ? throw new HttpRequestException("offline")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

/// <summary>#246 in the library: the Luma filter lists games with RHI's Luma installed or Luma mode turned on by the user,
/// never a game only because a manifest (the pinned 2.8.7 one) lists it in lumaDefaultGames.</summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LumaFilterNoManifestDefaultTests
{
    [Fact]
    public async Task LumaFilter_ListsInstalledAndChosenGames_NotAManifestDefault()
    {
        const string manifest = """{ "version":2, "lumaDefaultGames":["Default Game","Installed Game","Chosen Game"] }""";
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request =>
            request.RequestUri!.AbsoluteUri == UpstreamRhiManifestService.LiveManifestUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") }
                : null);
        var listed = fx.Game("Default Game");
        var installed = fx.Game("Installed Game");
        var chosen = fx.Game("Chosen Game");
        foreach (var game in new[] { listed, installed, chosen }) await AddAndAnalyseAsync(fx, game);
        installed = fx.Vm.Games.Single(row => row.Name == "Installed Game").Game;
        var addon = Path.Combine(installed.BinaryDirectory!, "Luma.addon64");
        File.WriteAllBytes(addon, [1]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(installed), GameName = installed.Name, Component = ManagedComponentKind.LumaFramework,
            BinaryDirectory = installed.BinaryDirectory!, Version = "latest-700", Files = [new LinuxDeployedFile { Path = addon }]
        });
        fx.Managed.SetLumaModePreference(fx.Vm.Games.Single(row => row.Name == "Chosen Game").Game, LumaModePreference.Enabled);
        // The manifest was answered and the policy primed for every card (the selection analysis primes it).
        Assert.All(fx.Vm.Games, row => Assert.DoesNotContain("not primed", fx.Managed.GetLumaPolicy(row.Game).Summary, StringComparison.Ordinal));

        fx.Vm.LibraryFilterMode = "Luma";

        Assert.Equal(["Chosen Game", "Installed Game"], fx.Vm.Games.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static async Task AddAndAnalyseAsync(ViewModelFixture fx, DetectedGame game)
    {
        var added = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added.Added, added.Message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromMinutes(1));
        var card = fx.Vm.Games.Single(row => row.Name == game.Name).Game;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == game.Name) done.TrySetResult(); }
        fx.Vm.SelectedGame = null;
        fx.Vm.Session.Invalidate(card);
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = card;
            if (!fx.Vm.Session.TryGetCached(card, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromMinutes(1));
    }
}
