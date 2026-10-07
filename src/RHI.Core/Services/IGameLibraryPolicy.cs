using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Optional post-detection policy stage for library-wide transformations such as
/// upstream blacklist rules, install-path remaps and multi-game splits.
/// Implementations must be fail-open: policy unavailability must not make the
/// locally detected library unusable.
/// </summary>
public interface IGameLibraryPolicy
{
    Task<IReadOnlyList<DetectedGame>> ApplyAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called before <see cref="ApplyAsync"/> on an explicit full library refresh only. Partial refreshes
    /// (adding, editing or removing one entry) keep whatever policy source the process already holds.
    /// </summary>
    void RefreshSources() { }
}
