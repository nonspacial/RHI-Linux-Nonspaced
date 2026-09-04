using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Cross-platform DLSS/Streamline manager using the same public upstream manifest as RHI.
/// It does not use NvAPI; it only manages game-local DLLs and therefore works with Wine/Proton.
/// </summary>
public sealed class DlssManagerService
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/RankFTW/RHI/main/dlss_manifest.json";

    public static readonly string[] KnownStreamlineDlls =
    [
        "sl.common.dll", "sl.deepdvc.dll", "sl.directsr.dll", "sl.dlss.dll", "sl.dlss_d.dll",
        "sl.dlss_g.dll", "sl.interposer.dll", "sl.nis.dll", "sl.nvperf.dll", "sl.pcl.dll", "sl.reflex.dll"
    ];

    private readonly HttpClient _http;
    private readonly IFileDeploymentService _deployment;
    private readonly string _cacheRoot;
    private DlssManifestData? _manifest;

    public DlssManagerService(HttpClient http, IFileDeploymentService deployment, string cacheRoot)
    {
        _http = http;
        _deployment = deployment;
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(_cacheRoot);
    }

    public DlssManifestData? Manifest => _manifest;

    public async Task<DlssManifestData> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(_cacheRoot, "dlss_manifest.json");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RHI-Linux", "0.1.0"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DlssManifestData>(json, JsonOptions())
                         ?? throw new InvalidDataException("DLSS manifest was empty or invalid.");
            ValidateManifest(parsed);
            _manifest = parsed;
            await File.WriteAllTextAsync(cachePath, json, cancellationToken).ConfigureAwait(false);
            return parsed;
        }
        catch when (File.Exists(cachePath))
        {
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DlssManifestData>(json, JsonOptions())
                         ?? throw new InvalidDataException("Cached DLSS manifest is invalid.");
            ValidateManifest(parsed);
            _manifest = parsed;
            return parsed;
        }
    }

    public DlssDetectionResult Detect(string gameRoot)
    {
        if (!Directory.Exists(gameRoot)) return new();
        var dlss = new List<string>();
        var dlssd = new List<string>();
        var dlssg = new List<string>();
        var dlssnr = new List<string>();
        var streamline = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateCandidateDlls(gameRoot, maxDepth: 8))
        {
            switch (Path.GetFileName(file).ToLowerInvariant())
            {
                case "nvngx_dlss.dll": dlss.Add(file); break;
                case "nvngx_dlssd.dll": dlssd.Add(file); break;
                case "nvngx_dlssg.dll": dlssg.Add(file); break;
                case "nvngx_dlssnr.dll": dlssnr.Add(file); break;
                case "sl.interposer.dll":
                case "sl.common.dll":
                    if (Path.GetDirectoryName(file) is { } dir) streamline.Add(dir);
                    break;
            }
        }

        return new()
        {
            DlssPaths = PreferLikelyGamePaths(dlss, gameRoot),
            DlssdPaths = PreferLikelyGamePaths(dlssd, gameRoot),
            DlssgPaths = PreferLikelyGamePaths(dlssg, gameRoot),
            DlssnrPaths = PreferLikelyGamePaths(dlssnr, gameRoot),
            StreamlineFolders = streamline.OrderBy(x => RelativeDepth(gameRoot, x)).ToArray()
        };
    }

    public IReadOnlyList<string> Versions(DlssComponentKind kind)
    {
        var entries = Entries(kind);
        return entries.Select(x => x.Version).ToArray();
    }

    public async Task<FileDeploymentBatchResult> SwapAsync(
        DlssComponentKind kind,
        IEnumerable<string> targetDlls,
        string version,
        CancellationToken cancellationToken = default)
    {
        var entry = Entries(kind).FirstOrDefault(x => string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new(false, [], $"Version '{version}' does not exist in the current manifest.");

        var expectedDll = DllName(kind);
        var cachedDll = await DownloadDllAsync(entry, expectedDll, CacheSubdir(kind), cancellationToken).ConfigureAwait(false);
        var targets = targetDlls.Where(File.Exists).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0) return new(false, [], $"No existing {expectedDll} target was found. RHI will not invent a target path.");

        return await _deployment.ReplaceBatchAsync(targets.Select(t => (cachedDll, t)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileDeploymentBatchResult> RestoreAsync(
        DlssComponentKind kind,
        IEnumerable<string> targetDlls,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FileDeploymentResult>();
        foreach (var target in targetDlls.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _deployment.RestoreAsync(target, cancellationToken).ConfigureAwait(false));
        }
        return new(results.Count > 0 && results.All(r => r.Success), results,
            results.FirstOrDefault(r => !r.Success)?.Error);
    }

    public async Task<FileDeploymentBatchResult> SwapStreamlineAsync(
        string targetFolder,
        string version,
        CancellationToken cancellationToken = default)
    {
        var entry = (_manifest?.Streamline ?? []).FirstOrDefault(x => string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new(false, [], $"Streamline version '{version}' does not exist in the current manifest.");
        if (!Directory.Exists(targetFolder)) return new(false, [], "Streamline target folder does not exist.");

        var cacheDir = Path.Combine(_cacheRoot, "Streamline", SanitizeVersion(version));
        await DownloadStreamlineAsync(entry, cacheDir, cancellationToken).ConfigureAwait(false);

        var replacements = KnownStreamlineDlls
            .Select(name => (Source: Path.Combine(cacheDir, name), Target: Path.Combine(targetFolder, name)))
            .Where(x => File.Exists(x.Source) && File.Exists(x.Target))
            .ToArray();
        if (replacements.Length == 0)
            return new(false, [], "No existing Streamline DLLs matched the downloaded set; refusing to create a guessed layout.");

        return await _deployment.ReplaceBatchAsync(replacements, cancellationToken).ConfigureAwait(false);
    }

    private List<DlssManifestEntry> Entries(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => _manifest?.Dlss ?? [],
        DlssComponentKind.RayReconstruction => _manifest?.Dlssd ?? [],
        DlssComponentKind.FrameGeneration => _manifest?.Dlssg ?? [],
        DlssComponentKind.NeuralRendering => _manifest?.Dlssnr ?? [],
        _ => []
    };

    private static string DllName(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => "nvngx_dlss.dll",
        DlssComponentKind.RayReconstruction => "nvngx_dlssd.dll",
        DlssComponentKind.FrameGeneration => "nvngx_dlssg.dll",
        DlssComponentKind.NeuralRendering => "nvngx_dlssnr.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string CacheSubdir(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => "DLSS",
        DlssComponentKind.RayReconstruction => "DLSS-D",
        DlssComponentKind.FrameGeneration => "DLSS-G",
        DlssComponentKind.NeuralRendering => "DLSS-NR",
        _ => "DLSS"
    };

    private async Task<string> DownloadDllAsync(
        DlssManifestEntry entry,
        string expectedDll,
        string componentDir,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(entry.Url);
        var cacheDir = Path.Combine(_cacheRoot, componentDir, SanitizeVersion(entry.Version));
        var cachedDll = Path.Combine(cacheDir, expectedDll);
        if (File.Exists(cachedDll) && new FileInfo(cachedDll).Length > 64 * 1024) return cachedDll;

        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, "download.zip.part");
        await DownloadFileAsync(entry.Url, zipPath, cancellationToken).ConfigureAwait(false);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var match = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), expectedDll, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Downloaded archive does not contain {expectedDll}.");
            var temp = cachedDll + ".tmp";
            match.ExtractToFile(temp, overwrite: true);
            if (new FileInfo(temp).Length < 64 * 1024) throw new InvalidDataException($"Extracted {expectedDll} is unexpectedly small.");
            File.Move(temp, cachedDll, overwrite: true);
        }
        File.Delete(zipPath);
        return cachedDll;
    }

    private async Task DownloadStreamlineAsync(DlssManifestEntry entry, string cacheDir, CancellationToken ct)
    {
        if (KnownStreamlineDlls.Any(name => File.Exists(Path.Combine(cacheDir, name)))) return;
        ValidateDownloadUrl(entry.Url);
        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, "download.zip.part");
        await DownloadFileAsync(entry.Url, zipPath, ct).ConfigureAwait(false);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var dll in KnownStreamlineDlls)
            {
                var match = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), dll, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                match.ExtractToFile(Path.Combine(cacheDir, dll), overwrite: true);
            }
        }
        File.Delete(zipPath);
        if (!KnownStreamlineDlls.Any(name => File.Exists(Path.Combine(cacheDir, name))))
            throw new InvalidDataException("Streamline archive contained no recognized DLLs.");
    }

    private async Task DownloadFileAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static void ValidateManifest(DlssManifestData manifest)
    {
        var all = manifest.Dlss.Concat(manifest.Dlssd).Concat(manifest.Dlssg).Concat(manifest.Dlssnr).Concat(manifest.Streamline).ToArray();
        if (all.Length == 0) throw new InvalidDataException("DLSS manifest has no entries.");
        foreach (var entry in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Version)) throw new InvalidDataException("Manifest entry has no version.");
            ValidateDownloadUrl(entry.Url);
        }
    }

    private static void ValidateDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Manifest contains a non-HTTPS download URL.");
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unexpected DLSS download host: {uri.Host}");
    }

    private static IEnumerable<string> EnumerateCandidateDlls(string root, int maxDepth)
    {
        var rootDepth = RelativeDepth(root, root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var depth = RelativeDepth(root, dir) - rootDepth;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.dll"); }
            catch { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("nvngx_dlss", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("sl.common.dll", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var n = Path.GetFileName(sub);
                    if (n.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(sub);
                }
            }
            catch { }
        }
    }

    private static IReadOnlyList<string> PreferLikelyGamePaths(IEnumerable<string> paths, string gameRoot)
        => paths.Distinct(StringComparer.Ordinal)
            .OrderBy(p => p.Contains("Engine/Binaries", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => RelativeDepth(gameRoot, Path.GetDirectoryName(p) ?? gameRoot))
            .ToArray();

    private static int RelativeDepth(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel == ".") return 0;
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        }
        catch { return int.MaxValue / 2; }
    }

    private static string SanitizeVersion(string version)
        => string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}
