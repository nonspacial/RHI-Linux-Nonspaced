using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What DX9 HDR's install needs to move ReShade from <c>d3d9.dll</c> to its Vulkan layer (#200), staged before the first change.</summary>
internal sealed record DxvkHdrReShadeMovePlan(
    LinuxDeploymentRecord Record, LinuxDeployedFile Proxy, bool Is32, string LoaderSource, VerifiedDownload LoaderDownload,
    string LoaderVersion, string LayerDll, string Manifest, string LoaderTarget, string WindowsFolder, bool NeedsIni);

/// <summary>
/// What the move did inside DX9 HDR's operation: the ReShade record to save in the same ledger write, the
/// <c>d3d9=native,builtin</c> override ReShade's proxy held (DX9 HDR's d3d9.dll keeps it), and what to undo unjournaled.
/// </summary>
internal sealed record DxvkHdrReShadeMove(
    LinuxDeploymentRecord ReShadeRecord, WineOverrideRecord? D3d9Override, WineOverrideRecord LoaderOverride,
    WineRegistryValueRecord LayerValue, IReadOnlyList<string> CreatedDirectories);

/// <summary>What DX9 HDR's removal needs to put ReShade back at <c>d3d9.dll</c>, checked before the first change.</summary>
internal sealed record DxvkHdrReShadeBackPlan(LinuxDeploymentRecord Record, LinuxDeployedFile LayerDll, string ProxyPath);

/// <summary>What the move back did: the ReShade record to save in the removal's commit write and the restores to undo on failure.</summary>
internal sealed record DxvkHdrReShadeBack(
    LinuxDeploymentRecord ReShadeRecord, IReadOnlyList<(WineOverrideRecord Record, WineOverrideRestoreResult Result)> OverrideRestores,
    IReadOnlyList<(WineRegistryValueRecord Record, WineOverrideRestoreResult Result)> ValueRestores, IReadOnlyList<string> LayerDirectories);

/// <summary>
/// #200 (upstream <c>DxvkService.Install.cs</c> DX9 direct mode): with DX9 HDR, DXVK HDR becomes the game's
/// <c>d3d9.dll</c>, so an RHI ReShade installed as <c>d3d9.dll</c> moves to the per-game Vulkan layer (#92: the Khronos
/// loader beside the exe, <c>vulkan-1=native,builtin</c>, the layer registered in <c>system.reg</c>), which DXVK HDR's
/// <c>d3d9.dll</c> reaches because it loads <c>vulkan-1.dll</c> first (<c>reviews/w7-dxvk-hdr-wine.md</c>). Removing DX9
/// HDR puts ReShade back at <c>d3d9.dll</c>. Both directions run INSIDE DX9 HDR's operation (the pattern of
/// <see cref="Dx9TranslationReShadeCoordinator.MigrateInsideAsync"/>): every file tracked and declared in its scope, every
/// Wine step journaled through its operation, and the returned ReShade record saved in DX9 HDR's own commit write.
/// </summary>
public sealed partial class ReShadeDeploymentService
{
    /// <summary>Record metadata: this layer ReShade was moved here from <c>d3d9.dll</c> by DX9 HDR.</summary>
    internal const string MetaMovedByDxvkHdr = "ReShade.MovedByDxvkHdr";
    /// <summary>Record metadata: the folders the proxy ReShade's record listed before the move (JSON), given back on the move back.</summary>
    private const string MetaDirectoriesBeforeDxvkHdr = "ReShade.DirectoriesBeforeDxvkHdr";

    // Platform messages: fixed text first, values last (PT-BR presentation pairs).
    public const string DxvkHdrReShadeNamePrefix =
        "ReShade is installed on this game under another DLL name, and DX9 HDR can only move a ReShade installed as d3d9.dll to its Vulkan path, so nothing was changed. Remove ReShade from its row, install DX9 HDR, then install ReShade again. ReShade is installed now as: ";
    public const string DxvkHdrReShadeModifiedPrefix =
        "RHI's ReShade DLL was changed outside RHI, so DX9 HDR did not move it and nothing was changed: ";
    public const string DxvkHdrReShadeOriginalPrefix =
        "ReShade took the place of a d3d9.dll that was there before RHI installed it, so DX9 HDR cannot take that name and nothing was changed. Remove ReShade from its row first: ";
    /// <summary>
    /// RHI's ReShade is an XNA install (#201), loaded from the prefix's .NET assembly folder, which DX9 HDR's d3d9.dll in
    /// the game folder never reaches (PT-BR pair).
    /// </summary>
    public const string DxvkHdrReShadeXnaGacMessage =
        "ReShade on this game is installed for XNA, which loads d3d9.dll only from the .NET assembly folder of its prefix, so DX9 HDR cannot work beside it and nothing was changed.";
    public const string DxvkHdrReShadeShortFuseMessage =
        "ReShade on this game is loaded through ShortFuse's ASI loader, which DX9 HDR cannot move, so nothing was changed. Remove ShortFuse first.";

