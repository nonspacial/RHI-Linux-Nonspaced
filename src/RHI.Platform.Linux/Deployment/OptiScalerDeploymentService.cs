using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class OptiScalerDeploymentService
{
    internal const string VariantMetadataKey = "OptiScaler.Variant";
    internal const string DlssNrRuntimeVersionMetadataKey = "OptiScaler.DlssNrRuntimeVersion";
    /// <summary>The nightly build (date) an install is pinned to (#156); absent = Latest, which auto-updates.</summary>
    internal const string NightlyBuildMetadataKey = "OptiScaler.NightlyBuild";
    /// <summary>The DLL name OptiScaler was installed under (#136); absent in older records = <see cref="DefaultProxyName"/>.</summary>
    internal const string ProxyNameMetadataKey = "OptiScaler.ProxyName";
    public const string DefaultProxyName = "dxgi.dll";

    /// <summary>
    /// Names OptiScaler can be installed under: upstream's (2.8.7 <c>OptiScalerService.SupportedDllNames</c>), each
    /// loaded by Wine from the game folder with a <c>native,builtin</c> override.
    /// </summary>
    public static readonly IReadOnlyList<string> ProxyNames =
        ["dxgi.dll", "winmm.dll", "d3d11.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"];

    /// <summary>Another RHI component (ReShade, ASI Loader, …) holds the chosen name; name and component follow.</summary>
    public const string NameHeldByRhiComponentPrefix =
        "Another component RHI installed on this game already uses this DLL name, so OptiScaler cannot be installed under it. Name and component: ";

    /// <summary>
    /// An OptiScaler RHI does not manage already sits beside the game under one of <see cref="ProxyNames"/>; the file
    /// name follows. Two OptiScaler proxies would both load, so the install is refused before anything is downloaded.
    /// </summary>
    public const string OptiScalerAlreadyPresentPrefix =
        "An OptiScaler that RHI did not install is already in this game's folder, so RHI will not install a second one (both would load); nothing was changed. Remove that OptiScaler first, then try again. File: ";

    public const string ProxyNameNotOfferedPrefix = "OptiScaler cannot be installed under this DLL name; choose one of dxgi.dll, winmm.dll, d3d11.dll, d3d12.dll, dbghelp.dll, version.dll, wininet.dll or winhttp.dll: ";

    public const string NightlyPinnedCurrentPrefix = "OptiScaler Nightly is pinned and current: ";
    public const string NightlyPinnedNotUpdatedPrefix = "OptiScaler Nightly is pinned, so it was not updated: ";
    public const string NightlyPinWrongChannelMessage = "A nightly build can be pinned only for the OptiScaler Nightly channel.";
    public const string NightlyBuildInvalidPrefix = "Not an OptiScaler nightly build date: ";
    /// <summary>Appended to the OptiScaler row note while DLSS5 Feeder owns nvngx_dlssnr.dll (#157).</summary>
    public const string FeederBlocksNrVariantsNote = " DLSS NR and F5 DLSSNR are blocked while DLSS5 Feeder is managed because both would own nvngx_dlssnr.dll; Stable/Nightly remain available.";

    private static readonly string[] SetupJunkNames =
    [
        "setup_windows.bat", "setup_linux.sh", "remove_optiscaler.sh", "!! EXTRACT ALL FILES TO GAME FOLDER !!"
    ];

    private readonly IFileDeploymentService _files;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly IOptiScalerPackageProvider _packages;
    private readonly DlssManagerService? _dlss;
    private readonly StandaloneDlssEnablerDeploymentService? _dlssEnabler;
    private readonly DeploymentJournal? _journal;
    private readonly IOptiPatcherProvider? _optiPatcher;

    /// <summary>The OptiPatcher build deployed with this install (#98); absent = none (older installs, or it was unavailable).</summary>
    internal const string OptiPatcherVersionMetadataKey = "OptiScaler.OptiPatcherVersion";
    internal const string OptiPatcherDownloadPart = "OptiPatcher";
    /// <summary>Where OptiScaler loads ASI plugins from (relative to the game's binary folder), as upstream.</summary>
    internal const string OptiPatcherRelativePath = "plugins/" + OptiPatcherProvider.FileName;
    public const string OptiPatcherDeployedPrefix = " OptiPatcher deployed to plugins/ (OptiScaler LoadAsiPlugins on). Build: ";
    public const string OptiPatcherSkippedPrefix = " OptiPatcher was not deployed; OptiScaler works without it: ";
    public const string OptiPatcherUpdatePrefix = "OptiPatcher update available: ";
    public const string OptiPatcherForeignMessage = "a plugins/OptiPatcher.asi RHI does not own is already there; it was left as it is.";

    /// <param name="optiPatcher">OptiPatcher source (#98); null = OptiScaler alone (tests, tools).</param>
    public OptiScalerDeploymentService(
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        IOptiScalerPackageProvider packages,
        DlssManagerService? dlss = null,
        StandaloneDlssEnablerDeploymentService? dlssEnabler = null,
        DeploymentJournal? journal = null,
        IOptiPatcherProvider? optiPatcher = null)
    {
        _optiPatcher = optiPatcher;
        _journal = journal;
        _files = files;
        _records = records;
        _overrides = overrides;
        _packages = packages;
        _dlss = dlss;
        _dlssEnabler = dlssEnabler;
    }

    /// <summary>
    /// Per-GPU INI templates (#97): a new install deploys the user's template for its channel and GPU setup as the game's
    /// OptiScaler.ini instead of the package's. Null = the package's OptiScaler.ini (tests, tools).
    /// </summary>
    public OptiScalerIniTemplates? IniTemplates { get; set; }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler);

    /// <summary>The deployment journal this service writes (null without one), for steps that run beside it (#136 FgSlHdr10).</summary>
    internal DeploymentJournal? Journal => _journal;

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "OptiScaler is not managed by RHI Linux.");
        // A pinned nightly stays on its build (upstream 2.8.6): current, never an update and never "could not check".
        if (GetPinnedNightlyBuild(record) is { } pinned)
            return new ManagedUpdateCheck(true, false, NightlyPinnedCurrentPrefix + pinned, record.Version, pinned);
        try
        {
            var variant = GetInstalledVariant(record);
            var latest = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.SourceUrl, StringComparison.OrdinalIgnoreCase);
            if (NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant) && _dlss is not null)
            {
                await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
                var latestNr = _dlss.Versions(DlssComponentKind.NeuralRendering).FirstOrDefault();
                var installedNr = record.Metadata.GetValueOrDefault(DlssNrRuntimeVersionMetadataKey);
                if (!string.IsNullOrWhiteSpace(latestNr) && !string.Equals(latestNr, installedNr, StringComparison.OrdinalIgnoreCase))
                    update = true;
            }
            var label = VariantLabel(variant);
            if (!update && record.Metadata.GetValueOrDefault(OptiPatcherVersionMetadataKey) is { Length: > 0 } patcher
                && await TryResolveLatestOptiPatcherAsync(cancellationToken).ConfigureAwait(false) is { } latestPatcher
                && !string.Equals(patcher, latestPatcher.Version, StringComparison.OrdinalIgnoreCase))
                // OptiPatcher's rolling build moved on (upstream refreshes it silently): an update of this component.
                return new ManagedUpdateCheck(true, true, OptiPatcherUpdatePrefix + $"{patcher} → {latestPatcher.Version}", record.Version, latest.Version);
            return new ManagedUpdateCheck(true, update,
                update ? $"OptiScaler {label} update available: {record.Version ?? "unknown"} → {latest.Version}." : $"OptiScaler {label} {record.Version ?? latest.Version} is current.",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"OptiScaler update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail("OptiScaler is not managed by RHI Linux for this game.");
        if (GetPinnedNightlyBuild(record) is { } pinned)
            return ComponentOperationResult.Ok(NightlyPinnedNotUpdatedPrefix + pinned);

        var legacySwaps = await FindLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);
        foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
        {
            if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"OptiScaler managed file is missing or has no ownership hash: {file.Path}");
            if (legacySwaps.Contains(file.Path)) continue; // RHI's own bytes sit in its .original; given back below
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed OptiScaler file was modified outside RHI and will not be updated: {file.Path}");
        }

        var variant = GetInstalledVariant(record);
        OptiScalerPackage package;
        string? nrRuntime = null;
        string? nrRuntimeVersion = null;
        try
        {
            package = await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false);
            (nrRuntime, nrRuntimeVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"Could not obtain latest OptiScaler {VariantLabel(variant)} package: {ex.Message}");
        }
        // OptiPatcher is refreshed only where this install carries it (upstream re-copies it only where it is deployed);
        // its download failing keeps the deployed build.
        var optiPatcher = record.Metadata.ContainsKey(OptiPatcherVersionMetadataKey)
            ? await TryLatestOptiPatcherAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var proxyTarget = Path.Combine(binaryDirectory, GetProxyName(record));
        var requested = new List<(string Source, string Target)>();
        try
        {
            BuildDeploymentPlan(package.RootDirectory, binaryDirectory, proxyTarget, requested);
            if (nrRuntime is not null) requested.Add((nrRuntime, Path.Combine(binaryDirectory, "nvngx_dlssnr.dll")));
            if (optiPatcher is not null) requested.Add((optiPatcher.Download.Path, OptiPatcherTarget(binaryDirectory)));
        }
        catch (Exception ex) { return ComponentOperationResult.Fail($"OptiScaler update package layout is invalid: {ex.Message}"); }

        var byPath = record.Files.ToDictionary(file => Path.GetFullPath(file.Path), StringComparer.Ordinal);
        var createdDirs = new List<string>();

        // Journaled (#23, #31): every replaced file is tracked with a verified copy and every new one declared before it
        // is placed; the record (stamped with the operation id) is the commit point. A failure, or a crash before the
        // record is saved, puts every file back (no temporary backups of its own).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"OptiScaler update: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Downloaded and checked: from the first replaced file on, the update runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await GiveBackLegacyDlssSwapsAsync(legacySwaps, scope, cancellationToken).ConfigureAwait(false);
            foreach (var (source, targetRaw) in requested)
            {
                var target = Path.GetFullPath(targetRaw);
                if (byPath.TryGetValue(target, out var owned))
                {
                    if (owned.AllowUserChanges)
                        continue; // OptiScaler.ini is user state and never overwritten by Update All.
                    owned.Sha256 = await scope.PlaceCopyAsync(source, target, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (File.Exists(target))
                    throw new InvalidOperationException($"New OptiScaler package file collides with an external/untracked file: {target}");
                EnsureTargetDirectory(binaryDirectory, target, createdDirs);
                var copiedHash = await scope.PlaceCopyAsync(source, target, cancellationToken, overwrite: false).ConfigureAwait(false);
                var added = new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = false,
                    Sha256 = copiedHash,
                    AllowUserChanges = Path.GetFileName(target).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                };
                record.Files.Add(added);
                byPath[target] = added;
            }

            record.SourceUrl = package.SourceUrl;
            record.Version = package.Version;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[VariantMetadataKey] = variant.ToString();
            if (!string.IsNullOrWhiteSpace(nrRuntimeVersion))
                record.Metadata[DlssNrRuntimeVersionMetadataKey] = nrRuntimeVersion;
            else
                record.Metadata.Remove(DlssNrRuntimeVersionMetadataKey);
            if (package.Download is { } updateDownload) updateDownload.RecordIn(record.Metadata);
            else VerifiedDownload.Forget(record.Metadata);
            if (optiPatcher is not null)
            {
                record.Metadata[OptiPatcherVersionMetadataKey] = optiPatcher.Version;
                optiPatcher.Download.RecordIn(record.Metadata, OptiPatcherDownloadPart);
            }
            foreach (var dir in createdDirs)
                if (!record.DirectoriesCreatedByRhi.Contains(dir, StringComparer.Ordinal)) record.DirectoriesCreatedByRhi.Add(dir);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            // Compensation must run even when the failure *is* a cancellation; never cancelled itself.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            CleanupDirectories(createdDirs);
            return ComponentOperationResult.Fail($"OptiScaler update rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"OptiScaler {VariantLabel(variant)} updated to {package.Version}; OptiScaler.ini, ReShade coexistence and Wine override ownership were preserved.",
            requested.Select(item => item.Target).Where(path => !Path.GetFileName(path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    public Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallAsync(game, OptiScalerVariant.Stable, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, variant, nightlyBuild: null, cancellationToken);

    /// <summary>
    /// Installs OptiScaler Nightly pinned to one build (#156; Control UE frame generation pins <c>20261004</c>, #136). The
    /// build is recorded, so the game stays on it: Update All reports it current and never replaces it.
    /// </summary>
    public Task<ComponentOperationResult> InstallPinnedNightlyAsync(
        DetectedGame game,
        string build,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, OptiScalerVariant.Nightly, build, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        string? nightlyBuild,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, variant, nightlyBuild, proxyName: null, cancellationToken);

    /// <param name="nightlyBuild">Nightly only: the build date to pin (<c>20261004</c>); null = Latest, which auto-updates.</param>
    /// <param name="proxyName">The DLL name to install OptiScaler under (<see cref="ProxyNames"/>; Control UE frame
    /// generation uses <c>winmm.dll</c>, #136); null = <see cref="DefaultProxyName"/>. Recorded: update and removal use it.</param>
    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        string? nightlyBuild,
        string? proxyName,
        CancellationToken cancellationToken = default)
    {
        var chosenName = ProxyNames.FirstOrDefault(name => name.Equals(proxyName ?? DefaultProxyName, StringComparison.OrdinalIgnoreCase));
        if (chosenName is null) return ComponentOperationResult.Fail(ProxyNameNotOfferedPrefix + proxyName);
        proxyName = chosenName;
        if (nightlyBuild is not null)
        {
            if (variant != OptiScalerVariant.Nightly) return ComponentOperationResult.Fail(NightlyPinWrongChannelMessage);
            if (!OptiScalerPackageProvider.IsNightlyBuild(nightlyBuild)) return ComponentOperationResult.Fail(NightlyBuildInvalidPrefix + nightlyBuild);
        }
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("OptiScaler is a Windows DLL middleware and is only deployed to Wine/Proton games.");
        if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.DirectX12))
            return ComponentOperationResult.Fail("O suporte de proxy do OptiScaler é habilitado automaticamente apenas para jogos DirectX 11/12 detectados. Vulkan exige uma escolha explícita de loader/proxy antes de poder ser habilitado com segurança.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (GameArchitecture.Of(game) != PeArchitecture.X64)
            return ComponentOperationResult.Fail("Current OptiScaler packages are deployed by RHI Linux only to 64-bit Windows games.");
        if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
            return ComponentOperationResult.Fail("Start the Windows game once so its Wine/Proton prefix is initialized before installing OptiScaler.");
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing OptiScaler. The ShortFuse transaction owns the ReShade/UAL/DLSS/Streamline chain.");
        // nvngx_dlssnr.dll has one owner (NeuralRenderingRuntimeOwnership, #157): DLSS NR / F5 refuse while another owner is recorded.
        if (NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant)
            && NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.OptiScaler) is { } nrOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("OptiScaler is already managed by RHI Linux for this game. Remove it before reinstalling/updating.");
        // Never a second OptiScaler (review minor 1): one already under ANY supported name (the user's own, usually
        // dxgi.dll) refuses every install, whichever name this one would take.
        if (ExistingOptiScalerProxy(game.BinaryDirectory) is { } existingProxy)
            return ComponentOperationResult.Fail(OptiScalerAlreadyPresentPrefix + existingProxy);
        if (_overrides.EditRefusal(prefix) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        OptiScalerPackage package;
        string? nrRuntime = null;
        string? nrRuntimeVersion = null;
        try
        {
            package = nightlyBuild is null
                ? await _packages.GetLatestAsync(variant, cancellationToken).ConfigureAwait(false)
                : await _packages.GetNightlyBuildAsync(nightlyBuild, cancellationToken).ConfigureAwait(false);
            (nrRuntime, nrRuntimeVersion) = await PrepareDlssNrRuntimeAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { return ComponentOperationResult.Fail($"Could not obtain the official OptiScaler {VariantLabel(variant)} package: {ex.Message}"); }

        // The user's INI template for this channel and GPU setup (#97), seeded on first use; the package's INI otherwise.
        string? iniTemplate = null;
        var iniTemplateNote = "";
        if (IniTemplates is { } templates)
        {
            try
            {
                iniTemplate = templates.EnsureSeeded(variant);
                iniTemplateNote = OptiScalerIniTemplates.NotePrefix + iniTemplate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                iniTemplateNote = OptiScalerIniTemplates.UnavailablePrefix + ex.Message;
            }
        }

        // OptiPatcher (#98) goes with every OptiScaler install, as upstream; its download failing never fails OptiScaler.
        var optiPatcherNote = "";
        OptiPatcherBuild? optiPatcher = null;
        if (_optiPatcher is not null)
        {
            try { optiPatcher = await FetchLatestOptiPatcherAsync(_optiPatcher, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                optiPatcherNote = OptiPatcherSkippedPrefix + ex.Message;
            }
            if (optiPatcher is not null && File.Exists(OptiPatcherTarget(game.BinaryDirectory)))
            {
                optiPatcherNote = OptiPatcherSkippedPrefix + OptiPatcherForeignMessage;
                optiPatcher = null;
            }
        }

        var binaryDirectory = Path.GetFullPath(game.BinaryDirectory);
        var proxyTarget = Path.Combine(binaryDirectory, proxyName);
        LinuxDeploymentRecord? migratedReShade = null;
        WineOverrideRecord? overrideRecord = null;
        var overrideCreatedHere = false;
        var createdDirectories = new List<string>();
        var requested = new List<(string Source, string Target)>();
        var existedBefore = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Journaled (#23, #31): the Wine override is journaled before it is written and every file is tracked with a
        // verified copy (and every placed content declared) before it changes. The commit point is one ledger write that
        // saves OptiScaler's record (stamped with the operation id) and, when RHI's ReShade moved aside, ReShade's. A
        // failure, or a crash before that write, puts every file, ReShade's name and the override back.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"OptiScaler install: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;

        // Downloaded: from the first change on, the install runs to completion or full rollback (Cancel is refused).
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        LinuxDeploymentRecord record;
        try
        {
            migratedReShade = await MigrateManagedReShadeIfNeededAsync(game, proxyTarget, scope, cancellationToken).ConfigureAwait(false);

            if (File.Exists(proxyTarget))
            {
                if (BinarySignatureInspector.LooksLikeOptiScaler(proxyTarget))
                    throw new InvalidOperationException($"OptiScaler is already present but is not recorded as RHI-managed. The external installation is protected: {proxyName}");
                if (BinarySignatureInspector.LooksLikeReShade(proxyTarget))
                    throw new InvalidOperationException($"ReShade occupies this name but is not a migratable RHI-managed install. External ReShade is protected: {proxyName}");
                if (_records.FindAll(game).FirstOrDefault(other => other.Component != ManagedComponentKind.OptiScaler
                        && other.Files.Any(file => Path.GetFullPath(file.Path).Equals(proxyTarget, StringComparison.Ordinal))) is { } holder)
                    throw new InvalidOperationException(NameHeldByRhiComponentPrefix + $"{proxyName} ({LinuxManagedComponentService.DisplayDependencyName(holder.Component)})");
                throw new InvalidOperationException($"Foreign DLL protection: {proxyName} is occupied by an unknown DLL. Nothing was overwritten.");
            }

            // ReShade's override for this name passes to OptiScaler with the file it guarded (no registry change).
            overrideRecord = migratedReShade is not null ? _records.Find(game, ManagedComponentKind.ReShade)?.WineOverride : null;
            if (overrideRecord is null)
            {
                overrideRecord = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, prefix, proxyName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                overrideCreatedHere = true;
            }

            BuildDeploymentPlan(package.RootDirectory, binaryDirectory, proxyTarget, requested);
            if (iniTemplate is not null)
            {
                var iniTarget = Path.GetFullPath(Path.Combine(binaryDirectory, "OptiScaler.ini"));
                var at = requested.FindIndex(item => Path.GetFullPath(item.Target).Equals(iniTarget, StringComparison.Ordinal));
                if (at >= 0) requested[at] = (iniTemplate, requested[at].Target);
            }
            if (nrRuntime is not null) requested.Add((nrRuntime, Path.Combine(binaryDirectory, "nvngx_dlssnr.dll")));
            if (optiPatcher is not null) requested.Add((optiPatcher.Download.Path, OptiPatcherTarget(binaryDirectory)));
            foreach (var (_, target) in requested)
            {
                existedBefore[target] = File.Exists(target);
                if (File.Exists(target) && File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
                    throw new InvalidOperationException($"Backup collision protection: {target}.original already exists. RHI will not guess which previous tool owns that backup.");
            }
            foreach (var (source, target) in requested)
            {
                EnsureTargetDirectory(binaryDirectory, target, createdDirectories);
                await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(target, await FileHash.Sha256HexAsync(source, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            var batch = await _files.ReplaceBatchAsync(requested, cancellationToken).ConfigureAwait(false);
            if (!batch.Success) throw new InvalidOperationException(batch.Error ?? "OptiScaler file deployment failed.");

            if (migratedReShade is not null)
            {
                OptiScalerIniEditor.SetLoadReShade(binaryDirectory, true);
                await scope.DeclareWrittenContentAsync(Path.Combine(binaryDirectory, "OptiScaler.ini"), cancellationToken).ConfigureAwait(false);
            }
            // OptiScaler loads plugins/*.asi only with LoadAsiPlugins on. Upstream turns it on at every install, whether
            // or not OptiPatcher was deployed (a user's own plugins/OptiPatcher.asi then loads too); review nit 6.
            OptiScalerIniEditor.SetValue(binaryDirectory, "Plugins", "LoadAsiPlugins", "true");
            await scope.DeclareWrittenContentAsync(Path.Combine(binaryDirectory, "OptiScaler.ini"), cancellationToken).ConfigureAwait(false);

            record = new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.OptiScaler,
                BinaryDirectory = binaryDirectory,
                SourceUrl = package.SourceUrl,
                Version = package.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = overrideRecord,
                DirectoriesCreatedByRhi = createdDirectories.Distinct(StringComparer.Ordinal).ToList(),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [VariantMetadataKey] = variant.ToString(),
                    [DlssNrRuntimeVersionMetadataKey] = nrRuntimeVersion ?? ""
                }
            };
            if (nightlyBuild is not null) record.Metadata[NightlyBuildMetadataKey] = nightlyBuild;
            record.Metadata[ProxyNameMetadataKey] = proxyName;
            package.Download?.RecordIn(record.Metadata);
            if (optiPatcher is not null)
            {
                record.Metadata[OptiPatcherVersionMetadataKey] = optiPatcher.Version;
                optiPatcher.Download.RecordIn(record.Metadata, OptiPatcherDownloadPart);
                optiPatcherNote = OptiPatcherDeployedPrefix + optiPatcher.Version;
            }

            foreach (var (_, target) in requested)
            {
                record.Files.Add(new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = existedBefore[target],
                    Sha256 = await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false),
                    AllowUserChanges = Path.GetFileName(target).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                });
            }

            // The commit point: OptiScaler's stamped record and, after a move, ReShade's renamed record in one write.
            _records.Commit(migratedReShade is null ? [scope.Stamp(record)] : [scope.Stamp(record), migratedReShade]);
        }
        catch (Exception ex)
        {
            // Compensation must run even when the failure *is* a cancellation; never cancelled itself. Files (ReShade's
            // name included) come back from the scope's copies, then the override this install wrote.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (overrideCreatedHere && overrideRecord is not null)
                _overrides.TryRestore(prefix, overrideRecord);
            CleanupDirectories(createdDirectories);
            if (!rollback.Success)
                AppLog.Warn("optiscaler", $"Install rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"OptiScaler install failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(
            $"OptiScaler {VariantLabel(variant)} {package.Version} installed from the official GitHub release as {proxyName}" +
            (migratedReShade is null ? "." : "; RHI-managed ReShade was moved to ReShade64.dll and OptiScaler LoadReshade was enabled.") + iniTemplateNote + optiPatcherNote,
            proxyTarget);
    }

    internal static OptiScalerVariant GetInstalledVariant(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(VariantMetadataKey, out var raw) && Enum.TryParse<OptiScalerVariant>(raw, true, out var parsed)
            ? parsed
            : OptiScalerVariant.Stable;

    /// <summary>The nightly build an installed Nightly is pinned to, or null (Latest, another channel, no valid pin).</summary>
    public static string? GetPinnedNightlyBuild(LinuxDeploymentRecord record)
        => GetInstalledVariant(record) == OptiScalerVariant.Nightly
           && record.Metadata.TryGetValue(NightlyBuildMetadataKey, out var build)
           && OptiScalerPackageProvider.IsNightlyBuild(build)
            ? build
            : null;

    /// <summary>The DLL name an installed OptiScaler sits under (records before #136: <see cref="DefaultProxyName"/>).</summary>
    public static string GetProxyName(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(ProxyNameMetadataKey, out var name)
           && ProxyNames.FirstOrDefault(known => known.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } valid
            ? valid
            : DefaultProxyName;

    /// <summary>
    /// The file name of an OptiScaler (by its identity, <see cref="BinarySignatureInspector.LooksLikeOptiScaler"/>)
    /// sitting in <paramref name="binaryDirectory"/> under any of <see cref="ProxyNames"/>, in any spelling; else null.
    /// </summary>
    internal static string? ExistingOptiScalerProxy(string binaryDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(binaryDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (ProxyNames.Any(proxy => proxy.Equals(name, StringComparison.OrdinalIgnoreCase))
                && BinarySignatureInspector.LooksLikeOptiScaler(path))
                return name;
        }
        return null;
    }

    private static string OptiPatcherTarget(string binaryDirectory)
        => Path.GetFullPath(Path.Combine(binaryDirectory, "plugins", OptiPatcherProvider.FileName));

    private static async Task<OptiPatcherBuild> FetchLatestOptiPatcherAsync(IOptiPatcherProvider provider, CancellationToken cancellationToken)
        => await provider.FetchAsync(await provider.ResolveLatestAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>The newest OptiPatcher, downloaded, or null when there is no source or it cannot be had now (logged, never a failure).</summary>
    private async Task<OptiPatcherBuild?> TryLatestOptiPatcherAsync(CancellationToken cancellationToken)
    {
        if (_optiPatcher is null) return null;
        try { return await FetchLatestOptiPatcherAsync(_optiPatcher, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("optiscaler", "OptiPatcher could not be checked or downloaded; the deployed build stays", ex);
            return null;
        }
    }

    /// <summary>
    /// The update check's view of OptiPatcher: the newest build's version and digest only, never its file (upstream
    /// <c>CheckOptiPatcherUpdateAsync</c>); null when there is no source or it cannot be read now (logged).
    /// </summary>
    private async Task<OptiPatcherRelease?> TryResolveLatestOptiPatcherAsync(CancellationToken cancellationToken)
    {
        if (_optiPatcher is null) return null;
        try { return await _optiPatcher.ResolveLatestAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("optiscaler", "OptiPatcher could not be checked; the deployed build stays", ex);
            return null;
        }
    }

    /// <summary>The newest nightly builds a game can be pinned to (dates, newest first).</summary>
    public Task<IReadOnlyList<string>> ListNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => _packages.ListNightlyBuildsAsync(cancellationToken);

    internal static string VariantLabel(OptiScalerVariant variant) => variant switch
    {
        OptiScalerVariant.Stable => "Stable",
        OptiScalerVariant.Nightly => "Nightly",
        OptiScalerVariant.DlssNr => "DLSS NR",
        OptiScalerVariant.F5 => "F5 DLSSNR",
        _ => variant.ToString()
    };

    private async Task<(string? Path, string? Version)> PrepareDlssNrRuntimeAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken)
    {
        if (!NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant)) return (null, null);
        if (_dlss is null)
            throw new InvalidOperationException($"OptiScaler {VariantLabel(variant)} requires the DLSS runtime service so nvngx_dlssnr.dll can be staged safely.");
        var path = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.NeuralRendering, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"The current DLSS manifest did not provide nvngx_dlssnr.dll for the {VariantLabel(variant)} variant.");
        var version = _dlss.Manifest?.Dlssnr?.FirstOrDefault()?.Version;
        return (path, version);
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.OptiScaler);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler was detected, but it is not managed by RHI Linux. Removal is locked to protect the external installation.");

        var reShadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        var reShade64 = reShadeRecord?.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        // ReShade64.dll goes back to the name OptiScaler took from it (dxgi.dll unless OptiScaler sits elsewhere, #136).
        var proxyName = GetProxyName(record);
        var dxgi = Path.Combine(record.BinaryDirectory, proxyName);

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        IReadOnlySet<string> legacySwaps;
        var moveReShadeBack = false;
        try
        {
            legacySwaps = await FindLegacyDlssSwapsAsync(record, cancellationToken).ConfigureAwait(false);

            // Preflight every destructive action before changing any file. Kept/taken-over files are hash-checked too:
            // restoring their .original would overwrite a file the user replaced.
            foreach (var file in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.ExistedBeforeInstall && !File.Exists(file.Path + TransactionalFileDeploymentService.BackupExtension))
                    return ComponentOperationResult.Fail($"Cannot safely remove OptiScaler: preserved original is missing for {file.Path}.");
                if (!File.Exists(file.Path) || file.AllowUserChanges || legacySwaps.Contains(file.Path)) continue;
                var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so OptiScaler was not removed.");
            }

            if (reShade64 is not null && File.Exists(reShade64.Path))
            {
                var current = await FileHash.Sha256HexAsync(reShade64.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, reShade64.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed ReShade64.dll was modified outside RHI. OptiScaler removal is blocked so the ReShade chain is not broken: {reShade64.Path}");

                // ReShade64 can only be moved back to dxgi.dll after OptiScaler is removed when
                // dxgi.dll did not contain a pre-existing game/third-party DLL before OptiScaler.
                // If OptiScaler preserved such a DLL as dxgi.dll.original, restoring it and moving
                // ReShade to the same path would be mutually exclusive. In that topology the safe
                // order is ReShade first, then OptiScaler.
                var proxyRecord = record.Files.FirstOrDefault(file =>
                    Path.GetFullPath(file.Path).Equals(Path.GetFullPath(dxgi), StringComparison.Ordinal));
                // No proxy record with dxgi.dll gone: an earlier, partly failed removal already deleted it.
                if (proxyRecord is null && File.Exists(dxgi))
                    return ComponentOperationResult.Fail($"Cannot safely remove OptiScaler while ReShade depends on it: the managed proxy record is missing: {proxyName}");
                if (proxyRecord is { ExistedBeforeInstall: true })
                    return ComponentOperationResult.Fail($"Remove RHI-managed ReShade first. OptiScaler preserved a pre-existing DLL under its name, so ReShade64.dll cannot be moved back without overwriting that original: {proxyName}");
                moveReShadeBack = reShadeRecord is not null;
            }
        }
        catch (Exception ex)
        {
            // Nothing changed yet (a Cancel or an unreadable file during the check).
            return ComponentOperationResult.Fail($"OptiScaler removal failed: {ex.Message}");
        }

        // Journaled (#23, #31): the record is stamped with the removal's id first; every file the teardown touches is
        // tracked with a verified copy (and every placed content declared) before it changes. The commit point is one
        // ledger write that takes OptiScaler's record away and saves ReShade's moved-back record. A failure, or a crash
        // before that write, puts every file, ReShade's name and the override back; a removal never stops half-way.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, $"OptiScaler removal: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;
        LinuxDeploymentRecord? reShadeAfter = null;

        // Cancel is honoured only up to here: the teardown runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await GiveBackLegacyDlssSwapsAsync(legacySwaps, scope, cancellationToken).ConfigureAwait(false);
            foreach (var file in record.Files.OrderByDescending(file => file.Path.Length).ToArray())
            {
                await scope.TrackAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (file.ExistedBeforeInstall)
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

            if (moveReShadeBack)
            {
                if (File.Exists(dxgi))
                    throw new IOException($"OptiScaler files were removed, but the name ReShade64.dll goes back to is unexpectedly occupied; RHI did not move ReShade back over it: {proxyName}");
                // The content is the ReShade64.dll the preflight just hash-checked, so its recorded hash stays valid.
                await scope.TrackAsync(reShade64!.Path, cancellationToken).ConfigureAwait(false);
                await scope.TrackAsync(dxgi, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(dxgi, scope.Files.Find(reShade64.Path)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                AtomicFile.Rename(reShade64.Path, dxgi);
                reShadeAfter = LinuxDeploymentRecordStore.Clone(reShadeRecord!);
                reShadeAfter.Files.First(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase)).Path = dxgi;
                reShadeAfter.WineOverride = record.WineOverride;
            }
            else if (game.PrefixPath is { } prefix && Directory.Exists(prefix))
            {
                overrideRestore = await _overrides.RestoreAsync(scope.Operation, prefix, record.WineOverride, cancellationToken).ConfigureAwait(false);
            }

            // The commit point: OptiScaler's record goes and ReShade's (moved back) is saved in the same write.
            _records.Commit(reShadeAfter is null ? [] : [reShadeAfter], [(game, ManagedComponentKind.OptiScaler)]);
        }
        catch (Exception ex)
        {
            // OptiScaler's record goes back (still stamped) with ReShade's as it was, before the files roll back.
            var rollback = await scope.RollbackRemovalAsync(
                _records, game, record, "optiscaler", alongside: reShadeAfter is null ? null : [reShadeRecord!]).ConfigureAwait(false);
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            if (!rollback.Success)
                AppLog.Warn("optiscaler", $"Removal rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"{RemovalFailedPrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point. Empty folders RHI created go after it (nothing to roll back).
        await scope.CommitAsync().ConfigureAwait(false);
        CleanupDirectories(record.DirectoriesCreatedByRhi);
        return ComponentOperationResult.Ok((reShadeAfter is not null
            ? $"RHI-managed OptiScaler removed; ReShade64.dll was moved back and retained its Wine override: {proxyName}"
            : "RHI-managed OptiScaler removed; preserved game files and the Wine DLL override were restored.")
            + WineDllOverrideService.KeptValueNotes(overrideRestore));
    }

    /// <summary>Fixed text of a failed, rolled-back OptiScaler removal; the reason follows (PT-BR pair exists).</summary>
    public const string RemovalFailedPrefix = "OptiScaler removal failed and was rolled back; OptiScaler stays installed: ";

    /// <summary>
    /// Before #135 the direct DLSS flows (Quick Apply / Batch Deploy with a Streamline default, the panel's Streamline
    /// swap) also swapped RHI's own <c>OptiScaler/Streamline/sl.*</c>: the owned file was kept next to the new one as
    /// <c>&lt;name&gt;.original</c> (the DLSS swap backup) and no longer matched the record. Those flows no longer reach
    /// OptiScaler's files, so that backup would be stranded and every disable/redeploy/removal refused. When a managed
    /// file RHI created (not a kept original) mismatches its recorded hash and its <c>.original</c> carries exactly that
    /// hash, the backup is RHI's own file. Read only: returns those files (full paths); <see cref="GiveBackLegacyDlssSwapsAsync"/>
    /// moves them back inside the operation.
    /// </summary>
    private static async Task<IReadOnlySet<string>> FindLegacyDlssSwapsAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ExistedBeforeInstall || file.AllowUserChanges || string.IsNullOrWhiteSpace(file.Sha256)) continue;
            var backup = file.Path + TransactionalFileDeploymentService.BackupExtension;
            if (!File.Exists(file.Path) || !File.Exists(backup)) continue;
            if (string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(await FileHash.Sha256HexAsync(backup, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(file.Path);
        }
        return found;
    }

    /// <summary>
    /// Moves RHI's own bytes back from each <c>.original</c> found by <see cref="FindLegacyDlssSwapsAsync"/>, tracked and
    /// declared in <paramref name="scope"/>: this only returns ownership and overwrites nothing foreign, and a rollback
    /// puts both copies back as they were.
    /// </summary>
    private static async Task GiveBackLegacyDlssSwapsAsync(IReadOnlySet<string> paths, DeploymentScope scope, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            await scope.TrackAsync(path, cancellationToken).ConfigureAwait(false);
            var backupSha = scope.Files.Find(path)!.BackupPreSha256
                ?? await FileHash.Sha256HexAsync(path + TransactionalFileDeploymentService.BackupExtension, CancellationToken.None).ConfigureAwait(false);
            await scope.DeclareWriteAsync(path, backupSha, cancellationToken).ConfigureAwait(false);
            AtomicFile.Rename(path + TransactionalFileDeploymentService.BackupExtension, path, overwrite: true);
        }
    }

    /// <summary>
    /// RHI's ReShade at <paramref name="proxyTarget"/> moves to <c>ReShade64.dll</c> (tracked and declared in
    /// <paramref name="scope"/>), so OptiScaler can load it through LoadReshade. Returns ReShade's record as it must be
    /// saved with OptiScaler's (renamed, its override passed on), or null when nothing moved; nothing is saved here.
    /// </summary>
    private async Task<LinuxDeploymentRecord?> MigrateManagedReShadeIfNeededAsync(
        DetectedGame game,
        string proxyTarget,
        DeploymentScope scope,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(proxyTarget)) return null;
        if (!BinarySignatureInspector.LooksLikeReShade(proxyTarget)) return null;

        var record = _records.Find(game, ManagedComponentKind.ReShade)
            ?? throw new InvalidOperationException($"External ReShade occupies this name. RHI will not rename or take ownership of it automatically: {Path.GetFileName(proxyTarget)}");
        var file = record.Files.FirstOrDefault(item => Path.GetFullPath(item.Path).Equals(Path.GetFullPath(proxyTarget), StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"ReShade is RHI-managed, but its deployment record does not own the current file: {Path.GetFileName(proxyTarget)}");
        var reShade64 = Path.Combine(Path.GetDirectoryName(proxyTarget)!, "ReShade64.dll");
        if (File.Exists(reShade64))
            throw new InvalidOperationException("ReShade64.dll already exists, so RHI cannot create the OptiScaler/ReShade coexistence chain safely.");

        var current = await FileHash.Sha256HexAsync(proxyTarget, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Managed ReShade was modified outside RHI and will not be migrated automatically: {Path.GetFileName(proxyTarget)}");

        await scope.TrackAsync(proxyTarget, cancellationToken).ConfigureAwait(false);
        await scope.TrackAsync(reShade64, cancellationToken).ConfigureAwait(false);
        await scope.DeclareWriteAsync(reShade64, current, cancellationToken).ConfigureAwait(false);
        AtomicFile.Rename(proxyTarget, reShade64);
        file.Path = reShade64;
        file.Sha256 = current;
        record.WineOverride = null;
        return record;
    }

    internal static void BuildDeploymentPlan(
        string packageRoot,
        string binaryDirectory,
        string proxyTarget,
        List<(string Source, string Target)> requested)
    {
        var optiDll = Path.Combine(packageRoot, "OptiScaler.dll");
        var optiIni = Path.Combine(packageRoot, "OptiScaler.ini");
        if (!File.Exists(optiDll) || !File.Exists(optiIni))
            throw new InvalidOperationException("OptiScaler package root is incomplete.");
        requested.Add((optiDll, proxyTarget));

        foreach (var source in SafeFileEnumeration.Files(packageRoot))
        {
            var relative = Path.GetRelativePath(packageRoot, source);
            if (relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (SetupJunkNames.Any(name => Path.GetFileName(relative).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment.Equals("Licenses", StringComparison.OrdinalIgnoreCase))) continue;
            if (IsNonGamePackageEntry(segments)) continue;

            var target = Path.GetFullPath(Path.Combine(binaryDirectory, relative));
            var rootWithSep = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new InvalidOperationException($"OptiScaler archive path escapes the game binary directory: {relative}");
            requested.Add((source, target));
        }
    }

    /// <summary>
    /// Package entries upstream never copies into the game (2.8.7 install step 5, needed since the F5 DLSSNR package,
    /// #157): top-level scripts, notes and tools (<c>.bat .sh .ps1 .txt .md .exe .pdb</c>, <c>LICENSE</c>, <c>!!…</c>) and
    /// the top-level <c>docs</c> and <c>redist</c> folders. Folders OptiScaler loads (<c>OptiScaler/</c>,
    /// <c>D3D12_Optiscaler/</c>) and every DLL/INI are still deployed.
    /// </summary>
    internal static bool IsNonGamePackageEntry(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return true;
        if (segments.Count > 1)
            return segments[0].Equals("docs", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("redist", StringComparison.OrdinalIgnoreCase);
        var name = segments[0];
        return name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("!!", StringComparison.Ordinal)
               || new[] { ".bat", ".sh", ".ps1", ".txt", ".md", ".exe", ".pdb" }.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureTargetDirectory(string binaryDirectory, string target, List<string> createdDirectories)
    {
        var baseDir = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(target);
        if (dir is null || dir.Equals(baseDir, StringComparison.Ordinal)) return;
        var missing = new Stack<string>();
        var current = dir;
        while (!current.Equals(baseDir, StringComparison.Ordinal) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? baseDir;
        }
        while (missing.Count > 0)
        {
            var create = missing.Pop();
            Directory.CreateDirectory(create);
            createdDirectories.Add(create);
        }
    }

    private static void CleanupDirectories(IEnumerable<string> directories)
    {
        foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch { }
        }
    }
}
