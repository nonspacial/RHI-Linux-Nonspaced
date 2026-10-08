using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public enum Dlss5FeederConsumer
{
    DeepFriedChicken,
    RenoDxDlss5
}

/// <summary>
/// Linux-native DLSS5 Feeder orchestration aligned with upstream RHI while preserving the accepted Deep Fried Chicken consumer path.
/// This implementation targets supported Wine/Proton games without native DLSS.
/// It stages the Feeder add-on, DLSS5 neural consumer, SR/NR runtime DLLs and an isolated
/// RHI-owned shader tree with LumeniteFX + DLSS5_Feed.fx. ReShade itself remains a separate,
/// already-validated managed component and must be installed first.
/// </summary>
public sealed class Dlss5FeederFullDeploymentService
{
    public const Dlss5FeederConsumer DefaultConsumer = Dlss5FeederConsumer.RenoDxDlss5;
    private const string FeederReleaseApi = "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest";
    private const string FeederArchiveAssetPattern = @"^DLSS5-Feeder-.*\.zip$";
    private const string RhiRepoReleasesApi = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    private const string RenoDx5AssetPattern = @"^renodx-dlss5_.*\.zip$";
    private const string FeederFxName = "DLSS5_Feed.fx";
    private const string LumeniteUrl = "https://codeload.github.com/umar-afzaal/LumeniteFX/zip/refs/heads/mainline";
    private const string ReShadeHeadersUrl = "https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip";

    private const string FeederAddonName = "dlss5-feed.addon64";
    private const string FeederAddon32Name = "dlss5-feed.addon32";
    private const string Host64DirectoryName = "host64";
    private const string Host64ExecutableName = "dlss5-feed-host64.exe";
    private const string DeepFriedAddonName = "deep-fried-chicken.addon64";
    private const string DeepFriedBridgeName = "deep-fried-chicken-nvngx.dll";
    private const string DeepFriedConfigName = "deep-fried-chicken.cfg";
    private const string RenoDx5AddonName = "renodx-dlss5.addon64";
    private const string RenoDxLegacyAddonName = "renodx-dlss.addon64";
    private const string AlexToolkitAddonName = "alexs-toolkit.addon64";
    private const string Dlss5BridgeAddonName = "dlss5-bridge.addon64";
    private const string Dlss5Dx11BridgeAddonName = "dlss5-dx11-bridge.addon64";
    private const string DlssName = "nvngx_dlss.dll";
    private const string NrName = "nvngx_dlssnr.dll";
    private const string PresetName = "RHI-DLSS5-Feeder.ini";
    private const string IntegrationRootName = "DLSS5FeederFull";
    private const string FullStackMetadata = "Feeder.FullStack";

    // Record metadata parts of the downloads besides the Feeder release (Download.<part>.Sha256/Integrity, #26).
    internal const string ConsumerDownloadPart = "Consumer";
    internal const string LumeniteDownloadPart = "Lumenite";
    internal const string HeadersDownloadPart = "ReShadeHeaders";
    internal const string FeederShaderDownloadPart = "FeederShader";

    // Public upstream archive password used by current encrypted Deep Fried Chicken releases.
    // It is supplied only to the extractor process through redirected stdin and is never persisted
    // in XDG settings, deployment records, command-line arguments or logs.
    internal const string DeepFriedChickenArchivePassword = "chicken";

    private const string MetaIniPath = "Feeder.ReShadeIniPath";
    private const string MetaIniCreated = "Feeder.ReShadeIniCreated";
    private const string MetaIniEffectKeyCreated = "Feeder.EffectKeyCreated";
    private const string MetaIniTextureKeyCreated = "Feeder.TextureKeyCreated";
    private const string MetaIniEffectAdded = "Feeder.EffectPathsAdded";
    private const string MetaIniTextureAdded = "Feeder.TexturePathsAdded";
    private const string MetaIniHash = "Feeder.ReShadeIniHash";
    private const string MetaPresetPreviousExists = "Feeder.PresetPreviousExists";
    private const string MetaPresetPreviousValue = "Feeder.PresetPreviousValue";
    private const string MetaPresetSetValue = "Feeder.PresetSetValue";
    private const string MetaFeederVersion = "Feeder.Version";
    private const string MetaConsumer = "Feeder.Consumer";
    private const string MetaConsumerVersion = "Feeder.ConsumerVersion";
    private const string MetaConsumerPackageSha256 = "Feeder.ConsumerPackageSha256";
    private const string MetaLoadFromDllMainAdded = "Feeder.LoadFromDllMainAdded";
    internal const string MetaDx9Migration = "Feeder.Dx9ReShadeMigration";
    internal const string MetaDgVoodooVersion = "Feeder.DgVoodooVersion";

    private static readonly string[] RequiredHeaderNames = ["ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh"];
    private static readonly TimeSpan DeepFriedChickenExtractionTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DlssManagerService _dlss;
    private readonly UpstreamRhiAddonDeploymentService _legacyAddonOnly;
    private readonly GitHubReleaseAssetResolver _releaseResolver;
    private readonly WineDllOverrideService? _overrides;
    private readonly ReShadeDeploymentService? _reShade;
    private readonly DgVoodooDeploymentService? _dgVoodoo;
    private readonly Dx9TranslationReShadeCoordinator? _dx9ReShade;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheDir;

