using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record UpstreamAddonPackDefinition(
    string SectionId,
    string PackageName,
    string? Description,
    string? DownloadUrl,
    string? DownloadUrl32,
    string? DownloadUrl64,
    string? RepositoryUrl,
    string? EffectInstallPath,
    string? DeployFileName,
    string? ReleaseApiUrl,
    bool Disabled);


public sealed record UpstreamShaderPackDefinition(
    string Id,
    string? DisplayName,
    string? Kind,
    string? Url,
    bool? IsMinimum,
    string? AssetExt,
    string? Description,
    string? Category,
    IReadOnlyList<string> Requires,
    bool Disabled);

public sealed record UpstreamRenoDxExtraOptionDefinition(string Value, string Name);

public sealed record UpstreamRenoDxExtraSettingDefinition(
    string Key,
    string Label,
    string DefaultValue,
    IReadOnlyList<UpstreamRenoDxExtraOptionDefinition> Options);

/// <summary>
/// Linux-consumable subset of the upstream RHI per-game manifest contract.
/// Windows-only settings (NVIDIA Profile Inspector, WinUI state, etc.) intentionally stay out.
/// </summary>
public sealed record UpstreamGameOverrideDefinition(
    string GameName,
    string? EngineOverride,
    string? EngineHint,
    string? SnapshotUrl,
    PeArchitecture? ForcedArchitecture,
    IReadOnlyDictionary<string, string> RenoDxIniOverrides,
    bool DofFixForced,
    bool DofFixSkipped,
    string? EngineIniProjectOverride,
    GraphicsApi? GraphicsApiOverride,
    IReadOnlyList<GraphicsApi> GraphicsApiOverrides);

internal sealed partial class UpstreamManifestDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("addonPacks")]
    public Dictionary<string, UpstreamManifestAddonPack>? AddonPacks { get; set; }

    [JsonPropertyName("shaderPacks")]
    public Dictionary<string, UpstreamManifestShaderPack>? ShaderPacks { get; set; }

    [JsonPropertyName("engineOverrides")]
    public Dictionary<string, string>? LegacyEngineOverrides { get; set; }

    // Upstream RHI 2.7.6+ renamed this key to "_engineOverrides"; older manifests use the bare name.
    [JsonPropertyName("_engineOverrides")]
    public Dictionary<string, string>? PrefixedEngineOverrides { get; set; }

    [JsonIgnore]
    public Dictionary<string, string>? EngineOverrides => PrefixedEngineOverrides ?? LegacyEngineOverrides;

    [JsonPropertyName("engineHintOverrides")]
    public Dictionary<string, string>? EngineHintOverrides { get; set; }

    [JsonPropertyName("engineIniPathOverrides")]
    public Dictionary<string, string>? EngineIniPathOverrides { get; set; }

    [JsonPropertyName("engineIniFiles")]
    public Dictionary<string, string>? EngineIniFiles { get; set; }

    [JsonPropertyName("graphicsApiOverrides")]
    public Dictionary<string, string>? GraphicsApiOverrides { get; set; }

    [JsonPropertyName("snapshotOverrides")]
    public Dictionary<string, string>? SnapshotOverrides { get; set; }

    [JsonPropertyName("thirtyTwoBitGames")]
    public List<string>? ThirtyTwoBitGames { get; set; }

    [JsonPropertyName("sixtyFourBitGames")]
    public List<string>? SixtyFourBitGames { get; set; }

    [JsonPropertyName("renodxIniOverrides")]
    public Dictionary<string, Dictionary<string, string>>? RenoDxIniOverrides { get; set; }

    [JsonPropertyName("renodxExtraSettings")]
    public List<UpstreamManifestRenoDxExtraSetting>? RenoDxExtraSettings { get; set; }

    [JsonPropertyName("dofFixForceGames")]
    public List<string>? DofFixForceGames { get; set; }

    [JsonPropertyName("dofFixSkipGames")]
    public List<string>? DofFixSkipGames { get; set; }

    [JsonPropertyName("lumaNameOverrides")]
    public Dictionary<string, string>? LumaNameOverrides { get; set; }

    [JsonPropertyName("lumaDefaultGames")]
    public List<string>? LumaDefaultGames { get; set; }

    [JsonPropertyName("lumaRenodxCompat")]
    public List<string>? LumaRenoDxCompat { get; set; }

    [JsonPropertyName("lumaRequiresDgVoodoo")]
    public List<string>? LumaRequiresDgVoodoo { get; set; }

    [JsonPropertyName("dgVoodooVersions")]
    public Dictionary<string, string>? DgVoodooVersions { get; set; }

    [JsonPropertyName("componentUrls")]
    public Dictionary<string, string>? ComponentUrls { get; set; }
}


