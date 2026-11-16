using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What the Universal MFG Unlocker ⚙ shows about the selected game (#278).</summary>
/// <param name="AsPlugin">Installed (or would install) as an OptiScaler plugin.</param>
/// <param name="XessInstalled">Proxy install: both Intel files are in <c>RTXMFG-XeSS</c>; null when not applicable.</param>
/// <param name="ReShadeAsDxgi">ReShade is installed as dxgi.dll in the game folder.</param>
/// <param name="InstalledAs">The installed file's name relative to the game folder, or null.</param>
public sealed record UniversalMfgStatus(bool AsPlugin, bool? XessInstalled, bool ReShadeAsDxgi, string? InstalledAs);

/// <summary>
/// #278 (upstream 2.8.9 <c>Rtx40MfgService</c> / <c>MainViewModel.MfgPlacement</c>): Universal MFG Unlocker's Intel XeSS
/// files and its OptiScaler-plugin placement.
/// <list type="bullet">
/// <item>A proxy install gets Intel's <c>libxess_fg.dll</c> and <c>libxell.dll</c> in <c>RTXMFG-XeSS</c> beside the
/// proxy (the mod's XeMFG standalone mode looks there). Files RHI did not place are left alone. Installs from before get
/// them through Update / Update All (the check plans it), not at start-up.</item>
/// <item>With RHI's OptiScaler Nightly, F5 DLSSNR or DLSS NR (a build with an <c>OptiScaler</c> folder) the mod goes in
/// <c>OptiScaler/plugins/MFGUnlocker.asi</c>, with no Wine override and no Intel files (OptiScaler brings its own).
/// Installing such an OptiScaler moves a proxy install in; removing it moves the mod back to a proxy name first.</item>
/// </list>
/// </summary>
public sealed partial class Rtx40MfgStandaloneDeploymentService
{
    /// <summary>The row's name (upstream 2.8.9; formerly "RTX 40 MFG").</summary>
    public const string DisplayName = "Universal MFG Unlocker";
    /// <summary>The folder beside the proxy the mod loads Intel's XeSS files from.</summary>
    public const string XessFolderName = "RTXMFG-XeSS";
    /// <summary>Where OptiScaler 10 builds load plugins from, relative to the game folder (upstream's <c>PluginRelPath</c>).</summary>
    public const string PluginRelativePath = "OptiScaler/plugins/MFGUnlocker.asi";

    private const string MetaPlacement = "UniversalMFG.Placement";
    private const string PlacementPlugin = "OptiScalerPlugin";
    /// <summary>The proxy name a plugin install came from, preferred when it moves back (upstream's <c>.rhi-proxy.txt</c>).</summary>
    private const string MetaPluginFromProxy = "UniversalMFG.PluginFromProxy";
    private const string XessPrefix = "UniversalMFG";

    /// <summary>Proxy names tried, in order, when the mod moves out of OptiScaler (upstream's candidate order).</summary>
    private static readonly string[] MoveOutOrder = ["version.dll", "winmm.dll", "dinput8.dll"];

    // Fixed messages, values last (PT-BR presentation pairs in UiLocalizationService).
    public const string InstalledAsPluginPrefix = "Universal MFG Unlocker installed as an OptiScaler plugin (OptiScaler/plugins/MFGUnlocker.asi). Choose DLSSG or XeFG with FG Output in OptiScaler's ⚙. Version: ";
    public const string PluginSlotTakenPrefix = "Universal MFG Unlocker not installed: OptiScaler's plugins folder already has a MFGUnlocker.asi that is not RHI's: ";
    public const string XessNotInstalledPrefix = " Intel's XeSS files for XeMFG were not installed (Update adds them): ";
    public const string XessDueMessage = "Universal MFG Unlocker is up to date, but Intel's XeSS files for XeMFG are not installed yet; Update adds them.";
    public const string XessAddedMessage = "Universal MFG Unlocker: Intel's XeSS files for XeMFG installed in RTXMFG-XeSS.";
    public const string MovedIntoOptiScalerMessage = " Universal MFG Unlocker moved into OptiScaler's plugins folder, as its author recommends.";
    public const string MovedOutOfOptiScalerPrefix = " Universal MFG Unlocker moved back beside the game as ";
    public const string MoveFailedPrefix = " Universal MFG Unlocker was not moved: ";
    public const string NoFreeProxyNameMessage = "every proxy name it can use is already taken in the game folder.";

    /// <summary>Intel's XeSS files (null: none are placed, as in tests that predate #278).</summary>
    public IntelXessFiles? Xess { get; init; }

    /// <summary>Whether <paramref name="optiScaler"/> (RHI's record) is an OptiScaler build that loads plugins from its own folder.</summary>
    public static bool IsPluginHost(LinuxDeploymentRecord? optiScaler)
        => optiScaler is not null
           && OptiScalerDeploymentService.GetInstalledVariant(optiScaler) is var variant // w14/optiscaler-2 merge: versions are ids, not enum constants
           && (variant == OptiScalerVariant.Nightly || variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5)
           && Directory.Exists(Path.Combine(optiScaler.BinaryDirectory, "OptiScaler"));

    /// <summary>Whether an install of the mod now would go in OptiScaler's plugins folder.</summary>
    public bool WouldInstallAsPlugin(DetectedGame game) => IsPluginHost(_records.Find(game, ManagedComponentKind.OptiScaler));

    private static bool IsPluginRecord(LinuxDeploymentRecord record)
        => string.Equals(record.Metadata.GetValueOrDefault(MetaPlacement), PlacementPlugin, StringComparison.Ordinal);

    private IReadOnlyList<IntelXessStep> XessSteps(string binaryDirectory)
        => (Xess?.FrameGenerationFiles ?? IntelXessFiles.FrameGeneration).Select(file => new IntelXessStep(file, Path.Combine(binaryDirectory, XessFolderName, file.Name))).ToArray();

    /// <summary>A proxy install whose record has had no XeSS step yet (installed before #278, or the download failed).</summary>
    private bool NeedsXessFiles(LinuxDeploymentRecord record)
        => Xess is not null && !IsPluginRecord(record) && !IntelXessFiles.HasEntries(record.Metadata, XessPrefix);

    private async Task<(IReadOnlyDictionary<IntelXessFile, string>? Cache, string? Failure)> CacheXessAsync(CancellationToken cancellationToken)
    {
        if (Xess is null) return (null, null);
        try { return (await Xess.EnsureCachedAsync(Xess.FrameGenerationFiles, cancellationToken).ConfigureAwait(false), null); }
        catch (InvalidDataException ex) { return (null, ex.Message); }
    }

    /// <summary>Update of an up-to-date install from before #278: only the Intel files are added (journaled).</summary>
    private async Task<ComponentOperationResult> AddXessFilesAsync(DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var (cache, failure) = await CacheXessAsync(cancellationToken).ConfigureAwait(false);
        if (cache is null) return ComponentOperationResult.Fail(IntelXessFiles.DownloadFailedPrefix.TrimEnd() + " " + failure);
        var (opened, refusal) = await DeploymentScope.BeginAsync(
            _journal, "Universal MFG Unlocker XeSS files: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        IntelXessPlaced placed;
        try
        {
            placed = await IntelXessFiles.PlaceAsync(scope, XessSteps(record.BinaryDirectory), cache, cancellationToken).ConfigureAwait(false);
            var updated = LinuxDeploymentRecordStore.Clone(record);
            IntelXessFiles.MarkDone(updated.Metadata, XessPrefix, placed.Entries);
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            return ComponentOperationResult.Fail($"Universal MFG update failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(XessAddedMessage + IntelXessFiles.Notes(placed));
    }

    private static void DeleteEmptyPluginFolder(string binaryDirectory)
    {
        var plugins = Path.Combine(binaryDirectory, "OptiScaler", "plugins");
        try { if (Directory.Exists(plugins) && !Directory.EnumerateFileSystemEntries(plugins).Any()) Directory.Delete(plugins); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The ⚙'s facts for <paramref name="game"/> (#278).</summary>
    public UniversalMfgStatus Status(DetectedGame game)
    {
        var record = GetRecord(game);
        var binaryDir = record?.BinaryDirectory ?? ResolveBinaryDirectory(game);
        var asPlugin = record is not null ? IsPluginRecord(record) : WouldInstallAsPlugin(game);
        bool? xess = record is null || asPlugin || binaryDir is null
            ? null
            : (Xess?.FrameGenerationFiles ?? IntelXessFiles.FrameGeneration).All(file => File.Exists(Path.Combine(binaryDir, XessFolderName, file.Name)));
        var dxgi = binaryDir is not null && _records.Find(game, ManagedComponentKind.ReShade) is { } reshade
                   && reshade.Files.Any(file => Path.GetFileName(file.Path).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        var installedAs = record is null ? null : record.Metadata.GetValueOrDefault(MetaProxyName) ?? record.Metadata.GetValueOrDefault(LegacyMetaProxyName);
        return new UniversalMfgStatus(asPlugin, xess, dxgi, installedAs);
    }

    /// <summary>
    /// Moves a proxy install into OptiScaler's plugins folder (after an OptiScaler Nightly/F5/DLSS NR install): the
    /// plugin is placed, the proxy, its Wine override and the Intel files go, and the record follows. Null when there is
    /// nothing to move; otherwise the message tail to add to OptiScaler's result.
    /// </summary>
    public async Task<string?> MoveIntoOptiScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null || IsPluginRecord(record) || !IsPluginHost(_records.Find(game, ManagedComponentKind.OptiScaler))) return null;
        var proxyPath = ProxyPathOf(record);
        var fileRecord = proxyPath is null ? null : record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        if (proxyPath is null || fileRecord?.Sha256 is not { Length: > 0 } || !File.Exists(proxyPath)
            || !FileHash.HexEquals(await FileHash.Sha256HexAsync(proxyPath, cancellationToken).ConfigureAwait(false), fileRecord.Sha256))
            return MoveFailedPrefix + ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + (proxyPath ?? record.BinaryDirectory);
        var pluginPath = Path.Combine(record.BinaryDirectory, PluginRelativePath);
        if (File.Exists(pluginPath) || Directory.Exists(pluginPath)) return MoveFailedPrefix + PluginSlotTakenPrefix + pluginPath;
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return MoveFailedPrefix + wineRunning;

        var (opened, refusal) = await DeploymentScope.BeginAsync(
            _journal, "Universal MFG Unlocker into OptiScaler: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return MoveFailedPrefix + refusal;
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        WineOverrideRestoreResult? restored = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            await scope.PlaceCopyAsync(proxyPath, pluginPath, cancellationToken, fileRecord.Sha256, overwrite: false).ConfigureAwait(false);
            await scope.DeleteAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            await IntelXessFiles.RemoveAsync(scope, IntelXessFiles.Load(record.Metadata, XessPrefix), cancellationToken).ConfigureAwait(false);
            restored = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            var moved = LinuxDeploymentRecordStore.Clone(record);
            moved.Metadata[MetaPluginFromProxy] = Path.GetFileName(proxyPath);
            moved.Metadata[MetaProxyName] = PluginRelativePath;
            moved.Metadata[LegacyMetaProxyName] = PluginRelativePath;
            moved.Metadata[MetaPlacement] = PlacementPlugin;
            IntelXessFiles.Save(moved.Metadata, XessPrefix, []);
            moved.WineOverride = null;
            moved.Files = [new LinuxDeployedFile { Path = pluginPath, Sha256 = fileRecord.Sha256 }];
            _records.Upsert(scope.Stamp(moved));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return MovedIntoOptiScalerMessage;
            if (rollback.RolledBack && restored?.Outcome == WineOverrideRestoreOutcome.Restored) _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return MoveFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        IntelXessFiles.DeleteEmptyFolders(IntelXessFiles.Load(record.Metadata, XessPrefix));
        return MovedIntoOptiScalerMessage;
    }

    /// <summary>
    /// Moves a plugin install back beside the game under a proxy name (before RHI removes OptiScaler): the name it came
    /// from when free, else version.dll, winmm.dll, dinput8.dll, then the other proxy names. The Intel files come back too
    /// when they can be downloaded. Null when there is nothing to move; otherwise the message tail.
    /// </summary>
    public async Task<string?> MoveOutOfOptiScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null || !IsPluginRecord(record)) return null;
        var pluginPath = Path.Combine(record.BinaryDirectory, PluginRelativePath);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, pluginPath));
        if (fileRecord?.Sha256 is not { Length: > 0 } || !File.Exists(pluginPath)
            || !FileHash.HexEquals(await FileHash.Sha256HexAsync(pluginPath, cancellationToken).ConfigureAwait(false), fileRecord.Sha256))
            return MoveFailedPrefix + ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + pluginPath;
        if (string.IsNullOrWhiteSpace(game.PrefixPath)) return MoveFailedPrefix + PrefixMissingMessage;
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return MoveFailedPrefix + wineRunning;

        var preferred = CanonicalProxyOrNull(record.Metadata.GetValueOrDefault(MetaPluginFromProxy));
        var name = new[] { preferred }.Concat(MoveOutOrder).Concat(ProxyNames)
            .Where(candidate => candidate is not null && !IsBinkProxy(candidate))
            .Select(candidate => candidate!)
            .FirstOrDefault(candidate => FindExistingProxySlot(record.BinaryDirectory, candidate) is null
                                         && !IsRhiManagedPath(game, Path.Combine(record.BinaryDirectory, candidate)));
        if (name is null) return MoveFailedPrefix + NoFreeProxyNameMessage;
        var proxyPath = Path.Combine(record.BinaryDirectory, name);
        var (xessCache, xessFailure) = await CacheXessAsync(cancellationToken).ConfigureAwait(false);

        var (opened, refusal) = await DeploymentScope.BeginAsync(
            _journal, "Universal MFG Unlocker out of OptiScaler: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return MoveFailedPrefix + refusal;
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        WineOverrideRecord? wineOverride = null;
        IntelXessPlaced? xessPlaced = null;
        try
        {
            wineOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, name, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
            await scope.PlaceCopyAsync(pluginPath, proxyPath, cancellationToken, fileRecord.Sha256, overwrite: false).ConfigureAwait(false);
            await scope.DeleteAsync(pluginPath, cancellationToken).ConfigureAwait(false);
            if (xessCache is not null)
                xessPlaced = await IntelXessFiles.PlaceAsync(scope, XessSteps(record.BinaryDirectory), xessCache, cancellationToken).ConfigureAwait(false);
            var moved = LinuxDeploymentRecordStore.Clone(record);
            moved.Metadata.Remove(MetaPluginFromProxy);
            moved.Metadata.Remove(MetaPlacement);
            moved.Metadata[MetaProxyName] = name;
            moved.Metadata[LegacyMetaProxyName] = name;
            if (xessPlaced is not null) IntelXessFiles.MarkDone(moved.Metadata, XessPrefix, xessPlaced.Entries);
            moved.WineOverride = wineOverride;
            moved.Files = [new LinuxDeployedFile { Path = proxyPath, Sha256 = fileRecord.Sha256 }];
            _records.Upsert(scope.Stamp(moved));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return MovedOutOfOptiScalerPrefix + name + ".";
            if (rollback.RolledBack) _overrides.TryRestore(game.PrefixPath, wineOverride);
            return MoveFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        DeleteEmptyPluginFolder(record.BinaryDirectory);
        return MovedOutOfOptiScalerPrefix + name + "."
               + (xessPlaced is not null ? IntelXessFiles.Notes(xessPlaced) : xessFailure is not null ? XessNotInstalledPrefix + xessFailure : "");
    }

    public const string PrefixMissingMessage = "the game's Wine/Proton prefix was not found.";
}
