using System.IO.Compression;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>ReShade setup extraction without a host 7-Zip/bsdtar (#3).</summary>
public sealed class ReShadeArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-extract-" + Guid.NewGuid().ToString("N"));
    private int _externalLookups;

    public ReShadeArchiveExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task SetupWithAppendedZip_ExtractsBothDlls_WithoutAHostTool()
    {
        // Like ReShade_Setup_*.exe: a PE stub, then a ZIP whose offsets count from the ZIP's own start.
        var dll64 = Payload(0x40, 70_000);
        var dll32 = Payload(0x20, 50_000);
        var setup = Write("ReShade_Setup_6.8.0_Addon.exe",
            [.. PeStub(150_000), .. Zip(("ReShade64.dll", dll64), ("ReShade32.dll", dll32), ("ReShade64.json", "{}"u8.ToArray()))]);
        var output = Path.Combine(_root, "out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        Assert.Equal(dll32, File.ReadAllBytes(Path.Combine(output, "ReShade32.dll")));
        Assert.False(File.Exists(Path.Combine(output, "ReShade64.json")));
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task SignedSetup_WithACertificateAfterTheZip_StillExtracts()
    {
        // The real 6.8.0 setup carries ~8 KB of Authenticode data after the ZIP's end record.
        var dll64 = Payload(0x33, 30_000);
        var setup = Write("signed.exe", [.. PeStub(150_000), .. Zip(("ReShade64.dll", dll64)), .. Payload(0x77, 7_965)]);
        var output = Path.Combine(_root, "signed-out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task PlainZip_StillExtracts()
    {
        var dll64 = Payload(0x11, 4_096);
        var setup = Write("plain.zip", Zip(("bin/ReShade64.dll", dll64)));
        var output = Path.Combine(_root, "plain-out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task NotAnArchive_WithoutAHostTool_SaysWhichToolsWouldHelp()
    {
        var setup = Write("broken.exe", PeStub(64_000));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NoHostTool().ExtractDllsAsync(setup, Path.Combine(_root, "x")));

        Assert.Contains("no ZIP archive found", error.Message);
        Assert.Contains("7z/7zz/7za or bsdtar", error.Message);
        Assert.Equal(1, _externalLookups);
    }

    [Fact]
    public async Task ArchiveWithoutReShade_IsReportedAsSuch()
    {
        var setup = Write("other.exe", [.. PeStub(10_000), .. Zip(("readme.txt", "hi"u8.ToArray()))]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NoHostTool().ExtractDllsAsync(setup, Path.Combine(_root, "y")));

        Assert.Contains("no ReShade64.dll or ReShade32.dll", error.Message);
    }

    [Fact]
    public void FindZipStart_ReturnsThePrefixLength()
    {
        var prefix = PeStub(12_345);
        using var stream = new MemoryStream([.. prefix, .. Zip(("ReShade64.dll", Payload(1, 100)))]);

        Assert.Equal(prefix.Length, ReShadeArchiveExtractor.FindZipStart(stream));
    }

    private ReShadeArchiveExtractor NoHostTool() => new(() =>
    {
        _externalLookups++;
        return null;
    });

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] PeStub(int size)
    {
        var bytes = Payload(0x5A, size);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static byte[] Payload(byte marker, int size) => Enumerable.Range(0, size).Select(i => (byte)(marker + i * 13)).ToArray();

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        return ms.ToArray();
    }
}
