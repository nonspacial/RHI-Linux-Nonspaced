using System.Net;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class DeploymentToolkitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-toolkit-tests-" + Guid.NewGuid().ToString("N"));

    public DeploymentToolkitTests() => Directory.CreateDirectory(_root);

    private string P(string name) => Path.Combine(_root, name);

    private string[] Leftovers() => Directory.GetFiles(_root).Where(f => Path.GetFileName(f).Contains(".rhi-", StringComparison.Ordinal)).ToArray();

    private static byte[] Bytes(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();

    // ---- AtomicFile ------------------------------------------------------------------------------

    [Fact]
    public void WriteAllBytes_ReplacesTargetAndLeavesNoTemporary()
    {
        File.WriteAllText(P("a.ini"), "old");
        AtomicFile.WriteAllText(P("a.ini"), "new content");
        Assert.Equal("new content", File.ReadAllText(P("a.ini")));
        Assert.Empty(Leftovers());
        Assert.True(AtomicFile.FsyncDirectory(_root));
    }

    [Fact]
    public async Task CopyAsync_WrongExpectedHash_LeavesTargetUntouchedAndNoTemporary()
    {
        File.WriteAllBytes(P("src.dll"), Bytes(200_000, 1));
        File.WriteAllBytes(P("dst.dll"), Bytes(10, 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => AtomicFile.CopyAsync(P("src.dll"), P("dst.dll"), new string('0', 64)));
        Assert.Equal(Bytes(10, 2), File.ReadAllBytes(P("dst.dll")));
        Assert.Empty(Leftovers());

        var sha = await AtomicFile.CopyAsync(P("src.dll"), P("dst.dll"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Bytes(200_000, 1))), sha);
        Assert.Equal(Bytes(200_000, 1), File.ReadAllBytes(P("dst.dll")));
    }

    [Fact]
    public async Task StageStreamAsync_OverSizeLimit_FailsWithoutTemporary()
    {
        using var content = new MemoryStream(Bytes(5000, 3));
        await Assert.ThrowsAsync<InvalidDataException>(() => AtomicFile.StageStreamAsync(content, P("big.bin"), null, null, 4096, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void WriteStoreFile_AppliesAnExplicitModeExactly_AndCreatesTheFolder()
    {
        var path = Path.Combine(_root, "new-dir", "token.txt");
        AtomicFile.WriteStoreFile(path, "secret"u8, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal("secret", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void WriteNew_RefusesAnExistingFile_AndLeavesItUntouched()
    {
        File.WriteAllText(P("copy.json"), "original");
        Assert.Throws<IOException>(() => AtomicFile.WriteNew(P("copy.json"), "other"u8));
        Assert.Equal("original", File.ReadAllText(P("copy.json")));

        AtomicFile.WriteNew(P("fresh.json"), "fresh"u8);
        Assert.Equal("fresh", File.ReadAllText(P("fresh.json")));
    }

    // ---- TrustedDownload ---------------------------------------------------------------------------

    [Theory]
    [InlineData("http://github.com/a/b.zip")]
    [InlineData("https://example.com/a/b.zip")]
    [InlineData("not a url")]
    [InlineData("https://github.com:8443/a/b.zip")]
    [InlineData("https://user:secret@github.com/a/b.zip")]
    public void Validate_RefusesNonHttpsAndUnlistedHosts(string url)
    {
        var refused = Assert.Throws<UntrustedDownloadException>(() => TrustedDownload.Validate(url, TrustedDownloadPolicy.GitHubReleases));
        Assert.Equal(TrustedDownloadPolicy.GitHubReleases.RefusalPrefix + url, refused.Message);
    }

    [Fact]
    public async Task Head_RedirectToUnlistedHost_IsRefused_AndAnUnlistedLinkIsNeverRequested()
    {
        var handler = new StubHandler(_ => [], redirectTo: new Uri("https://evil.example/x.addon64"));
        using var http = new HttpClient(handler);
        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => TrustedDownload.HeadAsync(
            http, "https://github.com/o/r/releases/download/v1/x.addon64", TrustedDownloadPolicy.RenoDxAddons));
        Assert.Equal("https://evil.example/x.addon64", refused.Url);

        await Assert.ThrowsAsync<UntrustedDownloadException>(() => TrustedDownload.HeadAsync(
            http, "http://github.com/o/r/releases/download/v1/x.addon64", TrustedDownloadPolicy.RenoDxAddons));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Download_RedirectToUnlistedHost_IsRefusedAndLeavesNothing()
    {
        var handler = new StubHandler(_ => Bytes(100, 4), redirectTo: new Uri("https://evil.example/payload.zip"));
        using var http = new HttpClient(handler);
        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => TrustedDownload.DownloadToTempAsync(
            http, "https://github.com/o/r/releases/download/v1/a.zip", P("a.zip"), TrustedDownloadPolicy.GitHubReleases));
        Assert.Equal("https://evil.example/payload.zip", refused.Url);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Download_PublishedHashMismatch_IsRefusedAndLeavesNothing()
    {
        using var http = new HttpClient(new StubHandler(_ => Bytes(100, 5)));
        await Assert.ThrowsAsync<InvalidDataException>(() => TrustedDownload.DownloadToTempAsync(
            http, "https://github.com/o/r/releases/download/v1/a.zip", P("a.zip"), TrustedDownloadPolicy.GitHubReleases, expectedSha256: new string('a', 64)));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task EnsureCached_ReusesOnlyStampedEntries()
    {
        const string url = "https://github.com/o/r/releases/download/v1/a.zip";
        var handler = new StubHandler(_ => Bytes(3000, 6));
        using var http = new HttpClient(handler);
        var entry = P("cache/a.zip");
        Directory.CreateDirectory(P("cache"));
        File.WriteAllBytes(entry, Bytes(3000, 9)); // foreign, unstamped

        var sha = await TrustedDownload.EnsureCachedAsync(http, url, entry, TrustedDownloadPolicy.GitHubReleases, "v1");
        Assert.Equal(1, handler.Requests);
        Assert.Equal(Bytes(3000, 6), File.ReadAllBytes(entry));
        Assert.Equal(sha, await TrustedDownload.EnsureCachedAsync(http, url, entry, TrustedDownloadPolicy.GitHubReleases, "v1"));
        Assert.Equal(1, handler.Requests);
        Assert.True(CacheStamp.IsValid(entry, url, "v1"));
    }

    // ---- OwnedFileSet ------------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_RestoresReplacedFileFromItsNewOriginal_AndRemovesCreatedFiles()
    {
        File.WriteAllBytes(P("game.dll"), Bytes(1000, 7));
        var set = new OwnedFileSet();
        await set.TrackAsync(P("game.dll"), CancellationToken.None);
        await set.TrackAsync(P("new.dll"), CancellationToken.None);
        // Operation: game.dll moved to .original and replaced; new.dll created.
        File.Move(P("game.dll"), P("game.dll.original"));
        File.WriteAllBytes(P("game.dll"), Bytes(500, 8));
        File.WriteAllBytes(P("new.dll"), Bytes(10, 9));

        var rollback = await set.RollbackAsync();

        Assert.True(rollback.Success, string.Join("; ", rollback.Failures));
        Assert.Equal(Bytes(1000, 7), File.ReadAllBytes(P("game.dll")));
        Assert.False(File.Exists(P("game.dll.original")));
        Assert.False(File.Exists(P("new.dll")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Rollback_PutsBackAConsumedOriginal()
    {
        File.WriteAllBytes(P("x.dll"), Bytes(300, 1));
        File.WriteAllBytes(P("x.dll.original"), Bytes(400, 2));
        var set = new OwnedFileSet();
        await set.TrackAsync(P("x.dll"), CancellationToken.None);
        await set.CaptureBackupAsync(P("x.dll"), CancellationToken.None);
        // Operation: restore = managed file moved to a snapshot, .original renamed over the target.
        File.Move(P("x.dll"), set.SnapshotPathOf(P("x.dll")));
        File.Move(P("x.dll.original"), P("x.dll"));

        Assert.True((await set.RollbackAsync()).Success);
        Assert.Equal(Bytes(300, 1), File.ReadAllBytes(P("x.dll")));
        Assert.Equal(Bytes(400, 2), File.ReadAllBytes(P("x.dll.original")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Rollback_WithoutAnySurvivingCopy_FailsAndDeletesNothing()
    {
        File.WriteAllBytes(P("y.dll"), Bytes(300, 1));
        var set = new OwnedFileSet();
        await set.TrackWithSnapshotAsync(P("y.dll"), CancellationToken.None);
        File.WriteAllBytes(P("y.dll"), Bytes(300, 2));
        File.WriteAllBytes(set.SnapshotPathOf(P("y.dll")), Bytes(300, 3)); // snapshot damaged too

        var rollback = await set.RollbackAsync();

        Assert.False(rollback.Success);
        Assert.Equal(Bytes(300, 2), File.ReadAllBytes(P("y.dll")));
        Assert.True(File.Exists(set.SnapshotPathOf(P("y.dll"))));
    }

    // ---- DeploymentJournal ---------------------------------------------------------------------------

    [Fact]
    public async Task Journal_CrashedOperation_IsRolledBackOnNextStart_WithCompensations()
    {
        File.WriteAllBytes(P("proxy.dll"), Bytes(2000, 1));
        var compensator = new RecordingCompensator();
        var journalDir = P("journal");
        var operation = await new DeploymentJournal(journalDir, [compensator]).BeginAsync("test install");
        await operation.TrackFileAsync(P("proxy.dll"));
        await operation.TrackFileAsync(P("payload.dll"));
        await operation.AddCompensationAsync(RecordingCompensator.KindName, "override:dxgi");
        File.WriteAllBytes(P("proxy.dll"), Bytes(2000, 2));
        File.WriteAllBytes(P("payload.dll"), Bytes(10, 3));
        await operation.RecordWrittenContentAsync(P("proxy.dll"));
        await operation.RecordWrittenContentAsync(P("payload.dll"));
        operation.AbandonAsIfCrashed();

        var report = await new DeploymentJournal(journalDir, [compensator]).RecoverAsync();

        Assert.Equal(1, report.RolledBack);
        Assert.Equal(Bytes(2000, 1), File.ReadAllBytes(P("proxy.dll")));
        Assert.False(File.Exists(P("payload.dll")));
        Assert.Equal(["override:dxgi"], compensator.Calls);
        Assert.Empty(Leftovers());
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    [Fact]
    public async Task Journal_LiveOperation_IsNotRecovered_CommittedOneIsOnlyCleaned()
    {
        File.WriteAllBytes(P("a.dll"), Bytes(100, 1));
        var journal = new DeploymentJournal(P("journal"));
        await using var live = await journal.BeginAsync("running", "probe-live");
        await live.TrackFileAsync(P("a.dll"));
        File.WriteAllBytes(P("a.dll"), Bytes(100, 2));

        var committed = await journal.BeginAsync("finished", "probe-committed");
        await committed.TrackFileAsync(P("b.dll"));
        File.WriteAllBytes(P("b.dll"), Bytes(100, 3));
        committed.AbandonAsIfCrashed();

        var report = await new DeploymentJournal(P("journal")).RecoverAsync(info => info.CommitProbe == "probe-committed");

        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, entry.Outcome);
        Assert.Equal(Bytes(100, 2), File.ReadAllBytes(P("a.dll")));
        Assert.Equal(Bytes(100, 3), File.ReadAllBytes(P("b.dll")));
        Assert.Contains(live.Id, journal.ReferencedOperationIds());

        Assert.True((await live.RollbackAsync()).Success);
        Assert.Equal(Bytes(100, 1), File.ReadAllBytes(P("a.dll")));
        Assert.Empty(journal.ReferencedOperationIds());
    }

    [Fact]
    public async Task Journal_UnknownCompensation_KeepsEntryForNextStart()
    {
        var journal = new DeploymentJournal(P("journal"));
        var operation = await journal.BeginAsync("x");
        await operation.AddCompensationAsync("nobody-handles-this", "{}");
        operation.AbandonAsIfCrashed();

        var report = await journal.RecoverAsync();

        Assert.Equal(1, report.Failed);
        Assert.Contains(operation.Id, journal.ReferencedOperationIds());
    }

    // ---- Sweeper -----------------------------------------------------------------------------------

    [Fact]
    public void Sweep_DeletesTempsAndRedundantCopies_SetsUniqueOnesAsideBesideThem_SkipsReferenced()
    {
        string Leftover(string name, byte[] content)
        {
            File.WriteAllBytes(P(name), content);
            return P(name);
        }
        var id = () => Guid.NewGuid().ToString("N");
        File.WriteAllBytes(P("game.dll"), Bytes(100, 1));
        File.WriteAllBytes(P("game.dll.original"), Bytes(100, 2));
        var temp = Leftover("game.dll.rhi-tmp-" + id(), Bytes(7, 9));
        var redundant = Leftover("game.dll.rhi-rollback-" + id(), Bytes(100, 2));
        var unique = Leftover("game.dll.rhi-update-snapshot-" + id(), Bytes(100, 3));
        var referencedId = id();
        var referenced = Leftover("game.dll.rhi-rollback-" + referencedId, Bytes(100, 5));

        // Seven hours on: every leftover's inode change is older than the six-hour minimum.
        var report = DeploymentLeftoverSweeper.Sweep([_root], opId => opId == referencedId, TimeSpan.FromHours(6), DateTime.UtcNow.AddHours(7));

        Assert.Equal(new[] { redundant, temp }.Order(), report.Deleted.Order());
        var moved = Assert.Single(report.Quarantined);
        Assert.Equal(unique, moved.From);
        Assert.Equal(Bytes(100, 3), File.ReadAllBytes(moved.To));
        // Same folder tree, so the move is a rename on the same file system.
        Assert.StartsWith(Path.Combine(_root, RecoveredCopies.FolderName) + Path.DirectorySeparatorChar, moved.To, StringComparison.Ordinal);
        Assert.True(File.Exists(referenced));
        Assert.Equal(Bytes(100, 1), File.ReadAllBytes(P("game.dll")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingCompensator : IDeploymentCompensator
    {
        public const string KindName = "test-compensation";
        public List<string> Calls { get; } = [];
        public string Kind => KindName;
        public CompensationResult Compensate(string data)
        {
            Calls.Add(data);
            return CompensationResult.Done;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, byte[]> body, Uri? redirectTo = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request)) };
            if (redirectTo is not null)
            {
                // What HttpClient reports after following a redirect: the final request URI.
                request.RequestUri = redirectTo;
            }
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
