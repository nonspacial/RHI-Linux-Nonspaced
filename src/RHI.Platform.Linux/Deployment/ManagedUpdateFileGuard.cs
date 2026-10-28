using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #236 / #237 / #238 (one owner, lane w12/mfg): what an update check says about a component's own file before it plans a
/// swap (w11 lesson: "check the file hash before planning"). A file removed outside RHI is never put back by an update
/// (only Remove, then Install, does that); a file changed outside RHI is never replaced. Both read as "could not be
/// checked" with a line that says why — never an update the swap would refuse on every pass, never "current". The
/// removal is seen before any network call; the hash is read only once an update is planned (callers ask after their
/// release lookup), so a current install is never hashed.
/// </summary>
public static class ManagedUpdateFileGuard
{
    /// <summary>The check's line for a component file removed outside RHI; the path follows.</summary>
    public const string RemovedOutsideRhiPrefix =
        "Not updated: this file was removed outside RHI, and an update never puts it back. Remove clears RHI's record of it: ";

    /// <summary>
    /// The row note of an RHI component whose file was removed outside RHI (platform text in Portuguese, its English row
    /// in PlatformMessageTranslations): Update All leaves it alone, and the row offers Remove because RHI's record alone
    /// makes it Installed.
    /// </summary>
    public const string RemovedOutsideRhiRowNote =
        "O arquivo deste componente foi removido fora do RHI, e as atualizações nunca o colocam de volta. Remover limpa o registro do RHI; depois, Instalar o coloca de novo.";

    /// <summary>The check's line for a component file changed outside RHI; the path follows.</summary>
    public const string ChangedOutsideRhiPrefix =
        "Not updated: this file was changed outside RHI, and an update never replaces it: ";

    /// <summary>
    /// The check's line for a component file RHI recorded without its hash (F5, review w12/mfg): RHI cannot tell whether it
    /// was changed outside RHI, so no swap is planned; Remove, then Install, records it again. The path follows.
    /// </summary>
    public const string NoRecordedHashPrefix =
        "Not updated: RHI's record of this file has no hash, so RHI cannot tell whether it was changed outside RHI. Remove it, then Install it again: ";

    /// <summary>
    /// w12 follow-up (re-review mfg N2): the one line for a record that lost its proxy DLL's name (Universal MFG, DLSSG 20/30,
    /// Ultimate ASI Loader, DLSS Enabler Standalone), in the check and in the update alike: RHI cannot tell which file is its own, so nothing is
    /// swapped; Remove, then Install, records it again.
    /// </summary>
    public const string MissingProxyNameMessage =
        "Not updated: RHI's record of this component has no proxy DLL name, so RHI cannot tell which file is its own. Remove it, then Install it again.";

    /// <summary>
    /// F1 (review w12/mfg; the toolkit rule since w10 D15): a game held by its own earlier undo (or an undecided change) gets
    /// the hold's restart text as a could-not-check result before the check reads any of its files, which would take RHI's
    /// own half-undone file for one removed or changed outside RHI. Null while nothing holds the game. Cheap when nothing
    /// waits (no journal read).
    /// </summary>
    public static async Task<ManagedUpdateCheck?> HeldAsync(
        DeploymentJournal? journal, DetectedGame game, ManagedComponentKind component, string? installedVersion)
        => await DeploymentScope.HoldRefusalAsync(journal, game, component).ConfigureAwait(false) is { } held
            ? new ManagedUpdateCheck(true, false, held, installedVersion, CouldNotCheck: true, LocalReason: true)
            : null;

    /// <summary>The first recorded file that is gone, as a could-not-check result; null while every one is there.</summary>
    public static ManagedUpdateCheck? Removed(IEnumerable<string> paths, string? installedVersion)
    {
        foreach (var path in paths)
            if (!File.Exists(path))
                return new ManagedUpdateCheck(true, false, RemovedOutsideRhiPrefix + path, installedVersion, CouldNotCheck: true, LocalReason: true);
        return null;
    }

    /// <summary>
    /// <paramref name="planned"/> turned into a could-not-check result when a file no longer has the hash RHI recorded
    /// (or cannot be read), or RHI recorded it without a hash (<see cref="NoRecordedHashPrefix"/>); <paramref name="planned"/>
    /// itself when it plans no update or every file still matches.
    /// </summary>
    public static async Task<ManagedUpdateCheck> BeforePlanningAsync(
        ManagedUpdateCheck planned,
        IEnumerable<(string Path, string? Sha256)> files,
        CancellationToken cancellationToken)
    {
        if (!planned.UpdateAvailable) return planned;
        foreach (var (path, sha) in files)
        {
            if (!File.Exists(path))
                return planned with { UpdateAvailable = false, CouldNotCheck = true, LocalReason = true, Message = RemovedOutsideRhiPrefix + path };
            if (string.IsNullOrWhiteSpace(sha))
                return planned with { UpdateAvailable = false, CouldNotCheck = true, LocalReason = true, Message = NoRecordedHashPrefix + path };
            if (!await MatchesAsync(path, sha!, cancellationToken).ConfigureAwait(false))
                return planned with { UpdateAvailable = false, CouldNotCheck = true, LocalReason = true, Message = ChangedOutsideRhiPrefix + path };
        }
        return planned;
    }

    private static async Task<bool> MatchesAsync(string path, string sha, CancellationToken cancellationToken)
    {
        try
        {
            var current = await FileHash.Sha256HexAsync(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(current, sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable reads as not matching
        }
    }
}
