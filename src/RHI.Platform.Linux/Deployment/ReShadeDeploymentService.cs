using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class ReShadeDeploymentService
{
    public const string HomeUrl = "https://reshade.me";

    // The one setup URL for a stable/legacy version: what EnsureSetupStagedAsync stamps and what the
    // outage fallback looks the stamp up with must be byte-identical (#142).
    internal static string StableSetupUrl(string version) => $"{HomeUrl}/downloads/ReShade_Setup_{version}_Addon.exe";
    /// <summary>The setup of the build without add-on support (#91, upstream <c>NormalReShadeUpdateService</c>); same rule as <see cref="StableSetupUrl"/>.</summary>
    internal static string NoAddonsSetupUrl(string version) => $"{HomeUrl}/downloads/ReShade_Setup_{version}.exe";

    /// <summary>Fixed text before the component name when an add-on is refused over No Addons ReShade (PT-BR presentation pair).</summary>
    public const string NoAddonsBlocksAddonPrefix =
        "ReShade on this game is the No Addons build, which cannot load add-ons; choose Stable or Nightly in the ReShade profile and apply it first: ";

    /// <summary>True when RHI's ReShade on this game is the No Addons build (#91): ReShade add-ons cannot load.</summary>
    public static bool LacksAddonSupport(LinuxDeploymentRecord? reShadeRecord)
        => reShadeRecord is not null && ParseChannel(reShadeRecord.Metadata.GetValueOrDefault(MetaChannel)) == ReShadeChannel.NoAddons;
    // Upstream 2.8.5's nightly links (ReShadeNightlyService): the newest successful build.yml run on main, served by
    // nightly.link because GitHub's own artifact download needs a login. Used as is only when the GitHub API lookup
    // below is unavailable (#27).
    internal const string Nightly64Url = "https://nightly.link/crosire/reshade/workflows/build/main/ReShade%20(64-bit).zip";
    internal const string Nightly32Url = "https://nightly.link/crosire/reshade/workflows/build/main/ReShade%20(32-bit).zip";

    /// <summary>The repository whose own builds are the nightly; a run built from any other head repository is never used.</summary>
    internal const string NightlyRepository = "crosire/reshade";

    /// <summary>
    /// The trigger events nightly.link's branch link reads (oprypin/nightly.link <c>get_latest_run</c>: push, then
    /// schedule, the newer of the two wins). A <c>pull_request</c> run is never one of them: GitHub's <c>branch</c>
    /// filter matches the head branch, so a fork's pull request from its own <c>main</c> passes it.
    /// </summary>
    internal static readonly IReadOnlyList<string> NightlyRunEvents = ["push", "schedule"];

    /// <summary>Successful build.yml runs on main for one trigger event, newest first.</summary>
    internal static string NightlyRunsApiUrl(string triggerEvent)
        => $"https://api.github.com/repos/crosire/reshade/actions/workflows/build.yml/runs?branch=main&event={triggerEvent}&status=success&per_page=10";

    /// <summary>The artifacts of one run; each carries the SHA-256 digest GitHub computed when it was uploaded.</summary>
    internal static string NightlyArtifactsApiUrl(long runId) => $"https://api.github.com/repos/crosire/reshade/actions/runs/{runId}/artifacts?per_page=100";

    /// <summary>nightly.link's link for one run's artifact (fixed content, unlike the branch link).</summary>
    internal static string NightlyRunUrl(long runId, bool is32)
        => $"https://nightly.link/crosire/reshade/actions/runs/{runId}/ReShade%20%28{(is32 ? "32" : "64")}-bit%29.zip";

    internal static string NightlyArtifactName(bool is32) => is32 ? "ReShade (32-bit)" : "ReShade (64-bit)";
    private const long MinDllSize = 1_000_000;

    private const string MetaChannel = "ReShade.Channel";
    private const string MetaSourceIdentity = "ReShade.SourceIdentity";
    private const string MetaCustomSource = "ReShade.CustomSource";
    private const string MetaLegacyVersion = "ReShade.LegacyVersion";
    private const string MetaShaderPacks = "ReShade.ShaderPacks";
    private const string MetaD3DCompilerSource = "ReShade.D3DCompiler47Source";
    private const string D3DCompilerFileName = "D3DCompiler_47.dll";
    private const string D3DCompilerOverrideName = "d3dcompiler_47";
    private const string MetaIniCreated = "ReShade.IniCreatedByRhi";
    private const string MetaIniEffectCreated = "ReShade.IniEffectKeyCreated";
    private const string MetaIniTextureCreated = "ReShade.IniTextureKeyCreated";
    private const string MetaIniEffectAdded = "ReShade.IniEffectPathsAdded";
    private const string MetaIniTextureAdded = "ReShade.IniTexturePathsAdded";
    private const string MetaShortFuseOriginalPayloadPath = ShortFuseDeploymentService.MetaReShadeOriginalPath;

    private readonly HttpClient _http;
    private readonly IFileDeploymentService _files;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly IReShadeArchiveExtractor _extractor;
    private readonly ReShadeShaderPackService _shaderPacks;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;
    private readonly DllNamePreferenceStore _dllNames;
    private readonly UpstreamRhiManifestService? _upstreamManifest;

    public ReShadeDeploymentService(
        HttpClient http,
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        IReShadeArchiveExtractor extractor,
        string? cacheDir = null,
        ReShadeShaderPackService? shaderPacks = null,
        UpstreamRhiManifestService? upstreamManifest = null,
        DeploymentJournal? journal = null,
        DllNamePreferenceStore? dllNames = null)
    {
        _journal = journal;
        _upstreamManifest = upstreamManifest;
        _dllNames = dllNames ?? new DllNamePreferenceStore();
        _http = http;
        _files = files;
        _records = records;
        _overrides = overrides;
        _extractor = extractor;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "reshade");
        _shaderPacks = shaderPacks ?? new ReShadeShaderPackService(
            http,
            Path.Combine(_cacheDir, "shader-packs"),
            upstreamManifest);
    }

    public IReadOnlyList<ReShadeShaderPackInfo> AvailableShaderPacks => _shaderPacks.AvailablePacks;
    /// <summary>#194: stages every shader pack not in the cache yet (the CacheAllShaders setting; cache only).</summary>
    public Task<int> PrefetchShaderPacksAsync(CancellationToken cancellationToken = default) => _shaderPacks.PrefetchMissingAsync(cancellationToken);
    public string CustomShaderRoot => _shaderPacks.CustomRoot;
    public Task<IReadOnlyList<ReShadeShaderPackInfo>> RefreshShaderPackCatalogAsync(CancellationToken cancellationToken = default)
        => _shaderPacks.RefreshCatalogAsync(cancellationToken);
    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> ListShaderPackFilesAsync(string packId, CancellationToken cancellationToken = default)
        => _shaderPacks.ListPackFilesAsync(packId, cancellationToken);
    public void SetShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => _shaderPacks.SetExcludedFiles(packId, excluded);
    public bool IsManaged(DetectedGame game) => _records.Find(game, ManagedComponentKind.ReShade) is not null;

    /// <summary>
    /// The add-on files the ReShade Addon Manager installed on a game (#170: switching to No Addons turns them off with
    /// the component add-ons); set by <see cref="LinuxManagedComponentService"/>, none when unset. Null from the function =
    /// the manager's saved list cannot be read: its add-ons are left as they are and the result says so.
    /// </summary>
    public Func<DetectedGame, IReadOnlyList<string>?>? AddonManagerFiles { get; set; }

    /// <summary>The per-game proxy DLL names chosen before install (#120); "Repair after move" carries them.</summary>
    public DllNamePreferenceStore DllNames => _dllNames;
    /// <summary>Template + Settings merged into ReShade.ini on install (#181); the flag is true for the Vulkan layer (#92: Vulkan template, depth defines kept).</summary>
    public Func<DetectedGame, bool, ReShadeIniDefaults?>? IniDefaults { get; set; } // w5/ini-presets hook (#181), vulkan flag w5 integration

    /// <summary>The proxy name the next install of ReShade on <paramref name="game"/> uses, and why (#120).</summary>
    public ProxyNameResolution? ResolveProxyName(DetectedGame game) => ProxyPayloadLocator.Resolve(game, _dllNames.GetReShade(game));

    /// <summary>Fixed text before the path when ReShade not installed by RHI already sits in the game folder under another
    /// name (PT-BR presentation pair).</summary>
    public const string OtherReShadePresentPrefix =
        "ReShade is already in this game folder, but RHI Linux did not install it; a second ReShade would load twice, so nothing was installed. Remove that ReShade first, or keep using it: ";

    /// <summary>Fixed text before "name (component)" when another RHI component already sits under the DLL name ReShade
    /// would use (PT-BR presentation pair).</summary>
    public const string NameHeldByRhiComponentPrefix =
        "Another component RHI installed on this game already uses this DLL name, so ReShade cannot be installed under it; choose another DLL name for ReShade. Name and component: ";

    private static IReadOnlySet<string> OwnedPaths(LinuxDeploymentRecord? record)
        => (record?.Files ?? []).Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Fixed text before the name when a chosen DLL name does not suit the game's bitness (PT-BR presentation pair).</summary>
    public const string NameNotForBitnessPrefix = "The chosen ReShade DLL name is for the other bitness of Windows game; choose another name: ";

    public ReShadeInstallOptions GetInstalledOptions(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return new ReShadeInstallOptions();
        var channel = ParseChannel(record.Metadata.GetValueOrDefault(MetaChannel));
        return new ReShadeInstallOptions
        {
            Channel = channel,
            LegacyVersion = record.Metadata.GetValueOrDefault(MetaLegacyVersion),
            CustomDllPath = record.Metadata.GetValueOrDefault(MetaCustomSource),
            ManageIni = record.Files.Any(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)),
            D3DCompiler47Path = record.Metadata.GetValueOrDefault(MetaD3DCompilerSource),
            ShaderPackIds = ReadStringList(record.Metadata.GetValueOrDefault(MetaShaderPacks)),
            VulkanLayer = ReShadeVulkanLayer.IsLayerRecord(record)
        };
    }

    public Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallAsync(game, DefaultOptionsFor(game), cancellationToken);

    /// <summary>
    /// The options of an install nobody chose a profile for (the ReShade row, a component that needs ReShade): Stable,
    /// or the RHI manifest's legacy version for the game (#186, upstream <c>legacyReShadeVersions</c>: Max Payne 3 → 6.4.1).
    /// An installed ReShade keeps its own profile on update (<see cref="GetInstalledOptions"/>).
    /// </summary>
    public static ReShadeInstallOptions DefaultOptionsFor(DetectedGame game)
        => UpstreamRhiManifestService.IsReShadeVersion(game.ManifestLegacyReShadeVersion)
            ? new ReShadeInstallOptions { Channel = ReShadeChannel.Legacy, LegacyVersion = game.ManifestLegacyReShadeVersion!.Trim() }
            : new ReShadeInstallOptions();

    /// <summary>
    /// The versions the Legacy channel offers (#186): the RHI manifest's <c>legacyReShadeAvailable</c>, else
    /// <see cref="UpstreamRhiManifestService.DefaultLegacyReShadeVersion"/> alone (offline, or a manifest without the key).
    /// </summary>
    public async Task<IReadOnlyList<string>> LegacyVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_upstreamManifest is null) return [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];
        try
        {
            var listed = await _upstreamManifest.FetchLegacyReShadeAvailableAsync(cancellationToken).ConfigureAwait(false);
            return listed.Count > 0 ? listed : [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("reshade", "The legacy ReShade version list could not be read from the RHI manifest; offering the default", ex);
            return [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];
        }
    }

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        ReShadeInstallOptions options,
        CancellationToken cancellationToken = default)
        => InstallCoreAsync(game, options, isUpdate: false, cancellationToken);

    /// <summary>
    /// Update/refresh path (Update All, managed update): re-applies the installed profile. During a
    /// reshade.me outage it never reports success by re-applying a cached copy that is not newer than
    /// the installed version; it fails with <see cref="ComponentOperationResult.SourceUnavailable"/>
    /// before touching any file. An explicit profile Apply (<see cref="InstallAsync(DetectedGame, ReShadeInstallOptions, CancellationToken)"/>)
    /// may still re-use the cached copy, because it applies the user's changed settings and says so.
    /// </summary>
    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallCoreAsync(game, GetInstalledOptions(game), isUpdate: true, cancellationToken);

    private async Task<ComponentOperationResult> InstallCoreAsync(
        DetectedGame game,
        ReShadeInstallOptions options,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ReShade DLL deployment is only for Windows games running through Wine/Proton.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");

        var architecture = GameArchitecture.Of(game);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("Could not determine whether the game executable is 32-bit or 64-bit.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);
        options = _shaderPacks.WithEffectiveSelection(game, options); // w7/shader-sets hook (#179): the game's effective shader set

        // #92: the Vulkan layer on request, and an installed layer stays one (Update All never switches the path).
        var installedRecord = _records.Find(game, ManagedComponentKind.ReShade);
        if (options.VulkanLayer || ReShadeVulkanLayer.IsLayerRecord(installedRecord))
            return await InstallVulkanLayerAsync(game, options, installedRecord, architecture, isUpdate, cancellationToken).ConfigureAwait(false);

        // The proxy name: the user's choice, else the RHI manifest's, else the API's (#120). An installed ReShade keeps
        // the name it was installed under (the record decides below); a new name needs a removal first.
        var resolution = ResolveProxyName(game);
        if (resolution is null)
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.IsVulkanTarget(game)
                ? ReShadeVulkanLayer.NotOnRequestMessage
                : "This graphics API does not yet have a safe game-local ReShade proxy mapping in RHI Linux.");
        if (!ProxyDllNames.OfferedFor(architecture == PeArchitecture.X86).Contains(resolution.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (resolution.Source == ProxyNameSource.User)
                return ComponentOperationResult.Fail(NameNotForBitnessPrefix + resolution.Name);
            resolution = new ProxyNameResolution(SelectProxyName(game.EffectiveGraphicsApi)!, ProxyNameSource.GraphicsApi, resolution.Name);
        }
        var directProxy = resolution.Name;

        // Switching to No Addons with add-ons installed is allowed (owner decision #91); its add-ons are turned off, and
        // back on when ReShade goes back to a build with add-on support (owner decision #170, ReShadeAddonSwitch below).

        var existingRecord = _records.Find(game, ManagedComponentKind.ReShade);
        var managedOptiScaler = _records.Find(game, ManagedComponentKind.OptiScaler);
        var directTarget = Path.Combine(game.BinaryDirectory, directProxy);
        var target = directTarget;
        var coexist = false;

        if (existingRecord is not null)
        {
            var payload = ProxyPayloadLocator.ReShadePayload(existingRecord);
            if (payload is null)
                return ComponentOperationResult.Fail("RHI has a ReShade record for this game, but its managed DLL path is missing. Update is blocked until the record is repaired or removed safely.");
            target = payload.Path;
            coexist = Path.GetFileName(target).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) && managedOptiScaler is not null;
        }
        else if (File.Exists(directTarget))
        {
            if (BinarySignatureInspector.LooksLikeReShade(directTarget))
                return ComponentOperationResult.Fail($"ReShade is already present at {directTarget}, but it was not installed by RHI Linux. Removal/update is locked to protect the external setup.");
            if (architecture == PeArchitecture.X64 && BinarySignatureInspector.LooksLikeOptiScaler(directTarget))
            {
                if (managedOptiScaler is null)
                    return ComponentOperationResult.Fail($"OptiScaler occupies {directTarget}, but it is not managed by RHI Linux. The external setup is protected; RHI will not edit its OptiScaler.ini automatically.");
                var managedProxy = managedOptiScaler.Files.FirstOrDefault(file =>
                    Path.GetFullPath(file.Path).Equals(Path.GetFullPath(directTarget), StringComparison.Ordinal));
                if (managedProxy is null)
                    return ComponentOperationResult.Fail($"An OptiScaler record exists, but it does not own {directTarget}. RHI treats the current proxy as external and will not build a ReShade chain on top of it.");
                if (!string.IsNullOrWhiteSpace(managedProxy.Sha256))
                {
                    var currentProxyHash = await FileHash.Sha256HexAsync(directTarget, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentProxyHash, managedProxy.Sha256, StringComparison.OrdinalIgnoreCase))
                        return ComponentOperationResult.Fail($"Managed OptiScaler proxy was modified outside RHI. ReShade coexistence is blocked: {directTarget}");
                }
                target = Path.Combine(game.BinaryDirectory, "ReShade64.dll");
                coexist = true;
                if (File.Exists(target))
                {
                    if (BinarySignatureInspector.LooksLikeReShade(target))
                        return ComponentOperationResult.Fail($"ReShade already exists at the OptiScaler coexistence target {target}, but it was not installed by RHI Linux. The external setup is protected.");
                    return ComponentOperationResult.Fail($"OptiScaler coexistence target is occupied by an unknown DLL: {target}");
                }
            }
            else if (_records.FindAll(game).FirstOrDefault(record => record.Component != ManagedComponentKind.ReShade &&
                         OwnedPaths(record).Contains(Path.GetFullPath(directTarget))) is { } holder)
            {
                // RHI's own component under that name (RE Framework's dinput8.dll on an RE Engine game): say which.
                return ComponentOperationResult.Fail(NameHeldByRhiComponentPrefix + $"{directProxy} ({LinuxManagedComponentService.DisplayDependencyName(holder.Component)})");
            }
            else
            {
                return ComponentOperationResult.Fail($"Foreign DLL protection: '{directProxy}' already exists and is not recognized as ReShade or OptiScaler. Nothing was overwritten.");
            }
        }

        // ReShade64.dll chosen as the name with RHI's OptiScaler here: nothing but OptiScaler's LoadReshade loads it, so
        // the install is the coexistence case (LoadReshade on, off again on removal), as an update of it already is.
        if (existingRecord is null && managedOptiScaler is not null && Path.GetFileName(target).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
            coexist = true;

        // One ReShade per game (#120): with no RHI ReShade here, a ReShade under any other name it can be installed as
        // (the user's own, or one another component left) means a second copy would load beside it. Refused before any
        // download or change, whichever path asked (profile Apply, Luma/ShortFuse dependency, a manifest name). The
        // target-name checks above ran first, so the target is absent by now; RHI's own OptiScaler files are not ReShade.
        if (existingRecord is null &&
            ProxyPayloadLocator.FindReShadeOnDisk(game.BinaryDirectory, OwnedPaths(managedOptiScaler)) is { } other)
            return ComponentOperationResult.Fail(OtherReShadePresentPrefix + other);

        D3DCompilerPlan? d3dCompilerPlan = null;
        if (options.ShaderPackIds.Count > 0 || !string.IsNullOrWhiteSpace(options.D3DCompiler47Path))
        {
            try
            {
                d3dCompilerPlan = BuildD3DCompilerPlan(game, architecture, options, existingRecord);
            }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"ReShade shader compiler compatibility check failed: {ex.Message}");
            }
        }

        ReShadeStagedDll staged;
        try
        {
            staged = await ResolveStagedAsync(options, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (ReShadeSiteUnavailableException ex)
        {
            return ComponentOperationResult.Unavailable($"ReShade source preparation failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ReShade source preparation failed: {ex.Message}");
        }
        if (staged.FromCache && existingRecord is not null)
        {
            // Outage fallback: an update must bring something newer; re-applying the same cached copy is not an update.
            var order = CompareNumericVersions(staged.Version, existingRecord.Version);
            if (isUpdate && order is not > 0)
                return ComponentOperationResult.Unavailable($"ReShade server unavailable: {staged.FallbackReason} Installed version kept ({existingRecord.Version}); nothing was changed; try again later.");
            if (order < 0)
                return ComponentOperationResult.Unavailable($"ReShade server unavailable: {staged.FallbackReason} The cached copy ({staged.Version}) is older than the installed version ({existingRecord.Version}); nothing was changed; try again later.");
        }

        // The shader packs (and core headers) are downloaded and staged with the ReShade payload, before the gate, and
        // copied into the game after it (w2 merge review, minor 1: they used to download after ReShade was copied).
        ReShadeShaderPackService.PreparedShaderSync preparedShaders;
        try
        {
            preparedShaders = await _shaderPacks.PrepareSyncAsync(game.BinaryDirectory, options.ShaderPackIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ReShade install/update failed: {ex.Message}");
        }

        // Last point Cancel is honoured: from the update snapshot on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

        var previousPayload = ProxyPayloadLocator.ReShadePayload(existingRecord);
        var existedBefore = previousPayload?.ExistedBeforeInstall ?? File.Exists(target);
        var previousOverride = existingRecord?.WineOverride;
        var previousAdditionalOverrides = existingRecord?.AdditionalWineOverrides ?? new List<WineOverrideRecord>();
        var previousCompilerOverride = previousAdditionalOverrides.FirstOrDefault(IsD3DCompilerOverride);
        var previousCompilerFile = existingRecord?.Files.FirstOrDefault(file => IsD3DCompilerPath(file.Path));
        WineOverrideRecord? appliedOverride = previousOverride;
        WineOverrideRecord? appliedCompilerOverride = previousCompilerOverride;
        var overrideCreatedThisAttempt = false;
        var compilerOverrideCreatedThisAttempt = false;
        var compilerFileCreatedThisAttempt = false;
        var fileReplaced = false;
        string? updateSnapshot = null;
        var oldOptions = existingRecord is null ? null : GetInstalledOptions(game);
        if (existingRecord is not null && previousPayload is not null)
        {
            var shortFuseActive = existingRecord.Metadata.TryGetValue(MetaShortFuseOriginalPayloadPath, out var sfOriginalPath)
                                  && !string.IsNullOrWhiteSpace(sfOriginalPath)
                                  && Path.GetFileName(target).Equals(ShortFuseDeploymentService.ReShadeAsiName, StringComparison.OrdinalIgnoreCase);
            var backupBasePath = shortFuseActive ? sfOriginalPath! : target;
            var originalBackup = backupBasePath + TransactionalFileDeploymentService.BackupExtension;
            if (File.Exists(target) && !string.IsNullOrWhiteSpace(previousPayload.Sha256))
            {
                var currentManagedHash = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentManagedHash, previousPayload.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed ReShade DLL was modified outside RHI; update is blocked to protect the current file: {target}");
            }
            if (previousPayload.ExistedBeforeInstall && !File.Exists(originalBackup))
                return ComponentOperationResult.Fail($"The original backup for managed ReShade is missing, so update is blocked rather than replacing the only recoverable state: {originalBackup}");
            if (!previousPayload.ExistedBeforeInstall && File.Exists(originalBackup))
                return ComponentOperationResult.Fail($"An unexpected .original file appeared beside an RHI-created ReShade proxy. Update is blocked to avoid adopting ambiguous backup state: {originalBackup}");

            // With a journal the operation tracks the target (verified copy) and its rollback puts it back (#169); the
            // hand-made snapshot is only for an unjournaled run (tests, tools), a verified copy too.
            if (File.Exists(target) && _journal is null)
            {
                updateSnapshot = target + $".rhi-update-snapshot-{Guid.NewGuid():N}";
                AtomicFile.Copy(target, updateSnapshot, overwrite: false);
            }
        }

        if (previousCompilerFile is not null)
        {
            if (!File.Exists(previousCompilerFile.Path))
                return ComponentOperationResult.Fail($"The RHI-managed D3DCompiler_47.dll is missing, so ReShade update is blocked until the compiler state is repaired or ReShade is removed safely: {previousCompilerFile.Path}");
            if (!string.IsNullOrWhiteSpace(previousCompilerFile.Sha256))
            {
                var currentCompilerHash = await FileHash.Sha256HexAsync(previousCompilerFile.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentCompilerHash, previousCompilerFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed D3DCompiler_47.dll was modified outside RHI; update is blocked to protect the current file: {previousCompilerFile.Path}");
            }
            if (!LooksLikeNativeD3DCompiler(previousCompilerFile.Path, architecture))
                return ComponentOperationResult.Fail($"The RHI-managed D3DCompiler_47.dll no longer looks like a native Microsoft shader compiler: {previousCompilerFile.Path}");
        }

        // An unreadable shader-pack manifest would make Cleanup throw after the INI was already cleaned.
        // Checked before the journal is opened, so a refusal leaves no operation behind.
        if (existingRecord is not null)
        {
            try { ReShadeShaderPackService.EnsureManifestReadable(game.BinaryDirectory); }
            catch (JsonStoreUnavailableException ex)
            {
                if (updateSnapshot is not null) FileCleanup.TryDelete(updateSnapshot);
                return ComponentOperationResult.Fail(ex.Message);
            }
        }

        // #170: the add-ons this channel switch turns off (No Addons) or back on; a taken name refuses before any change.
        IReadOnlyList<string>? managerFiles = AddonManagerFiles is null ? [] : AddonManagerFiles(game);
        var (addonPlan, addonRefusal) = ReShadeAddonSwitch.PlanFor(
            _records.FindAll(game), managerFiles ?? [], addonSupport: staged.Channel != ReShadeChannel.NoAddons);
        // Unreadable manager list while the switch matters (to No Addons, or back from it): its add-ons cannot be told
        // apart, so they stay as they are and the status line says so. A fresh install with add-on support (ReShade was
        // removed while No Addons had them off) counts as "back from it" while add-on files that are off remain.
        var managerNote = managerFiles is null
                          && (staged.Channel == ReShadeChannel.NoAddons
                              || LacksAddonSupport(existingRecord)
                              || (existingRecord is null && ReShadeAddonSwitch.HasOffAddons(game.BinaryDirectory)))
            ? ReShadeAddonSwitch.ManagerUnreadableNote : "";
        if (addonRefusal is not null)
        {
            if (updateSnapshot is not null) FileCleanup.TryDelete(updateSnapshot);
            return ComponentOperationResult.Fail(addonRefusal);
        }
        var addonMoves = new List<ReShadeAddonSwitch.Move>();

        // Write-ahead journal (#23): every file this install can change is tracked and every Wine
        // override is journaled before it is made, so a crash before the record is saved is rolled
        // back on the next start instead of leaving an unowned proxy and override behind.
        DeploymentOperation? journaled = null;
        if (_journal is not null)
        {
            try
            {
                journaled = await _journal.BeginAsync(
                    $"ReShade {(isUpdate ? "update" : "install")}: {game.Name}",
                    LinuxDeploymentRecovery.RecordCommitProbe(game, ManagedComponentKind.ReShade),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (updateSnapshot is not null) FileCleanup.TryDelete(updateSnapshot);
                return ComponentOperationResult.Fail($"ReShade install/update failed: the deployment journal could not be written ({ex.Message}). Nothing was changed.");
            }
        }
        await using var operation = journaled;
        string[] trackedInis = [];
        // Crash recovery restores a tracked file only while it holds what this install wrote, so every
        // write is declared: DLL payloads by hash before they are placed, in-place INI edits right after.
        async Task DeclareIniWritesAsync()
        {
            if (operation is null) return;
            foreach (var ini in trackedInis)
                await operation.RecordWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            trackedInis = IniPathsTouchedByInstall(game, existingRecord, coexist).ToArray();
            if (operation is not null)
            {
                await operation.TrackFileAsync(target, cancellationToken, includeBackup: false).ConfigureAwait(false);
                if (d3dCompilerPlan is not null)
                    await operation.TrackFileAsync(d3dCompilerPlan.TargetPath, cancellationToken, includeBackup: false).ConfigureAwait(false);
                foreach (var ini in trackedInis)
                    await operation.TrackFileAsync(ini, cancellationToken, includeBackup: false).ConfigureAwait(false);
            }

            // Remove only RHI-owned shader/INI entries from the previous profile before applying a new profile.
            if (existingRecord is not null)
            {
                CleanupManagedIni(existingRecord, game.InstallPath); // w5/ini-presets hook (review 8)
                await DeclareIniWritesAsync().ConfigureAwait(false);
                await _shaderPacks.CleanupAsync(game.BinaryDirectory, operation).ConfigureAwait(false); // #169: tracked
            }

            // For an update of an RHI-created proxy there is intentionally no long-lived .original.
            // Remove the current managed payload after snapshotting it so ReplaceAsync does not mistake
            // that managed DLL for a pre-RHI original and create a bogus .original backup.
            var shortFuseUpdate = existingRecord is not null
                                  && existingRecord.Metadata.ContainsKey(MetaShortFuseOriginalPayloadPath)
                                  && Path.GetFileName(target).Equals(ShortFuseDeploymentService.ReShadeAsiName, StringComparison.OrdinalIgnoreCase);
            if (existingRecord is not null && previousPayload is not null &&
                (!previousPayload.ExistedBeforeInstall || shortFuseUpdate) && File.Exists(target))
                File.Delete(target);

            if (operation is not null)
                await operation.RecordWriteAsync(target, await FileHash.Sha256HexAsync(staged.DllPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var result = await _files.ReplaceAsync(staged.DllPath, target, cancellationToken).ConfigureAwait(false);
            if (!result.Success) throw new IOException(result.Error ?? "Failed to deploy ReShade DLL.");
            fileReplaced = true;

            WineOverrideRecord? overrideRecord = previousOverride;
            if (!coexist)
            {
                // The override is for the name ReShade sits under (#120); ReShade64/32.dll chosen as a name are loaded by
                // another mod, never by Wine, so they get none. ShortFuse's .asi keeps the API name as before.
                var targetName = Path.GetFileName(target);
                if (ProxyDllNames.NeedsWineOverride(targetName))
                {
                    if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
                        throw new InvalidOperationException("Wine/Proton prefix is not initialized; start the game once before installing ReShade.");
                    if (overrideRecord is null)
                    {
                        // ShortFuse's .asi is loaded by its ASI loader; the override stays with the name ReShade had before.
                        var overrideName = !targetName.Equals(ProxyDllNames.ShortFuseAsiName, StringComparison.OrdinalIgnoreCase) ? targetName
                            : existingRecord?.Metadata.GetValueOrDefault(MetaShortFuseOriginalPayloadPath) is { Length: > 0 } original ? Path.GetFileName(original)
                            : directProxy;
                        overrideRecord = await _overrides.EnsureNativeBuiltinAsync(
                            operation, prefix, overrideName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                        appliedOverride = overrideRecord;
                        overrideCreatedThisAttempt = true;
                    }
                }
            }
            else
            {
                OptiScalerIniEditor.SetLoadReShade(game.BinaryDirectory, true);
                await DeclareIniWritesAsync().ConfigureAwait(false);
                await RefreshManagedOptiScalerIniHashAsync(game, cancellationToken).ConfigureAwait(false);
            }

            LinuxDeployedFile? compilerFileRecord = previousCompilerFile;
            var additionalOverrides = previousAdditionalOverrides
                .Where(record => !IsD3DCompilerOverride(record))
                .ToList();

            // ReShade 6.5+ intentionally avoids Wine's incomplete built-in D3DCompiler implementation.
            // When RHI shader packs are selected, make the native Microsoft compiler deterministic.
            if (d3dCompilerPlan is not null)
            {
                if (game.PrefixPath is not { } compilerPrefix || !Directory.Exists(compilerPrefix))
                    throw new InvalidOperationException("Wine/Proton prefix is not initialized; start the game once before enabling ReShade shader packs.");

                if (d3dCompilerPlan.DeploySourcePath is not null)
                {
                    if (operation is not null)
                        await operation.RecordWriteAsync(d3dCompilerPlan.TargetPath,
                            await FileHash.Sha256HexAsync(d3dCompilerPlan.DeploySourcePath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    var compilerDeploy = await _files.ReplaceAsync(
                        d3dCompilerPlan.DeploySourcePath,
                        d3dCompilerPlan.TargetPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!compilerDeploy.Success)
                        throw new IOException(compilerDeploy.Error ?? "Failed to deploy native D3DCompiler_47.dll.");
                    compilerFileCreatedThisAttempt = previousCompilerFile is null;
                    compilerFileRecord = new LinuxDeployedFile
                    {
                        Path = d3dCompilerPlan.TargetPath,
                        ExistedBeforeInstall = d3dCompilerPlan.TargetExistedBefore,
                        Sha256 = compilerDeploy.Sha256 ?? await FileHash.Sha256HexAsync(d3dCompilerPlan.TargetPath, cancellationToken).ConfigureAwait(false)
                    };
                }

                if (appliedCompilerOverride is null)
                {
                    appliedCompilerOverride = await _overrides.EnsureNativeBuiltinAsync(
                        operation, compilerPrefix, D3DCompilerOverrideName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                    compilerOverrideCreatedThisAttempt = true;
                }
                additionalOverrides.Add(appliedCompilerOverride);
            }
            else if (previousCompilerOverride is not null)
            {
                // Once enabled, retain the compiler compatibility override for the lifetime of this
                // ReShade installation. It is restored exactly on ReShade removal.
                additionalOverrides.Add(previousCompilerOverride);
            }

            var shaderResult = await _shaderPacks.ApplySyncAsync(preparedShaders, operation, cancellationToken).ConfigureAwait(false); // #169: journaled
            ReShadeIniApplyResult? iniResult = null;
            if (options.ManageIni)
                iniResult = ReShadeIniManager.Apply(game.BinaryDirectory, shaderResult.EffectSearchPaths, shaderResult.TextureSearchPaths, IniDefaults?.Invoke(game, false), game.InstallPath); // w5/ini-presets hook (#181, review 8)
            await DeclareIniWritesAsync().ConfigureAwait(false);

            var files = new List<LinuxDeployedFile>
            {
                new()
                {
                    Path = target,
                    ExistedBeforeInstall = existedBefore,
                    // The file service returns the hash of what it placed (#33); no second read of the target.
                    Sha256 = result.Sha256 ?? await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false)
                }
            };
            if (compilerFileRecord is not null)
                files.Add(compilerFileRecord);
            if (iniResult is not null)
            {
                files.Add(new LinuxDeployedFile
                {
                    Path = iniResult.Path,
                    ExistedBeforeInstall = iniResult.ExistedBefore,
                    Sha256 = iniResult.Sha256,
                    AllowUserChanges = true
                });
            }

            var metadata = existingRecord?.Metadata is { } oldMetadata
                ? new Dictionary<string, string>(oldMetadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata[MetaChannel] = staged.Channel.ToString();
            metadata[MetaSourceIdentity] = staged.SourceIdentity;
            if (staged.Download is { } download) download.RecordIn(metadata);
            else VerifiedDownload.Forget(metadata);
            metadata[MetaShaderPacks] = JsonSerializer.Serialize(shaderResult.PackIds);
            SetOrRemove(metadata, MetaCustomSource, options.Channel == ReShadeChannel.Custom ? staged.SourceUrl : null);
            SetOrRemove(metadata, MetaLegacyVersion, options.Channel == ReShadeChannel.Legacy ? options.LegacyVersion?.Trim() : null);
            if (d3dCompilerPlan is not null)
                SetOrRemove(metadata, MetaD3DCompilerSource, d3dCompilerPlan.UserSourcePath);
            WriteIniMetadata(metadata, iniResult);
            SetOrRemove(metadata, LinuxDeploymentRecovery.OperationIdMetadataKey, operation?.Id);

            var record = new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.ReShade,
                BinaryDirectory = game.BinaryDirectory,
                SourceUrl = staged.SourceUrl,
                Version = staged.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = overrideRecord,
                AdditionalWineOverrides = additionalOverrides,
                Files = files,
                Metadata = metadata
            };
            // #170: add-on files renamed last (journaled), their records saved in the same ledger write as ReShade's.
            await ReShadeAddonSwitch.ApplyAsync(addonPlan, operation, addonMoves).ConfigureAwait(false);
            _records.Commit([record, .. addonPlan.Records]);
            // The record carries the operation id, so it is the commit point even if this cleanup fails.
            if (operation is not null) await operation.CommitAsync().ConfigureAwait(false);
            try { if (updateSnapshot is not null && File.Exists(updateSnapshot)) File.Delete(updateSnapshot); } catch { }

            var mode = Path.GetFileName(target).Equals(ShortFuseDeploymentService.ReShadeAsiName, StringComparison.OrdinalIgnoreCase)
                ? "ReShade64.asi (ShortFuse / UAL)"
                : coexist ? "ReShade64.dll (OptiScaler coexistence)" : Path.GetFileName(target);
            var packs = shaderResult.PackIds.Count == 0 ? "no RHI shader packs" : $"shader packs: {string.Join(", ", shaderResult.PackIds)}";
            var compilerNote = d3dCompilerPlan is null
                ? ""
                : $" Native D3DCompiler_47 is active ({d3dCompilerPlan.Description}) with a per-game native,builtin override.";
            var cacheNote = staged.FromCache ? CachedCopyNote : "";
            return ComponentOperationResult.Ok($"ReShade {staged.Version} [{staged.Channel}] applied as {mode}; {packs}. Existing ReShade.ini customizations are preserved.{compilerNote}{cacheNote}{ReShadeAddonSwitch.Summary(addonPlan)}{managerNote}", target)
                with { FromCachedSource = staged.FromCache };
        }
        catch (Exception ex)
        {
            ReShadeAddonSwitch.Undo(addonMoves); // #170: add-ons back under their names before anything else
            // Best-effort rollback. On an update, the long-lived .original is the real pre-RHI game file,
            // so do not restore it over the previous managed ReShade. Instead the update is blocked before
            // destructive operations where possible; for a fresh install, restore/remove normally.
            if (existingRecord is null && fileReplaced)
                await RollbackFreshFileAsync(target, existedBefore).ConfigureAwait(false);
            if (existingRecord is null && overrideCreatedThisAttempt && game.PrefixPath is { } prefix && Directory.Exists(prefix))
                _overrides.TryRestore(prefix, appliedOverride);
            if (compilerOverrideCreatedThisAttempt && game.PrefixPath is { } compilerPrefix && Directory.Exists(compilerPrefix))
                _overrides.TryRestore(compilerPrefix, appliedCompilerOverride);
            if (compilerFileCreatedThisAttempt && d3dCompilerPlan is not null)
            {
                try
                {
                    if (d3dCompilerPlan.TargetExistedBefore && File.Exists(d3dCompilerPlan.TargetPath + TransactionalFileDeploymentService.BackupExtension))
                        await _files.RestoreAsync(d3dCompilerPlan.TargetPath, CancellationToken.None).ConfigureAwait(false);
                    else
                    {
                        if (File.Exists(d3dCompilerPlan.TargetPath)) File.Delete(d3dCompilerPlan.TargetPath);
                        var backup = d3dCompilerPlan.TargetPath + TransactionalFileDeploymentService.BackupExtension;
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                }
                catch { }
            }
            if (existingRecord is not null && updateSnapshot is not null && File.Exists(updateSnapshot))
            {
                try { AtomicFile.Copy(updateSnapshot, target); }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    AppLog.Warn("reshade", $"Could not put the previous ReShade back at {target}; its copy is kept at {updateSnapshot}", restoreError);
                    updateSnapshot = null; // keep the only copy
                }
            }
            try { if (updateSnapshot is not null && File.Exists(updateSnapshot)) File.Delete(updateSnapshot); } catch { }

            // If this was an update and profile application failed, keep the record conservative and try
            // to restore its previous INI/shader selection from cached sources.
            // Journaled, the operation's rollback (when it is disposed) puts the shader folder and ReShade.ini back (#169).
            if (existingRecord is not null && oldOptions is not null && operation is null)
            {
                try
                {
                    var oldShaders = await _shaderPacks.SyncAsync(game.BinaryDirectory, oldOptions.ShaderPackIds, CancellationToken.None).ConfigureAwait(false);
                    if (oldOptions.ManageIni)
                        _ = ReShadeIniManager.Apply(game.BinaryDirectory, oldShaders.EffectSearchPaths, oldShaders.TextureSearchPaths, gameRoot: game.InstallPath); // w5/ini-presets hook (review 8)
                }
                catch { }
            }
            return ComponentOperationResult.Fail($"ReShade install/update failed: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null)
            return ComponentOperationResult.Fail("ReShade was detected, but it is not managed by RHI Linux. Removal is locked to protect the external installation.");

        var binaryDirectory = !string.IsNullOrWhiteSpace(game.BinaryDirectory)
            ? game.BinaryDirectory
            : record.BinaryDirectory;
        if (string.IsNullOrWhiteSpace(binaryDirectory))
            return ComponentOperationResult.Fail("The managed ReShade installation has no recorded game directory, so removal was stopped safely.");

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var managedOptiScaler = _records.Find(game, ManagedComponentKind.OptiScaler);
        var coexist = managedOptiScaler is not null && !ReShadeVulkanLayer.IsLayerRecord(record) &&
            record.Files.Any(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));

        try
        {
            foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
            {
                if (!File.Exists(file.Path)) continue;
                if (file.ExistedBeforeInstall && File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension)) continue;
                var currentHash = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
            }
        }
        catch (Exception ex)
        {
            // Nothing changed yet (a Cancel or an unreadable file during the check).
            return ComponentOperationResult.Fail($"ReShade removal failed: {ex.Message}");
        }

        // Journaled (#23, #31; the record-removed probe): ReShade's record is stamped with the removal's id first; every
        // file the teardown touches (proxy, compiler, ReShade.ini, OptiScaler.ini's LoadReshade) is tracked with a
        // verified copy, and every override restore journals its put-back. The commit point is one ledger write that takes
        // ReShade's record away and, in the OptiScaler chain, saves OptiScaler's record with its edited INI. A failure, or
        // a crash before that write, puts every file and override back. Shader-pack folders are not journaled yet (#169).
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, $"ReShade removal: {game.Name}", game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var overrideRestores = new List<(WineOverrideRecord Record, WineOverrideRestoreResult Result)>();
        var valueRestores = new List<(WineRegistryValueRecord Record, WineOverrideRestoreResult Result)>();
        LinuxDeploymentRecord? optiScalerBefore = null;
        LinuxDeploymentRecord? optiScalerAfter = null;

        // Cancel is honoured only up to here: a removal that stopped half-way would be refused on retry by the hash check
        // above (restored originals no longer match RHI's hash), so it always runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            if (coexist)
            {
                // A removal is never blocked by the user's lock on OptiScaler.ini: written, kept read-only.
                var optiIni = Path.Combine(binaryDirectory, "OptiScaler.ini");
                await scope.TrackAsync(optiIni, cancellationToken).ConfigureAwait(false);
                OptiScalerIniEditor.SetLoadReShade(binaryDirectory, false, ReadOnlyUserFile.WriteKeepingLock);
                await scope.DeclareWrittenContentAsync(optiIni, cancellationToken).ConfigureAwait(false);
                optiScalerBefore = _records.Find(game, ManagedComponentKind.OptiScaler);
                optiScalerAfter = await ManagedOptiScalerWithIniHashAsync(game).ConfigureAwait(false);
            }

            await _shaderPacks.CleanupAsync(binaryDirectory, scope.Operation).ConfigureAwait(false); // #169: tracked
            if (record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)) is { } managedIni)
            {
                await scope.TrackAsync(managedIni.Path, cancellationToken).ConfigureAwait(false);
                CleanupManagedIni(record, game.InstallPath); // w5/ini-presets hook (review 8)
                await scope.DeclareWrittenContentAsync(managedIni.Path, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
            {
                await scope.TrackAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (file.ExistedBeforeInstall && File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                {
                    await scope.DeclareWriteAsync(file.Path, scope.Files.Find(file.Path)!.BackupPreSha256
                        ?? await FileHash.Sha256HexAsync(file.Path + TransactionalFileDeploymentService.BackupExtension, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success) throw new IOException(restored.Error ?? $"Could not restore {file.Path}");
                }
                else if (File.Exists(file.Path))
                {
                    AtomicFile.Delete(file.Path);
                }
            }

            if (game.PrefixPath is { } prefix && Directory.Exists(prefix))
            {
                foreach (var additionalOverride in record.AdditionalWineOverrides.AsEnumerable().Reverse())
                    overrideRestores.Add((additionalOverride, await _overrides.RestoreAsync(scope.Operation, prefix, additionalOverride, cancellationToken).ConfigureAwait(false)));
                if (record.WineOverride is { } main)
                    overrideRestores.Add((main, await _overrides.RestoreAsync(scope.Operation, prefix, main, cancellationToken).ConfigureAwait(false)));
                // #92: the Vulkan layer's ImplicitLayers registration (system.reg), only while it still holds RHI's value.
                foreach (var value in record.RegistryValues)
                    valueRestores.Add((value, await RegistryValues.RestoreAsync(scope.Operation, prefix, value, cancellationToken).ConfigureAwait(false)));
            }
            // The commit point: ReShade's record goes and, in the chain, OptiScaler's (INI hash) is saved in the same write.
            _records.Commit(optiScalerAfter is null ? [] : [optiScalerAfter], [(game, ManagedComponentKind.ReShade)]);
        }
        catch (Exception ex)
        {
            // ReShade's record goes back (still stamped) before the files roll back. In the chain the commit write also
            // saved OptiScaler's record with the LoadReshade=false INI hash: when that write landed and then failed, its
            // record goes back as it was before, beside ReShade's, so it matches the INI the rollback restores.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "reshade",
                alongside: optiScalerAfter is not null && optiScalerBefore is not null ? [optiScalerBefore] : null).ConfigureAwait(false);
            foreach (var (overrideRecord, result) in overrideRestores.AsEnumerable().Reverse())
                if (result.Outcome == WineOverrideRestoreOutcome.Restored)
                    _overrides.TryReapply(game.PrefixPath, overrideRecord);
            foreach (var (valueRecord, result) in valueRestores.AsEnumerable().Reverse())
                if (result.Outcome == WineOverrideRestoreOutcome.Restored)
                    RegistryValues.TryReapply(game.PrefixPath, valueRecord);
            if (!rollback.Success)
                AppLog.Warn("reshade", $"Removal rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"{RemovalFailedPrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        // #92: the layer's shared ProgramData\RHI-Linux\ReShadeVulkan folders go with the prefix's last layer (another
        // game's layer folder, or anything else in them, keeps them).
        RemoveEmptyDirectories(ReShadeVulkanLayer.IsLayerRecord(record) && !string.IsNullOrWhiteSpace(game.PrefixPath)
            ? [.. record.DirectoriesCreatedByRhi, .. ReShadeVulkanLayer.SharedFolders(game.PrefixPath)]
            : record.DirectoriesCreatedByRhi);
        if (ReShadeVulkanLayer.IsLayerRecord(record))
            return ComponentOperationResult.Ok(ReShadeVulkanLayer.RemovedMessage
                + WineDllOverrideService.KeptValueNotes([.. overrideRestores.Select(item => item.Result), .. valueRestores.Select(item => item.Result)]));
        return ComponentOperationResult.Ok((coexist
            ? "RHI-managed ReShade removed; OptiScaler LoadReshade was disabled. RHI shader files were cleaned and user-edited ReShade.ini content was preserved."
            : "RHI-managed ReShade removed; preserved foreign DLL/Wine override restored. RHI shader files were cleaned and user-edited ReShade.ini content was preserved.")
            + WineDllOverrideService.KeptValueNotes([.. overrideRestores.Select(item => item.Result)]));
    }

    /// <summary>Fixed text of a failed, rolled-back ReShade removal; the reason follows (PT-BR pair exists).</summary>
    public const string RemovalFailedPrefix = "ReShade removal failed and was rolled back; ReShade stays installed: ";

    /// <summary>
    /// OptiScaler's record with its <c>OptiScaler.ini</c> hash taken from disk (user-editable), for saving in the same
    /// write as the operation's commit point; null when RHI does not manage OptiScaler here or its INI is gone.
    /// </summary>
    private async Task<LinuxDeploymentRecord?> ManagedOptiScalerWithIniHashAsync(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.OptiScaler);
        var ini = record?.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        if (record is null || ini is null || !File.Exists(ini.Path)) return null;
        ini.Sha256 = await FileHash.Sha256HexAsync(ini.Path, CancellationToken.None).ConfigureAwait(false);
        ini.AllowUserChanges = true;
        return record;
    }

    public async Task<ReShadeUpdateStatus> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return new(false, false, "ReShade is not installed by RHI Linux.");
        var channel = ParseChannel(record.Metadata.GetValueOrDefault(MetaChannel));
        try
        {
            switch (channel)
            {
                case ReShadeChannel.Stable:
                {
                    var latest = await GetLatestStableAsync(cancellationToken).ConfigureAwait(false);
                    var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase);
                    return new(true, update, update ? $"Stable update available: {record.Version} → {latest.Version}." : $"Stable {record.Version} is current.", record.Version, latest.Version, channel);
                }
                case ReShadeChannel.NoAddons:
                {
                    var latest = await GetLatestStableAsync(cancellationToken, addonBuild: false).ConfigureAwait(false);
                    var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase);
                    return new(true, update, update
                        ? $"{NoAddonsUpdateAvailablePrefix}{record.Version} → {latest.Version}."
                        : $"{NoAddonsCurrentPrefix}{record.Version}.", record.Version, latest.Version, channel);
                }
                case ReShadeChannel.Nightly:
                {
                    var architecture = GameArchitecture.Of(game);
                    // Identity is the GitHub Actions run (and the artifact digest GitHub published for it), never the zip size (#27).
                    var artifact = await ResolveNightlyArtifactAsync(architecture == PeArchitecture.X86, cancellationToken).ConfigureAwait(false);
                    if (artifact is null) return new(true, false, NightlyLookupFailedMessage, record.Version, null, channel, CouldNotCheck: true);
                    var identity = NightlyIdentity(artifact);
                    var update = !string.Equals(record.Metadata.GetValueOrDefault(MetaSourceIdentity), identity, StringComparison.OrdinalIgnoreCase);
                    return new(true, update, update ? "A newer/different ReShade nightly artifact is available." : "ReShade nightly is current.", record.Version, "Nightly", channel);
                }
                case ReShadeChannel.Legacy:
                    return new(true, false, $"Legacy ReShade {record.Version} is pinned; RHI does not auto-upgrade legacy channels.", record.Version, record.Version, channel);
                case ReShadeChannel.Custom:
                {
                    var source = record.Metadata.GetValueOrDefault(MetaCustomSource);
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                        return new(true, false, "Custom ReShade source file is unavailable; installed DLL remains untouched.", record.Version, null, channel, CouldNotCheck: true);
                    var identity = "sha256:" + await FileHash.Sha256HexAsync(source, cancellationToken).ConfigureAwait(false);
                    var update = !string.Equals(record.Metadata.GetValueOrDefault(MetaSourceIdentity), identity, StringComparison.OrdinalIgnoreCase);
                    return new(true, update, update ? "The custom ReShade DLL changed since it was applied." : "Custom ReShade source matches the installed profile.", record.Version, update ? "custom changed" : record.Version, channel);
                }
                default:
                    return new(true, false, "Unknown ReShade channel; update is locked conservatively.", record.Version, null, channel);
            }
        }
        catch (ReShadeSiteUnavailableException ex)
        {
            return new(true, false, $"{ex.Message} Installed version kept ({record.Version}); RHI will check again later.", record.Version, null, channel, CouldNotCheck: true);
        }
        catch (Exception ex)
        {
            return new(true, false, $"ReShade update check failed: {ex.Message}", record.Version, null, channel, CouldNotCheck: true);
        }
    }

    /// <summary>Fixed texts of the No Addons update check (PT-BR presentation pairs).</summary>
    public const string NoAddonsUpdateAvailablePrefix = "ReShade No Addons update available: ";
    public const string NoAddonsCurrentPrefix = "ReShade No Addons is current: ";
    public const string NoAddonsLinkMissingMessage = "Could not find the current ReShade download without add-on support on reshade.me.";

    private D3DCompilerPlan BuildD3DCompilerPlan(
        DetectedGame game,
        PeArchitecture architecture,
        ReShadeInstallOptions options,
        LinuxDeploymentRecord? existingRecord)
    {
        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            throw new InvalidOperationException("Wine/Proton prefix is not initialized; start the game once before enabling ReShade shader packs.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || string.IsNullOrWhiteSpace(game.ExecutablePath))
            throw new InvalidOperationException("Game executable/binary directory is not available.");

        var target = Path.Combine(game.BinaryDirectory, D3DCompilerFileName);
        var existingManaged = existingRecord?.Files.FirstOrDefault(file => IsD3DCompilerPath(file.Path));
        if (existingManaged is not null)
        {
            if (!File.Exists(target))
                throw new InvalidOperationException($"RHI previously managed {D3DCompilerFileName}, but the file is missing: {target}");
            if (!LooksLikeNativeD3DCompiler(target, architecture))
                throw new InvalidOperationException($"RHI-managed {D3DCompilerFileName} is no longer a valid native Microsoft compiler: {target}");
            return new(target, null, false, "RHI-managed app-local compiler", existingRecord?.Metadata.GetValueOrDefault(MetaD3DCompilerSource));
        }

        var existingAppLocal = FindFileCaseInsensitive(game.BinaryDirectory, D3DCompilerFileName);
        if (existingAppLocal is not null)
        {
            if (!LooksLikeNativeD3DCompiler(existingAppLocal, architecture))
                throw new InvalidOperationException(
                    $"{Path.GetFileName(existingAppLocal)} already exists beside the game but is not recognized as a native Microsoft compiler. " +
                    "RHI will not overwrite an external DLL.");
            return new(existingAppLocal, null, true, "existing app-local Microsoft compiler", null);
        }

        var userSource = ExpandUserPath(options.D3DCompiler47Path);
        if (!string.IsNullOrWhiteSpace(userSource))
        {
            if (!LooksLikeNativeD3DCompiler(userSource, architecture))
                throw new InvalidOperationException(
                    $"The selected {D3DCompilerFileName} is missing, has the wrong architecture, or is not recognized as the native Microsoft HLSL compiler: {userSource}");
            return new(target, userSource, false, "RHI app-local compiler from selected Microsoft DLL", userSource);
        }

        foreach (var candidate in PrefixD3DCompilerCandidates(prefix, architecture))
        {
            if (LooksLikeNativeD3DCompiler(candidate, architecture))
                return new(target, null, false, $"native Microsoft compiler already present in prefix ({candidate})", null);
        }

        var appIdHint = game.SteamAppId.HasValue
            ? $" For this Steam game you can install it into the prefix with: protontricks {game.SteamAppId.Value} d3dcompiler_47"
            : " Install the native d3dcompiler_47 verb with winetricks for this prefix, or select a Microsoft D3DCompiler_47.dll in the ReShade profile.";
        throw new InvalidOperationException(
            $"RHI shader packs under Wine/Proton need a native Microsoft {D3DCompilerFileName}; Wine's built-in compiler is not sufficient for all ReShade effects." +
            appIdHint);
    }

    internal static bool LooksLikeNativeD3DCompiler(string path, PeArchitecture architecture)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 500_000) return false;
            if (!BinarySignatureInspector.HasPeSignature(path, 500_000)) return false;
            if (PeArchitectureDetector.Detect(path) != architecture) return false;
            // Wine's built-in carries a UTF-16 "Microsoft" and its own lower-case name, which the case-insensitive
            // search matches; its winebuild marker rules it out whatever its size.
            if (BinarySignatureInspector.IsWineBuiltinDll(path)) return false;

            // Native Windows SDK/system D3DCompiler binaries identify both Microsoft and the HLSL compiler.
            // Wine's built-in d3dcompiler does not carry these native product strings. One bounded streaming pass
            // that stops once both are seen, cached per file version (#34), instead of File.ReadAllBytes + 6 naive scans.
            var found = BinaryStringSearch.Find(path, NativeCompilerStrings, IsNativeCompilerEvidence);
            return IsNativeCompilerEvidence(found);
        }
        catch
        {
            return false;
        }
    }

    internal static string? FindNativeD3DCompilerInPrefix(string prefix, PeArchitecture architecture)
    {
        foreach (var candidate in PrefixD3DCompilerCandidates(prefix, architecture))
            if (LooksLikeNativeD3DCompiler(candidate, architecture))
                return candidate;
        return null;
    }

    internal static string? FindNativeD3DCompilerForGame(DetectedGame game, PeArchitecture architecture)
    {
        if (!string.IsNullOrWhiteSpace(game.BinaryDirectory) && Directory.Exists(game.BinaryDirectory))
        {
            var appLocal = FindFileCaseInsensitive(game.BinaryDirectory, D3DCompilerFileName);
            if (appLocal is not null && LooksLikeNativeD3DCompiler(appLocal, architecture))
                return appLocal;
        }

        if (!string.IsNullOrWhiteSpace(game.PrefixPath) && Directory.Exists(game.PrefixPath))
            return FindNativeD3DCompilerInPrefix(game.PrefixPath, architecture);

        return null;
    }

    private static IEnumerable<string> PrefixD3DCompilerCandidates(string prefix, PeArchitecture architecture)
    {
        var windows = Path.Combine(prefix, "drive_c", "windows");
        var directories = architecture == PeArchitecture.X86
            ? new[] { Path.Combine(windows, "syswow64"), Path.Combine(windows, "system32") }
            : new[] { Path.Combine(windows, "system32"), Path.Combine(windows, "syswow64") };

        foreach (var directory in directories)
        {
            var candidate = FindFileCaseInsensitive(directory, D3DCompilerFileName);
            if (candidate is not null)
                yield return candidate;
        }
    }

    internal static string? FindFileCaseInsensitive(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return null;

        var canonical = Path.Combine(directory, fileName);
        if (File.Exists(canonical)) return canonical;

        var lowercase = Path.Combine(directory, fileName.ToLowerInvariant());
        if (File.Exists(lowercase)) return lowercase;

        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExpandUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded == "~") expanded = home;
        else if (expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Path.Combine(home, expanded[2..]);
        return Path.GetFullPath(expanded);
    }

    /// <summary>"Microsoft" (0) plus "HLSL Shader Compiler" (1) or "D3DCompiler_47" (2) identify a native D3DCompiler.</summary>
    private static readonly string[] NativeCompilerStrings = ["Microsoft", "HLSL Shader Compiler", "D3DCompiler_47"];

    private static bool IsNativeCompilerEvidence(IReadOnlySet<int> found)
        => found.Contains(0) && (found.Contains(1) || found.Contains(2));

    private static bool IsD3DCompilerPath(string path)
        => Path.GetFileName(path).Equals(D3DCompilerFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsD3DCompilerOverride(WineOverrideRecord record)
        => string.Equals(Path.GetFileNameWithoutExtension(record.DllName), D3DCompilerOverrideName, StringComparison.OrdinalIgnoreCase);

    private sealed record D3DCompilerPlan(
        string TargetPath,
        string? DeploySourcePath,
        bool TargetExistedBefore,
        string Description,
        string? UserSourcePath);

    private async Task<ReShadeStagedDll> ResolveStagedAsync(ReShadeInstallOptions options, PeArchitecture architecture, CancellationToken cancellationToken)
        => options.Channel switch
        {
            ReShadeChannel.Stable => await EnsureLatestStagedAsync(architecture, cancellationToken).ConfigureAwait(false),
            ReShadeChannel.NoAddons => await EnsureLatestStagedAsync(architecture, cancellationToken, addonBuild: false).ConfigureAwait(false),
            ReShadeChannel.Nightly => await EnsureNightlyStagedAsync(architecture, cancellationToken).ConfigureAwait(false),
            ReShadeChannel.Legacy => await EnsureLegacyStagedAsync(options.LegacyVersion, architecture, cancellationToken).ConfigureAwait(false),
            ReShadeChannel.Custom => await EnsureCustomStagedAsync(options.CustomDllPath, architecture, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown ReShade channel.")
        };

    /// <param name="addonBuild">False for the build without add-on support (No Addons, #91): its own setup link on
    /// reshade.me, its own cache folder and outage fallback, never mixed with the add-on build.</param>
    internal async Task<ReShadeStagedDll> EnsureLatestStagedAsync(PeArchitecture architecture, CancellationToken cancellationToken = default, bool addonBuild = true)
    {
        var channel = addonBuild ? ReShadeChannel.Stable : ReShadeChannel.NoAddons;
        (string Version, string Url) latest;
        try
        {
            latest = await GetLatestStableAsync(cancellationToken, addonBuild).ConfigureAwait(false);
        }
        catch (ReShadeSiteUnavailableException ex)
        {
            // Outage, not a bad page: fall back to the newest stable copy already staged in the cache.
            return FindCachedStable(architecture, ex.Message, channel)
                   ?? throw new ReShadeSiteUnavailableException($"{ex.Message} No cached ReShade copy is available yet; try again later.", ex);
        }
        try
        {
            return await EnsureSetupStagedAsync(latest.Version, latest.Url, architecture, Path.Combine(CacheFolder(channel), latest.Version), channel, $"version:{latest.Version}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SetupDownloadOutage(ex, cancellationToken) is { } reason)
        {
            // The page answered but the setup download hit an outage (5xx, unreachable, timed out): same as a page outage.
            return FindCachedStable(architecture, reason, channel)
                   ?? throw new ReShadeSiteUnavailableException($"{reason} No cached ReShade copy is available yet; try again later.", ex);
        }
    }

    private static string CacheFolder(ReShadeChannel channel) => channel == ReShadeChannel.NoAddons ? "noaddons" : "stable";

    private static string SetupUrl(ReShadeChannel channel, string version)
        => channel == ReShadeChannel.NoAddons ? NoAddonsSetupUrl(version) : StableSetupUrl(version);

    // A server-side or network failure of the setup download, not a bad file or a 4xx: null when it is not an outage.
    private static string? SetupDownloadOutage(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException { StatusCode: { } status } when (int)status >= 500
            => $"reshade.me returned HTTP {(int)status} for the ReShade setup download.",
        HttpRequestException { StatusCode: null } or HttpIOException
            => $"reshade.me could not be reached ({ex.Message}).",
        TaskCanceledException when !cancellationToken.IsCancellationRequested
            => "reshade.me did not answer in time.",
        _ => null
    };

    private ReShadeStagedDll? FindCachedStable(PeArchitecture architecture, string reason, ReShadeChannel channel)
    {
        var root = Path.Combine(_cacheDir, CacheFolder(channel));
        if (!Directory.Exists(root)) return null;
        var dllName = architecture == PeArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll";
        var best = Directory.EnumerateDirectories(root)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Select(entry => (entry.Dir, entry.Name, Parsed: TryParseNumericVersion(entry.Name)))
            // Only copies this service stamped when it extracted them from that version's setup (#142):
            // filtered before ordering, so an unstamped newer folder never hides a stamped older one.
            .Where(entry => entry.Parsed is not null
                            && IsValidReShadeDll(Path.Combine(entry.Dir, dllName))
                            && CacheStamp.IsValid(Path.Combine(entry.Dir, dllName), SetupUrl(channel, entry.Name), entry.Name))
            .OrderByDescending(entry => entry.Parsed)
            .FirstOrDefault();
        if (best.Dir is null) return null;
        return new(
            Path.Combine(best.Dir, dllName),
            best.Name,
            SetupUrl(channel, best.Name),
            $"version:{best.Name}",
            channel,
            FromCache: true,
            FallbackReason: reason);
    }

    internal async Task<ReShadeStagedDll> EnsureLegacyStagedAsync(string? version, PeArchitecture architecture, CancellationToken cancellationToken = default)
    {
        version = version?.Trim();
        if (string.IsNullOrWhiteSpace(version) || !LegacyVersionRegex().IsMatch(version))
            throw new InvalidOperationException("Legacy channel needs an explicit numeric ReShade version such as 6.4.1.");
        var url = StableSetupUrl(version);
        return await EnsureSetupStagedAsync(version, url, architecture, Path.Combine("legacy", version), ReShadeChannel.Legacy, $"version:{version}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fixed text of a failed nightly lookup (PT-BR presentation pair).</summary>
    public const string NightlyLookupFailedMessage = "Could not look up the current ReShade nightly build on GitHub.";

    /// <summary>One ReShade nightly artifact: the Actions run it came from, its nightly.link URL and GitHub's digest.</summary>
    internal sealed record NightlyArtifact(long RunId, string Url, string? Sha256);

    internal static string NightlyIdentity(NightlyArtifact artifact)
        => string.IsNullOrWhiteSpace(artifact.Sha256) ? $"nightly-run:{artifact.RunId}" : $"nightly-run:{artifact.RunId}|sha256:{artifact.Sha256}";

    /// <summary>
    /// The newest successful nightly on main, chosen as nightly.link's branch link chooses it (newest push or
    /// schedule run, never a pull request), and the digest GitHub's Actions API publishes for its artifact
    /// (GitHub API calls revalidated with ETags for the session). Only a run built from crosire/reshade's own
    /// <c>main</c> counts. Null when GitHub cannot be asked (offline, rate-limited, no such run or artifact); the
    /// caller then falls back to upstream's branch link without a digest.
    /// </summary>
    internal async Task<NightlyArtifact?> ResolveNightlyArtifactAsync(bool is32, CancellationToken cancellationToken)
    {
        try
        {
            NightlyRun? newest = null;
            foreach (var triggerEvent in NightlyRunEvents)
            {
                var runsJson = await TrustedDownload.GetStringAsync(_http, NightlyRunsApiUrl(triggerEvent), DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
                var run = FirstOwnRun(runsJson, triggerEvent);
                if (run is not null && (newest is null || run.UpdatedAt > newest.UpdatedAt)) newest = run;
            }
            if (newest is null) throw new InvalidDataException("GitHub listed no successful ReShade nightly run on crosire/reshade main.");
            var runId = newest.Id;

            var artifactsJson = await TrustedDownload.GetStringAsync(_http, NightlyArtifactsApiUrl(runId), DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
            using var artifacts = JsonDocument.Parse(artifactsJson);
            if (!artifacts.RootElement.TryGetProperty("artifacts", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub returned no artifact list for the ReShade nightly run.");
            foreach (var item in items.EnumerateArray())
            {
                if (!string.Equals(item.TryGetProperty("name", out var name) ? name.GetString() : null, NightlyArtifactName(is32), StringComparison.Ordinal)) continue;
                if (item.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("The newest ReShade nightly artifact has expired.");
                var digest = TrustedDownload.NormalizeDigest(item.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null);
                return new NightlyArtifact(runId, NightlyRunUrl(runId, is32), digest);
            }
            throw new InvalidDataException($"The ReShade nightly run {runId} has no {NightlyArtifactName(is32)} artifact.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or InvalidDataException or JsonException or UntrustedDownloadException or TaskCanceledException or IOException)
        {
            AppLog.Warn("reshade-nightly", "GitHub could not be asked for the current ReShade nightly; using upstream's branch link without a digest", ex);
            return null;
        }
    }

    private sealed record NightlyRun(long Id, DateTimeOffset UpdatedAt);

    /// <summary>
    /// The first (newest) run in a GitHub workflow-runs answer that crosire/reshade built from its own <c>main</c>
    /// for <paramref name="triggerEvent"/>: head repository, head branch and event are all checked on the run itself,
    /// so a fork's pull request (head branch also named <c>main</c>) is skipped whatever the query returned.
    /// </summary>
    private static NightlyRun? FirstOwnRun(string runsJson, string triggerEvent)
    {
        using var runs = JsonDocument.Parse(runsJson);
        if (!runs.RootElement.TryGetProperty("workflow_runs", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned no run list for the ReShade nightly.");
        foreach (var run in list.EnumerateArray())
        {
            if (!string.Equals(StringOf(run, "event"), triggerEvent, StringComparison.Ordinal)) continue;
            if (!string.Equals(StringOf(run, "head_branch"), "main", StringComparison.Ordinal)) continue;
            if (!string.Equals(StringOf(run, "conclusion"), "success", StringComparison.Ordinal)) continue;
            var headRepository = run.TryGetProperty("head_repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                ? StringOf(repo, "full_name") : null;
            if (!string.Equals(headRepository, NightlyRepository, StringComparison.OrdinalIgnoreCase)) continue;
            if (!run.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
            var updated = DateTimeOffset.TryParse(StringOf(run, "updated_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
            return new NightlyRun(id, updated);
        }
        return null;

        static string? StringOf(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Stages the nightly DLL (#27). The artifact zip is identified by its GitHub Actions run and verified against the
    /// SHA-256 digest GitHub published for it (a mismatch is refused), then cached through its stamp; the DLL is
    /// extracted through the checked extraction into an entry bound to the zip's SHA-256, so two zips of equal size
    /// but different content never share a staged DLL. Without the GitHub lookup the branch link (upstream 2.8.5) is
    /// downloaded every time and accepted on HTTPS + host policy, its SHA-256 recorded (fail-open, as upstream).
    /// </summary>
    internal async Task<ReShadeStagedDll> EnsureNightlyStagedAsync(PeArchitecture architecture, CancellationToken cancellationToken = default)
    {
        var is32 = architecture == PeArchitecture.X86;
        var dllName = is32 ? "ReShade32.dll" : "ReShade64.dll";
        var dir = Path.Combine(_cacheDir, "nightly", is32 ? "x86" : "x64");
        Directory.CreateDirectory(dir);
        var staged = Path.Combine(dir, dllName);
        FileCleanup.TryDelete(Path.Combine(dir, "zip-size.txt")); // pre-#27 size marker

        var artifact = await ResolveNightlyArtifactAsync(is32, cancellationToken).ConfigureAwait(false);
        var url = artifact?.Url ?? (is32 ? Nightly32Url : Nightly64Url);
        var version = artifact is null ? null : $"run:{artifact.RunId}";
        var zipEntry = Path.Combine(dir, is32 ? "ReShade32-nightly.zip" : "ReShade64-nightly.zip");
        // The branch link has no identity of its own (new builds appear under it), so it is never reused.
        if (artifact is null) CacheStamp.Invalidate(zipEntry);
        var zip = await TrustedDownload.EnsureVerifiedAsync(
            _http, url, zipEntry, DownloadSources.ReShadeNightly, version, artifact?.Sha256,
            validate: ReShadeArchiveExtractor.RequireReadableZip,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var identity = artifact is null ? $"sha256:{zip.Sha256}" : NightlyIdentity(artifact);

        if (CacheStamp.IsValidBoundTo(staged, url, version, zip.Sha256) && IsValidReShadeDll(staged))
            return new(staged, "Nightly", url, identity, ReShadeChannel.Nightly, Download: zip);

        CacheStamp.Invalidate(staged);
        using (var archive = ZipFile.OpenRead(zip.Path))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(dllName, StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(dllName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new InvalidDataException($"{dllName} was not found in the nightly artifact.");
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, staged, ReShadeArchiveExtractor.MaxEntryBytes, cancellationToken).ConfigureAwait(false);
        }
        if (!IsValidReShadeDll(staged))
        {
            FileCleanup.TryDelete(staged);
            throw new InvalidDataException($"Extracted nightly {dllName} is invalid or too small.");
        }
        CacheStamp.WriteBoundTo(staged, url, version, zip.Sha256);
        return new(staged, "Nightly", url, identity, ReShadeChannel.Nightly, Download: zip);
    }

    internal async Task<ReShadeStagedDll> EnsureCustomStagedAsync(string? sourcePath, PeArchitecture architecture, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new InvalidOperationException("Custom channel needs a path to a ReShade DLL.");
        var expanded = Environment.ExpandEnvironmentVariables(sourcePath.Trim());
        if (expanded == "~") expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        var full = Path.GetFullPath(expanded);
        if (!IsValidReShadeDll(full)) throw new InvalidOperationException("Custom ReShade file is missing, not a PE DLL, or is smaller than 1 MB.");
        if (!BinarySignatureInspector.LooksLikeReShade(full))
            throw new InvalidOperationException("Custom DLL does not contain a recognizable ReShade signature; RHI will not inject an arbitrary DLL.");
        var customArch = PeArchitectureDetector.Detect(full);
        if (customArch != architecture)
            throw new InvalidOperationException($"Custom ReShade architecture mismatch: game is {architecture}, DLL is {customArch}.");
        var hash = await FileHash.Sha256HexAsync(full, cancellationToken).ConfigureAwait(false);
        var dir = Path.Combine(_cacheDir, "custom", hash[..16]);
        Directory.CreateDirectory(dir);
        var name = architecture == PeArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll";
        var staged = Path.Combine(dir, name);
        // The copy must still be the file just hashed (it could change in between): verified against that hash.
        await AtomicFile.CopyAsync(full, staged, hash, cancellationToken).ConfigureAwait(false);
        return new(staged, $"Custom {hash[..12]}", full, "sha256:" + hash, ReShadeChannel.Custom);
    }

    private async Task<ReShadeStagedDll> EnsureSetupStagedAsync(
        string version,
        string url,
        PeArchitecture architecture,
        string relativeCache,
        ReShadeChannel channel,
        string identity,
        CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(_cacheDir, relativeCache);
        Directory.CreateDirectory(versionDir);
        var desiredName = architecture == PeArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll";
        var staged = Path.Combine(versionDir, desiredName);
        // Reuse only files stamped at download/extraction time for this exact setup URL and version
        // (#142); anything else in the cache is replaced, never deployed.
        if (CacheStamp.IsValid(staged, url, version) && IsValidReShadeDll(staged)) return new(staged, version, url, identity, channel);

        // The setup's own file name (the add-on and the No Addons builds differ only in it).
        var setup = Path.Combine(versionDir, Path.GetFileName(new Uri(url).AbsolutePath));
        // reshade.me publishes no digest and upstream pins none: the setup is accepted on HTTPS + the reshade.me host
        // policy (redirects included) and a PE check, its SHA-256 recorded in the stamp and rhi.log (#27). A stamped
        // copy is reused only while it still hashes to the recorded value.
        var setupDownload = await TrustedDownload.EnsureVerifiedAsync(
            _http, url, setup, DownloadSources.ReShadeSetup, version,
            validate: (temp, _) => BinarySignatureInspector.HasPeSignature(temp, 500_000)
                ? Task.CompletedTask
                : throw new InvalidOperationException("reshade.me returned a file that is not a valid ReShade setup executable."),
            cancellationToken: cancellationToken,
            configureRequest: request => request.Headers.Referrer = new Uri(HomeUrl + "/")).ConfigureAwait(false);

        var extractDir = Path.Combine(versionDir, "extract");
        Directory.CreateDirectory(extractDir);
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll" })
        {
            // Only DLLs extracted from the verified setup in this call may be staged.
            foreach (var stale in new[] { Path.Combine(extractDir, name), Path.Combine(versionDir, name) })
            {
                CacheStamp.Invalidate(stale);
                if (File.Exists(stale)) File.Delete(stale);
            }
        }
        await _extractor.ExtractDllsAsync(setup, extractDir, cancellationToken).ConfigureAwait(false);
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll" })
        {
            var source = Path.Combine(extractDir, name);
            if (!IsValidReShadeDll(source)) continue;
            var target = Path.Combine(versionDir, name);
            AtomicFile.Copy(source, target);
            CacheStamp.Write(target, url, version);
        }
        if (!CacheStamp.IsValid(staged, url, version) || !IsValidReShadeDll(staged))
            throw new InvalidOperationException($"{desiredName} was not extracted from the ReShade setup.");
        return new(staged, version, url, identity, channel, Download: setupDownload);
    }

    // reshade.me intermittently answers HTTP 5xx while still rendering the full page (upstream 2.8.3).
    // A 5xx body is trusted only when it carries the add-on download link; the setup exe it points to is
    // still downloaded with a success check and PE-validated. 4xx and other statuses stay failures.
    private async Task<(string Version, string Url)> GetLatestStableAsync(CancellationToken cancellationToken, bool addonBuild = true)
    {
        string html;
        HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HomeUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            var code = (int)status;
            if (!response.IsSuccessStatusCode && code < 500)
                throw new HttpRequestException($"reshade.me returned HTTP {code}.", null, status);
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new ReShadeSiteUnavailableException($"reshade.me could not be reached ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReShadeSiteUnavailableException("reshade.me did not answer in time.", ex);
        }

        var match = (addonBuild ? AddonDownloadRegex() : NoAddonsDownloadRegex()).Match(html);
        if (!match.Success)
        {
            if ((int)status >= 500)
                throw new ReShadeSiteUnavailableException($"reshade.me is temporarily unavailable (HTTP {(int)status}) and its page has no ReShade download link.");
            throw new InvalidOperationException(addonBuild
                ? "Could not find the current full add-on ReShade download on reshade.me."
                : NoAddonsLinkMissingMessage);
        }
        var version = match.Groups[1].Value;
        // The captured version names cache folders and the setup file: only a plain dotted number is accepted ("..", "9..9" are not).
        if (TryParseNumericVersion(version) is null)
        {
            if ((int)status >= 500)
                throw new ReShadeSiteUnavailableException($"reshade.me is temporarily unavailable (HTTP {(int)status}) and its page has no ReShade download link.");
            throw new InvalidOperationException($"reshade.me listed a ReShade download with an invalid version: '{version}'.");
        }
        // Built from the captured version, not match.Value: the regex ignores case and has no host, so the
        // page's casing would stamp a URL the outage fallback could never match (#142).
        return (version, addonBuild ? StableSetupUrl(version) : NoAddonsSetupUrl(version));
    }

    private void CleanupManagedIni(LinuxDeploymentRecord record, string? gameRoot) // w5/ini-presets hook (review 8): gameRoot for the one link rule
    {
        var ini = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));
        if (ini is null) return;
        ReShadeIniManager.Cleanup(
            ini.Path,
            ReadBool(record.Metadata, MetaIniCreated),
            ini.Sha256,
            ReadStringList(record.Metadata.GetValueOrDefault(MetaIniEffectAdded)),
            ReadStringList(record.Metadata.GetValueOrDefault(MetaIniTextureAdded)),
            ReadBool(record.Metadata, MetaIniEffectCreated),
            ReadBool(record.Metadata, MetaIniTextureCreated),
            gameRoot);
    }

    private static void WriteIniMetadata(Dictionary<string, string> metadata, ReShadeIniApplyResult? ini)
    {
        foreach (var key in new[] { MetaIniCreated, MetaIniEffectCreated, MetaIniTextureCreated, MetaIniEffectAdded, MetaIniTextureAdded }) metadata.Remove(key);
        if (ini is null) return;
        metadata[MetaIniCreated] = ini.CreatedByRhi.ToString();
        metadata[MetaIniEffectCreated] = ini.EffectKeyCreated.ToString();
        metadata[MetaIniTextureCreated] = ini.TextureKeyCreated.ToString();
        metadata[MetaIniEffectAdded] = JsonSerializer.Serialize(ini.AddedEffectPaths);
        metadata[MetaIniTextureAdded] = JsonSerializer.Serialize(ini.AddedTexturePaths);
    }

    /// <summary>
    /// #203: adds a later RHI write into ReShade.ini (Deploy ReShade.ini) to what <paramref name="record"/> says RHI did
    /// there, so removal still takes all of it back — merged, never replaced: search paths RHI added stay recorded while
    /// the file still lists them, a key or file RHI created stays RHI's, and the file's hash becomes the new write's only
    /// when the file held RHI's last write before it (<paramref name="hashBefore"/> null = there was no file), so a change
    /// the user made meanwhile is never counted as RHI's. False when the record does not manage ReShade.ini.
    /// </summary>
    internal static bool RecordIniWrite(LinuxDeploymentRecord record, ReShadeIniApplyResult ini, string? hashBefore)
    {
        var entry = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        var metadata = record.Metadata;
        if (hashBefore is null || (entry.Sha256 is { Length: > 0 } last && FileHash.HexEquals(hashBefore, last)))
        {
            entry.Path = ini.Path;
            entry.Sha256 = ini.Sha256;
        }
        if (ini.CreatedByRhi) metadata[MetaIniCreated] = bool.TrueString;
        foreach (var (key, createdKey, addedKey, added, created) in new[]
                 {
                     (ReShadeIniManager.EffectKey, MetaIniEffectCreated, MetaIniEffectAdded, ini.AddedEffectPaths, ini.EffectKeyCreated),
                     (ReShadeIniManager.TextureKey, MetaIniTextureCreated, MetaIniTextureAdded, ini.AddedTexturePaths, ini.TextureKeyCreated)
                 })
        {
            var listed = ReShadeIniManager.SearchPaths(ini.Path, key);
            var merged = ReadStringList(metadata.GetValueOrDefault(addedKey))
                .Where(path => listed?.Contains(path, StringComparer.OrdinalIgnoreCase) == true)
                .Concat(added)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            metadata[addedKey] = JsonSerializer.Serialize(merged);
            metadata[createdKey] = ((ReadBool(metadata, createdKey) || created) && listed is not null).ToString();
        }
        return true;
    }

    /// <summary>INI files an install or update may rewrite: ReShade.ini (old record's and current) and, when coexisting, OptiScaler.ini.</summary>
    private static IEnumerable<string> IniPathsTouchedByInstall(DetectedGame game, LinuxDeploymentRecord? existingRecord, bool coexist)
    {
        var paths = new List<string> { ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!) };
        if (existingRecord?.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)) is { } recorded)
            paths.Add(recorded.Path);
        if (coexist) paths.Add(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini"));
        return paths.Distinct(StringComparer.Ordinal);
    }

    private async Task RefreshManagedOptiScalerIniHashAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var record = _records.Find(game, ManagedComponentKind.OptiScaler);
        if (record is null) return;
        var ini = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        if (ini is null || !File.Exists(ini.Path)) return;
        ini.Sha256 = await FileHash.Sha256HexAsync(ini.Path, cancellationToken).ConfigureAwait(false);
        ini.AllowUserChanges = true;
        _records.Upsert(record);
    }

    internal static string? SelectProxyName(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX9 => "d3d9.dll",
        GraphicsApi.DirectX10 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12 => "dxgi.dll",
        GraphicsApi.OpenGL => "opengl32.dll",
        _ => null
    };

    private static bool IsValidReShadeDll(string path)
        => File.Exists(path) && new FileInfo(path).Length > MinDllSize && BinarySignatureInspector.HasPeSignature(path, MinDllSize);

    // Compensation: never cancellable, or the .original the replace created stays beside the game's file.
    private async Task RollbackFreshFileAsync(string target, bool existedBefore)
    {
        if (existedBefore && File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
            await _files.RestoreAsync(target, CancellationToken.None).ConfigureAwait(false);
        else if (!FileCleanup.TryDelete(target, out var error))
        {
            AppLog.Warn("reshade", $"Rollback could not remove {target}", error);
        }
    }

    internal const string CachedCopyNote = " reshade.me was unavailable, so the cached copy was used.";

    // ReShade versions are dotted numbers; missing parts count as zero, so "6.3" equals "6.3.0".
    private static Version? TryParseNumericVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !LegacyVersionRegex().IsMatch(value) || !Version.TryParse(value, out var parsed))
            return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
    }

    /// <summary>Compares two numeric ReShade versions; null when either is not a plain dotted number.</summary>
    internal static int? CompareNumericVersions(string? candidate, string? installed)
    {
        var c = TryParseNumericVersion(candidate);
        var i = TryParseNumericVersion(installed);
        return c is null || i is null ? null : c.CompareTo(i);
    }

    private static ReShadeChannel ParseChannel(string? value)
        => Enum.TryParse<ReShadeChannel>(value, true, out var channel) ? channel : ReShadeChannel.Stable;

    private static IReadOnlyList<string> ReadStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch { return []; }
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var result) && result;

    private static void SetOrRemove(Dictionary<string, string> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) metadata.Remove(key);
        else metadata[key] = value;
    }

    [GeneratedRegex(@"/downloads/ReShade_Setup_([\d.]+)_Addon\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex AddonDownloadRegex();

    // Upstream NormalReShadeUpdateService's pattern: the setup without the _Addon suffix.
    [GeneratedRegex(@"/downloads/ReShade_Setup_([\d.]+)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex NoAddonsDownloadRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVersionRegex();
}

/// <summary>reshade.me is down or answered 5xx without a usable page; callers may fall back to a cached copy.</summary>
internal sealed class ReShadeSiteUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