internal sealed class UpstreamManifestRenoDxExtraSetting
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("default")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("options")]
    public List<UpstreamManifestRenoDxExtraOption>? Options { get; set; }
}

internal sealed class UpstreamManifestRenoDxExtraOption
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}


internal sealed class UpstreamManifestShaderPack
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("isMinimum")]
    public bool? IsMinimum { get; set; }

    [JsonPropertyName("assetExt")]
    public string? AssetExt { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("requires")]
    public List<string>? Requires { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }
}

internal sealed class UpstreamManifestAddonPack
{
    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("downloadUrl32")]
    public string? DownloadUrl32 { get; set; }

    [JsonPropertyName("downloadUrl64")]
    public string? DownloadUrl64 { get; set; }

    [JsonPropertyName("repositoryUrl")]
    public string? RepositoryUrl { get; set; }

    [JsonPropertyName("effectInstallPath")]
    public string? EffectInstallPath { get; set; }

    [JsonPropertyName("deployFileName")]
    public string? DeployFileName { get; set; }

    [JsonPropertyName("releaseApiUrl")]
    public string? ReleaseApiUrl { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }
}

/// <summary>
/// Reads upstream RHI's live remote manifest with a last-known-good disk cache and a pinned
/// upstream fallback for offline/bootstrap recovery. Only fields with defined Linux semantics
/// are enacted; additional upstream metadata is ignored until a Linux implementation consumes it.
/// </summary>
public sealed partial class UpstreamRhiManifestService
{
    public const string LiveManifestUrl =
        "https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json";
    public const string PinnedManifestCommit = "a94a574515da3a0864f76a6d120dc4957f61e91f";
    public const string PinnedManifestUrl =
        "https://raw.githubusercontent.com/RankFTW/RHI/a94a574515da3a0864f76a6d120dc4957f61e91f/manifest.json";

    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string? _fallbackManifestUrl;
    private readonly string? _cachePath;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private UpstreamManifestDocument? _manifestCache;

