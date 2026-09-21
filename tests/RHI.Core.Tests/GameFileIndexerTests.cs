using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class GameFileIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-index-tests-" + Guid.NewGuid().ToString("N"));

    public GameFileIndexerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Build_IndexesGameFilesButSkipsRedistributables()
    {
        File.WriteAllText(Path.Combine(_root, "Game.exe"), "x");
        var redist = Path.Combine(_root, "_CommonRedist");
        Directory.CreateDirectory(redist);
        File.WriteAllText(Path.Combine(redist, "vc_redist.exe"), "x");

        var files = GameFileIndexer.Build(_root);

        Assert.Contains(Path.Combine(_root, "Game.exe"), files);
        Assert.DoesNotContain(Path.Combine(redist, "vc_redist.exe"), files);
    }

    [Fact]
    public void Build_HonorsCancellation()
    {
        File.WriteAllText(Path.Combine(_root, "Game.exe"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => GameFileIndexer.Build(_root, cancellationToken: cts.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
