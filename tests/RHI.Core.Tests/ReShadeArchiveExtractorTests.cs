using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RHI.Core.Deployment;
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
            [.. PeStub(150_000), .. Zip(Entry("ReShade64.dll", dll64), Entry("ReShade32.dll", dll32), Entry("ReShade64.json", "{}"u8.ToArray()))]);
        var output = Path.Combine(_root, "out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        Assert.Equal(dll32, File.ReadAllBytes(Path.Combine(output, "ReShade32.dll")));
        Assert.Equal(["ReShade32.dll", "ReShade64.dll"], Directory.GetFiles(output).Select(Path.GetFileName).Order());
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task SignedSetup_WithACertificateAfterTheZip_StillExtracts()
    {
        // The real 6.8.0 setup carries ~8 KB of Authenticode data after the ZIP's end record.
        var dll64 = Payload(0x33, 30_000);
        var setup = Write("signed.exe", [.. PeStub(150_000), .. Zip(Entry("ReShade64.dll", dll64)), .. Payload(0x77, 7_965)]);
        var output = Path.Combine(_root, "signed-out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task SignedSetup_WhoseCertificateHoldsADecoyEndRecord_StillExtracts()
    {
        // Authenticode bytes can contain "PK\x05\x06" by chance. The decoy points at a "central
        // directory" with no directory header there, so only the real end record may be used, and
        // the ZIP reader must not see the trailer at all (it would pick the decoy itself).
        var dll64 = Payload(0x35, 20_000);
        var prefix = PeStub(40_000);
        var zip = Zip(Entry("ReShade64.dll", dll64));
        var setup = Write("decoy.exe", [.. prefix, .. zip, .. Payload(0x77, 3_000), .. DecoyEndRecord(directorySize: 100), .. Payload(0x78, 1_000)]);
        var output = Path.Combine(_root, "decoy-out");

        await NoHostTool().ExtractDllsAsync(setup, output);

        Assert.Equal(dll64, File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
        using var stream = File.OpenRead(setup);
        Assert.Equal(((long)prefix.Length, (long)(prefix.Length + zip.Length)), ReShadeArchiveExtractor.FindZip(stream));
        Assert.Equal(0, _externalLookups);
    }

    [Fact]
    public async Task PlainZip_StillExtracts()
    {
        var dll64 = Payload(0x11, 4_096);
        var setup = Write("plain.zip", Zip(Entry("bin/ReShade64.dll", dll64)));
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
        var setup = Write("other.exe", [.. PeStub(10_000), .. Zip(Entry("readme.txt", "hi"u8.ToArray()))]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NoHostTool().ExtractDllsAsync(setup, Path.Combine(_root, "y")));

        Assert.Contains("no ReShade64.dll or ReShade32.dll", error.Message);
    }

    [Fact]
    public async Task CorruptCentralDirectory_ReturnsAReason_AndTriesTheHostTool()
    {
        // The first directory header is intact (so the ZIP is found); the second is not. The
        // directory is read lazily, so this used to escape as InvalidDataException.
        var zip = Zip(Entry("ReShade64.dll", Payload(1, 5_000)), Entry("ReShade32.dll", Payload(2, 5_000)));
        zip[DirectoryHeaderOffset(zip, "ReShade32.dll")] = (byte)'X';
        var setup = Write("corrupt-dir.exe", [.. PeStub(20_000), .. zip]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NoHostTool().ExtractDllsAsync(setup, Path.Combine(_root, "cd")));

        Assert.StartsWith(ReShadeArchiveExtractor.SetupUnreadablePrefix, error.Message);
        Assert.Equal(1, _externalLookups);
    }

    [Fact]
    public void SetupUnreadableError_HasAPortuguesePresentation_KeepingTheReason()
    {
        // A reason RHI has no text for (exception text) is kept as is; the extractor's own reasons are translated too.
        const string reason = "E/S negada";
        var shown = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", ReShadeArchiveExtractor.SetupUnreadablePrefix + reason);

        Assert.StartsWith("O instalador do ReShade não pôde ser lido", shown);
        Assert.EndsWith(reason, shown);
        Assert.Equal(ReShadeArchiveExtractor.SetupUnreadablePrefix + reason,
            RHI.Desktop.Localization.UiLocalizationService.PresentFor("en-US", ReShadeArchiveExtractor.SetupUnreadablePrefix + reason));
        Assert.EndsWith("Motivo: nenhum arquivo ZIP encontrado no instalador",
            RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", ReShadeArchiveExtractor.SetupUnreadablePrefix + "no ZIP archive found in the setup"));
    }

    [Fact]
    public async Task EntryDeclaringMoreThanTheCap_IsRefusedBeforeWriting()
    {
        // A 310 KB setup could otherwise expand to hundreds of MiB (or a declared 4 GiB) in the cache.
        var zip = Zip(Entry("ReShade64.dll", Payload(3, 4_000)));
        SetDeclaredSize(zip, "ReShade64.dll", (uint)(ReShadeArchiveExtractor.MaxEntryBytes + 1));
        var setup = Write("huge.exe", [.. PeStub(20_000), .. zip]);
        var output = Path.Combine(_root, "huge");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NoHostTool().ExtractDllsAsync(setup, output));

        Assert.Contains("ReShade64.dll", error.Message);
        Assert.Contains("limit", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task EntriesTogetherAboveTheTotalCap_AreRefusedBeforeWritingAnything()
    {
        var each = (uint)(ReShadeArchiveExtractor.MaxTotalBytes / 2 + 1);
        Assert.True(each <= ReShadeArchiveExtractor.MaxEntryBytes);
        var zip = Zip(Entry("ReShade64.dll", Payload(4, 4_000)), Entry("ReShade32.dll", Payload(5, 4_000)));
        SetDeclaredSize(zip, "ReShade64.dll", each);
        SetDeclaredSize(zip, "ReShade32.dll", each);
        var setup = Write("total.exe", [.. PeStub(20_000), .. zip]);
        var output = Path.Combine(_root, "total");
        Directory.CreateDirectory(output);

        var reason = await ReShadeArchiveExtractor.TryExtractManagedAsync(setup, output, CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("together", reason);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task AlteredPayloadBytes_FailTheCrcCheck_AndLeaveNoFile()
    {
        var dll64 = Payload(0x44, 9_000);
        var zip = Zip(CompressionLevel.NoCompression, Entry("ReShade64.dll", dll64));
        var at = zip.AsSpan().IndexOf(dll64);
        Assert.True(at > 0);
        zip[at + 4_321] ^= 0x01;
        var setup = Write("crc.exe", [.. PeStub(20_000), .. zip]);
        var output = Path.Combine(_root, "crc");
        Directory.CreateDirectory(output);

        var reason = await ReShadeArchiveExtractor.TryExtractManagedAsync(setup, output, CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("CRC", reason);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task ShorterThanDeclared_FailsTheLengthCheck_AndLeavesNoFile()
    {
        var zip = Zip(Entry("ReShade64.dll", Payload(0x45, 9_000)));
        SetDeclaredSize(zip, "ReShade64.dll", 9_000 + 512);
        var setup = Write("short.exe", [.. PeStub(20_000), .. zip]);
        var output = Path.Combine(_root, "short");
        Directory.CreateDirectory(output);

        var reason = await ReShadeArchiveExtractor.TryExtractManagedAsync(setup, output, CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("9000", reason);
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task ArchiveUnixModes_AreNeverApplied()
    {
        // A write-only or executable mode in the archive must not reach the cache: files get 0644 less umask.
        var setup = Write("modes.exe", [.. PeStub(20_000), .. Zip(
            Entry("ReShade64.dll", Payload(6, 3_000), unixMode: 0b010_000_000),
            Entry("ReShade32.dll", Payload(7, 3_000), unixMode: 0b111_111_111))]);
        var output = Path.Combine(_root, "modes");
        Directory.CreateDirectory(output);

        Assert.Null(await ReShadeArchiveExtractor.TryExtractManagedAsync(setup, output, CancellationToken.None));

        const UnixFileMode forbidden = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
            | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll" })
        {
            var mode = File.GetUnixFileMode(Path.Combine(output, name));
            Assert.True(mode.HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite), $"{name}: {mode}");
            Assert.Equal(UnixFileMode.None, mode & forbidden);
        }
        Assert.Equal(Payload(6, 3_000), File.ReadAllBytes(Path.Combine(output, "ReShade64.dll")));
    }

    [Fact]
    public async Task Cancellation_IsHonoured_AndDoesNotFallBackToTheHostTool()
    {
        var setup = Write("cancel.exe", [.. PeStub(20_000), .. Zip(Entry("ReShade64.dll", Payload(8, 200_000)))]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHostTool().ExtractDllsAsync(setup, Path.Combine(_root, "cancel"), cts.Token));

        Assert.Equal(0, _externalLookups);
        Assert.False(File.Exists(Path.Combine(_root, "cancel", "ReShade64.dll")));
    }

    [Fact]
    public async Task BitFlipsAndTruncations_EitherFailWithAReason_OrYieldTheExactPayload()
    {
        // Regression for the review fuzz: exceptions escaped from the lazy directory read, and
        // corrupted payloads "succeeded" with no CRC check.
        var dll64 = Payload(0x50, 6_000);
        var dll32 = Payload(0x51, 4_000);
        byte[] original = [.. PeStub(2_000), .. Zip(CompressionLevel.NoCompression, Entry("ReShade64.dll", dll64), Entry("ReShade32.dll", dll32))];
        var random = new Random(20261007);
        for (var i = 0; i < 400; i++)
        {
            var bytes = (byte[])original.Clone();
            if (i % 5 == 4) bytes = bytes[..random.Next(1, bytes.Length)];
            else bytes[random.Next(2_000, bytes.Length)] ^= (byte)(1 << random.Next(8));
            var setup = Write($"fuzz-{i}.exe", bytes);
            var output = Path.Combine(_root, $"fuzz-{i}");
            Directory.CreateDirectory(output);

            var reason = await ReShadeArchiveExtractor.TryExtractManagedAsync(setup, output, CancellationToken.None);

            foreach (var (name, expected) in new[] { ("ReShade64.dll", dll64), ("ReShade32.dll", dll32) })
            {
                var path = Path.Combine(output, name);
                if (File.Exists(path)) Assert.Equal(expected, File.ReadAllBytes(path));
            }
            if (reason is null) Assert.NotEmpty(Directory.GetFiles(output));
            Assert.DoesNotContain(Directory.GetFiles(output), f => f.EndsWith(".tmp", StringComparison.Ordinal) || f.Contains(AtomicFile.TempMarker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FindZip_ReturnsThePrefixLengthAndTheEndOfTheEndRecord()
    {
        var prefix = PeStub(12_345);
        var zip = Zip(Entry("ReShade64.dll", Payload(1, 100)));
        using var stream = new MemoryStream([.. prefix, .. zip, .. Payload(9, 50)]);

        Assert.Equal(((long)prefix.Length, (long)(prefix.Length + zip.Length)), ReShadeArchiveExtractor.FindZip(stream));
    }

    [Fact]
    public void Crc32_MatchesTheStandardCheckValue()
        => Assert.Equal(0xCBF43926u, ReShadeArchiveExtractor.Crc32.Compute("123456789"u8));

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

    private static (string Name, byte[] Bytes, int UnixMode) Entry(string name, byte[] bytes, int unixMode = 0) => (name, bytes, unixMode);

    private static byte[] Zip(params (string Name, byte[] Bytes, int UnixMode)[] entries) => Zip(CompressionLevel.Optimal, entries);

    private static byte[] Zip(CompressionLevel level, params (string Name, byte[] Bytes, int UnixMode)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes, unixMode) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                if (unixMode != 0) entry.ExternalAttributes = (0x8000 | unixMode) << 16;
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        return ms.ToArray();
    }

    /// <summary>An end-of-central-directory record claiming a directory that ends where the record starts.</summary>
    private static byte[] DecoyEndRecord(uint directorySize)
    {
        var record = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), directorySize);
        return record;
    }

    private static int DirectoryHeaderOffset(byte[] zip, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        for (var i = 0; i + 46 + nameBytes.Length <= zip.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x02014b50
                && zip.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes))
                return i;
        throw new InvalidOperationException($"no directory header for {name}");
    }

    /// <summary>Rewrites the uncompressed size the central directory declares for <paramref name="name"/>.</summary>
    private static void SetDeclaredSize(byte[] zip, string name, uint size)
        => BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(DirectoryHeaderOffset(zip, name) + 24), size);
}
