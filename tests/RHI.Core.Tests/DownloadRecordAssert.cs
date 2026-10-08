using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Review w2-trust minor 6 (#26): every service's deployment record names the download it was made from with the
/// same keys (<c>Download[.part].Sha256</c> / <c>.Integrity</c>, written by <see cref="VerifiedDownload.Record"/>).
/// </summary>
internal static class DownloadRecordAssert
{
    /// <summary>The record names <paramref name="sha256"/> (any case) with <paramref name="integrity"/> for <paramref name="part"/>.</summary>
    public static void Recorded(LinuxDeploymentRecord record, string sha256, string integrity, string? part = null)
    {
        Assert.True(record.Metadata.TryGetValue(VerifiedDownload.Sha256KeyFor(part), out var recorded),
            $"{record.Component}: no {VerifiedDownload.Sha256KeyFor(part)} in the record");
        Assert.Equal(sha256, recorded, ignoreCase: true);
        Assert.Equal(integrity, record.Metadata.GetValueOrDefault(VerifiedDownload.IntegrityKeyFor(part)));
    }

    /// <summary>The record names no download for <paramref name="part"/>.</summary>
    public static void NotRecorded(LinuxDeploymentRecord record, string? part = null)
    {
        Assert.False(record.Metadata.ContainsKey(VerifiedDownload.Sha256KeyFor(part)));
        Assert.False(record.Metadata.ContainsKey(VerifiedDownload.IntegrityKeyFor(part)));
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    public static string Sha256Of(string path) => FileHash.Sha256Hex(path);
}
