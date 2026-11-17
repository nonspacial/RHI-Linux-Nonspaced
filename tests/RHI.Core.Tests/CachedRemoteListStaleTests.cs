using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review nit: a remote list older than its freshness is served at once and downloaded again in the background —
/// in memory and from the cache file at start-up — so a lookup never waits on the network while a copy exists.
/// </summary>
public sealed class CachedRemoteListStaleTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-stale-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "Alpha";
        public TaskCompletionSource? Hold;
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "text/plain"), RequestMessage = request };
        }
    }

    private CachedRemoteList<GameLinkEntry> List(Handler handler, Clock clock)
        => new("Test", GameLinkCatalogue.RoseReadmeUrl, DownloadSources.CommunityGameList,
            text => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(name => new GameLinkEntry(name, "https://example.invalid/" + name)).ToList(),
            entry => entry.Name, _ => true, Path.Combine(_root, "list.json"), TimeSpan.FromHours(4), clock, new HttpClient(handler));

    [Fact]
    public async Task AnOlderCopyInMemory_IsServedAtOnce_AndReplacedInTheBackground()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var handler = new Handler();
        var list = List(handler, clock);
        await list.LoadAsync();
        Assert.Equal(["Alpha"], list.Entries.Select(e => e.Name));

        clock.Now += TimeSpan.FromHours(5);
        handler.Body = "Beta";
        handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = list.LoadAsync();

        Assert.True(load.IsCompleted); // nothing to wait for: the older copy is there
        Assert.Equal(["Alpha"], list.Entries.Select(e => e.Name));
        handler.Hold.SetResult();
        await WaitForAsync(() => list.Entries.Any(e => e.Name == "Beta"));
    }

    [Fact]
    public async Task AnOlderCopyOnDisk_IsServedAtStart_AndReplacedInTheBackground()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var first = new Handler();
        await List(first, clock).LoadAsync();

        clock.Now += TimeSpan.FromHours(5);
        var second = new Handler { Body = "Beta", Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var list = List(second, clock);

        await list.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10)); // the network is held: only the disk copy can answer

        Assert.Equal(["Alpha"], list.Entries.Select(e => e.Name));
        await WaitForAsync(() => second.Requests > 0);
        second.Hold!.SetResult();
        await WaitForAsync(() => list.Entries.Any(e => e.Name == "Beta"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("timed out");
            await Task.Delay(20);
        }
    }
}
