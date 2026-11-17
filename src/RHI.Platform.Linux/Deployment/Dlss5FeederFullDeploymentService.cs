using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using static RHI.Platform.Linux.Deployment.DeepFriedChickenPackageVerifier;
using static RHI.Platform.Linux.Deployment.FeederLayoutPlanner;
using static RHI.Platform.Linux.Deployment.ReShadeIniTokenEditor;

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
    private const string FeederReleaseApi = "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1";
    private const string FeederArchiveAssetPattern = @"^DLSS5-Feeder-.*\.zip$";
    private const string RhiRepoReleasesApi = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    private const string RenoDx5AssetPattern = @"^renodx-dlss5_.*\.zip$";
    internal const string FeederFxName = "DLSS5_Feed.fx";
    private const string LumeniteUrl = "https://codeload.github.com/umar-afzaal/LumeniteFX/zip/refs/heads/mainline";
    private const string ReShadeHeadersUrl = "https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip";

    internal const string FeederAddonName = "dlss5-feed.addon64";
    internal const string FeederAddon32Name = "dlss5-feed.addon32";
    internal const string Host64DirectoryName = "host64";
    internal const string Host64ExecutableName = "dlss5-feed-host64.exe";
    internal const string DeepFriedAddonName = "deep-fried-chicken.addon64";
    internal const string DeepFriedBridgeName = "deep-fried-chicken-nvngx.dll";
    internal const string DeepFriedConfigName = "deep-fried-chicken.cfg";
    internal const string RenoDx5AddonName = "renodx-dlss5.addon64";
    internal const string RenoDxLegacyAddonName = "renodx-dlss.addon64";
    internal const string AlexToolkitAddonName = "alexs-toolkit.addon64";
    internal const string Dlss5BridgeAddonName = "dlss5-bridge.addon64";
    internal const string Dlss5Dx11BridgeAddonName = "dlss5-dx11-bridge.addon64";
    internal const string DlssName = "nvngx_dlss.dll";
    internal const string NrName = "nvngx_dlssnr.dll";
    internal const string PresetName = "RHI-DLSS5-Feeder.ini";
    internal const string IntegrationRootName = "DLSS5FeederFull";
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
    // w14/nr (#275): Feeder 2.0's motion vectors and the preset it was added to.
    internal const string MetaMvProvider = "Feeder.MvProvider";
    internal const string MetaPresetPath = "Feeder.PresetPath";
    internal const string MetaPresetCreated = "Feeder.PresetCreated";
    internal const string MetaPresetChange = "Feeder.PresetChange";
    private const string MetaConsumer = "Feeder.Consumer";
    private const string MetaConsumerVersion = "Feeder.ConsumerVersion";
    private const string MetaConsumerPackageSha256 = "Feeder.ConsumerPackageSha256";
    private const string MetaLoadFromDllMainAdded = "Feeder.LoadFromDllMainAdded";
    internal const string MetaDx9Migration = "Feeder.Dx9ReShadeMigration";
    internal const string MetaDgVoodooVersion = "Feeder.DgVoodooVersion";

    internal static readonly string[] RequiredHeaderNames = ["ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh"];
    internal static readonly TimeSpan DeepFriedChickenExtractionTimeout = TimeSpan.FromMinutes(2);

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

    /// <summary>w11/nr-gpu: the DLSS service this stack stages from (the NR seam points its card at the host's).</summary>
    internal DlssManagerService Dlss => _dlss;

    /// <summary>w11 integration: the journal its swaps run on (Update All's NR move asks it for a hold first).</summary>
    internal DeploymentJournal? Journal => _journal;

    /// <summary>Swaps the Feeder-owned nvngx_dlssnr.dll to a manifest version in place (upstream 2.7.6).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder", version, cancellationToken, _journal);

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
                }, cancellationToken, _journal, game).ConfigureAwait(false);
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
        if (NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } nrOwner) // OptiScaler DLSS NR or F5 DLSSNR (#157)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));

        var preflight = ValidateTarget(game, consumer);
        if (preflight is not null) return preflight;

        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full needs an initialized Wine/Proton prefix. Start the game once first.");
        if (_overrides?.EditRefusal(prefix) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing DLSS5 Feeder Full. Both workflows own the DLSS/Streamline stack and must not be stacked.");
        // w7 follow-up B29: a DirectX 9 game's Feeder needs the dgVoodoo2 translation, which DX9 HDR's d3d9.dll rules out:
        // refused here, before any download or write.
        if (game.EffectiveGraphicsApi == GraphicsApi.DirectX9 && Dx9TranslationReShadeCoordinator.DxvkHdrRefusal(_records, game) is { } dxvkHdr)
            return ComponentOperationResult.Fail(dxvkHdr);
        var gameArchitecture = GameArchitecture.Of(game);
        FeederCompilerPlan compilerPlan;
        try
        {
            compilerPlan = ResolveCompilerPlan(game, gameArchitecture, d3dCompiler47Path);
        }
        catch (Exception ex)
        {
            var installHint = game.SteamAppId.HasValue
                ? "Use the 'Install via Protontricks' D3DCompiler button on the ReShade card in Game Overrides, or browse a native Microsoft D3DCompiler_47.dll."
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

        var (targets, targetRefusal) = PlanInstall(game, gameArchitecture, consumer);
        if (targetRefusal is not null) return ComponentOperationResult.Fail(targetRefusal);

        Directory.CreateDirectory(_cacheDir);
        // #23/#169: one journaled operation for every game file, ReShade.ini edit and Wine override of the install; the
        // Feeder record carrying its id is the commit point, so a crash before it is rolled back at the next start.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "DLSS5 Feeder install: " + game.Name, game, ManagedComponentKind.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var stageRoot = Path.Combine(_cacheDir, $"stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);
        try
        {
            FeederStaged staged;
            try
            {
                staged = await StageAsync(game, consumer, consumerPackage, releaseTag, neuralRenderingDllVersion, targets!, gameArchitecture, stageRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"DLSS5 Feeder Full install failed: {ex.Message}"); // nothing in the game changed yet
            }
            return await PlaceAsync(scope, game, prefix, consumer, compilerPlan, targets!, staged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteStage(stageRoot);
        }
    }

    /// <summary>Everything an install deploys, downloaded, extracted and checked in the stage folder (nothing in the game yet).</summary>
    private sealed record FeederStaged(
        GitHubResolvedReleaseAsset FeederRelease,
        string FeederAddon,
        string? Host64Executable,
        string FeederFx,
        DeepFriedChickenBundle? Dfc,
        GitHubResolvedReleaseAsset? RenoDxRelease,
        string? RenoDxAddon,
        string? RenoDxPackageSha256,
        string CachedSr,
        string CachedNr,
        string? LumeniteStage, // w14/nr (#275): null with Feeder 2.0's built-in motion vectors
        string HeadersStage,
        ReShadeDx9MigrationState? SharedDx9State,
        LinuxDeploymentRecord? SharedDx9Record,
        DgVoodooPreparedPayload? DgVoodoo,
        ReShadeStagedDll? HostReShade,
        IReadOnlyList<(string? Part, string Sha256, bool DigestVerified)> Downloads,
        string? ReleaseTag,
        string? ConsumerPackage,
        string? CachedNrVersion = null, // #230: the manifest version of the NR DLL placed (recorded as NeuralRendering.DllVersion)
        int MvProvider = FeederPreset.LumeniteProvider); // w14/nr (#275): DLSS5_MV_PROVIDER for the game's preset

    // The network half of an install (before the write gate): every download and extraction.
    private async Task<FeederStaged> StageAsync(
        DetectedGame game, Dlss5FeederConsumer consumer, string? consumerPackage, string? releaseTag, string? neuralRenderingDllVersion,
        FeederInstallTargets targets, PeArchitecture gameArchitecture, string stageRoot, CancellationToken cancellationToken)
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
            dfc = await ExtractDeepFriedChickenBundleAsync(consumerPackage!, Path.Combine(stageRoot, "deep-fried-chicken"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Experimental consumer path restored for controlled A/B testing. Resolve only the
            // dedicated RenoDX DLSS5 neural consumer; ordinary game-specific RenoDX HDR add-ons
            // remain independent and may coexist because they are not classified as this consumer.
            renoDxRelease = await _releaseResolver.ResolveAsync(CreateRenoDxDlss5ReleaseQuery(), cancellationToken).ConfigureAwait(false);
            var consumerDownload = await EnsureCachedReleaseAssetAsync(renoDxRelease, DownloadSources.RhiRepository, cancellationToken).ConfigureAwait(false);
            renoDxPackageSha256 = consumerDownload.Sha256;
            downloads.Add((ConsumerDownloadPart, consumerDownload.Sha256, consumerDownload.DigestVerified));
            renoDxAddon = renoDxRelease.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? await ExtractReleasePayloadByNameAsync(consumerDownload.Path, RenoDx5AddonName, Path.Combine(stageRoot, RenoDx5AddonName), minimumSize: 32 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false)
                : ValidateDownloadedPayload(consumerDownload.Path, RenoDx5AddonName, minimumSize: 32 * 1024, requirePe: true);
        }

        // Current upstream publishes a single DLSS5-Feeder-*.zip release asset.
        // The archive is produced by Windows tooling, so entries may carry either '/' or '\\'
        // separators. Linux must normalize both before matching leaf names. The tagged raw
        // shader is a conservative fallback only when an archive genuinely omits DLSS5_Feed.fx.
        var feederRelease = await _releaseResolver.ResolveAsync(CreateFeederArchiveReleaseQuery(), releaseTag, cancellationToken).ConfigureAwait(false);
        var feederDownload = await EnsureCachedReleaseAssetAsync(feederRelease, DownloadSources.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
        var feederZip = feederDownload.Path;
        downloads.Add((null, feederDownload.Sha256, feederDownload.DigestVerified));
        var feederAssetName = gameArchitecture == PeArchitecture.X86 ? FeederAddon32Name : FeederAddonName;
        var feederAddon = await ExtractReleasePayloadByNameAsync(
            feederZip, feederAssetName, Path.Combine(stageRoot, feederAssetName), minimumSize: 64 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false);
        string? host64Executable = null;
        if (targets.Layout.RequiresHost64)
            host64Executable = await ExtractReleasePayloadByNameAsync(
                feederZip, Host64ExecutableName, Path.Combine(stageRoot, Host64ExecutableName), minimumSize: 64 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false);

        string feederFx;
        try
        {
            feederFx = await ExtractReleasePayloadByNameAsync(
                feederZip, FeederFxName, Path.Combine(stageRoot, FeederFxName), minimumSize: 256, requirePe: false, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(FeederFxName, StringComparison.OrdinalIgnoreCase))
        {
            feederFx = await DownloadFeederShaderFromResolvedVersionAsync(feederRelease.Version, Path.Combine(stageRoot, FeederFxName), cancellationToken).ConfigureAwait(false);
            downloads.Add((FeederShaderDownloadPart, FileHash.Sha256Hex(feederFx), false));
        }

        var cachedSr = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.SuperResolution, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current RHI DLSS manifest does not provide a Super Resolution runtime.");
        // #229: no pin = "Latest" made for this card; #230: the version placed is recorded so Update All can tell it later.
        var cachedNrVersion = await _dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, neuralRenderingDllVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current RHI DLSS manifest does not provide a Neural Rendering runtime.");
        var cachedNr = cachedNrVersion.Path;
        ValidateCachedRuntime(cachedSr, DlssName);
        ValidateCachedRuntime(cachedNr, NrName);

        // w14/nr (#275): the motion-vector provider, as Feeder 2.0's installer chooses it: the one the game's preset already
        // names, else the estimator built into DLSS5_Feed.fx (2.0.0+), else LumeniteFX. LumeniteFX is downloaded only for it.
        var mvProvider = FeederPreset.ChooseProvider(ReShadePresetFile.Read(targets.Preset), await File.ReadAllTextAsync(feederFx, cancellationToken).ConfigureAwait(false));
        string? lumeniteStage = null;
        if (FeederPreset.NeedsLumenite(mvProvider))
        {
            lumeniteStage = Path.Combine(stageRoot, "lumenite");
            downloads.Add((LumeniteDownloadPart,
                await DownloadAndExtractShaderArchiveAsync(LumeniteUrl, Path.Combine(stageRoot, "lumenite.zip"), lumeniteStage, cancellationToken).ConfigureAwait(false),
                false));
            FeederShaderStackInstaller.EnsureLumeniteLayout(lumeniteStage);
        }

        var headersStage = Path.Combine(stageRoot, "headers");
        downloads.Add((HeadersDownloadPart,
            await DownloadAndExtractShaderArchiveAsync(ReShadeHeadersUrl, Path.Combine(stageRoot, "headers.zip"), headersStage, cancellationToken).ConfigureAwait(false),
            false));
        FeederShaderStackInstaller.EnsureHeaderLayout(headersStage);

        ReShadeDx9MigrationState? sharedDx9 = null;
        LinuxDeploymentRecord? sharedDx9Record = null;
        DgVoodooPreparedPayload? dgVoodooPayload = null;
        if (game.EffectiveGraphicsApi == GraphicsApi.DirectX9)
        {
            var lumaRecord = _records.Find(game, ManagedComponentKind.LumaFramework);
            sharedDx9 = ReadDx9Migration(lumaRecord, LumaFrameworkDeploymentService.MetaDx9Migration);
            if (sharedDx9 is not null)
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
                var dgConfigTarget = Path.Combine(targets.BinaryDirectory, "dgVoodoo.conf");
                if (File.Exists(dgConfigTarget))
                    throw new IOException($"External dgVoodoo.conf already exists: {dgConfigTarget}. RHI Linux will not overwrite it automatically.");
            }
        }

        // A 32-bit game's host64 helper runs a 64-bit ReShade: staged (possibly downloaded from reshade.me) here with
        // the other downloads, before the gate (w2 merge review, minor 1: it used to download after the first change).
        ReShadeStagedDll? hostReShade = null;
        if (targets.Layout.RequiresHost64)
        {
            if (_reShade is null)
                throw new InvalidOperationException("32-bit DLSS5 Feeder needs the ReShade service to stage a 64-bit host64 ReShade runtime.");
            hostReShade = await _reShade.EnsureLatestStagedAsync(PeArchitecture.X64, cancellationToken).ConfigureAwait(false);
        }

        return new FeederStaged(feederRelease, feederAddon, host64Executable, feederFx, dfc, renoDxRelease, renoDxAddon, renoDxPackageSha256,
            cachedSr, cachedNr, lumeniteStage, headersStage, sharedDx9, sharedDx9Record, dgVoodooPayload, hostReShade, downloads, releaseTag, consumerPackage,
            cachedNrVersion.Version, mvProvider);
    }

    // The file half of an install (after the write gate): every change goes through the scope; the Feeder record (and,
    // after a DX9 translation, ReShade's) saved in one ledger write is the commit point.
    private async Task<ComponentOperationResult> PlaceAsync(
        DeploymentScope scope, DetectedGame game, string prefix, Dlss5FeederConsumer consumer, FeederCompilerPlan compilerPlan,
        FeederInstallTargets t, FeederStaged staged, CancellationToken cancellationToken)
    {
        // Every download and extraction is done: from the first game file on, the install runs to completion or full
        // rollback (Cancel is refused).
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var copied = new List<string>();
        var createdDirs = new List<string>();
        WineOverrideRecord? compilerOverride = null;
        Dx9MigrationInside? dx9 = null;
        string host64CacheNote;
        LinuxDeploymentRecord record;
        try
        {
            if (compilerPlan.DeploySourcePath is not null)
                await FeederShaderStackInstaller.PlaceAsync(scope, compilerPlan.DeploySourcePath, compilerPlan.UsablePath, copied).ConfigureAwait(false);
            if (_overrides is not null)
                compilerOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, prefix, "d3dcompiler_47", Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
            if (staged.DgVoodoo is not null)
                dx9 = await _dx9ReShade!.MigrateInsideAsync(game, scope, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(t.AddonDirectory);
            Directory.CreateDirectory(t.ShaderRoot);
            Directory.CreateDirectory(t.TextureRoot);
            createdDirs.Add(t.IntegrationRoot);
            host64CacheNote = "";
            if (staged.HostReShade is not null)
            {
                Directory.CreateDirectory(t.Layout.Host64Directory!);
                createdDirs.Add(t.Layout.Host64Directory!);
                host64CacheNote = Host64CacheNote(staged.HostReShade.FromCache);
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.Host64Executable!, t.Layout.Host64ExecutableTarget!, copied).ConfigureAwait(false);
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.HostReShade.DllPath, Path.Combine(t.Layout.Host64Directory!, "dxgi.dll"), copied).ConfigureAwait(false);
            }
            if (staged.DgVoodoo is not null)
            {
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.DgVoodoo.D3D9Path, Path.Combine(t.BinaryDirectory, "D3D9.dll"), copied).ConfigureAwait(false);
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.DgVoodoo.ConfigPath, Path.Combine(t.BinaryDirectory, "dgVoodoo.conf"), copied).ConfigureAwait(false);
            }

            await FeederShaderStackInstaller.PlaceAsync(scope, staged.FeederAddon, t.Layout.FeederTarget, copied).ConfigureAwait(false);
            if (consumer == Dlss5FeederConsumer.DeepFriedChicken)
            {
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.Dfc!.AddonPath, t.DeepFriedAddon, copied).ConfigureAwait(false);
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.Dfc.BridgePath, t.DeepFriedBridge, copied).ConfigureAwait(false);
                if (!t.DeepFriedConfigPreexisting)
                    await FeederShaderStackInstaller.PlaceAsync(scope, staged.Dfc.ConfigPath, t.DeepFriedConfig, copied).ConfigureAwait(false);
            }
            else
            {
                await FeederShaderStackInstaller.PlaceAsync(scope, staged.RenoDxAddon!, t.Layout.ConsumerTarget, copied).ConfigureAwait(false);
            }
            await FeederShaderStackInstaller.PlaceAsync(scope, staged.CachedSr, t.Layout.DlssTarget, copied).ConfigureAwait(false);
            await FeederShaderStackInstaller.PlaceAsync(scope, staged.CachedNr, t.Layout.NrTarget, copied).ConfigureAwait(false);
            await FeederShaderStackInstaller.InstallAsync(scope, staged.FeederFx, staged.LumeniteStage, staged.HeadersStage, t.ShaderRoot, t.TextureRoot, copied).ConfigureAwait(false);

            // w14/nr (#275): added to the game's own preset (merged), never a preset of RHI's own that replaces it.
            var presetWrite = await MergePresetAsync(scope, game, t, staged.MvProvider, copied).ConfigureAwait(false);

            // ReShade.ini: tracked before its three edits and declared after them, so a rollback (or the next start after a
            // crash) puts back exactly the file as it was. #213: the file the writes land in is tracked — a ReShade.ini
            // linked to a shared file (~/.config/ReShade) is followed by the search-path write — never the link, so a rollback
            // keeps the link and puts the shared file's bytes back.
            var iniTarget = ReShadeIniManager.ResolveWriteTarget(ReShadeIniManager.ResolveIniPath(t.BinaryDirectory), t.BinaryDirectory, gameRoot: null);
            await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
            var iniResult = ReShadeIniManager.Apply(t.BinaryDirectory, [EffectSearchPath], [TextureSearchPath]);
            var earlyLoadMutation = consumer == Dlss5FeederConsumer.DeepFriedChicken ? EnsureLoadFromDllMainToken(iniResult.Path, DeepFriedAddonName) : null;
            await scope.DeclareWrittenContentAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);

            record = BuildRecord(game, consumer, compilerPlan, t, staged, dx9?.State ?? staged.SharedDx9State, copied, createdDirs, compilerOverride,
                iniResult, presetWrite, earlyLoadMutation);
            // The record carries the operation id: it is the commit point (with ReShade's, after a DX9 translation, in the same write).
            _records.Commit(dx9 is null ? [scope.Stamp(record)] : [scope.Stamp(record), dx9.ReShadeRecord]);
        }
        catch (Exception ex)
        {
            // Every file, ReShade.ini and (journaled) every Wine override back from the scope; the overrides once more
            // for an unjournaled run (a no-op where the rollback already put the value back).
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // #204: the commit write (with ReShade's translated record, after a DX9 translation) landed and then threw:
            // both records are kept and nothing is undone, the rename and its overrides included.
            if (rollback.Committed) return DeploymentScope.Landed("DLSS5 Feeder Full", changedFiles: [.. copied]);
            if (rollback.RolledBack)
            {
                if (compilerOverride is not null && _overrides is not null)
                    RollbackStep("d3dcompiler_47 override restore", () => _overrides.TryRestore(prefix, compilerOverride));
                if (dx9 is not null) _dx9ReShade!.UndoInside(game, dx9);
                FileCleanup.TryDeleteEmptyTree(t.IntegrationRoot);
                if (t.Layout.Host64Directory is { } host64) FileCleanup.TryDeleteEmptyTree(host64);
            }
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full install failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);

        var configNote = t.DeepFriedConfigPreexisting
            ? $" Existing {DeepFriedConfigName} was preserved as user-owned configuration."
            : "";
        var consumerMessage = consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? $"Deep Fried Chicken {staged.Dfc!.Version}. The consumer binaries matched SHA256SUMS.txt and early ReShade loading was configured for {DeepFriedAddonName}.{configNote}"
            : $"RenoDX DLSS5 {staged.RenoDxRelease!.Version}. No Deep Fried Chicken files or LoadFromDllMain token were installed.";
        return ComponentOperationResult.Ok(
            $"DLSS5 Feeder Full {staged.FeederRelease.Version} installed with {consumerMessage}{host64CacheNote}",
            copied.ToArray());
    }

    private const string EffectSearchPath = @".\reshade-shaders\RHI\DLSS5FeederFull\Shaders\**";
    private const string TextureSearchPath = @".\reshade-shaders\RHI\DLSS5FeederFull\Textures\**";

    private static LinuxDeploymentRecord BuildRecord(
        DetectedGame game, Dlss5FeederConsumer consumer, FeederCompilerPlan compilerPlan, FeederInstallTargets t, FeederStaged staged,
        ReShadeDx9MigrationState? dx9Migration, List<string> copied, List<string> createdDirs, WineOverrideRecord? compilerOverride,
        ReShadeIniApplyResult iniResult, FeederPresetWrite presetWrite, IniListTokenMutation? earlyLoadMutation)
    {
        var files = copied.Distinct(StringComparer.Ordinal).Select(path => new LinuxDeployedFile
        {
            Path = path,
            ExistedBeforeInstall = false,
            Sha256 = FileHash.Sha256Hex(path),
            AllowUserChanges = string.Equals(path, presetWrite.Path, StringComparison.Ordinal)
                               || Path.GetFileName(path).Equals(DeepFriedConfigName, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        if (staged.SharedDx9Record is not null)
        {
            foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
            {
                var shared = staged.SharedDx9Record.Files.First(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                files.Add(new LinuxDeployedFile { Path = shared.Path, ExistedBeforeInstall = false, Sha256 = FileHash.Sha256Hex(shared.Path) });
            }
        }
        if (t.DeepFriedConfigPreexisting)
            files.Add(new LinuxDeployedFile { Path = t.DeepFriedConfig, ExistedBeforeInstall = true, Sha256 = FileHash.Sha256Hex(t.DeepFriedConfig), AllowUserChanges = true });

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FullStackMetadata] = "1",
            [MetaFeederVersion] = staged.FeederRelease.Version,
            [NeuralRenderingDllSwap.MetaDllVersion] = staged.CachedNrVersion ?? "", // #230
            ["GitHub.ReleaseTagPin"] = staged.ReleaseTag ?? "",
            ["Feeder.Architecture"] = GameArchitecture.Of(game).ToString(),
            ["Feeder.Host64"] = t.Layout.RequiresHost64 ? "1" : "0",
            [MetaDx9Migration] = dx9Migration is null ? "" : JsonSerializer.Serialize(dx9Migration),
            [MetaDgVoodooVersion] = staged.DgVoodoo?.Version ?? ReadDgVoodooVersion(staged.SharedDx9Record),
            [MetaConsumer] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? "Deep Fried Chicken" : "RenoDX DLSS5",
            [MetaConsumerVersion] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? staged.Dfc!.Version : staged.RenoDxRelease!.Version,
            [MetaConsumerPackageSha256] = consumer == Dlss5FeederConsumer.DeepFriedChicken ? FileHash.Sha256Hex(staged.ConsumerPackage!) : staged.RenoDxPackageSha256!,
            ["Feeder.ConsumerConfigPreexisting"] = t.DeepFriedConfigPreexisting ? "1" : "0",
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
            // w14/nr (#275): the preset Feeder was added to; a preset RHI created is one of the record's files (removed with
            // Feeder while unchanged), a merged one is put back with FeederPreset.Unmerge. No PresetPath change any more.
            [MetaMvProvider] = staged.MvProvider.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [MetaPresetPath] = presetWrite.Path,
            [MetaPresetCreated] = presetWrite.Created ? "1" : "0",
            [MetaPresetChange] = presetWrite.Change is null ? "" : JsonSerializer.Serialize(presetWrite.Change)
        };
        foreach (var (part, sha256, verified) in staged.Downloads)
            VerifiedDownload.Record(metadata, sha256, verified, part);
        if (staged.DgVoodoo?.Download is { } dgVoodooDownload)
            dgVoodooDownload.RecordIn(metadata, DgVoodooPreparedPayload.DownloadPart);

        return new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = t.BinaryDirectory,
            SourceUrl = staged.FeederRelease.AssetUrl,
            Version = staged.FeederRelease.Version,
            InstalledAtUtc = DateTime.UtcNow,
            Files = files,
            DirectoriesCreatedByRhi = createdDirs,
            AdditionalWineOverrides = compilerOverride is null ? [] : [compilerOverride],
            Metadata = metadata
        };
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

        ReShadeDx9MigrationState? dx9State;
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
            dx9State = record.Metadata.TryGetValue(MetaDx9Migration, out var dx9Json) && !string.IsNullOrWhiteSpace(dx9Json)
                ? JsonSerializer.Deserialize<ReShadeDx9MigrationState>(dx9Json)
                : null;
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full removal failed: {ex.Message}");
        }

        var lumaRecord = dx9State is null ? null : _records.Find(game, ManagedComponentKind.LumaFramework);
        var lumaStillUsesDx9 = dx9State is not null && ReadDx9Migration(lumaRecord, LumaFrameworkDeploymentService.MetaDx9Migration) is not null;
        // Preflight the DX9 coordinator before touching ReShade.ini or any managed files: if the DX9 migration cannot be
        // restored safely, the Feeder configuration stays untouched.
        var restoreDx9 = dx9State is not null && !lumaStillUsesDx9;
        if (restoreDx9 && _dx9ReShade is null)
            return ComponentOperationResult.Fail("DX9 Feeder removal needs the ReShade migration coordinator, but it is unavailable.");

        // #23/#169: one journaled removal (files, ReShade.ini, the DX9 translation and the overrides); taking the record
        // out of the ledger (with ReShade's restored record in the same write) is the commit point.
        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "DLSS5 Feeder removal: " + game.Name, game, ManagedComponentKind.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;

        // Cancel is honoured only up to here: the teardown never stops half-way.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var preserved = new List<string>();
        var overrideRestores = new List<(WineOverrideRecord Override, WineOverrideRestoreResult Result)>();
        LinuxDeploymentRecord? reShadeBefore = null;
        LinuxDeploymentRecord? reShadeAfter = null;
        WineOverrideRestoreResult? dx9OverrideRestore = null;
        try
        {
            if (record.Metadata.TryGetValue(MetaIniPath, out var iniPath) && !string.IsNullOrWhiteSpace(iniPath))
            {
                // Installs before w14 (#275) pointed PresetPath at RHI's own RHI-DLSS5-Feeder.ini; newer ones leave it alone.
                var mutation = record.Metadata.TryGetValue(MetaPresetSetValue, out var setValue) && !string.IsNullOrWhiteSpace(setValue)
                    ? new PresetPathMutation(
                        record.Metadata.GetValueOrDefault(MetaPresetPreviousExists) == "1",
                        record.Metadata.GetValueOrDefault(MetaPresetPreviousValue),
                        setValue)
                    : null;

                // Cleanup search-path tokens first. If Feeder had to create ReShade.ini and the
                // file stayed untouched, this can remove it entirely. Otherwise restore only the
                // PresetPath value we changed, and only while it still points at our preset.
                // #213: the file behind a linked ReShade.ini is tracked, never the link (see the install).
                var iniTarget = ReShadeIniManager.ResolveWriteTarget(iniPath, Path.GetDirectoryName(iniPath)!, gameRoot: null);
                await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
                ReShadeIniManager.Cleanup(
                    iniPath,
                    record.Metadata.GetValueOrDefault(MetaIniCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniHash),
                    ReadStringList(record.Metadata.GetValueOrDefault(MetaIniEffectAdded)),
                    ReadStringList(record.Metadata.GetValueOrDefault(MetaIniTextureAdded)),
                    record.Metadata.GetValueOrDefault(MetaIniEffectKeyCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniTextureKeyCreated) == "1");
                if (mutation is not null) RestorePresetPath(iniPath, mutation);
                if (record.Metadata.GetValueOrDefault(MetaLoadFromDllMainAdded) == "1")
                    RemoveLoadFromDllMainToken(iniPath, new IniListTokenMutation(true, DeepFriedAddonName));
                if (File.Exists(iniTarget)) await scope.DeclareWrittenContentAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
            }

            // w14/nr (#275): Feeder comes back out of a preset it was added to (the user's other effects stay).
            if (await UnmergePresetAsync(scope, game, record).ConfigureAwait(false) is { } presetNote) preserved.Add(presetNote);

            if (restoreDx9)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var managed = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (managed is not null && File.Exists(managed.Path)) await scope.DeleteAsync(managed.Path, CancellationToken.None).ConfigureAwait(false);
                }
                reShadeBefore = _records.Find(game, ManagedComponentKind.ReShade);
                (reShadeAfter, dx9OverrideRestore) = await _dx9ReShade!.RestoreInsideAsync(game, dx9State!, scope, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files.AsEnumerable().Reverse())
            {
                if (dx9State is not null && (Path.GetFileName(file.Path).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(file.Path).Equals("dgVoodoo.conf", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!File.Exists(file.Path)) continue;
                var currentHash = file.AllowUserChanges || file.ExistedBeforeInstall
                    ? await FileHash.Sha256HexAsync(file.Path, CancellationToken.None).ConfigureAwait(false)
                    : null;
                if (ShouldPreserveOnRemove(file, currentHash))
                {
                    preserved.Add(file.Path);
                    continue;
                }
                await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
            }

            if (_overrides is not null && game.PrefixPath is { } removePrefix && Directory.Exists(removePrefix))
                foreach (var wineOverride in record.AdditionalWineOverrides)
                    overrideRestores.Add((wineOverride, await _overrides.RestoreAsync(scope.Operation, removePrefix, wineOverride, CancellationToken.None).ConfigureAwait(false)));
            _records.Commit(reShadeAfter is null ? [] : [reShadeAfter], [(game, ManagedComponentKind.Dlss5Feeder)]); // the commit point
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "dlss5-feeder", alongside: reShadeBefore is null ? null : [reShadeBefore]).ConfigureAwait(false);
            // #204: the removal (with ReShade's record back at d3d9.dll) landed and then threw: it stays removed.
            if (rollback.Committed) return DeploymentScope.Landed("DLSS5 Feeder Full", removal: true);
            // The record keeps its overrides: put RHI's value back where this removal took it away (#30).
            if (rollback.RolledBack && game.PrefixPath is { } prefix)
            {
                foreach (var (wineOverride, result) in overrideRestores.Where(o => o.Result.Outcome == WineOverrideRestoreOutcome.Restored))
                    _overrides?.TryReapply(prefix, wineOverride);
                if (dx9OverrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored && reShadeBefore?.WineOverride is { } translated)
                    _overrides?.TryReapply(prefix, translated);
            }
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full removal failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);

        // Past the commit point: emptied folders RHI created go (best effort; never rolled back).
        foreach (var directory in record.DirectoriesCreatedByRhi.OrderByDescending(x => x.Length))
            FileCleanup.TryDeleteEmptyTree(directory);
        var suffix = preserved.Count == 0
            ? ""
            : $" Preserved {preserved.Count} user-modified file(s): {string.Join(", ", preserved.Select(Path.GetFileName))}.";
        return ComponentOperationResult.Ok("RHI-managed DLSS5 Feeder Full removed; ReShade itself was left installed." + suffix
            + WineDllOverrideService.KeptValueNotes([.. overrideRestores.Select(o => o.Result)]));
    }

    private static ReShadeDx9MigrationState? ReadDx9Migration(LinuxDeploymentRecord? record, string key)
    {
        if (record is null || !record.Metadata.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReShadeDx9MigrationState>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("dlss5-feeder", "Stored deployment state could not be read; it is treated as absent", ex);
            return null;
        }
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

    /// <summary>
    /// w14/nr (#275): the Feeder row's description before an install (upstream 2.9.3's Feeder description: its own motion vectors
    /// and the in-game menu, Alt+C). PT-BR in the UI's presentation table.
    /// </summary>
    public const string AvailableNote =
        "DLSS5 Feeder Full: the Feeder with RenoDX DLSS5 or Deep Fried Chicken, its own SR/NR runtimes and shaders. New installs use the motion vectors built into Feeder 2.0 (no LumeniteFX download) and add the Feeder to the game's ReShade preset. Press Alt+C in the game for the Feeder menu.";

    /// <summary>w14/nr (#275): the game's ReShade.ini names its preset by a Windows drive path, which RHI cannot map to a Linux file.</summary>
    public const string PresetOutsideRefusal =
        "The game's ReShade.ini names its preset (PresetPath) by a Windows drive path, so RHI cannot add DLSS5 Feeder to it. Choose a preset in the game's folder in ReShade first.";

    /// <summary>w14/nr (#275): the Feeder row's motion-vector status (upstream 2.9.3's NR status tag), EN; PT-BR in the UI's fragment table.</summary>
    public const string BuiltInMotionVectorsLabel = "Built-in motion vectors";
    public const string LumeniteMotionVectorsLabel = "LumeniteFX motion vectors";
    public const string ProviderMotionVectorsPrefix = "Motion vectors: provider ";

    /// <summary>
    /// w14/nr (#275): which motion vectors an installed Feeder uses, read from the preset it was added to (so a switch made in
    /// Feeder's own menu, Alt+C, shows), else from the record. Installs from before Feeder 2.0 support used LumeniteFX.
    /// Null for a legacy add-on-only record.
    /// </summary>
    public static string? MotionVectorsLabel(LinuxDeploymentRecord record)
    {
        if (!record.Metadata.TryGetValue(FullStackMetadata, out var full) || full != "1") return null;
        var presetPath = record.Metadata.GetValueOrDefault(MetaPresetPath);
        if (string.IsNullOrWhiteSpace(presetPath) && !string.IsNullOrWhiteSpace(record.BinaryDirectory))
            presetPath = Path.Combine(record.BinaryDirectory, PresetName); // before #275: RHI's own preset
        int? provider = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(presetPath)) provider = FeederPreset.ReadProvider(ReShadePresetFile.Read(presetPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dlss5-feeder", $"The Feeder preset could not be read for its motion vectors: {presetPath}", ex);
        }
        provider ??= int.TryParse(record.Metadata.GetValueOrDefault(MetaMvProvider), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var recorded) ? recorded : FeederPreset.LumeniteProvider;
        return provider.Value switch
        {
            FeederPreset.BuiltInProvider => BuiltInMotionVectorsLabel,
            3 or 4 => LumeniteMotionVectorsLabel,
            _ => ProviderMotionVectorsPrefix + provider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>w14/nr (#275): the preset Feeder was added to: created by RHI (one of the record's files) or merged into (with the change made).</summary>
    internal sealed record FeederPresetWrite(string Path, bool Created, FeederPresetChange? Change);

    /// <summary>
    /// w14/nr (#275): DLSS5_Feed (and LumeniteFX's kernel before it, for providers 3/4) switched on in the game's own preset,
    /// with the provider in <c>[DLSS5_Feed.fx]</c>; a missing preset is created. Upstream 2.9.3 (<c>cdff33f</c>). Journaled:
    /// the preset's previous bytes come back on a rollback.
    /// </summary>
    private static async Task<FeederPresetWrite> MergePresetAsync(DeploymentScope scope, DetectedGame game, FeederInstallTargets t, int provider, List<string> copied)
    {
        var existing = ReShadePresetFile.Read(t.Preset);
        if (existing is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(t.Preset)!);
            await scope.PlaceBytesAsync(t.Preset, new UTF8Encoding(false).GetBytes(FeederPreset.Merge(null, provider)), CancellationToken.None).ConfigureAwait(false);
            copied.Add(t.Preset);
            return new FeederPresetWrite(t.Preset, true, null);
        }

        var target = ReShadePresetFile.WriteTarget(t.Preset, t.BinaryDirectory, game.InstallPath);
        await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
        var (merged, change) = FeederPreset.MergeTracked(existing, provider);
        if (!string.Equals(merged, existing, StringComparison.Ordinal))
            ReShadePresetFile.Write(t.Preset, merged, t.BinaryDirectory, game.InstallPath);
        await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
        return new FeederPresetWrite(t.Preset, false, change);
    }

    /// <summary>
    /// w14/nr (#275): takes Feeder back out of a preset the install merged into (journaled). Null when done or nothing to do;
    /// otherwise the line the removal reports (the preset is gone, or cannot be written), with the preset left as it is.
    /// </summary>
    private static async Task<string?> UnmergePresetAsync(DeploymentScope scope, DetectedGame game, LinuxDeploymentRecord record)
    {
        if (!record.Metadata.TryGetValue(MetaPresetChange, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        if (!record.Metadata.TryGetValue(MetaPresetPath, out var presetPath) || string.IsNullOrWhiteSpace(presetPath)) return null;
        FeederPresetChange? change;
        try { change = JsonSerializer.Deserialize<FeederPresetChange>(json); }
        catch (JsonException ex)
        {
            AppLog.Warn("dlss5-feeder", "The preset change in the Feeder record could not be read; the preset is left as it is", ex);
            return presetPath;
        }
        if (change is null || !File.Exists(presetPath)) return null;
        string target;
        try { target = ReShadePresetFile.WriteTarget(presetPath, record.BinaryDirectory, game.InstallPath); }
        catch (IOException ex)
        {
            AppLog.Warn("dlss5-feeder", "The preset Feeder was added to cannot be written; it is left as it is", ex);
            return presetPath;
        }
        await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
        var text = await File.ReadAllTextAsync(presetPath, CancellationToken.None).ConfigureAwait(false);
        var back = FeederPreset.Unmerge(text, change);
        if (!string.Equals(back, text, StringComparison.Ordinal))
            ReShadePresetFile.Write(presetPath, back, record.BinaryDirectory, game.InstallPath);
        await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
        return null;
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


    internal static void NormalizeReadableFile(string path)
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("dlss5-feeder", "Stored deployment state could not be read; it is treated as absent", ex);
            return [];
        }
    }

}
