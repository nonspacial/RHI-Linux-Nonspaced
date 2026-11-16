using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14/extras-fg (#279): MFG Unlock's Intel XeSS files around its add-on (<see cref="MfgUnlockFiles"/>): placed after an
/// install or update, put back before a removal, and planned by the update check for an install from before.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>MFG Unlock's XeSS files (null: none are placed, as in tests that predate #279).</summary>
    public MfgUnlockFiles? MfgUnlockFiles { get; set; }

    private async Task<ComponentOperationResult> InstallMfgUnlockWithFilesAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var result = await _mfgAdaUnlock.InstallAsync(game, cancellationToken).ConfigureAwait(false);
        if (!result.Success || MfgUnlockFiles is null) return result;
        return result with { Message = result.Message + await MfgUnlockFiles.PlaceAsync(game, cancellationToken).ConfigureAwait(false) };
    }

    private async Task<ComponentOperationResult> UpdateMfgUnlockAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var result = await _mfgAdaUnlock.UpdateAsync(game, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success || MfgUnlockFiles is null) return result;
        return result with { Message = result.Message + await MfgUnlockFiles.PlaceAsync(game, cancellationToken).ConfigureAwait(false) };
    }

    private async Task<ManagedUpdateCheck> CheckMfgUnlockUpdateAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var check = await _mfgAdaUnlock.CheckUpdateAsync(game, cancellationToken: cancellationToken).ConfigureAwait(false);
        return check is { Managed: true, UpdateAvailable: false, CouldNotCheck: false }
               && MfgUnlockFiles is { } files && _records.Find(game, ManagedComponentKind.MfgAdaUnlock) is { } record && files.NeedsFiles(record)
            ? check with { UpdateAvailable = true, Message = MfgUnlockFiles.DueMessage }
            : check;
    }

    private async Task<ComponentOperationResult> RemoveMfgUnlockAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var notes = "";
        if (MfgUnlockFiles is not null)
        {
            var (refusal, restored) = await MfgUnlockFiles.RestoreAsync(game, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return ComponentOperationResult.Fail(refusal);
            notes = restored;
        }
        var result = await _mfgAdaUnlock.RemoveAsync(game, cancellationToken).ConfigureAwait(false);
        return notes.Length == 0 ? result : result with { Message = result.Message + notes };
    }

    /// <summary>MFG Unlock's Intel files in <paramref name="binaryDirectory"/> (the ⚙'s status line); empty without the service.</summary>
    public IReadOnlyList<MfgUnlockXessFile> MfgUnlockXessState(string binaryDirectory)
        => MfgUnlockFiles?.State(binaryDirectory) ?? [];

    /// <summary>RHI's MFG Unlock record for the game, or null.</summary>
    public LinuxDeploymentRecord? GetMfgUnlockRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.MfgAdaUnlock);
}
