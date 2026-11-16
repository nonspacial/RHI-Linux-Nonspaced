using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

// w14/extras-addons (#283, upstream 2.8.9 MainViewModel.ReShadeAsi): "Load ReShade as ASI" in ReShade's ⚙.
public sealed partial class ReShadeDeploymentService
{
    /// <summary>Not offered for the Vulkan layer, which has no file name (PT-BR presentation pair).</summary>
    public const string AsiModeNotForVulkanMessage =
        "Load ReShade as ASI is not available for Vulkan games: ReShade runs there as a Vulkan layer, which has no file name.";

    /// <summary>Fixed text before the file name while "Load ReShade as ASI" decides ReShade's name (PT-BR presentation pair).</summary>
    public const string AsiModeLocksNamePrefix =
        "ReShade is loaded as an ASI for this game; turn off Load ReShade as ASI in ReShade's ⚙ to choose a DLL name. File: ";

    /// <summary>Fixed text before the name after "On" renamed the installed ReShade (PT-BR presentation pair).</summary>
    public const string AsiModeOnPrefix = "Make sure an ASI loader is installed in this game (ASI Loader in Extras, or your own). ReShade is now: ";

    /// <summary>Fixed text before the name after "Off" renamed the installed ReShade back (PT-BR presentation pair).</summary>
    public const string AsiModeOffPrefix = "ReShade is back to its DLL name: ";

    /// <summary>Fixed text before the name when ReShade is not installed yet: only the setting is saved (PT-BR pair).</summary>
    public const string AsiModeSavedPrefix = "Saved. ReShade will be installed as: ";

    /// <summary>"Off" while ReShade is not installed (PT-BR presentation pair).</summary>
    public const string AsiModeOffSavedMessage = "Saved. ReShade will be installed under its normal DLL name.";

    /// <summary>Whether "Load ReShade as ASI" is on for <paramref name="game"/>.</summary>
    public bool GetAsiMode(DetectedGame game) => _dllNames.GetReShadeAsi(game);

    /// <summary>Whether the option is offered on <paramref name="game"/> at all (not for the Vulkan layer, as upstream).</summary>
    public bool AsiModeOffered(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ReShade) is { } record
            ? !ReShadeVulkanLayer.IsLayerRecord(record)
            : !ReShadeVulkanLayer.IsVulkanTarget(game) && game.EffectiveGraphicsApi != GraphicsApi.Vulkan;

    /// <summary>
    /// Turns "Load ReShade as ASI" on or off for <paramref name="game"/>. An installed ReShade is renamed in place first
    /// (to <c>ReShade64.asi</c> / <c>ReShade32.asi</c>, or back to the user's DLL name or the automatic one) in one journaled
    /// operation with its Wine override and record (<see cref="ProxyRename"/>), and the setting is saved with it; a refused
    /// or failed rename leaves the setting as it was. Without an installed ReShade only the setting is saved. The caller
    /// checks the Xbox write gate.
    /// </summary>
    public async Task<ComponentOperationResult> SetAsiModeAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
    {
        if (!AsiModeOffered(game)) return ComponentOperationResult.Fail(AsiModeNotForVulkanMessage);
        var is32 = GameArchitecture.Of(game) == PeArchitecture.X86;
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null)
        {
            try { _dllNames.SetReShadeAsi(game, on); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonStoreUnavailableException) { return ComponentOperationResult.Fail(ex.Message); }
            return ComponentOperationResult.Ok(on ? AsiModeSavedPrefix + ProxyDllNames.AsiName(is32) : AsiModeOffSavedMessage);
        }

        if (AsiModeBlocker(game, record) is { } blocked) return ComponentOperationResult.Fail(blocked);
        var payload = ProxyPayloadLocator.ReShadePayload(record)!;
        var target = on ? ProxyDllNames.AsiName(is32) : _dllNames.GetReShade(game) ?? AutomaticProxyName(game);
        if (target is null) return ComponentOperationResult.Fail(RenameNameNotOfferedPrefix + "automatic");
        var landed = (on ? AsiModeOnPrefix : AsiModeOffPrefix) + target;
        if (target.Equals(Path.GetFileName(payload.Path), StringComparison.OrdinalIgnoreCase))
        {
            try { _dllNames.SetReShadeAsi(game, on); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonStoreUnavailableException) { return ComponentOperationResult.Fail(ex.Message); }
            return ComponentOperationResult.Ok(landed, payload.Path);
        }
        StagedFileWrite? staged;
        try { staged = _dllNames.StageChoices(game, choices => choices.ReShadeAsi = on ? true : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonStoreUnavailableException) { return ComponentOperationResult.Fail(ex.Message); }
        var renamedLine = ProxyRename.RenamedPrefix + $"{Path.GetFileName(payload.Path)} → {target}";
        var result = await ProxyRename.RunAsync(_journal, _records, _overrides, "ReShade",
            new ProxyRename.Request(game, record, payload, target, null, staged), cancellationToken).ConfigureAwait(false);
        // The rename's own notes (an override value the user changed, kept) follow its "renamed" line.
        return result.Success && result.Message.StartsWith(renamedLine, StringComparison.Ordinal)
            ? result with { Message = landed + result.Message[renamedLine.Length..] }
            : result;
    }

    // Why the installed ReShade cannot switch between its DLL name and the .asi now: another component decides its name.
    private string? AsiModeBlocker(DetectedGame game, LinuxDeploymentRecord record)
    {
        var payload = ProxyPayloadLocator.ReShadePayload(record);
        var current = payload is null ? "ReShade" : Path.GetFileName(payload.Path);
        if (record.Metadata.ContainsKey(MetaMovedByDxvkHdr)) return RenameMovedByDxvkHdrMessage;
        if (payload is null
            || record.Metadata.TryGetValue(MetaShortFuseOriginalPayloadPath, out var shortFuse) && !string.IsNullOrWhiteSpace(shortFuse)
            || (IsReShadeOwnName(current) && _records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            || HasDx9Translation(game)
            || XnaGacReShade.IsGacRecord(record))
            return RenameNotInPlacePrefix + current;
        return null;
    }
}
