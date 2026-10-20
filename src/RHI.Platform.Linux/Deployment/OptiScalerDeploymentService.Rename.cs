using RHI.Core.Models;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

// w7/reshade (#196, upstream DllOverrideService / Reset DLL Names): an installed OptiScaler proxy renamed in place.
public sealed partial class OptiScalerDeploymentService
{
    /// <summary>
    /// Fixed text before OptiScaler's installed name while RHI's ReShade is chained to it as ReShade64.dll: removing
    /// OptiScaler moves ReShade back to OptiScaler's name, so it stays (PT-BR presentation pair).
    /// </summary>
    public const string RenameChainedPrefix =
        "OptiScaler's DLL cannot be renamed in place while RHI's ReShade is chained to it as ReShade64.dll; remove ReShade first, or remove OptiScaler and install it again under the new name: ";

    /// <summary>Why RHI's OptiScaler on <paramref name="game"/> cannot be renamed in place at all, or null when it can.</summary>
    public string? RenameBlocker(DetectedGame game)
    {
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not { } record)
            return ProxyRename.NotInstalledPrefix + "OptiScaler";
        var reShade = _records.Find(game, ManagedComponentKind.ReShade);
        if (reShade?.Files.Any(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase)) == true)
            return RenameChainedPrefix + GetProxyName(record);
        return null;
    }

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
        return await ProxyRename.RunAsync(_journal, _records, _overrides, "OptiScaler",
            new ProxyRename.Request(game, record, payload, target, renamed => renamed.Metadata[ProxyNameMetadataKey] = target, choice),
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
