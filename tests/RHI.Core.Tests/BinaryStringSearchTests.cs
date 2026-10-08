using System.Text;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>#34 (DEP-15): binary sniffing streams through one pooled window, stops on the first hit and is cached.</summary>
public sealed class BinaryStringSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-binary-search-" + Guid.NewGuid().ToString("N"));

    public BinaryStringSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public void LargeFile_IsScannedWithoutWholeFileBuffers_AndAStringPast32MiBIsFound()
    {
        // 40 MiB of filler with the signature at the very end: the old reader stopped at 32 MiB and allocated
        // about 128 MB (byte array plus Latin-1 and UTF-16 strings) on every call.
        var path = Path.Combine(_root, "dxgi.dll");
        using (var file = File.Create(path))
        {
            file.Write("MZ"u8);
            var filler = new byte[1024 * 1024];
            Array.Fill(filler, (byte)0x90);
            for (var i = 0; i < 40; i++) file.Write(filler);
            file.Write(Encoding.Unicode.GetBytes("ReShade by crosire"));
        }

        BinarySignatureInspector.LooksLikeReShade(Path.Combine(_root, "warm-up-missing.dll")); // JIT, pool warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = BinarySignatureInspector.LooksLikeReShade(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(found);
        Assert.True(allocated < 4L * 1024 * 1024, $"allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(BinaryStringSearch.WindowBytes - 3)] // split across two reads
    [InlineData(BinaryStringSearch.WindowBytes - 1)]
    [InlineData(BinaryStringSearch.WindowBytes)]
    public void StringSplitAcrossReadWindows_IsFound(int offset)
    {
        var path = Path.Combine(_root, $"split-{offset}.dll");
        var bytes = new byte[offset + 64];
        Encoding.ASCII.GetBytes("OptiScaler").CopyTo(bytes, offset);
        File.WriteAllBytes(path, bytes);

        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(path));
    }

    [Fact]
    public void Matching_IgnoresAsciiCase_InBothEncodings()
    {
        var ascii = Path.Combine(_root, "ascii.dll");
        File.WriteAllBytes(ascii, Encoding.ASCII.GetBytes("xx RESHADE.ME yy"));
        var wide = Path.Combine(_root, "wide.dll");
        File.WriteAllBytes(wide, Encoding.Unicode.GetBytes("xx renodx yy"));
        var none = Path.Combine(_root, "none.dll");
        File.WriteAllBytes(none, Encoding.ASCII.GetBytes("R e S h a d e"));

        Assert.True(BinarySignatureInspector.LooksLikeReShade(ascii));
        Assert.True(BinaryStringSearch.ContainsAny(wide, "RenoDX"));
        Assert.False(BinarySignatureInspector.LooksLikeReShade(none));
    }

    [Fact]
    public void SameFileVersion_IsReadOnce_AndARewriteIsReadAgain()
    {
        var path = Path.Combine(_root, "cached.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("nothing here"));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(BinaryStringSearch.ContainsAny(path, "UniqueNeedleForCacheTest"));
        var scans = BinaryStringSearch.ScansForTesting;
        Assert.False(BinaryStringSearch.ContainsAny(path, "UniqueNeedleForCacheTest"));
        Assert.Equal(scans, BinaryStringSearch.ScansForTesting);

        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("UniqueNeedleForCacheTest"));
        Assert.True(BinaryStringSearch.ContainsAny(path, "UniqueNeedleForCacheTest"));
    }

    [Fact]
    public void SameSizeRewriteWithTheOldModificationTime_IsReadAgain()
    {
        // Review w2-trust minor 3: an in-place rewrite of equal length whose mtime is set back (touch -d) kept
        // (path, length, mtime) and returned the stale answer; the ctime cannot be set back.
        var path = Path.Combine(_root, "same-size.dll");
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("xx-nothing-of-note-here-xx"));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.False(BinaryStringSearch.ContainsAny(path, "SameSizeNeedle"));

        Thread.Sleep(50); // past the file system's timestamp granularity
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.Write(Encoding.ASCII.GetBytes("xx-SameSizeNeedle-there-xx"));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(26, new FileInfo(path).Length);
        Assert.True(BinaryStringSearch.ContainsAny(path, "SameSizeNeedle"));
    }

    [Fact]
    public void SameSizeReplacementKeepingTheModificationTime_IsReadAgain()
    {
        // cp -p / .NET File.Copy + rename: another file of the same length and mtime takes the path.
        var path = Path.Combine(_root, "replaced.dll");
        var stamp = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("yy-nothing-of-note-here-yy"));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.False(BinaryStringSearch.ContainsAny(path, "ReplacementNeedle"));

        Thread.Sleep(50);
        var staged = Path.Combine(_root, "staged.dll");
        File.WriteAllBytes(staged, Encoding.ASCII.GetBytes("yy-ReplacementNeedle-yy-yy"));
        File.SetLastWriteTimeUtc(staged, stamp);
        File.Move(staged, path, overwrite: true);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.True(BinaryStringSearch.ContainsAny(path, "ReplacementNeedle"));
    }

    [Fact]
    public void SameSizeRewriteBehindASymlink_IsReadAgain()
    {
        // The ctime is the linked file's (what the scan reads), not the link's own, which a rewrite never moves.
        var target = Path.Combine(_root, "real-compiler.dll");
        var link = Path.Combine(_root, "linked-compiler.dll");
        var stamp = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(target, Encoding.ASCII.GetBytes("zz-nothing-of-note-here-zz"));
        File.SetLastWriteTimeUtc(target, stamp);
        File.CreateSymbolicLink(link, target);
        Assert.False(BinaryStringSearch.ContainsAny(link, "LinkedNeedle"));

        Thread.Sleep(50);
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write))
            stream.Write(Encoding.ASCII.GetBytes("zz-LinkedNeedle-there-zzzz"));
        File.SetLastWriteTimeUtc(target, stamp);

        Assert.True(BinaryStringSearch.ContainsAny(link, "LinkedNeedle"));
    }

    [Fact]
    public void Find_ReportsWhichNeedlesArePresent_AndStopsWhenTheRuleIsMet()
    {
        var path = Path.Combine(_root, "compiler.dll");
        File.WriteAllBytes(path, [.. Encoding.Unicode.GetBytes("Microsoft Corporation"), 0, 0, .. Encoding.ASCII.GetBytes("D3DCompiler_47.dll")]);

        var found = BinaryStringSearch.Find(path, ["Microsoft", "HLSL Shader Compiler", "D3DCompiler_47"]);

        Assert.Equal(new HashSet<int> { 0, 2 }, found.ToHashSet());
        Assert.Empty(BinaryStringSearch.Find(Path.Combine(_root, "missing.dll"), ["Microsoft"]));
    }
}
