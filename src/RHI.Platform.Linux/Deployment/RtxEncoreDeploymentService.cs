using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// RTX Encore (SilyNoMeta/rtx-encore, #158, upstream 2.8.6 <c>RtxEncoreService</c>): one x64 DLL, <c>rtx-encore.dll</c>, put
/// beside the game's executable under a name the game loads (version.dll by default, the #120 name store keeps the
/// choice) or as <c>rtx-encore.asi</c> beside an RHI-managed Ultimate ASI Loader.
/// <list type="bullet">
/// <item>Every release so far is a prerelease, so the newest non-draft release of <c>releases?per_page=10</c> is used
/// (as upstream); its zip is checked against GitHub's digest and the publisher's SHA256SUMS, and the DLL must carry RTX
/// Encore's own version resource.</item>
/// <item>A file RHI did not write already under the chosen name is kept as <c>&lt;name&gt;.original</c> and put back on
/// removal (upstream's backup). Each <c>.dll</c> name gets the journaled per-game Wine <c>native,builtin</c> override.</item>
/// <item>Optional: NVIDIA's <c>nvngx_dlssnr.dll</c> 310.8.0, the only one RTX Encore accepts (it checks the SHA-256), from
/// the DLSS list. With it RTX Encore's record owns the game's Neural Rendering runtime under the one-owner rule
/// (<see cref="NeuralRenderingRuntimeOwnership"/>); without it RTX Encore owns nothing and uses what is there.</item>
/// </list>
/// Under Proton nothing about it is confirmed yet (no game or Wine was run for #158): every row note says so.
/// </summary>
public sealed class RtxEncoreDeploymentService
{
    public const string DisplayName = ComponentProbe.RtxEncoreDisplayName;
    public const string ReleasesApiUrl = "https://api.github.com/repos/SilyNoMeta/rtx-encore/releases?per_page=10";
    public const string RepositoryUrl = "https://github.com/SilyNoMeta/rtx-encore";
    public const string PayloadName = "rtx-encore.dll";
    public const string DefaultName = "version.dll";
    public const string KeptSuffix = ".original";

    /// <summary>The Neural Rendering runtime RTX Encore accepts: NVIDIA's 310.8.0, by SHA-256 ("310.8.0 SHA256 mismatch").</summary>
    public const string NeuralRenderingVersion = "310.8.0";
    public const string NeuralRenderingSha256 = "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";

