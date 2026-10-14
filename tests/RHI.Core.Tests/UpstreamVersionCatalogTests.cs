using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #86 (UI-19): "latest version" values expire; #117: Full Refresh re-fetches the NR version lists.
public sealed class UpstreamVersionCatalogTests
{
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LatestVersion_IsReusedWithinTheTimeToLive_AndQueriedAgainAfterIt()
    {
        var cache = new LatestVersionCache<PeArchitecture>(TimeSpan.FromHours(1), () => _now);
        var loads = 0;
        Task<string?> Load() => Task.FromResult<string?>($"v{++loads}");

        Assert.Equal("v1", await cache.GetAsync(PeArchitecture.X64, Load));
        _now = _now.AddMinutes(59);
        Assert.Equal("v1", await cache.GetAsync(PeArchitecture.X64, Load));
        Assert.Equal("v1", cache.TryGetFresh(PeArchitecture.X64));

        _now = _now.AddMinutes(2); // 61 minutes after the first query
        Assert.Null(cache.TryGetFresh(PeArchitecture.X64));
        Assert.Equal("v2", await cache.GetAsync(PeArchitecture.X64, Load));
        Assert.Equal(2, loads);
    }

    [Fact]
    public void LatestVersion_DefaultTimeToLive_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), LatestVersionCache<PeArchitecture>.DefaultTimeToLive);
        var cache = new LatestVersionCache<PeArchitecture>(utcNow: () => _now);
        cache.Set(PeArchitecture.X86, "1.0");
        _now = _now.AddHours(1);
        Assert.Null(cache.TryGetFresh(PeArchitecture.X86));
    }

    [Fact]
    public async Task LatestVersion_ConcurrentQueriesShareOneRequest_AndALoadStartedBeforeClearIsNotStored()
    {
        var cache = new LatestVersionCache<ManagedComponentKind>(utcNow: () => _now);
        var gate = new TaskCompletionSource<string?>();
        var loads = 0;
        Task<string?> Load() { loads++; return gate.Task; }

        var first = cache.GetAsync(ManagedComponentKind.ShortFuse, Load);
        var second = cache.GetAsync(ManagedComponentKind.ShortFuse, Load);
        cache.Clear();
        gate.SetResult("v9");

        Assert.Equal(("v9", "v9"), (await first, await second));
        Assert.Equal(1, loads);
        Assert.Null(cache.TryGetFresh(ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task NeuralVersionLists_LoadOnce_AndFullRefreshFetchesThemAgainAndForgetsLatestVersions()
    {
        var fetches = new Dictionary<ManagedComponentKind, int>();
        NeuralVersionLists? applied = null;
        Task<IReadOnlyList<GitHubReleaseVersion>> List(ManagedComponentKind kind)
        {
            fetches[kind] = fetches.GetValueOrDefault(kind) + 1;
            return Task.FromResult<IReadOnlyList<GitHubReleaseVersion>>([new GitHubReleaseVersion($"v{fetches[kind]}", $"{fetches[kind]}", false)]);
        }
        var catalog = new UpstreamVersionCatalog(List, lists => applied = lists, () => _now);
        catalog.UalLatest.Set(PeArchitecture.X64, "5.0");
        catalog.ShortFuseLatest.Set(ManagedComponentKind.ShortFuse, "v3");

        Assert.Equal(NeuralCatalogOutcome.Loaded, await catalog.NeuralVersions.EnsureAsync());
        Assert.Equal(NeuralCatalogOutcome.NotNeeded, await catalog.NeuralVersions.EnsureAsync());
        Assert.All(new[] { ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge, ManagedComponentKind.ShortFuse },
            kind => Assert.Equal(1, fetches[kind]));
        Assert.Equal("5.0", catalog.UalLatest.TryGetFresh(PeArchitecture.X64));

        Assert.Equal(NeuralCatalogOutcome.Loaded, await catalog.FullRefreshAsync());

        Assert.Equal(3, fetches.Count);
        Assert.All(fetches.Values, count => Assert.Equal(2, count));
        Assert.Equal("v2", applied!.ShortFuse.Single().Tag);
        Assert.Null(catalog.UalLatest.TryGetFresh(PeArchitecture.X64));
        Assert.Null(catalog.ShortFuseLatest.TryGetFresh(ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task NeuralVersionLists_FailedLoad_IsThrownToItsStarter_AndRetriedByTheNextCall()
    {
        var fail = true;
        Task<IReadOnlyList<GitHubReleaseVersion>> List(ManagedComponentKind kind)
            => fail ? Task.FromException<IReadOnlyList<GitHubReleaseVersion>>(new HttpRequestException("offline"))
                : Task.FromResult<IReadOnlyList<GitHubReleaseVersion>>([]);
        var catalog = new NeuralVersionCatalog(List, _ => { });

        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.EnsureAsync());
        Assert.False(catalog.IsLoaded);
        fail = false;
        Assert.Equal(NeuralCatalogOutcome.Loaded, await catalog.EnsureAsync());
        Assert.True(catalog.IsLoaded);
    }

    // w2 merge review, minor 2: the shared load runs on its starter's token ("Load versions" passes the operation's).

    [Fact]
    public async Task NeuralVersionLists_AJoinerCancelling_DoesNotCancelTheStartersLoad()
    {
        var lists = new BlockingLists();
        var applied = 0;
        var catalog = new NeuralVersionCatalog(lists.List, _ => applied++);
        var starter = catalog.EnsureAsync();
        using var joinerCancel = new CancellationTokenSource();
        var joiner = catalog.EnsureAsync(cancellationToken: joinerCancel.Token);

        joinerCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        Assert.All(lists.Tokens, token => Assert.False(token.IsCancellationRequested));
        lists.Release("v1");

        Assert.Equal(NeuralCatalogOutcome.Loaded, await starter.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(catalog.IsLoaded);
        Assert.Equal(1, applied);
        Assert.Equal(3, lists.Calls);
    }

    [Fact]
    public async Task NeuralVersionLists_ACancelledStarter_DoesNotStopAForcedJoinerFromLoading()
    {
        var lists = new BlockingLists();
        NeuralVersionLists? applied = null;
        var catalog = new NeuralVersionCatalog(lists.List, value => applied = value);
        using var starterCancel = new CancellationTokenSource();
        var starter = catalog.EnsureAsync(cancellationToken: starterCancel.Token);
        var forced = catalog.EnsureAsync(forceRefresh: true); // Full Refresh joins with no token of its own

        starterCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        for (var i = 0; i < 200 && lists.Calls < 6; i++) await Task.Delay(10);
        Assert.Equal(6, lists.Calls); // the forced joiner started its own load
        lists.Release("v2");

        Assert.Equal(NeuralCatalogOutcome.Loaded, await forced.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(catalog.IsLoaded);
        Assert.Equal("v2", applied!.Dlss5Feeder.Single().Tag);
    }

    [Fact]
    public async Task NeuralVersionLists_ACancelledStarter_LeavesThemUnloadedAndUnapplied()
    {
        var lists = new BlockingLists();
        var applied = 0;
        var catalog = new NeuralVersionCatalog(lists.List, _ => applied++);
        using var cancel = new CancellationTokenSource();
        var starter = catalog.EnsureAsync(cancellationToken: cancel.Token);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        lists.Release("late");
        await Task.Delay(50);

        Assert.False(catalog.IsLoaded);
        Assert.Equal(0, applied);
        Assert.Equal(NeuralCatalogOutcome.Loaded, await catalog.EnsureAsync()); // the next caller loads them again
        Assert.Equal(1, applied);
    }

    /// <summary>List calls that wait until released (or their token is cancelled), recording each call's token.</summary>
    private sealed class BlockingLists
    {
        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentQueue<CancellationToken> _tokens = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<CancellationToken> Tokens => _tokens.ToArray();

        public void Release(string tag) => _release.TrySetResult(tag);

        public async Task<IReadOnlyList<GitHubReleaseVersion>> List(ManagedComponentKind kind, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _tokens.Enqueue(cancellationToken);
            var tag = await _release.Task.WaitAsync(cancellationToken);
            return [new GitHubReleaseVersion(tag, tag.TrimStart('v'), false)];
        }
    }
}

// #117: the view model's Full Refresh re-fetches the NR version lists (DLSS5 Feeder, DX11 Bridge, ShortFuse); a partial
// library refresh (adding a game) does not touch the network for them. Each response serves a new tag (v<n>), so the
// lists shown after Full Refresh prove they came from a request made by it. ShortFuse's list shares its URL with the
// ShortFuse latest-version check a selection makes, so its request count is not compared exactly.
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralVersionRefreshViewModelTests
{
    private const string Feeder = "/repos/jlrouzies-fr/DLSS5-Feeder/releases";
    private const string Bridge = "/repos/NIGos/dlss5-bridge/releases";
    private const string ShortFuse = "/repos/RankFTW/rhi-repo/releases";

    [Fact]
    public async Task FullRefresh_FetchesTheNrVersionListsAgain_PartialRefreshDoesNot()
    {
        var fetches = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        HttpResponseMessage? Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            if (!uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) || !uri.Query.Contains("per_page=100", StringComparison.Ordinal))
                return null;
            var count = fetches.AddOrUpdate(uri.AbsolutePath, 1, (_, n) => n + 1);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent($$"""[{"tag_name":"{{Prefix(uri.AbsolutePath)}}{{count}}"}]""") };
        }
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Respond);
        Assert.Equal(new[] { Bridge, Feeder, ShortFuse }.Order(StringComparer.Ordinal), fetches.Keys.Order(StringComparer.Ordinal));

        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        var beforeRefresh = new Dictionary<string, int>(fetches);

        await fx.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(beforeRefresh[Feeder] + 1, fetches[Feeder]);
        Assert.Equal(beforeRefresh[Bridge] + 1, fetches[Bridge]);
        Assert.Contains($"v{fetches[Feeder]}", fx.Vm.Extras.Dlss5FeederVersionOptions);
        Assert.Contains($"v{fetches[Bridge]}", fx.Vm.Extras.Dlss5BridgeVersionOptions);
        Assert.Contains(fx.Vm.Extras.ShortFuseVersionOptions,
            tag => tag.StartsWith(Prefix(ShortFuse), StringComparison.Ordinal) && int.Parse(tag[Prefix(ShortFuse).Length..]) > beforeRefresh[ShortFuse]);
    }

    // ShortFuse lists only its own tags in the shared rhi-repo releases.
    private static string Prefix(string path) => path == ShortFuse ? ShortFuseDeploymentService.ReleaseTagPrefix : "v";

    [Fact]
    public async Task PartialRefresh_DoesNotFetchTheNrVersionLists()
    {
        var fetches = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        HttpResponseMessage? Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath is not (Feeder or Bridge)) return null;
            fetches.AddOrUpdate(uri.AbsolutePath, 1, (_, n) => n + 1);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Respond);
        var afterStart = new Dictionary<string, int>(fetches);

        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });

        Assert.True(added, message);
        Assert.Equal(afterStart, new Dictionary<string, int>(fetches));
    }
}
