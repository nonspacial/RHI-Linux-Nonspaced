using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Transactional Linux/Wine orchestration for upstream RHI's ShortFuse DLSS Frame Generation path.
/// The service keeps ShortFuse ownership separate from ReShade and Ultimate ASI Loader ownership:
/// ReShade remains a ReShade record, UAL remains a UAL record, and this record owns only the
/// temporary orchestration state plus ShortFuse/DLSS/Streamline files.
/// </summary>
public sealed class ShortFuseDeploymentService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string AddonFileName = "renodx-dlss.addon64";
    public const string ReleaseTagPrefix = "renodx-dlss-SF-";
    public const string ArchiveAssetPrefix = "renodx-dlss";
    public const string ReShadeAsiName = "ReShade64.asi";

    internal const string MetaReShadeOriginalPath = "ReShade.ShortFuseOriginalPayloadPath";
    internal const string MetaAutoConfigEnabled = "ShortFuse.AutoConfigEnabled";
    internal const string MetaUalInstalledByShortFuse = "ShortFuse.UalInstalledByShortFuse";
    internal const string MetaUalProxyName = "ShortFuse.UalProxyName";
    internal const string MetaReShadeOverrideSuspended = "ShortFuse.ReShadeOverrideSuspended";
    internal const string MetaIniPath = "ShortFuse.IniPath";
    internal const string MetaIniSectionExisted = "ShortFuse.IniSectionExisted";
    internal const string MetaHookStreamlineExisted = "ShortFuse.HookStreamlineExisted";
    internal const string MetaHookStreamlineValue = "ShortFuse.HookStreamlineValue";
    internal const string MetaHookDirectXExisted = "ShortFuse.HookDirectXExisted";
    internal const string MetaHookDirectXValue = "ShortFuse.HookDirectXValue";
    internal const string MetaAddonSha256 = "ShortFuse.AddonSha256";

    private static readonly string[] PreferredUalNames = ["winmm.dll", "version.dll", "dinput8.dll"];

    private readonly HttpClient _http;
    private readonly IFileDeploymentService _files;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly UltimateAsiLoaderDeploymentService _ual;
    private readonly DlssManagerService _dlss;
    private readonly ShortFuseSettingsStore _settings;
    private readonly string _cacheRoot;

    public ShortFuseDeploymentService(
        HttpClient http,
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        UltimateAsiLoaderDeploymentService ual,
        DlssManagerService dlss,
        string? cacheRoot = null,
        ShortFuseSettingsStore? settings = null)
    {
        _http = http;
        _files = files;
        _records = records;
        _overrides = overrides;
        _ual = ual;
        _dlss = dlss;
        _settings = settings ?? new ShortFuseSettingsStore();
        _cacheRoot = cacheRoot ?? Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.CacheHome, "rhi-linux", "shortfuse");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ShortFuse);

    public bool IsManaged(DetectedGame game) => GetRecord(game) is not null;

    public bool GetAutoConfigEnabled(DetectedGame game) => _settings.IsAutoConfigEnabled(game);

    public void SetAutoConfigEnabled(DetectedGame game, bool enabled)
        => _settings.SetAutoConfigEnabled(game, enabled);

    public Task<ShortFuseUpdateStatus> CheckLatestAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => CheckLatestAsync(game, releaseTag: null, cancellationToken);

    public async Task<ShortFuseUpdateStatus> CheckLatestAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            var current = GetRecord(game)?.Version;
            var update = !string.IsNullOrWhiteSpace(current) && !string.Equals(current, release.Version, StringComparison.OrdinalIgnoreCase);
            return new(true, current, release.Version, update,
                string.IsNullOrWhiteSpace(current)
                    ? $"ShortFuse {(string.IsNullOrWhiteSpace(releaseTag) ? "Latest" : $"pinned {releaseTag}")}: {release.Version}"
                    : update ? $"ShortFuse target: {current} → {release.Version}" : $"ShortFuse {current} is current{(string.IsNullOrWhiteSpace(releaseTag) ? "" : $" (pinned {releaseTag})")}.");
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Could not check ShortFuse: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces only the managed renodx-dlss.addon64 with the pinned (or latest) release. ReShade,
    /// UAL, INI and the DLSS/Streamline stack stay as installed. Used by the in-place version swap
    /// and by Update All.
    /// </summary>
    public async Task<ComponentOperationResult> SwapAddonVersionAsync(
        DetectedGame game,
        string? releaseTag,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("ShortFuse is not managed by RHI Linux.");
        var addon = ManagedFileSwap.FindFile(record, AddonFileName);
        if (addon is null)
            return ComponentOperationResult.Fail($"The ShortFuse record does not own {AddonFileName}. Remove and reinstall ShortFuse.");

        ShortFuseReleaseInfo release;
        string staged;
        try
        {
            release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            staged = await EnsureCachedAddonAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"ShortFuse download failed; nothing was changed: {ex.Message}");
        }

        try
        {
            var changed = await ManagedFileSwap.ReplaceAsync([(addon, staged)], () =>
            {
                record.Version = release.Version;
                record.SourceUrl = release.AssetUrl;
                record.Metadata[MetaAddonSha256] = addon.Sha256 ?? "";
                record.Metadata["GitHub.ReleaseTagPin"] = releaseTag ?? "";
                _records.Upsert(record);
            }, cancellationToken).ConfigureAwait(false);
            return ComponentOperationResult.Ok(changed == 0
                ? $"ShortFuse {release.Version} is already installed."
                : $"ShortFuse add-on swapped in place to {release.Version}. ReShade, UAL and the DLSS stack were not changed.", addon.Path);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ShortFuse in-place swap failed; the previous add-on was kept: {ex.Message}");
        }
    }

    /// <summary>Swaps the ShortFuse-owned nvngx_dlssnr.dll to a manifest version in place (upstream 2.7.6).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.ShortFuse, "ShortFuse", version, cancellationToken);

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var status = await CheckLatestAsync(game, releaseTag, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(GetRecord(game) is not null, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion);
    }

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled = true,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, autoConfigEnabled, releaseTag: null, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled,
        string? releaseTag,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, autoConfigEnabled, releaseTag, neuralRenderingDllVersion: null, cancellationToken);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled,
        string? releaseTag,
        string? neuralRenderingDllVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ShortFuse is a Windows/ReShade injection workflow and is not applicable to a native Linux target.");
        if (game.EffectiveGraphicsApi == GraphicsApi.OpenGL)
            return ComponentOperationResult.Fail("ShortFuse is published for 64-bit Windows games except OpenGL targets; OpenGL is not a supported ShortFuse path.");
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("ShortFuse is already managed for this game. Remove it before reinstalling.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath) ||
            PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
            return ComponentOperationResult.Fail("ShortFuse auto-setup currently requires a detected 64-bit Windows executable.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not available for ShortFuse.");
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return ComponentOperationResult.Fail("Start the game once so its Wine/Proton prefix exists before installing ShortFuse.");

        var reshadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        if (reshadeRecord is null)
            return ComponentOperationResult.Fail("ShortFuse requires RHI-managed ReShade. External ReShade installations are not adopted automatically.");
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("ShortFuse auto-setup will not rewrite an active RHI-managed OptiScaler/ReShade chain. Remove OptiScaler first or keep that stack unchanged.");
        if (_records.Find(game, ManagedComponentKind.Dlss5Feeder) is not null)
            return ComponentOperationResult.Fail("ShortFuse must not be stacked with RHI-managed DLSS5 Feeder Full. Remove Feeder first.");
        if (_records.Find(game, ManagedComponentKind.Dlss5Dx11Bridge) is not null)
            return ComponentOperationResult.Fail("ShortFuse must not be stacked with RHI-managed DLSS5 DX11 Bridge. Remove the bridge first.");

        var reshadePayload = reshadeRecord.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
        if (reshadePayload is null || !File.Exists(reshadePayload.Path))
            return ComponentOperationResult.Fail("The RHI-managed ReShade payload is missing; ShortFuse will not repair or adopt ambiguous state.");
        if (!string.IsNullOrWhiteSpace(reshadePayload.Sha256))
        {
            var hash = await Sha256Async(reshadePayload.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, reshadePayload.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("The managed ReShade DLL was modified outside RHI. ShortFuse install is blocked to preserve that change.");
        }
        if (Path.GetFileName(reshadePayload.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ReShade is currently in OptiScaler coexistence mode (ReShade64.dll). ShortFuse will not rewrite that chain automatically.");

        var originalReShadePath = reshadePayload.Path;
        var asiPath = Path.Combine(game.BinaryDirectory, ReShadeAsiName);
        if (!Path.GetFullPath(originalReShadePath).Equals(Path.GetFullPath(asiPath), StringComparison.Ordinal) && File.Exists(asiPath))
            return ComponentOperationResult.Fail($"{ReShadeAsiName} already exists but is not owned by this ShortFuse transaction. External/stale files are protected from overwrite.");

        var addonDir = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory);
        var addonTarget = Path.Combine(addonDir, AddonFileName);
        if (File.Exists(addonTarget))
            return ComponentOperationResult.Fail($"{AddonFileName} already exists without an RHI ShortFuse deployment record. It may be an external or stale ShortFuse install; RHI will not overwrite it implicitly.");

        // Refuse to stack over direct RHI DLL swaps. Their .original files represent a separate ownership cycle.
        var detection = _dlss.Detect(game.InstallPath, game.ExecutablePath, cancellationToken: cancellationToken);
        var existingTargets = detection.DlssPaths.Concat(detection.DlssdPaths).Concat(detection.DlssgPaths)
            .Concat(detection.DlssnrPaths)
            .Concat(detection.StreamlineFolders.SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name))))
            .Where(File.Exists).Distinct(StringComparer.Ordinal).ToArray();
        var stackedBackup = existingTargets.FirstOrDefault(path => File.Exists(path + TransactionalFileDeploymentService.BackupExtension));
        if (stackedBackup is not null)
            return ComponentOperationResult.Fail($"ShortFuse found an existing RHI .original ownership cycle at {stackedBackup}. Restore direct DLSS/Streamline swaps before enabling ShortFuse.");

        ShortFuseReleaseInfo release;
        string stagedAddon;
        Dictionary<DlssComponentKind, string?> cachedDlss;
        string? cachedStreamline;
        try
        {
            release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            stagedAddon = await EnsureCachedAddonAsync(release, cancellationToken).ConfigureAwait(false);
            cachedDlss = new()
            {
                [DlssComponentKind.SuperResolution] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.SuperResolution, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.RayReconstruction] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.RayReconstruction, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.FrameGeneration] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.FrameGeneration, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.NeuralRendering] = await _dlss.EnsureCachedAsync(DlssComponentKind.NeuralRendering, neuralRenderingDllVersion, cancellationToken).ConfigureAwait(false),
            };
            cachedStreamline = await _dlss.EnsureNewestStreamlineCachedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ShortFuse download/staging failed before any game files were changed: {ex.Message}");
        }

        var ualWasPresent = _ual.GetRecord(game) is not null;
        var ualInstalledByUs = false;
        string? ualProxy = _ual.InstalledProxyName(game);
        var overrideSuspended = false;
        var reshadeMoved = false;
        IniSnapshot? iniSnapshot = null;
        var deployed = new List<LinuxDeployedFile>();
        var changedFiles = new List<string>();

        try
        {
            if (!ualWasPresent && autoConfigEnabled)
            {
                ComponentOperationResult? last = null;
                foreach (var candidate in PreferredUalNames)
                {
                    if (!UltimateAsiLoaderDeploymentService.IsSupportedProxyName(game, candidate)) continue;
                    last = await _ual.InstallAsync(game, candidate, cancellationToken).ConfigureAwait(false);
                    if (last.Success)
                    {
                        ualInstalledByUs = true;
                        ualProxy = candidate;
                        break;
                    }
                }
                if (!ualInstalledByUs)
                    throw new InvalidOperationException(last?.Message ?? "No safe UAL proxy from winmm → version → dinput8 was available.");
            }

            if (autoConfigEnabled)
            {
                if (reshadeRecord.WineOverride is not null)
                {
                    _overrides.Restore(game.PrefixPath, reshadeRecord.WineOverride);
                    overrideSuspended = reshadeRecord.WineOverride.ChangedByRhi;
                }

                if (!Path.GetFullPath(originalReShadePath).Equals(Path.GetFullPath(asiPath), StringComparison.Ordinal))
                {
                    File.Move(originalReShadePath, asiPath);
                    reshadeMoved = true;
                }

                reshadePayload.Path = asiPath;
                reshadeRecord.Metadata[MetaReShadeOriginalPath] = originalReShadePath;
                reshadeRecord.Metadata[MetaReShadeOverrideSuspended] = overrideSuspended ? "1" : "0";
                _records.Upsert(reshadeRecord);
                changedFiles.Add(asiPath);

                var iniPath = FindReShadeIni(game.BinaryDirectory);
                if (iniPath is not null)
                {
                    iniSnapshot = CaptureAndApplyIni(iniPath);
                    changedFiles.Add(iniPath);
                }
            }

            Directory.CreateDirectory(addonDir);
            File.Copy(stagedAddon, addonTarget, overwrite: false);
            var addonHash = await Sha256Async(addonTarget, cancellationToken).ConfigureAwait(false);
            deployed.Add(new LinuxDeployedFile { Path = addonTarget, ExistedBeforeInstall = false, Sha256 = addonHash });
            changedFiles.Add(addonTarget);

            await DeployDlssStackAsync(game, detection, cachedDlss, cachedStreamline, deployed, changedFiles, cancellationToken).ConfigureAwait(false);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaAutoConfigEnabled] = autoConfigEnabled ? "1" : "0",
                [MetaUalInstalledByShortFuse] = ualInstalledByUs ? "1" : "0",
                [MetaUalProxyName] = ualProxy ?? "",
                [MetaReShadeOverrideSuspended] = overrideSuspended ? "1" : "0",
                [MetaAddonSha256] = addonHash,
                ["GitHub.ReleaseTagPin"] = releaseTag ?? "",
            };
            if (autoConfigEnabled) metadata[MetaReShadeOriginalPath] = originalReShadePath;
            if (iniSnapshot is not null) WriteIniSnapshot(metadata, iniSnapshot);

            _records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.ShortFuse,
                BinaryDirectory = game.BinaryDirectory,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = deployed,
                Metadata = metadata
            });

            return ComponentOperationResult.Ok(
                $"ShortFuse {release.Version} installed. ReShade {(autoConfigEnabled ? "is loaded as ReShade64.asi" : "auto-config is OFF")}; " +
                $"UAL {(ualWasPresent ? "pre-existing/preserved" : ualInstalledByUs ? $"installed as {ualProxy}" : "not changed")}; HookStreamline/HookDirectX {(iniSnapshot is null ? "not changed (no ReShade.ini found)" : "enabled")}. " +
                "DLSS/Streamline ownership is transactional and direct swaps are locked until ShortFuse is removed.", changedFiles.ToArray());
        }
        catch (Exception ex)
        {
            await RollbackDeployedFilesAsync(deployed, cancellationToken).ConfigureAwait(false);
            try { if (File.Exists(addonTarget)) File.Delete(addonTarget); } catch { }
            if (iniSnapshot is not null) try { RestoreIni(iniSnapshot); } catch { }
            try
            {
                if (reshadeMoved && File.Exists(asiPath) && !File.Exists(originalReShadePath)) File.Move(asiPath, originalReShadePath);
                reshadePayload.Path = originalReShadePath;
                reshadeRecord.Metadata.Remove(MetaReShadeOriginalPath);
                reshadeRecord.Metadata.Remove(MetaReShadeOverrideSuspended);
                _records.Upsert(reshadeRecord);
            }
            catch { }
            try
            {
                if (overrideSuspended && reshadeRecord.WineOverride is not null)
                    _ = _overrides.EnsureNativeBuiltin(game.PrefixPath, reshadeRecord.WineOverride.DllName, reshadeRecord.WineOverride.ApplicationName);
            }
            catch { }
            if (ualInstalledByUs) try { await _ual.RemoveAsync(game, CancellationToken.None).ConfigureAwait(false); } catch { }
            return ComponentOperationResult.Fail($"ShortFuse install failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("ShortFuse is not managed by RHI Linux. External/stale ShortFuse files are protected from implicit removal.");

        // Hash guard every non-user-editable ShortFuse-owned file before destructive teardown.
        foreach (var file in record.Files.Where(f => !f.AllowUserChanges))
        {
            if (!File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"ShortFuse ownership record has no SHA-256 for {file.Path}; Remove is blocked.");
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"ShortFuse-owned file was modified outside RHI: {file.Path}. Remove is blocked to protect the external change.");
        }

        var reshadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        var ualByUs = record.Metadata.GetValueOrDefault(MetaUalInstalledByShortFuse) == "1";

        if (record.Metadata.TryGetValue(MetaReShadeOriginalPath, out var preflightOriginalPath) && !string.IsNullOrWhiteSpace(preflightOriginalPath))
        {
            if (reshadeRecord is null)
                return ComponentOperationResult.Fail("ShortFuse owns a ReShade transformation but the ReShade deployment record is missing. Remove is blocked rather than guessing ownership.");
            var payload = reshadeRecord.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
            if (payload is null || !File.Exists(payload.Path))
                return ComponentOperationResult.Fail("ShortFuse-managed ReShade64.asi is missing. Remove is blocked to avoid re-enabling a proxy path without a recoverable ReShade payload.");
            if (!string.IsNullOrWhiteSpace(payload.Sha256))
            {
                var current = await Sha256Async(payload.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed ReShade payload was modified outside RHI: {payload.Path}. ShortFuse Remove is blocked.");
            }
            if (!Path.GetFullPath(preflightOriginalPath).Equals(Path.GetFullPath(payload.Path), StringComparison.Ordinal) && File.Exists(preflightOriginalPath))
                return ComponentOperationResult.Fail($"The pre-ShortFuse ReShade path is occupied by another file: {preflightOriginalPath}. Remove is blocked to protect it.");
        }

        if (ualByUs)
        {
            var ualRecord = _ual.GetRecord(game);
            if (ualRecord is null)
                return ComponentOperationResult.Fail("ShortFuse installed UAL, but its deployment record is missing. Remove is blocked to avoid guessing loader ownership.");
            foreach (var file in ualRecord.Files.Where(f => !f.AllowUserChanges))
            {
                if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                    return ComponentOperationResult.Fail($"ShortFuse-owned UAL state is incomplete: {file.Path}. Remove is blocked.");
                var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"ShortFuse-owned UAL proxy was modified outside RHI: {file.Path}. Remove is blocked.");
            }
        }

        var rollbackSnapshots = new List<(string Path, byte[]? Bytes, byte[]? BackupBytes)>();

        // Snapshot the cross-component state as well as ShortFuse-owned files. Remove may touch
        // ReShade naming/metadata, reshade.ini, UAL and user.reg after restoring the DLL stack;
        // keeping byte-exact snapshots lets a late failure return to the installed ShortFuse state.
        var iniRollbackPath = record.Metadata.GetValueOrDefault(MetaIniPath);
        var iniRollbackBytes = !string.IsNullOrWhiteSpace(iniRollbackPath) && File.Exists(iniRollbackPath)
            ? await File.ReadAllBytesAsync(iniRollbackPath, cancellationToken).ConfigureAwait(false)
            : null;
        var registryPath = !string.IsNullOrWhiteSpace(game.PrefixPath) ? Path.Combine(game.PrefixPath, "user.reg") : null;
        var registryRollbackBytes = registryPath is not null && File.Exists(registryPath)
            ? await File.ReadAllBytesAsync(registryPath, cancellationToken).ConfigureAwait(false)
            : null;
        var reshadeRecordJson = reshadeRecord is null ? null : JsonSerializer.Serialize(reshadeRecord);
        var reshadePayloadBefore = reshadeRecord?.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
        var reshadePayloadPathBefore = reshadePayloadBefore?.Path;
        var reshadePayloadBytesBefore = reshadePayloadPathBefore is not null && File.Exists(reshadePayloadPathBefore)
            ? await File.ReadAllBytesAsync(reshadePayloadPathBefore, cancellationToken).ConfigureAwait(false)
            : null;
        var reshadeOriginalPathBefore = record.Metadata.GetValueOrDefault(MetaReShadeOriginalPath);
        var reshadeOriginalBytesBefore = !string.IsNullOrWhiteSpace(reshadeOriginalPathBefore) && File.Exists(reshadeOriginalPathBefore)
            ? await File.ReadAllBytesAsync(reshadeOriginalPathBefore, cancellationToken).ConfigureAwait(false)
            : null;

        var ualRecordBefore = ualByUs ? _ual.GetRecord(game) : null;
        var ualRecordJson = ualRecordBefore is null ? null : JsonSerializer.Serialize(ualRecordBefore);
        var ualTargetPath = ualRecordBefore?.Files.FirstOrDefault()?.Path;
        var ualTargetBytes = ualTargetPath is not null && File.Exists(ualTargetPath)
            ? await File.ReadAllBytesAsync(ualTargetPath, cancellationToken).ConfigureAwait(false)
            : null;
        var ualHookedPath = ualTargetPath is null ? null : Path.Combine(
            Path.GetDirectoryName(ualTargetPath)!, UltimateAsiLoaderDeploymentService.HookedName(Path.GetFileName(ualTargetPath)));
        var ualHookedBytes = ualHookedPath is not null && File.Exists(ualHookedPath)
            ? await File.ReadAllBytesAsync(ualHookedPath, cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            // Snapshot every owned target and backup so a late teardown failure can reconstruct state.
            foreach (var file in record.Files)
            {
                rollbackSnapshots.Add((file.Path,
                    File.Exists(file.Path) ? await File.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false) : null,
                    File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension)
                        ? await File.ReadAllBytesAsync(file.Path + TransactionalFileDeploymentService.BackupExtension, cancellationToken).ConfigureAwait(false)
                        : null));
            }

            foreach (var file in record.Files.AsEnumerable().Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall && File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                {
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success) throw new IOException(restored.Error ?? $"Could not restore {file.Path}");
                }
                else if (File.Exists(file.Path))
                {
                    File.Delete(file.Path);
                }
            }

            if (record.Metadata.ContainsKey(MetaIniPath))
                RestoreIni(ReadIniSnapshot(record.Metadata));

            if (reshadeRecord is not null && record.Metadata.TryGetValue(MetaReShadeOriginalPath, out var originalPath) && !string.IsNullOrWhiteSpace(originalPath))
            {
                var payload = reshadeRecord.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
                var asiPath = payload?.Path ?? Path.Combine(game.BinaryDirectory!, ReShadeAsiName);
                if (File.Exists(originalPath))
                    throw new IOException($"Cannot restore ReShade name because the original path is occupied: {originalPath}");
                if (File.Exists(asiPath)) File.Move(asiPath, originalPath);
                if (payload is not null) payload.Path = originalPath;
                reshadeRecord.Metadata.Remove(MetaReShadeOriginalPath);
                reshadeRecord.Metadata.Remove(MetaReShadeOverrideSuspended);
                _records.Upsert(reshadeRecord);
            }

            if (ualByUs && _ual.GetRecord(game) is not null)
            {
                var ualResult = await _ual.RemoveAsync(game, cancellationToken).ConfigureAwait(false);
                if (!ualResult.Success) throw new IOException(ualResult.Message);
            }

            if (reshadeRecord?.WineOverride is not null && record.Metadata.GetValueOrDefault(MetaReShadeOverrideSuspended) == "1")
                _ = _overrides.EnsureNativeBuiltin(game.PrefixPath!, reshadeRecord.WineOverride.DllName, reshadeRecord.WineOverride.ApplicationName);

            _records.Remove(game, ManagedComponentKind.ShortFuse);
            return ComponentOperationResult.Ok("ShortFuse removed. RHI restored the previous DLSS/Streamline files, ReShade filename, reshade.ini values, UAL state and ReShade Wine override ownership.");
        }
        catch (Exception ex)
        {
            // Restore the installed ShortFuse state byte-for-byte where possible so a retry remains safe.
            foreach (var snapshot in rollbackSnapshots)
            {
                try
                {
                    if (snapshot.Bytes is null) { if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!); File.WriteAllBytes(snapshot.Path, snapshot.Bytes); }
                    var backup = snapshot.Path + TransactionalFileDeploymentService.BackupExtension;
                    if (snapshot.BackupBytes is null) { if (File.Exists(backup)) File.Delete(backup); }
                    else File.WriteAllBytes(backup, snapshot.BackupBytes);
                }
                catch { }
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(iniRollbackPath))
                {
                    if (iniRollbackBytes is null) { if (File.Exists(iniRollbackPath)) File.Delete(iniRollbackPath); }
                    else File.WriteAllBytes(iniRollbackPath, iniRollbackBytes);
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(reshadePayloadPathBefore))
                {
                    if (reshadePayloadBytesBefore is null) { if (File.Exists(reshadePayloadPathBefore)) File.Delete(reshadePayloadPathBefore); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(reshadePayloadPathBefore)!); File.WriteAllBytes(reshadePayloadPathBefore, reshadePayloadBytesBefore); }
                }
                if (!string.IsNullOrWhiteSpace(reshadeOriginalPathBefore) &&
                    !Path.GetFullPath(reshadeOriginalPathBefore).Equals(Path.GetFullPath(reshadePayloadPathBefore ?? reshadeOriginalPathBefore), StringComparison.Ordinal))
                {
                    if (reshadeOriginalBytesBefore is null) { if (File.Exists(reshadeOriginalPathBefore)) File.Delete(reshadeOriginalPathBefore); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(reshadeOriginalPathBefore)!); File.WriteAllBytes(reshadeOriginalPathBefore, reshadeOriginalBytesBefore); }
                }
                if (reshadeRecordJson is not null && JsonSerializer.Deserialize<LinuxDeploymentRecord>(reshadeRecordJson) is { } restoredReShade)
                    _records.Upsert(restoredReShade);
            }
            catch { }

            try
            {
                if (ualTargetPath is not null)
                {
                    if (ualTargetBytes is null) { if (File.Exists(ualTargetPath)) File.Delete(ualTargetPath); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(ualTargetPath)!); File.WriteAllBytes(ualTargetPath, ualTargetBytes); }
                }
                if (ualHookedPath is not null)
                {
                    if (ualHookedBytes is null) { if (File.Exists(ualHookedPath)) File.Delete(ualHookedPath); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(ualHookedPath)!); File.WriteAllBytes(ualHookedPath, ualHookedBytes); }
                }
                if (ualRecordJson is not null && JsonSerializer.Deserialize<LinuxDeploymentRecord>(ualRecordJson) is { } restoredUal)
                    _records.Upsert(restoredUal);
            }
            catch { }

            try
            {
                if (registryPath is not null)
                {
                    if (registryRollbackBytes is null) { if (File.Exists(registryPath)) File.Delete(registryPath); }
                    else File.WriteAllBytes(registryPath, registryRollbackBytes);
                }
            }
            catch { }

            try { _records.Upsert(record); } catch { }
            return ComponentOperationResult.Fail($"ShortFuse removal failed; the installed ShortFuse/ReShade/UAL/INI/registry state was restored from transaction snapshots where possible: {ex.Message}");
        }
    }

    private async Task DeployDlssStackAsync(
        DetectedGame game,
        DlssDetectionResult detection,
        IReadOnlyDictionary<DlssComponentKind, string?> cached,
        string? cachedStreamline,
        List<LinuxDeployedFile> deployed,
        List<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var binaryDir = game.BinaryDirectory!;
        var mappings = new[]
        {
            (DlssComponentKind.SuperResolution, "nvngx_dlss.dll", detection.DlssPaths.FirstOrDefault()),
            (DlssComponentKind.RayReconstruction, "nvngx_dlssd.dll", detection.DlssdPaths.FirstOrDefault()),
            (DlssComponentKind.FrameGeneration, "nvngx_dlssg.dll", detection.DlssgPaths.FirstOrDefault()),
            (DlssComponentKind.NeuralRendering, "nvngx_dlssnr.dll", detection.DlssnrPaths.FirstOrDefault()),
        };

        foreach (var (kind, name, detected) in mappings)
        {
            if (!cached.TryGetValue(kind, out var source) || string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
            var target = detected ?? Path.Combine(binaryDir, name);
            await DeployOneAsync(source, target, deployed, changedFiles, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(cachedStreamline) && Directory.Exists(cachedStreamline))
        {
            var targetFolder = detection.StreamlineFolders.FirstOrDefault() ?? binaryDir;
            foreach (var name in DlssManagerService.KnownStreamlineDlls)
            {
                var source = Path.Combine(cachedStreamline, name);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(targetFolder, name);
                await DeployOneAsync(source, target, deployed, changedFiles, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DeployOneAsync(string source, string target, List<LinuxDeployedFile> deployed, List<string> changedFiles, CancellationToken ct)
    {
        var existed = File.Exists(target);
        var result = await _files.ReplaceAsync(source, target, ct).ConfigureAwait(false);
        if (!result.Success) throw new IOException(result.Error ?? $"Could not deploy {target}");
        if (!result.Changed) return;
        deployed.Add(new LinuxDeployedFile
        {
            Path = target,
            ExistedBeforeInstall = existed,
            Sha256 = await Sha256Async(target, ct).ConfigureAwait(false),
            AllowUserChanges = false
        });
        changedFiles.Add(target);
    }

    private async Task RollbackDeployedFilesAsync(IEnumerable<LinuxDeployedFile> files, CancellationToken ct)
    {
        foreach (var file in files.Reverse())
        {
            try
            {
                if (file.ExistedBeforeInstall && File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                    _ = await _files.RestoreAsync(file.Path, ct).ConfigureAwait(false);
                else if (File.Exists(file.Path)) File.Delete(file.Path);
            }
            catch { }
        }
    }

    internal Task<ShortFuseReleaseInfo> FetchLatestReleaseAsync(CancellationToken cancellationToken = default)
        => FetchReleaseAsync(releaseTag: null, cancellationToken);

    internal async Task<ShortFuseReleaseInfo> FetchReleaseAsync(string? releaseTag, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(releaseTag)
            ? ReleasesApiUrl
            : GitHubReleaseAssetResolver.BuildReleaseTagApiUrl(ReleasesApiUrl, releaseTag);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseReleaseJson(json, releaseTag);
    }

    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await new GitHubReleaseAssetResolver(_http)
            .ListVersionsAsync(ReleasesApiUrl, cancellationToken)
            .ConfigureAwait(false);
        return all.Where(v => v.Tag.StartsWith(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static ShortFuseReleaseInfo ParseReleaseJson(string json, string? requiredTag = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new InvalidDataException("ShortFuse release response was not an object or array.");

        var candidates = new List<(ShortFuseReleaseInfo Info, (Version Base, int PreRelease) Parsed)>();
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray()
            : new[] { doc.RootElement.Clone() };
        foreach (var release in releases)
        {
            var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(requiredTag) && !tag.Equals(requiredTag, StringComparison.OrdinalIgnoreCase)) continue;
            if (release.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            JsonElement? archive = null;
            JsonElement? direct = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(name, AddonFileName, StringComparison.OrdinalIgnoreCase))
                {
                    direct = asset.Clone();
                    break;
                }
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith(ArchiveAssetPrefix, StringComparison.OrdinalIgnoreCase))
                    archive ??= asset.Clone();
            }

            var chosen = direct ?? archive;
            if (chosen is null) continue;
            var chosenValue = chosen.Value;
            var assetName = chosenValue.TryGetProperty("name", out var chosenNameEl) ? chosenNameEl.GetString() ?? "" : "";
            var url = chosenValue.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var digest = chosenValue.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest[7..];

            var versionText = tag[ReleaseTagPrefix.Length..];
            var parsed = ReleaseVersionOrdering.Key(versionText);
            candidates.Add((new ShortFuseReleaseInfo(
                versionText, tag, url, string.IsNullOrWhiteSpace(digest) ? null : digest,
                assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), assetName), parsed));
        }

        if (candidates.Count == 0)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(requiredTag)
                ? $"No {ReleaseTagPrefix} release containing {AddonFileName} or a {ArchiveAssetPrefix}*.zip payload was found."
                : $"Pinned ShortFuse release {requiredTag} was not found or does not contain a supported payload.");
        // Version.TryParse rejected every "-rcN" tag, so rc builds fell back to API order (rc5 could beat rc10).
        return candidates
            .OrderByDescending(c => c.Parsed.Base)
            .ThenByDescending(c => c.Parsed.PreRelease)
            .First().Info;
    }

    private async Task<string> EnsureCachedAddonAsync(ShortFuseReleaseInfo release, CancellationToken ct)
    {
        var dir = Path.Combine(_cacheRoot, Sanitize(release.Version));
        var target = Path.Combine(dir, AddonFileName);
        var provenancePath = target + ".source.json";
        Directory.CreateDirectory(dir);

        if (File.Exists(target))
        {
            if (!release.IsArchive)
            {
                if (release.AssetSha256 is null || string.Equals(await Sha256Async(target, ct).ConfigureAwait(false), release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                    return target;
            }
            else if (TryReadCacheProvenance(provenancePath, out var provenance)
                && string.Equals(provenance.AssetUrl, release.AssetUrl, StringComparison.Ordinal)
                && string.Equals(provenance.AssetSha256 ?? "", release.AssetSha256 ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(provenance.ExtractedSha256, await Sha256Async(target, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            File.Delete(target);
            TryDelete(provenancePath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tempAsset = Path.Combine(dir, $"asset.tmp-{Guid.NewGuid():N}");
        var tempAddon = target + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(tempAsset))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);

            // Do not reject a ZIP based on its compressed byte size. A valid archive can be
            // very small when its contents compress well; archive safety is established below
            // by the release SHA-256 (when provided), ZIP parsing, exact payload cardinality,
            // and the uncompressed add-on size check. Direct .addon64 assets still keep the
            // conservative minimum-size guard.
            if (!release.IsArchive && new FileInfo(tempAsset).Length < 32 * 1024)
                throw new InvalidDataException("ShortFuse release asset is unexpectedly small.");

            var assetHash = await Sha256Async(tempAsset, ct).ConfigureAwait(false);
            if (release.AssetSha256 is not null && !string.Equals(assetHash, release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ShortFuse SHA256 mismatch: expected {release.AssetSha256}, got {assetHash}.");

            if (release.IsArchive)
            {
                using var zip = ZipFile.OpenRead(tempAsset);
                var selected = SelectArchiveAddonEntry(zip, release.AssetName);
                if (selected.Length < 32 * 1024)
                    throw new InvalidDataException("ShortFuse add-on inside the archive is unexpectedly small.");

                await using var source = selected.Open();
                await using var destination = File.Create(tempAddon);
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }
            else
            {
                File.Move(tempAsset, tempAddon, overwrite: true);
            }

            var extractedHash = await Sha256Async(tempAddon, ct).ConfigureAwait(false);
            File.Move(tempAddon, target, overwrite: true);
            if (release.IsArchive)
            {
                var provenance = new ShortFuseCacheProvenance(release.AssetUrl, release.AssetSha256, extractedHash);
                File.WriteAllText(provenancePath, JsonSerializer.Serialize(provenance));
            }
            else
            {
                TryDelete(provenancePath);
            }
            return target;
        }
        finally
        {
            TryDelete(tempAsset);
            TryDelete(tempAddon);
        }
    }


    internal static ZipArchiveEntry SelectArchiveAddonEntry(ZipArchive zip, string assetName)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var exact = zip.Entries
            .Where(entry => string.Equals(entry.Name, AddonFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1)
            throw new InvalidDataException($"ShortFuse archive '{assetName}' contains multiple ambiguous {AddonFileName} entries.");

        // Upstream relaxed archive extraction because ShortFuse occasionally ships the
        // correct payload under a different .addon64 filename. On Linux we keep that tolerance
        // fail-closed: exactly one fallback candidate is accepted; multiple candidates remain
        // ambiguous and no file is deployed.
        var fallback = zip.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                            && entry.Name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fallback.Length == 1) return fallback[0];
        if (fallback.Length == 0)
            throw new InvalidDataException($"ShortFuse archive '{assetName}' contains no .addon64 payload.");
        throw new InvalidDataException($"ShortFuse archive '{assetName}' contains multiple ambiguous .addon64 payloads.");
    }

    private static bool TryReadCacheProvenance(string path, out ShortFuseCacheProvenance provenance)
    {
        provenance = new ShortFuseCacheProvenance("", null, "");
        if (!File.Exists(path)) return false;
        try
        {
            var value = JsonSerializer.Deserialize<ShortFuseCacheProvenance>(File.ReadAllText(path));
            if (value is null || string.IsNullOrWhiteSpace(value.AssetUrl) || string.IsNullOrWhiteSpace(value.ExtractedSha256)) return false;
            provenance = value;
            return true;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static IniSnapshot CaptureAndApplyIni(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var section = FindSection(lines, "INSTALL");
        var sectionExisted = section.Start >= 0;
        var stream = ReadIniValue(lines, section, "HookStreamline");
        var direct = ReadIniValue(lines, section, "HookDirectX");
        SetIniValue(lines, "INSTALL", "HookStreamline", "1");
        SetIniValue(lines, "INSTALL", "HookDirectX", "1");
        AtomicWriteLines(path, lines);
        return new(path, sectionExisted, stream.Exists, stream.Value, direct.Exists, direct.Value);
    }

    private static void RestoreIni(IniSnapshot snapshot)
    {
        if (!File.Exists(snapshot.Path)) return;
        var lines = File.ReadAllLines(snapshot.Path).ToList();
        RestoreIniValue(lines, "INSTALL", "HookStreamline", snapshot.HookStreamlineExisted, snapshot.HookStreamlineValue);
        RestoreIniValue(lines, "INSTALL", "HookDirectX", snapshot.HookDirectXExisted, snapshot.HookDirectXValue);
        if (!snapshot.SectionExisted) RemoveSectionIfEmpty(lines, "INSTALL");
        AtomicWriteLines(snapshot.Path, lines);
    }

    private static void WriteIniSnapshot(Dictionary<string, string> metadata, IniSnapshot s)
    {
        metadata[MetaIniPath] = s.Path;
        metadata[MetaIniSectionExisted] = s.SectionExisted ? "1" : "0";
        metadata[MetaHookStreamlineExisted] = s.HookStreamlineExisted ? "1" : "0";
        metadata[MetaHookStreamlineValue] = s.HookStreamlineValue ?? "";
        metadata[MetaHookDirectXExisted] = s.HookDirectXExisted ? "1" : "0";
        metadata[MetaHookDirectXValue] = s.HookDirectXValue ?? "";
    }

    private static IniSnapshot ReadIniSnapshot(IReadOnlyDictionary<string, string> m) => new(
        m.GetValueOrDefault(MetaIniPath) ?? "",
        m.GetValueOrDefault(MetaIniSectionExisted) == "1",
        m.GetValueOrDefault(MetaHookStreamlineExisted) == "1",
        m.GetValueOrDefault(MetaHookStreamlineValue),
        m.GetValueOrDefault(MetaHookDirectXExisted) == "1",
        m.GetValueOrDefault(MetaHookDirectXValue));

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string sectionName)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (start < 0) { if (line.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase)) start = i; continue; }
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) return (start, i);
        }
        return start < 0 ? (-1, -1) : (start, lines.Count);
    }

    private static (bool Exists, string? Value) ReadIniValue(IReadOnlyList<string> lines, (int Start, int End) section, string key)
    {
        if (section.Start < 0) return (false, null);
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return (true, line[(eq + 1)..].Trim());
        }
        return (false, null);
    }

    private static void SetIniValue(List<string> lines, string sectionName, string key, string value)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add($"[{sectionName}]");
            lines.Add($"{key}={value}");
            return;
        }
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim(); var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { lines[i] = $"{key}={value}"; return; }
        }
        lines.Insert(section.End, $"{key}={value}");
    }

    private static void RestoreIniValue(List<string> lines, string sectionName, string key, bool existed, string? value)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0) { if (existed) SetIniValue(lines, sectionName, key, value ?? ""); return; }
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim(); var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (existed) lines[i] = $"{key}={value ?? ""}"; else lines.RemoveAt(i);
            return;
        }
        if (existed) SetIniValue(lines, sectionName, key, value ?? "");
    }

    private static void RemoveSectionIfEmpty(List<string> lines, string sectionName)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0) return;
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var text = lines[i].Trim();
            if (text.Length > 0 && !text.StartsWith(";", StringComparison.Ordinal) && !text.StartsWith("#", StringComparison.Ordinal)) return;
        }
        lines.RemoveRange(section.Start, section.End - section.Start);
    }

    private static void AtomicWriteLines(string path, IReadOnlyList<string> lines)
    {
        var temp = path + $".rhi-sf-{Guid.NewGuid():N}";
        File.WriteAllLines(temp, lines);
        File.Move(temp, path, overwrite: true);
    }

    private static string? FindReShadeIni(string binaryDir)
        => new[] { "ReShade.ini", "reshade.ini" }.Select(name => Path.Combine(binaryDir, name)).FirstOrDefault(File.Exists);

    private static bool IsReShadePayload(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(ReShadeAsiName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    internal sealed record IniSnapshot(string Path, bool SectionExisted, bool HookStreamlineExisted, string? HookStreamlineValue, bool HookDirectXExisted, string? HookDirectXValue);
}

public sealed record ShortFuseReleaseInfo(string Version, string Tag, string AssetUrl, string? AssetSha256, bool IsArchive = false, string AssetName = "");
internal sealed record ShortFuseCacheProvenance(string AssetUrl, string? AssetSha256, string ExtractedSha256);
public sealed record ShortFuseUpdateStatus(bool Success, string? InstalledVersion, string? LatestVersion, bool HasUpdate, string Message);