    // Fixed messages, values last (PT-BR presentation pairs in UiLocalizationService).
    public const string NotYetConfirmedNote = "Not yet confirmed on Linux: Frame Generation ×3–×6 and Neural Rendering may work through Proton's DLSS; Smooth Motion cannot (it needs the Windows driver).";
    public const string NativeLinuxMessage = "RTX Encore is a Windows DLL; it does not apply to native Linux games.";
    public const string NeedsX64Message = "RTX Encore needs a 64-bit Windows game.";
    public const string ApiMessage = "RTX Encore works with DirectX 11, DirectX 12 and Vulkan games only.";
    public const string NeedsDlssMessage = "RTX Encore works through the game's own DLSS; this game has no DLSS.";
    public const string PrefixMessage = "Start the game once so its Wine/Proton prefix exists before installing RTX Encore.";
    public const string AlreadyInstalledMessage = "RTX Encore is already installed by RHI for this game.";
    public const string NotInstalledMessage = "RTX Encore is not installed by RHI for this game.";
    public const string NameLockedMessage = "RTX Encore is installed: remove it to choose another name.";
    public const string NameNotOfferedPrefix = "RTX Encore cannot use this name for this game; choose another in ⚙: ";
    public const string NameHeldPrefix = "This name already belongs to another RHI component; choose another in ⚙: ";
    public const string KeptCopyInTheWayPrefix = "RTX Encore not installed: a copy kept from before is in the way; move it aside first: ";
    public const string DownloadFailedPrefix = "RTX Encore download failed; nothing was changed: ";
    public const string NeuralRenderingUnavailableMessage = "The DLSS list no longer offers NVIDIA's Neural Rendering file 310.8.0, the only one RTX Encore accepts; nothing was changed.";
    public const string NeuralRenderingMismatchMessage = "The Neural Rendering file 310.8.0 that was downloaded is not the one RTX Encore accepts; nothing was changed.";
    public const string NeuralRenderingNeedsDlssListMessage = "The DLSS list is not available, so the Neural Rendering file cannot be fetched; nothing was changed.";
    public const string FailedRolledBackPrefix = "RTX Encore failed and was rolled back: ";
    public const string ModifiedPrefix = "RTX Encore not changed: a file changed since RHI installed it (left as it is): ";
    public const string MissingPrefix = "RTX Encore not updated: its file is gone; remove it and install it again: ";
    public const string InstalledPrefix = "RTX Encore installed; press Insert in the game for its menu. Not yet confirmed on Linux. Name: ";
    public const string InstalledWithNeuralRenderingSuffix = " (with the Neural Rendering file 310.8.0)";
    public const string RemovedMessage = "RTX Encore removed; files kept from before it are back.";
    public const string KeptCopyMissingPrefix = " The copy kept from before was not found, so nothing was put back: ";
    public const string UpToDatePrefix = "RTX Encore is up to date: ";
    public const string UpdatedPrefix = "RTX Encore updated to ";
    public const string UpdateAvailablePrefix = "RTX Encore update available: ";
    public const string LatestPrefix = "Latest RTX Encore: ";
    public const string CheckFailedPrefix = "Could not check RTX Encore: ";
    public const string NeuralRenderingOnMessage = "RTX Encore: Neural Rendering file 310.8.0 installed.";
    public const string NeuralRenderingOffMessage = "RTX Encore: Neural Rendering file removed; a file kept from before is back.";
    public const string NeuralRenderingAlreadyMessage = "RTX Encore: nothing to change.";
    public const string SavedWithNeuralRenderingMessage = "Saved: RTX Encore will be installed with the Neural Rendering file 310.8.0.";
    public const string SavedWithoutNeuralRenderingMessage = "Saved: RTX Encore will be installed without the Neural Rendering file.";
    public const string SavedNamePrefix = "Saved: RTX Encore will be installed as ";
    // Why a release or download is refused (they follow DownloadFailedPrefix / FailedRolledBackPrefix).
    public const string ReleaseListShapeMessage = "The RTX Encore release list could not be read.";
    public const string NoReleaseMessage = "No RTX Encore release with an RTX-Encore-*.zip was found.";
    public const string NoPublishedDigestPrefix = "The RTX Encore release publishes no SHA-256 (GitHub digest or SHA256SUMS); refused: ";
    public const string TagNotAVersionPrefix = "The RTX Encore release tag is not a version: ";
    public const string SumsNoEntryPrefix = "RTX Encore's SHA256SUMS has no line for the zip: ";
    public const string SumsMismatchPrefix = "The RTX Encore zip does not match its SHA256SUMS: ";
    public const string PayloadNotAtRootMessage = "rtx-encore.dll is not at the root of the RTX Encore zip exactly once.";
    public const string TooSmallMessage = "The RTX Encore DLL is implausibly small.";
    public const string NotX64Message = "The RTX Encore download is not a 64-bit Windows DLL.";
    public const string NoVersionResourceMessage = "The RTX Encore download does not carry RTX Encore's version information.";

    private const string MetaName = "RtxEncore.Name";
    private const string MetaTag = "RtxEncore.Tag";
    private const string MetaAssetName = "RtxEncore.AssetName";
    private const string MetaKept = "RtxEncore.KeptOriginal";
    private const string MetaNrKept = "RtxEncore.NeuralRenderingKeptOriginal";
    private const string MetaNrVersion = "RtxEncore.NeuralRenderingVersion";
    private const string NrPart = "NeuralRendering";
    private const int MinimumDllSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly DllNamePreferenceStore _names;
    private readonly DlssManagerService? _dlss;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheRoot;

