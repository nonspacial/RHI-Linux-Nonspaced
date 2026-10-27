using RHI.Core.Deployment;

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

    /// <summary>The first recorded file that is gone, as a could-not-check result; null while every one is there.</summary>
    public static ManagedUpdateCheck? Removed(IEnumerable<string> paths, string? installedVersion)
    {
        foreach (var path in paths)
            if (!File.Exists(path))
                return new ManagedUpdateCheck(true, false, RemovedOutsideRhiPrefix + path, installedVersion, CouldNotCheck: true);
        return null;
    }

    /// <summary>
    /// <paramref name="planned"/> turned into a could-not-check result when a file no longer has the hash RHI recorded
    /// (or cannot be read, or has no recorded hash); <paramref name="planned"/> itself when it plans no update or every file
    /// still matches.
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
                return planned with { UpdateAvailable = false, CouldNotCheck = true, Message = RemovedOutsideRhiPrefix + path };
            if (!await MatchesAsync(path, sha, cancellationToken).ConfigureAwait(false))
                return planned with { UpdateAvailable = false, CouldNotCheck = true, Message = ChangedOutsideRhiPrefix + path };
        }
        return planned;
    }

    private static async Task<bool> MatchesAsync(string path, string? sha, CancellationToken cancellationToken)
    {
        // A record without the file's hash cannot show the file is unchanged: not swapped.
        if (string.IsNullOrWhiteSpace(sha)) return false;
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
