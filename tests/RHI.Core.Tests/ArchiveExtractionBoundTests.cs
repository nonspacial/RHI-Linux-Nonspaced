using System.Diagnostics;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;
using Xunit.Abstractions;
using static RHI.Core.Tests.DroppedArchiveExtractorTests;

namespace RHI.Core.Tests;

/// <summary>
/// #216 correction round. What an archive tool may write is bounded while it runs (the kernel's file-size limit per
/// file, a watcher on the output folder for all of them), for 7-Zip and bsdtar alike, and the in-process zip reader
/// never reads past an entry's listed size: an archive whose listing is smaller than what it unpacks to is stopped at
/// the listing, refused, and its folder cleaned — never after it has filled RHI's cache. Also: extraction reads RHI's own
/// copy; RHI's folder is private and a closed RHI's leftovers go at the next start; a file still being written is never
/// raised by the watcher, and one that changed while RHI copied it is refused.
/// </summary>
public sealed class ArchiveExtractionBoundTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-archive-bound-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Scratch => Path.Combine(_root, "cache", "dropped-archives");
    private string Downloads => Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
    private bool ScratchIsEmpty() => !Directory.Exists(Scratch) || !Directory.EnumerateFileSystemEntries(Scratch).Any();

    public void Dispose() => DroppedArchiveExtractor.DeleteFolder(_root);

    private static ExternalArchiveTool Tool(ExternalArchiveToolKind kind, string sevenZip, string bsdtar)
        => new(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);

    /// <summary>The largest file seen under <paramref name="folder"/> while <paramref name="run"/> ran (looked at every ms) and after it.</summary>
    private static async Task<(long Largest, Exception? Error)> WatchLargestAsync(string folder, Func<Task> run, Func<string, bool>? skip = null)
    {
        long largest = 0;
        void Look()
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (skip?.Invoke(file) == true) continue;
                try { largest = Math.Max(largest, new FileInfo(file).Length); } catch (IOException) { }
            }
        }
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () => { while (!stop.IsCancellationRequested) { Look(); await Task.Delay(1); } });
        Exception? error = null;
        try { await run(); }
        catch (Exception ex) { error = ex; }
        Look();
        stop.Cancel();
        await sampler;
        Look();
        return (largest, error);
    }

    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip)]
    [InlineData(ExternalArchiveToolKind.BsdTar)]
    public async Task TheRunner_StopsEachTool_AtTheFileBound_AndRefuses(ExternalArchiveToolKind kind)
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        var archive = SevenZipArchive(sevenZip, Path.Combine(Downloads, "big.7z"), ("big.bin", new byte[32 << 20])); // 32 MiB of zeros, a few KiB packed
        var outFolder = Path.Combine(_root, "out-" + kind);
        const long bound = 1 << 20;

        var (largest, error) = await WatchLargestAsync(outFolder, () => ExternalArchiveRunner.ExtractAsync(archive, outFolder, CancellationToken.None,
            tool: Tool(kind, sevenZip, bsdtar), limit: new ExtractionLimit(bound, bound)));

        var refused = Assert.IsType<InvalidDataException>(error);
        Assert.True(ExternalArchiveRunner.IsOverLimit(refused), refused.Message);
        Assert.Equal(ReShadeArchiveExtractor.ArchiveRefusedPrefix + ExternalArchiveRunner.OutputLimitRefusal + bound, refused.Message);
        Assert.InRange(largest, 0, bound + 512); // never past the bound (a block at most): not 32 MiB first, refused after
    }

    [Fact]
    public async Task TheRunner_StopsATool_ThatKeepsWritingSmallFiles_AtTheTotalBound()
    {
        // Every file under the per-file bound; together past the total one: the watcher stops the tool's tree. The fake
        // tool is stepped, not timed (w10 final review 1: a timed writer outran the watcher under a loaded suite): each
        // 64 KiB file appears whole (written beside the folder, then renamed in), and once the folder holds more than the
        // bound (9 files, 576 KiB) the tool waits for the test's "go" before it writes again — it never ends by itself.
        // Only the watcher can end it before the 2-minute timeout, and it must find exactly those 9 files.
        var script = Path.Combine(_root, "slow7z.sh");
        var go = Path.Combine(_root, "go-slow");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            OUT=""
            for a in "$@"; do case "$a" in -o*) OUT="${a#-o}";; esac; done
            GO='{{go}}'
            i=0
            while :; do
              head -c 65536 /dev/zero > "$OUT.stage"; mv "$OUT.stage" "$OUT/f$i"; i=$((i+1))
              if [ "$i" -ge 9 ]; then while [ ! -e "$GO" ]; do sleep 0.02; done; fi
            done
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var archive = Path.Combine(Downloads, "slow.7z");
        File.WriteAllText(archive, "7z");
        var outFolder = Path.Combine(_root, "out-slow");
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExternalArchiveRunner.ExtractAsync(archive, outFolder, CancellationToken.None,
                tool: new ExternalArchiveTool(script, ExternalArchiveToolKind.SevenZip), timeout: TimeSpan.FromMinutes(2),
                limit: new ExtractionLimit(512 << 10, 1 << 20))
            .WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.True(ExternalArchiveRunner.IsOverLimit(error), error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"stopped after {clock.Elapsed}");
        long Total() => Directory.EnumerateFiles(outFolder).Sum(file => new FileInfo(file).Length);
        Assert.Equal(9L * 65536, Total()); // stopped at the first look past the bound, whatever the load
        File.WriteAllText(go, ""); // a tool still running would write again now
        await Task.Delay(500);
        Assert.Equal(9L * 65536, Total()); // and it stays stopped
    }

    [Fact]
    public async Task TheCallersCancel_StaysACancel_NotABoundRefusal()
    {
        var script = Path.Combine(_root, "idle7z.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var archive = Path.Combine(Downloads, "idle.7z");
        File.WriteAllText(archive, "7z");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalArchiveRunner.ExtractAsync(archive, Path.Combine(_root, "out-idle"), cancel.Token,
            tool: new ExternalArchiveTool(script, ExternalArchiveToolKind.SevenZip), limit: new ExtractionLimit(1, 1)).WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip)]
    [InlineData(ExternalArchiveToolKind.BsdTar)]
    public async Task AListingThatLies_IsStoppedAtTheListedSize_AndTheAddonsFolderGoes(ExternalArchiveToolKind kind)
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        // A gzip stream behind a .7z name: 7-Zip lists 2 KiB (its trailer) and unpacks 32 MiB. bsdtar sees no archive in it.
        var archivePath = LyingGzip(Path.Combine(Downloads, "renodx-foogame.7z"), "renodx-foogame.addon64", 32 << 20, 2048);
        ExternalArchiveRunner.FindToolForTesting.Value = () => Tool(kind, sevenZip, bsdtar);
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            if (kind == ExternalArchiveToolKind.BsdTar)
            {
                Assert.Empty(archive.Addons); // nothing offered, nothing extracted
                return;
            }
            var addon = archive.Addons.Single();
            Assert.Equal(2048, addon.Size);

            var (largest, error) = await WatchLargestAsync(archive.Folder, () => archive.ExtractAsync(addon, CancellationToken.None),
                skip: file => file == archive.SnapshotPath);

            Assert.Equal(DroppedArchiveExtractor.SizeMismatchRefusal + "renodx-foogame.addon64", Assert.IsType<InvalidDataException>(error).Message);
            Assert.InRange(largest, 0, 2048 + 512);
            Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());
    }

    [Fact]
    public async Task AZipEntryThatHoldsMoreThanItLists_IsNeverReadPastItsListedSize()
    {
        // A stored entry: .NET reads the stored bytes as they are, so only RHI's bounded copy stops at the listed size.
        var zip = Path.Combine(Downloads, "renodx-foogame.zip");
        using (var file = File.Create(zip))
        using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("renodx-foogame.addon64", System.IO.Compression.CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(AddonBytes(size: 1 << 20));
        }
        PatchListedSize(zip, "renodx-foogame.addon64", 2048);
        using var inspected = (await DroppedArchiveExtractor.InspectAsync(zip, Scratch)).Archive!;
        var addon = inspected.Addons.Single();
        Assert.Equal(2048, addon.Size);

        var (largest, error) = await WatchLargestAsync(inspected.Folder, () => inspected.ExtractAsync(addon, CancellationToken.None),
            skip: file => file == inspected.SnapshotPath);

        Assert.Equal("renodx-foogame.addon64: holds more bytes than it declares (2048)", Assert.IsType<InvalidDataException>(error).Message);
        Assert.InRange(largest, 0, 2048);
        Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(inspected.Folder).Select(Path.GetFileName));
    }

    /// <summary>A fake 7-Zip: <c>l</c> prints <paramref name="listing"/>; anything else runs <paramref name="extract"/> with $OUT set.</summary>
    private ExternalArchiveTool FakeSevenZip(string listing, string extract)
    {
        var listingFile = Path.Combine(_root, "listing-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(listingFile, listing);
        var script = Path.Combine(_root, "fake7z-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            if [ "$1" = l ]; then cat '{{listingFile}}'; exit 0; fi
            OUT=""
            for a in "$@"; do case "$a" in -o*) OUT="${a#-o}";; esac; done
            {{extract}}
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new ExternalArchiveTool(script, ExternalArchiveToolKind.SevenZip);
    }

    private const string LumaListing = "Path = Luma-Foo.addon64\nSize = 2048\nAttributes = A -rw-r--r--\n\nPath = shaders\nFolder = +\nSize = 0\n\nPath = shaders/a.hlsl\nSize = 4\nAttributes = A -rw-r--r--\n\n";
    private const string LumaFiles = "head -c 2048 /dev/zero > \"$OUT/Luma-Foo.addon64\"; mkdir -p \"$OUT/shaders\"; printf abcd > \"$OUT/shaders/a.hlsl\"";

    [Theory]
    [InlineData("", true)]                                                       // what the listing says: zipped again
    [InlineData("; printf x > \"$OUT/unlisted.txt\"", false)]                    // a file the listing did not give
    [InlineData("; rm \"$OUT/shaders/a.hlsl\"", false)]                         // a listed file missing
    [InlineData("; printf abc > \"$OUT/shaders/a.hlsl\"", false)]               // a listed file of another size
    [InlineData("; ln -s /etc/hostname \"$OUT/shaders/b.hlsl\"", false)]         // a link (the runner's own refusal comes first)
    public async Task ALumaPackageFromA7z_IsZippedAgain_OnlyWhenItUnpacksExactlyAsListed(string extra, bool accepted)
    {
        var tool = FakeSevenZip(LumaListing, LumaFiles + extra);
        var archivePath = Path.Combine(Downloads, "luma-foo.7z");
        File.WriteAllText(archivePath, "7z");
        ExternalArchiveRunner.FindToolForTesting.Value = () => tool;
        try
        {
            using var archive = (await DroppedArchiveExtractor.InspectAsync(archivePath, Scratch)).Archive!;
            Assert.Equal(DroppedArchiveAddonKind.Luma, archive.Addons.Single().Kind);
            if (accepted)
            {
                var zip = await archive.PackageAsZipAsync(CancellationToken.None);
                using var read = System.IO.Compression.ZipFile.OpenRead(zip);
                Assert.Equal(["Luma-Foo.addon64", "shaders/a.hlsl"], read.Entries.Select(e => e.FullName));
                Assert.Equal([2048L, 4L], read.Entries.Select(e => e.Length));
                Assert.Equal([Path.GetFileName(zip)], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(zip)!).Select(Path.GetFileName)); // the extracted files went
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => archive.PackageAsZipAsync(CancellationToken.None));
                if (extra.Contains("ln -s", StringComparison.Ordinal))
                    Assert.StartsWith(ReShadeArchiveExtractor.ArchiveRefusedPrefix + ExternalArchiveRunner.SymlinkRefusal, error.Message, StringComparison.Ordinal);
                else
                    Assert.Equal(DroppedArchiveExtractor.PackageMismatchRefusal + "luma-foo.7z", error.Message);
                Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
            }
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.True(ScratchIsEmpty());
    }

    [Fact]
    public async Task Extraction_ReadsRhisCopy_NeverTheUsersFile()
    {
        var addon = AddonBytes();
        var zip = Zip(Path.Combine(Downloads, "renodx-foogame.zip"), ("renodx-foogame.addon64", addon));
        using var archive = (await DroppedArchiveExtractor.InspectAsync(zip, Scratch)).Archive!;
        Assert.NotEqual(zip, archive.SnapshotPath);
        Assert.StartsWith(Scratch + Path.DirectorySeparatorChar, archive.SnapshotPath, StringComparison.Ordinal);
        Zip(zip, ("renodx-foogame.addon64", AddonBytes(size: 4096))); // the user's file is replaced after RHI read it

        Assert.Equal(addon, File.ReadAllBytes(await archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None)));
        File.Delete(zip); // and gone: still RHI's copy
        Assert.Equal(addon, File.ReadAllBytes(await archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None)));

        if (SevenZip() is not { } sevenZip) { output.WriteLine("SKIP (7z part): no 7z on PATH"); return; }
        var sz = SevenZipArchive(sevenZip, Path.Combine(Downloads, "renodx-foogame.7z"), ("renodx-foogame.addon64", addon));
        using var seven = (await DroppedArchiveExtractor.InspectAsync(sz, Scratch)).Archive!;
        File.Delete(sz);
        Assert.Equal(addon, File.ReadAllBytes(await seven.ExtractAsync(seven.Addons.Single(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(true)]  // still growing
    [InlineData(false)] // rewritten in place, same size
    public async Task AFileThatChangesWhileRhiCopiesIt_IsRefused_AndNothingIsLeft(bool grows)
    {
        var zip = Zip(Path.Combine(Downloads, "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        DroppedArchiveExtractor.AfterCopyForTesting.Value = () =>
        {
            if (grows) { using var file = new FileStream(zip, FileMode.Append); file.Write(new byte[16]); }
            else { Thread.Sleep(20); File.WriteAllBytes(zip, File.ReadAllBytes(zip)); } // ctime moves on
        };
        DroppedArchiveInspection inspection;
        try
        {
            inspection = await DroppedArchiveExtractor.InspectAsync(zip, Scratch);
        }
        finally
        {
            DroppedArchiveExtractor.AfterCopyForTesting.Value = null;
        }

        Assert.Null(inspection.Archive);
        Assert.Equal(DroppedArchiveExtractor.ChangedWhileReadRefusal + "renodx-foogame.zip", inspection.Refusal);
        Assert.True(ScratchIsEmpty());
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task RhisArchiveFolder_IsPrivate_EvenWhenItWasNot()
    {
        Directory.CreateDirectory(Scratch);
        File.SetUnixFileMode(Scratch, (UnixFileMode)0b111_101_101); // 0755, as an older build or the user left it
        var zip = Zip(Path.Combine(Downloads, "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        using var archive = (await DroppedArchiveExtractor.InspectAsync(zip, Scratch)).Archive!;
        const UnixFileMode folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(folder, File.GetUnixFileMode(Scratch));
        Assert.Equal(folder, File.GetUnixFileMode(archive.Folder));
        Assert.Equal(folder, File.GetUnixFileMode(Path.GetDirectoryName(archive.SnapshotPath)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive.SnapshotPath));
        var extracted = await archive.ExtractAsync(archive.Addons.Single(), CancellationToken.None);
        Assert.Equal(folder, File.GetUnixFileMode(Path.GetDirectoryName(extracted)!));
    }

    [Fact]
    public void LeftoversOfAClosedRhi_GoAtTheNextStart_ARunningRhisAndOtherFoldersStay()
    {
        var root = Directory.CreateDirectory(DroppedArchiveExtractor.DefaultRoot).FullName; // the isolated test cache
        string Make(string name) { var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName; File.WriteAllText(Path.Combine(dir, "x"), "x"); return dir; }
        var id = Guid.NewGuid().ToString("N");
        var gone = Make($"4194305-123-{id}");                                             // no such process (past pid_max)
        var reused = Make($"{Environment.ProcessId}-1-{id}");                             // this pid, another start time: a pid reused
        var legacy = Make(Guid.NewGuid().ToString("N"));                                  // before owner tags
        var mine = Make($"{DroppedArchiveExtractor.OwnerTag(Environment.ProcessId)}-{id}"); // this RHI, running
        var other = Make("not-rhis-" + id);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep"), "k");
        var link = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            // At start: the app's composition sweeps RHI's cache folder.
            using var http = new HttpClient(new RefusingHandler());
            var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
            var wine = new WineDllOverrideService();
            RHI.Desktop.AppComposition.Create(http, records, wine, LinuxDeploymentRecovery.CreateJournal(wine));

            Assert.False(Directory.Exists(gone));
            Assert.False(Directory.Exists(reused));
            Assert.False(Directory.Exists(legacy));
            Assert.True(Directory.Exists(mine));
            Assert.True(Directory.Exists(other));
            Assert.True(File.Exists(Path.Combine(outside, "keep"))); // a link is never followed
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        }
        finally
        {
            File.Delete(link);
            DroppedArchiveExtractor.DeleteFolder(mine);
            DroppedArchiveExtractor.DeleteFolder(other);
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline (test)");
    }

    [Fact]
    public async Task AWatchedFileStillBeingWritten_IsRaisedOnlyOnceItHasSettled()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_root, "watch")).FullName;
        using var watcher = new LinuxAddonFileWatcher();
        var raised = new TaskCompletionSource<(LinuxWatchedFileCandidate Candidate, long Length, DateTime At)>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.CandidateDetected += candidate => raised.TrySetResult((candidate, new FileInfo(candidate.Path).Length, DateTime.UtcNow));
        Assert.True(watcher.Start(watch));
        var path = Path.Combine(watch, "renodx-foogame.zip");

        // A download written in pieces for about 3 s (a pause longer than one look in between).
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            for (var i = 0; i < 12; i++)
            {
                file.Write(new byte[4096]);
                file.Flush();
                await Task.Delay(i == 5 ? 600 : 200);
                Assert.False(raised.Task.IsCompleted, "raised while still being written");
            }
        var finished = DateTime.UtcNow;

        var (candidate, length, at) = await raised.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(path, candidate.Path);
        Assert.Equal(LinuxWatchedFileKind.Archive, candidate.Kind);
        Assert.Equal(12 * 4096, length);
        Assert.True(at >= finished, "raised before the writes ended");
    }

    [Fact]
    public async Task AFileThatNeverSettles_IsNeverRaised()
    {
        var path = Path.Combine(_root, "renodx-growing.zip");
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            while (!stop.IsCancellationRequested) { file.Write(new byte[512]); file.Flush(); await Task.Delay(100); }
        });
        try
        {
            Assert.False(await LinuxAddonFileWatcher.WaitUntilSettledAsync(path, TimeSpan.FromSeconds(3)));
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
        Assert.True(await LinuxAddonFileWatcher.WaitUntilSettledAsync(path, TimeSpan.FromSeconds(10))); // settles once the writer stops
        File.Delete(path);
        Assert.False(await LinuxAddonFileWatcher.WaitUntilSettledAsync(path, TimeSpan.FromSeconds(3))); // gone: never raised
    }

    [Theory]
    [InlineData("luma-foo.zip", LinuxWatchedFileKind.Archive, WatchedFileRoute.ArchiveOffer)] // decision 4: no longer the old Luma prompt
    [InlineData("Luma_Foo-v2.ZIP", LinuxWatchedFileKind.Archive, WatchedFileRoute.ArchiveOffer)]
    [InlineData("renodx-foogame.7z", LinuxWatchedFileKind.Archive, WatchedFileRoute.ArchiveOffer)]
    [InlineData("luma-foo.rar", LinuxWatchedFileKind.Archive, WatchedFileRoute.ArchiveOffer)]
    [InlineData("Luma-Foo.addon64", LinuxWatchedFileKind.Addon, WatchedFileRoute.LumaAddonPrompt)]
    [InlineData("renodx-foogame.addon64", LinuxWatchedFileKind.Addon, WatchedFileRoute.DropDialog)]
    [InlineData("ShaderToggler.addon64", LinuxWatchedFileKind.Addon, WatchedFileRoute.CustomAddonsImport)]
    public void AWatchedFile_TakesItsRoute(string name, LinuxWatchedFileKind kind, WatchedFileRoute route)
    {
        var path = Path.Combine(_root, name);
        Assert.Equal(kind, LinuxAddonFileWatcher.Classify(path));
        Assert.Equal(route, DroppedRenoDxPresenter.RouteFor(new LinuxWatchedFileCandidate(path, kind)));
    }
}