    /// <summary>
    /// Why DX9 HDR cannot move this ReShade (an RHI DLL ReShade) to the Vulkan layer, or null. Checked before any download
    /// or change; every reason names what the user can do in RHI.
    /// </summary>
    internal string? DxvkHdrMoveRefusal(DetectedGame game, LinuxDeploymentRecord record)
    {
        if (ReShadeVulkanLayer.IsLayerRecord(record)) return null;
        if (XnaGacReShade.IsGacRecord(record)) return DxvkHdrReShadeXnaGacMessage; // w7 integration (#200 × #201)
        if (record.Metadata.ContainsKey(MetaShortFuseOriginalPayloadPath)) return DxvkHdrReShadeShortFuseMessage;
        var proxy = ProxyPayloadLocator.ReShadePayload(record);
        var d3d9 = Path.Combine(game.BinaryDirectory!, "d3d9.dll");
        if (proxy is null || !Path.GetFullPath(proxy.Path).Equals(Path.GetFullPath(d3d9), StringComparison.Ordinal))
            return DxvkHdrReShadeNamePrefix + (proxy is null ? "?" : Path.GetFileName(proxy.Path));
        if (proxy.ExistedBeforeInstall) return DxvkHdrReShadeOriginalPrefix + proxy.Path;
        if (!File.Exists(proxy.Path) || string.IsNullOrWhiteSpace(proxy.Sha256)
            || !FileHash.HexEquals(FileHash.Sha256Hex(proxy.Path), proxy.Sha256))
            return DxvkHdrReShadeModifiedPrefix + proxy.Path;
        if (ReShadeVulkanLayer.PrefixRefusal(game) is { } prefix) return prefix;
        if (ReShadeVulkanLayer.ForeignLoader(game.BinaryDirectory!, null) is { } loader) return ReShadeVulkanLayer.GameShipsLoaderPrefix + loader;
        var is32 = GameArchitecture.Of(game) == PeArchitecture.X86;
        var (folder, _) = ReShadeVulkanLayer.LayerFolder(game);
        foreach (var taken in new[] { Path.Combine(folder, ReShadeVulkanLayer.LayerDllName(is32)), Path.Combine(folder, ReShadeVulkanLayer.ManifestName(is32)) })
            if (File.Exists(taken)) return ReShadeVulkanLayer.LayerFolderTakenPrefix + taken;
        return null;
    }

