namespace RHI.Core.Models;

/// <summary>
/// Cross-platform evolution of RHI's original DetectedGame model.
/// The original fields are intentionally preserved; Linux-specific metadata is additive.
/// </summary>
public sealed class DetectedGame
{
    /// <summary>
    /// Shallow copy of the detector output. Detector results are cached between partial rescans
    /// and every downstream stage (policy, merge, identity, runtime probe) mutates the instance,
    /// so cached entries must never be handed out directly.
    /// </summary>
    public DetectedGame ShallowClone() => (DetectedGame)MemberwiseClone();

    public string Name { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsManuallyAdded { get; set; }
    /// <summary>Persisted library visibility state; not launcher metadata.</summary>
    public bool IsFavourite { get; set; }
    /// <summary>Persisted library visibility state; hidden games remain reachable through Favourites/Hidden.</summary>
    public bool IsHidden { get; set; }
    /// <summary>
    /// A saved manual entry whose folder the game-root guard refuses ($HOME, /, a whole Wine prefix…).
    /// It stays listed so the user can see and remove it, but nothing indexes or patches that folder.
    /// </summary>
    public bool GameRootNotAllowed { get; set; }
    public string LibraryBadge => (IsFavourite ? "★" : "") + (IsHidden ? "◌" : "") + (GameRootNotAllowed ? "⚠" : "");
    public int? SteamAppId { get; set; }
    /// <summary>Upstream Steam AppID policy metadata without changing a non-Steam launcher's identity.</summary>
    public int? ManifestSteamAppIdOverride { get; set; }
    public string? EpicCatalogNamespace { get; set; }
    public string? EpicAppName { get; set; }
    /// <summary>Heroic application id for any Heroic runner (Epic/GOG/Amazon/sideload).</summary>
    public string? HeroicAppName { get; set; }
    public string? XboxAumid { get; set; }

    // Linux additions.
    public string? PrefixPath { get; set; }

    /// <summary>
    /// Executable used as the game/runtime authority for architecture, graphics API detection and game-local deployment.
    /// For launcher-mediated entries this may intentionally be null until the user confirms a patch target.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Original launcher/store identity path used to keep favourites and per-game overrides stable when a manual
    /// patch target points at a different game root.
    /// </summary>
    public string? LibraryIdentityPath { get; set; }

    /// <summary>Human-readable library/source provider used by the header badge (Steam, Heroic, Lutris, Manual/RHI, Wine).</summary>
    public string? LibraryProvider { get; set; }

    /// <summary>Executable supplied by the launcher for starting the entry. It is never used as a patch destination.</summary>
    public string? LaunchExecutablePath { get; set; }

    /// <summary>Human-readable launcher/platform provider shown by the second header badge.</summary>
    public string? LaunchProvider { get; set; }

    /// <summary>
    /// True when the discovered executable is a launcher/shared client and RHI must not deploy game-local payloads
    /// until a separate executable target is confirmed.
    /// </summary>
    public bool RequiresExplicitPatchTarget { get; set; }

    /// <summary>Automatic or Manual; presentation only, never an ownership claim.</summary>
    public string PatchTargetAuthority { get; set; } = "Automatic";

    public string? Runner { get; set; }
    public string? LaunchCommand { get; set; }
    /// <summary>
    /// Set when Launch starts this launcher rather than the game (a game installed inside a launcher's
    /// prefix, e.g. Ubisoft Connect or Battle.net); the user starts the game from the launcher.
    /// </summary>
    public string? ParentLauncherName { get; set; }
    public string? CompatibilityTool { get; set; }
    /// <summary>Compatibility-layer family selected by the launcher (for example wine or proton).</summary>
    public string? CompatibilityLayerType { get; set; }
    /// <summary>Actual Wine binary used by launcher-managed prefixes. Proton entries are normalized to their embedded files/dist/bin/wine when possible.</summary>
    public string? WineBinaryPath { get; set; }
    /// <summary>Optional wineserver matching <see cref="WineBinaryPath"/>.</summary>
    public string? WineServerPath { get; set; }
    public GraphicsApi GraphicsApi { get; set; } = GraphicsApi.Unknown;
    /// <summary>Explicit per-game user authority. Null means automatic runtime/manifest detection.</summary>
    public GraphicsApi? UserGraphicsApiOverride { get; set; }
    /// <summary>Published upstream manifest primary API authority. Null means the local detector remains authoritative.</summary>
    public GraphicsApi? ManifestGraphicsApiOverride { get; set; }
    /// <summary>All APIs declared by upstream for dual/multi-API games (for example "DX12, VLK").</summary>
    public IReadOnlyList<GraphicsApi> ManifestGraphicsApis { get; set; } = [];
    /// <summary>Effective API for component eligibility; explicit user authority wins, then the upstream manifest, then local detection.</summary>
    public GraphicsApi EffectiveGraphicsApi => UserGraphicsApiOverride ?? ManifestGraphicsApiOverride ?? GraphicsApi;
    /// <summary>Effective API set for display/capability checks while preserving upstream multi-API declarations.</summary>
    public IReadOnlyList<GraphicsApi> EffectiveGraphicsApis =>
        UserGraphicsApiOverride is GraphicsApi userApi && userApi != GraphicsApi.Unknown
            ? [userApi]
            : ManifestGraphicsApis.Count > 0
                ? ManifestGraphicsApis
                : GraphicsApis.Count > 0
                    ? GraphicsApis
                    : GraphicsApi != GraphicsApi.Unknown ? [GraphicsApi] : [];
    public bool HasExplicitGraphicsApiOverride => UserGraphicsApiOverride.HasValue && UserGraphicsApiOverride.Value != GraphicsApi.Unknown;
    public IReadOnlyList<GraphicsApi> GraphicsApis { get; set; } = [];
    public GraphicsApiConfidence GraphicsApiConfidence { get; set; } = GraphicsApiConfidence.Unknown;
    public string? GraphicsApiEvidence { get; set; }
    /// <summary>Canonical title matched from the live upstream RHI manifest.</summary>
    public string? CanonicalManifestName { get; set; }
    /// <summary>Optional RenoDX wiki-name mapping supplied by the upstream manifest.</summary>
    public string? WikiNameOverride { get; set; }
    /// <summary>Original launcher/store install root before a manifest split/installPathOverride is applied.</summary>
    public string? ManifestInstallRoot { get; set; }
    /// <summary>True only for a child created from upstream splitGames; installPathOverride alone does not set this.</summary>
    public bool IsManifestSplitChild { get; set; }
    /// <summary>True when the manifest explicitly unlinks this title from RenoDX wiki matching.</summary>
    public bool WikiUnlinkedByManifest { get; set; }
    /// <summary>True when the manifest says recursive DLSS/Streamline discovery can be skipped.</summary>
    public bool DlssScanSkippedByManifest { get; set; }
    public bool UeExtendedByManifest { get; set; }
    public bool NativeHdrByManifest { get; set; }
    public bool UeExtendedBlockedByManifest { get; set; }
    /// <summary>Nullable upstream UE-Extended compatibility overrides; null means upstream default behavior.</summary>
    public bool? UeExtendedHdrByManifest { get; set; }
    public bool? UeExtendedLutByManifest { get; set; }
    public string? ReShadeDllNameOverride { get; set; }
    public string? DisplayCommanderDllNameOverride { get; set; }
    public string? OptiScalerDllNameOverride { get; set; }
    public string? PdUpscalerArtifact { get; set; }
    public string? ManifestLaunchExecutable { get; set; }
    public string? ExternalModUrl { get; set; }
    public string? ExternalModLabel { get; set; }
    public string? NexusUrlOverride { get; set; }
    public string? PcgwUrlOverride { get; set; }
    public string? UwFixUrlOverride { get; set; }
    public string? UltraPlusUrlOverride { get; set; }
    public string? WikiStatusOverride { get; set; }
    public string? AuthorOverride { get; set; }
    public string? ManifestGameNote { get; set; }
    public string? ManifestGameNoteUrl { get; set; }
    public string? ManifestGameNoteUrlLabel { get; set; }
    public IReadOnlyDictionary<string, string> ManifestInstallWarnings { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional engine hint supplied by the upstream RHI manifest.</summary>
    public string? EngineHint { get; set; }
    /// <summary>Optional upstream manifest bitness override used when a game ships ambiguous launch executables.</summary>
    public PeArchitecture? ForcedArchitecture { get; set; }
    /// <summary>Optional upstream-RHI Unreal project/config path authority for Engine.ini deployment.</summary>
    public string? EngineIniProjectOverride { get; set; }
    /// <summary>Optional upstream RHI engine-files template filename applied transactionally with UE-Extended RenoDX installs.</summary>
    public string? EngineIniFileOverride { get; set; }
    /// <summary>True when the upstream RHI manifest force-enables Universal UE DOF Fix for this title.</summary>
    public bool DofFixForcedByManifest { get; set; }
    /// <summary>True when the upstream RHI manifest suppresses Universal UE DOF Fix for this title.</summary>
    public bool DofFixSkippedByManifest { get; set; }
    public string? BinaryDirectory { get; set; }

    /// <summary>
    /// Game-local patch target is ready when no explicit split is required or when a real game executable has been
    /// confirmed separately from the launcher.
    /// </summary>
    public bool HasConfirmedPatchTarget =>
        !GameRootNotAllowed && (!RequiresExplicitPatchTarget ||
        (!string.IsNullOrWhiteSpace(ExecutablePath) && !string.IsNullOrWhiteSpace(BinaryDirectory)));

    public bool UsesProton => !string.IsNullOrWhiteSpace(PrefixPath) && SteamAppId.HasValue;

    public override string ToString() => $"{Name} ({Source})";
}
