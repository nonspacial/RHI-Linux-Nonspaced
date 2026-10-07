using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Applies upstream RHI's launcher-independent game policy after Linux storefront detection.
/// Only filesystem-safe, cross-platform semantics are enacted here. Windows-only driver/admin
/// settings remain metadata for future platform support.
/// </summary>
public sealed class UpstreamGameLibraryPolicyService(UpstreamRhiManifestService manifest) : IGameLibraryPolicy
{
    /// <summary>
    /// An explicit full library refresh is the user-visible policy refresh point: the manifest is
    /// revalidated (ETag) once and every other consumer in the session reuses it. Partial refreshes
    /// never touch the network for policy (Refs #61).
    /// </summary>
    public void RefreshSources() => manifest.InvalidateProcessCache();

    /// <summary>Stable name; the Desktop status line localises it.</summary>
    public const string PolicyName = "Upstream RHI manifest";

    public string Name => PolicyName;

    public async Task<IReadOnlyList<DetectedGame>> ApplyAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken = default)
    {
        var policy = await manifest.FetchLibraryPolicyAsync(cancellationToken).ConfigureAwait(false);
        var knownNames = policy.KnownGameNames;
        var result = new List<DetectedGame>(games.Count);

        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBlacklisted(game.Name, policy)) continue;

            var canonical = ResolveCanonicalName(game, knownNames) ?? game.Name.Trim();
            // The blacklist is a title policy. A canonical name recovered from path evidence
            // (mount point "Windows", a "Blender" folder, "Unreal Engine" in a path) must never
            // hide a game; only a title-level match may.
            if (IsBlacklisted(canonical, policy) && IsTitleMatch(game.Name, canonical)) continue;

            if (TryGet(policy.SplitGames, canonical, out var splitEntries))
            {
                var splitRoot = game.InstallPath;
                var added = 0;
                foreach (var split in splitEntries)
                {
                    var splitPath = ResolveExistingRelativePath(splitRoot, split.SubPath);
                    if (splitPath is null) continue;
                    var child = Clone(game);
                    child.Name = split.Name;
                    child.InstallPath = splitPath;
                    child.ManifestInstallRoot = splitRoot;
                    child.IsManifestSplitChild = true;
                    var childCanonical = ResolveCanonicalName(child, knownNames) ?? split.Name;
                    if (IsBlacklisted(child.Name, policy) || IsBlacklisted(childCanonical, policy)) continue;
                    ApplySingle(child, childCanonical, policy);
                    result.Add(child);
                    added++;
                }
                if (added > 0) continue;
                // Upstream only replaces a bundle when at least one declared sub-game exists.
                // A stale/partial manifest must not make the launcher entry disappear.
            }

            ApplySingle(game, canonical, policy);
            result.Add(game);
        }

        return result;
    }

    private static void ApplySingle(DetectedGame game, string canonical, UpstreamLibraryPolicySnapshot policy)
    {
        game.CanonicalManifestName = canonical;

        if (TryGet(policy.WikiNameOverrides, canonical, out var wikiName))
            game.WikiNameOverride = wikiName;

        if (TryGet(policy.InstallPathOverrides, canonical, out var installPathOverride))
        {
            var originalRoot = game.InstallPath;
            var resolved = ResolveFirstExistingCandidate(originalRoot, installPathOverride);
            if (resolved is not null && !PathsEqual(resolved, originalRoot))
            {
                game.ManifestInstallRoot ??= originalRoot;
                game.InstallPath = resolved;
            }
        }

        if (TryGet(policy.SteamAppIdOverrides, canonical, out var steamAppId))
        {
            game.ManifestSteamAppIdOverride = steamAppId;
            // SteamAppId is also launcher/dedupe/Proton identity on Linux. Never turn a
            // Heroic/Lutris/Wine entry into a Steam entry merely because upstream supplies
            // an AppID for metadata/PCGW matching. Existing Steam entries may be corrected.
            if (game.SteamAppId.HasValue || game.Source.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                game.SteamAppId = steamAppId;
        }

        game.WikiUnlinkedByManifest = policy.WikiUnlinks.Contains(canonical);
        game.DlssScanSkippedByManifest = policy.DlssSkipGames.Contains(canonical);
        game.UeExtendedBlockedByManifest = policy.NoUeExtendedGames.Contains(canonical);
        game.UeExtendedByManifest = !game.UeExtendedBlockedByManifest
            && (policy.UeExtendedGames.Contains(canonical)
                || policy.NativeHdrGames.Contains(canonical)
                || policy.UeExtendedCompatibility.ContainsKey(canonical));
        game.NativeHdrByManifest = !game.UeExtendedBlockedByManifest
            && (policy.NativeHdrGames.Contains(canonical) || policy.UeExtendedCompatibility.ContainsKey(canonical));
        if (TryGet(policy.UeExtendedCompatibility, canonical, out var ueCompat))
        {
            game.UeExtendedHdrByManifest = ueCompat.Hdr;
            game.UeExtendedLutByManifest = ueCompat.Lut;
        }
        if (TryGet(policy.EngineIniFiles, canonical, out var engineIniFile))
            game.EngineIniFileOverride = engineIniFile;
        if (TryGet(policy.OptiScalerDllOverrides, canonical, out var optiDll))
            game.OptiScalerDllNameOverride = optiDll;
        if (TryGet(policy.PdUpscalerGames, canonical, out var pdArtifact))
            game.PdUpscalerArtifact = pdArtifact;
        if (TryGet(policy.WikiStatusOverrides, canonical, out var wikiStatus))
            game.WikiStatusOverride = wikiStatus;
        if (TryGet(policy.NexusUrlOverrides, canonical, out var nexus))
            game.NexusUrlOverride = nexus;
        if (TryGet(policy.PcgwUrlOverrides, canonical, out var pcgw))
            game.PcgwUrlOverride = pcgw;
        if (TryGet(policy.UwFixUrlOverrides, canonical, out var uwFix))
            game.UwFixUrlOverride = uwFix;
        if (TryGet(policy.UltraPlusUrlOverrides, canonical, out var ultraPlus))
            game.UltraPlusUrlOverride = ultraPlus;
        if (TryGet(policy.AuthorOverrides, canonical, out var author))
            game.AuthorOverride = author;
        // Upstream matches install warnings on the card's own title. Like the blacklist, they are a title policy here:
        // a canonical name recovered only from path evidence (a game folder named after another manifest title) must not
        // attach that game's warning to this one.
        if (IsTitleMatch(game.Name, canonical) && TryGet(policy.InstallWarnings, canonical, out var warnings))
            game.ManifestInstallWarnings = warnings;
        if (TryGet(policy.GameNotes, canonical, out var gameNote))
        {
            game.ManifestGameNote = gameNote.Notes;
            game.ManifestGameNoteUrl = gameNote.Url;
            game.ManifestGameNoteUrlLabel = gameNote.UrlLabel;
        }

        if (TryGet(policy.DllNameOverrides, canonical, out var dllNames))
        {
            game.ReShadeDllNameOverride = dllNames.ReShade;
            game.DisplayCommanderDllNameOverride = dllNames.DisplayCommander;
        }

        if (TryGet(policy.ForceExternalOnly, canonical, out var external))
        {
            game.ExternalModUrl = external.Url;
            game.ExternalModLabel = external.Label;
        }

        if (TryGet(policy.LaunchExeOverrides, canonical, out var launchExe))
        {
            game.ManifestLaunchExecutable = launchExe;
            var resolvedExe = ResolveExistingFile(game.InstallPath, launchExe);
            if (resolvedExe is not null)
            {
                game.ExecutablePath = resolvedExe;
                game.BinaryDirectory = Path.GetDirectoryName(resolvedExe);
            }
        }
    }

    private static bool IsBlacklisted(string? name, UpstreamLibraryPolicySnapshot policy)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return policy.Blacklist.Contains(trimmed)
               || policy.BlacklistPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the canonical name was reached from the title itself rather than from path evidence.</summary>
    private static bool IsTitleMatch(string title, string canonical)
        => !string.IsNullOrWhiteSpace(title) &&
           (title.Trim().Equals(canonical, StringComparison.OrdinalIgnoreCase) ||
            NormalizeIdentity(title) == NormalizeIdentity(canonical));

    private static string? ResolveCanonicalName(DetectedGame game, IReadOnlySet<string> knownNames)
    {
        if (!string.IsNullOrWhiteSpace(game.Name) && knownNames.Contains(game.Name.Trim()))
            return knownNames.First(x => x.Equals(game.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(game.Name))
        {
            var normalizedTitle = GamePathIdentity.Normalize(game.Name);
            var titleMatches = knownNames
                .Where(name => GamePathIdentity.Normalize(name) == normalizedTitle)
                .Take(2)
                .ToArray();
            if (titleMatches.Length == 1) return titleMatches[0];
        }

        var evidence = GamePathIdentity.Of(game);
        if (evidence.IsEmpty) return null;

        var matches = knownNames
            .Where(name => GamePathIdentity.Matches(evidence, name))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string NormalizeIdentity(string value) => GamePathIdentity.Normalize(value);

    private static string? ResolveFirstExistingCandidate(string root, string raw)
    {
        foreach (var candidate in raw.Split('|', StringSplitOptions.TrimEntries))
        {
            if (candidate.Length == 0) return root;
            var path = ResolveExistingRelativePath(root, candidate);
            if (path is not null) return path;
        }
        return null;
    }

    /// <summary>
    /// Resolves a manifest sub-path (installPathOverrides, splitGames) below <paramref name="root"/>.
    /// A candidate that leaves the game root (<c>..</c>, an absolute path, or a symlink pointing out) is
    /// rejected: upstream entries such as <c>"Hades II": "Ship|..\\.."</c> would otherwise make a Steam
    /// <c>steamapps/</c> folder or a whole library the install path and the patch/indexing root.
    /// </summary>
    private static string? ResolveExistingRelativePath(string root, string relative)
    {
        var path = CombineManifestPath(root, relative);
        return path is not null && Directory.Exists(path) && PathIdentity.IsSameOrInside(path, root) ? path : null;
    }

    /// <summary>launchExeOverrides: the executable must exist inside the game root, as for sub-paths.</summary>
    private static string? ResolveExistingFile(string root, string relative)
    {
        var path = CombineManifestPath(root, relative);
        return path is not null && File.Exists(path) && PathIdentity.IsSameOrInside(path, root) ? path : null;
    }

    private static string? CombineManifestPath(string root, string relative)
    {
        var normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        try { return Path.GetFullPath(Path.Combine(root, normalized)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // malformed manifest path: no override, the detected root stays
        }
    }

    private static bool PathsEqual(string a, string b) => PathIdentity.AreSame(a, b);

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> values, string key, out T value)
        => values.TryGetValue(key, out value!);

    private static DetectedGame Clone(DetectedGame game)
        => new()
        {
            Name = game.Name,
            InstallPath = game.InstallPath,
            Source = game.Source,
            IsManuallyAdded = game.IsManuallyAdded,
            SteamAppId = game.SteamAppId,
            ManifestSteamAppIdOverride = game.ManifestSteamAppIdOverride,
            EpicCatalogNamespace = game.EpicCatalogNamespace,
            EpicAppName = game.EpicAppName,
            HeroicAppName = game.HeroicAppName,
            XboxAumid = game.XboxAumid,
            PrefixPath = game.PrefixPath,
            ExecutablePath = game.ExecutablePath,
            LibraryIdentityPath = game.LibraryIdentityPath,
            LibraryProvider = game.LibraryProvider,
            LaunchExecutablePath = game.LaunchExecutablePath,
            LaunchProvider = game.LaunchProvider,
            RequiresExplicitPatchTarget = game.RequiresExplicitPatchTarget,
            PatchTargetAuthority = game.PatchTargetAuthority,
            Runner = game.Runner,
            LaunchCommand = game.LaunchCommand,
            ParentLauncherName = game.ParentLauncherName,
            CompatibilityTool = game.CompatibilityTool,
            CompatibilityLayerType = game.CompatibilityLayerType,
            WineBinaryPath = game.WineBinaryPath,
            WineServerPath = game.WineServerPath,
            GraphicsApi = game.GraphicsApi,
            UserGraphicsApiOverride = game.UserGraphicsApiOverride,
            ManifestGraphicsApiOverride = game.ManifestGraphicsApiOverride,
            ManifestGraphicsApis = game.ManifestGraphicsApis,
            GraphicsApis = game.GraphicsApis,
            GraphicsApiConfidence = game.GraphicsApiConfidence,
            GraphicsApiEvidence = game.GraphicsApiEvidence,
            BinaryDirectory = game.BinaryDirectory
        };
}
