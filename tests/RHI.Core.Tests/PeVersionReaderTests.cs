using System.Runtime.InteropServices;
using System.Text;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class PeVersionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pe-version-tests-" + Guid.NewGuid().ToString("N"));

    public PeVersionReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Read_VersionResource_ReturnsStringsAndFixedVersion()
    {
        var path = Path.Combine(_root, "renodx-test.addon64");
        File.WriteAllBytes(path, BuildVersionedPe(fileVersion: "0.2026.0325.2215", productVersion: null));

        var info = PeVersionReader.Read(path);

        Assert.NotNull(info);
        Assert.Null(info.ProductVersion);
        Assert.Equal("0.2026.0325.2215", info.FileVersion);
        Assert.Equal(new Version(0, 2026, 325, 2215), info.FixedFileVersion);
        Assert.Equal("26.0325.2215", RenoDxDeploymentService.ReadAddonVersion(path));
    }

    [Fact]
    public void ReadAddonVersion_PrefersProductVersionAndKeepsNonRenoDxShapes()
    {
        var path = Path.Combine(_root, "other.addon64");
        File.WriteAllBytes(path, BuildVersionedPe(fileVersion: "0.2026.0101.0000", productVersion: "1.4.2"));

        Assert.Equal("1.4.2", RenoDxDeploymentService.ReadAddonVersion(path));
    }

    [Fact]
    public void ReadAddonVersion_WithoutVersionResource_UsesFileTimeInUpstreamShape()
    {
        var path = Path.Combine(_root, "plain.addon64");
        PeArchitectureDetectorTests.WritePe(path, 0x8664, 4096);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 25, 22, 15, 0, DateTimeKind.Utc));

        Assert.Null(PeVersionReader.Read(path));
        Assert.Equal("26.0325.2215", RenoDxDeploymentService.ReadAddonVersion(path));
    }

    [Fact]
    public void Read_TruncatedOrForeignFile_ReturnsNull()
    {
        var full = BuildVersionedPe("0.2026.0325.2215", null);
        var truncated = Path.Combine(_root, "truncated.addon64");
        File.WriteAllBytes(truncated, full[..0x240]);
        var text = Path.Combine(_root, "error.html");
        File.WriteAllText(text, "<html>404</html>");

        Assert.Null(PeVersionReader.Read(truncated));
        Assert.Null(PeVersionReader.Read(text));
        Assert.Null(PeVersionReader.Read(Path.Combine(_root, "missing.dll")));
    }

    // Review up-129 #1: a text-typed (wType 1) root used to push the children past StringFileInfo
    // and a short root value threw out of ParseVersionInfo, losing every version string.
    [Fact]
    public void Read_RootTypedAsText_StillReturnsStringsAndFixedVersion()
    {
        var path = Path.Combine(_root, "text-root.addon64");
        File.WriteAllBytes(path, BuildVersionedPe(fileVersion: "0.2026.0325.2215", productVersion: "1.2.3", rootType: 1));

        var info = PeVersionReader.Read(path);

        Assert.NotNull(info);
        Assert.Equal(("1.2.3", "0.2026.0325.2215"), (info.ProductVersion, info.FileVersion));
        Assert.Equal(new Version(0, 2026, 325, 2215), info.FixedFileVersion);
    }

    [Fact]
    public void ParseVersionInfo_RootValueShorterThanFixedInfo_DoesNotThrow()
    {
        var shortRoot = Block("VS_VERSION_INFO", new byte[8], 1, 52, []);
        var shortTyped0 = Block("VS_VERSION_INFO", new byte[52], 0, 52, []);
        shortTyped0 = shortTyped0[..^20]; // truncated resource: the block claims more than the blob holds
        BitConverter.GetBytes((ushort)(shortTyped0.Length + 20)).CopyTo(shortTyped0, 0);

        Assert.Null(PeVersionReader.ParseVersionInfo(shortRoot));
        Assert.Null(PeVersionReader.ParseVersionInfo(shortTyped0));
    }

    // Review up-129 #8: File.Exists is true for a FIFO, and opening one blocks until a writer appears.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_Fifo_ReturnsNullWithoutOpeningIt(bool throughSymlink)
    {
        var fifo = Path.Combine(_root, "pipe.addon64");
        Assert.Equal(0, mkfifo(fifo, Convert.ToUInt32("600", 8)));
        var path = fifo;
        if (throughSymlink)
        {
            path = Path.Combine(_root, new string('l', 120) + ".addon64"); // link longer than a DOS header
            File.CreateSymbolicLink(path, fifo);
        }

        var read = Task.Run(() => PeVersionReader.Read(path));
        try
        {
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Null(await read);
        }
        finally
        {
            if (!read.IsCompleted)
            {
                using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // release the blocked reader
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Minimal PE32+ with one .rsrc section holding RT_VERSION/1/0x409.</summary>
    internal static byte[] BuildVersionedPe(string? fileVersion, string? productVersion, int size = 4096, ushort rootType = 0)
    {
        var strings = new List<byte[]>();
        if (fileVersion is not null) strings.Add(StringBlock("FileVersion", fileVersion));
        if (productVersion is not null) strings.Add(StringBlock("ProductVersion", productVersion));
        var table = Block("040904b0", [], 1, 0, strings);
        var stringInfo = Block("StringFileInfo", [], 1, 0, [table]);
        var fixedInfo = new byte[52];
        var numbers = Version.TryParse(fileVersion, out var v) ? v : new Version(0, 0, 0, 0);
        BitConverter.GetBytes(0xFEEF04BDu).CopyTo(fixedInfo, 0);
        BitConverter.GetBytes(0x00010000u).CopyTo(fixedInfo, 4);
        BitConverter.GetBytes((uint)((numbers.Major << 16) | numbers.Minor)).CopyTo(fixedInfo, 8);
        BitConverter.GetBytes((uint)((Math.Max(0, numbers.Build) << 16) | Math.Max(0, numbers.Revision))).CopyTo(fixedInfo, 12);
        var versionInfo = Block("VS_VERSION_INFO", fixedInfo, rootType, fixedInfo.Length, [stringInfo]);

        const int rsrcFile = 0x200, rsrcRva = 0x1000, blobAt = 0x58;
        var rsrc = new byte[blobAt + versionInfo.Length];
        void Dir(int at, uint id, uint target)
        {
            BitConverter.GetBytes((ushort)1).CopyTo(rsrc, at + 14);
            BitConverter.GetBytes(id).CopyTo(rsrc, at + 16);
            BitConverter.GetBytes(target).CopyTo(rsrc, at + 20);
        }
        Dir(0x00, 16, 0x80000000 | 0x18);
        Dir(0x18, 1, 0x80000000 | 0x30);
        Dir(0x30, 0x409, 0x48);
        BitConverter.GetBytes((uint)(rsrcRva + blobAt)).CopyTo(rsrc, 0x48);
        BitConverter.GetBytes((uint)versionInfo.Length).CopyTo(rsrc, 0x4C);
        versionInfo.CopyTo(rsrc, blobAt);

        var pe = new byte[Math.Max(size, rsrcFile + rsrc.Length)];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(pe, 0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(pe, 0x86);      // sections
        BitConverter.GetBytes((ushort)240).CopyTo(pe, 0x94);    // optional header size (PE32+)
        const int optional = 0x98;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(pe, optional);
        BitConverter.GetBytes(16u).CopyTo(pe, optional + 108);
        BitConverter.GetBytes((uint)rsrcRva).CopyTo(pe, optional + 112 + 16);
        BitConverter.GetBytes((uint)rsrc.Length).CopyTo(pe, optional + 112 + 20);
        const int section = optional + 240;
        ".rsrc\0\0\0"u8.ToArray().CopyTo(pe, section);
        BitConverter.GetBytes((uint)rsrc.Length).CopyTo(pe, section + 8);
        BitConverter.GetBytes((uint)rsrcRva).CopyTo(pe, section + 12);
        BitConverter.GetBytes((uint)rsrc.Length).CopyTo(pe, section + 16);
        BitConverter.GetBytes((uint)rsrcFile).CopyTo(pe, section + 20);
        rsrc.CopyTo(pe, rsrcFile);
        return pe;
    }

    private static byte[] StringBlock(string key, string value)
    {
        var text = Encoding.Unicode.GetBytes(value + "\0");
        return Block(key, text, 1, value.Length + 1, []);
    }

    private static byte[] Block(string key, byte[] value, ushort type, int valueLength, IReadOnlyList<byte[]> children)
    {
        using var body = new MemoryStream();
        body.Write(new byte[6]);
        body.Write(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(body);
        body.Write(value);
        foreach (var child in children)
        {
            Pad(body);
            body.Write(child);
        }
        var bytes = body.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)valueLength).CopyTo(bytes, 2);
        BitConverter.GetBytes(type).CopyTo(bytes, 4);
        return bytes;
    }

    private static void Pad(MemoryStream stream)
    {
        while (stream.Length % 4 != 0) stream.WriteByte(0);
    }
}
