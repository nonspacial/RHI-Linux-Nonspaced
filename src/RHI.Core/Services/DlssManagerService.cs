using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Deployment;
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
        SweepCacheLeftovers();
    }

    public DlssManifestData? Manifest => _manifest;

    /// <summary>Why the last downloaded manifest could not be cached (the fresh copy was still used); null when it was.</summary>
    public string? ManifestCacheWriteError { get; private set; }

    /// <summary>Manifest download URLs and their redirect targets (GitHub release hosts only, HTTPS only).</summary>
    internal static readonly TrustedDownloadPolicy DownloadPolicy = TrustedDownloadPolicy.GitHubReleases with { MaxBytes = 1024L * 1024 * 1024 };

    /// <summary>Upper bound for one extracted DLL.</summary>
    private const long MaxDllBytes = 512L * 1024 * 1024;

    /// <summary>Crash leftovers in the cache older than this are removed at start-up.</summary>
    private static readonly TimeSpan CacheLeftoverAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Per-request deadline for the manifest. The shared HttpClient allows 5 minutes, which on a
    /// black-holed network (VPN kill-switch, captive portal) would stall every caller; the cached copy
    /// is used instead (Refs #43).
    /// </summary>
    public TimeSpan ManifestFetchTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public Task<DlssManifestData> FetchManifestAsync(CancellationToken cancellationToken = default)
        => FetchManifestAsync(conditional: true, cancellationToken);

    private async Task<DlssManifestData> FetchManifestAsync(bool conditional, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_cacheRoot, "dlss_manifest.json");
        var etagPath = cachePath + ".etag";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ManifestFetchTimeout);
        try
        {
            // Conditional GET (#35 residual): the cached copy's ETag, so an unchanged manifest costs a 304 and no body.
            var cachedEtag = conditional && File.Exists(cachePath) ? ReadManifestEtag(etagPath) : null;
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RHI-Linux", RhiProductInfo.Version));
            if (cachedEtag is not null) request.Headers.IfNoneMatch.Add(cachedEtag);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cachedEtag is not null)
            {
                if (await TryReadCachedManifestAsync(cachePath, cancellationToken).ConfigureAwait(false) is { } unchanged)
                {
                    _manifest = unchanged;
                    return unchanged;
                }
                // The cache no longer reads: forget its ETag and ask for the whole manifest again.
                FileCleanup.TryDelete(etagPath);
                return await FetchManifestAsync(conditional: false, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DlssManifestData>(json, JsonOptions())
                         ?? throw new InvalidDataException("DLSS manifest was empty or invalid.");
            ValidateManifest(parsed);
            _manifest = parsed;
            // Durable and atomic (#39): a crash never leaves a truncated fallback manifest, and a
            // cache that cannot be written does not discard the manifest just downloaded.
            // CancellationToken.None: the manifest is already parsed and in use; a cancellation here
            // must not turn into the outer fallback that returns the older cached copy instead.
            try
            {
                // The ETag never outlives the copy it describes: dropped first, written only after this copy is in place.
                FileCleanup.TryDelete(etagPath);
                await AtomicFile.WriteAllTextAsync(cachePath, json, CancellationToken.None).ConfigureAwait(false);
                ManifestCacheWriteError = null;
                if (response.Headers.ETag is { } etag)
                    await AtomicFile.WriteAllTextAsync(etagPath, etag.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileCleanup.TryDelete(etagPath);
                ManifestCacheWriteError = ex.Message;
                AppLog.Warn("dlss-manifest", $"The downloaded DLSS manifest is in use but could not be cached at {cachePath}; the next offline start uses the older copy", ex);
            }
            return parsed;
        }
        catch (Exception ex) when (File.Exists(cachePath))
        {
            if (!cancellationToken.IsCancellationRequested)
                AppLog.Warn("dlss", "DLSS manifest download failed; using the cached copy", ex);
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DlssManifestData>(json, JsonOptions())
                         ?? throw new InvalidDataException("Cached DLSS manifest is invalid.");
            ValidateManifest(parsed);
            _manifest = parsed;
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DLSS manifest did not answer and no cached copy exists (timeout {ManifestFetchTimeout.TotalSeconds:0} s).");
        }
    }

    private static EntityTagHeaderValue? ReadManifestEtag(string etagPath)
    {
        try
        {
            return File.Exists(etagPath) && EntityTagHeaderValue.TryParse(File.ReadAllText(etagPath).Trim(), out var etag) ? etag : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dlss-manifest", $"The DLSS manifest ETag could not be read ({etagPath}); asking for the whole manifest", ex);
            return null;
        }
    }

    private static async Task<DlssManifestData?> TryReadCachedManifestAsync(string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<DlssManifestData>(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false), JsonOptions());
            if (parsed is null) return null;
            ValidateManifest(parsed);
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppLog.Warn("dlss-manifest", $"The cached DLSS manifest no longer reads ({cachePath}); asking for the whole manifest", ex);
            return null;
        }
    }

    /// <summary>Folder name of the OptiScaler stack; nothing below it is ever a direct DLSS swap/restore target.</summary>
    public const string OptiScalerFolderName = "OptiScaler";

    public DlssDetectionResult Detect(
        string gameRoot,
        string? executablePath = null,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
        => Detect(gameRoot, executablePath, indexedFiles, protectedFiles: null, cancellationToken);

    /// <summary>
    /// The game's DLSS/Streamline targets with the manifest folder overrides applied (#155, upstream 2.8.6
    /// <c>dlssPathOverrides</c>/<c>streamlinePathOverrides</c>): when <see cref="DetectedGame.DlssPathOverride"/> names an
    /// existing folder inside the game root, only that folder (and the Streamline override folder, else the same folder)
    /// is read, without the depth-limited walk, as upstream's exact-path detection; <c>nvngx_dlssg.dll</c> is also looked
    /// for in the Streamline folder. An override that is missing or leaves the game root falls back to the normal walk.
    /// Protected files are dropped in both cases.
    /// </summary>
    public DlssDetectionResult Detect(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles,
        IEnumerable<string>? protectedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var dlssFolder = ResolveManifestFolder(game.InstallPath, game.DlssPathOverride);
        if (dlssFolder is null)
            return Detect(game.InstallPath, game.ExecutablePath, indexedFiles, protectedFiles, cancellationToken);
        var streamlineFolder = ResolveManifestFolder(game.InstallPath, game.StreamlinePathOverride) ?? dlssFolder;
        return ExcludeProtected(DetectInFolders(game.InstallPath, dlssFolder, streamlineFolder), protectedFiles);
    }

    /// <summary>
    /// A manifest sub-path (<c>Engine\\Plugins\\…\\Win64</c>) below <paramref name="root"/>, or null when it is blank,
    /// absolute, missing, or leaves the root (<c>..</c> or a symlink pointing out, checked with symlinks resolved).
    /// </summary>
    public static string? ResolveManifestFolder(string? root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || !Directory.Exists(root)) return null;
        var normalized = relative.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, normalized)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // malformed manifest path: the normal walk runs
        }
        return Directory.Exists(full) && PathIdentity.IsSameOrInside(full, root) ? full : null;
    }

    private static DlssDetectionResult DetectInFolders(string gameRoot, string dlssFolder, string streamlineFolder)
    {
        Dictionary<string, string> Files(string folder)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (DlssManagerService.IsUnderOptiScalerFolder(gameRoot, Path.Combine(folder, "x"))) return byName;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                    byName.TryAdd(Path.GetFileName(file), file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("dlss", $"DLSS manifest folder could not be read: {folder}", ex);
            }
            return byName;
        }
        IReadOnlyList<string> One(Dictionary<string, string> files, string name)
            => files.TryGetValue(name, out var path) ? [path] : [];

        var dlss = Files(dlssFolder);
        var streamline = string.Equals(streamlineFolder, dlssFolder, StringComparison.Ordinal) ? dlss : Files(streamlineFolder);
        var dlssg = One(dlss, "nvngx_dlssg.dll");
        if (dlssg.Count == 0) dlssg = One(streamline, "nvngx_dlssg.dll");
        return new DlssDetectionResult
        {
            DlssPaths = One(dlss, "nvngx_dlss.dll"),
            DlssdPaths = One(dlss, "nvngx_dlssd.dll"),
            DlssgPaths = dlssg,
            DlssnrPaths = One(dlss, "nvngx_dlssnr.dll"),
            StreamlineFolders = streamline.ContainsKey("sl.interposer.dll") || streamline.ContainsKey("sl.common.dll")
                ? [streamlineFolder]
                : []
        };
    }

    /// <summary>
    /// Finds the game's own DLSS/Streamline DLLs, i.e. the direct swap/restore targets.
    /// Files under an <c>OptiScaler/</c> folder are never targets, and <paramref name="protectedFiles"/>
    /// are dropped through <see cref="ExcludeProtected"/>: a swap or restore there would break the
    /// owning component's hash ownership and consume the <c>.original</c> it restores on removal.
    /// </summary>
    public DlssDetectionResult Detect(
        string gameRoot,
        string? executablePath,
        IReadOnlyList<string>? indexedFiles,
        IEnumerable<string>? protectedFiles,
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
            if (IsUnderOptiScalerFolder(gameRoot, file)) continue;
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

        return ExcludeProtected(new DlssDetectionResult
        {
            DlssPaths = PreferLikelyGamePaths(dlss, gameRoot, binaryDirectory),
            DlssdPaths = PreferLikelyGamePaths(dlssd, gameRoot, binaryDirectory),
            DlssgPaths = PreferLikelyGamePaths(dlssg, gameRoot, binaryDirectory),
            DlssnrPaths = PreferLikelyGamePaths(dlssnr, gameRoot, binaryDirectory),
            StreamlineFolders = streamline
                .OrderBy(x => PathAffinity(x, binaryDirectory, gameRoot))
                .ThenBy(x => RelativeDepth(gameRoot, x))
                .ToArray()
        }, protectedFiles);
    }

    /// <summary>
    /// Drops <paramref name="protectedFiles"/> (files another RHI component owns) from the swap/restore
    /// targets, and every Streamline folder holding a protected Streamline DLL (folder swaps and
    /// restores act on all known <c>sl.*</c> names in it). Also used at action time on a cached
    /// detection, so ownership gained after the probe is still respected.
    /// </summary>
    public static DlssDetectionResult ExcludeProtected(DlssDetectionResult detection, IEnumerable<string>? protectedFiles)
    {
        var guarded = (protectedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.Ordinal);
        if (guarded.Count == 0) return detection;
        var knownStreamline = new HashSet<string>(KnownStreamlineDlls, StringComparer.OrdinalIgnoreCase);
        var guardedFolders = guarded
            .Where(path => knownStreamline.Contains(Path.GetFileName(path)))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> Keep(IReadOnlyList<string> paths)
            => paths.Where(path => !guarded.Contains(Path.GetFullPath(path))).ToArray();
        return new DlssDetectionResult
        {
            DlssPaths = Keep(detection.DlssPaths),
            DlssdPaths = Keep(detection.DlssdPaths),
            DlssgPaths = Keep(detection.DlssgPaths),
            DlssnrPaths = Keep(detection.DlssnrPaths),
            StreamlineFolders = detection.StreamlineFolders
                .Where(folder => !guardedFolders.Contains(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)))
                .ToArray()
        };
    }

    /// <summary>
    /// True when <paramref name="path"/> lies below a directory named <c>OptiScaler</c> inside
    /// <paramref name="root"/> (segments relative to the root, so a library path that happens to
    /// contain that name does not count).
    /// </summary>
    public static bool IsUnderOptiScalerFolder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var directory = Path.GetDirectoryName(relative);
        return !string.IsNullOrEmpty(directory)
               && directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(segment => segment.Equals(OptiScalerFolderName, StringComparison.OrdinalIgnoreCase));
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
        targetDlls = targetDlls.ToArray();
        // Refused before the download (Refs #90): an Xbox title the installer app is working on, or a protected package file.
        if (XboxWriteGate.Refusal(targetDlls) is { } xbox) return new(false, [], xbox); // w3/scan hook
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
        targetFolders = targetFolders.ToArray();
        if (XboxWriteGate.Refusal(targetFolders) is { } xbox) return new(false, [], xbox); // w3/scan hook (Refs #90)
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
            // Reuse only a DLL stamped at download time for this manifest entry (#142) and, when the
            // manifest publishes a digest, for that digest (#39); anything else is downloaded again.
            if (IsReadableCachedDll(cachedDll) && CacheStamp.IsValid(cachedDll, entry.Url, entry.Version, entry.Sha256)) return cachedDll;
            FileCleanup.TryDelete(cachedDll);
        }

        Directory.CreateDirectory(cacheDir);
        CacheStamp.Invalidate(cachedDll);
        using (var download = await DownloadArchiveAsync(entry, cacheDir, cancellationToken).ConfigureAwait(false))
        using (var zip = ZipFile.OpenRead(download.Path))
        {
            var match = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), expectedDll, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Downloaded archive does not contain {expectedDll}.");
            await ExtractVerifiedAsync(match, cachedDll, cancellationToken).ConfigureAwait(false);
        }
        if (!IsReadableCachedDll(cachedDll))
            throw new IOException($"The cached DLL is not readable after extraction: {cachedDll}");
        CacheStamp.Write(cachedDll, entry.Url, entry.Version, entry.Sha256);
        return cachedDll;
    }

    /// <summary>
    /// Downloads a manifest archive through <see cref="TrustedDownload"/> (HTTPS, GitHub release hosts
    /// including the redirect target, size cap, the manifest's sha256 when it publishes one) to a
    /// uniquely named temporary file that is deleted when the result is disposed, whatever happens.
    /// </summary>
    private Task<TempDownload> DownloadArchiveAsync(DlssManifestEntry entry, string cacheDir, CancellationToken cancellationToken)
        => TrustedDownload.DownloadToTempAsync(_http, entry.Url, Path.Combine(cacheDir, "download.zip"), DownloadPolicy, entry.Sha256,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Extracts one archive member to <paramref name="destination"/> through a verified, fsynced
    /// temporary file and an atomic rename (never <c>ExtractToFile</c>, which also applies the
    /// archive's Unix mode bits); the payload must pass the readable-DLL check before it is placed.
    /// </summary>
    private static async Task ExtractVerifiedAsync(ZipArchiveEntry member, string destination, CancellationToken cancellationToken)
    {
        StagedFile staged;
        await using (var content = member.Open())
            staged = await AtomicFile.StageStreamAsync(content, destination, null, null, MaxDllBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            NormalizeCacheDllPermissions(staged.Path);
            if (!IsReadableCachedDll(staged.Path))
                throw new InvalidDataException($"Extracted file is unreadable or unexpectedly small: {member.Name}");
            AtomicFile.MoveIntoPlace(staged.Path, destination);
        }
        finally
        {
            FileCleanup.TryDelete(staged.Path);
        }
        NormalizeCacheDllPermissions(destination);
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
            // Every Streamline DLL present here is deployed, so all of them must carry a stamp from
            // this manifest entry's download (#142, and its published digest, #39); otherwise the
            // whole set is downloaded again.
            if (existing.All(path => IsReadableCachedDll(path) && CacheStamp.IsValid(path, entry.Url, entry.Version, entry.Sha256))) return;
        }

        ValidateDownloadUrl(entry.Url);
        Directory.CreateDirectory(cacheDir);
        // Clear the set before extracting: a DLL the archive does not contain must not survive
        // next to the fresh ones and be deployed with them.
        foreach (var dll in KnownStreamlineDlls)
        {
            var path = Path.Combine(cacheDir, dll);
            CacheStamp.Invalidate(path);
            FileCleanup.TryDelete(path);
        }
        var extractedFiles = new List<string>();
        using (var download = await DownloadArchiveAsync(entry, cacheDir, ct).ConfigureAwait(false))
        using (var zip = ZipFile.OpenRead(download.Path))
        {
            foreach (var dll in KnownStreamlineDlls)
            {
                var match = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), dll, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                var extracted = Path.Combine(cacheDir, dll);
                await ExtractVerifiedAsync(match, extracted, ct).ConfigureAwait(false);
                extractedFiles.Add(extracted);
            }
        }
        if (extractedFiles.Count == 0)
            throw new InvalidDataException("Streamline archive contained no readable recognized DLLs.");
        foreach (var path in extractedFiles) CacheStamp.Write(path, entry.Url, entry.Version, entry.Sha256);
    }

    /// <summary>
    /// Removes download/extraction temporaries a crash left in the NVIDIA cache namespaces (current
    /// <c>*.rhi-tmp-*</c> names and the older <c>download-*.zip.part</c> / <c>*.dll.tmp-*</c> names).
    /// Cache files only: nothing here is user data. Recent files are kept in case another RHI process
    /// is downloading right now.
    /// </summary>
    private void SweepCacheLeftovers()
    {
        foreach (var component in new[] { "DLSS", "DLSS-D", "DLSS-G", "DLSS-NR", "Streamline" })
        {
            var componentRoot = Path.Combine(_cacheRoot, component);
            if (!Directory.Exists(componentRoot)) continue;
            IEnumerable<string> files;
            try
            {
                files = SafeFileEnumeration.Files(componentRoot).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable cache folder: nothing can be reused from it either
            }
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                var leftover = name.Contains(AtomicFile.TempMarker, StringComparison.Ordinal)
                               || (name.StartsWith("download-", StringComparison.Ordinal) && name.EndsWith(".zip.part", StringComparison.Ordinal))
                               || name.Contains(".dll.tmp-", StringComparison.OrdinalIgnoreCase);
                if (leftover && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CacheLeftoverAge)
                    FileCleanup.TryDelete(path);
            }
        }
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
                dlls = SafeFileEnumeration.Files(componentRoot, "*.dll").ToArray();
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

    private static void ValidateManifest(DlssManifestData manifest)
    {
        var all = manifest.Dlss.Concat(manifest.Dlssd).Concat(manifest.Dlssg).Concat(manifest.Dlssnr).Concat(manifest.Streamline).ToArray();
        if (all.Length == 0) throw new InvalidDataException("DLSS manifest has no entries.");
        foreach (var entry in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Version)) throw new InvalidDataException("Manifest entry has no version.");
            ValidateDownloadUrl(entry.Url);
            if (entry.Sha256 is { } sha && (sha.Length != 64 || !sha.All(Uri.IsHexDigit)))
                throw new InvalidDataException($"Manifest entry has a malformed sha256: {entry.Version}");
        }
    }

    private static void ValidateDownloadUrl(string url) => TrustedDownload.Validate(url, DownloadPolicy);

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
