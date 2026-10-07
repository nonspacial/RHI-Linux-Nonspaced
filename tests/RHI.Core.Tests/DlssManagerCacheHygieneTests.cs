using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>#39 (DEP-20): DlssManagerService cache hygiene.</summary>
[Collection(AppLogCollection.Name)]
public sealed class DlssManagerCacheHygieneTests : IDisposable
{
    private const string ZipUrl = "https://github.com/RankFTW/rhi-repo/releases/download/test/dlss.zip";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-hygiene-" + Guid.NewGuid().ToString("N"));

    public DlssManagerCacheHygieneTests() => Directory.CreateDirectory(_root);

    private static byte[] Dll(byte fill) => Enumerable.Repeat(fill, 80_000).ToArray();

    private static string Manifest(string? sha256) => sha256 is null
        ? $$"""{ "dlss": [{ "version": "9.1", "url": "{{ZipUrl}}" }] }"""
        : $$"""{ "dlss": [{ "version": "9.1", "url": "{{ZipUrl}}", "sha256": "{{sha256}}" }] }""";

    private string[] CacheFiles(string cache) => Directory.GetFiles(cache, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "dlss_manifest.json")
        .ToArray();

    [Fact]
    public async Task DownloadThatFailsMidway_LeavesNothingInTheCache()
    {
        var cache = Path.Combine(_root, "cache-midway");
        var zip = Zip(Dll(0x22));
        var handler = new Handler(Manifest(null), zip) { FailZipAfterBytes = zip.Length / 2 };
        var service = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), cache);
        await service.FetchManifestAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => service.EnsureCachedAsync(DlssComponentKind.SuperResolution, "9.1"));

        Assert.Empty(CacheFiles(cache));
    }

    [Fact]
    public async Task PublishedDigestMismatch_IsRefused_AndTheGameFileIsUntouched()
    {
        var target = Path.Combine(_root, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(target, Dll(0x11));
        var cache = Path.Combine(_root, "cache-mismatch");
        var service = new DlssManagerService(new HttpClient(new Handler(Manifest(new string('a', 64)), Zip(Dll(0x22)))), new TransactionalFileDeploymentService(), cache);
        await service.FetchManifestAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SwapAsync(DlssComponentKind.SuperResolution, [target], "9.1"));

        Assert.Equal(Dll(0x11), await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".original"));
        Assert.Empty(CacheFiles(cache));
    }

    [Fact]
    public async Task PublishedDigest_IsBoundIntoTheStamp_AndAChangedDigestForcesADownload()
    {
        var cache = Path.Combine(_root, "cache-digest");
        var first = Zip(Dll(0x22));
        var handler = new Handler(Manifest(Hex(first)), first);
        var service = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), cache);
        await service.FetchManifestAsync();
        var cached = (await service.EnsureCachedAsync(DlssComponentKind.SuperResolution, "9.1"))!;
        Assert.True(CacheStamp.IsValid(cached, ZipUrl, "9.1", Hex(first)));
        Assert.Equal(1, handler.ZipRequests);
        Assert.DoesNotContain(CacheFiles(cache), path => path.Contains(".rhi-tmp-", StringComparison.Ordinal));

        // Same URL and version, but the publisher now announces a different archive.
        var second = Zip(Dll(0x33));
        var republished = new Handler(Manifest(Hex(second)), second);
        var again = new DlssManagerService(new HttpClient(republished), new TransactionalFileDeploymentService(), cache);
        await again.FetchManifestAsync();
        var recached = (await again.EnsureCachedAsync(DlssComponentKind.SuperResolution, "9.1"))!;

        Assert.Equal(1, republished.ZipRequests);
        Assert.Equal(Dll(0x33), await File.ReadAllBytesAsync(recached));
    }

    [Fact]
    public async Task RedirectToAnUnlistedHost_IsRefused()
    {
        var cache = Path.Combine(_root, "cache-redirect");
        var handler = new Handler(Manifest(null), Zip(Dll(0x22))) { RedirectZipTo = new Uri("https://cdn.example.invalid/dlss.zip") };
        var service = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), cache);
        await service.FetchManifestAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureCachedAsync(DlssComponentKind.SuperResolution, "9.1"));
        Assert.Empty(CacheFiles(cache));
    }

    [Fact]
    public void Constructor_RemovesOldCrashLeftovers_KeepsRecentOnesAndPayloads()
    {
        var cache = Path.Combine(_root, "cache-leftovers");
        var dir = Path.Combine(cache, "DLSS", "9.1");
        Directory.CreateDirectory(dir);
        string Make(string name, DateTime mtime)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, mtime);
            return path;
        }
        var old = DateTime.UtcNow.AddDays(-1);
        var legacyPart = Make($"download-{Guid.NewGuid():N}.zip.part", old);
        var legacyTemp = Make($"nvngx_dlss.dll.tmp-{Guid.NewGuid():N}", old);
        var toolkitTemp = Make($"download.zip.rhi-tmp-{Guid.NewGuid():N}", old);
        var running = Make($"download.zip.rhi-tmp-{Guid.NewGuid():N}", DateTime.UtcNow);
        var payload = Make("nvngx_dlss.dll", old);

        _ = new DlssManagerService(new HttpClient(new Handler(Manifest(null), [])), new TransactionalFileDeploymentService(), cache);

        Assert.False(File.Exists(legacyPart));
        Assert.False(File.Exists(legacyTemp));
        Assert.False(File.Exists(toolkitTemp));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public async Task ManifestCacheWriteFailure_IsLogged_AndTheFreshManifestIsStillUsed()
    {
        var cache = Path.Combine(_root, "cache-unwritable");
        Directory.CreateDirectory(Path.Combine(cache, "dlss_manifest.json")); // a folder where the file goes: the write fails
        var log = new AppLog(Path.Combine(_root, "state", "rhi.log"));
        var flowLog = AppLog.UseInThisFlow(log);
        DlssManifestData manifest;
        DlssManagerService service;
        try
        {
            service = new DlssManagerService(new HttpClient(new Handler(Manifest(null), [])), new TransactionalFileDeploymentService(), cache);
            manifest = await service.FetchManifestAsync();
        }
        finally
        {
            flowLog.Dispose();
        }

        Assert.Equal("9.1", Assert.Single(manifest.Dlss).Version);
        Assert.NotNull(service.ManifestCacheWriteError);
        Assert.Contains("[dlss-manifest]", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public async Task CancellationAfterTheManifestWasParsed_DoesNotSwapInTheOlderCachedCopy()
    {
        var cache = Path.Combine(_root, "cache-cancel");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "dlss_manifest.json"),
            """{ "dlss": [{ "version": "1.0-old", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/old.zip" }] }""");
        using var cancel = new CancellationTokenSource();
        var handler = new Handler(Manifest(null), []) { CancelAfterManifest = cancel };
        var service = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), cache);

        var manifest = await service.FetchManifestAsync(cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("9.1", Assert.Single(manifest.Dlss).Version);
        Assert.Contains("\"9.1\"", File.ReadAllText(Path.Combine(cache, "dlss_manifest.json")));
    }

    [Fact]
    public async Task MalformedPublishedDigest_RejectsTheManifest()
    {
        var service = new DlssManagerService(new HttpClient(new Handler(Manifest("not-a-digest"), [])), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchManifestAsync());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Zip(byte[] dll)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("nvngx_dlss.dll").Open();
            entry.Write(dll);
        }
        return memory.ToArray();
    }

    private sealed class Handler(string manifest, byte[] zip) : HttpMessageHandler
    {
        public int ZipRequests { get; private set; }
        public int? FailZipAfterBytes { get; init; }
        public Uri? RedirectZipTo { get; init; }
        public CancellationTokenSource? CancelAfterManifest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = CancelAfterManifest is { } cancel
                        ? new CancelWhenReadContent(Encoding.UTF8.GetBytes(manifest), cancel)
                        : new ByteArrayContent(Encoding.UTF8.GetBytes(manifest))
                });
            ZipRequests++;
            HttpContent content = FailZipAfterBytes is int limit
                ? new StreamContent(new FailingStream(zip, limit))
                : new ByteArrayContent(zip);
            if (RedirectZipTo is not null) request.RequestUri = RedirectZipTo;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>Delivers the whole body, then cancels: the caller's token fires right after the manifest arrived.</summary>
    private sealed class CancelWhenReadContent(byte[] body, CancellationTokenSource cancel) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(body);
            cancel.Cancel();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    /// <summary>Serves the first <c>failAfter</c> bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= failAfter) throw new IOException("Connection reset (test).");
            var count = Math.Min(buffer.Length, Math.Min(failAfter, data.Length) - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));
    }
}
