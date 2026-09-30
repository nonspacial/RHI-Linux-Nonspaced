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
    public void Build_DoesNotFollowDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_root, "Game.exe"), "x");
        var outside = Path.Combine(Path.GetTempPath(), "rhi-index-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leak.txt"), "x");
        try
        {
            // Mirrors a Wine prefix dosdevices/z: -> / link.
            Directory.CreateSymbolicLink(Path.Combine(_root, "z:"), outside);

            var files = GameFileIndexer.Build(_root);

            Assert.Contains(Path.Combine(_root, "Game.exe"), files);
            Assert.DoesNotContain(files, f => f.EndsWith("leak.txt", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Build_SkipsDosDevicesAndHonorsFileCap()
    {
        var dosdevices = Path.Combine(_root, "dosdevices");
        Directory.CreateDirectory(dosdevices);
        File.WriteAllText(Path.Combine(dosdevices, "c.txt"), "x");
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(_root, $"f{i}.bin"), "x");

        var files = GameFileIndexer.Build(_root, maxDepth: 8, maxFiles: 4);

        Assert.Equal(4, files.Count);
        Assert.DoesNotContain(files, f => f.Contains("dosdevices", StringComparison.Ordinal));
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
