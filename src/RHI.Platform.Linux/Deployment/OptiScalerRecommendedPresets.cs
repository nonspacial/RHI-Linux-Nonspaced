using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// One ready-made OptiScaler preset (#269, upstream 2.8.9 <c>optiscaler_presets.json</c>), as the OptiScaler ⚙ settings
/// it sets (Linux setting ids → values, in apply order) and the extras it installs.
/// </summary>
/// <param name="InstallUniversalMfg">Installs Universal MFG Unlocker when the game lacks it (UMFG XeFG).</param>
/// <param name="InstallAnrFg">Installs ANR FG as a custom FG provider (#272).</param>
public sealed record OptiScalerRecommendedPreset(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<KeyValuePair<string, string>> Settings,
    bool InstallUniversalMfg = false,
    bool InstallAnrFg = false);

/// <summary>
/// #269 (upstream 2.8.9 <c>OsPresetService.ManifestPresets</c>): the recommended OptiScaler presets. Upstream's
/// <c>optiscaler_presets.json</c> (RankFTW/RHI <c>main</c>) is read from raw.githubusercontent.com, so new presets arrive
/// without an app update; the last good copy is cached and the built-in copies (upstream's) are used until the file has
/// been read once and whenever it cannot be. Upstream field names map to the Linux ⚙ setting ids; a field the Linux ⚙
/// does not know is ignored, and a preset that sets nothing Linux knows is left out.
/// </summary>
public sealed class OptiScalerRecommendedPresets
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/RankFTW/RHI/main/optiscaler_presets.json";
    /// <summary>As upstream: at most 12 presets.</summary>
    public const int MaxPresets = 12;
    /// <summary>The file is a few KiB; anything over this is refused.</summary>
    public const int MaxBytes = 64 * 1024;
    /// <summary>The value upstream's <c>streamlineVersion</c> uses for the newest Streamline.</summary>
    public const string LatestStreamline = "latest";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient? _http;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private IReadOnlyList<OptiScalerRecommendedPreset>? _presets;

    public OptiScalerRecommendedPresets(HttpClient? http, string? cachePath = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "optiscaler_presets.json");
    }

    /// <summary>The presets: the fetched or cached file, else the built-in copies.</summary>
    public IReadOnlyList<OptiScalerRecommendedPreset> Presets
    {
        get
        {
            lock (_gate)
            {
                if (_presets is not null) return _presets;
                try
                {
                    if (File.Exists(_cachePath) && Parse(File.ReadAllText(_cachePath)) is { } cached) return _presets = cached;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("optiscaler-presets", "The cached OptiScaler presets could not be read", ex);
                }
                return _presets = BuiltIn;
            }
        }
    }

    /// <summary>Reads the file from GitHub and caches it; the current list stays on any failure.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_http is null) return;
        try
        {
            var json = await TrustedDownload.GetStringAsync(_http, ManifestUrl, DownloadSources.ComponentNotes, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes || Parse(json) is not { } presets)
            {
                AppLog.Warn("optiscaler-presets", "optiscaler_presets.json has no usable presets; the current list stays");
                return;
            }
            lock (_gate) _presets = presets;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                AtomicFile.WriteAllText(_cachePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("optiscaler-presets", "The OptiScaler presets could not be cached", ex);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("optiscaler-presets", "optiscaler_presets.json could not be read; the current list stays", ex);
        }
    }

    /// <summary>Upstream's built-in copies (2.9.4 <c>OsPresetService.BuiltInPresets</c>).</summary>
    public static IReadOnlyList<OptiScalerRecommendedPreset> BuiltIn { get; } = Parse(BuiltInJson)!;

    /// <summary>The presets in <paramref name="json"/> ("presets" then "presetsV2"); null when it has none Linux can apply.</summary>
    public static IReadOnlyList<OptiScalerRecommendedPreset>? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
            var presets = (manifest?.Presets ?? []).Concat(manifest?.PresetsV2 ?? [])
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
                .Take(MaxPresets)
                .Select(ToPreset)
                .OfType<OptiScalerRecommendedPreset>()
                .ToArray();
            return presets.Length > 0 ? presets : null;
        }
        catch (JsonException ex)
        {
            AppLog.Warn("optiscaler-presets", "optiscaler_presets.json is not valid", ex);
            return null;
        }
    }

    private static OptiScalerRecommendedPreset? ToPreset(Entry entry)
    {
        var settings = new List<KeyValuePair<string, string>>();
        void Add(string id, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) settings.Add(new(id, value.Trim()));
        }
        // The Streamline / DLSS Enabler stack and its version first: they decide which files the later settings act on.
        if (entry.DeployStreamline is { } streamline || entry.DeployDlssEnabler is not null)
            Add(OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, (entry.DeployStreamline ?? false) || (entry.DeployDlssEnabler ?? false) ? "true" : "false");
        Add("StreamlineVersion", entry.StreamlineVersion);
        Add("FrameGenEnabled", entry.FgEnabled);
        Add("FGInput", entry.FgInput);
        Add("FGOutput", entry.FgOutput);
        Add("FGNvngxReplacement", entry.FgNvngxReplacement);
        Add("HUDFix", entry.HudFix);
        Add(OptiScalerDeploymentService.ForceReflexSettingId, entry.ForceReflex);
        Add(OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, entry.UseGamesReflexMarkers);
        Add(OptiScalerDeploymentService.DxgiSpoofingSettingId, entry.DxgiSpoofing);
        if (settings.Count == 0 && entry.InstallUniversalMfg != true && entry.InstallAnrFg != true) return null;
        return new OptiScalerRecommendedPreset(
            string.IsNullOrWhiteSpace(entry.Id) ? entry.Name!.Trim() : entry.Id.Trim(),
            entry.Name!.Trim(),
            entry.Description?.Trim() ?? "",
            settings,
            entry.InstallUniversalMfg == true,
            entry.InstallAnrFg == true);
    }

    private sealed class Manifest
    {
        public List<Entry>? Presets { get; set; }
        public List<Entry>? PresetsV2 { get; set; }
    }

    private sealed class Entry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? FgEnabled { get; set; }
        public string? FgInput { get; set; }
        public string? FgOutput { get; set; }
        public string? FgNvngxReplacement { get; set; }
        public bool? DeployStreamline { get; set; }
        public bool? DeployDlssEnabler { get; set; }
        public string? StreamlineVersion { get; set; }
        public string? HudFix { get; set; }
        public string? ForceReflex { get; set; }
        public string? UseGamesReflexMarkers { get; set; }
        public string? DxgiSpoofing { get; set; }
        public bool? InstallUniversalMfg { get; set; }
        public bool? InstallAnrFg { get; set; }
    }

    // Upstream 2.9.4 optiscaler_presets.json (also its built-in copies).
    private const string BuiltInJson = """
        {
          "version": 1,
          "presets": [
            { "id": "RealDlssFg", "name": "Real DLSS FG",
              "description": "NVIDIA's own DLSS frame generation through OptiScaler, with Reflex forced on. For RTX 40 and 50 cards.",
              "fgEnabled": "true", "fgInput": "upscaler", "fgOutput": "dlssg", "fgNvngxReplacement": "None",
              "deployStreamline": true, "deployDlssEnabler": true, "streamlineVersion": "latest",
              "hudFix": "true", "forceReflex": "2", "useGamesReflexMarkers": "false" },
            { "id": "DlssEnablerFg", "name": "DLSS Enabler FG",
              "description": "DLSS frame generation through DLSS Enabler, for cards without DLSS frame generation.",
              "fgEnabled": "true", "fgInput": "upscaler", "fgOutput": "dlssg", "fgNvngxReplacement": "Arturs",
              "deployStreamline": true, "deployDlssEnabler": true, "streamlineVersion": "latest",
              "hudFix": "true", "forceReflex": "0", "useGamesReflexMarkers": "true" },
            { "id": "UmfgXefg", "name": "UMFG XeFG",
              "description": "XeSS frame generation beyond 2x with Universal MFG Unlocker, which is installed if it's missing.",
              "fgEnabled": "true", "fgInput": "upscaler", "fgOutput": "xefg",
              "deployStreamline": true, "deployDlssEnabler": true, "streamlineVersion": "latest",
              "hudFix": "true", "forceReflex": "0", "useGamesReflexMarkers": "true", "installUniversalMfg": true }
          ],
          "presetsV2": [
            { "id": "AnrFg", "name": "ANR FG",
              "description": "Apex Neural Rendering frame generation (beta) in place of the game's DLSS frame generation. Needs OptiScaler Nightly (or a build based on a recent Nightly) and a game with DLSS frame generation; turn on DLSS frame generation in the game.",
              "fgInput": "nvngxfg", "fgOutput": "auto", "fgNvngxReplacement": "Apex Neural Rendering FG", "installAnrFg": true }
          ]
        }
        """;
}
