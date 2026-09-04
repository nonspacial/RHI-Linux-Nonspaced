using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class TransactionalFileDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-linux-tests-" + Guid.NewGuid().ToString("N"));

    public TransactionalFileDeploymentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Replace_PreservesTrueOriginal_AndRestoreReturnsIt()
    {
        var source1 = Path.Combine(_root, "new1.dll");
        var source2 = Path.Combine(_root, "new2.dll");
        var target = Path.Combine(_root, "nvngx_dlss.dll");
        await File.WriteAllTextAsync(source1, "replacement one");
        await File.WriteAllTextAsync(source2, "replacement two");
        await File.WriteAllTextAsync(target, "game original");

        var service = new TransactionalFileDeploymentService();
        Assert.True((await service.ReplaceAsync(source1, target)).Success);
        Assert.True((await service.ReplaceAsync(source2, target)).Success);
        Assert.Equal("game original", await File.ReadAllTextAsync(target + ".original"));
        Assert.Equal("replacement two", await File.ReadAllTextAsync(target));

        Assert.True((await service.RestoreAsync(target)).Success);
        Assert.Equal("game original", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".original"));
    }

    [Fact]
    public async Task ReplaceBatch_WhenSecondSourceMissing_RollsBackFirstTarget()
    {
        var source = Path.Combine(_root, "source.dll");
        var target1 = Path.Combine(_root, "one.dll");
        var target2 = Path.Combine(_root, "two.dll");
        await File.WriteAllTextAsync(source, "replacement");
        await File.WriteAllTextAsync(target1, "original one");
        await File.WriteAllTextAsync(target2, "original two");

        var service = new TransactionalFileDeploymentService();
        var result = await service.ReplaceBatchAsync(new[]
        {
            (source, target1),
            (Path.Combine(_root, "missing.dll"), target2)
        });

        Assert.False(result.Success);
        Assert.Equal("original one", await File.ReadAllTextAsync(target1));
        Assert.Equal("original two", await File.ReadAllTextAsync(target2));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