    public UpstreamRhiManifestService(
        HttpClient http,
        string? manifestUrl = null,
        string? fallbackManifestUrl = null,
        string? cachePath = null)
    {
        _http = http;
        _manifestUrl = manifestUrl ?? LiveManifestUrl;
        _fallbackManifestUrl = manifestUrl is null ? fallbackManifestUrl ?? PinnedManifestUrl : fallbackManifestUrl;
        _cachePath = manifestUrl is null
            ? cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "upstream-manifest.json")
            : cachePath;
    }


    public async Task<IReadOnlyDictionary<string, UpstreamShaderPackDefinition>> FetchShaderPacksAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, UpstreamShaderPackDefinition>(StringComparer.OrdinalIgnoreCase);
        if (manifest.ShaderPacks is null) return result;

        foreach (var (id, entry) in manifest.ShaderPacks)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            result[id] = new UpstreamShaderPackDefinition(
                id,
                entry.DisplayName,
                entry.Kind,
                entry.Url,
                entry.IsMinimum,
                entry.AssetExt,
                entry.Description,
                entry.Category,
                (entry.Requires ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                entry.Disabled == true);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, UpstreamAddonPackDefinition>> FetchAddonPacksAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, UpstreamAddonPackDefinition>(StringComparer.OrdinalIgnoreCase);
        if (manifest.AddonPacks is null) return result;

        foreach (var (sectionId, entry) in manifest.AddonPacks)
        {
            if (string.IsNullOrWhiteSpace(sectionId) || string.IsNullOrWhiteSpace(entry.PackageName)) continue;
            result[sectionId] = new UpstreamAddonPackDefinition(
                sectionId,
                entry.PackageName.Trim(),
                entry.Description,
                entry.DownloadUrl,
                entry.DownloadUrl32,
                entry.DownloadUrl64,
                entry.RepositoryUrl,
                entry.EffectInstallPath,
                entry.DeployFileName,
                entry.ReleaseApiUrl,
                entry.Disabled == true);
        }

        return result;
    }


    public async Task<IReadOnlyList<UpstreamRenoDxExtraSettingDefinition>> FetchRenoDxExtraSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (manifest.RenoDxExtraSettings is null || manifest.RenoDxExtraSettings.Count == 0)
            return Array.Empty<UpstreamRenoDxExtraSettingDefinition>();

        var result = new List<UpstreamRenoDxExtraSettingDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in manifest.RenoDxExtraSettings)
        {
            var key = raw.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;

            var options = (raw.Options ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new UpstreamRenoDxExtraOptionDefinition(
                    x.Value!.Trim(),
                    string.IsNullOrWhiteSpace(x.Name) ? x.Value!.Trim() : x.Name!.Trim()))
                .ToList();
            if (options.Count == 0)
            {
                options.Add(new("0", "Off"));
                options.Add(new("1", "On"));
            }

            result.Add(new UpstreamRenoDxExtraSettingDefinition(
                key,
                string.IsNullOrWhiteSpace(raw.Label) ? key : raw.Label!.Trim(),
                string.IsNullOrWhiteSpace(raw.DefaultValue) ? "0" : raw.DefaultValue!.Trim(),
                options));
        }

        return result;
    }

    public async Task<string?> FetchComponentUrlAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return NullIfWhiteSpace(GetValueIgnoreCase(manifest.ComponentUrls, key.Trim()));
    }


    public async Task<IReadOnlyDictionary<string, string>> FetchLumaNameOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.LumaNameOverrides is null) return result;
        foreach (var (game, mapped) in manifest.LumaNameOverrides)
        {
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(mapped)) continue;
            result[game.Trim()] = mapped.Trim();
        }
        return result;
    }

    /// <summary>
    /// Resolves the upstream lumaNameOverrides mapping for a Linux-discovered game.
    /// Exact launcher title wins; when launcher naming differs, a unique manifest title equal to one
    /// of the game's own folder names or its executable stem is accepted, as for other per-game
    /// manifest overrides.
    /// </summary>
    public async Task<string?> ResolveLumaNameOverrideAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var values = await FetchLumaNameOverridesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName)
            && values.TryGetValue(game.CanonicalManifestName.Trim(), out var canonical))
            return canonical;
        if (!string.IsNullOrWhiteSpace(game.Name) && values.TryGetValue(game.Name.Trim(), out var exact))
            return exact;

        var evidence = RHI.Platform.Linux.Detection.GamePathIdentity.Of(game);
        if (evidence.IsEmpty) return null;

        var matches = values
            .Where(pair => RHI.Platform.Linux.Detection.GamePathIdentity.Matches(evidence, pair.Key))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    public async Task<IReadOnlySet<string>> FetchLumaDefaultGamesAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(
            (manifest.LumaDefaultGames ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> FetchLumaRenoDxCompatAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(
            (manifest.LumaRenoDxCompat ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> FetchLumaRequiresDgVoodooAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(
            (manifest.LumaRequiresDgVoodoo ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, string>> FetchDgVoodooVersionsAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (version, url) in manifest.DgVoodooVersions ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) continue;
            result[version.Trim()] = url.Trim();
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, UpstreamGameOverrideDefinition>> FetchGameOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeys(names, manifest.EngineOverrides?.Keys);
        AddKeys(names, manifest.EngineHintOverrides?.Keys);
        AddKeys(names, manifest.EngineIniPathOverrides?.Keys);
        AddKeys(names, manifest.GraphicsApiOverrides?.Keys);
        AddKeys(names, manifest.SnapshotOverrides?.Keys);
        AddKeys(names, manifest.ThirtyTwoBitGames);
        AddKeys(names, manifest.SixtyFourBitGames);
        AddKeys(names, manifest.RenoDxIniOverrides?.Keys);
        AddKeys(names, manifest.DofFixForceGames);
        AddKeys(names, manifest.DofFixSkipGames);

        var x86 = new HashSet<string>(manifest.ThirtyTwoBitGames ?? [], StringComparer.OrdinalIgnoreCase);
        var x64 = new HashSet<string>(manifest.SixtyFourBitGames ?? [], StringComparer.OrdinalIgnoreCase);
        var dofForce = new HashSet<string>(manifest.DofFixForceGames ?? [], StringComparer.OrdinalIgnoreCase);
        var dofSkip = new HashSet<string>(manifest.DofFixSkipGames ?? [], StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, UpstreamGameOverrideDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var engineOverride = GetValueIgnoreCase(manifest.EngineOverrides, name);
            var engineHint = GetValueIgnoreCase(manifest.EngineHintOverrides, name);
            var engineIniProjectOverride = GetValueIgnoreCase(manifest.EngineIniPathOverrides, name);
            var graphicsApiOverrides = ParseGraphicsApis(GetValueIgnoreCase(manifest.GraphicsApiOverrides, name));
            GraphicsApi? graphicsApiOverride = graphicsApiOverrides.Count > 0 ? graphicsApiOverrides[0] : null;
            var snapshotUrl = GetValueIgnoreCase(manifest.SnapshotOverrides, name);

            PeArchitecture? forcedArchitecture = null;
            if (x64.Contains(name)) forcedArchitecture = PeArchitecture.X64;
            else if (x86.Contains(name)) forcedArchitecture = PeArchitecture.X86;

            var renoDxIniOverrides = GetNestedValueIgnoreCase(manifest.RenoDxIniOverrides, name);

            result[name] = new UpstreamGameOverrideDefinition(
                name,
                NullIfWhiteSpace(engineOverride),
                NullIfWhiteSpace(engineHint),
                NullIfWhiteSpace(snapshotUrl),
                forcedArchitecture,
                NormalizeIniOverrides(renoDxIniOverrides),
                dofForce.Contains(name),
                dofSkip.Contains(name),
                NullIfWhiteSpace(engineIniProjectOverride),
                graphicsApiOverride,
                graphicsApiOverrides);
        }

        return result;
    }

    public async Task<UpstreamGameOverrideDefinition?> ResolveGameOverrideAsync(
        string? gameName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;
        var overrides = await FetchGameOverridesAsync(cancellationToken).ConfigureAwait(false);
        return overrides.TryGetValue(gameName.Trim(), out var value) ? value : null;
    }

    /// <summary>
    /// Resolves upstream metadata for a Linux-discovered game. Launcher/sideload names are
    /// not always the canonical Windows RHI title (for example Heroic may expose "Expedition 33"
    /// while the manifest key is "Clair Obscur: Expedition 33"). Exact title identity remains
    /// authoritative; when that fails, a unique canonical manifest title equal to one of the game's
    /// own folder names or its executable stem is accepted as Linux launcher identity evidence.
    /// </summary>
    public async Task<UpstreamGameOverrideDefinition?> ResolveGameOverrideAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        var overrides = await FetchGameOverridesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName)
            && overrides.TryGetValue(game.CanonicalManifestName.Trim(), out var canonical))
            return canonical;
        if (!string.IsNullOrWhiteSpace(game.Name)
            && overrides.TryGetValue(game.Name.Trim(), out var exact))
            return exact;

        // Path evidence: the game's own folder names or executable stem, or a parent folder that
        // carries the whole title the card's own name is part of; never any phrase in the full path (Refs #62).
        var evidence = RHI.Platform.Linux.Detection.GamePathIdentity.Of(game);
        if (evidence.IsEmpty) return null;

        var pathMatches = overrides.Values
            .Where(definition => RHI.Platform.Linux.Detection.GamePathIdentity.Matches(evidence, definition.GameName))
            .DistinctBy(definition => definition.GameName, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        return pathMatches.Length == 1 ? pathMatches[0] : null;
    }

    private async Task<UpstreamManifestDocument> FetchDocumentAsync(CancellationToken cancellationToken)
    {
        if (_manifestCache is not null) return _manifestCache;

        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_manifestCache is not null) return _manifestCache;

            Exception? remoteFailure = null;
            try
            {
                var (document, raw) = await DownloadDocumentAsync(_manifestUrl, cancellationToken).ConfigureAwait(false);
                _manifestCache = document;
                await PersistLastKnownGoodAsync(raw, cancellationToken).ConfigureAwait(false);
                return document;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                remoteFailure = ex;
            }

            var cached = await TryReadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _manifestCache = cached;
                return cached;
            }

            if (!string.IsNullOrWhiteSpace(_fallbackManifestUrl)
                && !string.Equals(_fallbackManifestUrl, _manifestUrl, StringComparison.Ordinal))
            {
                try
                {
                    var (fallback, fallbackRaw) = await DownloadDocumentAsync(_fallbackManifestUrl!, cancellationToken).ConfigureAwait(false);
                    _manifestCache = fallback;
                    await PersistLastKnownGoodAsync(fallbackRaw, cancellationToken).ConfigureAwait(false);
                    return fallback;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception fallbackFailure)
                {
                    throw new InvalidDataException(
                        $"Upstream RHI manifest unavailable. Live fetch failed: {remoteFailure?.Message}. " +
                        $"Pinned fallback failed: {fallbackFailure.Message}",
                        fallbackFailure);
                }
            }

            throw new InvalidDataException($"Upstream RHI manifest unavailable: {remoteFailure?.Message}", remoteFailure);
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    public void InvalidateProcessCache() => _manifestCache = null;

    private async Task<(UpstreamManifestDocument Document, byte[] Raw)> DownloadDocumentAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<UpstreamManifestDocument>(raw)
                ?? throw new InvalidDataException("Upstream RHI manifest is empty or invalid.");
            if (document.Version <= 0)
                throw new InvalidDataException("Upstream RHI manifest has no valid version.");
            return (document, raw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out fetching upstream RHI manifest from {url}.");
        }
    }

    internal async Task PersistLastKnownGoodAsync(byte[] raw, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cachePath)) return;
        string? temp = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            temp = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await stream.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _cachePath, true);
            temp = null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Cache persistence is optional; a valid live manifest must remain usable.
        }
        finally
        {
            // A cancelled or failed write (e.g. the Donate dialog closed mid-write) leaves no manifest.json.tmp-* behind.
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { /* best effort: the cache directory is RHI's own */ }
            }
        }
    }

    private async Task<UpstreamManifestDocument?> TryReadLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath)) return null;
        try
        {
            var raw = await File.ReadAllBytesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<UpstreamManifestDocument>(raw);
            return document is { Version: > 0 } ? document : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }



    internal static GraphicsApi? ParseGraphicsApi(string? value)
    {
        var values = ParseGraphicsApis(value);
        return values.Count > 0 ? values[0] : null;
    }

    internal static IReadOnlyList<GraphicsApi> ParseGraphicsApis(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var result = new List<GraphicsApi>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = new string(token.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var api = normalized switch
            {
                "directx12" or "dx12" or "d3d12" => GraphicsApi.DirectX12,
                "directx11" or "dx11" or "d3d11" => GraphicsApi.DirectX11,
                "directx10" or "dx10" or "d3d10" => GraphicsApi.DirectX10,
                "directx9" or "dx9" or "d3d9" => GraphicsApi.DirectX9,
                "directx8" or "dx8" or "d3d8" => GraphicsApi.DirectX8,
                "vulkan" or "vlk" => GraphicsApi.Vulkan,
                "opengl" or "ogl" or "gl" => GraphicsApi.OpenGL,
                _ => GraphicsApi.Unknown
            };
            if (api != GraphicsApi.Unknown && !result.Contains(api))
                result.Add(api);
        }
        return result;
    }

    private static void AddKeys(HashSet<string> target, IEnumerable<string>? values)
    {
        if (values is null) return;
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                target.Add(value.Trim());
    }

    private static string? GetValueIgnoreCase(IReadOnlyDictionary<string, string>? values, string key)
    {
        if (values is null) return null;
        if (values.TryGetValue(key, out var direct)) return direct;
        foreach (var pair in values)
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }


    private static IReadOnlyDictionary<string, string> NormalizeIniOverrides(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null) continue;
            result[key.Trim()] = value.Trim();
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string>? GetNestedValueIgnoreCase(
        IReadOnlyDictionary<string, Dictionary<string, string>>? values,
        string key)
    {
        if (values is null) return null;
        if (values.TryGetValue(key, out var direct)) return direct;
        foreach (var pair in values)
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