    public RtxEncoreDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        DllNamePreferenceStore names,
        DlssManagerService? dlss,
        string? cacheRoot = null,
        DeploymentJournal? journal = null)
    {
        _http = http;
        _records = records;
        _overrides = overrides;
        _names = names;
        _dlss = dlss;
        _journal = journal;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "rtx-encore");
    }

    /// <summary>The SHA-256 the Neural Rendering file must have (tests substitute their own runtime).</summary>
    internal string RequiredNeuralRenderingSha256 { get; init; } = NeuralRenderingSha256;

    /// <summary>Test seam: runs on the cached payload's path right before it is staged into the game folder.</summary>
    internal Action<string>? BeforeStagingForTesting { get; init; }
    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.RtxEncore);

    /// <summary>The installed name, else the saved choice, else <see cref="DefaultName"/>.</summary>
    public string ChosenName(DetectedGame game)
        => GetRecord(game)?.Metadata.GetValueOrDefault(MetaName) ?? _names.GetRtxEncore(game) ?? DefaultName;

    /// <summary>Whether the Neural Rendering file is installed (installed) or wanted at the next install (not installed).</summary>
    public bool NeuralRenderingChosen(DetectedGame game)
        => GetRecord(game) is { } record ? NeuralRenderingRuntimeOwnership.ListsRuntime(record) : _names.GetRtxEncoreNeuralRendering(game);

    /// <summary>
    /// The names RTX Encore can take for this game: its documented names without the Bink ones (they need the game's own
    /// Bink DLL chained as <c>*Hooked.dll</c>, not automated), without names the game's graphics API cannot load under
    /// Wine (<see cref="ProxyDllNames.LoadsFor"/>), without names another RHI component holds, and the <c>.asi</c> only
    /// while RHI manages an Ultimate ASI Loader for the game.
    /// </summary>
    public IReadOnlyList<string> OfferedNames(DetectedGame game)
    {
        var held = HeldNames(game);
        var hasLoader = _records.Find(game, ManagedComponentKind.UltimateAsiLoader) is not null;
        return RtxEncoreNames.All
            .Where(name => !RtxEncoreNames.IsBink(name))
            .Where(name => RtxEncoreNames.IsAsi(name) ? hasLoader : ProxyDllNames.LoadsFor(name, game.EffectiveGraphicsApi))
            .Where(name => !held.Contains(name))
            .ToArray();
    }

    /// <summary>Why RTX Encore cannot apply to the game (row hidden), or null when it can.</summary>
    public static string? IneligibleReason(DetectedGame game, bool gameHasDlss)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeLinuxMessage;
        if (GameArchitecture.Of(game) != PeArchitecture.X64) return NeedsX64Message;
        if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.DirectX12 or GraphicsApi.Vulkan)) return ApiMessage;
        if (!gameHasDlss) return NeedsDlssMessage;
        return null;
    }

    /// <summary>Whether the game ships DLSS (<c>nvngx_dlss.dll</c> anywhere under it, RHI's OptiScaler folder excluded).</summary>
    public static bool HasGameDlss(DetectedGame game)
    {
        var root = !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath) ? game.InstallPath : ResolveBinaryDirectory(game);
        if (root is null) return false;
        return GameFileIndexer.Build(root, 8)
            .Where(path => !DlssManagerService.IsUnderOptiScalerFolder(root, path))
            .Any(path => Path.GetFileName(path).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
    }

    public ComponentOperationResult SetName(DetectedGame game, string name)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(NameLockedMessage);
        var canonical = RtxEncoreNames.Canonical(name);
        if (canonical is null || !OfferedNames(game).Contains(canonical, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail(NameNotOfferedPrefix + name);
        try { _names.SetRtxEncore(game, canonical.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? null : canonical); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok(SavedNamePrefix + canonical);
    }

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (IneligibleReason(game, HasGameDlss(game)) is { } ineligible) return ComponentOperationResult.Fail(ineligible);
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return ComponentOperationResult.Fail(PrefixMessage);
        var binaryDir = ResolveBinaryDirectory(game);
        if (binaryDir is null) return ComponentOperationResult.Fail(PrefixMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return ComponentOperationResult.Fail(wineRunning);

        var name = ChosenName(game);
        if (!OfferedNames(game).Contains(name, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail((HeldNames(game).Contains(name) ? NameHeldPrefix : NameNotOfferedPrefix) + name);
        var target = ExistingSlot(binaryDir, name) ?? Path.Combine(binaryDir, name);
        var keep = File.Exists(target) ? target + KeptSuffix : null;
        if (keep is not null && ExistingSlot(binaryDir, Path.GetFileName(keep)) is { } keepInTheWay)
            return ComponentOperationResult.Fail(KeptCopyInTheWayPrefix + keepInTheWay);

        // One owner of nvngx_dlssnr.dll (#157): refused before any download, naming the owner.
        var withNr = _names.GetRtxEncoreNeuralRendering(game);
        string? nrTarget = null, nrKeep = null;
        if (withNr)
        {
            if (NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.RtxEncore) is { } owner)
                return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(owner));
            nrTarget = ExistingSlot(binaryDir, NeuralRenderingRuntimeOwnership.DllName) ?? Path.Combine(binaryDir, NeuralRenderingRuntimeOwnership.DllName);
            nrKeep = File.Exists(nrTarget) ? nrTarget + KeptSuffix : null;
            if (nrKeep is not null && ExistingSlot(binaryDir, Path.GetFileName(nrKeep)) is { } nrKeepInTheWay)
                return ComponentOperationResult.Fail(KeptCopyInTheWayPrefix + nrKeepInTheWay);
        }

        RtxEncoreReleaseInfo release;
        string payload;
        string? nrSource = null;
        try
        {
            release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            payload = await EnsureCachedPayloadAsync(release, cancellationToken).ConfigureAwait(false);
            if (withNr)
            {
                var (path, refusal) = await EnsureNeuralRenderingCachedAsync(cancellationToken).ConfigureAwait(false);
                if (refusal is not null) return ComponentOperationResult.Fail(refusal);
                nrSource = path;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RTX Encore install: " + game.Name, game, ManagedComponentKind.RtxEncore, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRecord? wineOverride = null;
        string? staged = null;
        // Downloaded and checked: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            BeforeStagingForTesting?.Invoke(payload);
            var stagedCopy = await AtomicFile.StageCopyAsync(payload, target, null, null, cancellationToken).ConfigureAwait(false);
            staged = stagedCopy.Path;
            ValidatePayload(staged);
            if (!RtxEncoreNames.IsAsi(name))
                wineOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, name, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
            if (keep is not null) await KeepAsync(scope, target, keep, cancellationToken).ConfigureAwait(false);
            await scope.PlaceStagedAsync(stagedCopy, target, cancellationToken, overwrite: false).ConfigureAwait(false);
            staged = null;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaName] = name,
                [MetaTag] = release.Tag,
                [MetaAssetName] = release.AssetName,
            };
            if (keep is not null) metadata[MetaKept] = Path.GetFileName(keep);
            // Fail-closed source: the zip matched GitHub's digest or the publisher's SHA256SUMS (refused otherwise).
            VerifiedDownload.Record(metadata, stagedCopy.Sha256, digestVerified: true);
            var files = new List<LinuxDeployedFile>
            {
                new() { Path = target, ExistedBeforeInstall = keep is not null, Sha256 = stagedCopy.Sha256, AllowUserChanges = false }
            };
            if (nrSource is not null)
                files.Add(await PlaceNeuralRenderingAsync(scope, nrSource, nrTarget!, nrKeep, metadata, cancellationToken).ConfigureAwait(false));

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.RtxEncore,
                BinaryDirectory = binaryDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files = files,
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            if (staged is not null) FileCleanup.TryDelete(staged);
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            _overrides.TryRestore(game.PrefixPath, wineOverride);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The record carries the operation id: the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + name + (nrSource is null ? "" : InstalledWithNeuralRenderingSuffix), target);
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return ComponentOperationResult.Fail(wineRunning);
        if (await FirstModifiedAsync(record, cancellationToken).ConfigureAwait(false) is { } modified)
            return ComponentOperationResult.Fail(ModifiedPrefix + modified);

        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "RTX Encore removal: " + game.Name, game, ManagedComponentKind.RtxEncore, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;
        var missing = new List<string>();
        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var file in record.Files)
            {
                await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                var keptName = NeuralRenderingRuntimeOwnership.DllName.Equals(Path.GetFileName(file.Path), StringComparison.OrdinalIgnoreCase)
                    ? record.Metadata.GetValueOrDefault(MetaNrKept)
                    : record.Metadata.GetValueOrDefault(MetaKept);
                if (string.IsNullOrWhiteSpace(keptName)) continue;
                var kept = Path.Combine(Path.GetDirectoryName(file.Path)!, keptName);
                if (File.Exists(kept)) await RestoreKeptAsync(scope, kept, file.Path, cancellationToken).ConfigureAwait(false);
                else missing.Add(kept);
            }
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.RtxEncore);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "rtx-encore").ConfigureAwait(false);
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored) _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        var missingNote = missing.Count == 0 ? "" : KeptCopyMissingPrefix + string.Join(", ", missing);
        return ComponentOperationResult.Ok(RemovedMessage + WineDllOverrideService.KeptValueNotes(overrideRestore) + missingNote);
    }

    /// <summary>
    /// The ⚙ "Neural Rendering file" choice. Not installed: saved for the next install. Installed: puts NVIDIA's 310.8.0
    /// file beside RTX Encore (refused while another component owns <c>nvngx_dlssnr.dll</c>) or takes RTX Encore's copy
    /// away again (a file kept from before goes back), journaled like an install.
    /// </summary>
    public async Task<ComponentOperationResult> SetNeuralRenderingAsync(DetectedGame game, bool enabled, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return SetNeuralRenderingPreference(game, enabled);
        var nrFile = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(NeuralRenderingRuntimeOwnership.DllName, StringComparison.OrdinalIgnoreCase));
        if (enabled == (nrFile is not null)) return ComponentOperationResult.Ok(NeuralRenderingAlreadyMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return ComponentOperationResult.Fail(wineRunning);

        string? nrSource = null, nrTarget = null, nrKeep = null;
        if (enabled)
        {
            if (NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.RtxEncore) is { } owner)
                return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(owner));
            nrTarget = ExistingSlot(record.BinaryDirectory, NeuralRenderingRuntimeOwnership.DllName) ?? Path.Combine(record.BinaryDirectory, NeuralRenderingRuntimeOwnership.DllName);
            nrKeep = File.Exists(nrTarget) ? nrTarget + KeptSuffix : null;
            if (nrKeep is not null && ExistingSlot(record.BinaryDirectory, Path.GetFileName(nrKeep)) is { } inTheWay)
                return ComponentOperationResult.Fail(KeptCopyInTheWayPrefix + inTheWay);
            try
            {
                var (path, refusal) = await EnsureNeuralRenderingCachedAsync(cancellationToken).ConfigureAwait(false);
                if (refusal is not null) return ComponentOperationResult.Fail(refusal);
                nrSource = path;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
            }
        }
        else if (File.Exists(nrFile!.Path) && !FileHash.HexEquals(await FileHash.Sha256HexAsync(nrFile.Path, cancellationToken).ConfigureAwait(false), nrFile.Sha256))
        {
            return ComponentOperationResult.Fail(ModifiedPrefix + nrFile.Path);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RTX Encore Neural Rendering file: " + game.Name, game, ManagedComponentKind.RtxEncore, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            if (enabled)
            {
                record.Files.Add(await PlaceNeuralRenderingAsync(scope, nrSource!, nrTarget!, nrKeep, record.Metadata, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                await scope.DeleteAsync(nrFile!.Path, cancellationToken).ConfigureAwait(false);
                if (record.Metadata.GetValueOrDefault(MetaNrKept) is { Length: > 0 } keptName
                    && Path.Combine(Path.GetDirectoryName(nrFile.Path)!, keptName) is var kept && File.Exists(kept))
                    await RestoreKeptAsync(scope, kept, nrFile.Path, cancellationToken).ConfigureAwait(false);
                record.Files.Remove(nrFile);
                record.Metadata.Remove(MetaNrKept);
                record.Metadata.Remove(MetaNrVersion);
                VerifiedDownload.Forget(record.Metadata, NrPart);
            }
            // The preference is saved before the commit point, so nothing after the record can fail into the rollback.
            _names.SetRtxEncoreNeuralRendering(game, enabled);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(enabled ? NeuralRenderingOnMessage : NeuralRenderingOffMessage);
    }

    /// <summary>The Neural Rendering choice saved for the next install (refused while another component owns the runtime).</summary>
    public ComponentOperationResult SetNeuralRenderingPreference(DetectedGame game, bool enabled)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (enabled && NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.RtxEncore) is { } owner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(owner));
        try { _names.SetRtxEncoreNeuralRendering(game, enabled); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok(enabled ? SavedWithNeuralRenderingMessage : SavedWithoutNeuralRenderingMessage);
    }

    public async Task<RtxEncoreUpdateStatus> CheckLatestAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var current = GetRecord(game)?.Version;
        try
        {
            var latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var hasUpdate = current is not null && !string.Equals(current, latest.Version, StringComparison.OrdinalIgnoreCase);
            return new(true, current, latest.Version, hasUpdate,
                current is null ? LatestPrefix + latest.Version
                : hasUpdate ? UpdateAvailablePrefix + current + " → " + latest.Version
                : UpToDatePrefix + current);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, current, null, false, CheckFailedPrefix + ex.Message);
        }
    }

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is null) return new ManagedUpdateCheck(false, false, NotInstalledMessage);
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: !status.Success);
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        var file = ProxyFile(record);
        if (file?.Sha256 is not { Length: > 0 }) return ComponentOperationResult.Fail(MissingPrefix + (file?.Path ?? record.BinaryDirectory));
        if (!File.Exists(file.Path)) return ComponentOperationResult.Fail(MissingPrefix + file.Path);
        if (!FileHash.HexEquals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256))
            return ComponentOperationResult.Fail(ModifiedPrefix + file.Path);

        RtxEncoreReleaseInfo latest;
        string payload;
        try
        {
            latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok(UpToDatePrefix + record.Version);
            payload = await EnsureCachedPayloadAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RTX Encore update: " + game.Name, game, ManagedComponentKind.RtxEncore, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        string? staged = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            BeforeStagingForTesting?.Invoke(payload);
            var stagedCopy = await AtomicFile.StageCopyAsync(payload, file.Path, null, null, cancellationToken).ConfigureAwait(false);
            staged = stagedCopy.Path;
            ValidatePayload(staged);
            await scope.PlaceStagedAsync(stagedCopy, file.Path, cancellationToken).ConfigureAwait(false);
            staged = null;
            file.Sha256 = stagedCopy.Sha256;
            record.Version = latest.Version;
            record.SourceUrl = latest.AssetUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaTag] = latest.Tag;
            record.Metadata[MetaAssetName] = latest.AssetName;
            VerifiedDownload.Record(record.Metadata, stagedCopy.Sha256, digestVerified: true);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            if (staged is not null) FileCleanup.TryDelete(staged);
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(UpdatedPrefix + latest.Version, file.Path);
    }

    /// <summary>The newest non-draft release (prereleases included: every RTX Encore release so far is one).</summary>
    internal async Task<RtxEncoreReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ReleasesApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        return ParseReleases(json);
    }

    internal static RtxEncoreReleaseInfo ParseReleases(string json)
    {
        try { return ParseReleasesCore(json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) // malformed JSON, or a value of the wrong kind
        {
            throw new InvalidDataException(ReleaseListShapeMessage, ex);
        }
    }

    private static RtxEncoreReleaseInfo ParseReleasesCore(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(ReleaseListShapeMessage);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string? assetName = null, assetUrl = null, assetSha = null, sumsUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = url;
                    continue;
                }
                if (assetName is not null || !name.StartsWith("RTX-Encore", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                assetName = name;
                assetUrl = url;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    assetSha = digest["sha256:".Length..].Trim().ToLowerInvariant();
            }
            if (assetName is null) continue;
            if (string.IsNullOrWhiteSpace(assetSha) && string.IsNullOrWhiteSpace(sumsUrl))
                throw new InvalidDataException(NoPublishedDigestPrefix + tag);
            return new RtxEncoreReleaseInfo(NormalizeVersion(tag), tag, assetName, assetUrl!, assetSha, sumsUrl);
        }
        throw new InvalidDataException(NoReleaseMessage);
    }

    internal static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Regex.IsMatch(value, @"^[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,40})?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(TagNotAVersionPrefix + tag);
        return value;
    }

    internal async Task<string> EnsureCachedPayloadAsync(RtxEncoreReleaseInfo release, CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(_cacheRoot, release.Version);
        var dllPath = Path.Combine(versionDir, PayloadName);
        Directory.CreateDirectory(versionDir);
        if (File.Exists(dllPath))
        {
            try
            {
                // Reuse only a DLL stamped at download time for this release asset (#142).
                if (!CacheStamp.IsValid(dllPath, release.AssetUrl, release.Version, release.AssetSha256))
                    throw new InvalidDataException("Cached payload has no valid download stamp.");
                ValidatePayload(dllPath);
                return dllPath;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                CacheStamp.Invalidate(dllPath);
                FileCleanup.TryDelete(dllPath);
            }
        }

        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, release.AssetUrl, Path.Combine(versionDir, release.AssetName), DownloadSources.RtxEncore,
            release.AssetSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(release.ChecksumsUrl))
        {
            var sums = await TrustedDownload.GetStringAsync(_http, release.ChecksumsUrl, DownloadSources.RtxEncore, cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = Rtx40MfgStandaloneDeploymentService.TryFindChecksum(sums, release.AssetName)
                           ?? throw new InvalidDataException(SumsNoEntryPrefix + release.AssetName);
            if (!FileHash.HexEquals(download.Sha256, expected))
                throw new InvalidDataException(SumsMismatchPrefix + release.AssetName);
        }

        using (var zip = ZipFile.OpenRead(download.Path))
        {
            // The release's own rtx-encore.dll at the zip root; the pre-named copies under alternative-proxies/ are other builds.
            var entries = zip.Entries.Where(e => e.FullName.Equals(PayloadName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException(PayloadNotAtRootMessage);
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entries[0], dllPath, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        try { ValidatePayload(dllPath); }
        catch
        {
            FileCleanup.TryDelete(dllPath);
            throw;
        }
        CacheStamp.Write(dllPath, release.AssetUrl, release.Version, release.AssetSha256);
        return dllPath;
    }

    /// <summary>A 64-bit Windows DLL carrying RTX Encore's own version resource (SilyNoMeta, "RTX Encore…").</summary>
    internal static void ValidatePayload(string path)
    {
        if (new FileInfo(path).Length < MinimumDllSize)
            throw new InvalidDataException(TooSmallMessage);
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException(NotX64Message);
        if (!BinarySignatureInspector.LooksLikeRtxEncore(path))
            throw new InvalidDataException(NoVersionResourceMessage);
    }

    /// <summary>
    /// NVIDIA's 310.8.0 runtime from the DLSS list (the entry whose version starts with 310.8.0), cached by the DLSS
    /// service; it must have <see cref="RequiredNeuralRenderingSha256"/>. Never another version: RTX Encore refuses it.
    /// </summary>
    private async Task<(string? Path, string? Refusal)> EnsureNeuralRenderingCachedAsync(CancellationToken cancellationToken)
    {
        if (_dlss is null) return (null, NeuralRenderingNeedsDlssListMessage);
        await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        var version = _dlss.Versions(DlssComponentKind.NeuralRendering).FirstOrDefault(IsRequiredNeuralRenderingVersion);
        if (version is null) return (null, NeuralRenderingUnavailableMessage);
        var cached = await _dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, version, cancellationToken).ConfigureAwait(false);
        if (cached is not { } source || !string.Equals(source.Version, version, StringComparison.OrdinalIgnoreCase))
            return (null, NeuralRenderingUnavailableMessage);
        if (!FileHash.HexEquals(await FileHash.Sha256HexAsync(source.Path, cancellationToken).ConfigureAwait(false), RequiredNeuralRenderingSha256))
            return (null, NeuralRenderingMismatchMessage);
        return (source.Path, null);
    }

    /// <summary>"310.8.0" or "310.8.0 (…)" — never 310.8.0x or 310.8.01.</summary>
    internal static bool IsRequiredNeuralRenderingVersion(string? version)
        => version is not null && version.StartsWith(NeuralRenderingVersion, StringComparison.Ordinal)
           && (version.Length == NeuralRenderingVersion.Length || version[NeuralRenderingVersion.Length] == ' ');

    private async Task<LinuxDeployedFile> PlaceNeuralRenderingAsync(
        DeploymentScope scope, string source, string target, string? keep, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        if (keep is not null)
        {
            await KeepAsync(scope, target, keep, cancellationToken).ConfigureAwait(false);
            metadata[MetaNrKept] = Path.GetFileName(keep);
        }
        var sha = await scope.PlaceCopyAsync(source, target, cancellationToken, overwrite: false).ConfigureAwait(false);
        metadata[MetaNrVersion] = NeuralRenderingVersion;
        VerifiedDownload.Record(metadata, sha, digestVerified: true, NrPart);
        return new LinuxDeployedFile { Path = target, ExistedBeforeInstall = keep is not null, Sha256 = sha, AllowUserChanges = false };
    }

    // A file RHI did not write is renamed to <name>.original (both tracked, the kept bytes declared first).
    private static async Task KeepAsync(DeploymentScope scope, string target, string keep, CancellationToken cancellationToken)
    {
        await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
        await scope.TrackAsync(keep, cancellationToken).ConfigureAwait(false);
        await scope.DeclareWriteAsync(keep, scope.Files.Find(target)!.PreSha256!, cancellationToken).ConfigureAwait(false);
        AtomicFile.Rename(target, keep);
    }

    // The kept file goes back under its name (both tracked, its bytes declared at the name first).
    private static async Task RestoreKeptAsync(DeploymentScope scope, string kept, string target, CancellationToken cancellationToken)
    {
        await scope.TrackAsync(kept, cancellationToken).ConfigureAwait(false);
        await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
        await scope.DeclareWriteAsync(target, scope.Files.Find(kept)!.PreSha256!, cancellationToken).ConfigureAwait(false);
        AtomicFile.Rename(kept, target, overwrite: true);
    }

    private static async Task<string?> FirstModifiedAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        foreach (var file in record.Files)
        {
            if (!File.Exists(file.Path)) continue;
            if (file.Sha256 is not { Length: > 0 } || !FileHash.HexEquals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256))
                return file.Path;
        }
        return null;
    }

    private static LinuxDeployedFile? ProxyFile(LinuxDeploymentRecord record)
    {
        var name = record.Metadata.GetValueOrDefault(MetaName);
        return record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // File names other RHI records hold in the game's executable folder.
    private HashSet<string> HeldNames(DetectedGame game)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var binaryDir = ResolveBinaryDirectory(game);
        if (binaryDir is null) return held;
        foreach (var record in _records.FindAll(game))
        {
            if (record.Component == ManagedComponentKind.RtxEncore) continue;
            foreach (var file in record.Files)
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.Path)), binaryDir, StringComparison.Ordinal))
                    held.Add(Path.GetFileName(file.Path));
        }
        return held;
    }

    private static string? ExistingSlot(string directory, string name)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var exact = Path.Combine(directory, name);
            return File.Exists(exact) ? exact : null;
        }
    }

    internal static string? ResolveBinaryDirectory(DetectedGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.BinaryDirectory) && Directory.Exists(game.BinaryDirectory))
            return Path.GetFullPath(game.BinaryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            return Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath));
        return null;
    }
}

public sealed record RtxEncoreReleaseInfo(string Version, string Tag, string AssetName, string AssetUrl, string? AssetSha256, string? ChecksumsUrl);

public sealed record RtxEncoreUpdateStatus(bool Success, string? InstalledVersion, string? LatestVersion, bool HasUpdate, string Message);
