using RHI.Core.Models;

namespace RHI.Core.Services;

public sealed class GameLibraryScanner
{
    private readonly IGameDetector[] _detectors;
    private readonly IGameLibraryPolicy[] _policies;
    private readonly Dictionary<string, IReadOnlyList<DetectedGame>> _lastRawResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DetectorScanStatus> _lastStatuses = new(StringComparer.Ordinal);
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

    /// <summary>Games only; see <see cref="ScanWithReportAsync"/> for per-detector status.</summary>
    public async Task<IReadOnlyList<DetectedGame>> ScanAsync(
        IReadOnlySet<string>? onlyDetectors,
        CancellationToken cancellationToken = default)
        => (await ScanWithReportAsync(onlyDetectors, cancellationToken).ConfigureAwait(false)).Games;

    /// <summary>
    /// Runs the library scan. When <paramref name="onlyDetectors"/> is given, only those detectors
    /// (by <see cref="IGameDetector.Name"/>) are re-run; every other detector reuses its result from
    /// the previous scan. This keeps cheap library edits (adding/removing a manual game) from
    /// re-walking every Steam/Heroic/Lutris/Wine install on disk. Falls back to a full scan when
    /// no previous result exists for a detector that was not requested.
    /// A detector that throws, or reports skipped entries, no longer looks like "no games": its status
    /// says so, and the failure is written to <see cref="AppLog"/> (Refs #56).
    /// </summary>
    public async Task<GameLibraryScanResult> ScanWithReportAsync(
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
            if (partial && !onlyDetectors!.Contains(detector.Name))
            {
                lock (_rawLock)
                {
                    var previous = _lastStatuses.TryGetValue(detector.Name, out var status)
                        ? status with { Reused = true }
                        : new DetectorScanStatus(detector.Name, _lastRawResults[detector.Name].Count, null, [], Reused: true);
                    return (detector.Name, raw: _lastRawResults[detector.Name], status: previous);
                }
            }

            try
            {
                var (raw, warnings) = await DetectorDiagnostics
                    .CollectAsync(() => detector.DetectAsync(cancellationToken))
                    .ConfigureAwait(false);
                return (detector.Name, raw, status: new DetectorScanStatus(detector.Name, raw.Count, null, warnings));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One detector failing must not empty the whole library; it must not pass for "no games" either.
                AppLog.Warn("scan", $"{detector.Name} detector failed", ex);
                return (detector.Name, raw: (IReadOnlyList<DetectedGame>)Array.Empty<DetectedGame>(),
                    status: new DetectorScanStatus(detector.Name, 0, ex.Message, []));
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (_rawLock)
        {
            // Keep pristine snapshots: downstream stages mutate the instances they receive.
            foreach (var (name, list, status) in results)
            {
                _lastRawResults[name] = list.Select(g => g.ShallowClone()).ToArray();
                _lastStatuses[name] = status with { Reused = false };
            }
        }
        IReadOnlyList<DetectedGame> games = results
            .SelectMany(x => x.raw.Select(g => g.ShallowClone()))
            .ToArray();

        if (onlyDetectors is null)
        {
            foreach (var policy in _policies)
                policy.RefreshSources();
        }

        var policyFailures = new List<PolicyScanStatus>();
        foreach (var policy in _policies)
        {
            try
            {
                games = await policy.ApplyAsync(games, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Policies are metadata/enrichment layers. If an upstream policy source is
                // unavailable, keep the locally detected library exactly as it was, and say so.
                AppLog.Warn("scan", $"{policy.Name} not applied", ex);
                policyFailures.Add(new PolicyScanStatus(policy.Name, ex.Message));
            }
        }

        var merged = games
            // A saved manual entry whose folder is gone stays listed as "folder missing" (Refs #145).
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && (g.FolderMissing || Directory.Exists(g.InstallPath)))
            .GroupBy(GameKey, StringComparer.Ordinal)
            .SelectMany(SplitDistinctLauncherEntries)
            .Select(Merge)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new GameLibraryScanResult(merged, results.Select(r => r.status).ToArray(), policyFailures);
    }

    private static string GameKey(DetectedGame g)
    {
        if (g.SteamAppId is int id)
        {
            // Normal storefront duplicates still merge by AppID. Manifest splitGames intentionally
            // produces multiple cards from one AppID, so those children need independent keys.
            if (g.IsManifestSplitChild)
                return $"steam:{id}:split:{g.Name}:{PathIdentity.Canonicalize(g.InstallPath)}";
            return $"steam:{id}";
        }
        // One folder is one key however it is spelt: symlinks (~/Games -> /media/disk/Games), a
        // trailing separator, and case (Linux paths are case-sensitive) (Refs #55).
        return $"path:{PathIdentity.Canonicalize(g.InstallPath)}";
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
            .Any(s => s.Select(IdentityOf).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (!conflict) return [entries];

        return entries
            .GroupBy(g => (g.Source ?? "") + "|" + IdentityOf(g), StringComparer.Ordinal)
            .Select(g => g.ToArray());
    }

    private static string IdentityOf(DetectedGame g)
        => PathIdentity.Canonicalize(string.IsNullOrWhiteSpace(g.LibraryIdentityPath) ? g.InstallPath : g.LibraryIdentityPath);

    /// <summary>
    /// Merges duplicates of one game into the first entry (detector order) without losing anything a
    /// duplicate knows (Refs #55): every optional field the first lacks is filled, every flag is OR-ed, and
    /// a manually added duplicate keeps its user-authored fields — the chosen executable (patch target),
    /// the API override and the launch command — and makes the card removable as a manual entry. The
    /// first entry keeps its name, path and source so the card's identity does not flip between scans.
    /// </summary>
    internal static DetectedGame Merge(DetectedGame[] games)
    {
        var first = games[0];
        // A fail-closed patch-target verdict (launcher exe) must not be satisfied by another source's
        // automatic guess: only a user-chosen (manual) executable may confirm the target.
        var requiresExplicitTarget = games.Any(g => g.RequiresExplicitPatchTarget);
        foreach (var candidate in games.Skip(1))
        {
            if (candidate.IsManuallyAdded && !first.IsManuallyAdded)
                ApplyUserAuthoredFields(first, candidate);
            first.IsManuallyAdded |= candidate.IsManuallyAdded;

            first.SteamAppId ??= candidate.SteamAppId;
            first.EpicCatalogNamespace ??= candidate.EpicCatalogNamespace;
            first.EpicAppName ??= candidate.EpicAppName;
            first.HeroicAppName ??= candidate.HeroicAppName;
            first.XboxAumid ??= candidate.XboxAumid;
            first.PrefixPath ??= candidate.PrefixPath;
            if (first.ExecutablePath is null && candidate.ExecutablePath is not null)
            {
                // The binary directory belongs to the executable it was derived from, never on its own.
                first.ExecutablePath = candidate.ExecutablePath;
                if (!requiresExplicitTarget) first.BinaryDirectory = candidate.BinaryDirectory ?? first.BinaryDirectory;
            }
            first.LaunchExecutablePath ??= candidate.LaunchExecutablePath;
            first.MergedAliases = first.MergedAliases.Concat(candidate.MergedAliases)
                .Append(new LibraryEntryAlias(candidate.Source ?? "",
                    string.IsNullOrWhiteSpace(candidate.LibraryIdentityPath) ? candidate.InstallPath : candidate.LibraryIdentityPath,
                    candidate.SteamAppId, candidate.HeroicAppName))
                .Distinct().ToArray();
            first.LibraryIdentityPath ??= candidate.LibraryIdentityPath;
            first.LibraryProvider ??= candidate.LibraryProvider;
            first.LaunchProvider ??= candidate.LaunchProvider;
            first.Runner ??= candidate.Runner;
            // How the card starts is one unit (Refs #151): the command, the "Launch opens the launcher" hint that
            // describes it, and the Edit game command / detected launch identity resolution builds from them. A
            // duplicate's hint or detected launch never lands beside another source's command.
            if (first.LaunchCommand is null && candidate.LaunchCommand is not null)
                TakeLaunch(first, candidate);
            else if (first.LaunchCommand is null)
                first.ParentLauncherName ??= candidate.ParentLauncherName;
            if (first.PatchTargetAuthority == "Automatic") first.PatchTargetAuthority = candidate.PatchTargetAuthority;
            first.CompatibilityTool ??= candidate.CompatibilityTool;
            first.CompatibilityLayerType ??= candidate.CompatibilityLayerType;
            first.WineBinaryPath ??= candidate.WineBinaryPath;
            first.WineServerPath ??= candidate.WineServerPath;
            first.UserGraphicsApiOverride ??= candidate.UserGraphicsApiOverride;
            first.CanonicalManifestName ??= candidate.CanonicalManifestName;
            first.WikiNameOverride ??= candidate.WikiNameOverride;
            first.ManifestInstallRoot ??= candidate.ManifestInstallRoot;
            first.IsManifestSplitChild |= candidate.IsManifestSplitChild;
            // A fail-closed patch-target verdict from any source survives the merge.
            first.RequiresExplicitPatchTarget |= candidate.RequiresExplicitPatchTarget;
            first.GameRootNotAllowed |= candidate.GameRootNotAllowed;
            first.FolderMissing |= candidate.FolderMissing;
            first.PatchTargetFolderMissing |= candidate.PatchTargetFolderMissing;
            first.ManifestSteamAppIdOverride ??= candidate.ManifestSteamAppIdOverride;
            first.ManifestGraphicsApiOverride ??= candidate.ManifestGraphicsApiOverride;
            first.ManifestGraphicsApis = Union(first.ManifestGraphicsApis, candidate.ManifestGraphicsApis);
            first.EngineHint ??= candidate.EngineHint;
            first.ForcedArchitecture ??= candidate.ForcedArchitecture;
            first.EngineIniProjectOverride ??= candidate.EngineIniProjectOverride;
            first.EngineIniFileOverride ??= candidate.EngineIniFileOverride;
            first.WikiUnlinkedByManifest |= candidate.WikiUnlinkedByManifest;
            first.DlssScanSkippedByManifest |= candidate.DlssScanSkippedByManifest;
            first.UeExtendedByManifest |= candidate.UeExtendedByManifest;
            first.NativeHdrByManifest |= candidate.NativeHdrByManifest;
            first.UeExtendedBlockedByManifest |= candidate.UeExtendedBlockedByManifest;
            first.UeExtendedHdrByManifest ??= candidate.UeExtendedHdrByManifest;
            first.UeExtendedLutByManifest ??= candidate.UeExtendedLutByManifest;
            first.DofFixForcedByManifest |= candidate.DofFixForcedByManifest;
            first.DofFixSkippedByManifest |= candidate.DofFixSkippedByManifest;
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
            if (first.GraphicsApi == GraphicsApi.Unknown && candidate.GraphicsApi != GraphicsApi.Unknown)
            {
                // Confidence and evidence describe the API they came with.
                first.GraphicsApi = candidate.GraphicsApi;
                first.GraphicsApiConfidence = candidate.GraphicsApiConfidence;
                first.GraphicsApiEvidence = candidate.GraphicsApiEvidence;
            }
            first.GraphicsApiEvidence ??= candidate.GraphicsApiEvidence;
            if (first.GraphicsApiConfidence == GraphicsApiConfidence.Unknown) first.GraphicsApiConfidence = candidate.GraphicsApiConfidence;
            first.GraphicsApis = Union(first.GraphicsApis, candidate.GraphicsApis);
            // w3/sync rows
            first.DlssPathOverride ??= candidate.DlssPathOverride;
            first.StreamlinePathOverride ??= candidate.StreamlinePathOverride;
            // w3/journal rows
            // w3/scan rows
            first.XboxPackage ??= candidate.XboxPackage;
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            first.UserDisplayName ??= candidate.UserDisplayName;
            first.UserWikiName ??= candidate.UserWikiName;
            first.DetectedWikiName ??= candidate.DetectedWikiName;
            first.UserArchitectureOverride ??= candidate.UserArchitectureOverride;
            // w5/reshade-core rows
            first.ManifestLegacyReShadeVersion ??= candidate.ManifestLegacyReShadeVersion;
            first.ManifestReShadeNote ??= candidate.ManifestReShadeNote;
            first.ManifestReShadeNoteUrl ??= candidate.ManifestReShadeNoteUrl;
            first.ManifestReShadeNoteUrlLabel ??= candidate.ManifestReShadeNoteUrlLabel;
            // w7/smalls rows
            // w7/reshade rows
            first.ManifestXnaGacFolder ??= candidate.ManifestXnaGacFolder;
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
        }
        return first;
    }

    /// <summary>The user picked these for the manual entry; a launcher's automatic guess must not replace them.</summary>
    private static void ApplyUserAuthoredFields(DetectedGame target, DetectedGame manual)
    {
        if (!string.IsNullOrWhiteSpace(manual.ExecutablePath))
        {
            target.ExecutablePath = manual.ExecutablePath;
            target.BinaryDirectory = manual.BinaryDirectory ?? Path.GetDirectoryName(manual.ExecutablePath);
        }
        if (manual.UserGraphicsApiOverride is not null) target.UserGraphicsApiOverride = manual.UserGraphicsApiOverride;
        if (!string.IsNullOrWhiteSpace(manual.LaunchCommand))
            TakeLaunch(target, manual);
    }

    /// <summary>Copies how <paramref name="source"/> starts (command, launcher hint, Edit game command, detected launch) as one unit.</summary>
    private static void TakeLaunch(DetectedGame target, DetectedGame source)
    {
        target.LaunchCommand = source.LaunchCommand;
        target.ParentLauncherName = source.ParentLauncherName;
        target.UserLaunchCommand = source.UserLaunchCommand;
        target.DetectedLaunch = source.DetectedLaunch;
    }

    private static IReadOnlyList<GraphicsApi> Union(IReadOnlyList<GraphicsApi> a, IReadOnlyList<GraphicsApi> b)
        => b.Count == 0 ? a : a.Count == 0 ? b : a.Concat(b).Distinct().ToArray();
}
