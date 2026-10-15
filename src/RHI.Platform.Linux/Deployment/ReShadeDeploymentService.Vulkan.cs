using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>ReShade as a Vulkan layer (#92): install and update; removal is the shared <see cref="RemoveAsync"/>.</summary>
public sealed partial class ReShadeDeploymentService
{
    private VulkanRuntimeProvider? _vulkanRuntime;
    private WineRegistryValueService? _registryValues;

    /// <summary>The Khronos loader source (LunarG); settable for tests.</summary>
    public VulkanRuntimeProvider VulkanRuntime
    {
        get => _vulkanRuntime ??= new VulkanRuntimeProvider(_http, Path.Combine(_cacheDir, "vulkan-runtime"));
        set => _vulkanRuntime = value;
    }

    /// <summary>The prefix registry value writer (the layer's ImplicitLayers registration in <c>system.reg</c>).</summary>
    public WineRegistryValueService RegistryValues => _registryValues ??= new WineRegistryValueService(_overrides.Registry);

    /// <summary>Test seam: runs inside the install right after the layer DLL is placed (to fail or kill it mid-way).</summary>
    internal Action? AfterVulkanLayerPlacedForTesting { get; set; }

    private async Task<ComponentOperationResult> InstallVulkanLayerAsync(
        DetectedGame game, ReShadeInstallOptions options, LinuxDeploymentRecord? existing, PeArchitecture architecture, bool isUpdate,
        CancellationToken cancellationToken)
    {
        // One ReShade per game (#120): a DLL ReShade is never converted into the layer.
        if (existing is not null && !ReShadeVulkanLayer.IsLayerRecord(existing))
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.DllReShadeInstalledMessage);
        var refusal = existing is null ? ReShadeVulkanLayer.OfferRefusal(game) : ReShadeVulkanLayer.PrefixRefusal(game);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        var prefix = game.PrefixPath!;
        var binaryDirectory = game.BinaryDirectory!;
        var is32 = architecture == PeArchitecture.X86;
        if (ReShadeVulkanLayer.ForeignLoader(binaryDirectory, existing) is { } foreignLoader)
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.GameShipsLoaderPrefix + foreignLoader);
        if (existing is null && ProxyPayloadLocator.FindReShadeOnDisk(binaryDirectory) is { } other)
            return ComponentOperationResult.Fail(OtherReShadePresentPrefix + other);

        // Where everything goes: an installed layer keeps its own paths (the record decides), a new one gets the game's folder.
        var previousDll = existing is null ? null : ProxyPayloadLocator.ReShadePayload(existing);
        var previousLoader = existing?.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(ReShadeVulkanLayer.LoaderFileName, StringComparison.OrdinalIgnoreCase));
        var previousManifest = existing?.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(ReShadeVulkanLayer.ManifestName(is32), StringComparison.OrdinalIgnoreCase));
        if (existing is not null && (previousDll is null || previousLoader is null || previousManifest is null || existing.RegistryValues.Count == 0))
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.IncompleteRecordMessage);
        var (folder, windowsFolder) = ReShadeVulkanLayer.LayerFolder(game);
        var layerDll = previousDll?.Path ?? Path.Combine(folder, ReShadeVulkanLayer.LayerDllName(is32));
        var manifest = previousManifest?.Path ?? Path.Combine(folder, ReShadeVulkanLayer.ManifestName(is32));
        var loaderTarget = previousLoader?.Path ?? Path.Combine(binaryDirectory, ReShadeVulkanLayer.LoaderFileName);
        foreach (var file in new[] { previousDll, previousLoader, previousManifest })
        {
            if (file is null || !File.Exists(file.Path)) continue;
            if (!string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(ReShadeVulkanLayer.ModifiedFilePrefix + file.Path);
        }
        if (existing is null && (File.Exists(layerDll) || File.Exists(manifest)))
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.LayerFolderTakenPrefix + (File.Exists(layerDll) ? layerDll : manifest));

        // Downloads and staging, all before the first change.
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
        if (staged.FromCache && existing is not null)
        {
            var order = CompareNumericVersions(staged.Version, existing.Version);
            if (isUpdate && order is not > 0)
                return ComponentOperationResult.Unavailable($"ReShade server unavailable: {staged.FallbackReason} Installed version kept ({existing.Version}); nothing was changed; try again later.");
            if (order < 0)
                return ComponentOperationResult.Unavailable($"ReShade server unavailable: {staged.FallbackReason} The cached copy ({staged.Version}) is older than the installed version ({existing.Version}); nothing was changed; try again later.");
        }

        // The loader: a new install takes LunarG's newest; an update swaps it only when LunarG lists a newer one.
        string? loaderSource = null;
        VerifiedDownload? loaderDownload = null;
        var loaderVersion = existing?.Metadata.GetValueOrDefault(ReShadeVulkanLayer.MetaLoaderVersion);
        var loaderNote = "";
        try
        {
            if (existing is null)
            {
                loaderVersion = await VulkanRuntime.LatestVersionAsync(cancellationToken).ConfigureAwait(false);
                (loaderSource, loaderDownload) = await VulkanRuntime.EnsureLoaderAsync(loaderVersion, architecture, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? latest = null;
                try { latest = await VulkanRuntime.LatestVersionAsync(cancellationToken).ConfigureAwait(false); }
                catch (InvalidOperationException ex)
                {
                    AppLog.Warn("reshade-vulkan", "Vulkan loader version check failed; the installed loader is kept", ex);
                    loaderNote = ReShadeVulkanLayer.LoaderCheckSkippedNote;
                }
                if (latest is not null && VulkanRuntimeProvider.Compare(latest, loaderVersion) > 0)
                {
                    (loaderSource, loaderDownload) = await VulkanRuntime.EnsureLoaderAsync(latest, architecture, cancellationToken).ConfigureAwait(false);
                    loaderVersion = latest;
                    loaderNote = ReShadeVulkanLayer.LoaderUpdatedNote;
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"ReShade source preparation failed: {ex.Message}");
        }

        ReShadeShaderPackService.PreparedShaderSync preparedShaders;
        try
        {
            preparedShaders = await _shaderPacks.PrepareSyncAsync(binaryDirectory, options.ShaderPackIds, cancellationToken).ConfigureAwait(false);
            if (existing is not null) ReShadeShaderPackService.EnsureManifestReadable(binaryDirectory);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ReShade install/update failed: {ex.Message}");
        }

        // #170: the add-ons a channel switch turns off (No Addons) or back on, as for the DLL install.
        IReadOnlyList<string>? managerFiles = AddonManagerFiles is null ? [] : AddonManagerFiles(game);
        var (addonPlan, addonRefusal) = ReShadeAddonSwitch.PlanFor(_records.FindAll(game), managerFiles ?? [], addonSupport: staged.Channel != ReShadeChannel.NoAddons);
        if (addonRefusal is not null) return ComponentOperationResult.Fail(addonRefusal);
        var managerNote = managerFiles is null && (staged.Channel == ReShadeChannel.NoAddons || LacksAddonSupport(existing))
            ? ReShadeAddonSwitch.ManagerUnreadableNote : "";
        var addonMoves = new List<ReShadeAddonSwitch.Move>();
        var oldOptions = existing is null ? null : GetInstalledOptions(game);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"ReShade Vulkan layer {(isUpdate ? "update" : "install")}: {game.Name}", game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRecord? overrideRecord = existing?.WineOverride;
        WineRegistryValueRecord? layerValue = existing?.RegistryValues.FirstOrDefault();
        var overrideCreated = false;
        var valueCreated = false;
        var createdDirectories = new List<string>();
        // Downloaded and staged: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var ini = ReShadeIniManager.ResolveIniPath(binaryDirectory);
            await scope.TrackAsync(ini, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                CleanupManagedIni(existing);
                await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
                _shaderPacks.Cleanup(binaryDirectory);
            }

            foreach (var directory in MissingDirectories(Path.GetDirectoryName(layerDll)!, prefix))
            {
                // Journaled first: a crash before the record removes the empty folders with the files.
                if (scope.Operation is { } operation)
                    await operation.AddCompensationAsync(CreatedDirectoryCompensator.KindName, CreatedDirectoryCompensator.Describe(directory), cancellationToken).ConfigureAwait(false);
                AtomicFile.CreateDirectoryDurable(directory);
                createdDirectories.Add(directory);
            }
            var dllSha = await scope.PlaceCopyAsync(staged.DllPath, layerDll, cancellationToken).ConfigureAwait(false);
            AfterVulkanLayerPlacedForTesting?.Invoke();
            var manifestSha = await scope.PlaceBytesAsync(manifest, ReShadeVulkanLayer.Manifest(is32), cancellationToken).ConfigureAwait(false);
            var loaderSha = loaderSource is not null
                ? await scope.PlaceCopyAsync(loaderSource, loaderTarget, cancellationToken).ConfigureAwait(false)
                : previousLoader!.Sha256;

            if (overrideRecord is null)
            {
                overrideRecord = await _overrides.EnsureNativeBuiltinAsync(
                    scope.Operation, prefix, ReShadeVulkanLayer.LoaderOverrideName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                overrideCreated = true;
            }
            if (layerValue is null)
            {
                layerValue = await RegistryValues.EnsureAsync(scope.Operation, prefix, WineRegistryValueService.SystemHive,
                    ReShadeVulkanLayer.RegistryKey(is32), windowsFolder + @"\" + ReShadeVulkanLayer.ManifestName(is32),
                    WineRegistryValueService.Dword(0), cancellationToken, rhiNamePrefix: ReShadeVulkanLayerFolder.WindowsRoot + @"\").ConfigureAwait(false);
                valueCreated = true;
            }

            // ReShade's layer stays inactive without a ReShade.ini beside the game, so the layer always manages one.
            var shaderResult = _shaderPacks.ApplySync(preparedShaders, cancellationToken);
            var iniResult = ReShadeIniManager.Apply(binaryDirectory, shaderResult.EffectSearchPaths, shaderResult.TextureSearchPaths);
            await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);

            var files = new List<LinuxDeployedFile>
            {
                new() { Path = layerDll, ExistedBeforeInstall = false, Sha256 = dllSha },
                new() { Path = manifest, ExistedBeforeInstall = false, Sha256 = manifestSha },
                new() { Path = loaderTarget, ExistedBeforeInstall = false, Sha256 = loaderSha },
                new() { Path = iniResult.Path, ExistedBeforeInstall = iniResult.ExistedBefore, Sha256 = iniResult.Sha256, AllowUserChanges = true }
            };
            var metadata = existing?.Metadata is { } oldMetadata
                ? new Dictionary<string, string>(oldMetadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata[ReShadeVulkanLayer.MetaPath] = ReShadeVulkanLayer.PathValue;
            metadata[MetaChannel] = staged.Channel.ToString();
            metadata[MetaSourceIdentity] = staged.SourceIdentity;
            if (staged.Download is { } download) download.RecordIn(metadata);
            else VerifiedDownload.Forget(metadata);
            if (loaderDownload is not null) loaderDownload.RecordIn(metadata, ReShadeVulkanLayer.DownloadPartLoader);
            SetOrRemove(metadata, ReShadeVulkanLayer.MetaLoaderVersion, loaderVersion);
            metadata[MetaShaderPacks] = JsonSerializer.Serialize(shaderResult.PackIds);
            SetOrRemove(metadata, MetaCustomSource, options.Channel == ReShadeChannel.Custom ? staged.SourceUrl : null);
            SetOrRemove(metadata, MetaLegacyVersion, options.Channel == ReShadeChannel.Legacy ? options.LegacyVersion?.Trim() : null);
            WriteIniMetadata(metadata, iniResult);

            var record = scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.ReShade,
                BinaryDirectory = binaryDirectory,
                SourceUrl = staged.SourceUrl,
                Version = staged.Version,
                InstalledAtUtc = existing?.InstalledAtUtc ?? DateTime.UtcNow,
                WineOverride = overrideRecord,
                RegistryValues = [layerValue],
                DirectoriesCreatedByRhi = [.. existing?.DirectoriesCreatedByRhi ?? [], .. createdDirectories],
                Files = files,
                Metadata = metadata
            });
            await ReShadeAddonSwitch.ApplyAsync(addonPlan, scope.Operation, addonMoves).ConfigureAwait(false);
            _records.Commit([record, .. addonPlan.Records]);
        }
        catch (Exception ex)
        {
            ReShadeAddonSwitch.Undo(addonMoves);
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // Journaled, the rollback already undid these; unjournaled (or a journal step that failed) they go here. Both act
            // only while the value is still RHI's.
            if (valueCreated) RegistryValues.TryRestore(prefix, layerValue);
            if (overrideCreated) _overrides.TryRestore(prefix, overrideRecord);
            RemoveEmptyDirectories(createdDirectories);
            if (oldOptions is not null)
            {
                try
                {
                    await _shaderPacks.SyncAsync(binaryDirectory, oldOptions.ShaderPackIds, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    AppLog.Warn("reshade-vulkan", "Could not put the previous shader packs back after a failed update", restoreError);
                }
            }
            return ComponentOperationResult.Fail(ReShadeVulkanLayer.FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The record carries the operation id: the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        var cacheNote = staged.FromCache ? CachedCopyNote : "";
        return ComponentOperationResult.Ok(
            (existing is null ? ReShadeVulkanLayer.InstalledPrefix : ReShadeVulkanLayer.UpdatedPrefix) + staged.Version
            + loaderNote + cacheNote + ReShadeAddonSwitch.Summary(addonPlan) + managerNote, layerDll) with { FromCachedSource = staged.FromCache };
    }

    /// <summary>The folders from <paramref name="directory"/> up to (not including) <paramref name="root"/> that do not exist yet, outermost first.</summary>
    private static List<string> MissingDirectories(string directory, string root)
    {
        var missing = new List<string>();
        for (var current = Path.GetFullPath(directory);
             !Directory.Exists(current) && PathIdentity.IsSameOrInside(current, root) && !PathIdentity.IsSameOrInside(root, current);
             current = Path.GetDirectoryName(current)!)
            missing.Insert(0, current);
        return missing;
    }

    /// <summary>Removes folders RHI created, innermost first, while they are empty; anything else in them keeps them.</summary>
    private static void RemoveEmptyDirectories(IEnumerable<string> created)
    {
        foreach (var directory in created.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("reshade-vulkan", $"Could not remove the empty folder {directory}", ex);
            }
        }
    }
}
