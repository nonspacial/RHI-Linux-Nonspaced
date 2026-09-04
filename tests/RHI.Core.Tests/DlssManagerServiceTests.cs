using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class DlssManagerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-tests-" + Guid.NewGuid().ToString("N"));

    public DlssManagerServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Detect_FindsNestedDlssAndStreamline()
    {
        var game = Path.Combine(_root, "game");
        var bin = Path.Combine(game, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "nvngx_dlss.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(bin, "sl.interposer.dll"), new byte[] { 1 });

        var service = new DlssManagerService(new HttpClient(new FakeHandler([], [])), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache"));
        var found = service.Detect(game);

        Assert.Single(found.DlssPaths);
        Assert.Single(found.StreamlineFolders);
        Assert.Equal(bin, found.StreamlineFolders[0]);
    }

    [Fact]
    public async Task SwapDlss_UsesManifestZip_AndPreservesOriginal()
    {
        var target = Path.Combine(_root, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0x11, 70_000).ToArray());
        var replacement = Enumerable.Repeat((byte)0x22, 80_000).ToArray();
        var zipBytes = ZipWith("nvngx_dlss.dll", replacement);
        const string manifest = "{ \"dlss\": [{ \"version\": \"999.1\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/dlss.zip\" }] }";

        var handler = new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes);
        var service = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache2"));
        await service.FetchManifestAsync();
        var result = await service.SwapAsync(DlssComponentKind.SuperResolution, new[] { target }, "999.1");

        Assert.True(result.Success);
        Assert.Equal(80_000L, new FileInfo(target).Length);
        Assert.Equal(70_000L, new FileInfo(target + ".original").Length);
    }

    private static byte[] ZipWith(string name, byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(payload);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeHandler(byte[] manifest, byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isManifest = request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl;
            var bytes = isManifest ? manifest : zip;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            return Task.FromResult(response);
        }
    }
}
