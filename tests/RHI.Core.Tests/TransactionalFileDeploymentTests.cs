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

    [Fact]
    public async Task RestoreBatch_RestoresEveryTargetAndConsumesBackups()
    {
        var source = Path.Combine(_root, "new.dll");
        var target1 = Path.Combine(_root, "sl.common.dll");
        var target2 = Path.Combine(_root, "sl.reflex.dll");
        await File.WriteAllTextAsync(source, "replacement");
        await File.WriteAllTextAsync(target1, "original one");
        await File.WriteAllTextAsync(target2, "original two");

        var service = new TransactionalFileDeploymentService();
        Assert.True((await service.ReplaceBatchAsync(new[] { (source, target1), (source, target2) })).Success);

        var restored = await service.RestoreBatchAsync(new[] { target1, target2 });

        Assert.True(restored.Success);
        Assert.Equal("original one", await File.ReadAllTextAsync(target1));
        Assert.Equal("original two", await File.ReadAllTextAsync(target2));
        Assert.False(File.Exists(target1 + ".original"));
        Assert.False(File.Exists(target2 + ".original"));
    }

    [Fact]
    public async Task RestoreBatch_MissingBackupRefusesBeforeChangingOtherTargets()
    {
        var source = Path.Combine(_root, "new-restore.dll");
        var target1 = Path.Combine(_root, "managed.dll");
        var target2 = Path.Combine(_root, "unmanaged.dll");
        await File.WriteAllTextAsync(source, "replacement");
        await File.WriteAllTextAsync(target1, "original managed");
        await File.WriteAllTextAsync(target2, "external current");

        var service = new TransactionalFileDeploymentService();
        Assert.True((await service.ReplaceAsync(source, target1)).Success);
        var before = await File.ReadAllTextAsync(target1);

        var restored = await service.RestoreBatchAsync(new[] { target1, target2 });

        Assert.False(restored.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(target1));
        Assert.Equal("external current", await File.ReadAllTextAsync(target2));
        Assert.True(File.Exists(target1 + ".original"));
    }


    [Fact]
    public async Task Replace_IdenticalSource_IsNoOpWithoutBackup()
    {
        var source = Path.Combine(_root, "same-source.dll");
        var target = Path.Combine(_root, "same-target.dll");
        var payload = Enumerable.Repeat((byte)0x5A, 96_000).ToArray();
        await File.WriteAllBytesAsync(source, payload);
        await File.WriteAllBytesAsync(target, payload);

        var service = new TransactionalFileDeploymentService();
        var result = await service.ReplaceAsync(source, target);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.False(File.Exists(target + ".original"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Restore_PreservesBackupUntilVerified_AndReturnsExactBytes()
    {
        var source = Path.Combine(_root, "verified-new.dll");
        var target = Path.Combine(_root, "verified-target.dll");
        var original = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        var replacement = Enumerable.Repeat((byte)0xA7, 110_000).ToArray();
        await File.WriteAllBytesAsync(source, replacement);
        await File.WriteAllBytesAsync(target, original);

        var service = new TransactionalFileDeploymentService();
        var replaced = await service.ReplaceAsync(source, target);
        Assert.True(replaced.Success);
        Assert.True(replaced.Changed);
        Assert.True(File.Exists(target + ".original"));

        var restored = await service.RestoreAsync(target);

        Assert.True(restored.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".original"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
