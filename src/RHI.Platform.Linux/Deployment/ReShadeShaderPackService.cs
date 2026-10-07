using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

public sealed record ReShadeShaderPackInfo(
    string Id,
    string DisplayName,
    string Description,
    string Category = "Extra",
    IReadOnlyList<string>? Requires = null,
    bool IsCustom = false);

public sealed record ReShadeShaderPackFileInfo(
    string PackId,
    string Selector,
    string DisplayPath,
    bool IsTexture,
    bool IsExcluded);

internal sealed record ReShadeShaderSyncResult(
    string RootDirectory,
    IReadOnlyList<string> EffectSearchPaths,
    IReadOnlyList<string> TextureSearchPaths,
    IReadOnlyList<string> PackIds);

/// <summary>
/// Linux-native shader pack manager aligned with upstream RHI behavior.
/// Managed files are isolated under reshade-shaders/RHI and guarded by SHA-256.
/// The built-in catalog mirrors upstream's DefaultPacks and can be remotely amended
/// by manifest.shaderPacks. Local custom shaders live under XDG_DATA_HOME.
/// </summary>
public sealed class ReShadeShaderPackService
{
    private sealed class Manifest
    {
        public int Schema { get; set; } = 2;
        public List<string> PackIds { get; set; } = [];
        public List<ManifestFile> Files { get; set; } = [];
    }

    private sealed class ManifestFile
    {
        public string RelativePath { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private sealed class PackSettings
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, List<string>> ExcludedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly TimeSpan ExternalExtractionTimeout = TimeSpan.FromMinutes(2);

    private static readonly ReShadeShaderPackDefinition CoreHeadersPack = new(
        "ReShadeCoreHeaders",
        "ReShade core headers",
        "Hidden dependency for third-party shader packs.",
        ReShadeShaderPackSourceKind.DirectUrl,
        "https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip");

    private static readonly string[] CoreHeaderNames = ["ReShade.fxh", "ReShadeUI.fxh"];

    // Mirrors upstream ShaderPackService.ExcludedShaderFiles.
    private static readonly HashSet<string> GloballyExcludedShaderFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "BX_XIV_ChromakeyPlus.fx",
        "GrainSpread.fx",
        "NTSCCustom.fx",
        "NTSC_XOT.fx",
    };

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService? _manifest;
    private readonly string _cacheDir;
    private readonly string _settingsPath;
    private readonly JsonFileStore<PackSettings> _settingsFile;
    private readonly string _customRoot;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private IReadOnlyList<ReShadeShaderPackDefinition> _packs;

