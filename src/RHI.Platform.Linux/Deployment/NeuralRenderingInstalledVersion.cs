using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>How <see cref="NeuralRenderingInstalledVersion"/> told the installed NR DLL's manifest version.</summary>
public enum InstalledNrVersionSource
{
    /// <summary>The owner's record says it (<c>NeuralRendering.DllVersion</c>, <c>OptiScaler.DlssNrRuntimeVersion</c>).</summary>
    Metadata,
    /// <summary>The record's own SHA-256 of the file equals the content of exactly one stamped cache entry.</summary>
    CacheStamp,
    /// <summary>The DLL's PE version maps to exactly one published manifest entry.</summary>
    PeVersion,
    /// <summary>Nothing told it (no file in the record, no stamp, no readable version, no matching entry).</summary>
    Unknown,
    /// <summary>The DLL's PE version matches several published entries (same build, different labels).</summary>
    Ambiguous
}

/// <summary>The installed NR DLL's manifest version label (null when <see cref="Source"/> is Unknown or Ambiguous).</summary>
public sealed record InstalledNrVersion(string? Version, InstalledNrVersionSource Source)
{
    public bool Known => Version is not null;
}

/// <summary>
/// #230: which manifest NR version an owner's installed <c>nvngx_dlssnr.dll</c> is — the one helper (#234's Check Neural
/// Rendering page reuses it for its "DLL fit" line). Order: the record's metadata; else (older records such as Borderlands
/// 3's Feeder) the record's own SHA-256 of the file against RHI's stamped cache entries (<c>DLSS-NR/&lt;version&gt;/
/// nvngx_dlssnr.dll.rhi-stamp.json</c>, the stamp's recorded content hash and manifest label); else the DLL's PE version
/// (commas, compared number by number) mapped to EXACTLY one published entry. None or several = not known: the caller
/// reports "could not check", never "current", and never swaps blind. While the NR Cost Scaler wraps the runtime the
/// owner's content lives in <c>nvngx_dlssnr_real.dll</c> (<see cref="NrCostScalerService.ContentPath"/>).
/// </summary>
public static class NeuralRenderingInstalledVersion
{
    public const string DllName = "nvngx_dlssnr.dll";

    /// <summary>The record's metadata key that names its NR version (null for a component that owns none).</summary>
    public static string? MetadataKey(ManagedComponentKind owner) => owner switch
    {
        ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder => NeuralRenderingDllSwap.MetaDllVersion,
        ManagedComponentKind.OptiScaler => OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey,
        _ => null
    };

    /// <param name="record">The owner's record.</param>
    /// <param name="publishedVersions">The manifest's NR labels (every one, card or not).</param>
    /// <param name="cacheFolder">RHI's NR cache folder (<c>…/rhi-linux/DLSS-NR</c>).</param>
    /// <param name="gameRecords">Every record of the game (the NR Cost Scaler's tells where the owner's content is).</param>
    public static InstalledNrVersion Identify(LinuxDeploymentRecord record, IReadOnlyList<string> publishedVersions, string cacheFolder,
        IEnumerable<LinuxDeploymentRecord> gameRecords)
    {
        if (MetadataKey(record.Component) is { } key && record.Metadata.TryGetValue(key, out var recorded) && !string.IsNullOrWhiteSpace(recorded))
            return new(recorded, InstalledNrVersionSource.Metadata);
        var file = ManagedFileSwap.FindFile(record, DllName);
        if (file is null) return new(null, InstalledNrVersionSource.Unknown);

        if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            var stamped = StampedVersions(cacheFolder)
                .Where(stamp => string.Equals(stamp.ContentSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                .Select(stamp => stamp.Version)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (stamped.Length == 1) return new(stamped[0], InstalledNrVersionSource.CacheStamp);
        }

        var content = NrCostScalerService.ContentPath(record, file, gameRecords);
        var pe = DlssAutoFollowService.PeFileVersion(content);
        if (pe is null) return new(null, InstalledNrVersionSource.Unknown);
        var matches = publishedVersions.Where(version => DlssAutoFollowService.SameVersion(version, pe)).Distinct(StringComparer.Ordinal).ToArray();
        return matches.Length switch
        {
            1 => new(matches[0], InstalledNrVersionSource.PeVersion),
            0 => new(null, InstalledNrVersionSource.Unknown),
            _ => new(null, InstalledNrVersionSource.Ambiguous)
        };
    }

    private sealed record Stamp(string Version, string ContentSha256);

    // RHI's own stamps beside each cached NR DLL (CacheStamp's format: Version + ContentSha256); read only, never trusted
    // for reuse here — they only name which manifest label a hash was downloaded as.
    private static IEnumerable<Stamp> StampedVersions(string cacheFolder)
    {
        var stamps = new List<Stamp>();
        try
        {
            if (!Directory.Exists(cacheFolder)) return stamps;
            foreach (var dir in Directory.EnumerateDirectories(cacheFolder))
            {
                var path = CacheStamp.StampPath(Path.Combine(dir, DllName));
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String &&
                        root.TryGetProperty("ContentSha256", out var sha) && sha.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(version.GetString()) && !string.IsNullOrWhiteSpace(sha.GetString()))
                        stamps.Add(new(version.GetString()!, sha.GetString()!));
                }
                catch (JsonException) { /* a damaged stamp names nothing */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nr-gpu", "The Neural Rendering cache stamps could not be read", ex);
        }
        return stamps;
    }
}
