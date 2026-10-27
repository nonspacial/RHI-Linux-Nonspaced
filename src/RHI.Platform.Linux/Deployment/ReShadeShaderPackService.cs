using System.IO.Compression;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

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
    /// <summary>A sync staged and validated by <see cref="PrepareSyncAsync"/>, applied by <see cref="ApplySync"/>.</summary>
    internal sealed record PreparedShaderSync(
        string Root,
        string ManifestPath,
        Manifest Previous,
        string[] Normalized,
        Dictionary<string, string> StagedById,
        Dictionary<string, HashSet<string>> ExclusionsById,
        bool NeedsCoreHeaders,
        string? CoreHeadersStaged);

    internal sealed class Manifest
    {
        public int Schema { get; set; } = 2;
        public List<string> PackIds { get; set; } = [];
        public List<ManifestFile> Files { get; set; } = [];
    }

    internal sealed class ManifestFile
    {
        public string RelativePath { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private sealed class PackSettings
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, List<string>> ExcludedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }


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
        var prepared = await PrepareSyncAsync(gameDirectory, selectedPackIds, cancellationToken).ConfigureAwait(false);
        return await ApplySyncAsync(prepared, operation: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// w3/parity hook (#93, shader-profile zip export; read-only): the files a sync of <paramref name="packId"/> would
    /// deploy — its staged <c>Shaders/…</c> and <c>Textures/…</c> files minus the user's and the global exclusions — with
    /// the path of each staged copy. Stages the pack first, as <see cref="ListPackFilesAsync"/> does.
    /// </summary>
    public async Task<IReadOnlyList<(string Selector, string StagedPath)>> ListDeployedPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);
        var pack = _packs.FirstOrDefault(p => p.Id.Equals(packId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown ReShade shader pack id: {packId}");
        var staged = pack.IsCustom ? EnsureCustomPack() : await EnsurePackAsync(pack, cancellationToken).ConfigureAwait(false);
        var excluded = new HashSet<string>(GetExcludedFiles(pack.Id), StringComparer.OrdinalIgnoreCase);
        if (!pack.IsCustom) excluded.UnionWith(GloballyExcludedShaderFiles);
        var result = new List<(string, string)>();
        foreach (var category in new[] { "Shaders", "Textures" })
        {
            var root = Path.Combine(staged, category);
            if (!Directory.Exists(root)) continue;
            foreach (var file in SafeFileEnumeration.Files(root))
                if (!IsExcluded(excluded, category, root, file))
                    result.Add(($"{category}/{Path.GetRelativePath(root, file).Replace('\\', '/')}", file));
        }
        return result;
    }

    /// <summary>
    /// Everything a sync needs from the network (catalogue, packs, core headers), staged and checked against the game
    /// folder, with nothing in it changed: a deployment prepares the sync before its write gate and applies it with
    /// <see cref="ApplySync"/> after (w2 merge review, minor 1: the packs used to download after ReShade was copied).
    /// </summary>
    internal async Task<PreparedShaderSync> PrepareSyncAsync(
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

        // #239 (upstream EnsureReShadeHeaders): ReShade.fxh/ReShadeUI.fxh go with every sync that deploys a pack — most
        // packs include them and ship no copy (Prod80 failed to compile without them); owned through the manifest like a
        // pack file, so they leave with the last RHI pack.
        var needsCoreHeaders = normalized.Length > 0;

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
            try
            {
                var staged = await EnsurePackAsync(CoreHeadersPack, cancellationToken).ConfigureAwait(false);
                EnsureCoreHeadersPresent(staged);
                coreHeadersStaged = staged;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // As upstream (a header missing from its staging folder is skipped): the packs still go in — a local custom
                // pack works offline — and the next sync that can stage the headers brings them.
                AppLog.Warn("shaders", "ReShade's core headers (ReShade.fxh, ReShadeUI.fxh) could not be staged; the shader packs are deployed without them", ex);
            }
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

        if (coreHeadersStaged is not null)
        {
            var targetCoreRoot = Path.Combine(root, "_Core", "Shaders");
            foreach (var header in CoreHeaderNames)
                ValidateDestinationFile(
                    Path.Combine(coreHeadersStaged!, "Shaders", header),
                    Path.Combine(targetCoreRoot, header),
                    root,
                    previousByPath);
        }

        return new PreparedShaderSync(root, manifestPath, previous, normalized, stagedById, exclusionsById, needsCoreHeaders, coreHeadersStaged);
    }

    /// <summary>
    /// The game-folder half of a sync <see cref="PrepareSyncAsync"/> prepared: no network. With <paramref name="operation"/>
    /// (#169: the ReShade install's journaled operation) every shader file it removes or places and the ownership
    /// manifest are tracked first and the placed content declared, so a failure — or a crash, at the next start — puts the
    /// pack folders back as they were.
    /// </summary>
    internal async Task<ReShadeShaderSyncResult> ApplySyncAsync(PreparedShaderSync prepared, DeploymentOperation? operation, CancellationToken cancellationToken = default)
    {
        var (root, manifestPath, previous, normalized, stagedById, exclusionsById, needsCoreHeaders, coreHeadersStaged) = prepared;
        // Everything is staged and validated: from the first change in the game folder on, the sync runs to the end
        // (a cancel half-way used to leave copied packs that no manifest owns).
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        await RemoveManifestFilesAsync(root, previous, operation).ConfigureAwait(false);
        if (normalized.Length == 0)
        {
            if (operation is not null && File.Exists(manifestPath))
                await operation.TrackFileAsync(manifestPath, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
            if (!FileCleanup.TryDelete(manifestPath, out var manifestError))
                AppLog.Warn("shaders", $"Could not remove the shader manifest {manifestPath}", manifestError);
            FileCleanup.TryDeleteEmptyTree(root);
            return new(root, [], [], []);
        }

        Directory.CreateDirectory(root);
        var outputManifest = new Manifest { PackIds = normalized.ToList() };
        var effectPaths = new List<string>();
        var texturePaths = new List<string>();

        foreach (var id in normalized)
        {
            var staged = stagedById[id];
            var safeId = SafePackDirectoryName(id);
            var targetPackRoot = Path.Combine(root, safeId);
            var stagedShaders = Path.Combine(staged, "Shaders");
            var stagedTextures = Path.Combine(staged, "Textures");

            if (Directory.Exists(stagedShaders))
            {
                var targetShaders = Path.Combine(targetPackRoot, "Shaders");
                await CopyTreeAsync(stagedShaders, targetShaders, root, outputManifest, operation, exclusionsById[id], "Shaders").ConfigureAwait(false);
                if (Directory.Exists(targetShaders) && SafeFileEnumeration.Files(targetShaders).Any())
                    effectPaths.Add($@".\reshade-shaders\RHI\{safeId}\Shaders\**");
            }

            if (Directory.Exists(stagedTextures))
            {
                var targetTextures = Path.Combine(targetPackRoot, "Textures");
                await CopyTreeAsync(stagedTextures, targetTextures, root, outputManifest, operation, exclusionsById[id], "Textures").ConfigureAwait(false);
                if (Directory.Exists(targetTextures) && SafeFileEnumeration.Files(targetTextures).Any())
                    texturePaths.Add($@".\reshade-shaders\RHI\{safeId}\Textures\**");
            }
        }

        if (coreHeadersStaged is not null)
        {
            var targetCoreRoot = Path.Combine(root, "_Core", "Shaders");
            foreach (var header in CoreHeaderNames)
                await CopyManagedFileAsync(
                    Path.Combine(coreHeadersStaged!, "Shaders", header),
                    Path.Combine(targetCoreRoot, header),
                    root,
                    outputManifest,
                    operation).ConfigureAwait(false);
            effectPaths.Add(@".\reshade-shaders\RHI\_Core\Shaders\**");
        }

        var manifestFile = new JsonFileStore<Manifest>(manifestPath);
        if (operation is null) manifestFile.Save(outputManifest);
        else
        {
            // Declared BEFORE the write (w6/crash review 3): a crash right after it rolls the manifest back with the pack
            // files; declared after, recovery would keep the new manifest as user content over the restored packs.
            var manifestBytes = manifestFile.Stage(_ => outputManifest)!.After;
            await operation.TrackFileAsync(manifestPath, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
            await operation.RecordWriteAsync(manifestPath, FileHash.Sha256HexOf(manifestBytes), CancellationToken.None).ConfigureAwait(false);
            AtomicFile.WriteStoreFile(manifestPath, manifestBytes);
            ManifestWrittenForTesting?.Invoke(manifestPath); // straight after the write: a crash test here fails if the declaration moves after it
        }
        return new(root, effectPaths, texturePaths, normalized);
    }

    /// <summary>
    /// Removes RHI's shader packs and their manifest from the game; inside <paramref name="operation"/> (#169) each file
    /// and the manifest are tracked first, so a rollback (or the next start after a crash) puts them back.
    /// </summary>
    internal async Task CleanupAsync(string gameDirectory, DeploymentOperation? operation)
    {
        var root = Path.Combine(gameDirectory, "reshade-shaders", "RHI");
        var manifestPath = Path.Combine(root, ".rhi-manifest.json");
        if (!File.Exists(manifestPath)) return;
        var manifest = LoadManifest(manifestPath);
        await RemoveManifestFilesAsync(root, manifest, operation).ConfigureAwait(false);
        if (operation is not null) await operation.TrackFileAsync(manifestPath, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
        if (!FileCleanup.TryDelete(manifestPath, out var removeError))
            AppLog.Warn("shaders", $"Could not remove the shader manifest {manifestPath}", removeError);
        FileCleanup.TryDeleteEmptyTree(root);
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

    /// <summary>
    /// #179: the pack ids a sync of <paramref name="ids"/> deploys (dependencies added) in <paramref name="catalog"/>, so
    /// two selections that deploy the same packs compare equal (a record holds the expanded list).
    /// </summary>
    public static IReadOnlySet<string> DeployedPackIds(IEnumerable<string> ids, IReadOnlyList<ReShadeShaderPackInfo> catalog)
    {
        var definitions = catalog.Select(pack => new ReShadeShaderPackDefinition(pack.Id, pack.DisplayName, pack.Description,
            ReShadeShaderPackSourceKind.DirectUrl, "", Requires: pack.Requires, IsCustom: pack.IsCustom)).ToList();
        return ExpandDependencies(ids, definitions).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    // One pack cache writer at a time (#194): the start-up prefetch and an install's staging never build the same entry
    // together (one would delete the other's freshly moved tree).
    private readonly SemaphoreSlim _packGate = new(1, 1);

    /// <summary>Test seam: runs straight after a journaled sync wrote the game's shader-pack manifest (inside the journaled branch).</summary>
    internal Action<string>? ManifestWrittenForTesting { get; set; }

    /// <summary>Test seam: runs in the prefetch right before it waits for the pack cache gate.</summary>
    internal Func<ReShadeShaderPackDefinition, Task>? BeforePrefetchGateForTesting { get; set; }

    /// <summary>
    /// #179: the global shader set and each game's Global / Select / Off choice. Null (tests, a service built without it):
    /// every install keeps the packs its caller asked for, as before.
    /// </summary>
    public GlobalShaderAddonStore? Sets { get; set; }

    /// <summary>
    /// #179, the one answer to "which shader packs does this game get": Off → none; Select → the game's own list; Global →
    /// the global set (upstream's global Off is an empty set, its Custom mode the set holding only the local custom
    /// folder). Null when no store is attached or its file cannot be read (the caller's list stands).
    /// </summary>
    public IReadOnlyList<string>? EffectivePackIds(DetectedGame game)
        => Sets is { } sets && sets.ChoiceFor(game) is { } choice ? EffectivePackIds(choice, sets.GlobalShaderPackIds) : null;

    internal static IReadOnlyList<string> EffectivePackIds(ShaderAddonGameChoice choice, IReadOnlyList<string> globalSet)
        => choice.ShaderMode switch
        {
            ShaderAddonMode.Off => [],
            ShaderAddonMode.Select => choice.ShaderPackIds,
            _ => globalSet,
        };

    /// <summary>
    /// #179 hook for ReShade's install and update (every path: the ReShade row, a dependency install, profile Apply,
    /// Update All): the packs become the game's effective set, and a game seen for the first time has its choice saved
    /// first (so the record this install writes is never taken for an older build's selection). Unchanged without a
    /// readable store.
    /// </summary>
    public ReShadeInstallOptions WithEffectiveSelection(DetectedGame game, ReShadeInstallOptions options)
    {
        if (Sets is not { } sets) return options;
        sets.Pin(game);
        if (EffectivePackIds(game) is not { } packs) return options;
        // Review major 2: a set this game cannot compile (no native D3DCompiler_47 beside it or in its prefix, none chosen,
        // not the Vulkan layer) keeps the caller's own list, so ReShade itself still installs; the managed paths try to get
        // the compiler first and say why the packs are missing.
        if (packs.Count > 0 && !options.VulkanLayer && string.IsNullOrWhiteSpace(options.D3DCompiler47Path) && !HasNativeCompiler(game))
            return options;
        options.ShaderPackIds = packs.ToArray();
        return options;
    }

    private static bool HasNativeCompiler(DetectedGame game)
        => GameArchitecture.Of(game) is var architecture and (PeArchitecture.X86 or PeArchitecture.X64)
           && ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture) is not null;

    internal async Task<string> EnsurePackAsync(
        ReShadeShaderPackDefinition pack,
        CancellationToken cancellationToken)
    {
        await _packGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsurePackCoreAsync(pack, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _packGate.Release();
        }
    }

    /// <summary>
    /// #194 (upstream 2.8.7 <c>CacheAllShaders</c>, parity T-19): stages every pack the catalogue offers (and the core
    /// headers) that has no cache entry yet, so a later install needs no download. An entry already there is left as it
    /// is (an install refreshes it on demand), so this never replaces a tree an install may be copying from: the check
    /// is made under the pack cache gate (w6/crash review 4), so an install that staged the pack while the prefetch
    /// waited is seen. Cache only: nothing in any game changes. A pack that fails is logged and skipped. Returns how many
    /// packs it staged.
    /// </summary>
    public async Task<int> PrefetchMissingAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);
        // #179: the global set's packs first, so a game that follows it finds them staged soonest.
        IReadOnlyList<string> first = Sets is { } sets ? sets.GlobalShaderPackIds : [];
        var fetched = 0;
        foreach (var pack in _packs.Where(pack => !pack.IsCustom)
                     .OrderBy(pack => first.Contains(pack.Id, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
                     .Append(CoreHeadersPack).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await EnsurePackIfMissingAsync(pack, cancellationToken).ConfigureAwait(false)) fetched++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn("shaders", $"Could not stage the shader pack {pack.DisplayName} at start-up", ex);
            }
        }
        return fetched;
    }

    /// <summary>Stages <paramref name="pack"/> only when it has no cache entry, checked under the pack cache gate.</summary>
    private async Task<bool> EnsurePackIfMissingAsync(ReShadeShaderPackDefinition pack, CancellationToken cancellationToken)
    {
        if (BeforePrefetchGateForTesting is { } beforeGate) await beforeGate(pack).ConfigureAwait(false);
        await _packGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(Path.Combine(_cacheDir, SafePackDirectoryName(pack.Id), "extracted"))) return false;
            await EnsurePackCoreAsync(pack, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _packGate.Release();
        }
    }

    private async Task<string> EnsurePackCoreAsync(
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

        var raw = Path.Combine(packDir, "raw-" + Guid.NewGuid().ToString("N"));
        // Shader-pack host policy (GitHub, codeload.github.com, GitLab) on the link and every redirect, size cap (#42).
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, downloadUrl, Path.Combine(packDir, "download" + (string.IsNullOrWhiteSpace(extension) ? "" : extension)),
            DownloadSources.ShaderPacks, cancellationToken: cancellationToken).ConfigureAwait(false);
        var temp = download.Path;

        try
        {
            if (new FileInfo(temp).Length < 512)
                throw new InvalidOperationException($"Shader pack {pack.DisplayName} download is unexpectedly small.");

            Directory.CreateDirectory(raw);
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                (pack.AssetExt?.Equals(".zip", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                await ExtractZipSafelyAsync(temp, raw, cancellationToken).ConfigureAwait(false);
            }
            else if (downloadUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                     (pack.AssetExt?.Equals(".7z", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                // Host tool through the shared runner: time limit, process-tree kill, link check (#37).
                await ExternalArchiveRunner.ExtractAsync(temp, raw, cancellationToken).ConfigureAwait(false);
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
            AtomicFile.WriteAllText(sourceMarker, sourceIdentity);
            AtomicFile.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            CacheStamp.Write(extracted, downloadUrl, sourceIdentity);
            return extracted;
        }
        finally
        {
            try { if (Directory.Exists(raw)) Directory.Delete(raw, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("shaders", $"Could not remove the extraction folder {raw}", ex);
            }
        }
    }

    private string EnsureCustomPack()
    {
        EnsureCustomDirectories();
        foreach (var link in LinkedFoldersNotDeployed(_customRoot))
            AppLog.Warn("shaders", $"{link} is a folder symlink in the custom shader folder; its files are not deployed (links are never followed into a game). Copy the files in instead.");
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
            AppLog.Warn("shaders", $"custom shader folder {customRoot} not fully readable", ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shaders", $"Could not create the custom shader folders under {_customRoot}", ex);
        }
    }

    private async Task<(string DownloadUrl, string SourceIdentity)> ResolveGitHubReleaseAssetAsync(
        string apiUrl,
        string assetExtension,
        CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, apiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
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

    private static async Task ExtractZipSafelyAsync(string archivePath, string outputRoot, CancellationToken cancellationToken)
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
            // Checked extraction (declared-size cap, fixed mode, length + CRC-32, atomic rename); never ExtractToFile (#42).
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, destination, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
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
            // Verified, durable copy (#31): the normalised tree becomes the cached pack every install copies from.
            AtomicFile.Copy(file, destination);
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
            !string.Equals(FileHash.Sha256Hex(destination), old.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Protected shader file in RHI path was modified or is externally owned: {destination}");
    }

    private static async Task CopyManagedFileAsync(
        string source,
        string destination,
        string managedRoot,
        Manifest manifest,
        DeploymentOperation? operation)
    {
        var rootWithSep = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        destination = Path.GetFullPath(destination);
        if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new InvalidOperationException($"Shader path escapes managed root: {destination}");
        if (File.Exists(destination))
            throw new IOException($"Protected shader file already exists in RHI-managed path: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        manifest.Files.Add(new ManifestFile
        {
            RelativePath = Path.GetRelativePath(managedRoot, destination),
            Sha256 = await PlaceAsync(source, destination, operation).ConfigureAwait(false)
        });
    }

    /// <summary>
    /// One verified copy into the game's shader folder; inside <paramref name="operation"/> the target is tracked and the
    /// content declared first (#169). Never replaces a file that appeared meanwhile. Returns the placed content's SHA-256.
    /// </summary>
    private static async Task<string> PlaceAsync(string source, string destination, DeploymentOperation? operation)
    {
        if (operation is null) return AtomicFile.Copy(source, destination, overwrite: false);
        var sha = await FileHash.Sha256HexAsync(source, CancellationToken.None).ConfigureAwait(false);
        await operation.TrackFileAsync(destination, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
        await operation.RecordWriteAsync(destination, sha, CancellationToken.None).ConfigureAwait(false);
        return await AtomicFile.CopyAsync(source, destination, sha, CancellationToken.None, overwrite: false).ConfigureAwait(false);
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
                !string.Equals(FileHash.Sha256Hex(destination), old.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Protected shader file in RHI path was modified or is externally owned: {destination}");
        }
    }

    private static async Task CopyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        string managedRoot,
        Manifest manifest,
        DeploymentOperation? operation,
        IReadOnlySet<string>? excludedFileNames,
        string category)
    {
        foreach (var source in SafeFileEnumeration.Files(sourceRoot))
        {
            if (IsExcluded(excludedFileNames, category, sourceRoot, source)) continue;
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            var rootWithSep = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new InvalidOperationException($"Shader path escapes managed root: {relative}");
            if (File.Exists(destination))
                throw new IOException($"Protected shader file already exists in RHI-managed path: {destination}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            manifest.Files.Add(new ManifestFile
            {
                RelativePath = Path.GetRelativePath(managedRoot, destination),
                Sha256 = await PlaceAsync(source, destination, operation).ConfigureAwait(false)
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

    // Inside an operation each file is tracked (verified copy) before it goes, so a rollback puts it back (#169).
    private static async Task RemoveManifestFilesAsync(string root, Manifest manifest, DeploymentOperation? operation)
    {
        foreach (var file in manifest.Files.OrderByDescending(f => f.RelativePath.Length))
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                var rootWithSep = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(rootWithSep, StringComparison.Ordinal) || !File.Exists(path)) continue;
                if (!string.Equals(FileHash.Sha256Hex(path), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                if (operation is not null) await operation.TrackFileAsync(path, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
                AtomicFile.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (operation is not null) throw; // journaled: a file that cannot be tracked or removed fails the install (rolled back)
                AppLog.Warn("shaders", $"Could not remove the managed shader file {file.RelativePath}", ex);
            }
        }

        if (Directory.Exists(root))
        {
            List<string> directories;
            try { directories = SafeFileEnumeration.Directories(root).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            FileCleanup.TryDeleteEmptyDirectories(directories);
        }
    }
}
