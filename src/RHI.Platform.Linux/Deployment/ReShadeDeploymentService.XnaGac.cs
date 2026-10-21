using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

// w7/reshade (#201): the XNA GAC copies of ReShade's install, update and removal (see XnaGacReShade).
public sealed partial class ReShadeDeploymentService
{
    /// <summary>
    /// Why the GAC folder cannot take ReShade's copies, or null: a <c>d3d9.dll</c> or <c>ReShade.ini</c> RHI did not
    /// place there (any spelling), or RHI's DLL copy changed since it was placed. Read only, before any download.
    /// </summary>
    private async Task<string?> XnaGacRefusalAsync(LinuxDeploymentRecord? existing, string folder, CancellationToken cancellationToken)
    {
        var ours = XnaGacReShade.IsGacRecord(existing)
                   && string.Equals(existing!.Metadata[XnaGacReShade.FolderMetadataKey], folder, StringComparison.Ordinal);
        if (FindFileCaseInsensitive(folder, XnaGacReShade.ProxyName) is { } dll)
        {
            var recorded = ours ? existing!.Files.FirstOrDefault(file => string.Equals(file.Path, dll, StringComparison.Ordinal)) : null;
            if (recorded is null) return XnaGacReShade.ForeignFilePrefix + dll;
            if (!string.IsNullOrWhiteSpace(recorded.Sha256)
                && !FileHash.HexEquals(await FileHash.Sha256HexAsync(dll, cancellationToken).ConfigureAwait(false), recorded.Sha256))
                return XnaGacReShade.ChangedCopyPrefix + dll;
        }
        if (FindFileCaseInsensitive(folder, "ReShade.ini") is { } ini
            && (!ours || !string.Equals(ini, Path.Combine(folder, "ReShade.ini"), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(existing!.Metadata.GetValueOrDefault(XnaGacReShade.IniSha256MetadataKey))))
            return XnaGacReShade.ForeignFilePrefix + ini;
        return null;
    }

    /// <summary>
    /// Copies <paramref name="source"/> (verified, <paramref name="sha256"/>) to <paramref name="target"/> in the GAC
    /// folder, tracked (journal, else <paramref name="local"/>) and declared before the copy, so a failure or a crash puts
    /// the folder back as it was.
    /// </summary>
    private static async Task PlaceGacCopyAsync(DeploymentOperation? operation, OwnedFileSet? local, string source, string target,
        string sha256, CancellationToken cancellationToken)
    {
        await TrackGacWriteAsync(operation, local, target, sha256, cancellationToken).ConfigureAwait(false);
        await AtomicFile.CopyAsync(source, target, sha256, CancellationToken.None, overwrite: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> (RHI's GAC ReShade.ini) to <paramref name="target"/>, tracked (journal, else
    /// <paramref name="local"/>) and declared before the write; returns their SHA-256.
    /// </summary>
    private static async Task<string> PlaceGacBytesAsync(DeploymentOperation? operation, OwnedFileSet? local, string target,
        byte[] bytes, CancellationToken cancellationToken)
    {
        var sha256 = FileHash.Sha256HexOf(bytes);
        await TrackGacWriteAsync(operation, local, target, sha256, cancellationToken).ConfigureAwait(false);
        AtomicFile.WriteAllBytes(target, bytes);
        return sha256;
    }

    // Tracks a GAC folder file (journal, else the local set) and declares what is about to be written there.
    private static async Task TrackGacWriteAsync(DeploymentOperation? operation, OwnedFileSet? local, string target, string sha256,
        CancellationToken cancellationToken)
    {
        if (operation is not null)
        {
            await operation.TrackFileAsync(target, cancellationToken, includeBackup: false).ConfigureAwait(false);
            await operation.RecordWriteAsync(target, sha256, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await local!.TrackWithSnapshotAsync(target, cancellationToken, includeBackup: false).ConfigureAwait(false);
            await local.RecordWriteAsync(target, sha256, cancellationToken).ConfigureAwait(false);
        }
    }

    // The GAC ReShade.ini may be (re)written: absent, or still the copy RHI last placed (a changed one is the user's).
    private static bool GacIniIsRhis(string gacIni, string? lastCopySha256)
        => !File.Exists(gacIni)
           || (lastCopySha256 is { Length: > 0 } && FileHash.HexEquals(FileHash.Sha256Hex(gacIni), lastCopySha256));
}
