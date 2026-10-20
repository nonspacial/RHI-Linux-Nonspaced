using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

// w7/reshade (#196, upstream DllOverrideService / Reset DLL Names): an installed OptiScaler proxy renamed in place.
public sealed partial class OptiScalerDeploymentService
{
    /// <summary>
    /// Metadata on RHI's ReShade record while it is chained to OptiScaler as <c>ReShade64.dll</c> and OptiScaler was renamed
    /// in place (#196 decision): the name ReShade had when OptiScaler took it, so removing OptiScaler moves ReShade back
    /// there (as upstream restores ReShade's own name), not to OptiScaler's new name — which ReShade may not load under
    /// (wininet.dll, winhttp.dll, dbghelp.dll). Absent = OptiScaler still has that name.
    /// </summary>
    public const string ReShadeChainNameMetadataKey = "ReShade.ChainedFromName";

    /// <summary>Why RHI's OptiScaler on <paramref name="game"/> cannot be renamed in place at all, or null when it can.</summary>
    public string? RenameBlocker(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is null ? ProxyRename.NotInstalledPrefix + "OptiScaler" : null;

    /// <summary>
    /// The name RHI's ReShade, chained to OptiScaler as <c>ReShade64.dll</c>, goes back to when OptiScaler is removed: the
    /// name it had when the chain was made (<see cref="ReShadeChainNameMetadataKey"/>), else OptiScaler's current name.
    /// </summary>
    internal static string ChainedReShadeName(LinuxDeploymentRecord? reShade, LinuxDeploymentRecord optiScaler)
        => reShade?.Metadata.GetValueOrDefault(ReShadeChainNameMetadataKey) is { Length: > 0 } recorded
           && ProxyDllNames.Canonical(recorded) is { } offered
            ? offered
            : GetProxyName(optiScaler);

    // RHI's ReShade chained to OptiScaler (its record lists ReShade64.dll), or null.
    private LinuxDeploymentRecord? ChainedReShade(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ReShade) is { } reShade
           && reShade.Files.Any(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
            ? reShade
            : null;

    /// <summary>
    /// What OptiScaler's DLL-name dropdown offers while it is installed (#196): the names of <paramref name="candidates"/>
    /// it can be renamed to now, or the refusal that keeps the name (read only).
    /// </summary>
    public (IReadOnlyList<string> Targets, string? Refusal) RenameOptions(DetectedGame game, IEnumerable<string> candidates)
    {
        if (RenameBlocker(game) is { } blocked) return ([], blocked);
        var record = _records.Find(game, ManagedComponentKind.OptiScaler)!;
        if (ProxyFile(record) is not { } payload) return ([], ProxyRename.ChangedPrefix + GetProxyName(record));
        if (ProxyRename.PayloadRefusal(payload) is { } refused) return ([], refused);
        var current = GetProxyName(record);
        return (candidates.Where(name => !name.Equals(current, StringComparison.OrdinalIgnoreCase)
                && ProxyRename.TargetRefusal(_records, new ProxyRename.Request(game, record, payload, name, null, null)) is null).ToArray(),
            null);
    }

    /// <summary>
    /// Renames RHI's installed OptiScaler on <paramref name="game"/> to <paramref name="name"/> (one of
    /// <see cref="ProxyNames"/>) in place (#196), writing <paramref name="choice"/> (the saved DLL-name choice) with its
    /// record. The caller decides the name (automatic or chosen) and checks the Xbox write gate.
    /// </summary>
    public async Task<ComponentOperationResult> RenameProxyAsync(
        DetectedGame game, string name, StagedFileWrite? choice, CancellationToken cancellationToken = default)
    {
        if (RenameBlocker(game) is { } blocked) return ComponentOperationResult.Fail(blocked);
        var target = ProxyNames.FirstOrDefault(known => known.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target is null) return ComponentOperationResult.Fail(ProxyNameNotOfferedPrefix + name);
        var record = _records.Find(game, ManagedComponentKind.OptiScaler)!;
        if (ProxyFile(record) is not { } payload) return ComponentOperationResult.Fail(ProxyRename.ChangedPrefix + GetProxyName(record));
        // RHI's ReShade chained as ReShade64.dll stays where it is (OptiScaler loads it by that name, whatever its own);
        // its record keeps the name it goes back to on OptiScaler's removal, saved in the same write (#196 decision).
        LinuxDeploymentRecord? chained = null;
        if (ChainedReShade(game) is { } reShade && !reShade.Metadata.ContainsKey(ReShadeChainNameMetadataKey))
        {
            chained = reShade;
            chained.Metadata[ReShadeChainNameMetadataKey] = GetProxyName(record);
        }
        return await ProxyRename.RunAsync(_journal, _records, _overrides, "OptiScaler",
            new ProxyRename.Request(game, record, payload, target, renamed => renamed.Metadata[ProxyNameMetadataKey] = target, choice, chained),
            cancellationToken).ConfigureAwait(false);
    }

    // The record's proxy file: the file under the recorded name in the record's game folder.
    private static LinuxDeployedFile? ProxyFile(LinuxDeploymentRecord record)
    {
        var proxy = Path.GetFullPath(Path.Combine(record.BinaryDirectory, GetProxyName(record)));
        return record.Files.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Path)
            && Path.GetFullPath(file.Path).Equals(proxy, StringComparison.Ordinal));
    }
}
