using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

// w7/reshade (#196, upstream DllOverrideService / Reset DLL Names): an installed ReShade proxy renamed in place.
public sealed partial class ReShadeDeploymentService
{
    /// <summary>
    /// Fixed text before the installed name when another component decides ReShade's DLL name (PT-BR presentation pair).
    /// </summary>
    public const string RenameNotInPlacePrefix =
        "ReShade's DLL cannot be renamed in place while another component decides its name (OptiScaler, ShortFuse or the DirectX 9 translation); remove ReShade and install it again to change it: ";

    /// <summary>Fixed text before the name when ReShade is not offered under it for this game (PT-BR presentation pair).</summary>
    public const string RenameNameNotOfferedPrefix = "The DLL was not renamed: ReShade is not offered under this name for this game: ";

    /// <summary>
    /// The name "Automatic" stands for on <paramref name="game"/> (the RHI manifest's name, else the graphics API's; the
    /// saved choice left out), as an install would pick it; null when the API has no ReShade proxy.
    /// </summary>
    public string? AutomaticProxyName(DetectedGame game)
    {
        if (ProxyPayloadLocator.Resolve(game, null) is not { } resolution) return null;
        return ProxyDllNames.OfferedFor(GameArchitecture.Of(game) == PeArchitecture.X86).Contains(resolution.Name, StringComparer.OrdinalIgnoreCase)
            ? resolution.Name
            : SelectProxyName(game.EffectiveGraphicsApi);
    }

    /// <summary>
    /// Why RHI's ReShade on <paramref name="game"/> cannot be renamed in place at all, or null when it can: not RHI's,
    /// the Vulkan layer (no DLL), ShortFuse's loader name, the OptiScaler chain (<c>ReShade64.dll</c>, OptiScaler loads it
    /// by that name), the DirectX 9 translation's move (its restore expects the name it moved ReShade to) or an XNA GAC
    /// install (#201: XNA's graphics assembly loads d3d9.dll only).
    /// </summary>
    public string? RenameBlocker(DetectedGame game)
    {
        if (_records.Find(game, ManagedComponentKind.ReShade) is not { } record)
            return ProxyRename.NotInstalledPrefix + "ReShade";
        var payload = ProxyPayloadLocator.ReShadePayload(record);
        var current = payload is null ? "ReShade" : Path.GetFileName(payload.Path);
        if (payload is null || ReShadeVulkanLayer.IsLayerRecord(record)
            || record.Metadata.TryGetValue(MetaShortFuseOriginalPayloadPath, out var shortFuse) && !string.IsNullOrWhiteSpace(shortFuse)
            || current.Equals(ProxyDllNames.ShortFuseAsiName, StringComparison.OrdinalIgnoreCase)
            || (IsReShadeOwnName(current) && _records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            || HasDx9Translation(game)
            || XnaGacReShade.IsGacRecord(record)) // #201: XNA loads ReShade as d3d9.dll only
            return RenameNotInPlacePrefix + current;
        return null;
    }

    /// <summary>
    /// Renames RHI's installed ReShade on <paramref name="game"/> to <paramref name="name"/> (null = the automatic name) in
    /// place, saving the choice (#196). The same name only saves the choice. The caller checks the Xbox write gate.
    /// </summary>
    public async Task<ComponentOperationResult> RenameProxyAsync(DetectedGame game, string? name, CancellationToken cancellationToken = default)
    {
        if (RenameBlocker(game) is { } blocked) return ComponentOperationResult.Fail(blocked);
        var record = _records.Find(game, ManagedComponentKind.ReShade)!;
        var payload = ProxyPayloadLocator.ReShadePayload(record)!;
        var is32 = GameArchitecture.Of(game) == PeArchitecture.X86;
        var target = name is null ? AutomaticProxyName(game) : ProxyDllNames.Canonical(name);
        if (target is null || !ProxyDllNames.OfferedFor(is32).Contains(target, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail(RenameNameNotOfferedPrefix + (name ?? "automatic"));
        // ReShade64/32.dll beside RHI's OptiScaler is the coexistence chain: an install, not a rename, sets it up.
        if (IsReShadeOwnName(target) && _records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail(RenameNotInPlacePrefix + Path.GetFileName(payload.Path));
        var choice = name is null ? null : target;
        if (target.Equals(Path.GetFileName(payload.Path), StringComparison.OrdinalIgnoreCase))
        {
            try { _dllNames.SetReShade(game, choice); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ComponentOperationResult.Fail(ex.Message); }
            return ComponentOperationResult.Ok(ProxyRename.NameKeptPrefix + target, payload.Path);
        }
        StagedFileWrite? staged;
        try { staged = _dllNames.StageChoices(game, choices => choices.ReShade = choice); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ComponentOperationResult.Fail(ex.Message); }
        return await ProxyRename.RunAsync(_journal, _records, _overrides, "ReShade",
            new ProxyRename.Request(game, record, payload, target, null, staged), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the DLL-name dropdown of an installed ReShade offers (#196): the names of <paramref name="candidates"/> it can
    /// be renamed to now (free, offered, not the OptiScaler chain's), or the refusal that keeps the name (read only).
    /// </summary>
    public (IReadOnlyList<string> Targets, string? Refusal) RenameOptions(DetectedGame game, IEnumerable<string> candidates)
    {
        if (RenameBlocker(game) is { } blocked) return ([], blocked);
        var record = _records.Find(game, ManagedComponentKind.ReShade)!;
        var payload = ProxyPayloadLocator.ReShadePayload(record)!;
        if (ProxyRename.PayloadRefusal(payload) is { } refused) return ([], refused);
        var current = Path.GetFileName(payload.Path);
        var optiScaler = _records.Find(game, ManagedComponentKind.OptiScaler) is not null;
        return (candidates.Where(name => !name.Equals(current, StringComparison.OrdinalIgnoreCase)
                && !(optiScaler && IsReShadeOwnName(name))
                && ProxyRename.TargetRefusal(_records, new ProxyRename.Request(game, record, payload, name, null, null)) is null).ToArray(),
            null);
    }

    private static bool IsReShadeOwnName(string name)
        => name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase);

    // Luma or the DLSS5 Feeder moved ReShade for dgVoodoo2 (d3d9.dll -> dxgi.dll); their removal moves it back by name.
    private bool HasDx9Translation(DetectedGame game)
        => _records.FindAll(game).Any(other =>
            (other.Metadata.TryGetValue(LumaFrameworkDeploymentService.MetaDx9Migration, out var luma) && !string.IsNullOrWhiteSpace(luma))
            || (other.Metadata.TryGetValue(Dlss5FeederFullDeploymentService.MetaDx9Migration, out var feeder) && !string.IsNullOrWhiteSpace(feeder)));
}
