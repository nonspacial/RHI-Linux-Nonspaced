using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M7 / m16: the library check pass reads the selection cache before it leaves the caller's thread, the
/// cache itself is safe to read from another thread, and a pass that an Update All overtakes does not note its older
/// findings (it runs again).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class UpdateCheckPassOrderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-check-order-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }

    [Fact]
    public async Task APassThatAnUpdateAllOvertakes_RunsAgain_BeforeItNotesAnything()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Alpha")]);
        var passes = 0;
        fx.Vm.Updates.CheckPassFinishedForTesting = () =>
        {
            if (++passes == 1)
            {
                fx.Vm.Updates.UpdateAllFinished(); // Update All finished while the first pass was checking
                Assert.False(fx.Vm.TopBar.AllUpToDate);
            }
            return Task.CompletedTask;
        };

        await fx.Vm.Updates.RunCheckOnlyPassAsync(CancellationToken.None);

        Assert.Equal(2, passes);
        Assert.True(fx.Vm.TopBar.AllUpToDate); // noted from the second pass
    }

    [Fact]
    public async Task TheLruCache_IsSafeToReadAndWriteFromSeveralThreads()
    {
        var cache = new LruCache<int, int>(8);
        var errors = 0;
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 20000; i++)
            {
                try
                {
                    if (i % 3 == 0) cache.Set((i + worker) % 16, i);
                    else cache.TryGet(i % 16, out _);
                    if (i % 997 == 0) cache.Remove(worker);
                }
                catch (Exception) { Interlocked.Increment(ref errors); }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(0, errors);
        Assert.InRange(cache.Count, 1, 8);
        var seen = 0;
        for (var key = 0; key < 16; key++) if (cache.TryGet(key, out _)) seen++;
        Assert.Equal(cache.Count, seen); // the list and the map agree
    }
}
