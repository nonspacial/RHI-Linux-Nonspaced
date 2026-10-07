using RHI.Core.Models;

namespace RHI.Core.Services;

public sealed class GameLibraryScanner
{
    private readonly IGameDetector[] _detectors;
    private readonly IGameLibraryPolicy[] _policies;
    private readonly Dictionary<string, IReadOnlyList<DetectedGame>> _lastRawResults = new(StringComparer.Ordinal);
    private readonly object _rawLock = new();

    public GameLibraryScanner(
        IEnumerable<IGameDetector> detectors,
        IEnumerable<IGameLibraryPolicy>? policies = null)
    {
        _detectors = detectors.ToArray();
        _policies = policies?.ToArray() ?? [];
    }

    public Task<IReadOnlyList<DetectedGame>> ScanAsync(CancellationToken cancellationToken = default)
        => ScanAsync(null, cancellationToken);

    /// <summary>
    /// Runs the library scan. When <paramref name="onlyDetectors"/> is given, only those detectors
    /// (by <see cref="IGameDetector.Name"/>) are re-run; every other detector reuses its result from
    /// the previous scan. This keeps cheap library edits (adding/removing a manual game) from
    /// re-walking every Steam/Heroic/Lutris/Wine install on disk. Falls back to a full scan when
    /// no previous result exists for a detector that was not requested.
    /// </summary>
    public async Task<IReadOnlyList<DetectedGame>> ScanAsync(
        IReadOnlySet<string>? onlyDetectors,
        CancellationToken cancellationToken = default)
    {
        bool partial;
        lock (_rawLock)
        {
            partial = onlyDetectors is not null &&
                      _detectors.All(d => onlyDetectors.Contains(d.Name) || _lastRawResults.ContainsKey(d.Name));
        }

        var tasks = _detectors.Select(async detector =>
        {
            IReadOnlyList<DetectedGame> raw;
            if (partial && !onlyDetectors!.Contains(detector.Name))
            {
                lock (_rawLock) raw = _lastRawResults[detector.Name];
            }
            else
            {
                try
                {
                    raw = await detector.DetectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    raw = Array.Empty<DetectedGame>();
                }
            }
            return (detector.Name, raw);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (_rawLock)
        {
            // Keep pristine snapshots: downstream stages mutate the instances they receive.
            foreach (var (name, list) in results)
                _lastRawResults[name] = list.Select(g => g.ShallowClone()).ToArray();
        }
        IReadOnlyList<DetectedGame> games = results
            .SelectMany(x => x.raw.Select(g => g.ShallowClone()))
            .ToArray();

        if (onlyDetectors is null)
        {
            foreach (var policy in _policies)
                policy.RefreshSources();
        }

        foreach (var policy in _policies)
        {
            try
            {
                games = await policy.ApplyAsync(games, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Policies are metadata/enrichment layers. If an upstream policy source is
                // unavailable, keep the locally detected library exactly as it was.
            }
        }

        return games
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && Directory.Exists(g.InstallPath))
            .GroupBy(GameKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(SplitDistinctLauncherEntries)
            .Select(Merge)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string GameKey(DetectedGame g)
    {
        if (g.SteamAppId is int id)
        {
            // Normal storefront duplicates still merge by AppID. Manifest splitGames intentionally
            // produces multiple cards from one AppID, so those children need independent keys.
            if (g.IsManifestSplitChild)
                return $"steam:{id}:split:{g.Name}:{Path.GetFullPath(g.InstallPath)}";
            return $"steam:{id}";
        }
        return $"path:{Path.GetFullPath(g.InstallPath)}";
    }

    /// <summary>
    /// One folder seen by two launchers is one game, so a path group normally merges across sources.
    /// One launcher listing two entries with different library identities at that folder is two games
    /// (e.g. two Lutris wrappers): then the group splits per source + identity and nothing merges.
    /// </summary>
    private static IEnumerable<DetectedGame[]> SplitDistinctLauncherEntries(IGrouping<string, DetectedGame> group)
    {
        var entries = group.ToArray();
        if (entries.Length < 2 || group.Key.StartsWith("steam:", StringComparison.Ordinal))
            return [entries];

        var conflict = entries
            .GroupBy(g => g.Source ?? "", StringComparer.OrdinalIgnoreCase)
            .Any(s => s.Select(IdentityOf).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any());
        if (!conflict) return [entries];

        return entries
            .GroupBy(g => (g.Source ?? "") + "|" + IdentityOf(g), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToArray());
    }

    private static string IdentityOf(DetectedGame g)
    {
        var identity = string.IsNullOrWhiteSpace(g.LibraryIdentityPath) ? g.InstallPath : g.LibraryIdentityPath;
        try { return Path.GetFullPath(identity).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return identity.Trim(); }
    }

    private static DetectedGame Merge(DetectedGame[] games)
    {
        var first = games[0];
        foreach (var candidate in games.Skip(1))
        {
            first.PrefixPath ??= candidate.PrefixPath;
            first.ExecutablePath ??= candidate.ExecutablePath;
            first.LaunchExecutablePath ??= candidate.LaunchExecutablePath;
            first.LibraryIdentityPath ??= candidate.LibraryIdentityPath;
            first.LibraryProvider ??= candidate.LibraryProvider;
            first.LaunchProvider ??= candidate.LaunchProvider;
            first.Runner ??= candidate.Runner;
            // The "Launch opens the launcher" hint belongs to the command it describes.
            if (first.LaunchCommand is null)
            {
                first.LaunchCommand = candidate.LaunchCommand;
                first.ParentLauncherName = candidate.ParentLauncherName;
            }
            first.CompatibilityTool ??= candidate.CompatibilityTool;
            first.CompatibilityLayerType ??= candidate.CompatibilityLayerType;
            first.WineBinaryPath ??= candidate.WineBinaryPath;
            first.WineServerPath ??= candidate.WineServerPath;
            first.CanonicalManifestName ??= candidate.CanonicalManifestName;
            first.WikiNameOverride ??= candidate.WikiNameOverride;
            first.ManifestInstallRoot ??= candidate.ManifestInstallRoot;
            first.IsManifestSplitChild |= candidate.IsManifestSplitChild;
            // A fail-closed patch-target verdict from any source survives the merge.
            first.RequiresExplicitPatchTarget |= candidate.RequiresExplicitPatchTarget;
            first.ManifestSteamAppIdOverride ??= candidate.ManifestSteamAppIdOverride;
            first.ManifestGraphicsApiOverride ??= candidate.ManifestGraphicsApiOverride;
            if (first.ManifestGraphicsApis.Count == 0) first.ManifestGraphicsApis = candidate.ManifestGraphicsApis;
            first.EngineHint ??= candidate.EngineHint;
            first.ForcedArchitecture ??= candidate.ForcedArchitecture;
            first.EngineIniProjectOverride ??= candidate.EngineIniProjectOverride;
            first.WikiUnlinkedByManifest |= candidate.WikiUnlinkedByManifest;
            first.DlssScanSkippedByManifest |= candidate.DlssScanSkippedByManifest;
            first.UeExtendedByManifest |= candidate.UeExtendedByManifest;
            first.NativeHdrByManifest |= candidate.NativeHdrByManifest;
            first.UeExtendedBlockedByManifest |= candidate.UeExtendedBlockedByManifest;
            first.UeExtendedHdrByManifest ??= candidate.UeExtendedHdrByManifest;
            first.UeExtendedLutByManifest ??= candidate.UeExtendedLutByManifest;
            first.ReShadeDllNameOverride ??= candidate.ReShadeDllNameOverride;
            first.DisplayCommanderDllNameOverride ??= candidate.DisplayCommanderDllNameOverride;
            first.OptiScalerDllNameOverride ??= candidate.OptiScalerDllNameOverride;
            first.PdUpscalerArtifact ??= candidate.PdUpscalerArtifact;
            first.ManifestLaunchExecutable ??= candidate.ManifestLaunchExecutable;
            first.ExternalModUrl ??= candidate.ExternalModUrl;
            first.ExternalModLabel ??= candidate.ExternalModLabel;
            first.NexusUrlOverride ??= candidate.NexusUrlOverride;
            first.PcgwUrlOverride ??= candidate.PcgwUrlOverride;
            first.UwFixUrlOverride ??= candidate.UwFixUrlOverride;
            first.UltraPlusUrlOverride ??= candidate.UltraPlusUrlOverride;
            first.WikiStatusOverride ??= candidate.WikiStatusOverride;
            first.AuthorOverride ??= candidate.AuthorOverride;
            first.ManifestGameNote ??= candidate.ManifestGameNote;
            first.ManifestGameNoteUrl ??= candidate.ManifestGameNoteUrl;
            first.ManifestGameNoteUrlLabel ??= candidate.ManifestGameNoteUrlLabel;
            if (first.ManifestInstallWarnings.Count == 0) first.ManifestInstallWarnings = candidate.ManifestInstallWarnings;
            if (first.GraphicsApi == GraphicsApi.Unknown)
                first.GraphicsApi = candidate.GraphicsApi;
        }
        return first;
    }
}
