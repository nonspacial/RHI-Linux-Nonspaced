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

    [Fact]
    public async Task Restore_TouchesOnlyTargetAndOriginal_LeavesUnownedSidecarsAlone()
    {
        // #40: restore used to delete "<target>.rhi_custom", a file nothing in RHI creates.
        var source = Path.Combine(_root, "sidecar-new.dll");
        var target = Path.Combine(_root, "sidecar-target.dll");
        var sidecar = target + ".rhi_custom";
        var unrelated = Path.Combine(_root, "unrelated.txt");
        await File.WriteAllTextAsync(source, "replacement");
        await File.WriteAllTextAsync(target, "original");
        await File.WriteAllTextAsync(sidecar, "user data");
        await File.WriteAllTextAsync(unrelated, "other");

        var service = new TransactionalFileDeploymentService();
        Assert.True((await service.ReplaceAsync(source, target)).Success);
        var before = Directory.GetFiles(_root).Order(StringComparer.Ordinal).ToArray();

        Assert.True((await service.RestoreAsync(target)).Success);

        var after = Directory.GetFiles(_root).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(before.Where(path => path != target + ".original"), after);
        Assert.Equal("user data", await File.ReadAllTextAsync(sidecar));
        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    // #33 (DEP-14). The previous implementation read about 11 MiB and wrote 3 MiB for this
    // replacement (two hash-copy-hash copies of the target, one of the source, a 2-read post-verify;
    // 13 MiB when the lengths match and FilesEqual hashes both first).
    [Fact]
    public async Task Replace_ExistingTargetWithoutBackup_ReadsEachFileOnceAndWritesOnce()
    {
        const int MiB = 1024 * 1024;
        var source = Path.Combine(_root, "io-source.dll");
        var target = Path.Combine(_root, "io-target.dll");
        await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)0x31, MiB).ToArray());
        await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0x42, MiB + 17).ToArray());

        FileDeploymentResult result;
        using (var meter = RHI.Core.Deployment.IoMeter.Begin())
        {
            result = await new TransactionalFileDeploymentService().ReplaceAsync(source, target);
            // target pre-state hash + source read while copying + verification of the staged copy
            Assert.Equal(3L * MiB + 17, meter.BytesRead);
            Assert.Equal(MiB, meter.BytesWritten);
        }

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))), result.Sha256);
        Assert.Equal(MiB + 17, new FileInfo(target + ".original").Length);
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.Contains(".rhi-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceBatch_HashesSharedSourceOnce_AndRestoreBatchCopiesNothing()
    {
        const int Size = 256 * 1024;
        var source = Path.Combine(_root, "batch-source.dll");
        await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)0x55, Size).ToArray());
        var targets = Enumerable.Range(0, 3).Select(i => Path.Combine(_root, $"batch-{i}.dll")).ToArray();
        foreach (var target in targets)
            await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0x10, Size).ToArray()); // same length: no-op check runs

        var service = new TransactionalFileDeploymentService();
        using (var meter = RHI.Core.Deployment.IoMeter.Begin())
        {
            Assert.True((await service.ReplaceBatchAsync(targets.Select(t => (source, t)))).Success);
            // source hashed once for the no-op check, then per target: target hash, copy read, verify read
            Assert.Equal((1 + 3 * 3) * (long)Size, meter.BytesRead);
            Assert.Equal(3L * Size, meter.BytesWritten);
        }

        using (var meter = RHI.Core.Deployment.IoMeter.Begin())
        {
            Assert.True((await service.RestoreBatchAsync(targets)).Success);
            // per target: .original hash + managed-file pre-state hash; restoring is a rename
            Assert.Equal(3 * 2 * (long)Size, meter.BytesRead);
            Assert.Equal(0, meter.BytesWritten);
        }

        foreach (var target in targets)
        {
            Assert.Equal(Enumerable.Repeat((byte)0x10, Size).ToArray(), await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".original"));
        }
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.Contains(".rhi-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceBatch_FailureAfterEarlierSwaps_RestoresEveryTargetAndOriginalExactly()
    {
        var source = Path.Combine(_root, "rb-source.dll");
        var managed = Path.Combine(_root, "rb-managed.dll");
        var fresh = Path.Combine(_root, "rb-fresh.dll");
        await File.WriteAllTextAsync(source, "new payload");
        await File.WriteAllTextAsync(managed, "managed v1");
        await File.WriteAllTextAsync(managed + ".original", "true original");
        await File.WriteAllTextAsync(fresh, "game file");

        var result = await new TransactionalFileDeploymentService().ReplaceBatchAsync(new[]
        {
            (source, managed),
            (source, fresh),
            (Path.Combine(_root, "rb-missing.dll"), Path.Combine(_root, "rb-third.dll"))
        });

        Assert.False(result.Success);
        Assert.Equal("managed v1", await File.ReadAllTextAsync(managed));
        Assert.Equal("true original", await File.ReadAllTextAsync(managed + ".original"));
        Assert.Equal("game file", await File.ReadAllTextAsync(fresh));
        Assert.False(File.Exists(fresh + ".original"));
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.Contains(".rhi-", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
