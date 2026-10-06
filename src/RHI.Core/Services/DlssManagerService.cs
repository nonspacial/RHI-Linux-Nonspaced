using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using RHI.Core;
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
        RepairLegacyCachePermissions();
    }

    public DlssManifestData? Manifest => _manifest;

    public async Task<DlssManifestData> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(_cacheRoot, "dlss_manifest.json");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RHI-Linux", RhiProductInfo.Version));
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

    public DlssDetectionResult Detect(
        string gameRoot,
        string? executablePath = null,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(gameRoot)) return new();
        var dlss = new List<string>();
        var dlssd = new List<string>();
        var dlssg = new List<string>();
        var dlssnr = new List<string>();
        var streamline = new HashSet<string>(StringComparer.Ordinal);

        indexedFiles ??= GameFileIndexer.Build(gameRoot, 8, cancellationToken);
        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        var binaryDirectory = !string.IsNullOrWhiteSpace(executablePath)
            ? Path.GetDirectoryName(executablePath)
            : null;

        return new()
        {
            DlssPaths = PreferLikelyGamePaths(dlss, gameRoot, binaryDirectory),
            DlssdPaths = PreferLikelyGamePaths(dlssd, gameRoot, binaryDirectory),
            DlssgPaths = PreferLikelyGamePaths(dlssg, gameRoot, binaryDirectory),
            DlssnrPaths = PreferLikelyGamePaths(dlssnr, gameRoot, binaryDirectory),
            StreamlineFolders = streamline
                .OrderBy(x => PathAffinity(x, binaryDirectory, gameRoot))
                .ThenBy(x => RelativeDepth(gameRoot, x))
                .ToArray()
        };
    }

    public IReadOnlyList<string> Versions(DlssComponentKind kind)
    {
        var entries = Entries(kind);
        return entries.Select(x => x.Version).ToArray();
    }

    /// <summary>Best-effort PE version read used by Quick Apply to retain upstream DLSS 1.x protection.</summary>
    public static string? TryGetDllVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            var raw = !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var token = raw.Trim().Split(new[] { ' ', '+', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ensures the newest payload for one DLSS component is cached and returns its readable DLL path.
    /// Unlike SwapAsync this does not require an existing game target and is used by orchestrated
    /// workflows such as DLSS5 Feeder that intentionally create a complete private stack.
    /// </summary>
    public async Task<string?> EnsureNewestCachedAsync(
        DlssComponentKind kind,
        CancellationToken cancellationToken = default)
    {
        if (_manifest is null)
            await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        var entry = Entries(kind).FirstOrDefault();
        if (entry is null) return null;
        return await DownloadDllAsync(entry, DllName(kind), CacheSubdir(kind), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures one manifest version of a DLSS component is cached and returns its DLL path. A null
    /// or no-longer-published version falls back to the newest entry, as upstream RHI does.
    /// </summary>
    public async Task<string?> EnsureCachedAsync(
        DlssComponentKind kind,
        string? version,
        CancellationToken cancellationToken = default)
        => (await EnsureCachedVersionAsync(kind, version, cancellationToken).ConfigureAwait(false))?.Path;

    /// <summary>
    /// As <see cref="EnsureCachedAsync"/>, but also returns the manifest version actually cached,
    /// which differs from <paramref name="version"/> when that version is no longer published.
    /// </summary>
    public async Task<(string Path, string Version)?> EnsureCachedVersionAsync(
        DlssComponentKind kind,
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (_manifest is null)
            await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        var entries = Entries(kind);
        var entry = entries.FirstOrDefault(x => string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault();
        if (entry is null) return null;
        var path = await DownloadDllAsync(entry, DllName(kind), CacheSubdir(kind), cancellationToken).ConfigureAwait(false);
        return (path, entry.Version);
    }

    /// <summary>Ensures the newest Streamline package is cached and returns its directory.</summary>
    public async Task<string?> EnsureNewestStreamlineCachedAsync(CancellationToken cancellationToken = default)
    {
        if (_manifest is null)
            await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        var entry = (_manifest?.Streamline ?? []).FirstOrDefault();
        if (entry is null) return null;
        var cacheDir = Path.Combine(_cacheRoot, "Streamline", SanitizeVersion(entry.Version));
        await DownloadStreamlineAsync(entry, cacheDir, cancellationToken).ConfigureAwait(false);
        return cacheDir;
    }

    /// <summary>Ensures one exact Streamline version is cached and returns its directory.</summary>
    public async Task<string?> EnsureStreamlineCachedAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (_manifest is null)
            await FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        var entry = (_manifest?.Streamline ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.Version, version, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        var cacheDir = Path.Combine(_cacheRoot, "Streamline", SanitizeVersion(entry.Version));
        await DownloadStreamlineAsync(entry, cacheDir, cancellationToken).ConfigureAwait(false);
        return cacheDir;
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

    public Task<FileDeploymentBatchResult> RestoreAsync(
        DlssComponentKind kind,
        IEnumerable<string> targetDlls,
        CancellationToken cancellationToken = default)
    {
        var targets = targetDlls.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
            return Task.FromResult(new FileDeploymentBatchResult(false, [], $"No existing {DllName(kind)} target was found."));
        return _deployment.RestoreBatchAsync(targets, cancellationToken);
    }

    public Task<FileDeploymentBatchResult> SwapStreamlineAsync(
        string targetFolder,
        string version,
        CancellationToken cancellationToken = default)
        => SwapStreamlineAsync(new[] { targetFolder }, version, cancellationToken);

    public async Task<FileDeploymentBatchResult> SwapStreamlineAsync(
        IEnumerable<string> targetFolders,
        string version,
        CancellationToken cancellationToken = default)
    {
        var entry = (_manifest?.Streamline ?? []).FirstOrDefault(x => string.Equals(x.Version, version, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new(false, [], $"Streamline version '{version}' does not exist in the current manifest.");

        var folders = targetFolders
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (folders.Length == 0) return new(false, [], "No detected Streamline target folder exists.");

        var cacheDir = Path.Combine(_cacheRoot, "Streamline", SanitizeVersion(version));
        await DownloadStreamlineAsync(entry, cacheDir, cancellationToken).ConfigureAwait(false);

        // Upstream RHI only swaps DLLs that already exist in the game's Streamline layout.
        // Keep the same invariant on Linux: never create guessed sl.* targets.
        var replacements = folders
            .SelectMany(folder => KnownStreamlineDlls.Select(name =>
                (Source: Path.Combine(cacheDir, name), Target: Path.Combine(folder, name))))
            .Where(x => File.Exists(x.Source) && File.Exists(x.Target))
            .DistinctBy(x => x.Target, StringComparer.Ordinal)
            .ToArray();
        if (replacements.Length == 0)
            return new(false, [], "No existing Streamline DLLs matched the downloaded set; refusing to create a guessed layout.");

        return await _deployment.ReplaceBatchAsync(replacements, cancellationToken).ConfigureAwait(false);
    }

    public Task<FileDeploymentBatchResult> RestoreStreamlineAsync(
        string targetFolder,
        CancellationToken cancellationToken = default)
        => RestoreStreamlineAsync(new[] { targetFolder }, cancellationToken);

    public Task<FileDeploymentBatchResult> RestoreStreamlineAsync(
        IEnumerable<string> targetFolders,
        CancellationToken cancellationToken = default)
    {
        var targets = targetFolders
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(folder => KnownStreamlineDlls.Select(name => Path.Combine(folder, name)))
            .Where(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (targets.Length == 0)
            return Task.FromResult(new FileDeploymentBatchResult(false, [], "No RHI-created Streamline .original backups were found."));

        return _deployment.RestoreBatchAsync(targets, cancellationToken);
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
        if (File.Exists(cachedDll))
        {
            // Some upstream ZIP entries carry Unix external attributes that System.IO.Compression
            // can materialize as execute-only files (for example mode 0010). Repair older
            // cache entries in place before deciding whether the cached payload is reusable.
            NormalizeCacheDllPermissions(cachedDll);
            if (IsReadableCachedDll(cachedDll)) return cachedDll;
            TryDeleteCacheFile(cachedDll);
        }

        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, "download.zip.part");
        await DownloadFileAsync(entry.Url, zipPath, cancellationToken).ConfigureAwait(false);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var match = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), expectedDll, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Downloaded archive does not contain {expectedDll}.");
            var temp = cachedDll + ".tmp";
            match.ExtractToFile(temp, overwrite: true);
            NormalizeCacheDllPermissions(temp);
            if (!IsReadableCachedDll(temp))
                throw new InvalidDataException($"Extracted {expectedDll} is unreadable or unexpectedly small.");
            File.Move(temp, cachedDll, overwrite: true);
            NormalizeCacheDllPermissions(cachedDll);
        }
        File.Delete(zipPath);
        if (!IsReadableCachedDll(cachedDll))
            throw new IOException($"Cached {expectedDll} is not readable after extraction: {cachedDll}");
        return cachedDll;
    }

    private async Task DownloadStreamlineAsync(DlssManifestEntry entry, string cacheDir, CancellationToken ct)
    {
        var existing = KnownStreamlineDlls
            .Select(name => Path.Combine(cacheDir, name))
            .Where(File.Exists)
            .ToArray();
        if (existing.Length > 0)
        {
            foreach (var path in existing) NormalizeCacheDllPermissions(path);
            if (existing.All(IsReadableCachedDll)) return;
            foreach (var path in existing.Where(path => !IsReadableCachedDll(path))) TryDeleteCacheFile(path);
        }

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
                var extracted = Path.Combine(cacheDir, dll);
                match.ExtractToFile(extracted, overwrite: true);
                NormalizeCacheDllPermissions(extracted);
                if (!IsReadableCachedDll(extracted))
                    throw new InvalidDataException($"Extracted Streamline DLL is unreadable or unexpectedly small: {dll}");
            }
        }
        File.Delete(zipPath);
        if (!KnownStreamlineDlls.Any(name => File.Exists(Path.Combine(cacheDir, name)) && IsReadableCachedDll(Path.Combine(cacheDir, name))))
            throw new InvalidDataException("Streamline archive contained no readable recognized DLLs.");
    }


    private void RepairLegacyCachePermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        // Earlier code repaired a cache entry only when that exact version was selected.
        // Untouched legacy payloads could therefore remain
        // mode 0010 indefinitely. Sweep only RHI-owned NVIDIA cache namespaces at
        // service startup so every known DLSS/Streamline payload is readable before
        // the user chooses a version. Unrelated add-on caches are intentionally out
        // of scope.
        var roots = new[] { "DLSS", "DLSS-D", "DLSS-G", "DLSS-NR", "Streamline" };
        var knownNames = new HashSet<string>(KnownStreamlineDlls, StringComparer.OrdinalIgnoreCase)
        {
            "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll", "nvngx_dlssnr.dll"
        };

        foreach (var component in roots)
        {
            var componentRoot = Path.Combine(_cacheRoot, component);
            if (!Directory.Exists(componentRoot)) continue;

            string[] dlls;
            try
            {
                dlls = Directory.EnumerateFiles(componentRoot, "*.dll", SearchOption.AllDirectories).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var path in dlls)
            {
                if (knownNames.Contains(Path.GetFileName(path)))
                    NormalizeCacheDllPermissions(path);
            }
        }
    }

    private static bool IsReadableCachedDll(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 64 * 1024) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.ReadByte() >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeCacheDllPermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try
        {
            // DLLs are data payloads for Wine/Proton; execute permission is neither required nor
            // desirable in the cache. Explicitly ignore ZIP-carried Unix modes and keep a normal
            // user-owned readable cache file.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (PlatformNotSupportedException) { }
    }

    private static void TryDeleteCacheFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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

    private static IReadOnlyList<string> PreferLikelyGamePaths(
        IEnumerable<string> paths, string gameRoot, string? binaryDirectory)
        => paths.Distinct(StringComparer.Ordinal)
            .OrderBy(p => PathAffinity(Path.GetDirectoryName(p) ?? gameRoot, binaryDirectory, gameRoot))
            .ThenBy(p => RelativeDepth(gameRoot, Path.GetDirectoryName(p) ?? gameRoot))
            .ToArray();

    private static int PathAffinity(string directory, string? binaryDirectory, string gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(binaryDirectory))
        {
            try
            {
                if (Path.GetFullPath(directory) == Path.GetFullPath(binaryDirectory)) return 0;
                var rel = Path.GetRelativePath(binaryDirectory, directory);
                if (!rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && rel != "..") return 1;
            }
            catch { }
        }
        var normalized = directory.Replace('\\', '/');
        if (normalized.Contains("/Binaries/Win64", StringComparison.OrdinalIgnoreCase)) return 2;
        if (normalized.Contains("/Engine/Binaries", StringComparison.OrdinalIgnoreCase)) return 4;
        return 3 + Math.Min(20, RelativeDepth(gameRoot, directory));
    }

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
