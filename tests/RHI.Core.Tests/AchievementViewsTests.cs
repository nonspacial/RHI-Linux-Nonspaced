using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review: Spotlight's achievements no longer rebuild the panel and decode every icon on each unlock. Icons load
/// once per key (shared while in flight, kept up to a bound, retried when a load fails), and a refresh reuses the views of
/// rows that did not change.
/// </summary>
public sealed class AchievementViewsTests
{
    [Fact]
    public async Task IconCache_LoadsEachKeyOnce_SharesALoadInFlight_AndEvictsTheLeastRecent()
    {
        var cache = new BoundedTaskCache<string, string>(capacity: 2);
        var loads = new List<string>();
        var gate = new TaskCompletionSource<string?>();
        Task<string?> Load(string key)
        {
            loads.Add(key);
            return key == "slow" ? gate.Task : Task.FromResult<string?>("bitmap:" + key);
        }

        var first = cache.GetOrAdd("slow", () => Load("slow"));
        var second = cache.GetOrAdd("slow", () => Load("slow")); // still loading: the same load
        Assert.Same(first, second);
        gate.SetResult("bitmap:slow");
        Assert.Equal("bitmap:slow", await second);

        Assert.Equal("bitmap:a", await cache.GetOrAdd("a", () => Load("a")));
        Assert.Equal("bitmap:a", await cache.GetOrAdd("a", () => Load("a")));
        Assert.Equal(["slow", "a"], loads);

        await cache.GetOrAdd("b", () => Load("b")); // over capacity: "slow" was used least recently
        Assert.Equal(2, cache.Count);
        await cache.GetOrAdd("slow", () => Load("slow"));
        Assert.Equal(["slow", "a", "b", "slow"], loads);
    }

    [Fact]
    public async Task IconCache_ForgetsALoadThatFailedOrFoundNothing()
    {
        var cache = new BoundedTaskCache<string, string>(capacity: 8);
        var calls = 0;
        Assert.Null(await cache.GetOrAdd("missing", () => { calls++; return Task.FromResult<string?>(null); }));
        await Assert.ThrowsAsync<IOException>(() => cache.GetOrAdd("broken", () => { calls++; return Task.FromException<string?>(new IOException()); }));
        Assert.Equal("ok", await cache.GetOrAdd("missing", () => { calls++; return Task.FromResult<string?>("ok"); }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void KeyedViews_ARefreshBuildsOnlyTheRowsThatChanged()
    {
        var kept = new Dictionary<string, object>();
        var built = new List<string>();
        object Create(string row) { built.Add(row); return new object(); }

        var before = KeyedViews.Reuse(["A locked", "B locked", "C unlocked"], ref kept, Create);
        built.Clear();
        var after = KeyedViews.Reuse(["A unlocked", "B locked", "C unlocked"], ref kept, Create); // A was just unlocked

        Assert.Equal(["A unlocked"], built);
        Assert.Same(before[1], after[1]);
        Assert.Same(before[2], after[2]);
        Assert.Equal(3, kept.Count);
    }
}
