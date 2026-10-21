using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;
using Xunit.Abstractions;

namespace RHI.Core.Tests;

/// <summary>
/// #216: a dropped or watched archive is copied into a private folder, listed and checked before anything is extracted
/// (traversal, absolute, empty parts, duplicates, backslash names, links/devices, size caps, a zip bomb's ratio,
/// unreadable archives), only the chosen add-on is extracted and its size checked again, and RHI's folder is always
/// removed. Fixture archives are built here: zips with <see cref="ZipArchive"/> (hostile ones patched by hand), 7z with
/// the host 7-Zip (<c>7z rn</c> makes the hostile names); a test whose tool is missing says so and returns.
/// </summary>
public sealed class DroppedArchiveExtractorTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dropped-archive-" + Guid.NewGuid().ToString("N"));
    private string Scratch => Path.Combine(_root, "cache", "dropped-archives");
    private string Downloads => Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;

    public DroppedArchiveExtractorTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        DroppedArchiveExtractor.DeleteFolder(_root);
    }

    internal static byte[] AddonBytes(ushort machine = 0x8664, int size = 2048)
    {
        var path = Path.Combine(Path.GetTempPath(), "rhi-pe-" + Guid.NewGuid().ToString("N"));
        PeArchitectureDetectorTests.WritePe(path, machine, size);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>A zip with fixed entry times; an entry's <c>Mode</c> (Unix type and bits) goes in its external attributes.</summary>
    internal static string Zip(string path, params (string Name, byte[] Data, int Mode)[] entries)
    {
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            foreach (var (name, data, mode) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
                if (mode != 0) entry.ExternalAttributes = mode << 16;
                using var stream = entry.Open();
                stream.Write(data);
            }
        return path;
    }

    internal static string Zip(string path, params (string Name, byte[] Data)[] entries)
        => Zip(path, entries.Select(e => (e.Name, e.Data, 0)).ToArray());

    /// <summary>Rewrites the uncompressed size the central directory gives <paramref name="name"/> (a lying header).</summary>
    internal static void PatchListedSize(string zip, string name, uint size)
    {
        var bytes = File.ReadAllBytes(zip);
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) != 0x02014b50) continue;
            int nameLength = BitConverter.ToUInt16(bytes, i + 28);
            if (Encoding.UTF8.GetString(bytes, i + 46, nameLength) != name) continue;
            BitConverter.GetBytes(size).CopyTo(bytes, i + 24);
            File.WriteAllBytes(zip, bytes);
            return;
        }
        throw new InvalidOperationException("no central directory entry " + name);
    }

    internal static string? SevenZip() => RHI.Platform.Linux.Deployment.ExternalProcess.FindOnPath("7zz", "7z", "7za");
    internal static string? BsdTar() => RHI.Platform.Linux.Deployment.ExternalProcess.FindOnPath("bsdtar");

    internal static void Run(string tool, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} {string.Join(' ', arguments)}: {stdout.Result} {stderr.Result}");
    }

    /// <summary>A .7z of <paramref name="files"/> (relative name → bytes) made by the host 7-Zip.</summary>
    internal static string SevenZipArchive(string sevenZip, string path, params (string Name, byte[] Data)[] files)
    {
        var source = Directory.CreateDirectory(path + ".src").FullName;
        foreach (var (name, data) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, name))!);
            File.WriteAllBytes(Path.Combine(source, name), data);
        }
        Run(sevenZip, source, ["a", "-bd", "-y", "-mx1", path, .. files.Select(f => f.Name)]);
        Directory.Delete(source, recursive: true);
        return path;
    }

    private bool ScratchIsEmpty() => !Directory.Exists(Scratch) || !Directory.EnumerateFileSystemEntries(Scratch).Any();

    private async Task<string> RefusedAsync(string archive)
    {
        var inspection = await DroppedArchiveExtractor.InspectAsync(archive, Scratch);
        Assert.Null(inspection.Archive);
        Assert.True(ScratchIsEmpty(), "nothing may stay in RHI's folder after a refusal");
        return inspection.Refusal!;
    }

    [Theory]
    [InlineData("renodx-hogwartslegacy.addon64", DroppedArchiveAddonKind.RenoDx)]
    [InlineData("RenoDX-Cyberpunk2077.addon32", DroppedArchiveAddonKind.RenoDx)]
    [InlineData("renodx-hogwartslegacy.addon", DroppedArchiveAddonKind.RenoDx)]
    [InlineData("renodx-mfg_unlock.addon64", DroppedArchiveAddonKind.GameAddon)] // not RenoDX's own: upstream's drop installs it beside RenoDX
    [InlineData("renodx-devkit.addon64", null)]       // a name one of RHI's rows installs
    [InlineData("Luma-Prey.addon64", DroppedArchiveAddonKind.Luma)]
    [InlineData("prey_luma.addon", DroppedArchiveAddonKind.Luma)]
    [InlineData("renodx-dlss5-feed.addon64", null)]   // a dropped file would only be imported into Custom Add-ons
    [InlineData("renodx-upgrade.addon64", null)]
    [InlineData("renodx-universal_ue-dof-fix.addon64", null)]
    [InlineData("ShaderToggler.addon64", null)]       // upstream offers only renodx-* and Luma from an archive
    [InlineData("renodx-foo.dll", null)]
    [InlineData("readme.txt", null)]
    [InlineData("renodx-foo.zip", null)]              // nested archives are never opened
    public void TheAddonsOffered_AreUpstreamsCandidates(string name, DroppedArchiveAddonKind? expected)
        => Assert.Equal(expected, LinuxDroppedFileClassifier.ArchiveAddonKind(name));

    [Fact]
    public async Task AZip_IsCopiedHashedAndListed_OnlyTheAddonIsOffered_ItsFolderIsPrivate_AndGoesOnDispose()
    {
        var addon = AddonBytes();
        var zip = Zip(Path.Combine(Downloads, "Hogwarts RenoDX.zip"),
            ("readme.txt", "hi"u8.ToArray()), ("renodx-hogwartslegacy.addon64", addon), ("docs/", []), ("docs/notes.md", "n"u8.ToArray()));

        var inspection = await DroppedArchiveExtractor.InspectAsync(zip, Scratch);

        using var archive = inspection.Archive!;
        Assert.Null(inspection.Refusal);
        Assert.Equal(["renodx-hogwartslegacy.addon64"], archive.Addons.Select(a => a.FileName));
        Assert.Equal(DroppedArchiveAddonKind.RenoDx, archive.Addons[0].Kind);
        Assert.Equal(addon.Length, archive.Addons[0].Size);
        Assert.Equal(await FileHash.Sha256HexAsync(zip), archive.Sha256, ignoreCase: true);
        Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(archive.SnapshotPath));
        Assert.Equal("Hogwarts RenoDX.zip", Path.GetFileName(archive.SnapshotPath));
        Assert.StartsWith(Scratch + Path.DirectorySeparatorChar, archive.Folder, StringComparison.Ordinal);
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(Private, File.GetUnixFileMode(archive.Folder));
        Assert.Equal(Private, File.GetUnixFileMode(Scratch));
        Assert.True(await archive.SourceStillMatchesAsync());

        var extracted = await archive.ExtractAsync(archive.Addons[0], CancellationToken.None);
        Assert.Equal("renodx-hogwartslegacy.addon64", Path.GetFileName(extracted));
        Assert.Equal(addon, File.ReadAllBytes(extracted));
        Assert.StartsWith(archive.Folder, extracted, StringComparison.Ordinal);
        Assert.Equal(new[] { archive.SnapshotPath, extracted }.Order(StringComparer.Ordinal), Directory.EnumerateFiles(archive.Folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)); // nothing else

        File.AppendAllText(zip, "x"); // a browser re-saves the download
        Assert.False(await archive.SourceStillMatchesAsync());

        archive.Dispose();
        Assert.False(Directory.Exists(archive.Folder));
        Assert.True(File.Exists(zip)); // the user's archive is never touched
    }

    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip)]
    [InlineData(ExternalArchiveToolKind.BsdTar)]
    public async Task A7z_IsListedAndExtractedThroughTheHostTool(ExternalArchiveToolKind kind)
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { _output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        var addon = AddonBytes();
        var archivePath = SevenZipArchive(sevenZip, Path.Combine(Downloads, "renodx-pack.7z"),
            ("renodx-foogame.addon64", addon), ("Luma-Foo.addon64", AddonBytes()), ("extra/readme.txt", "r"u8.ToArray()));
        ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            Assert.False(archive.IsZip);
            Assert.Equal(["renodx-foogame.addon64"], archive.Addons.Select(a => a.FileName));
            Assert.Equal(["Luma-Foo.addon64"], archive.LumaOutsideZip.Select(a => a.FileName)); // the Luma package install takes a .zip
            var extracted = await archive.ExtractAsync(archive.Addons[0], CancellationToken.None);
            Assert.Equal(addon, File.ReadAllBytes(extracted));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());
    }

    [Fact]
    public async Task ABackslashName_IsReadAsAFolder_AndTheAddonKeepsItsOwnName()
    {
        var addon = AddonBytes();
        var zip = Zip(Path.Combine(Downloads, "win.zip"), ("renodx-foo\\renodx-foogame.addon64", addon));
        using (var archive = (await DroppedArchiveExtractor.InspectAsync(zip, Scratch)).Archive!)
        {
            Assert.Equal("renodx-foogame.addon64", archive.Addons.Single().FileName);
            Assert.Equal(addon, File.ReadAllBytes(await archive.ExtractAsync(archive.Addons[0], CancellationToken.None)));
        }
        if (SevenZip() is not { } sevenZip) { _output.WriteLine("SKIP (7z part): no 7z on PATH"); return; }
        var sz = SevenZipArchive(sevenZip, Path.Combine(Downloads, "win.7z"), ("a.addon64", addon));
        Run(sevenZip, Downloads, "rn", "-bd", sz, "a.addon64", "dir\\renodx-foogame.addon64");
        using (var archive = (await DroppedArchiveExtractor.InspectAsync(sz, Scratch)).Archive!)
            Assert.Equal(addon, File.ReadAllBytes(await archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None)));
    }

    public static TheoryData<string, string> HostileZipNames => new()
    {
        { "../renodx-foo.addon64", DroppedArchiveExtractor.ParentRefusal },
        { "docs/../../x.txt", DroppedArchiveExtractor.ParentRefusal },
        { "a/../renodx-foo.addon64", DroppedArchiveExtractor.ParentRefusal }, // inside only: still refused
        { "dir\\..\\x.txt", DroppedArchiveExtractor.ParentRefusal },          // a backslash name climbing out
        { "/abs/renodx-foo.addon64", DroppedArchiveExtractor.AbsoluteRefusal },
        { "\\abs\\x.txt", DroppedArchiveExtractor.AbsoluteRefusal },
        { "C:/Windows/x.txt", DroppedArchiveExtractor.AbsoluteRefusal },
        { "docs//x.txt", DroppedArchiveExtractor.EmptyPartRefusal },
        { "dir\\\\x.txt", DroppedArchiveExtractor.EmptyPartRefusal },
        { "bad\nname.txt", DroppedArchiveExtractor.NameRefusal },
    };

    [Theory]
    [MemberData(nameof(HostileZipNames))]
    public async Task AHostileMemberName_RefusesTheWholeArchive_BeforeAnythingIsExtracted(string name, string reason)
    {
        var zip = Zip(Path.Combine(Downloads, "hostile.zip"), ("renodx-foogame.addon64", AddonBytes()), (name, "x"u8.ToArray()));
        Assert.StartsWith(reason, await RefusedAsync(zip), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xA1FF, "symbolic link")] // S_IFLNK
    [InlineData(0x11B6, "FIFO")]          // S_IFIFO
    [InlineData(0x21B6, "char device")]   // S_IFCHR
    public async Task ALinkOrDevice_InAZip_IsRefused(int mode, string what)
    {
        _output.WriteLine(what);
        var zip = Zip(Path.Combine(Downloads, "links.zip"), ("renodx-foogame.addon64", AddonBytes(), 0x81A4), ("evil", "/etc/passwd"u8.ToArray(), mode));
        Assert.Equal(DroppedArchiveExtractor.LinkRefusal + "evil", await RefusedAsync(zip));
    }

    [Theory]
    [InlineData("renodx-foogame.addon64", "renodx-foogame.addon64")]
    [InlineData("a/renodx-foogame.addon64", "a\\renodx-foogame.addon64")] // the same file once the backslash is a folder
    [InlineData("docs", "docs/x.txt")]                                    // a file where a folder is needed
    public async Task AMemberListedTwice_IsRefused(string first, string second)
    {
        var zip = Zip(Path.Combine(Downloads, "dup.zip"), (first, AddonBytes()), (second, AddonBytes(size: 4096)));
        Assert.StartsWith(DroppedArchiveExtractor.DuplicateRefusal, await RefusedAsync(zip), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileMembers_In7z_AreRefusedThroughBothToolListings()
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { _output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        var cases = new List<(string Archive, string Reason)>();
        string Renamed(string file, string from, string to)
        {
            var path = SevenZipArchive(sevenZip, Path.Combine(Downloads, file), ("renodx-foogame.addon64", AddonBytes()), ("x.txt", "x"u8.ToArray()));
            Run(sevenZip, Downloads, "rn", "-bd", path, from, to);
            return path;
        }
        cases.Add((Renamed("parent.7z", "x.txt", "../evil.txt"), DroppedArchiveExtractor.ParentRefusal));
        cases.Add((Renamed("absolute.7z", "x.txt", "/abs/evil.txt"), DroppedArchiveExtractor.AbsoluteRefusal));
        cases.Add((Renamed("duplicate.7z", "x.txt", "renodx-foogame.addon64"), DroppedArchiveExtractor.DuplicateRefusal));
        cases.Add((Renamed("backslash.7z", "x.txt", "dir\\..\\evil.txt"), DroppedArchiveExtractor.ParentRefusal));
        var linkSource = Directory.CreateDirectory(Path.Combine(_root, "linksrc")).FullName;
        File.WriteAllBytes(Path.Combine(linkSource, "renodx-foogame.addon64"), AddonBytes());
        File.CreateSymbolicLink(Path.Combine(linkSource, "evil"), "/etc/passwd");
        var linked = Path.Combine(Downloads, "symlink.7z");
        Run(sevenZip, linkSource, "a", "-bd", "-snl", linked, "renodx-foogame.addon64", "evil");
        cases.Add((linked, DroppedArchiveExtractor.LinkRefusal));

        foreach (var kind in new[] { ExternalArchiveToolKind.SevenZip, ExternalArchiveToolKind.BsdTar })
        {
            ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);
            try
            {
                foreach (var (archive, reason) in cases)
                {
                    var refusal = await RefusedAsync(archive);
                    Assert.True(refusal.StartsWith(reason, StringComparison.Ordinal), $"{kind} {Path.GetFileName(archive)}: {refusal}");
                }
            }
            finally
            {
                ExternalArchiveRunner.FindToolForTesting.Value = null;
            }
        }
    }

    [Fact]
    public async Task AnAddonListedOverTheCap_IsRefusedBeforeExtraction_InAZipAndA7z()
    {
        var zip = Zip(Path.Combine(Downloads, "big.zip"), ("renodx-foogame.addon64", AddonBytes()));
        PatchListedSize(zip, "renodx-foogame.addon64", 256u * 1024 * 1024 + 1);
        Assert.Equal(DroppedArchiveExtractor.AddonTooLargeRefusal + "renodx-foogame.addon64", await RefusedAsync(zip));

        // A header that lists fewer bytes than the entry holds: offered, and refused once the bytes really come out (the
        // add-on's folder is removed, RHI's copy stays until the archive is disposed).
        var lying = Zip(Path.Combine(Downloads, "lying.zip"), ("renodx-foogame.addon64", AddonBytes()));
        PatchListedSize(lying, "renodx-foogame.addon64", 100);
        var inspection = await DroppedArchiveExtractor.InspectAsync(lying, Scratch);
        Assert.True(inspection.Archive is not null, inspection.Refusal);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => inspection.Archive!.ExtractAsync(inspection.Archive.Addons[0], CancellationToken.None));
        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
        Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(inspection.Archive.Folder).Select(Path.GetFileName));
        inspection.Archive.Dispose();

        if (SevenZip() is not { } sevenZip) { _output.WriteLine("SKIP (7z part): no 7z on PATH"); return; }
        var source = Directory.CreateDirectory(Path.Combine(_root, "bigsrc")).FullName;
        using (var big = File.Create(Path.Combine(source, "renodx-foogame.addon64"))) big.SetLength(256L * 1024 * 1024 + 1);
        var sz = Path.Combine(Downloads, "big.7z");
        Run(sevenZip, source, "a", "-bd", "-mx1", sz, "renodx-foogame.addon64");
        Directory.Delete(source, recursive: true);
        Assert.Equal(DroppedArchiveExtractor.AddonTooLargeRefusal + "renodx-foogame.addon64", await RefusedAsync(sz));
    }

    [Fact]
    public async Task FilesListingMoreThanTheTotalCap_OrAZipBombsRatio_AreRefused()
    {
        var total = Zip(Path.Combine(Downloads, "total.zip"), ("renodx-foogame.addon64", AddonBytes()), ("a.bin", "a"u8.ToArray()), ("b.bin", "b"u8.ToArray()));
        PatchListedSize(total, "a.bin", 0x40000000u);       // 1 GiB
        PatchListedSize(total, "b.bin", 0x40000000u + 4096); // together with the add-on: just over 2 GiB
        Assert.Equal(DroppedArchiveExtractor.TotalTooLargeRefusal + "total.zip", await RefusedAsync(total));

        // 32 MiB of zeros packs to a few KiB: over 100 times the archive's own size.
        var bomb = Zip(Path.Combine(Downloads, "bomb.zip"), ("renodx-foogame.addon64", AddonBytes()), ("zeros.bin", new byte[32 * 1024 * 1024]));
        Assert.True(new FileInfo(bomb).Length < 32 * 1024 * 1024 / 100);
        Assert.Equal(DroppedArchiveExtractor.RatioRefusal + "bomb.zip", await RefusedAsync(bomb));

        // 12 MiB of zeros (under the 16 MiB floor) is no bomb.
        var small = Zip(Path.Combine(Downloads, "small.zip"), ("renodx-foogame.addon64", AddonBytes()), ("zeros.bin", new byte[12 * 1024 * 1024]));
        using var fine = (await DroppedArchiveExtractor.InspectAsync(small, Scratch)).Archive;
        Assert.NotNull(fine);
    }

    [Fact]
    public async Task ADamagedOrUnterminatedArchive_IsRefusedAsUnreadable()
    {
        var zip = Zip(Path.Combine(Downloads, "cut.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var bytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(zip, bytes[..(bytes.Length / 2)]); // no central directory
        Assert.Equal(DroppedArchiveExtractor.UnreadableRefusal + "cut.zip", await RefusedAsync(zip));

        var garbage = Path.Combine(Downloads, "renodx-garbage.rar");
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes(new string('x', 4096)));
        var refusal = await RefusedAsync(garbage);
        if (ExternalArchiveRunner.FindTool() is null) Assert.StartsWith(ExternalArchiveRunner.NoToolPrefix, refusal, StringComparison.Ordinal);
        else Assert.Equal(DroppedArchiveExtractor.UnreadableRefusal + "renodx-garbage.rar", refusal);

        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { _output.WriteLine("SKIP (7z part): needs 7z and bsdtar on PATH"); return; }
        var sz = SevenZipArchive(sevenZip, Path.Combine(Downloads, "cut.7z"), ("renodx-foogame.addon64", AddonBytes(size: 64 * 1024)));
        var full = File.ReadAllBytes(sz);
        File.WriteAllBytes(sz, full[..(full.Length - 40)]); // the header at the end is cut off
        foreach (var kind in new[] { ExternalArchiveToolKind.SevenZip, ExternalArchiveToolKind.BsdTar })
        {
            ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);
            try
            {
                Assert.Equal(DroppedArchiveExtractor.UnreadableRefusal + "cut.7z", await RefusedAsync(sz));
            }
            finally
            {
                ExternalArchiveRunner.FindToolForTesting.Value = null;
            }
        }
    }

    [Fact]
    public async Task AFifoOrADeviceLink_IsNeverRead_AndATooLargeArchiveIsNotCopied()
    {
        var fifo = Path.Combine(Downloads, "renodx-fifo.zip");
        Assert.Equal(0, Mono_MkFifo(fifo));
        var read = Task.Run(() => DroppedArchiveExtractor.InspectAsync(fifo, Scratch));
        var inTime = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20))) == read;
        if (!inTime) { using (File.OpenWrite(fifo)) { } } // unblock a read that should never have started
        Assert.True(inTime, "a FIFO must never be read (it would block the copy)");
        Assert.Equal(DroppedArchiveExtractor.NotAFileRefusal + "renodx-fifo.zip", (await read).Refusal);

        var device = Path.Combine(Downloads, "renodx-zero.zip");
        File.CreateSymbolicLink(device, "/dev/zero");
        Assert.Equal(DroppedArchiveExtractor.NotAFileRefusal + "renodx-zero.zip", await RefusedAsync(device));

        var huge = Path.Combine(Downloads, "renodx-huge.zip");
        using (var sparse = File.Create(huge)) sparse.SetLength((1L << 30) + 1); // sparse: nothing is written
        // Unreadable on purpose: refused on its listed size alone, before RHI opens it (opening it would fail otherwise).
        if (Environment.UserName != "root") File.SetUnixFileMode(huge, UnixFileMode.UserWrite);
        Assert.Equal(DroppedArchiveExtractor.ArchiveTooLargeRefusal + "renodx-huge.zip", await RefusedAsync(huge));
    }

    private static int Mono_MkFifo(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("mkfifo", [path]))!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_root, name + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r", "") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A fake 7-Zip: <c>l</c> prints <paramref name="listing"/>; <c>e</c> runs <paramref name="extract"/> with $OUT set.</summary>
    private ExternalArchiveTool FakeSevenZip(string listing, string extract)
    {
        var listingFile = Path.Combine(_root, "listing-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(listingFile, listing);
        return new ExternalArchiveTool(Script("fake7z-" + Guid.NewGuid().ToString("N"), $$"""
            if [ "$1" = l ]; then cat '{{listingFile}}'; exit 0; fi
            OUT=""
            for a in "$@"; do case "$a" in -o*) OUT="${a#-o}";; esac; done
            {{extract}}
            """), ExternalArchiveToolKind.SevenZip);
    }

    private const string OneAddonListing = "Path = renodx-foogame.addon64\nSize = 2048\nAttributes = A -rw-r--r--\n\n";

    [Fact]
    public async Task AToolThatWritesMoreThanItListed_IsRefusedAfterExtraction_AndTheAddonsFolderGoes()
    {
        var tool = FakeSevenZip(OneAddonListing, "head -c 4096 /dev/zero > \"$OUT/renodx-foogame.addon64\"");
        var archivePath = Path.Combine(Downloads, "lying.7z");
        File.WriteAllText(archivePath, "7z");
        ExternalArchiveRunner.FindToolForTesting.Value = () => tool;
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None));
            Assert.Equal(DroppedArchiveExtractor.SizeMismatchRefusal + "renodx-foogame.addon64", error.Message);
            Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());
    }

    [Theory]
    [InlineData("true")]                                                                                       // nothing came out
    [InlineData("head -c 2048 /dev/zero > \"$OUT/renodx-foogame.addon64\"; head -c 2048 /dev/zero > \"$OUT/other.addon64\"")] // two files
    [InlineData("mkfifo \"$OUT/renodx-foogame.addon64\"")]                                                     // not a plain file
    public async Task AToolThatGivesBackNoneTwoOrNoPlainFile_IsRefused(string extract)
    {
        var tool = FakeSevenZip(OneAddonListing, extract);
        var archivePath = Path.Combine(Downloads, "odd.7z");
        File.WriteAllText(archivePath, "7z");
        ExternalArchiveRunner.FindToolForTesting.Value = () => tool;
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(DroppedArchiveExtractor.NotExtractedRefusal + "renodx-foogame.addon64", error.Message);
            Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());
    }

    [Fact]
    public async Task AListingLongerThanTheToolCapture_IsRefused_NotCheckedHalfway()
    {
        var block = new StringBuilder();
        for (var i = 0; block.Length < 300 * 1024; i++) block.Append($"Path = docs/file-{i:D6}.txt\nSize = 1\nAttributes = A -rw-r--r--\n\n");
        var tool = FakeSevenZip(OneAddonListing.Replace("Path = renodx-foogame.addon64", "Path = ../renodx-foogame.addon64") + block, "true");
        var archivePath = Path.Combine(Downloads, "long.7z");
        File.WriteAllText(archivePath, "7z");
        ExternalArchiveRunner.FindToolForTesting.Value = () => tool;
        try
        {
            Assert.Equal(DroppedArchiveExtractor.TooManyFilesRefusal + "long.7z", await RefusedAsync(archivePath));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
    }

    [Fact]
    public async Task AMemberNamedLikeAWildcard_ExtractsOnlyItself()
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { _output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        var wanted = AddonBytes(size: 3000);
        var sz = SevenZipArchive(sevenZip, Path.Combine(Downloads, "wild.7z"), ("renodx-foogame.addon64", AddonBytes()), ("x.addon64", wanted));
        Run(sevenZip, Downloads, "rn", "-bd", sz, "x.addon64", "renodx-*.addon64");
        foreach (var kind in new[] { ExternalArchiveToolKind.SevenZip, ExternalArchiveToolKind.BsdTar })
        {
            ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);
            try
            {
                using var archive = (await DroppedArchiveExtractor.InspectAsync(sz, Scratch)).Archive!;
                var star = archive.Addons.Single(a => a.FileName == "renodx-*.addon64");
                Assert.Equal(wanted, File.ReadAllBytes(await archive.ExtractAsync(star, CancellationToken.None)));
            }
            finally
            {
                ExternalArchiveRunner.FindToolForTesting.Value = null;
            }
        }
    }

    [Fact]
    public async Task CancelDuringExtraction_StopsTheTool_AndLeavesNothingBehind()
    {
        var pidFile = Path.Combine(_root, "pid");
        var tool = FakeSevenZip(OneAddonListing, $"echo $$ > '{pidFile}'\nsleep 60");
        var archivePath = Path.Combine(Downloads, "slow.7z");
        File.WriteAllText(archivePath, "7z");
        ExternalArchiveRunner.FindToolForTesting.Value = () => tool;
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            using var cancel = new CancellationTokenSource();
            var extract = archive.ExtractAsync(archive.Addons.Single(), cancel.Token);
            for (var i = 0; i < 200 && !File.Exists(pidFile); i++) await Task.Delay(50);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extract.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());

        // A zip: Cancel pressed right before the entry is copied.
        var zip = Zip(Path.Combine(Downloads, "cancel.zip"), ("renodx-foogame.addon64", AddonBytes()));
        using var zipArchive = (await DroppedArchiveExtractor.InspectAsync(zip, Scratch)).Archive!;
        using var zipCancel = new CancellationTokenSource();
        DroppedArchiveExtractor.BeforeExtractForTesting.Value = zipCancel.Cancel;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => zipArchive.ExtractAsync(zipArchive.Addons[0], zipCancel.Token));
        }
        finally
        {
            DroppedArchiveExtractor.BeforeExtractForTesting.Value = null;
        }
        Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(zipArchive.Folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ACancelledInspection_LeavesNoFolder()
    {
        var zip = Zip(Path.Combine(Downloads, "c.zip"), ("renodx-foogame.addon64", AddonBytes()));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DroppedArchiveExtractor.InspectAsync(zip, Scratch, cancel.Token));
        Assert.True(ScratchIsEmpty());
    }

    [Fact]
    public void DeleteFolder_RemovesLinksWithoutFollowingThem_AndUnwritableFolders()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "keep")).FullName;
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
        var folder = DroppedArchiveExtractor.CreatePrivateFolder(Scratch);
        var locked = Directory.CreateDirectory(Path.Combine(folder, "raw", "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "f"), "x");
        Directory.CreateSymbolicLink(Path.Combine(folder, "raw", "out"), target);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.True(DroppedArchiveExtractor.DeleteFolder(folder));
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    // Real tool output (7-Zip 26.04, bsdtar 3.8.10) captured from fixtures like the ones above: a tar holding a hard
    // link, a symbolic link and a FIFO listed by 7-Zip, and bsdtar's listing of a 7z with a backslash name.
    private const string SevenZipTarListing = """
        Path = .
        Folder = +
        Size = 0
        Symbolic Link = 
        Hard Link = 

        Path = ./renodx-foo.addon64
        Folder = -
        Size = 7
        Symbolic Link = 
        Hard Link = 

        Path = ./link
        Folder = -
        Size = 11
        Symbolic Link = /etc/passwd
        Hard Link = 

        Path = ./hard.addon64
        Folder = -
        Size = 0
        Symbolic Link = 
        Hard Link = ./renodx-foo.addon64

        """;

    [Fact]
    public void TheSevenZipListing_IsReadStrictly()
    {
        var members = DroppedArchiveExtractor.ParseSevenZip(SevenZipTarListing)!;
        Assert.Equal([".", "./renodx-foo.addon64", "./link", "./hard.addon64"], members.Select(m => m.Name));
        Assert.Equal([null, null, "symbolic link", "hard link"], members.Select(m => m.Special));
        Assert.True(members[0].IsDirectory);
        Assert.Equal(7, members[1].Size);
        var sevenZipLink = DroppedArchiveExtractor.ParseSevenZip("Path = link\nSize = 11\nAttributes = A lrwxrwxrwx\n\nPath = sub\nSize = 0\nAttributes = D drwxr-xr-x\n")!;
        Assert.Equal(["symbolic link", null], sevenZipLink.Select(m => m.Special));
        Assert.True(sevenZipLink[1].IsDirectory);

        // A name cannot smuggle in a line of its own: a key twice, a stray line, a file without a size.
        Assert.Null(DroppedArchiveExtractor.ParseSevenZip("Path = a\nSize = 1\nSize = 999999999\n\n"));
        Assert.Null(DroppedArchiveExtractor.ParseSevenZip("Path = a\nb\nSize = 1\n\n"));
        Assert.Null(DroppedArchiveExtractor.ParseSevenZip("Path = a\nFolder = -\n\n"));
        Assert.Null(DroppedArchiveExtractor.ParseSevenZip("Size = 1\n\n"));
    }

    [Fact]
    public void TheBsdTarListing_IsReadStrictly()
    {
        const string listing = """
            drwxr-xr-x  0 0      0           0 Oct 21 22:00 sub/
            -rw-r--r--  0 0      0           7 Oct 21 22:00 my renodx  x.addon64
            lrwxrwxrwx  0 0      0           0 Oct 21 22:00 link -> /etc/passwd
            hrw-r--r--  0 nope   nope        0 Oct 21 22:00 ./hard.addon64 link to ./renodx-foo.addon64
            prw-r--r--  0 nope   nope        0 Oct 21  2025 ./fifo
            -rw-r--r--  0 0      0           1 Oct 21 22:00 dir\\renodx-bs.addon64
            -rw-------  0 0      0           2 Oct 21 22:00 a\nb.addon64
            """;
        var members = DroppedArchiveExtractor.ParseBsdTar(listing)!;
        Assert.Equal(["sub/", "my renodx  x.addon64", "link", "./hard.addon64", "./fifo", "dir\\renodx-bs.addon64", "a\u0001b.addon64"], members.Select(m => m.Name));
        Assert.Equal([null, null, "symbolic link", "hard link", "special file", null, null], members.Select(m => m.Special));
        Assert.Equal([0L, 7, 0, 0, 0, 1, 2], members.Select(m => m.Size!.Value));
        Assert.True(members[0].IsDirectory);
        Assert.Null(DroppedArchiveExtractor.ParseBsdTar("crw-rw-rw-  0 0      0         1,3 Oct 21 22:00 null\n")); // a device's size
        Assert.Null(DroppedArchiveExtractor.ParseBsdTar("-rw-r--r-- garbage\n"));
    }
}