    public Dlss5FeederFullDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        DlssManagerService dlss,
        UpstreamRhiAddonDeploymentService legacyAddonOnly,
        string? cacheDir = null,
        WineDllOverrideService? overrides = null,
        ReShadeDeploymentService? reShade = null,
        DgVoodooDeploymentService? dgVoodoo = null,
        Dx9TranslationReShadeCoordinator? dx9ReShade = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _dlss = dlss;
        _legacyAddonOnly = legacyAddonOnly;
        _releaseResolver = new GitHubReleaseAssetResolver(http);
        _overrides = overrides;
        _reShade = reShade;
        _dgVoodoo = dgVoodoo;
        _dx9ReShade = dx9ReShade;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "dlss5-feeder-full");
    }

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, DefaultConsumer, deepFriedChickenPackagePath: null, d3dCompiler47Path: null, cancellationToken: cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? deepFriedChickenPackagePath,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, Dlss5FeederConsumer.DeepFriedChicken, deepFriedChickenPackagePath, d3dCompiler47Path: null, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, Dlss5FeederConsumer.DeepFriedChicken, deepFriedChickenPackagePath, d3dCompiler47Path, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, consumer, deepFriedChickenPackagePath, d3dCompiler47Path, releaseTag: null, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        string? releaseTag,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, consumer, deepFriedChickenPackagePath, d3dCompiler47Path, releaseTag, neuralRenderingDllVersion: null, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        string? releaseTag,
        string? neuralRenderingDllVersion,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => InstallCoreAsync(game, consumer, deepFriedChickenPackagePath, d3dCompiler47Path, releaseTag, neuralRenderingDllVersion, cancellationToken),
            cancellationToken);

    /// <summary>Swaps the Feeder-owned nvngx_dlssnr.dll to a manifest version in place (upstream 2.7.6).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder", version, cancellationToken);

    public Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => _releaseResolver.ListVersionsAsync(FeederReleaseApi, cancellationToken);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        if (record is null) return new ManagedUpdateCheck(false, false, "DLSS5 Feeder is not managed by RHI Linux.");
        if (!record.Metadata.TryGetValue(FullStackMetadata, out var full) || full != "1")
            // Not checked against a release, so not known to be current (w2-launch review, minor 4).
            return new ManagedUpdateCheck(true, false, "Legacy DLSS5 Feeder add-on-only deployment; remove and reinstall to update.", record.Version,
                CouldNotCheck: true);
        try
        {
            var target = await _releaseResolver.ResolveAsync(CreateFeederArchiveReleaseQuery(), releaseTag, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, target.Version, StringComparison.OrdinalIgnoreCase);
            var mode = string.IsNullOrWhiteSpace(releaseTag) ? "latest" : $"pinned {releaseTag}";
            return new ManagedUpdateCheck(true, update,
                update ? $"DLSS5 Feeder {mode}: {record.Version} → {target.Version}." : $"DLSS5 Feeder {record.Version} is current ({mode}).",
                record.Version, target.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"DLSS5 Feeder update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    // The host64 ReShade is staged like any stable ReShade: during a reshade.me outage it comes from the cache.
    internal static string Host64CacheNote(bool fromCache)
        => fromCache ? " The 64-bit host ReShade came from the ReShade cache because reshade.me was unavailable." : "";

    /// <summary>
    /// Replaces only the Feeder add-on (plus the host64 helper on 32-bit games, which ships in the
    /// same archive and must match it) with the pinned or latest release. The consumer, DLSS DLLs,
    /// shaders, preset and ReShade.ini are left as installed.
    /// </summary>
    public Task<ComponentOperationResult> SwapFeederVersionAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
        => Task.Run(() => SwapFeederVersionCoreAsync(game, releaseTag, cancellationToken), cancellationToken);

    private async Task<ComponentOperationResult> SwapFeederVersionCoreAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        if (record is null)
            return ComponentOperationResult.Fail("DLSS5 Feeder is not managed by RHI Linux.");
        if (!record.Metadata.TryGetValue(FullStackMetadata, out var full) || full != "1")
            return ComponentOperationResult.Fail("Legacy DLSS5 Feeder add-on-only deployment cannot be swapped in place. Remove it, then install the full Feeder stack.");

        var payloadNames = new List<string>();
        if (ManagedFileSwap.FindFile(record, FeederAddonName) is not null) payloadNames.Add(FeederAddonName);
        else if (ManagedFileSwap.FindFile(record, FeederAddon32Name) is not null) payloadNames.Add(FeederAddon32Name);
        else return ComponentOperationResult.Fail("The DLSS5 Feeder record does not own a Feeder add-on. Remove and reinstall DLSS5 Feeder.");
        if (ManagedFileSwap.FindFile(record, Host64ExecutableName) is not null) payloadNames.Add(Host64ExecutableName);

        var stageRoot = Path.Combine(_cacheDir, $"swap-{Guid.NewGuid():N}");
        try
        {
            GitHubResolvedReleaseAsset release;
            VerifiedDownload download;
            var items = new List<(string RecordedPath, string SourcePath)>();
            try
            {
                Directory.CreateDirectory(stageRoot);
                release = await _releaseResolver.ResolveAsync(CreateFeederArchiveReleaseQuery(), releaseTag, cancellationToken).ConfigureAwait(false);
                // The release zip is cached per version through its stamp (#114): swapping several games, or the
                // same game again, downloads it once.
                download = await EnsureCachedReleaseAssetAsync(release, DownloadSources.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
                var zip = download.Path;
                foreach (var name in payloadNames)
                    items.Add((ManagedFileSwap.FindFile(record, name)!.Path,
                        await ExtractReleasePayloadByNameAsync(zip, name, Path.Combine(stageRoot, name), minimumSize: 64 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return ComponentOperationResult.Fail($"DLSS5 Feeder download failed; nothing was changed: {ex.Message}");
            }

            try
            {
                var changed = await ManagedFileSwap.ReplaceAsync(record, items, updated =>
                {
                    updated.Version = release.Version;
                    updated.SourceUrl = release.AssetUrl;
                    updated.Metadata[MetaFeederVersion] = release.Version;
                    updated.Metadata["GitHub.ReleaseTagPin"] = releaseTag ?? "";
                    download.RecordIn(updated.Metadata);
                    _records.Upsert(updated);
                }, cancellationToken).ConfigureAwait(false);
                return ComponentOperationResult.Ok(changed == 0
                    ? $"DLSS5 Feeder {release.Version} is already installed."
                    : $"DLSS5 Feeder swapped in place to {release.Version}. Consumer, DLSS DLLs, shaders and configs were not changed.",
                    items.Select(item => item.RecordedPath).ToArray());
            }
            catch (ManagedFileRestoreException ex)
            {
                return ComponentOperationResult.Fail($"DLSS5 Feeder in-place swap failed and the previous files could not be restored: {ex.Message}");
            }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"DLSS5 Feeder in-place swap failed; the previous files were kept: {ex.Message}");
            }
        }
        finally
        {
            DeleteStage(stageRoot);
        }
    }

    private async Task<ComponentOperationResult> InstallCoreAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        string? releaseTag,
        string? neuralRenderingDllVersion,
        CancellationToken cancellationToken)
    {
        // Installation can perform bounded recursive file indexing, archive validation,
        // SHA-256 hashing and external extraction before the first network await. Keep the
        // entire core off the Avalonia caller thread so a slow game tree or consumer archive
        // can never freeze the application window.
        cancellationToken.ThrowIfCancellationRequested();

        // Ownership conflicts are stronger than install prerequisites: if OptiScaler DLSS NR
        // already owns nvngx_dlssnr.dll, surface that blocker before asking the user to install
        // ReShade or resolve a compiler. This also guarantees the conflict is detected before
        // any package/network work.
        var optiRecord = _records.Find(game, ManagedComponentKind.OptiScaler);
        if (optiRecord is not null && OptiScalerDeploymentService.GetInstalledVariant(optiRecord) == OptiScalerVariant.DlssNr)
            return ComponentOperationResult.Fail("Remove the OptiScaler DLSS NR variant before installing DLSS5 Feeder. Both components would own nvngx_dlssnr.dll and RHI Linux does not yet have a shared-file ownership ledger for that runtime.");

        var preflight = ValidateTarget(game, consumer);
        if (preflight is not null) return preflight;

        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full needs an initialized Wine/Proton prefix. Start the game once first.");
        if (_overrides?.EditRefusal(prefix) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing DLSS5 Feeder Full. Both workflows own the DLSS/Streamline stack and must not be stacked.");
        var gameArchitecture = PeArchitectureDetector.Detect(game.ExecutablePath!);
        FeederCompilerPlan compilerPlan;
        try
        {
            compilerPlan = ResolveCompilerPlan(game, gameArchitecture, d3dCompiler47Path);
        }
        catch (Exception ex)
        {
            var installHint = game.SteamAppId.HasValue
                ? "Use the 'Install via Protontricks' D3DCompiler button in the ReShade profile, or browse a native Microsoft D3DCompiler_47.dll."
                : D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
                    ? $"Use the Winetricks button bound to the active {game.CompatibilityTool ?? game.Runner ?? game.Source} runner, or browse a native Microsoft D3DCompiler_47.dll."
                    : "Use the 'Install via Winetricks' D3DCompiler button, or browse a native Microsoft D3DCompiler_47.dll.";
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full could not resolve a usable native Microsoft D3DCompiler_47.dll. {ex.Message} {installHint}");
        }

        var existingRecord = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        if (existingRecord is not null)
        {
            if (!existingRecord.Metadata.TryGetValue(FullStackMetadata, out var full) || full != "1")
                return ComponentOperationResult.Fail("Legacy DLSS5 Feeder add-on-only deployment is still managed by RHI Linux. Remove it once, then install again to deploy the full Feeder stack.");
            return ComponentOperationResult.Fail("DLSS5 Feeder Full is already managed by RHI Linux. Remove it before reinstalling.");
        }

        var consumerPackage = ExpandUserPath(deepFriedChickenPackagePath);
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken)
        {
            if (string.IsNullOrWhiteSpace(consumerPackage) || !File.Exists(consumerPackage))
                return ComponentOperationResult.Fail("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z. Selecione-o no campo de pacote consumidor antes de instalar.");
            if (!IsSupportedDeepFriedChickenArchive(consumerPackage))
                return ComponentOperationResult.Fail("O pacote consumidor do Deep Fried Chicken deve ser um arquivo oficial .zip ou .7z.");
        }

        var binaryDir = Path.GetFullPath(game.BinaryDirectory!);
        var addonDir = RenoDxDeploymentService.ResolveAddonDeployPath(binaryDir);
        FeederDeploymentLayout layout;
        try { layout = ResolveFeederLayout(binaryDir, addonDir, gameArchitecture, consumer); }
        catch (NotSupportedException ex) { return ComponentOperationResult.Fail(ex.Message); }
        var feederTarget = layout.FeederTarget;
        var deepFriedAddonTarget = Path.Combine(binaryDir, DeepFriedAddonName);
        var deepFriedBridgeTarget = Path.Combine(binaryDir, DeepFriedBridgeName);
        var deepFriedConfigTarget = (consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? ReShadeDeploymentService.FindFileCaseInsensitive(binaryDir, DeepFriedConfigName)
            : null) ?? Path.Combine(binaryDir, DeepFriedConfigName);
        var renoDx5Target = layout.ConsumerTarget;
        var deepFriedConfigPreexisting = consumer == Dlss5FeederConsumer.DeepFriedChicken && File.Exists(deepFriedConfigTarget);
        var dlssTarget = layout.DlssTarget;
        var nrTarget = layout.NrTarget;
        var integrationRoot = Path.Combine(binaryDir, "reshade-shaders", "RHI", IntegrationRootName);
        var shaderRoot = Path.Combine(integrationRoot, "Shaders");
        var textureRoot = Path.Combine(integrationRoot, "Textures");
        var presetTarget = Path.Combine(binaryDir, PresetName);

        var protectedTargets = consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? new[] { feederTarget, deepFriedAddonTarget, deepFriedBridgeTarget, deepFriedConfigTarget, dlssTarget, nrTarget, presetTarget }
            : new[] { feederTarget, renoDx5Target, dlssTarget, nrTarget, presetTarget };
        foreach (var path in protectedTargets)
            if (IsBlockingPreexistingFullStackTarget(path) &&
                ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(path)!, Path.GetFileName(path)) is { } existingTarget)
                return ComponentOperationResult.Fail($"External or pre-existing DLSS5 Feeder Full target detected: {existingTarget}. RHI Linux will not overwrite it automatically.");

        var conflictPaths = (consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? new[]
            {
                Path.Combine(binaryDir, RenoDx5AddonName),
                Path.Combine(binaryDir, RenoDxLegacyAddonName),
                Path.Combine(binaryDir, AlexToolkitAddonName),
                Path.Combine(binaryDir, Dlss5BridgeAddonName),
                Path.Combine(binaryDir, Dlss5Dx11BridgeAddonName),
                Path.Combine(addonDir, RenoDx5AddonName),
                Path.Combine(addonDir, RenoDxLegacyAddonName),
                Path.Combine(addonDir, AlexToolkitAddonName),
                Path.Combine(addonDir, Dlss5BridgeAddonName),
                Path.Combine(addonDir, Dlss5Dx11BridgeAddonName)
            }
            : new[]
            {
                Path.Combine(binaryDir, DeepFriedAddonName),
                Path.Combine(binaryDir, DeepFriedBridgeName),
                Path.Combine(addonDir, DeepFriedAddonName),
                Path.Combine(binaryDir, RenoDxLegacyAddonName),
                Path.Combine(binaryDir, AlexToolkitAddonName),
                Path.Combine(binaryDir, Dlss5BridgeAddonName),
                Path.Combine(binaryDir, Dlss5Dx11BridgeAddonName),
                Path.Combine(addonDir, RenoDxLegacyAddonName),
                Path.Combine(addonDir, AlexToolkitAddonName),
                Path.Combine(addonDir, Dlss5BridgeAddonName),
                Path.Combine(addonDir, Dlss5Dx11BridgeAddonName)
            }).Concat(new[]
            {
                // ShortFuse deployed under the upstream ZZZ load-order name is the same consumer.
                Path.Combine(binaryDir, ShortFuseDeploymentService.ZzzAddonFileName),
                Path.Combine(addonDir, ShortFuseDeploymentService.ZzzAddonFileName)
            }).Distinct(StringComparer.Ordinal);
        foreach (var conflict in conflictPaths)
            if (ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(conflict)!, Path.GetFileName(conflict)) is { } existingConflict)
                return ComponentOperationResult.Fail($"Consumidor neural conflitante detectado: {existingConflict}. Apenas um consumidor neural pode controlar o processo. Remova explicitamente o add-on conflitante primeiro.");


        if (layout.RequiresHost64 && Directory.Exists(layout.Host64Directory!) && Directory.EnumerateFileSystemEntries(layout.Host64Directory!).Any())
            return ComponentOperationResult.Fail($"External or previous host64 tree detected at {layout.Host64Directory}. RHI Linux will not overwrite it automatically.");
        if (Directory.Exists(integrationRoot) && Directory.EnumerateFileSystemEntries(integrationRoot).Any())
            return ComponentOperationResult.Fail($"External or previous shader tree detected at {integrationRoot}. RHI Linux will not overwrite it automatically.");

        Directory.CreateDirectory(_cacheDir);
        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (operation, journalRefusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(
            _journal, "DLSS5 Feeder install: " + game.Name, game, ManagedComponentKind.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var journaled = operation;
        var stageRoot = Path.Combine(_cacheDir, $"stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);

        var copied = new List<string>();
        var createdDirs = new List<string>();
        ReShadeIniApplyResult? iniResult = null;
        PresetPathMutation? presetMutation = null;
        IniListTokenMutation? earlyLoadMutation = null;
        WineOverrideRecord? compilerOverride = null;
        ReShadeDx9MigrationState? dx9Migration = null;
        DgVoodooPreparedPayload? dgVoodooPayload = null;
        LinuxDeploymentRecord? sharedDx9Record = null;
        var dx9TranslationCreatedThisAttempt = false;
        try
        {
            DeepFriedChickenBundle? dfc = null;
            GitHubResolvedReleaseAsset? renoDxRelease = null;
            string? renoDxAddon = null;
            string? renoDxPackageSha256 = null;
            // Every download this record deploys, by record metadata part (#26): null = the Feeder release itself.
            var downloads = new List<(string? Part, string Sha256, bool DigestVerified)>();

            if (consumer == Dlss5FeederConsumer.DeepFriedChicken)
            {
                // Manual consumer path: validate the user-supplied package before any registry mutation.
                dfc = await ExtractDeepFriedChickenBundleAsync(
                    consumerPackage!,
                    Path.Combine(stageRoot, "deep-fried-chicken"),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Experimental consumer path restored for controlled A/B testing. Resolve only the
                // dedicated RenoDX DLSS5 neural consumer; ordinary game-specific RenoDX HDR add-ons
                // remain independent and may coexist because they are not classified as this consumer.
                renoDxRelease = await _releaseResolver.ResolveAsync(
                    CreateRenoDxDlss5ReleaseQuery(),
                    cancellationToken).ConfigureAwait(false);
                var consumerDownload = await EnsureCachedReleaseAssetAsync(renoDxRelease, DownloadSources.RhiRepository, cancellationToken).ConfigureAwait(false);
                var consumerAsset = consumerDownload.Path;
                renoDxPackageSha256 = consumerDownload.Sha256;
                downloads.Add((ConsumerDownloadPart, consumerDownload.Sha256, consumerDownload.DigestVerified));
                renoDxAddon = renoDxRelease.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? await ExtractReleasePayloadByNameAsync(consumerAsset, RenoDx5AddonName, Path.Combine(stageRoot, RenoDx5AddonName), minimumSize: 32 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false)
                    : ValidateDownloadedPayload(consumerAsset, RenoDx5AddonName, minimumSize: 32 * 1024, requirePe: true);
            }

            if (compilerPlan.DeploySourcePath is not null)
                await CopyOwnedAsync(compilerPlan.DeploySourcePath, compilerPlan.UsablePath, copied, cancellationToken).ConfigureAwait(false);

            if (_overrides is not null)
                compilerOverride = await _overrides.EnsureNativeBuiltinAsync(operation, prefix, "d3dcompiler_47", Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
            // Current upstream publishes a single DLSS5-Feeder-*.zip release asset.
            // The archive is produced by Windows tooling, so entries may carry either '/' or '\\'
            // separators. Linux must normalize both before matching leaf names. The tagged raw
            // shader is a conservative fallback only when an archive genuinely omits DLSS5_Feed.fx.
            var feederRelease = await _releaseResolver.ResolveAsync(
                CreateFeederArchiveReleaseQuery(),
                releaseTag,
                cancellationToken).ConfigureAwait(false);
            var feederDownload = await EnsureCachedReleaseAssetAsync(feederRelease, DownloadSources.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
            var feederZip = feederDownload.Path;
            downloads.Add((null, feederDownload.Sha256, feederDownload.DigestVerified));
            var feederAssetName = gameArchitecture == PeArchitecture.X86 ? FeederAddon32Name : FeederAddonName;
            var feederAddon = await ExtractReleasePayloadByNameAsync(
                feederZip,
                feederAssetName,
                Path.Combine(stageRoot, feederAssetName),
                minimumSize: 64 * 1024,
                requirePe: true,
                cancellationToken).ConfigureAwait(false);
            string? host64Executable = null;
            if (layout.RequiresHost64)
            {
                host64Executable = await ExtractReleasePayloadByNameAsync(
                    feederZip,
                    Host64ExecutableName,
                    Path.Combine(stageRoot, Host64ExecutableName),
                    minimumSize: 64 * 1024,
                    requirePe: true,
                    cancellationToken).ConfigureAwait(false);
            }

            string feederFx;
            try
            {
                feederFx = await ExtractReleasePayloadByNameAsync(
                    feederZip,
                    FeederFxName,
                    Path.Combine(stageRoot, FeederFxName),
                    minimumSize: 256,
                    requirePe: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains(FeederFxName, StringComparison.OrdinalIgnoreCase))
            {
                feederFx = await DownloadFeederShaderFromResolvedVersionAsync(
                    feederRelease.Version,
                    Path.Combine(stageRoot, FeederFxName),
                    cancellationToken).ConfigureAwait(false);
                downloads.Add((FeederShaderDownloadPart, FileHash.Sha256Hex(feederFx), false));
            }

            var cachedSr = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.SuperResolution, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current RHI DLSS manifest does not provide a Super Resolution runtime.");
            var cachedNr = await _dlss.EnsureCachedAsync(DlssComponentKind.NeuralRendering, neuralRenderingDllVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current RHI DLSS manifest does not provide a Neural Rendering runtime.");
            ValidateCachedRuntime(cachedSr, DlssName);
            ValidateCachedRuntime(cachedNr, NrName);

            var lumeniteStage = Path.Combine(stageRoot, "lumenite");
            downloads.Add((LumeniteDownloadPart,
                await DownloadAndExtractShaderArchiveAsync(LumeniteUrl, Path.Combine(stageRoot, "lumenite.zip"), lumeniteStage, cancellationToken).ConfigureAwait(false),
                false));
            EnsureLumeniteLayout(lumeniteStage);

            var headersStage = Path.Combine(stageRoot, "headers");
            downloads.Add((HeadersDownloadPart,
                await DownloadAndExtractShaderArchiveAsync(ReShadeHeadersUrl, Path.Combine(stageRoot, "headers.zip"), headersStage, cancellationToken).ConfigureAwait(false),
                false));
            EnsureHeaderLayout(headersStage);

            if (game.EffectiveGraphicsApi == GraphicsApi.DirectX9)
            {
                var lumaRecord = _records.Find(game, ManagedComponentKind.LumaFramework);
                dx9Migration = ReadDx9Migration(lumaRecord, LumaFrameworkDeploymentService.MetaDx9Migration);
                if (dx9Migration is not null)
                {
                    var sharedError = await ValidateSharedDx9TranslationAsync(lumaRecord!, cancellationToken).ConfigureAwait(false);
                    if (sharedError is not null)
                        throw new InvalidOperationException($"Existing Luma DX9 translation ownership is inconsistent: {sharedError}");
                    sharedDx9Record = lumaRecord;
                }
                else
                {
                    if (_dgVoodoo is null || _dx9ReShade is null)
                        throw new InvalidOperationException("DX9 DLSS5 Feeder requires dgVoodoo2 staging and the managed ReShade DX9 migration coordinator.");
                    dgVoodooPayload = await _dgVoodoo.PrepareLatestAsync(gameArchitecture, cancellationToken).ConfigureAwait(false);
                    var dgConfigTarget = Path.Combine(binaryDir, "dgVoodoo.conf");
                    if (File.Exists(dgConfigTarget))
                        throw new IOException($"External dgVoodoo.conf already exists: {dgConfigTarget}. RHI Linux will not overwrite it automatically.");
                    dx9Migration = await _dx9ReShade.MigrateAsync(game, cancellationToken).ConfigureAwait(false);
                    dx9TranslationCreatedThisAttempt = true;
                }
            }

            Directory.CreateDirectory(addonDir);
            Directory.CreateDirectory(shaderRoot);
            Directory.CreateDirectory(textureRoot);
            createdDirs.Add(integrationRoot);
            var host64CacheNote = "";
            if (layout.RequiresHost64)
            {
                if (_reShade is null)
                    throw new InvalidOperationException("32-bit DLSS5 Feeder needs the ReShade service to stage a 64-bit host64 ReShade runtime.");
                Directory.CreateDirectory(layout.Host64Directory!);
                createdDirs.Add(layout.Host64Directory!);
                var hostReShade = await _reShade.EnsureLatestStagedAsync(PeArchitecture.X64, cancellationToken).ConfigureAwait(false);
                host64CacheNote = Host64CacheNote(hostReShade.FromCache);
                await CopyOwnedAsync(host64Executable!, layout.Host64ExecutableTarget!, copied, cancellationToken).ConfigureAwait(false);
                await CopyOwnedAsync(hostReShade.DllPath, Path.Combine(layout.Host64Directory!, "dxgi.dll"), copied, cancellationToken).ConfigureAwait(false);
            }

            if (dgVoodooPayload is not null)
            {
                await CopyOwnedAsync(dgVoodooPayload.D3D9Path, Path.Combine(binaryDir, "D3D9.dll"), copied, cancellationToken).ConfigureAwait(false);
                await CopyOwnedAsync(dgVoodooPayload.ConfigPath, Path.Combine(binaryDir, "dgVoodoo.conf"), copied, cancellationToken).ConfigureAwait(false);
            }

            await CopyOwnedAsync(feederAddon, feederTarget, copied, cancellationToken).ConfigureAwait(false);
            if (consumer == Dlss5FeederConsumer.DeepFriedChicken)
            {
                await CopyOwnedAsync(dfc!.AddonPath, deepFriedAddonTarget, copied, cancellationToken).ConfigureAwait(false);
                await CopyOwnedAsync(dfc.BridgePath, deepFriedBridgeTarget, copied, cancellationToken).ConfigureAwait(false);
                if (!deepFriedConfigPreexisting)
                    await CopyOwnedAsync(dfc.ConfigPath, deepFriedConfigTarget, copied, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyOwnedAsync(renoDxAddon!, renoDx5Target, copied, cancellationToken).ConfigureAwait(false);
            }
            await CopyOwnedAsync(cachedSr, dlssTarget, copied, cancellationToken).ConfigureAwait(false);
            await CopyOwnedAsync(cachedNr, nrTarget, copied, cancellationToken).ConfigureAwait(false);

            var feederFxTarget = Path.Combine(shaderRoot, "DLSS5_Feed.fx");
            await CopyOwnedAsync(feederFx, feederFxTarget, copied, cancellationToken).ConfigureAwait(false);

            // Match the upstream Feeder refresh invariant: deploy the Lumenite Kernel
            // and its dependencies, not every optional LumeniteFX effect. This avoids the
            // pack-refresh class of bug fixed upstream on Windows.
            CopyLumeniteKernel(Path.Combine(lumeniteStage, "Shaders"), shaderRoot, copied, cancellationToken);
            CopyLumeniteTextures(Path.Combine(lumeniteStage, "Textures"), textureRoot, copied, cancellationToken);
            foreach (var headerName in RequiredHeaderNames)
            {
                var source = Path.Combine(headersStage, "Shaders", headerName);
                var destination = Path.Combine(shaderRoot, headerName);
                if (!File.Exists(destination))
                    CopyShaderFile(source, destination, copied);
            }

            var presetText = BuildPresetText();
            File.WriteAllText(presetTarget, presetText, new UTF8Encoding(false));
            copied.Add(presetTarget);

            var effectPath = @".\reshade-shaders\RHI\DLSS5FeederFull\Shaders\**";
            var texturePath = @".\reshade-shaders\RHI\DLSS5FeederFull\Textures\**";
            iniResult = ReShadeIniManager.Apply(binaryDir, [effectPath], [texturePath]);
            presetMutation = SetPresetPath(iniResult.Path, @".\RHI-DLSS5-Feeder.ini");
            if (consumer == Dlss5FeederConsumer.DeepFriedChicken)
                earlyLoadMutation = EnsureLoadFromDllMainToken(iniResult.Path, DeepFriedAddonName);

            var files = new List<LinuxDeployedFile>();
            foreach (var path in copied.Distinct(StringComparer.Ordinal))
            {
                files.Add(new LinuxDeployedFile
                {
                    Path = path,
                    ExistedBeforeInstall = false,
                    Sha256 = FileHash.Sha256Hex(path),
                    AllowUserChanges = Path.GetFileName(path).Equals(PresetName, StringComparison.OrdinalIgnoreCase)
                                       || Path.GetFileName(path).Equals(DeepFriedConfigName, StringComparison.OrdinalIgnoreCase)
                });
            }

            if (sharedDx9Record is not null)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var shared = sharedDx9Record.Files.First(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    files.Add(new LinuxDeployedFile
                    {
                        Path = shared.Path,
                        ExistedBeforeInstall = false,
                        Sha256 = await FileHash.Sha256HexAsync(shared.Path, cancellationToken).ConfigureAwait(false)
                    });
                }
            }

            if (deepFriedConfigPreexisting)
            {
                files.Add(new LinuxDeployedFile
                {
                    Path = deepFriedConfigTarget,
                    ExistedBeforeInstall = true,
                    Sha256 = FileHash.Sha256Hex(deepFriedConfigTarget),
                    AllowUserChanges = true
                });
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FullStackMetadata] = "1",
                [MetaFeederVersion] = feederRelease.Version,
                ["GitHub.ReleaseTagPin"] = releaseTag ?? "",
                ["Feeder.Architecture"] = gameArchitecture.ToString(),
                ["Feeder.Host64"] = layout.RequiresHost64 ? "1" : "0",
                [MetaDx9Migration] = dx9Migration is null ? "" : JsonSerializer.Serialize(dx9Migration),
                [MetaDgVoodooVersion] = dgVoodooPayload?.Version ?? ReadDgVoodooVersion(sharedDx9Record),
                [MetaConsumer] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? "Deep Fried Chicken" : "RenoDX DLSS5",
                [MetaConsumerVersion] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? dfc!.Version : renoDxRelease!.Version,
                [MetaConsumerPackageSha256] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? FileHash.Sha256Hex(consumerPackage!) : renoDxPackageSha256!,
                ["Feeder.ConsumerConfigPreexisting"] = deepFriedConfigPreexisting ? "1" : "0",
                ["Feeder.D3DCompilerSource"] = compilerPlan.Description,
                ["Feeder.D3DCompilerOwned"] = compilerPlan.DeploySourcePath is null ? "0" : "1",
                [MetaLoadFromDllMainAdded] = earlyLoadMutation?.AddedByRhi == true ? "1" : "0",
                [MetaIniPath] = iniResult.Path,
                [MetaIniCreated] = iniResult.CreatedByRhi ? "1" : "0",
                [MetaIniEffectKeyCreated] = iniResult.EffectKeyCreated ? "1" : "0",
                [MetaIniTextureKeyCreated] = iniResult.TextureKeyCreated ? "1" : "0",
                [MetaIniEffectAdded] = JsonSerializer.Serialize(iniResult.AddedEffectPaths),
                [MetaIniTextureAdded] = JsonSerializer.Serialize(iniResult.AddedTexturePaths),
                [MetaIniHash] = FileHash.Sha256Hex(iniResult.Path),
                [MetaPresetPreviousExists] = presetMutation.PreviousKeyExisted ? "1" : "0",
                [MetaPresetPreviousValue] = presetMutation.PreviousValue ?? "",
                [MetaPresetSetValue] = presetMutation.SetValue
            };
            foreach (var (part, sha256, verified) in downloads)
                VerifiedDownload.Record(metadata, sha256, verified, part);
            if (dgVoodooPayload?.Download is { } dgVoodooDownload)
                dgVoodooDownload.RecordIn(metadata, DgVoodooPreparedPayload.DownloadPart);

            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Dlss5Feeder,
                BinaryDirectory = binaryDir,
                SourceUrl = feederRelease.AssetUrl,
                Version = feederRelease.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = files,
                DirectoriesCreatedByRhi = createdDirs,
                AdditionalWineOverrides = compilerOverride is null ? [] : [compilerOverride],
                Metadata = metadata
            }));
            // The record carries the operation id: it is the commit point.
            if (operation is not null) await operation.CommitAsync().ConfigureAwait(false);

            var configNote = deepFriedConfigPreexisting
                ? $" Existing {DeepFriedConfigName} was preserved as user-owned configuration."
                : "";
            var consumerMessage = consumer == Dlss5FeederConsumer.DeepFriedChicken
                ? $"Deep Fried Chicken {dfc!.Version}. The consumer binaries matched SHA256SUMS.txt and early ReShade loading was configured for {DeepFriedAddonName}.{configNote}"
                : $"RenoDX DLSS5 {renoDxRelease!.Version}. No Deep Fried Chicken files or LoadFromDllMain token were installed.";
            return ComponentOperationResult.Ok(
                $"DLSS5 Feeder Full {feederRelease.Version} installed with {consumerMessage}{host64CacheNote}",
                copied.ToArray());
        }
        catch (Exception ex)
        {
            // Best-effort rollback. Only paths actually copied by this install are removed; a pre-existing user-owned DFC cfg is never added to this list.
            foreach (var leftover in FileCleanup.TryDeleteAll(copied.AsEnumerable().Reverse()))
                AppLog.Warn("dlss5-feeder", $"Rollback could not remove a file this install copied: {leftover}");
            if (iniResult is not null)
                RollbackStep("ReShade.ini cleanup", () => ReShadeIniManager.Cleanup(
                    iniResult.Path,
                    iniResult.CreatedByRhi,
                    File.Exists(iniResult.Path) ? FileHash.Sha256Hex(iniResult.Path) : null,
                    iniResult.AddedEffectPaths,
                    iniResult.AddedTexturePaths,
                    iniResult.EffectKeyCreated,
                    iniResult.TextureKeyCreated));
            if (presetMutation is not null && iniResult is not null)
                RollbackStep("PresetPath restore", () => RestorePresetPath(iniResult.Path, presetMutation));
            if (earlyLoadMutation is not null && iniResult is not null)
                RollbackStep("LoadFromDllMain restore", () => RemoveLoadFromDllMainToken(iniResult.Path, earlyLoadMutation));
            if (dx9TranslationCreatedThisAttempt && dx9Migration is not null && _dx9ReShade is not null)
            {
                // Rollback runs to completion even when the install itself was cancelled.
                try { await _dx9ReShade.RestoreAsync(game, dx9Migration, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception restoreError) { AppLog.Warn("dlss5-feeder", "Rollback could not restore the DX9 ReShade translation", restoreError); }
            }
            FileCleanup.TryDeleteEmptyTree(integrationRoot);
            if (compilerOverride is not null && _overrides is not null)
                RollbackStep("d3dcompiler_47 override restore", () => _overrides.TryRestore(prefix, compilerOverride));
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full install failed: {ex.Message}");
        }
        finally
        {
            DeleteStage(stageRoot);
        }
    }

    /// <summary>One best-effort rollback step: a failure is logged (rhi.log) and the remaining steps still run.</summary>
    private static void RollbackStep(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { AppLog.Warn("dlss5-feeder", $"Rollback step failed: {what}", ex); }
    }

    /// <summary>Removes a staging folder under the cache (RHI's own temporary files); a failure is logged.</summary>
    private static void DeleteStage(string stageRoot)
    {
        try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dlss5-feeder", $"Staging folder could not be removed: {stageRoot}", ex);
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        if (record is null)
            return ComponentOperationResult.Fail("DLSS5 Feeder is not managed by RHI Linux. Removal is locked to protect an external installation.");

        if (!record.Metadata.TryGetValue(FullStackMetadata, out var full) || full != "1")
            return await _legacyAddonOnly.RemoveAsync(game, cancellationToken).ConfigureAwait(false);
        if (_overrides?.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var preserved = new List<string>();
        try
        {
            // Preflight destructive removals first. User-editable preset is preserved instead of blocking teardown.
            foreach (var file in record.Files.Where(f => !f.AllowUserChanges))
            {
                if (!File.Exists(file.Path)) continue;
                if (string.IsNullOrWhiteSpace(file.Sha256))
                    return ComponentOperationResult.Fail($"Protected file has no recorded hash: {file.Path}");
                var currentHash = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so the full Feeder teardown was not started.");
            }

            var dx9State = record.Metadata.TryGetValue(MetaDx9Migration, out var dx9Json) && !string.IsNullOrWhiteSpace(dx9Json)
                ? JsonSerializer.Deserialize<ReShadeDx9MigrationState>(dx9Json)
                : null;
            var lumaRecord = dx9State is null ? null : _records.Find(game, ManagedComponentKind.LumaFramework);
            var lumaStillUsesDx9 = dx9State is not null && ReadDx9Migration(lumaRecord, LumaFrameworkDeploymentService.MetaDx9Migration) is not null;
            var dx9ReShade = _dx9ReShade;
            Dx9TranslationReShadeCoordinator? dx9RestoreCoordinator = null;

            // Preflight the DX9 coordinator before touching ReShade.ini or any managed files.
            // Removal must be atomic from the user's perspective: if DX9 migration cannot be
            // restored safely, leave the existing Feeder configuration untouched.
            if (dx9State is not null && !lumaStillUsesDx9)
            {
                if (dx9ReShade is null)
                    return ComponentOperationResult.Fail("DX9 Feeder removal needs the ReShade migration coordinator, but it is unavailable.");
                dx9RestoreCoordinator = dx9ReShade;
            }

            if (record.Metadata.TryGetValue(MetaIniPath, out var iniPath) && !string.IsNullOrWhiteSpace(iniPath))
            {
                var mutation = new PresetPathMutation(
                    record.Metadata.GetValueOrDefault(MetaPresetPreviousExists) == "1",
                    record.Metadata.GetValueOrDefault(MetaPresetPreviousValue),
                    record.Metadata.GetValueOrDefault(MetaPresetSetValue) ?? @".\RHI-DLSS5-Feeder.ini");

                // Cleanup search-path tokens first. If Feeder had to create ReShade.ini and the
                // file stayed untouched, this can remove it entirely. Otherwise restore only the
                // PresetPath value we changed, and only while it still points at our preset.
                ReShadeIniManager.Cleanup(
                    iniPath,
                    record.Metadata.GetValueOrDefault(MetaIniCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniHash),
                    ReadStringList(record.Metadata.GetValueOrDefault(MetaIniEffectAdded)),
                    ReadStringList(record.Metadata.GetValueOrDefault(MetaIniTextureAdded)),
                    record.Metadata.GetValueOrDefault(MetaIniEffectKeyCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniTextureKeyCreated) == "1");
                RestorePresetPath(iniPath, mutation);
                if (record.Metadata.GetValueOrDefault(MetaLoadFromDllMainAdded) == "1")
                    RemoveLoadFromDllMainToken(iniPath, new IniListTokenMutation(true, DeepFriedAddonName));
            }

            if (dx9State is not null && !lumaStillUsesDx9 && dx9RestoreCoordinator is { } coordinator)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var managed = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (managed is not null && File.Exists(managed.Path)) File.Delete(managed.Path);
                }
                await coordinator.RestoreAsync(game, dx9State, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files.AsEnumerable().Reverse())
            {
                if (dx9State is not null && (Path.GetFileName(file.Path).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(file.Path).Equals("dgVoodoo.conf", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!File.Exists(file.Path)) continue;
                var currentHash = file.AllowUserChanges || file.ExistedBeforeInstall
                    ? await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false)
                    : null;
                if (ShouldPreserveOnRemove(file, currentHash))
                {
                    preserved.Add(file.Path);
                    continue;
                }
                File.Delete(file.Path);
            }

            foreach (var directory in record.DirectoriesCreatedByRhi.OrderByDescending(x => x.Length))
                FileCleanup.TryDeleteEmptyTree(directory);
            var overrideRestores = new List<WineOverrideRestoreResult>();
            if (_overrides is not null && game.PrefixPath is { } removePrefix && Directory.Exists(removePrefix))
                foreach (var wineOverride in record.AdditionalWineOverrides)
                    overrideRestores.Add(_overrides.Restore(removePrefix, wineOverride));
            _records.Remove(game, ManagedComponentKind.Dlss5Feeder);

            var suffix = preserved.Count == 0
                ? ""
                : $" Preserved {preserved.Count} user-modified file(s): {string.Join(", ", preserved.Select(Path.GetFileName))}.";
            return ComponentOperationResult.Ok("RHI-managed DLSS5 Feeder Full removed; ReShade itself was left installed." + suffix
                + WineDllOverrideService.KeptValueNotes([.. overrideRestores]));
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full removal failed: {ex.Message}");
        }
    }

    private static ReShadeDx9MigrationState? ReadDx9Migration(LinuxDeploymentRecord? record, string key)
    {
        if (record is null || !record.Metadata.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReShadeDx9MigrationState>(json); }
        catch { return null; }
    }

    private static string ReadDgVoodooVersion(LinuxDeploymentRecord? record)
    {
        if (record is null) return "";
        if (record.Component == ManagedComponentKind.LumaFramework)
            return record.Metadata.GetValueOrDefault(LumaFrameworkDeploymentService.MetaDgVoodooVersion) ?? "";
        return record.Metadata.GetValueOrDefault(MetaDgVoodooVersion) ?? "";
    }

    private static async Task<string?> ValidateSharedDx9TranslationAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
        {
            var file = record.Files.FirstOrDefault(item => Path.GetFileName(item.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file is null) return $"{name} is not tracked by the component that owns the DX9 translation.";
            if (!File.Exists(file.Path)) return $"{name} is missing at {file.Path}.";
            if (string.IsNullOrWhiteSpace(file.Sha256)) return $"{name} has no ownership hash.";
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"{name} no longer matches its managed hash.";
        }
        return null;
    }

    internal static void ValidateCachedRuntime(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException($"Cached {displayName} runtime is unavailable.");
        var length = new FileInfo(path).Length;
        if (length <= 0)
            throw new InvalidDataException($"Cached {displayName} runtime is empty; refusing to deploy a 0-byte DLL.");
        if (length < 64 * 1024)
            throw new InvalidDataException($"Cached {displayName} runtime is unexpectedly small ({length} bytes).");
    }

    internal static bool IsBlockingPreexistingFullStackTarget(string path)
        => !Path.GetFileName(path).Equals(DeepFriedConfigName, StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldPreserveOnRemove(LinuxDeployedFile file, string? currentHash)
        => file.ExistedBeforeInstall ||
           (file.AllowUserChanges &&
            !string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase));

    internal static FeederCompilerPlan ResolveCompilerPlan(
        DetectedGame game,
        PeArchitecture architecture,
        string? selectedExternalPath)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            throw new InvalidOperationException("Game binary directory is not available.");

        var existing = ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture);
        if (existing is not null)
        {
            var inPrefix = !string.IsNullOrWhiteSpace(game.PrefixPath) &&
                           Path.GetFullPath(existing).StartsWith(Path.GetFullPath(game.PrefixPath), StringComparison.Ordinal);
            return new FeederCompilerPlan(existing, null, inPrefix ? $"prefix:{existing}" : $"app-local:{existing}");
        }

        var selected = ExpandUserPath(selectedExternalPath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (!ReShadeDeploymentService.LooksLikeNativeD3DCompiler(selected, architecture))
                throw new InvalidOperationException($"The selected D3DCompiler_47.dll is missing, has the wrong PE architecture, or is not recognized as Microsoft's native HLSL compiler: {selected}");

            var target = Path.Combine(game.BinaryDirectory, "D3DCompiler_47.dll");
            var occupied = ReShadeDeploymentService.FindFileCaseInsensitive(game.BinaryDirectory, "D3DCompiler_47.dll");
            if (occupied is not null)
                throw new InvalidOperationException($"An app-local D3DCompiler_47.dll already exists but was not recognized as a usable native Microsoft compiler: {occupied}");

            return new FeederCompilerPlan(target, selected, $"selected:{selected}");
        }

        throw new InvalidOperationException("No valid compiler was found app-local or in the selected Wine/Proton prefix.");
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

    internal static ComponentOperationResult? ValidateTarget(DetectedGame game, Dlss5FeederConsumer consumer, bool requireReShade = true)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full currently targets Windows games running through Wine/Proton.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");

        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("DLSS5 Feeder Full requires a recognized x86/x64 Windows executable.");
        if (architecture == PeArchitecture.X86 && consumer != Dlss5FeederConsumer.RenoDxDlss5)
            return ComponentOperationResult.Fail("32-bit DLSS5 Feeder uses the documented RenoDX host64 consumer path. Deep Fried Chicken remains limited to the proven 64-bit layout.");

        var api = game.EffectiveGraphicsApi;
        var supported = architecture == PeArchitecture.X86
            ? api is GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX11
            : api is GraphicsApi.DirectX9 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12;
        if (!supported)
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full does not have a verified local orchestration path for {architecture} {api} yet.");
        if (requireReShade && !GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install ReShade with full add-on support first.");

        // Feeder is the no-native-DLSS workflow. Reuse the bounded file index instead of
        // doing an unbounded recursive walk through large game trees.
        var scanRoot = Directory.Exists(game.InstallPath) ? game.InstallPath : game.BinaryDirectory;
        if (GameFileIndexer.Build(scanRoot!, 8).Any(path => Path.GetFileName(path).Equals(DlssName, StringComparison.OrdinalIgnoreCase)))
            return ComponentOperationResult.Fail("A native nvngx_dlss.dll already exists in this game. Use the native-DLSS workflow instead of Feeder.");
        return null;
    }

    internal static FeederDeploymentLayout ResolveFeederLayout(
        string binaryDirectory,
        string addonDirectory,
        PeArchitecture architecture,
        Dlss5FeederConsumer consumer)
    {
        if (architecture == PeArchitecture.X86)
        {
            if (consumer != Dlss5FeederConsumer.RenoDxDlss5)
                throw new NotSupportedException("Deep Fried Chicken does not have a verified 32-bit host64 deployment contract in RHI Linux.");
            var host64 = Path.Combine(binaryDirectory, Host64DirectoryName);
            return new FeederDeploymentLayout(
                Path.Combine(addonDirectory, FeederAddon32Name),
                Path.Combine(host64, RenoDx5AddonName),
                Path.Combine(host64, DlssName),
                Path.Combine(host64, NrName),
                true,
                host64,
                Path.Combine(host64, Host64ExecutableName));
        }

        if (architecture != PeArchitecture.X64)
            throw new NotSupportedException($"DLSS5 Feeder requires x86 or x64, not {architecture}.");

        return new FeederDeploymentLayout(
            Path.Combine(addonDirectory, FeederAddonName),
            consumer == Dlss5FeederConsumer.RenoDxDlss5
                ? Path.Combine(addonDirectory, RenoDx5AddonName)
                : Path.Combine(binaryDirectory, DeepFriedAddonName),
            Path.Combine(binaryDirectory, DlssName),
            Path.Combine(binaryDirectory, NrName),
            false,
            null,
            null);
    }

    internal static string BuildPresetText()
        => "Techniques=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx\r\n" +
           "TechniqueSorting=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx\r\n" +
           "\r\n" +
           "[DLSS5_Feed.fx]\r\n" +
           "DEBUG_VIEW=0\r\n" +
           "MV_SCALE=1.000000\r\n" +
           "MV_SIGN=1.000000,1.000000\r\n" +
           "PreprocessorDefinitions=DLSS5_MV_PROVIDER=3\r\n";

    internal sealed record PresetPathMutation(bool PreviousKeyExisted, string? PreviousValue, string SetValue);

    internal static PresetPathMutation SetPresetPath(string iniPath, string value)
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var (sectionStart, sectionEnd) = FindSection(lines, "GENERAL");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("[GENERAL]");
            lines.Add($"PresetPath={value}");
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
            return new(false, null, value);
        }

        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "PresetPath");
        if (keyIndex < 0)
        {
            lines.Insert(sectionEnd, $"PresetPath={value}");
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
            return new(false, null, value);
        }

        var eq = lines[keyIndex].IndexOf('=');
        var previous = eq >= 0 ? lines[keyIndex][(eq + 1)..].Trim() : "";
        lines[keyIndex] = $"PresetPath={value}";
        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        return new(true, previous, value);
    }

    internal static void RestorePresetPath(string iniPath, PresetPathMutation mutation)
    {
        if (!File.Exists(iniPath)) return;
        var lines = File.ReadAllLines(iniPath).ToList();
        var (sectionStart, sectionEnd) = FindSection(lines, "GENERAL");
        if (sectionStart < 0) return;
        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "PresetPath");
        if (keyIndex < 0) return;
        var eq = lines[keyIndex].IndexOf('=');
        var current = eq >= 0 ? lines[keyIndex][(eq + 1)..].Trim() : "";
        if (!current.Equals(mutation.SetValue, StringComparison.OrdinalIgnoreCase))
            return; // User changed the active preset after install. Preserve that choice.

        if (mutation.PreviousKeyExisted)
            lines[keyIndex] = $"PresetPath={mutation.PreviousValue ?? ""}";
        else
            lines.RemoveAt(keyIndex);
        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
    }

    /// <summary>
    /// A release asset from the stamped download cache (<c>releases/&lt;version&gt;/&lt;asset&gt;</c>): reused while its
    /// stamp matches the asset URL, version and GitHub digest, otherwise downloaded under <paramref name="policy"/>
    /// and verified against the digest when GitHub publishes one (#26, #114).
    /// </summary>
    private Task<VerifiedDownload> EnsureCachedReleaseAssetAsync(
        GitHubResolvedReleaseAsset asset,
        TrustedDownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var entry = Path.Combine(_cacheDir, "releases", SanitizeSegment(asset.Version), Path.GetFileName(asset.AssetName));
        return TrustedDownload.EnsureVerifiedAsync(
            _http, asset.AssetUrl, entry, policy, asset.Version, asset.Sha256,
            validate: (temp, _) =>
            {
                if (new FileInfo(temp).Length < 512)
                    throw new InvalidDataException($"Downloaded asset is unexpectedly small: {asset.AssetName}");
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken);
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    private async Task<string> DownloadFeederShaderFromResolvedVersionAsync(
        string version,
        string destination,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var url in BuildFeederShaderRawUrls(version))
        {
            try
            {
                using var download = await TrustedDownload.DownloadToTempAsync(
                    _http, url, destination, DownloadSources.Dlss5Feeder, cancellationToken: cancellationToken).ConfigureAwait(false);
                ValidateDownloadedPayload(download.Path, FeederFxName, minimumSize: 256, requirePe: false);
                download.MoveTo(destination);
                return destination;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw new InvalidDataException(
            $"The Feeder release archive did not expose {FeederFxName}, and the shader could not be fetched from the matching upstream tag {version}.",
            last);
    }

    internal static IReadOnlyList<string> BuildFeederShaderRawUrls(string version)
    {
        var clean = (version ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Equals("latest", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A concrete Feeder release version is required for tagged shader fallback.", nameof(version));
        clean = clean.TrimStart('v', 'V');
        return
        [
            $"https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/v{clean}/shaders/{FeederFxName}",
            $"https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/{clean}/shaders/{FeederFxName}"
        ];
    }

    /// <returns>SHA-256 of the downloaded archive (record metadata, #26).</returns>
    private async Task<string> DownloadAndExtractShaderArchiveAsync(
        string url,
        string zipPath,
        string destination,
        CancellationToken cancellationToken)
    {
        // Branch archives (codeload.github.com) change under the same link, so they are fetched per install.
        string sha256;
        using (var download = await TrustedDownload.DownloadToTempAsync(
                   _http, url, zipPath, DownloadSources.Dlss5Feeder, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (download.Length < 1024) throw new InvalidDataException("Shader dependency archive is unexpectedly small.");
            sha256 = download.Sha256;
            download.MoveTo(zipPath);
        }

        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var normalized = entry.FullName.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var shaderIndex = Array.FindIndex(segments, x => x.Equals("Shaders", StringComparison.OrdinalIgnoreCase));
            var textureIndex = Array.FindIndex(segments, x => x.Equals("Textures", StringComparison.OrdinalIgnoreCase));
            var categoryIndex = shaderIndex >= 0 ? shaderIndex : textureIndex;
            var category = shaderIndex >= 0 ? "Shaders" : textureIndex >= 0 ? "Textures" : null;
            if (category is null || categoryIndex + 1 >= segments.Length) continue;
            var relative = Path.Combine(segments[(categoryIndex + 1)..]);
            var target = Path.GetFullPath(Path.Combine(destination, category, relative));
            var allowed = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(allowed, StringComparison.Ordinal))
                throw new InvalidDataException($"Shader dependency archive entry escapes staging directory: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, target, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        return sha256;
    }

    internal static GitHubReleaseAssetQuery CreateFeederAddonReleaseQuery()
        => new(FeederReleaseApi, ExactAssetName: FeederAddonName);

    internal static GitHubReleaseAssetQuery CreateFeederFxReleaseQuery()
        => new(FeederReleaseApi, ExactAssetName: FeederFxName);

    internal static GitHubReleaseAssetQuery CreateFeederArchiveReleaseQuery()
        => new(FeederReleaseApi, AssetNamePattern: FeederArchiveAssetPattern);

    internal static GitHubReleaseAssetQuery CreateRenoDxDlss5ReleaseQuery()
        => new(RhiRepoReleasesApi, ExactAssetName: RenoDx5AddonName, AssetNamePattern: RenoDx5AssetPattern);

    internal static string ValidateDownloadedPayload(string path, string name, long minimumSize, bool requirePe)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Downloaded payload is missing: {name}");
        if (new FileInfo(path).Length < minimumSize)
            throw new InvalidDataException($"Downloaded {name} is unexpectedly small.");
        if (requirePe && !BinarySignatureInspector.HasPeSignature(path, minimumSize))
            throw new InvalidDataException($"Downloaded {name} is not a valid PE payload.");
        return path;
    }

    internal static async Task<string> ExtractReleasePayloadByNameAsync(
        string zipPath, string name, string destination, long minimumSize, bool requirePe, CancellationToken cancellationToken)
    {
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                !string.IsNullOrEmpty(e.FullName) &&
                ArchiveLeafName(e.FullName).Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Release archive does not contain {name}.");
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, destination, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        if (new FileInfo(destination).Length < minimumSize)
            throw new InvalidDataException($"Extracted {name} is unexpectedly small.");
        if (requirePe && !BinarySignatureInspector.HasPeSignature(destination, minimumSize))
            throw new InvalidDataException($"Extracted {name} is not a valid PE payload.");
        return destination;
    }

    internal static string ArchiveLeafName(string fullName)
    {
        var normalized = (fullName ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    internal sealed record FeederDeploymentLayout(
        string FeederTarget,
        string ConsumerTarget,
        string DlssTarget,
        string NrTarget,
        bool RequiresHost64,
        string? Host64Directory,
        string? Host64ExecutableTarget);

    internal sealed record FeederCompilerPlan(string UsablePath, string? DeploySourcePath, string Description);
    internal sealed record DeepFriedChickenBundle(string AddonPath, string BridgePath, string ConfigPath, string Version);

    internal static bool IsSupportedDeepFriedChickenArchive(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAesEncryptedZipArchive(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            if (stream.Read(header) < header.Length) return false;

            // ZIP local-file header: PK\x03\x04, general-purpose flags at 6..7 and
            // compression method at 8..9. WinZip AES uses method 99 and sets encryption bit 0.
            if (header[0] != (byte)'P' || header[1] != (byte)'K' || header[2] != 0x03 || header[3] != 0x04)
                return false;

            var flags = header[6] | (header[7] << 8);
            var method = header[8] | (header[9] << 8);
            return (flags & 0x0001) != 0 && method == 99;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static DeepFriedChickenBundle ExtractDeepFriedChickenBundle(string archivePath, string destination)
        => ExtractDeepFriedChickenBundleAsync(archivePath, destination, CancellationToken.None)
            .GetAwaiter().GetResult();

    internal static async Task<DeepFriedChickenBundle> ExtractDeepFriedChickenBundleAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(archivePath))
            throw new InvalidDataException("Deep Fried Chicken package does not exist.");
        if (!IsSupportedDeepFriedChickenArchive(archivePath))
            throw new InvalidDataException("O pacote do Deep Fried Chicken deve usar o formato oficial .zip ou .7z.");

        var extension = Path.GetExtension(archivePath);
        // Some upstream releases use a .7z filename while the payload is actually
        // an AES-encrypted ZIP. Ordinary unencrypted ZIPs stay on the native ZipArchive path;
        // encrypted ZIPs and .7z-labelled packages go through 7-Zip so AES can be handled.
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !IsAesEncryptedZipArchive(archivePath))
            return await ExtractDeepFriedChickenZipBundleAsync(archivePath, destination, cancellationToken).ConfigureAwait(false);

        return await ExtractDeepFriedChicken7zBundleAsync(archivePath, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DeepFriedChickenBundle> ExtractDeepFriedChickenZipBundleAsync(string zipPath, string destination, CancellationToken cancellationToken)
    {
        // Do not reject the outer ZIP by compressed file size. Highly compressible but
        // structurally valid packages (including deterministic test fixtures) can be
        // smaller than an arbitrary archive-size threshold. The required contents are
        // validated below, and both binaries are still subject to minimum extracted-size,
        // PE-signature and SHA-256 checks before deployment.
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        var sumsEntry = FindArchiveEntryByLeaf(archive, "SHA256SUMS.txt")
            ?? throw new InvalidDataException("Deep Fried Chicken package does not contain SHA256SUMS.txt. Use the official release archive.");
        var sumsText = ReadArchiveEntryText(sumsEntry);
        var expected = ParseDeepFriedChickenManifest(sumsText, out var version);

        var addon = await ExtractVerifiedDfcEntryAsync(archive, expected, DeepFriedAddonName, Path.Combine(destination, DeepFriedAddonName), 64 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false);
        var bridge = await ExtractVerifiedDfcEntryAsync(archive, expected, DeepFriedBridgeName, Path.Combine(destination, DeepFriedBridgeName), 1024, requirePe: true, cancellationToken).ConfigureAwait(false);

        var configEntry = FindArchiveEntryByLeaf(archive, DeepFriedConfigName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedConfigName}.");
        var config = Path.Combine(destination, DeepFriedConfigName);
        await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(configEntry, config, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        ValidateDeepFriedChickenConfig(config);

        return new(addon, bridge, config, version);
    }

    private static async Task<DeepFriedChickenBundle> ExtractDeepFriedChicken7zBundleAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var extractedRoot = Path.Combine(destination, "_archive");
        Directory.CreateDirectory(extractedRoot);
        await ExtractExternalArchiveAsync(archivePath, extractedRoot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateExtractedDeepFriedChickenDirectory(extractedRoot, destination);
    }

    internal static DeepFriedChickenBundle ValidateExtractedDeepFriedChickenDirectory(string extractedRoot, string destination)
    {
        if (!Directory.Exists(extractedRoot))
            throw new InvalidDataException("Deep Fried Chicken extracted package directory does not exist.");

        var sumsPath = FindExtractedFileByLeaf(extractedRoot, "SHA256SUMS.txt")
            ?? throw new InvalidDataException("Deep Fried Chicken package does not contain SHA256SUMS.txt. Use the official release archive.");
        var sumsText = File.ReadAllText(sumsPath);
        var expected = ParseDeepFriedChickenManifest(sumsText, out var version);

        Directory.CreateDirectory(destination);
        var addonSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedAddonName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedAddonName}.");
        var bridgeSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedBridgeName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedBridgeName}.");
        var configSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedConfigName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedConfigName}.");

        var addon = CopyVerifiedDfcFile(addonSource, expected, DeepFriedAddonName, Path.Combine(destination, DeepFriedAddonName), 64 * 1024, requirePe: true);
        var bridge = CopyVerifiedDfcFile(bridgeSource, expected, DeepFriedBridgeName, Path.Combine(destination, DeepFriedBridgeName), 1024, requirePe: true);
        var config = Path.Combine(destination, DeepFriedConfigName);
        AtomicFile.Copy(configSource, config);
        NormalizeReadableFile(config);
        ValidateDeepFriedChickenConfig(config);

        return new(addon, bridge, config, version);
    }

    private static IReadOnlyDictionary<string, string> ParseDeepFriedChickenManifest(string sumsText, out string version)
    {
        var versionMatch = Regex.Match(sumsText, @"(?im)^\s*Deep\s+Fried\s+Chicken\s+([^\r\n]+?)\s*$");
        if (!versionMatch.Success)
            throw new InvalidDataException("Deep Fried Chicken SHA256SUMS.txt does not contain a recognizable release version.");
        version = versionMatch.Groups[1].Value.Trim();

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(sumsText, @"(?im)^\s*([0-9A-F]{64})\s+([^\s]+)\s*$"))
            expected[ArchiveLeafName(match.Groups[2].Value)] = match.Groups[1].Value.ToUpperInvariant();

        if (!expected.ContainsKey(DeepFriedAddonName) || !expected.ContainsKey(DeepFriedBridgeName))
            throw new InvalidDataException("Deep Fried Chicken SHA256SUMS.txt does not authenticate the required add-on and NGX bridge.");
        return expected;
    }

    private static void ValidateDeepFriedChickenConfig(string configPath)
    {
        var configText = File.ReadAllText(configPath);
        if (!Regex.IsMatch(configText, @"(?im)^\s*arm\s*=\s*1\s*$"))
            throw new InvalidDataException("Deep Fried Chicken config is not armed (expected arm=1 in the official package).");
    }

    private static string CopyVerifiedDfcFile(
        string source,
        IReadOnlyDictionary<string, string> expected,
        string name,
        string destination,
        long minimumSize,
        bool requirePe)
    {
        var actual = AtomicFile.Copy(source, destination);
        NormalizeReadableFile(destination);
        ValidateDownloadedPayload(destination, name, minimumSize, requirePe);
        if (!actual.Equals(expected[name], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Deep Fried Chicken package failed SHA-256 verification for {name}.");
        return destination;
    }

    private static string? FindExtractedFileByLeaf(string root, string name)
        => SafeFileEnumeration.Files(root)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A .7z (or AES-encrypted ZIP) Deep Fried Chicken package through the shared host-tool runner (time limit,
    /// process-tree kill, link check; #37). An AES package gets its public upstream password on stdin, never on
    /// the command line (visible to other local processes through /proc), and therefore needs 7-Zip.
    /// </summary>
    private static Task ExtractExternalArchiveAsync(
        string archivePath,
        string outputDirectory,
        CancellationToken cancellationToken)
        => ExternalArchiveRunner.ExtractAsync(
            archivePath, outputDirectory, cancellationToken,
            password: IsAesEncryptedZipArchive(archivePath) ? DeepFriedChickenArchivePassword : null,
            timeout: DeepFriedChickenExtractionTimeout);

    private static async Task<string> ExtractVerifiedDfcEntryAsync(
        ZipArchive archive,
        IReadOnlyDictionary<string, string> expected,
        string name,
        string destination,
        long minimumSize,
        bool requirePe,
        CancellationToken cancellationToken)
    {
        var entry = FindArchiveEntryByLeaf(archive, name)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {name}.");
        await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, destination, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        ValidateDownloadedPayload(destination, name, minimumSize, requirePe);
        var actual = await FileHash.Sha256HexAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(expected[name], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Deep Fried Chicken package failed SHA-256 verification for {name}.");
        return destination;
    }

    private static ZipArchiveEntry? FindArchiveEntryByLeaf(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(entry.FullName) &&
            ArchiveLeafName(entry.FullName).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ReadArchiveEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal sealed record IniListTokenMutation(bool AddedByRhi, string Token);

    internal static IniListTokenMutation EnsureLoadFromDllMainToken(string iniPath, string token)
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var (sectionStart, sectionEnd) = FindSection(lines, "ADDON");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("[ADDON]");
            lines.Add($"LoadFromDllMain={token}");
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
            return new(true, token);
        }

        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "LoadFromDllMain");
        if (keyIndex < 0)
        {
            lines.Insert(sectionEnd, $"LoadFromDllMain={token}");
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
            return new(true, token);
        }

        var eq = lines[keyIndex].IndexOf('=');
        var values = SplitIniList(eq >= 0 ? lines[keyIndex][(eq + 1)..] : "");
        if (values.Any(value => value.Equals(token, StringComparison.OrdinalIgnoreCase)))
            return new(false, token);
        values.Add(token);
        lines[keyIndex] = $"LoadFromDllMain={string.Join(',', values)}";
        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        return new(true, token);
    }

    internal static void RemoveLoadFromDllMainToken(string iniPath, IniListTokenMutation mutation)
    {
        if (!mutation.AddedByRhi || !File.Exists(iniPath)) return;
        var lines = File.ReadAllLines(iniPath).ToList();
        var (sectionStart, sectionEnd) = FindSection(lines, "ADDON");
        if (sectionStart < 0) return;
        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "LoadFromDllMain");
        if (keyIndex < 0) return;
        var eq = lines[keyIndex].IndexOf('=');
        var values = SplitIniList(eq >= 0 ? lines[keyIndex][(eq + 1)..] : "");
        var removed = values.RemoveAll(value => value.Equals(mutation.Token, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (values.Count == 0)
            lines.RemoveAt(keyIndex);
        else
            lines[keyIndex] = $"LoadFromDllMain={string.Join(',', values)}";
        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
    }

    private static List<string> SplitIniList(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void EnsureLumeniteLayout(string stage)
    {
        var shaderRoot = Path.Combine(stage, "Shaders");
        if (!Directory.Exists(shaderRoot) || !SafeFileEnumeration.Files(shaderRoot, "lumenite_Kernel.fx").Any())
            throw new InvalidDataException("LumeniteFX mainline archive does not contain lumenite_Kernel.fx.");
    }

    private static void EnsureHeaderLayout(string stage)
    {
        foreach (var name in RequiredHeaderNames)
        {
            var direct = Path.Combine(stage, "Shaders", name);
            if (!File.Exists(direct))
            {
                var found = Directory.Exists(stage)
                    ? SafeFileEnumeration.Files(stage, name).FirstOrDefault()
                    : null;
                if (found is null) throw new InvalidDataException($"ReShade shader framework header is missing: {name}");
                Directory.CreateDirectory(Path.Combine(stage, "Shaders"));
                AtomicFile.Copy(found, direct);
            }
        }
    }

    private static async Task CopyOwnedAsync(string source, string target, List<string> copied, CancellationToken cancellationToken)
    {
        if (File.Exists(target)) throw new IOException($"Protected target already exists: {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Verified durable copy (full re-read hash, never length only); never replaces a file that appeared meanwhile.
        await AtomicFile.CopyAsync(source, target, cancellationToken: cancellationToken, overwrite: false).ConfigureAwait(false);
        NormalizeReadableFile(target);
        copied.Add(target);
    }

    private static void CopyLumeniteKernel(string sourceRoot, string targetRoot, List<string> copied, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
            throw new InvalidDataException("LumeniteFX shader directory is missing.");

        var kernel = SafeFileEnumeration.Files(sourceRoot, "lumenite_Kernel.fx").FirstOrDefault()
            ?? throw new InvalidDataException("LumeniteFX kernel shader is missing.");
        CopyShaderFile(kernel, Path.Combine(targetRoot, "lumenite_Kernel.fx"), copied);

        var includeRoot = Path.Combine(sourceRoot, "include");
        if (!Directory.Exists(includeRoot))
            throw new InvalidDataException("LumeniteFX include directory is missing.");
        foreach (var source in SafeFileEnumeration.Files(includeRoot, "*.fxh"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, source);
            CopyShaderFile(source, Path.Combine(targetRoot, relative), copied);
        }
    }

    private static void CopyLumeniteTextures(string sourceRoot, string targetRoot, List<string> copied, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var source in SafeFileEnumeration.Files(sourceRoot, "lumenite_*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, source);
            CopyShaderFile(source, Path.Combine(targetRoot, relative), copied);
        }
    }

    private static void CopyShaderFile(string source, string target, List<string> copied)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) throw new IOException($"Protected target already exists: {target}");
        AtomicFile.Copy(source, target);
        NormalizeReadableFile(target);
        copied.Add(target);
    }

    private static void NormalizeReadableFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dlss5-feeder", $"Could not make {path} readable (0644)", ex);
        }
    }

    private static IReadOnlyList<string> ReadStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal)) { end = j; break; }
            return (i, end);
        }
        return (-1, -1);
    }

    private static int FindKey(IReadOnlyList<string> lines, int start, int end, string key)
    {
        for (var i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            if (trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
