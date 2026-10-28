using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w12 final review M1 (B2's way out): Remove for a record that lost its proxy DLL's name (Universal MFG, RTX 20 FG
/// Unlock, Ultimate ASI Loader; older builds or edited ledgers). <see cref="ManagedUpdateFileGuard.MissingProxyNameMessage"/>
/// tells the user to Remove, then Install, so Remove must work for exactly that record. RHI cannot tell its proxy by name,
/// so it goes by the record's own file list: inside the family's journaled removal it deletes each listed file whose
/// SHA-256 still equals the recorded one, restores the recorded Wine override (the override names its own DLL) and drops
/// the record. A listed file that is missing, has no recorded hash, was changed outside RHI or lies outside the record's
/// binary folder stays where it is and is named in the result line (paths last). Planned before the removal opens, after
/// the game's hold check (the toolkit rule for hashes read before a journaled operation).
/// </summary>
public static class ProxylessRecordRemoval
{
    /// <summary>The result line after the component's name: what this removal did and did not do.</summary>
    public const string RemovedSuffix =
        " removed. RHI's record of it had no proxy DLL name, so RHI deleted only the listed files that still held the bytes it installed, restored the Wine override it recorded and dropped the record.";

    /// <summary>The files a removal without the proxy name left where they are; the paths follow, joined by "; ".</summary>
    public const string NotDeletedPrefix = " Files RHI did not delete: ";

    /// <summary>Ultimate ASI Loader: the chained original was put back over RHI's proxy.</summary>
    public const string ChainedOriginalBackNote = " The chained original DLL is back under its own name.";

    /// <summary>Ultimate ASI Loader: the chained original is gone, so the proxy stays (named among the files not deleted).</summary>
    public const string ChainedOriginalMissingNote =
        " The chained original DLL is missing, so the ASI Loader's DLL was left where it is.";

    /// <summary>
    /// Ultimate ASI Loader: the chained original exists, but RHI's proxy was changed outside RHI (or RHI cannot tell which
    /// listed file it replaced), so neither moves; both are named among the files not deleted.
    /// </summary>
    public const string ChainedOriginalKeptNote =
        " The chained original DLL was not put back, because the ASI Loader's DLL was changed outside RHI or RHI cannot tell which file it replaced.";

    /// <summary>Which listed files the removal deletes and which it leaves (in the record's order, each path once).</summary>
    public sealed record Plan(IReadOnlyList<string> Delete, IReadOnlyList<string> Left);

    /// <summary>Whether <paramref name="record"/> needs this removal (no proxy DLL name under any of <paramref name="metadataKeys"/>).</summary>
    public static bool Applies(LinuxDeploymentRecord record, params string[] metadataKeys)
        => metadataKeys.All(key => string.IsNullOrWhiteSpace(record.Metadata.GetValueOrDefault(key)));

    /// <summary>Hashes every listed file: a file is deleted only while it sits in the record's binary folder and still matches.</summary>
    public static async Task<Plan> PlanAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var delete = new List<string>();
        var left = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folder = string.IsNullOrWhiteSpace(record.BinaryDirectory) ? null : Path.GetFullPath(record.BinaryDirectory);
        foreach (var file in record.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var path = Path.GetFullPath(file.Path);
            if (!seen.Add(path)) continue;
            var deletable = folder is not null
                            && string.Equals(Path.GetDirectoryName(path), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(file.Sha256)
                            && File.Exists(path)
                            && await MatchesAsync(path, file.Sha256!, cancellationToken).ConfigureAwait(false);
            (deletable ? delete : left).Add(path);
        }
        return new Plan(delete, left);
    }

    /// <summary>The result line: the component, what was done, <paramref name="notes"/>, then the files not deleted (paths last).</summary>
    public static string Message(string component, IReadOnlyList<string> left, string notes = "")
        => component + RemovedSuffix + notes + (left.Count == 0 ? "" : NotDeletedPrefix + string.Join("; ", left));

    private static async Task<bool> MatchesAsync(string path, string sha, CancellationToken cancellationToken)
    {
        try
        {
            var current = await FileHash.Sha256HexAsync(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(current, sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable reads as changed: left in place
        }
    }
}
