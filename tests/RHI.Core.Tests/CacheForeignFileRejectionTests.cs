using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #142: every download cache that hands out an executable payload must refuse a foreign file sitting
/// under the cached name, even when the sidecars/markers that used to drive reuse (".source", size
/// marker, ".complete") are still in place. Each case downloads the genuine entry once, swaps the
/// cached entry for foreign bytes of the same size (with the stamp removed, or the old stamp left
/// behind), and requires the next fetch to redo the download/extraction and hand back genuine bytes.
/// </summary>
public sealed class CacheForeignFileRejectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-cache-foreign-tests-" + Guid.NewGuid().ToString("N"));

    public CacheForeignFileRejectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    public static IEnumerable<object[]> Cases()
    {
        string[] consumers =
        [
            "ShortFuseDirect", "ShortFuseArchive", "Ual", "Rtx40Mfg", "DlssEnabler", "Dlssg2030",
            "ReShadeSetupExe", "ReShadeSetupStagedDll", "ReShadeNightly", "OptiScalerArchive",
            "OptiScalerExtractedTree", "DgVoodoo2", "RenoDx", "ShaderPack"
        ];
        foreach (var consumer in consumers)
        {
            yield return [consumer, false];
            yield return [consumer, true];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ForeignFileUnderCachedName_IsRejectedAndReplaced(string consumer, bool keepOldStamp)
    {
        var dir = Path.Combine(_root, consumer + (keepOldStamp ? "-stale" : "-unstamped"));
        Directory.CreateDirectory(dir);
        var c = Build(consumer, dir);

        await c.Fetch();
        var entry = c.Entry();
        Assert.True(File.Exists(CacheStamp.StampPath(entry)), $"{consumer}: first fetch must stamp {entry}");
        var genuine = HashEntry(entry);
        var workBefore = c.Work();

        PlantForeign(entry, keepOldStamp);
        Assert.NotEqual(genuine, HashEntry(entry));
        c.BeforeRefetch?.Invoke();

        await c.Fetch();

        Assert.True(c.Work() > workBefore, $"{consumer}: the foreign entry was reused instead of being fetched again");
        Assert.Equal(genuine, HashEntry(c.Entry()));
        Assert.True(File.Exists(CacheStamp.StampPath(c.Entry())), $"{consumer}: replacement must be stamped again");
    }

    private static string GitBlob(byte[] content)
        => Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content])).ToLowerInvariant();

    private sealed record Case(Func<Task> Fetch, Func<string> Entry, Func<int> Work, Action? BeforeRefetch = null);

    private Case Build(string consumer, string dir)
    {
        var http = new CountingHandler();
        var client = new HttpClient(http);
        var records = new LinuxDeploymentRecordStore(Path.Combine(dir, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var cache = Path.Combine(dir, "cache");
        string? entry = null;

        switch (consumer)
        {
            case "ShortFuseDirect":
            case "ShortFuseArchive":
            {
                var addon = Pe(0x8664, 80 * 1024, 0x21);
                var archive = consumer == "ShortFuseArchive";
                var url = archive ? "https://github.com/rhi-tests/sf/renodx-dlss_SF_2.0.zip" : "https://github.com/rhi-tests/sf/renodx-dlss.addon64";
                var body = archive ? Zip(("payload/renodx-dlss.addon64", addon)) : addon;
                http.Serve(url, body);
                var digest = archive ? Hex(body) : null;
                var sf = new ShortFuseDeploymentService(client, new TransactionalFileDeploymentService(), records, overrides,
                    new UltimateAsiLoaderDeploymentService(client, records, overrides, Path.Combine(dir, "cache-ual")),
                    new DlssManagerService(client, new TransactionalFileDeploymentService(), Path.Combine(dir, "cache-dlss")),
                    cache, new ShortFuseSettingsStore(Path.Combine(dir, "sf-settings.json")));
                var release = new ShortFuseReleaseInfo("2.0", "renodx-dlss-SF-2.0", url, digest, archive,
                    archive ? "renodx-dlss_SF_2.0.zip" : ShortFuseDeploymentService.AddonFileName);
                return new(async () => entry = await sf.EnsureCachedAddonAsync(release, CancellationToken.None), () => entry!, () => http.Count(url));
            }
            case "Ual":
            {
                var url = "https://github.com/rhi-tests/ual/Ultimate-ASI-Loader_x64.zip";
                http.Serve(url, Zip(("dinput8.dll", Pe(0x8664, 80 * 1024, 0x31))));
                var ual = new UltimateAsiLoaderDeploymentService(client, records, overrides, cache);
                var release = new UalReleaseInfo("9.7.4", "Ultimate-ASI-Loader_x64.zip", url, null);
                return new(async () => entry = await ual.EnsureCachedDllAsync(release, PeArchitecture.X64, CancellationToken.None), () => entry!, () => http.Count(url));
            }
            case "Rtx40Mfg":
            {
                var url = "https://github.com/rhi-tests/rtx40/RTX40MFG.zip";
                http.Serve(url, Zip((Rtx40MfgStandaloneDeploymentService.InnerDllName, Pe(0x8664, 300 * 1024, 0x41))));
                var svc = new Rtx40MfgStandaloneDeploymentService(client, records, overrides, new Rtx40MfgPreferenceStore(Path.Combine(dir, "rtx40.json")), cache);
                var release = new Rtx40MfgReleaseInfo("1.0", "v1.0", "RTX40MFG.zip", url, null, null);
                return new(async () => entry = await svc.EnsureCachedPayloadAsync(release, CancellationToken.None), () => entry!, () => http.Count(url));
            }
            case "DlssEnabler":
            {
                var url = "https://github.com/rhi-tests/enabler/version.dll";
                http.Serve(url, Pe(0x8664, 200 * 1024, 0x51));
                var svc = new StandaloneDlssEnablerDeploymentService(client, records, overrides, new StandaloneDlssEnablerPreferenceStore(Path.Combine(dir, "enabler.json")), cache);
                var release = new DlssEnablerReleaseInfo("1.0", "v1.0", "version.dll", url, null);
                return new(async () => entry = await svc.EnsureCachedPayloadAsync(release, CancellationToken.None), () => entry!, () => http.Count(url));
            }
            case "Dlssg2030":
            {
                const string sha = "0123456789abcdef0123456789abcdef01234567";
                var dllUrl = Dlssg2030UnlockDeploymentService.RawUrl(sha, Dlssg2030UnlockDeploymentService.DefaultProxyName);
                var iniUrl = Dlssg2030UnlockDeploymentService.RawUrl(sha, Dlssg2030UnlockDeploymentService.IniFileName);
                var dll = Pe(0x8664, 300 * 1024, 0x61);
                var ini = Encoding.UTF8.GetBytes("; Native 1.2.3\n[Compatibility]\nRouter=SM86\n[FrameGeneration]\nMaxGeneratedFrames=3\n");
                http.Serve(dllUrl, dll);
                http.Serve(iniUrl, ini);
                // The git blob ids GitHub reports for both files at that commit (#41).
                http.Serve(Dlssg2030UnlockDeploymentService.ContentsApiUrl(sha, Dlssg2030UnlockDeploymentService.DefaultProxyName),
                    Encoding.UTF8.GetBytes($"{{\"sha\":\"{GitBlob(dll)}\"}}"));
                http.Serve(Dlssg2030UnlockDeploymentService.ContentsApiUrl(sha, Dlssg2030UnlockDeploymentService.IniFileName),
                    Encoding.UTF8.GetBytes($"{{\"sha\":\"{GitBlob(ini)}\"}}"));
                var svc = new Dlssg2030UnlockDeploymentService(client, records, overrides, new Dlssg2030PreferenceStore(Path.Combine(dir, "fg2030.json")), cache);
                return new(async () => entry = (await svc.EnsureCachedPayloadAsync(new Dlssg2030SnapshotInfo(sha, "1.2.3"), CancellationToken.None)).DllPath,
                    () => entry!, () => http.Count(dllUrl));
            }
            case "ReShadeSetupExe":
            case "ReShadeSetupStagedDll":
            {
                var url = $"{ReShadeDeploymentService.HomeUrl}/downloads/ReShade_Setup_6.4.1_Addon.exe";
                http.Serve(url, Pe(0x8664, 600_000, 0x71));
                var extractor = new ReShadeExtractor();
                var svc = new ReShadeDeploymentService(client, new TransactionalFileDeploymentService(), records, overrides, extractor, cache, ShaderPacks(client, dir));
                var versionDir = Path.Combine(cache, "legacy", "6.4.1");
                var setup = Path.Combine(versionDir, "ReShade_Setup_6.4.1_Addon.exe");
                // A valid staged DLL short-circuits before the setup is consulted; drop it so the
                // (foreign) setup executable is what the next fetch has to deal with.
                return consumer == "ReShadeSetupExe"
                    ? new(async () => await svc.EnsureLegacyStagedAsync("6.4.1", PeArchitecture.X64), () => setup, () => http.Count(url),
                        () => File.Delete(Path.Combine(versionDir, "ReShade64.dll")))
                    : new(async () => entry = (await svc.EnsureLegacyStagedAsync("6.4.1", PeArchitecture.X64)).DllPath, () => entry!, () => extractor.Calls);
            }
            case "ReShadeNightly":
            {
                var url = ReShadeDeploymentService.Nightly64Url;
                http.Serve(url, Zip(("ReShade64.dll", ReShadeExtractor.Dll(0x8664, 0x11))));
                var svc = new ReShadeDeploymentService(client, new TransactionalFileDeploymentService(), records, overrides, new ReShadeExtractor(), cache, ShaderPacks(client, dir));
                return new(async () => entry = (await svc.EnsureNightlyStagedAsync(PeArchitecture.X64)).DllPath, () => entry!, () => http.Count(url));
            }
            case "OptiScalerArchive":
            case "OptiScalerExtractedTree":
            {
                var archiveBytes = new byte[1_200_000];
                new Random(4242).NextBytes(archiveBytes);
                var assetUrl = "https://github.com/rhi-tests/opti/Optiscaler_9.9.9.7z";
                http.Serve(OptiScalerPackageProvider.LatestReleaseApi, Encoding.UTF8.GetBytes(
                    $$"""{"tag_name":"v9.9.9","assets":[{"name":"Optiscaler_9.9.9.7z","browser_download_url":"{{assetUrl}}","size":{{archiveBytes.Length}},"digest":"sha256:{{Hex(archiveBytes)}}"}]}"""));
                http.Serve(assetUrl, archiveBytes);
                var extractor = new OptiExtractor();
                var provider = new OptiScalerPackageProvider(client, extractor, cache);
                var versionDir = Path.Combine(cache, "v9.9.9");
                return consumer == "OptiScalerArchive"
                    ? new(async () => await provider.GetLatestAsync(), () => Path.Combine(versionDir, "Optiscaler_9.9.9.7z"), () => http.Count(assetUrl))
                    : new(async () => await provider.GetLatestAsync(), () => Path.Combine(versionDir, "extract"), () => extractor.Calls);
            }
            case "DgVoodoo2":
            {
                var url = "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.86/dgVoodoo2_86.zip";
                http.Serve(url, Zip(("MS/x64/D3D9.dll", Pe(0x8664, 80 * 1024, 0x81)), ("MS/x86/D3D9.dll", Pe(0x014c, 80 * 1024, 0x82))));
                var svc = new DgVoodooDeploymentService(client, cache);
                return new(async () => await svc.PrepareManifestVersionAsync("2.86", url, PeArchitecture.X64),
                    () => Path.Combine(cache, "2.86", "dgVoodoo2_86.zip"), () => http.Count(url));
            }
            case "RenoDx":
            {
                var url = "https://github.com/test/renodx/renodx-test.addon64";
                http.Serve(url, Pe(0x8664, 150 * 1024, 0x91));
                var svc = new RenoDxDeploymentService(client, records, new RenoDxCatalogService(client), cache);
                return new(async () => entry = (await svc.DownloadAsync(url, "renodx-test.addon64", CancellationToken.None)).Path, () => entry!, () => http.Count(url));
            }
            case "ShaderPack":
            {
                var url = "https://github.com/rhi-tests/shaders/pack.zip";
                var fx = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("// genuine shader body\n", 64)));
                http.Serve(url, Zip(("pack-main/Shaders/Genuine.fx", fx), ("pack-main/Textures/Genuine.png", Pe(0x8664, 4096, 0xA1))));
                var svc = new ReShadeShaderPackService(client, cache, null, Path.Combine(dir, "custom-shaders"));
                var pack = new ReShadeShaderPackDefinition("ForeignTestPack", "Foreign test pack", "test", ReShadeShaderPackSourceKind.DirectUrl, url);
                return new(async () => entry = await svc.EnsurePackAsync(pack, CancellationToken.None), () => entry!, () => http.Count(url));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(consumer), consumer, null);
        }
    }

    private static ReShadeShaderPackService ShaderPacks(HttpClient client, string dir)
        => new(client, Path.Combine(dir, "shader-packs"), null, Path.Combine(dir, "custom-shaders"));

    /// <summary>
    /// Swaps the entry (first file of a directory) for a foreign file of the same size that keeps the
    /// genuine header and signature strings (first 1 KiB, or half of a smaller file), so PE, machine,
    /// size and signature checks all still pass and only the stamp can tell it apart.
    /// </summary>
    private static void PlantForeign(string entry, bool keepOldStamp)
    {
        if (!keepOldStamp) File.Delete(CacheStamp.StampPath(entry));
        var target = Directory.Exists(entry)
            ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First()
            : entry;
        var bytes = File.ReadAllBytes(target);
        var keep = Math.Min(1024, bytes.Length / 2);
        for (var i = keep; i < bytes.Length; i++) bytes[i] ^= 0x5A;
        File.WriteAllBytes(target, bytes);
    }

    private static string HashEntry(string entry)
    {
        if (File.Exists(entry)) return Hex(File.ReadAllBytes(entry));
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(Path.GetRelativePath(entry, file)).Append('=').Append(Hex(File.ReadAllBytes(file))).Append('\n');
        return sb.ToString();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static byte[] Pe(ushort machine, int size, byte marker)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(marker + i * 13);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                stream.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Serves fixed bodies by absolute URL (GET and HEAD, with Content-Length) and counts GETs.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _gets = new(StringComparer.Ordinal);

        public void Serve(string url, byte[] body) => _bodies[new Uri(url).AbsoluteUri] = body;
        public int Count(string url) => _gets.GetValueOrDefault(new Uri(url).AbsoluteUri);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.AbsoluteUri;
            if (!_bodies.TryGetValue(key, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Get) _gets.AddOrUpdate(key, 1, (_, n) => n + 1);
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ReShadeExtractor : IReShadeArchiveExtractor
    {
        public int Calls { get; private set; }

        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, "ReShade64.dll"), Dll(0x8664, 0x64));
            File.WriteAllBytes(Path.Combine(outputDirectory, "ReShade32.dll"), Dll(0x014c, 0x32));
            return Task.CompletedTask;
        }

        public static byte[] Dll(ushort machine, byte marker)
        {
            var bytes = Pe(machine, 1_100_000, marker);
            Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(bytes, 512);
            return bytes;
        }
    }

    private sealed class OptiExtractor : IOptiScalerArchiveExtractor
    {
        public int Calls { get; private set; }

        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            var root = Path.Combine(outputDirectory, "package");
            Directory.CreateDirectory(root);
            var dll = Pe(0x8664, 300_000, 0x0F);
            Encoding.ASCII.GetBytes("OptiScaler official package").CopyTo(dll, 512);
            File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), dll);
            File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
            return Task.CompletedTask;
        }
    }
}