    /// <summary>The network part of the move (the Khronos loader, LunarG's newest), before DX9 HDR's write gate. Throws on failure.</summary>
    internal async Task<DxvkHdrReShadeMovePlan> PrepareDxvkHdrMoveAsync(DetectedGame game, LinuxDeploymentRecord record, PeArchitecture architecture, CancellationToken cancellationToken)
    {
        var is32 = architecture == PeArchitecture.X86;
        var version = await VulkanRuntime.LatestVersionAsync(cancellationToken).ConfigureAwait(false);
        var (loader, download) = await VulkanRuntime.EnsureLoaderAsync(version, architecture, cancellationToken).ConfigureAwait(false);
        var (folder, windowsFolder) = ReShadeVulkanLayer.LayerFolder(game);
        var needsIni = !File.Exists(ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!));
        return new DxvkHdrReShadeMovePlan(record, ProxyPayloadLocator.ReShadePayload(record)!, is32, loader, download, version,
            Path.Combine(folder, ReShadeVulkanLayer.LayerDllName(is32)), Path.Combine(folder, ReShadeVulkanLayer.ManifestName(is32)),
            Path.Combine(game.BinaryDirectory!, ReShadeVulkanLayer.LoaderFileName), windowsFolder, needsIni);
    }

    /// <summary>
    /// Moves ReShade from <c>d3d9.dll</c> to the Vulkan layer inside <paramref name="scope"/> (DX9 HDR's install): the proxy's
    /// bytes become the layer DLL (the same ReShade build), then the manifest, the Khronos loader, the loader override and
    /// the layer registration; ReShade.ini stays (one is written when there is none: the layer needs it). Saves nothing:
    /// the returned record goes into the caller's commit write.
    /// </summary>
    internal async Task<DxvkHdrReShadeMove> MoveToLayerForDxvkHdrInsideAsync(DetectedGame game, DxvkHdrReShadeMovePlan plan, DeploymentScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var prefix = game.PrefixPath!;
        var created = new List<string>();
        foreach (var directory in MissingDirectories(Path.GetDirectoryName(plan.LayerDll)!, prefix))
        {
            if (scope.Operation is { } operation)
                await operation.AddCompensationAsync(CreatedDirectoryCompensator.KindName, CreatedDirectoryCompensator.Describe(directory), cancellationToken).ConfigureAwait(false);
            AtomicFile.CreateDirectoryDurable(directory);
            created.Add(directory);
        }
        var dllSha = await scope.PlaceCopyAsync(plan.Proxy.Path, plan.LayerDll, cancellationToken, plan.Proxy.Sha256, overwrite: false).ConfigureAwait(false);
        await scope.DeleteAsync(plan.Proxy.Path, cancellationToken).ConfigureAwait(false);
        var manifestSha = await scope.PlaceBytesAsync(plan.Manifest, ReShadeVulkanLayer.Manifest(plan.Is32), cancellationToken).ConfigureAwait(false);
        var loaderSha = await scope.PlaceCopyAsync(plan.LoaderSource, plan.LoaderTarget, cancellationToken, overwrite: false).ConfigureAwait(false);
        var loaderOverride = await _overrides.EnsureNativeBuiltinAsync(
            scope.Operation, prefix, ReShadeVulkanLayer.LoaderOverrideName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
        var layerValue = await RegistryValues.EnsureAsync(scope.Operation, prefix, WineRegistryValueService.SystemHive,
            ReShadeVulkanLayer.RegistryKey(plan.Is32), plan.WindowsFolder + @"\" + ReShadeVulkanLayer.ManifestName(plan.Is32),
            WineRegistryValueService.Dword(0), cancellationToken, rhiNamePrefix: ReShadeVulkanLayerFolder.WindowsRoot + @"\").ConfigureAwait(false);

        var record = LinuxDeploymentRecordStore.Clone(plan.Record);
        if (plan.NeedsIni)
        {
            // #213 (w8 integration): the file the write replaces, under the one ReShade.ini link rule every writer follows.
            // The audit's dangling-link case (a link to a shared ~/.config/ReShade file not made yet) never reaches here:
            // .NET counts a dangling link as an existing file, so NeedsIni is false for it (w8 follow-up C15; UPSTREAM.md).
            var ini = ReShadeIniWriteTarget(game, ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!));
            await scope.TrackAsync(ini, cancellationToken).ConfigureAwait(false);
            var iniResult = ReShadeIniManager.Apply(game.BinaryDirectory!, [], [], IniDefaults?.Invoke(game, true), game.InstallPath);
            await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
            record.Files.Add(new LinuxDeployedFile { Path = iniResult.Path, ExistedBeforeInstall = iniResult.ExistedBefore, Sha256 = iniResult.Sha256, AllowUserChanges = true });
            WriteIniMetadata(record.Metadata, iniResult);
        }
        record.Files.RemoveAll(file => Path.GetFullPath(file.Path).Equals(Path.GetFullPath(plan.Proxy.Path), StringComparison.Ordinal));
        record.Files.Add(new LinuxDeployedFile { Path = plan.LayerDll, ExistedBeforeInstall = false, Sha256 = dllSha });
        record.Files.Add(new LinuxDeployedFile { Path = plan.Manifest, ExistedBeforeInstall = false, Sha256 = manifestSha });
        record.Files.Add(new LinuxDeployedFile { Path = plan.LoaderTarget, ExistedBeforeInstall = false, Sha256 = loaderSha });
        var d3d9Override = record.WineOverride is { } proxyOverride
                           && string.Equals(Path.GetFileNameWithoutExtension(proxyOverride.DllName), "d3d9", StringComparison.OrdinalIgnoreCase)
            ? proxyOverride
            : null;
        record.WineOverride = loaderOverride;
        record.RegistryValues = [layerValue];
        record.Metadata[ReShadeVulkanLayer.MetaPath] = ReShadeVulkanLayer.PathValue;
        record.Metadata[ReShadeVulkanLayer.MetaLoaderVersion] = plan.LoaderVersion;
        plan.LoaderDownload.RecordIn(record.Metadata, ReShadeVulkanLayer.DownloadPartLoader);
        record.Metadata[MetaMovedByDxvkHdr] = "1";
        record.Metadata[MetaDirectoriesBeforeDxvkHdr] = JsonSerializer.Serialize(record.DirectoriesCreatedByRhi);
        record.DirectoriesCreatedByRhi = [.. record.DirectoriesCreatedByRhi, .. created];
        return new DxvkHdrReShadeMove(record, d3d9Override, loaderOverride, layerValue, created);
    }

    /// <summary>After a failed DX9 HDR install that ran the move: the Wine steps it made go (no-op where the journaled
    /// rollback already put them back) and the folders it created go while empty. Never throws.</summary>
    internal void UndoDxvkHdrMoveInside(DetectedGame game, DxvkHdrReShadeMove move)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath)) return;
        RegistryValues.TryRestore(game.PrefixPath, move.LayerValue);
        _overrides.TryRestore(game.PrefixPath, move.LoaderOverride);
        RemoveEmptyDirectories(move.CreatedDirectories);
    }

    /// <summary>
    /// Whether DX9 HDR's removal can put this layer ReShade back at <c>d3d9.dll</c>: its layer DLL, manifest and loader
    /// must still be what RHI placed. Returns the plan or the refusal (nothing changed).
    /// </summary>
    internal async Task<(DxvkHdrReShadeBackPlan? Plan, string? Refusal)> PrepareDxvkHdrMoveBackAsync(DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var layerDll = ProxyPayloadLocator.ReShadePayload(record);
        if (layerDll is null || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return (null, ReShadeVulkanLayer.IncompleteRecordMessage);
        var plan = new DxvkHdrReShadeBackPlan(record, layerDll, Path.Combine(game.BinaryDirectory, "d3d9.dll"));
        foreach (var file in record.Files.Where(file => IsLayerFile(file, plan)))
        {
            if (!File.Exists(file.Path))
            {
                if (ReferenceEquals(file, layerDll)) return (null, ReShadeVulkanLayer.ModifiedFilePrefix + file.Path);
                continue;
            }
            if (!string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, ReShadeVulkanLayer.ModifiedFilePrefix + file.Path);
        }
        if (_overrides.EditRefusal(game.PrefixPath) is { } running) return (null, running);
        return (plan, null);
    }

    /// <summary>
    /// Puts ReShade back at <c>d3d9.dll</c> inside DX9 HDR's removal (after its d3d9.dll left): the layer DLL's bytes become
    /// the proxy again, the manifest and loader go, the loader override and the layer registration are restored (each only
    /// while it is still RHI's value), and ReShade's record takes the <c>d3d9=native,builtin</c> override back. Saves
    /// nothing: the returned record goes into the removal's commit write.
    /// </summary>
    internal async Task<DxvkHdrReShadeBack> MoveBackFromDxvkHdrInsideAsync(
        DetectedGame game, DxvkHdrReShadeBackPlan plan, WineOverrideRecord? d3d9Override, DeploymentScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var record = LinuxDeploymentRecordStore.Clone(plan.Record);
        var proxySha = await scope.PlaceCopyAsync(plan.LayerDll.Path, plan.ProxyPath, cancellationToken, plan.LayerDll.Sha256, overwrite: false).ConfigureAwait(false);
        var layerFiles = record.Files.Where(file => IsLayerFile(file, plan)).ToList();
        foreach (var file in layerFiles) await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
        var overrideRestores = new List<(WineOverrideRecord, WineOverrideRestoreResult)>();
        var valueRestores = new List<(WineRegistryValueRecord, WineOverrideRestoreResult)>();
        if (game.PrefixPath is { } prefix && Directory.Exists(prefix))
        {
            if (record.WineOverride is { } loaderOverride)
                overrideRestores.Add((loaderOverride, await _overrides.RestoreAsync(scope.Operation, prefix, loaderOverride, cancellationToken).ConfigureAwait(false)));
            foreach (var value in record.RegistryValues)
                valueRestores.Add((value, await RegistryValues.RestoreAsync(scope.Operation, prefix, value, cancellationToken).ConfigureAwait(false)));
        }

        // The files RHI placed for the layer (DLL, manifest, loader) leave the record; the DLL comes back as the proxy.
        // ReShade's own files (ReShade.ini, a native D3DCompiler_47.dll) stay as they are.
        record.Files.RemoveAll(file => IsLayerFile(file, plan));
        record.Files.Insert(0, new LinuxDeployedFile { Path = plan.ProxyPath, ExistedBeforeInstall = false, Sha256 = proxySha });
        record.WineOverride = d3d9Override;
        record.RegistryValues = [];
        record.Metadata.Remove(ReShadeVulkanLayer.MetaPath);
        record.Metadata.Remove(ReShadeVulkanLayer.MetaLoaderVersion);
        VerifiedDownload.Forget(record.Metadata, ReShadeVulkanLayer.DownloadPartLoader);
        record.Metadata.Remove(MetaMovedByDxvkHdr);
        var layerRoot = string.IsNullOrWhiteSpace(game.PrefixPath) ? null : ReShadeVulkanLayerFolder.Root(game.PrefixPath);
        var before = record.Metadata.TryGetValue(MetaDirectoriesBeforeDxvkHdr, out var json) ? ReadStringList(json) : null;
        record.Metadata.Remove(MetaDirectoriesBeforeDxvkHdr);
        var kept = before is not null
            ? record.DirectoriesCreatedByRhi.Where(directory => before.Contains(directory, StringComparer.Ordinal)).ToList()
            : record.DirectoriesCreatedByRhi.Where(directory => layerRoot is null || !PathIdentity.IsSameOrInside(directory, layerRoot)
                && !PathIdentity.IsSameOrInside(layerRoot, directory)).ToList();
        var layerDirectories = record.DirectoriesCreatedByRhi.Except(kept, StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(game.PrefixPath)) layerDirectories.AddRange(ReShadeVulkanLayer.SharedFolders(game.PrefixPath));
        record.DirectoriesCreatedByRhi = kept;
        return new DxvkHdrReShadeBack(record, overrideRestores, valueRestores, layerDirectories);
    }

    /// <summary>The layer's own files in a layer ReShade record: its DLL, its manifest and the Khronos loader beside the exe.</summary>
    private static bool IsLayerFile(LinuxDeployedFile file, DxvkHdrReShadeBackPlan plan)
    {
        if (string.Equals(file.Path, plan.LayerDll.Path, StringComparison.Ordinal)) return true;
        var name = Path.GetFileName(file.Path);
        return name.Equals(ReShadeVulkanLayer.ManifestName(false), StringComparison.OrdinalIgnoreCase)
               || name.Equals(ReShadeVulkanLayer.ManifestName(true), StringComparison.OrdinalIgnoreCase)
               || (name.Equals(ReShadeVulkanLayer.LoaderFileName, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.Path)), Path.GetDirectoryName(Path.GetFullPath(plan.ProxyPath)), StringComparison.Ordinal));
    }

    /// <summary>After a failed DX9 HDR removal that ran the move back: the loader override and layer registration it
    /// restored are put back (the layer record keeps them). Never throws.</summary>
    internal void UndoDxvkHdrMoveBackInside(DetectedGame game, DxvkHdrReShadeBack back)
    {
        foreach (var (overrideRecord, result) in back.OverrideRestores.Reverse())
            if (result.Outcome == WineOverrideRestoreOutcome.Restored) _overrides.TryReapply(game.PrefixPath, overrideRecord);
        foreach (var (valueRecord, result) in back.ValueRestores.Reverse())
            if (result.Outcome == WineOverrideRestoreOutcome.Restored) RegistryValues.TryReapply(game.PrefixPath, valueRecord);
    }

    /// <summary>After the move back committed: the layer's folders RHI created go while empty (and the prefix's shared layer folders with the last layer).</summary>
    internal static void RemoveDxvkHdrLayerFolders(DxvkHdrReShadeBack back) => RemoveEmptyDirectories(back.LayerDirectories);

    /// <summary>The Wine override/registry outcomes of a move back, for the removal message's kept-value notes.</summary>
    internal static IEnumerable<WineOverrideRestoreResult> RestoreResults(DxvkHdrReShadeBack back)
        => [.. back.OverrideRestores.Select(item => item.Result), .. back.ValueRestores.Select(item => item.Result)];

    /// <summary>
    /// #200: the game ReShade's row and profile Apply install for: on a game with DX9 HDR and no RHI ReShade, the game as a
    /// Vulkan game (DXVK HDR draws through Vulkan), so a new ReShade takes the per-game Vulkan layer path, which DXVK HDR's
    /// d3d9.dll reaches; every other game unchanged.
    /// </summary>
    public DetectedGame ViewForDxvkHdr(DetectedGame game)
    {
        if (XnaGacReShade.IsListed(game)) return game; // w7 integration (#200 × #201): an XNA game's ReShade goes to the GAC, never the layer
        if (_records.Find(game, ManagedComponentKind.DxvkHdr) is null || _records.Find(game, ManagedComponentKind.ReShade) is not null) return game;
        var view = game.ShallowClone();
        view.UserGraphicsApiOverride = GraphicsApi.Vulkan;
        return view;
    }
}
