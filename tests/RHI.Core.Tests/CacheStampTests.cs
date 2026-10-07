using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class CacheStampTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-cache-stamp-tests-" + Guid.NewGuid().ToString("N"));

    public CacheStampTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void File_WithoutStamp_IsNotValid()
    {
        var file = Path.Combine(_root, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);

        Assert.False(CacheStamp.IsValid(file, "https://example.test/a"));
    }

    [Fact]
    public void File_StampedThenUnchanged_IsValid_ForSameSourceAndVersionOnly()
    {
        var file = Path.Combine(_root, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);
        CacheStamp.Write(file, "https://example.test/a", "1.0", "ABC");

        Assert.True(CacheStamp.IsValid(file, "https://example.test/a", "1.0"));
        Assert.True(CacheStamp.IsValid(file, "https://example.test/a", "1.0", "abc"));
        Assert.False(CacheStamp.IsValid(file, "https://example.test/b", "1.0"));
        Assert.False(CacheStamp.IsValid(file, "https://example.test/a", "2.0"));
        Assert.False(CacheStamp.IsValid(file, "https://example.test/a", "1.0", "DEF"));
    }

    [Fact]
    public void TryValidate_HandsBackTheVerifiedHashThatWriteRecorded()
    {
        var file = Path.Combine(_root, "h.dll");
        File.WriteAllBytes(file, [4, 5, 6]);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 4, 5, 6 }));

        var written = CacheStamp.Write(file, "https://example.test/h", "1.0");

        Assert.Equal(expected, written, ignoreCase: true);
        Assert.True(CacheStamp.TryValidate(file, "https://example.test/h", "1.0", null, out var verified));
        Assert.Equal(expected, verified, ignoreCase: true);

        File.WriteAllBytes(file, [4, 5, 7]);
        Assert.False(CacheStamp.TryValidate(file, "https://example.test/h", "1.0", null, out var none));
        Assert.Equal("", none);
    }

    [Fact]
    public void File_ReplacedAfterStamping_IsNotValid()
    {
        var file = Path.Combine(_root, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);
        CacheStamp.Write(file, "https://example.test/a");

        File.WriteAllBytes(file, [1, 2, 4]); // same size, different content
        Assert.False(CacheStamp.IsValid(file, "https://example.test/a"));
    }

    [Fact]
    public void Invalidate_RemovesStamp()
    {
        var file = Path.Combine(_root, "a.dll");
        File.WriteAllBytes(file, [1]);
        CacheStamp.Write(file, "u");
        CacheStamp.Invalidate(file);

        Assert.False(File.Exists(CacheStamp.StampPath(file)));
        Assert.False(CacheStamp.IsValid(file, "u"));
    }

    [Fact]
    public void CorruptStamp_IsNotValid()
    {
        var file = Path.Combine(_root, "a.dll");
        File.WriteAllBytes(file, [1]);
        File.WriteAllText(CacheStamp.StampPath(file), "{not json");

        Assert.False(CacheStamp.IsValid(file, "u"));
    }

    [Fact]
    public void Directory_TreeChanges_InvalidateStamp()
    {
        var dir = Path.Combine(_root, "extract");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "OptiScaler.dll"), "genuine");
        CacheStamp.Write(dir, "https://example.test/pkg", "v1");
        Assert.True(CacheStamp.IsValid(dir, "https://example.test/pkg", "v1"));

        File.WriteAllText(Path.Combine(dir, "extra.dll"), "planted");
        Assert.False(CacheStamp.IsValid(dir, "https://example.test/pkg", "v1"));
        File.Delete(Path.Combine(dir, "extra.dll"));
        Assert.True(CacheStamp.IsValid(dir, "https://example.test/pkg", "v1"));

        File.WriteAllText(Path.Combine(dir, "sub", "OptiScaler.dll"), "swapped");
        Assert.False(CacheStamp.IsValid(dir, "https://example.test/pkg", "v1"));
    }

    [Fact]
    public void Directory_WithSymlink_IsNotValid()
    {
        var dir = Path.Combine(_root, "extract");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        CacheStamp.Write(dir, "u");
        File.CreateSymbolicLink(Path.Combine(dir, "link"), "/etc/hostname");

        Assert.False(CacheStamp.IsValid(dir, "u"));
    }
}