    public ReShadeShaderPackService(
        HttpClient http,
        string? cacheDir = null,
        UpstreamRhiManifestService? manifest = null,
        string? customRoot = null)
    {
        _http = http;
        _manifest = manifest;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "reshade-shaders");
        _settingsPath = Path.Combine(_cacheDir, ".pack-settings.json");
        _settingsFile = new JsonFileStore<PackSettings>(_settingsPath);
        _customRoot = customRoot ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "Custom", "Shaders");
        _packs = BuildDefaultCatalog(_customRoot);
        EnsureCustomDirectories();
    }

    public string CustomRoot => _customRoot;

    public IReadOnlyList<ReShadeShaderPackInfo> AvailablePacks =>
        _packs
            .OrderBy(p => p.Category)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ReShadeShaderPackInfo(
                p.Id,
                p.DisplayName,
                p.Description,
                p.Category.ToString(),
                p.Requires ?? Array.Empty<string>(),
                p.IsCustom))
            .ToArray();

    public string[] GetRequiredPacks(string packId) =>
        _packs.FirstOrDefault(p => p.Id.Equals(packId, StringComparison.OrdinalIgnoreCase))?.Requires?.ToArray()
        ?? Array.Empty<string>();

    public async Task<IReadOnlyList<ReShadeShaderPackInfo>> RefreshCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var merged = BuildDefaultCatalog(_customRoot).ToList();
            if (_manifest is not null)
            {
                try
                {
                    var overrides = await _manifest.FetchShaderPacksAsync(cancellationToken).ConfigureAwait(false);
                    ApplyManifestOverrides(merged, overrides);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // Keep the accepted built-in catalog if the live manifest is unavailable.
                }
            }
            _packs = merged;
            return AvailablePacks;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>Returns the persisted filename exclusions for one shader pack.</summary>
    public IReadOnlySet<string> GetExcludedFiles(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _settingsLock.Wait();
        try
        {
            var settings = LoadPackSettings();
            if (!settings.ExcludedFiles.TryGetValue(packId, out var files))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public void SetExcludedFiles(string packId, IEnumerable<string> excluded)
    {
        if (string.IsNullOrWhiteSpace(packId))
            throw new ArgumentException("Shader pack id is required.", nameof(packId));

        var normalized = excluded
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeFileSelector)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settingsLock.Wait();
        try
        {
            var settings = LoadPackSettings();
            if (normalized.Count == 0) settings.ExcludedFiles.Remove(packId);
            else settings.ExcludedFiles[packId] = normalized;
            SavePackSettings(settings);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<IReadOnlyList<ReShadeShaderPackFileInfo>> ListPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packId))
            throw new ArgumentException("Shader pack id is required.", nameof(packId));

        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);
        var pack = _packs.FirstOrDefault(p => p.Id.Equals(packId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown ReShade shader pack id: {packId}");
        var staged = pack.IsCustom
            ? EnsureCustomPack()
            : await EnsurePackAsync(pack, cancellationToken).ConfigureAwait(false);
        var excluded = new HashSet<string>(GetExcludedFiles(pack.Id), StringComparer.OrdinalIgnoreCase);
        if (!pack.IsCustom) excluded.UnionWith(GloballyExcludedShaderFiles);

        var result = new List<ReShadeShaderPackFileInfo>();
        AddPackFileInfos(result, pack.Id, staged, "Shaders", isTexture: false, excluded);
        AddPackFileInfos(result, pack.Id, staged, "Textures", isTexture: true, excluded);
        return result
            .OrderBy(x => x.IsTexture)
            .ThenBy(x => x.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<ReShadeShaderSyncResult> SyncAsync(
        string gameDirectory,
        IReadOnlyList<string> selectedPackIds,
        CancellationToken cancellationToken = default)
    {
        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);

        var expanded = ExpandDependencies(selectedPackIds, _packs);
        var unknown = expanded
            .Where(id => !_packs.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"Unknown ReShade shader pack id(s): {string.Join(", ", unknown)}");

        var normalized = expanded
            .Select(id => _packs.First(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var needsCoreHeaders = normalized.Any(id =>
            id.Equals("ClshortfuseShaders", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("MaxG2DSimpleHDR", StringComparison.OrdinalIgnoreCase));

        var root = Path.Combine(gameDirectory, "reshade-shaders", "RHI");
        var manifestPath = Path.Combine(root, ".rhi-manifest.json");
        var previous = LoadManifest(manifestPath);
        var exclusionsById = normalized.ToDictionary(
            id => id,
            id =>
            {
                var set = new HashSet<string>(GetExcludedFiles(id), StringComparer.OrdinalIgnoreCase);
                set.UnionWith(GloballyExcludedShaderFiles);
                return set;
            },
            StringComparer.OrdinalIgnoreCase);

        // Stage everything before touching the game directory.
        var stagedById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = _packs.First(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            stagedById[id] = pack.IsCustom
                ? EnsureCustomPack()
                : await EnsurePackAsync(pack, cancellationToken).ConfigureAwait(false);
        }

        string? coreHeadersStaged = null;
        if (needsCoreHeaders)
        {
            coreHeadersStaged = await EnsurePackAsync(CoreHeadersPack, cancellationToken).ConfigureAwait(false);
            EnsureCoreHeadersPresent(coreHeadersStaged);
        }

        var previousByPath = previous.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var id in normalized)
        {
            var staged = stagedById[id];
            var targetPackRoot = Path.Combine(root, SafePackDirectoryName(id));
            ValidateDestinationTree(
                Path.Combine(staged, "Shaders"),
                Path.Combine(targetPackRoot, "Shaders"),
                root,
                previousByPath,
                exclusionsById[id],
                "Shaders");
            ValidateDestinationTree(
                Path.Combine(staged, "Textures"),
                Path.Combine(targetPackRoot, "Textures"),
                root,
                previousByPath,
                exclusionsById[id],
                "Textures");
        }

        if (needsCoreHeaders)
        {
            var targetCoreRoot = Path.Combine(root, "_Core", "Shaders");
            foreach (var header in CoreHeaderNames)
                ValidateDestinationFile(
                    Path.Combine(coreHeadersStaged!, "Shaders", header),
                    Path.Combine(targetCoreRoot, header),
                    root,
                    previousByPath);
        }

        RemoveManifestFiles(root, previous);
        if (normalized.Length == 0)
        {
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
            TryDeleteEmptyTree(root, keepRootIfFilesRemain: true);
            return new(root, [], [], []);
        }

        Directory.CreateDirectory(root);
        var outputManifest = new Manifest { PackIds = normalized.ToList() };
        var effectPaths = new List<string>();
        var texturePaths = new List<string>();

        foreach (var id in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = stagedById[id];
            var safeId = SafePackDirectoryName(id);
            var targetPackRoot = Path.Combine(root, safeId);
            var stagedShaders = Path.Combine(staged, "Shaders");
            var stagedTextures = Path.Combine(staged, "Textures");

            if (Directory.Exists(stagedShaders))
            {
                var targetShaders = Path.Combine(targetPackRoot, "Shaders");
                CopyTree(stagedShaders, targetShaders, root, outputManifest, cancellationToken, exclusionsById[id], "Shaders");
                if (Directory.Exists(targetShaders) && SafeFileEnumeration.Files(targetShaders).Any())
                    effectPaths.Add($@".\reshade-shaders\RHI\{safeId}\Shaders\**");
            }

            if (Directory.Exists(stagedTextures))
            {
                var targetTextures = Path.Combine(targetPackRoot, "Textures");
                CopyTree(stagedTextures, targetTextures, root, outputManifest, cancellationToken, exclusionsById[id], "Textures");
                if (Directory.Exists(targetTextures) && SafeFileEnumeration.Files(targetTextures).Any())
                    texturePaths.Add($@".\reshade-shaders\RHI\{safeId}\Textures\**");
            }
        }

        if (needsCoreHeaders)
        {
            var targetCoreRoot = Path.Combine(root, "_Core", "Shaders");
            foreach (var header in CoreHeaderNames)
                CopyManagedFile(
                    Path.Combine(coreHeadersStaged!, "Shaders", header),
                    Path.Combine(targetCoreRoot, header),
                    root,
                    outputManifest);
            effectPaths.Add(@".\reshade-shaders\RHI\_Core\Shaders\**");
        }

        new JsonFileStore<Manifest>(manifestPath).Save(outputManifest);
        return new(root, effectPaths, texturePaths, normalized);
    }

    internal void Cleanup(string gameDirectory)
    {
        var root = Path.Combine(gameDirectory, "reshade-shaders", "RHI");
        var manifestPath = Path.Combine(root, ".rhi-manifest.json");
        if (!File.Exists(manifestPath)) return;
        var manifest = LoadManifest(manifestPath);
        RemoveManifestFiles(root, manifest);
        try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
        TryDeleteEmptyTree(root, keepRootIfFilesRemain: true);
    }

    internal static IReadOnlyList<string> ExpandDependencies(
        IEnumerable<string> selected,
        IReadOnlyList<ReShadeShaderPackDefinition> catalog)
    {
        var byId = catalog.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id, bool explicitlySelected)
        {
            // Never pull the user's local custom shaders in as a dependency (upstream 2.7.9):
            // they deploy only when ticked explicitly, whatever a remote manifest declares.
            if (!explicitlySelected && byId.TryGetValue(id, out var candidate) && candidate.IsCustom) return;
            if (!visited.Add(id)) return;
            if (byId.TryGetValue(id, out var pack))
            {
                foreach (var dependency in pack.Requires ?? Array.Empty<string>())
                    Visit(dependency, explicitlySelected: false);
                result.Add(pack.Id);
            }
            else
            {
                result.Add(id);
            }
        }

        foreach (var id in selected.Where(x => !string.IsNullOrWhiteSpace(x)))
            Visit(id.Trim(), explicitlySelected: true);

        return result;
    }

    internal static IReadOnlyList<ReShadeShaderPackDefinition> MergeManifestOverridesForTest(
        IReadOnlyDictionary<string, UpstreamShaderPackDefinition> overrides)
    {
        var merged = BuildDefaultCatalog().ToList();
        ApplyManifestOverrides(merged, overrides);
        return merged;
    }

    private static List<ReShadeShaderPackDefinition> BuildDefaultCatalog(string? customRoot = null)
    {
        var result = ReShadeShaderPackCatalog.DefaultPacks.ToList();
        customRoot ??= Path.Combine(LinuxPaths.DataHome, "rhi-linux", "Custom", "Shaders");
        result.Add(ReShadeShaderPackCatalog.CustomPack(customRoot));
        return result;
    }

    private static void ApplyManifestOverrides(
        List<ReShadeShaderPackDefinition> merged,
        IReadOnlyDictionary<string, UpstreamShaderPackDefinition> overrides)
    {
        foreach (var (id, entry) in overrides)
        {
            if (entry.Disabled)
            {
                merged.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var existingIndex = merged.FindIndex(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = merged[existingIndex];
                merged[existingIndex] = existing with
                {
                    DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? existing.DisplayName : entry.DisplayName!,
                    Description = string.IsNullOrWhiteSpace(entry.Description) ? existing.Description : entry.Description!,
                    SourceKind = ParseSourceKind(entry.Kind) ?? existing.SourceKind,
                    Url = string.IsNullOrWhiteSpace(entry.Url) ? existing.Url : entry.Url!,
                    IsMinimum = entry.IsMinimum ?? existing.IsMinimum,
                    AssetExt = entry.AssetExt ?? existing.AssetExt,
                    Category = ParseCategory(entry.Category) ?? existing.Category,
                    Requires = entry.Requires.Count > 0 ? entry.Requires : existing.Requires
                };
                continue;
            }

            var kind = ParseSourceKind(entry.Kind);
            if (kind is null || string.IsNullOrWhiteSpace(entry.Url))
                continue;

            merged.Add(new ReShadeShaderPackDefinition(
                id,
                string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName!,
                entry.Description ?? "",
                kind.Value,
                entry.Url!,
                entry.IsMinimum ?? false,
                entry.AssetExt,
                ParseCategory(entry.Category) ?? ReShadeShaderPackCategory.Extra,
                entry.Requires));
        }
    }

    private static ReShadeShaderPackSourceKind? ParseSourceKind(string? value) => value switch
    {
        "GhRelease" => ReShadeShaderPackSourceKind.GhRelease,
        "DirectUrl" => ReShadeShaderPackSourceKind.DirectUrl,
        _ => null
    };

    private static ReShadeShaderPackCategory? ParseCategory(string? value) => value switch
    {
        "Essential" => ReShadeShaderPackCategory.Essential,
        "Recommended" => ReShadeShaderPackCategory.Recommended,
        "Extra" => ReShadeShaderPackCategory.Extra,
        "Custom" => ReShadeShaderPackCategory.Custom,
        _ => null
    };

    internal async Task<string> EnsurePackAsync(
        ReShadeShaderPackDefinition pack,
        CancellationToken cancellationToken)
    {
        var packDir = Path.Combine(_cacheDir, SafePackDirectoryName(pack.Id));
        var extracted = Path.Combine(packDir, "extracted");
        var marker = Path.Combine(packDir, ".complete");
        var sourceMarker = Path.Combine(packDir, ".source");

        var (downloadUrl, sourceIdentity) = pack.SourceKind == ReShadeShaderPackSourceKind.GhRelease
            ? await ResolveGitHubReleaseAssetAsync(pack.Url, pack.AssetExt ?? ".zip", cancellationToken).ConfigureAwait(false)
            : (pack.Url, pack.Url);

        if (File.Exists(marker) && File.Exists(sourceMarker) && Directory.Exists(extracted) &&
            SafeFileEnumeration.Files(extracted).Any() &&
            string.Equals(File.ReadAllText(sourceMarker), sourceIdentity, StringComparison.Ordinal) &&
            // The markers name the source; the stamp proves the tree is still what this service
            // extracted from it (#142).
            CacheStamp.IsValid(extracted, downloadUrl, sourceIdentity))
            return extracted;
        CacheStamp.Invalidate(extracted);

        Directory.CreateDirectory(packDir);
        var extension = Path.GetExtension(new Uri(downloadUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(pack.AssetExt))
            extension = pack.AssetExt;
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) &&
            downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            extension = ".tar.gz";

        var temp = Path.Combine(packDir, $"download-{Guid.NewGuid():N}" + (string.IsNullOrWhiteSpace(extension) ? ".tmp" : extension) + ".tmp");
        var raw = Path.Combine(packDir, "raw-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            if (new FileInfo(temp).Length < 512)
                throw new InvalidOperationException($"Shader pack {pack.DisplayName} download is unexpectedly small.");

            Directory.CreateDirectory(raw);
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                (pack.AssetExt?.Equals(".zip", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                ExtractZipSafely(temp, raw);
            }
            else if (downloadUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                     (pack.AssetExt?.Equals(".7z", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                await Extract7zAsync(temp, raw, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException($"Shader archive format is not supported on Linux yet: {downloadUrl}");
            }

            var normalized = Path.Combine(packDir, "normalized-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(normalized);
            var extractedCount = NormalizeExtractedShaderTree(raw, normalized);
            if (extractedCount == 0)
                throw new InvalidDataException($"Shader pack {pack.DisplayName} contained no usable Shaders/Textures files.");

            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            Directory.Move(normalized, extracted);
            File.WriteAllText(sourceMarker, sourceIdentity);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            CacheStamp.Write(extracted, downloadUrl, sourceIdentity);
            return extracted;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { if (Directory.Exists(raw)) Directory.Delete(raw, true); } catch { }
        }
    }

    private string EnsureCustomPack()
    {
        EnsureCustomDirectories();
        foreach (var link in LinkedFoldersNotDeployed(_customRoot))
            Trace.TraceWarning($"[RHI shaders] {link} is a folder symlink in the custom shader folder; its files are not deployed (links are never followed into a game). Copy the files in instead.");
        return _customRoot;
    }

    /// <summary>
    /// Folder symlinks inside the custom shader folder. Deployment never walks a directory link (it could
    /// lead anywhere, / included), so the shaders behind one are not copied; the user is told which.
    /// </summary>
    internal static IReadOnlyList<string> LinkedFoldersNotDeployed(string customRoot)
    {
        if (!Directory.Exists(customRoot)) return [];
        try
        {
            return SafeFileEnumeration.Entries(customRoot, ignoreInaccessible: true)
                .Where(entry => entry.LinkTarget is not null && Directory.Exists(entry.FullName))
                .Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[RHI shaders] custom shader folder {customRoot} not fully readable ({ex.Message})");
            return [];
        }
    }

    private void EnsureCustomDirectories()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(_customRoot, "Shaders"));
            Directory.CreateDirectory(Path.Combine(_customRoot, "Textures"));
        }
        catch { }
    }

    private async Task<(string DownloadUrl, string SourceIdentity)> ResolveGitHubReleaseAssetAsync(
        string apiUrl,
        string assetExtension,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "unknown" : "unknown";
        if (!root.TryGetProperty("assets", out var assets))
            throw new InvalidDataException($"GitHub release has no assets: {apiUrl}");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            if (!name.EndsWith(assetExtension, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(url))
                return (url!, $"{tag}|{name}|{url}");
        }

        throw new InvalidDataException($"GitHub release has no {assetExtension} asset: {apiUrl}");
    }

    private static void ExtractZipSafely(string archivePath, string outputRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var allowed = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            // Reject traversal syntax itself, even when normalization would happen
            // to land back inside outputRoot (for example: repo/Shaders/../../evil.fx).
            // This keeps archive semantics conservative and makes the extraction
            // contract independent from the surrounding directory depth.
            var segments = entry.FullName
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == ".."))
                throw new InvalidDataException($"Shader archive entry contains path traversal: {entry.FullName}");

            var relative = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(outputRoot, relative));
            if (!destination.StartsWith(allowed, StringComparison.Ordinal))
                throw new InvalidDataException($"Shader archive entry escapes staging directory: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static async Task Extract7zAsync(string archivePath, string outputRoot, CancellationToken cancellationToken)
    {
        var tool = FindExecutable(["7zz", "7z", "7za"])
            ?? throw new InvalidOperationException("A 7z-compatible extractor is required for this shader pack (install 7zip/7zz/7za).");

        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add($"-o{outputRoot}");
        psi.ArgumentList.Add(archivePath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExternalExtractionTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Shader pack 7z extraction exceeded 2 minutes and was terminated to keep the RHI UI responsive.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool)} extraction failed ({process.ExitCode}): {stderr}\n{stdout}");
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    internal static string? FindExecutable(IEnumerable<string> names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var name in names)
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static int NormalizeExtractedShaderTree(string rawRoot, string outputRoot)
    {
        var count = 0;
        foreach (var file in SafeFileEnumeration.Files(rawRoot))
        {
            var segments = Path.GetRelativePath(rawRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var shaderIndex = Array.FindIndex(segments, s => s.Equals("Shaders", StringComparison.OrdinalIgnoreCase));
            var textureIndex = Array.FindIndex(segments, s => s.Equals("Textures", StringComparison.OrdinalIgnoreCase));

            string? category = null;
            string relative;
            if (shaderIndex >= 0)
            {
                category = "Shaders";
                relative = shaderIndex + 1 < segments.Length
                    ? Path.Combine(segments[(shaderIndex + 1)..])
                    : Path.GetFileName(file);
            }
            else if (textureIndex >= 0)
            {
                category = "Textures";
                relative = textureIndex + 1 < segments.Length
                    ? Path.Combine(segments[(textureIndex + 1)..])
                    : Path.GetFileName(file);
            }
            else
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".fx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fxh", StringComparison.OrdinalIgnoreCase))
                    category = "Shaders";
                else if (new[] { ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".tga" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    category = "Textures";
                else
                    continue;
                relative = Path.GetFileName(file);
            }

            if (category == "Shaders" && GloballyExcludedShaderFiles.Contains(Path.GetFileName(file)))
                continue;

            var destination = Path.Combine(outputRoot, category, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            count++;
        }
        return count;
    }

    private static string SafePackDirectoryName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = id.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        return new string(chars);
    }

    private static void EnsureCoreHeadersPresent(string staged)
    {
        foreach (var header in CoreHeaderNames)
        {
            var path = Path.Combine(staged, "Shaders", header);
            if (!File.Exists(path) || new FileInfo(path).Length < 256)
                throw new InvalidDataException($"ReShade core dependency is missing or invalid: {header}");
        }
    }

    private static void ValidateDestinationFile(
        string source,
        string destination,
        string managedRoot,
        IReadOnlyDictionary<string, ManifestFile> previousByPath)
    {
        if (!File.Exists(source))
            throw new InvalidDataException($"Required staged shader dependency is missing: {source}");
        if (!File.Exists(destination)) return;
        var manifestRelative = Path.GetRelativePath(managedRoot, destination);
        if (!previousByPath.TryGetValue(manifestRelative, out var old) ||
            !string.Equals(Sha256(destination), old.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Protected shader file in RHI path was modified or is externally owned: {destination}");
    }

    private static void CopyManagedFile(
        string source,
        string destination,
        string managedRoot,
        Manifest manifest)
    {
        var rootWithSep = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        destination = Path.GetFullPath(destination);
        if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new InvalidOperationException($"Shader path escapes managed root: {destination}");
        if (File.Exists(destination))
            throw new IOException($"Protected shader file already exists in RHI-managed path: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        manifest.Files.Add(new ManifestFile
        {
            RelativePath = Path.GetRelativePath(managedRoot, destination),
            Sha256 = Sha256(destination)
        });
    }

    private static void ValidateDestinationTree(
        string sourceRoot,
        string destinationRoot,
        string managedRoot,
        IReadOnlyDictionary<string, ManifestFile> previousByPath,
        IReadOnlySet<string>? excludedFileNames,
        string category)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var source in SafeFileEnumeration.Files(sourceRoot))
        {
            if (IsExcluded(excludedFileNames, category, sourceRoot, source)) continue;
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!File.Exists(destination)) continue;
            var manifestRelative = Path.GetRelativePath(managedRoot, destination);
            if (!previousByPath.TryGetValue(manifestRelative, out var old) ||
                !string.Equals(Sha256(destination), old.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Protected shader file in RHI path was modified or is externally owned: {destination}");
        }
    }

    private static void CopyTree(
        string sourceRoot,
        string destinationRoot,
        string managedRoot,
        Manifest manifest,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedFileNames,
        string category)
    {
        foreach (var source in SafeFileEnumeration.Files(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(excludedFileNames, category, sourceRoot, source)) continue;
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            var rootWithSep = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new InvalidOperationException($"Shader path escapes managed root: {relative}");
            if (File.Exists(destination))
                throw new IOException($"Protected shader file already exists in RHI-managed path: {destination}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            manifest.Files.Add(new ManifestFile
            {
                RelativePath = Path.GetRelativePath(managedRoot, destination),
                Sha256 = Sha256(destination)
            });
        }
    }

    private static void AddPackFileInfos(
        ICollection<ReShadeShaderPackFileInfo> output,
        string packId,
        string stagedRoot,
        string category,
        bool isTexture,
        IReadOnlySet<string> excluded)
    {
        var root = Path.Combine(stagedRoot, category);
        if (!Directory.Exists(root)) return;
        foreach (var file in SafeFileEnumeration.Files(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var selector = $"{category}/{relative}";
            output.Add(new ReShadeShaderPackFileInfo(
                packId,
                selector,
                selector,
                isTexture,
                IsExcluded(excluded, category, root, file)));
        }
    }

    private static bool IsExcluded(
        IReadOnlySet<string>? exclusions,
        string category,
        string sourceRoot,
        string sourcePath)
    {
        if (exclusions is null || exclusions.Count == 0) return false;
        var relative = Path.GetRelativePath(sourceRoot, sourcePath).Replace('\\', '/');
        var selector = $"{category}/{relative}";
        return exclusions.Contains(selector) || exclusions.Contains(Path.GetFileName(sourcePath));
    }

    private static string NormalizeFileSelector(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)) return "";
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException($"Shader selector contains an unsafe path segment: {value}", nameof(value));

        if (segments.Length >= 2 &&
            (segments[0].Equals("Shaders", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Textures", StringComparison.OrdinalIgnoreCase)))
        {
            var category = segments[0].Equals("Textures", StringComparison.OrdinalIgnoreCase) ? "Textures" : "Shaders";
            return category + "/" + string.Join('/', segments.Skip(1));
        }

        // Compatibility with schema-1 settings: historic exclusions were stored as basenames.
        return segments[^1];
    }

    // The exclusions are the user's choices; an unreadable file is copied aside and kept.
    private PackSettings LoadPackSettings()
    {
        var settings = _settingsFile.Load().Value ?? new PackSettings();
        settings.ExcludedFiles = JsonStoreMaps.CaseInsensitive(settings.ExcludedFiles);
        return settings;
    }

    private void SavePackSettings(PackSettings settings) => _settingsFile.Save(settings);

    /// <summary>
    /// Loads a game's ownership manifest. Missing means nothing is owned yet; an unreadable one is
    /// copied aside and throws, so no sync or cleanup can treat owned files as foreign or rewrite
    /// the manifest without them.
    /// </summary>
    private static Manifest LoadManifest(string path)
    {
        var file = new JsonFileStore<Manifest>(path);
        var load = file.Load();
        if (load.IsUnreadable)
            throw new JsonStoreUnavailableException(Path.GetFullPath(path), load.QuarantinePath);
        return load.Value ?? new Manifest();
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> when the game's shader-pack manifest is unreadable.</summary>
    internal static void EnsureManifestReadable(string gameDirectory)
        => LoadManifest(Path.Combine(gameDirectory, "reshade-shaders", "RHI", ".rhi-manifest.json"));

    private static void RemoveManifestFiles(string root, Manifest manifest)
    {
        foreach (var file in manifest.Files.OrderByDescending(f => f.RelativePath.Length))
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                var rootWithSep = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(rootWithSep, StringComparison.Ordinal) || !File.Exists(path)) continue;
                if (string.Equals(Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch { }
        }

        if (Directory.Exists(root))
        {
            foreach (var dir in SafeFileEnumeration.Directories(root).OrderByDescending(x => x.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }
    }

    private static void TryDeleteEmptyTree(string root, bool keepRootIfFilesRemain)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in SafeFileEnumeration.Directories(root).OrderByDescending(x => x.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            else if (!keepRootIfFilesRemain) Directory.Delete(root, true);
        }
        catch { }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
