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

    // Re-review nit 2 (#142): a derived entry's binding is required, so a blank expected hash is a
    // rejection, never a skipped comparison (IsValid skips a blank published digest by design).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BoundEntry_BlankExpectedHash_IsRejectedNotSkipped(string? blank)
    {
        var tree = Path.Combine(_root, "extract");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "OptiScaler.dll"), [7, 8, 9]);
        CacheStamp.WriteBoundTo(tree, "https://example.test/o.7z", "v1", "AA11");

        Assert.True(CacheStamp.IsValid(tree, "https://example.test/o.7z", "v1", blank));
        Assert.False(CacheStamp.IsValidBoundTo(tree, "https://example.test/o.7z", "v1", blank));
        Assert.Throws<ArgumentException>(() => CacheStamp.WriteBoundTo(tree, "https://example.test/o.7z", "v1", blank));
    }

    [Fact]
    public void BoundEntry_IsValidOnlyForTheHashItWasBoundTo()
    {
        var tree = Path.Combine(_root, "extract-b");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "OptiScaler.dll"), [7, 8, 9]);

        CacheStamp.Write(tree, "https://example.test/o.7z", "v1");
        Assert.False(CacheStamp.IsValidBoundTo(tree, "https://example.test/o.7z", "v1", "AA11"));

        CacheStamp.WriteBoundTo(tree, "https://example.test/o.7z", "v1", "AA11");
        Assert.True(CacheStamp.IsValidBoundTo(tree, "https://example.test/o.7z", "v1", "aa11"));
        Assert.False(CacheStamp.IsValidBoundTo(tree, "https://example.test/o.7z", "v1", "BB22"));
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
