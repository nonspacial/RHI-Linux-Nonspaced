namespace RHI.Platform.Linux.Deployment;

/// <summary>#271: upstream's recommended OptiScaler forks (manifest <c>osForks</c>, 2.9.4 format).</summary>
public sealed partial class UpstreamRhiManifestService
{
    /// <summary>
    /// The manifest's forks after upstream's checks (<see cref="OptiScalerForks.Parse"/>), in manifest order; empty when the
    /// manifest lists none. The cached or pinned manifest answers when the live one cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<OptiScalerFork>> FetchOsForksAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return OptiScalerForks.Parse(manifest.OsForks);
    }

    /// <summary>Reads the manifest's forks into <see cref="OptiScalerForks"/>; a failure keeps the forks already known (the built-in four at first).</summary>
    public async Task RefreshOsForksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            OptiScalerForks.SetFromManifest(await FetchOsForksAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RHI.Core.Services.AppLog.Warn("optiscaler", "OptiScaler forks not read from the manifest; the known list is kept", ex);
        }
    }
}
