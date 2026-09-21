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


    [Fact]
    public async Task EnsureStreamlineCachedAsync_CachesRequestedVersionWithoutInventingGameTargets()
    {
        var zipBytes = ZipWithMany(
            ("sl.common.dll", Enumerable.Repeat((byte)0x71, 80_000).ToArray()),
            ("sl.interposer.dll", Enumerable.Repeat((byte)0x72, 81_000).ToArray()));
        const string manifest = "{ \"streamline\": [{ \"version\": \"2.12.0\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";
        var cacheRoot = Path.Combine(_root, "cache-streamline-exact");
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes)),
            new TransactionalFileDeploymentService(),
            cacheRoot);

        var cached = await service.EnsureStreamlineCachedAsync("2.12.0");

        Assert.NotNull(cached);
        Assert.True(File.Exists(Path.Combine(cached!, "sl.common.dll")));
        Assert.True(File.Exists(Path.Combine(cached, "sl.interposer.dll")));
        Assert.Null(await service.EnsureStreamlineCachedAsync("does-not-exist"));
    }

    [Fact]
    public async Task SwapStreamline_ReplacesOnlyExistingKnownDlls_AndRestoreReturnsOriginal()
    {
        var folder = Path.Combine(_root, "streamline-game");
        Directory.CreateDirectory(folder);
        var common = Path.Combine(folder, "sl.common.dll");
        await File.WriteAllBytesAsync(common, Enumerable.Repeat((byte)0x31, 70_000).ToArray());

        var zipBytes = ZipWithMany(
            ("sl.common.dll", Enumerable.Repeat((byte)0x41, 80_000).ToArray()),
            ("sl.reflex.dll", Enumerable.Repeat((byte)0x42, 80_000).ToArray()));
        const string manifest = "{ \"streamline\": [{ \"version\": \"9.9.9\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";

        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes)),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-streamline"));
        await service.FetchManifestAsync();

        var swap = await service.SwapStreamlineAsync(new[] { folder }, "9.9.9");
        Assert.True(swap.Success);
        Assert.Single(swap.Results);
        Assert.Equal(80_000L, new FileInfo(common).Length);
        Assert.Equal(70_000L, new FileInfo(common + ".original").Length);
        Assert.False(File.Exists(Path.Combine(folder, "sl.reflex.dll")));

        var restore = await service.RestoreStreamlineAsync(new[] { folder });
        Assert.True(restore.Success);
        Assert.Equal(70_000L, new FileInfo(common).Length);
        Assert.False(File.Exists(common + ".original"));
    }

    [Fact]
    public async Task SwapStreamline_UpdatesAllDetectedFoldersTransactionally()
    {
        var folder1 = Path.Combine(_root, "sl-one");
        var folder2 = Path.Combine(_root, "sl-two");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var dll1 = Path.Combine(folder1, "sl.common.dll");
        var dll2 = Path.Combine(folder2, "sl.common.dll");
        await File.WriteAllBytesAsync(dll1, Enumerable.Repeat((byte)0x11, 70_000).ToArray());
        await File.WriteAllBytesAsync(dll2, Enumerable.Repeat((byte)0x22, 71_000).ToArray());

        var zipBytes = ZipWith("sl.common.dll", Enumerable.Repeat((byte)0x55, 90_000).ToArray());
        const string manifest = "{ \"streamline\": [{ \"version\": \"8.8.8\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes)),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-streamline-multi"));
        await service.FetchManifestAsync();

        var result = await service.SwapStreamlineAsync(new[] { folder1, folder2 }, "8.8.8");

        Assert.True(result.Success);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(90_000L, new FileInfo(dll1).Length);
        Assert.Equal(90_000L, new FileInfo(dll2).Length);
        Assert.True(File.Exists(dll1 + ".original"));
        Assert.True(File.Exists(dll2 + ".original"));
    }

    [Fact]
    public async Task SwapStreamline_RefusesToInventMissingLayout()
    {
        var folder = Path.Combine(_root, "streamline-empty");
        Directory.CreateDirectory(folder);
        var zipBytes = ZipWith("sl.common.dll", Enumerable.Repeat((byte)0x55, 90_000).ToArray());
        const string manifest = "{ \"streamline\": [{ \"version\": \"7.7.7\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes)),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-streamline-empty"));
        await service.FetchManifestAsync();

        var result = await service.SwapStreamlineAsync(new[] { folder }, "7.7.7");

        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.False(File.Exists(Path.Combine(folder, "sl.common.dll")));
    }

    [Fact]
    public void Detect_PrefersDllBesideSelectedGameExecutable()
    {
        var game = Path.Combine(_root, "affinity-game");
        var bin = Path.Combine(game, "Game", "Binaries", "Win64");
        var engine = Path.Combine(game, "Engine", "Plugins", "Runtime");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(engine);
        var exe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(exe, new byte[] { 1 });
        var besideExe = Path.Combine(bin, "nvngx_dlss.dll");
        var engineCopy = Path.Combine(engine, "nvngx_dlss.dll");
        File.WriteAllBytes(besideExe, new byte[] { 1 });
        File.WriteAllBytes(engineCopy, new byte[] { 1 });

        var service = new DlssManagerService(new HttpClient(new FakeHandler([], [])), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-affinity"));
        var found = service.Detect(game, exe);

        Assert.Equal(2, found.DlssPaths.Count);
        Assert.Equal(besideExe, found.DlssPaths[0]);
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

    private static byte[] ZipWithMany(params (string Name, byte[] Payload)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, payload) in files)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(payload);
            }
        }
        return output.ToArray();
    }


    [Fact]
    public async Task SwapStreamline_IdenticalPackage_IsNoOpAndCreatesNoOriginals()
    {
        var folder = Path.Combine(_root, "streamline-identical");
        Directory.CreateDirectory(folder);
        var common = Path.Combine(folder, "sl.common.dll");
        var reflex = Path.Combine(folder, "sl.reflex.dll");
        var commonBytes = Enumerable.Repeat((byte)0x61, 80_000).ToArray();
        var reflexBytes = Enumerable.Repeat((byte)0x62, 81_000).ToArray();
        await File.WriteAllBytesAsync(common, commonBytes);
        await File.WriteAllBytesAsync(reflex, reflexBytes);

        var zipBytes = ZipWithMany(("sl.common.dll", commonBytes), ("sl.reflex.dll", reflexBytes));
        const string manifest = "{ \"streamline\": [{ \"version\": \"2.13.0.0\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), zipBytes)),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-streamline-identical"));
        await service.FetchManifestAsync();

        var result = await service.SwapStreamlineAsync(new[] { folder }, "2.13.0.0");

        Assert.True(result.Success);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.False(r.Changed));
        Assert.False(File.Exists(common + ".original"));
        Assert.False(File.Exists(reflex + ".original"));
        Assert.Equal(commonBytes, await File.ReadAllBytesAsync(common));
        Assert.Equal(reflexBytes, await File.ReadAllBytesAsync(reflex));
    }

    [Fact]
    public async Task SwapDlss_RepairsExecuteOnlyCachedDll_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var cacheRoot = Path.Combine(_root, "cache-permissions");
        var cacheDir = Path.Combine(cacheRoot, "DLSS-G", "310.8.0");
        Directory.CreateDirectory(cacheDir);
        var cached = Path.Combine(cacheDir, "nvngx_dlssg.dll");
        var cachedBytes = Enumerable.Repeat((byte)0x7A, 80_000).ToArray();
        await File.WriteAllBytesAsync(cached, cachedBytes);
        File.SetUnixFileMode(cached, UnixFileMode.GroupExecute); // reproduces an executable-only cache mode (0010) seen in legacy data

        var target = Path.Combine(_root, "game-fg", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0x11, 70_000).ToArray());

        const string manifest = "{ \"dlssg\": [{ \"version\": \"310.8.0\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/dlssg.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), [])),
            new TransactionalFileDeploymentService(),
            cacheRoot);
        await service.FetchManifestAsync();

        var result = await service.SwapAsync(DlssComponentKind.FrameGeneration, new[] { target }, "310.8.0");

        Assert.True(result.Success);
        Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(target));
        Assert.True(File.Exists(target + ".original"));
        var mode = File.GetUnixFileMode(cached);
        Assert.True((mode & UnixFileMode.UserRead) != 0);
        Assert.True((mode & UnixFileMode.UserWrite) != 0);
        Assert.True((mode & UnixFileMode.GroupExecute) == 0);
    }

    [Fact]
    public async Task SwapStreamline_RepairsExecuteOnlyCachedDll_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var cacheRoot = Path.Combine(_root, "cache-streamline-permissions");
        var cacheDir = Path.Combine(cacheRoot, "Streamline", "2.13.0.0");
        Directory.CreateDirectory(cacheDir);
        var cached = Path.Combine(cacheDir, "sl.common.dll");
        var cachedBytes = Enumerable.Repeat((byte)0x6B, 80_000).ToArray();
        await File.WriteAllBytesAsync(cached, cachedBytes);
        File.SetUnixFileMode(cached, UnixFileMode.GroupExecute);

        var folder = Path.Combine(_root, "streamline-permission-game");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "sl.common.dll");
        await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0x21, 70_000).ToArray());

        const string manifest = "{ \"streamline\": [{ \"version\": \"2.13.0.0\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), [])),
            new TransactionalFileDeploymentService(),
            cacheRoot);
        await service.FetchManifestAsync();

        var result = await service.SwapStreamlineAsync(new[] { folder }, "2.13.0.0");

        Assert.True(result.Success);
        Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(target));
        var mode = File.GetUnixFileMode(cached);
        Assert.True((mode & UnixFileMode.UserRead) != 0);
        Assert.True((mode & UnixFileMode.GroupExecute) == 0);
    }

    [Fact]
    public void Constructor_ProactivelyRepairsAllLegacyNvidiaCacheDllPermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var cacheRoot = Path.Combine(_root, "cache-startup-sweep");
        var legacyFiles = new[]
        {
            Path.Combine(cacheRoot, "DLSS-G", "310.9.0", "nvngx_dlssg.dll"),
            Path.Combine(cacheRoot, "DLSS-D", "310.9.0", "nvngx_dlssd.dll"),
            Path.Combine(cacheRoot, "Streamline", "2.13.0.0", "sl.deepdvc.dll"),
            Path.Combine(cacheRoot, "Streamline", "2.13.0.0", "sl.directsr.dll"),
            Path.Combine(cacheRoot, "Streamline", "2.13.0.0", "sl.nvperf.dll")
        };

        foreach (var file in legacyFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Enumerable.Repeat((byte)0x5A, 80_000).ToArray());
            File.SetUnixFileMode(file, UnixFileMode.GroupExecute);
        }

        var unrelated = Path.Combine(cacheRoot, "addons", "foreign.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllBytes(unrelated, Enumerable.Repeat((byte)0x33, 80_000).ToArray());
        File.SetUnixFileMode(unrelated, UnixFileMode.GroupExecute);

        _ = new DlssManagerService(
            new HttpClient(new FakeHandler([], [])),
            new TransactionalFileDeploymentService(),
            cacheRoot);

        foreach (var file in legacyFiles)
        {
            var mode = File.GetUnixFileMode(file);
            Assert.True((mode & UnixFileMode.UserRead) != 0);
            Assert.True((mode & UnixFileMode.UserWrite) != 0);
            Assert.True((mode & UnixFileMode.GroupExecute) == 0);
        }

        Assert.Equal(UnixFileMode.GroupExecute, File.GetUnixFileMode(unrelated));
    }

    [Fact]
    public async Task FetchManifest_NeuralRenderingAcceptsShortFuseTaggedVersionWithoutSemVerParsing()
    {
        const string manifest = "{ \"dlssnr\": [{ \"version\": \"310.8.SF-v2\", \"url\": \"https://github.com/RankFTW/rhi-repo/releases/download/test/nr.zip\" }] }";
        var service = new DlssManagerService(
            new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes(manifest), [])),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-nr-sf-tag"));

        await service.FetchManifestAsync();

        Assert.Equal(new[] { "310.8.SF-v2" }, service.Versions(DlssComponentKind.NeuralRendering));
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
