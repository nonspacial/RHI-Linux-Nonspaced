using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    public void Decorate(DetectedGame game, IReadOnlyList<ManagedComponent> components) => _rows.Decorate(game, components);

    // The refusal text when every link of the entry is outside the RenoDX host allow-list; null when one is usable.
    internal static string? UntrustedRenoDxLink(RenoDxModEntry entry)
    {
        var urls = new[] { entry.UrlFor(PeArchitecture.X64), entry.UrlFor(PeArchitecture.X86) }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (urls.Length == 0 || urls.Any(RenoDxDeploymentService.DownloadPolicy.IsTrusted)) return null;
        return new UntrustedDownloadException(urls[0], RenoDxDeploymentService.DownloadPolicy).Message;
    }

    /// <summary>#178: what the game's update inclusion (and Settings, for every game) leaves out of Update All.</summary>
    public UpdateExclusion UpdateExclusionFor(DetectedGame game, ManagedComponentKind component)
        => _updateInclusion?.ExclusionFor(game, component) ?? UpdateExclusion.None;
}
