using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record LumaFrameworkReleaseInfo(string Version, string AssetUrl, string AssetName, string? Sha256);

public sealed record LumaPackageSelection(
    LumaFrameworkReleaseInfo Release,
    bool IsNamed,
    string DisplayName,
    string? Author,
    string? SpecialNotes,
    LumaGenericGameEntry? GenericMetadata);

/// <summary>
/// Linux-native deployment service for the upstream RHI Luma Framework paths.
/// The upstream Completed Mods catalog and lumaNameOverrides data give named game-specific
/// Luma packages priority over the generic Unreal package. The generic Unreal wiki metadata
/// (notes, Engine.ini keys, launch arguments, HDR/DLSS-FSR flags, UE version) is also consumed. Package extraction still excludes bundled
/// ReShade proxies/reshade.ini/nvngx_dlss.dll, routes ReShade add-ons through [ADDON] AddonPath, and
/// enables [Luma] EnableHDR/DisplayMode with Linux ownership, digest verification, Zip-Slip rejection
/// and conservative rollback.
/// </summary>
public sealed class LumaFrameworkDeploymentService
{
    /// <summary>Test seam: runs at the start of each install rollback step with its description (may throw).</summary>
    internal Action<string>? RollbackStepFaultForTesting { get; set; }

    public const string LatestReleaseApiUrl = "https://api.github.com/repos/Filoppi/Luma-Framework/releases/latest";
    public const string GenericUnrealAssetName = "Luma-Unreal_Engine.zip";
    internal const string MetaDx9Migration = "LumaFramework.Dx9ReShadeMigration";
    internal const string MetaDgVoodooVersion = "LumaFramework.DgVoodooVersion";
    internal const string MetaLocalPackage = "LumaFramework.LocalPackage";

    private const long MinimumArchiveSize = 64 * 1024;
    private static readonly HashSet<string> PackageOwnedFilesSkippedByRhi = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll",
        "opengl32.dll", "ReShade64.dll", "ReShade32.dll", "ReShade.ini", "reshade.ini",
        "nvngx_dlss.dll", "dgVoodoo.conf"
    };
    private static readonly HashSet<string> AcceptedDx9DgVoodooFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        "Borderlands 2",
        "Borderlands: The Pre-Sequel",
        "Medal of Honor: Airborne",
        "The Witcher 2: Assassins of Kings Enhanced Edition",
        "Vanquish"
    };

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly GitHubReleaseAssetResolver _resolver;
    private readonly LumaCatalogService _catalog;
    private readonly LumaPolicyService? _policy;
    private readonly UpstreamRhiManifestService? _manifest;
    private readonly DgVoodooDeploymentService? _dgVoodoo;
    private readonly Dx9TranslationReShadeCoordinator? _dx9ReShade;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheDir;

    public LumaFrameworkDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        string? cacheDir = null,
        LumaCatalogService? catalog = null,
        LumaPolicyService? policy = null,
        UpstreamRhiManifestService? manifest = null,
        DgVoodooDeploymentService? dgVoodoo = null,
        Dx9TranslationReShadeCoordinator? dx9ReShade = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _resolver = new GitHubReleaseAssetResolver(http);
        _catalog = catalog ?? new LumaCatalogService(http, manifest);
        _policy = policy;
        _manifest = manifest;
        _dgVoodoo = dgVoodoo;
        _dx9ReShade = dx9ReShade;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "luma-framework");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.LumaFramework);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "Luma Framework is not managed by RHI Linux.");
        if (record.Metadata.GetValueOrDefault(MetaLocalPackage) == "1")
            return new ManagedUpdateCheck(true, false,
                "This Luma package was installed from a local drag/drop file. Drop a Luma archive/add-on onto the game again to reinstall or change it; automatic update has no trusted source URL.",
                record.Version, CouldNotCheck: true);
        try
        {
            var selection = await ResolvePackageAsync(game, cancellationToken).ConfigureAwait(false);
            if (selection is null)
                // Nothing to compare the install against: not known to be current (w2-launch review, minor 4).
                return new ManagedUpdateCheck(true, false, "No current trusted Luma package matches this game; installed payload was preserved.", record.Version,
                    CouldNotCheck: true);
            var latest = selection.Release;
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.Metadata.GetValueOrDefault("LumaFramework.Package"), latest.AssetName, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                update ? $"Luma update available: {record.Version ?? "unknown"} → {latest.Version} ({latest.AssetName})." : $"Luma {record.Version ?? latest.Version} is current.",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"Luma update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return Task.FromResult(ComponentOperationResult.Fail("Luma Framework is not managed by RHI Linux."));
        if (record.Metadata.GetValueOrDefault(MetaLocalPackage) == "1")
            return Task.FromResult(ComponentOperationResult.Fail(
                "This Luma install came from a local drag/drop package and has no trusted automatic download URL. Drop the Luma archive/add-on onto the game again to reinstall it."));
        return InstallAsync(game, cancellationToken);
    }

    public async Task PrimeNamedMatchAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        await _catalog.ResolveAsync(game, cancellationToken).ConfigureAwait(false);
        await _catalog.ResolveGenericUnrealAsync(game, cancellationToken).ConfigureAwait(false);
    }

    public LumaCatalogEntry? GetCachedNamedMatch(DetectedGame game)
        => _catalog.TryGetCached(game, out var entry) ? entry : null;

    public LumaGenericGameEntry? GetCachedGenericMatch(DetectedGame game)
        => _catalog.TryGetGenericCached(game, out var entry) ? entry : null;

    public static bool IsGameEligible(DetectedGame game, LumaGenericGameEntry? metadata = null)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX11) return false;
        if (string.IsNullOrWhiteSpace(game.EngineHint) ||
            !game.EngineHint.Contains("Unreal", StringComparison.OrdinalIgnoreCase)) return false;
        // Upstream RHI allows UE5 only when the user explicitly overrides the API to DX11.
        // When local disk evidence proves Unreal but cannot resolve its generation, fail closed
        // unless that same explicit DX11 authority exists; an unknown UE generation may be UE5.
        var explicitDx11 = game.UserGraphicsApiOverride == GraphicsApi.DirectX11;
        var knownUe4 = game.EngineHint.Contains("Unreal Engine 4", StringComparison.OrdinalIgnoreCase);
        var knownUe5 = game.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase);
        if ((knownUe5 || (!knownUe4 && game.EngineHint.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase))) &&
            !explicitDx11) return false;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) return false;
        if (GameArchitecture.Of(game) != PeArchitecture.X64) return false;
        // Pinned RHI suppresses a generic entry explicitly marked DLSS/FSR-blocked when the
        // same table does not advertise HDR support for that title.
        if (metadata is { DlssFsrBlocked: true, HdrSupported: false }) return false;
        return true;
    }

    public static string EligibilityReason(DetectedGame game, LumaGenericGameEntry? metadata = null)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "Luma Framework is a Windows injection component and is not deployed to native Linux games.";
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX11)
            return game.HasExplicitGraphicsApiOverride
                ? $"Generic Luma requires DirectX 11; the explicit API override is {game.EffectiveGraphicsApi}."
                : "Generic Luma follows RHI's Unreal DirectX 11 path; this game is not detected as DX11.";
        if (string.IsNullOrWhiteSpace(game.EngineHint) || !game.EngineHint.Contains("Unreal", StringComparison.OrdinalIgnoreCase))
            return "Generic Luma requires a detected Unreal Engine game.";
        var explicitDx11 = game.UserGraphicsApiOverride == GraphicsApi.DirectX11;
        var knownUe4 = game.EngineHint.Contains("Unreal Engine 4", StringComparison.OrdinalIgnoreCase);
        var knownUe5 = game.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase);
        if ((knownUe5 || (!knownUe4 && game.EngineHint.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase))) &&
            !explicitDx11)
            return knownUe5
                ? "RHI generic Luma excludes UE5 unless the user explicitly forces DirectX 11. Set the per-game API override to DirectX 11 only when the game really supports that launch path."
                : "Local disk evidence identifies Unreal Engine but not its generation. Generic Luma remains unavailable because the game may be UE5; an explicit DirectX 11 override is required to authorize generic Luma.";
        if (GameArchitecture.Of(game) == PeArchitecture.X86)
            return "Generic Luma is currently enabled for 64-bit Windows games.";
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath) ||
            GameArchitecture.Of(game) != PeArchitecture.X64)
            return "Generic Luma requires a detected 64-bit Windows executable.";
        if (metadata is { DlssFsrBlocked: true, HdrSupported: false })
            return $"The Luma generic table explicitly blocks DLSS/FSR for '{metadata.Name}' and does not mark HDR support; pinned RHI suppresses generic Luma for this entry.";
        return game.UserGraphicsApiOverride == GraphicsApi.DirectX11
            ? $"Eligible for generic Luma Framework ({game.EngineHint}, explicit DirectX 11 override, x64)."
            : $"Eligible for generic Luma Framework ({game.EngineHint}, DirectX 11, x64).";
    }

    public static bool IsNamedGameEligible(DetectedGame game, LumaCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl)) return false;
        if (!IsTrustedFiloppiUrl(entry.DownloadUrl)) return false;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) return false;
        var detected = GameArchitecture.Of(game);
        return detected is PeArchitecture.X64 or PeArchitecture.X86;
    }

    public static string NamedEligibilityReason(DetectedGame game, LumaCatalogEntry entry)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "Luma Framework is a Windows injection component and is not deployed to native Linux games.";
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            return $"Named Luma match '{entry.Name}' has no downloadable package in the upstream Completed Mods table.";
        if (!IsTrustedFiloppiUrl(entry.DownloadUrl))
            return $"Named Luma match '{entry.Name}' points outside Filoppi's GitHub namespace and is blocked by the Linux trust policy.";
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return $"Named Luma match '{entry.Name}' requires a detected Windows executable.";
        var detected = GameArchitecture.Of(game);
        if (detected is not (PeArchitecture.X64 or PeArchitecture.X86))
            return $"Named Luma match '{entry.Name}' requires a recognized x86/x64 Windows executable.";
        return string.IsNullOrWhiteSpace(entry.Author)
            ? $"Named Luma match: {entry.Name}. The upstream game-specific package takes priority over generic Unreal eligibility."
            : $"Named Luma match: {entry.Name} by {entry.Author}. The upstream game-specific package takes priority over generic Unreal eligibility.";
    }

    internal static bool RequiresDgVoodoo(DetectedGame game, IReadOnlySet<string> manifestGames)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(manifestGames);
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX9) return false;
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName) && manifestGames.Contains(game.CanonicalManifestName.Trim()))
            return true;
        return !string.IsNullOrWhiteSpace(game.Name) && manifestGames.Contains(game.Name.Trim());
    }

    public async Task<LumaPackageSelection?> ResolvePackageAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var named = await _catalog.ResolveAsync(game, cancellationToken).ConfigureAwait(false);
        if (named is not null)
        {
            if (!IsNamedGameEligible(game, named)) return null;
            var release = await ResolveNamedReleaseAsync(named, cancellationToken).ConfigureAwait(false);
            return new LumaPackageSelection(release, true, named.Name, named.Author, named.SpecialNotes, null);
        }

        var genericMetadata = await _catalog.ResolveGenericUnrealAsync(game, cancellationToken).ConfigureAwait(false);
        if (!IsGameEligible(game, genericMetadata)) return null;
        var generic = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        return new LumaPackageSelection(generic, false, "Generic Unreal Engine", null, null, genericMetadata);
    }

    public async Task<LumaFrameworkReleaseInfo> ResolveLatestAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await _resolver.ResolveAsync(
            new GitHubReleaseAssetQuery(LatestReleaseApiUrl, ExactAssetName: GenericUnrealAssetName),
            cancellationToken).ConfigureAwait(false);
        return new LumaFrameworkReleaseInfo(resolved.Version, resolved.AssetUrl, resolved.AssetName, resolved.Sha256);
    }

    public Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallCoreAsync(game, localPackagePath: null, cancellationToken);

    public Task<ComponentOperationResult> InstallLocalPackageAsync(
        DetectedGame game,
        string packagePath,
        CancellationToken cancellationToken = default)
        => InstallCoreAsync(game, packagePath, cancellationToken);

    private async Task<ComponentOperationResult> InstallCoreAsync(
        DetectedGame game,
        string? localPackagePath,
        CancellationToken cancellationToken)
    {
        var localPackage = !string.IsNullOrWhiteSpace(localPackagePath);
        LumaPackageSelection? localSelection = null;
        if (localPackage)
        {
            var fullLocalPath = Path.GetFullPath(localPackagePath!);
            if (!File.Exists(fullLocalPath))
                return ComponentOperationResult.Fail("The dropped Luma package no longer exists.");
            var localName = Path.GetFileName(fullLocalPath);
            var extension = Path.GetExtension(localName);
            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".addon", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("Local Luma reinstall accepts .zip, .addon, .addon32 or .addon64 packages only.");
            if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("Luma Framework is a Windows/ReShade component and cannot be installed into a native Linux game.");
            if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
                return ComponentOperationResult.Fail("Local Luma install requires a detected Windows executable.");
            var architecture = GameArchitecture.Of(game);
            if (architecture is not (PeArchitecture.X64 or PeArchitecture.X86))
                return ComponentOperationResult.Fail("Local Luma install requires a recognized x86/x64 Windows executable.");

            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var packageArchitecture = PeArchitectureDetector.Detect(fullLocalPath);
                if (packageArchitecture is not (PeArchitecture.X64 or PeArchitecture.X86))
                    return ComponentOperationResult.Fail("Local Luma add-on architecture could not be recognized as x86/x64 PE.");

                var extensionArchitecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                    ? PeArchitecture.X86
                    : extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
                        ? PeArchitecture.X64
                        : packageArchitecture;
                if (packageArchitecture != extensionArchitecture)
                    return ComponentOperationResult.Fail($"Local Luma add-on architecture does not match its {extension} extension.");
                if (packageArchitecture != architecture)
                    return ComponentOperationResult.Fail($"Local Luma add-on architecture ({packageArchitecture}) does not match the game architecture ({architecture}).");
            }

            // Lower case keeps the "local-<hash>" version label identical to earlier installs.
            var hash = (await FileHash.Sha256HexAsync(fullLocalPath, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            var localRelease = new LumaFrameworkReleaseInfo(
                $"local-{hash[..Math.Min(12, hash.Length)]}",
                string.Empty,
                localName,
                hash);
            localSelection = new LumaPackageSelection(
                localRelease,
                true,
                Path.GetFileNameWithoutExtension(localName),
                null,
                "Installed from a local drag/drop package.",
                null);
            localPackagePath = fullLocalPath;
        }

        LumaCatalogEntry? namedMatch = null;
        LumaGenericGameEntry? genericMetadata = null;
        var genericMetadataLookupCompleted = false;
        if (!localPackage)
        {
            try { namedMatch = await _catalog.ResolveAsync(game, cancellationToken).ConfigureAwait(false); }
            catch when (!cancellationToken.IsCancellationRequested) { }
            if (namedMatch is null)
            {
                try
                {
                    genericMetadata = await _catalog.ResolveGenericUnrealAsync(game, cancellationToken).ConfigureAwait(false);
                    genericMetadataLookupCompleted = true;
                }
                catch when (!cancellationToken.IsCancellationRequested) { }
            }

            if (namedMatch is not null)
            {
                if (!IsNamedGameEligible(game, namedMatch))
                    return ComponentOperationResult.Fail(NamedEligibilityReason(game, namedMatch));
            }
            else if (!IsGameEligible(game, genericMetadata))
            {
                return ComponentOperationResult.Fail(EligibilityReason(game, genericMetadata));
            }
        }

        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (_records.Find(game, ManagedComponentKind.ReShade) is null)
            return ComponentOperationResult.Fail("Luma Framework requires RHI-managed ReShade first. External ReShade is not adopted automatically.");
        if (RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory) is null)
            return ComponentOperationResult.Fail("Luma Framework requires the RHI-managed ReShade.ini before package deployment.");

        var existingRecord = _records.Find(game, ManagedComponentKind.LumaFramework);
        var requiresDgVoodoo = false;
        IReadOnlyDictionary<string, string> dgVoodooVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((namedMatch is not null || localPackage) && game.EffectiveGraphicsApi == GraphicsApi.DirectX9)
        {
            IReadOnlySet<string> dgGames = AcceptedDx9DgVoodooFallback;
            if (_manifest is not null)
            {
                try
                {
                    var live = await _manifest.FetchLumaRequiresDgVoodooAsync(cancellationToken).ConfigureAwait(false);
                    if (live.Count > 0) dgGames = live;
                    dgVoodooVersions = await _manifest.FetchDgVoodooVersionsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // Accepted 2.7.5 list remains the offline fallback.
                }
            }
            requiresDgVoodoo = RequiresDgVoodoo(game, dgGames);
        }

        if (RequiresDx9RemovalBeforeUpdate(existingRecord, requiresDgVoodoo))
            return ComponentOperationResult.Fail("The existing Luma install owns a DX9/dgVoodoo2 translation, but the current catalog no longer requires that path. Remove Luma first so RHI can restore ReShade safely, then install the current package again.");

        ReShadeDx9MigrationState? dx9Migration = null;
        LinuxDeploymentRecord? sharedDx9Record = null;
        if (requiresDgVoodoo)
        {
            // w7 follow-up B29: DX9 HDR holds the game's d3d9.dll; refused here, before the package is fetched or anything written.
            if (Dx9TranslationReShadeCoordinator.DxvkHdrRefusal(_records, game) is { } dxvkHdr) return ComponentOperationResult.Fail(dxvkHdr);
            dx9Migration = ReadDx9Migration(existingRecord, MetaDx9Migration);
            sharedDx9Record = dx9Migration is not null ? existingRecord : null;
            if (dx9Migration is null)
            {
                var feederRecord = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
                dx9Migration = ReadDx9Migration(feederRecord, Dlss5FeederFullDeploymentService.MetaDx9Migration);
                sharedDx9Record = dx9Migration is not null ? feederRecord : null;
            }
            if (sharedDx9Record is not null)
            {
                var sharedError = await ValidateSharedDx9TranslationAsync(sharedDx9Record, cancellationToken).ConfigureAwait(false);
                if (sharedError is not null)
                    return ComponentOperationResult.Fail($"Existing DX9 translation ownership is inconsistent: {sharedError}");
            }
            else if (_dgVoodoo is null || _dx9ReShade is null)
            {
                return ComponentOperationResult.Fail("This Luma DX9 package requires dgVoodoo2, but the DX9 translation services are unavailable.");
            }
        }

        if (existingRecord is not null)
        {
            var preflight = await PreflightManagedFilesAsync(existingRecord, cancellationToken).ConfigureAwait(false);
            if (preflight is not null) return ComponentOperationResult.Fail(preflight);
        }

        LumaPackageSelection selection;
        LumaFrameworkReleaseInfo release;
        string archivePath;
        VerifiedDownload? download = null;
        try
        {
            if (localSelection is not null)
            {
                selection = localSelection;
                release = selection.Release;
                archivePath = localPackagePath!;
            }
            else
            {
                selection = namedMatch is not null
                    ? new LumaPackageSelection(
                        await ResolveNamedReleaseAsync(namedMatch, cancellationToken).ConfigureAwait(false),
                        true, namedMatch.Name, namedMatch.Author, namedMatch.SpecialNotes, null)
                    : new LumaPackageSelection(
                        await ResolveLatestAsync(cancellationToken).ConfigureAwait(false),
                        false, "Generic Unreal Engine", null, null, genericMetadata);
                release = selection.Release;
                download = await DownloadAndValidateArchiveAsync(release, cancellationToken).ConfigureAwait(false);
                archivePath = download.Path;
            }
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Could not resolve/download Luma Framework: {ex.Message}");
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"rhi-luma-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        DgVoodooPreparedPayload? preparedDgVoodoo = null;
        var sharedDx9Targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dgVoodooVersion = sharedDx9Record is null ? "" : ReadDgVoodooVersion(sharedDx9Record);
        (LinuxDeploymentRecord Record, LumaEngineIniOverrideState? EngineIni, string[] Changed)? lumaInstalled = null;
        try
        {
            if (requiresDgVoodoo && sharedDx9Record is null)
            {
                var architecture = GameArchitecture.Of(game);
                try
                {
                    var preferred = dgVoodooVersions.FirstOrDefault(pair =>
                        !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));
                    preparedDgVoodoo = !string.IsNullOrWhiteSpace(preferred.Key)
                        ? await _dgVoodoo!.PrepareManifestVersionAsync(preferred.Key, preferred.Value, architecture, cancellationToken).ConfigureAwait(false)
                        : await _dgVoodoo!.PrepareLatestAsync(architecture, cancellationToken).ConfigureAwait(false);
                    dgVoodooVersion = preparedDgVoodoo.Version;
                }
                catch (Exception ex)
                {
                    return ComponentOperationResult.Fail($"Could not prepare dgVoodoo2 for Luma DX9: {ex.Message}");
                }
            }
            else if (requiresDgVoodoo)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var file = sharedDx9Record!.Files.First(item => Path.GetFileName(item.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    sharedDx9Targets.Add(Path.GetFullPath(file.Path));
                }
            }
            IReadOnlyList<LumaPlanItem> plan;
            try { plan = await BuildPackagePlanAsync(archivePath, release.AssetName, game.BinaryDirectory, stagingRoot, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { return ComponentOperationResult.Fail($"Luma package validation failed: {ex.Message}"); }
            if (plan.Count == 0)
                return ComponentOperationResult.Fail("Luma package did not contain any deployable files after RHI exclusions.");

            var oldFiles = (existingRecord?.Files ?? new List<LinuxDeployedFile>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .ToDictionary(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var item in plan)
            {
                var existingPath = FindCaseInsensitiveExistingPath(item.TargetPath);
                if (existingPath is null) continue;
                var full = Path.GetFullPath(existingPath);
                if (!oldFiles.ContainsKey(full))
                    return ComponentOperationResult.Fail($"Foreign Luma protection: package target already exists and is not RHI-owned: {existingPath}");
            }

            var installedTargets = new List<string>();
            var createdDirectories = new HashSet<string>(
                existingRecord?.DirectoriesCreatedByRhi ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var iniPath = RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory)!;
            var iniBefore = await File.ReadAllBytesAsync(iniPath, cancellationToken).ConfigureAwait(false);
            // #213: the scope tracks (and the rollback step writes) the file behind a ReShade.ini linked to a shared file, never
            // the link: a rollback then keeps the link and puts the shared file's bytes back.
            var iniLinkPolicy = ReShadeIniManager.LinkPolicy(iniPath, game.BinaryDirectory, game.InstallPath);
            var previousIniState = existingRecord is not null &&
                existingRecord.Metadata.TryGetValue(LumaIniOverrideManager.MetadataKey, out var previousIniJson)
                    ? LumaIniOverrideManager.Deserialize(previousIniJson)
                    : null;
            var previousEngineIniState = existingRecord is not null &&
                existingRecord.Metadata.TryGetValue(LumaEngineIniOverrideManager.MetadataKey, out var previousEngineIniJson)
                    ? LumaEngineIniOverrideManager.Deserialize(previousEngineIniJson)
                    : null;
            var shouldReconcileEngineIni = selection.IsNamed || genericMetadataLookupCompleted;
            var requestedEngineIni = selection.IsNamed
                ? Array.Empty<LumaEngineIniEntry>()
                : selection.GenericMetadata?.EngineIniKeys ?? Array.Empty<LumaEngineIniEntry>();
            var engineIniSnapshotPath = previousEngineIniState?.IniPath
                ?? (shouldReconcileEngineIni && requestedEngineIni.Count > 0
                    ? LumaEngineIniOverrideManager.ResolveEngineIniPath(game)
                    : null);
            var engineIniBeforeExisted = engineIniSnapshotPath is not null && File.Exists(engineIniSnapshotPath);
            var engineIniBefore = engineIniBeforeExisted
                ? await File.ReadAllBytesAsync(engineIniSnapshotPath!, cancellationToken).ConfigureAwait(false)
                : null;
            // Every Engine.ini the reconcile may write: the one the record names and, with keys to set, the game's current one.
            var engineIniTargets = new[]
                {
                    previousEngineIniState?.IniPath,
                    shouldReconcileEngineIni && requestedEngineIni.Count > 0 ? LumaEngineIniOverrideManager.ResolveEngineIniPath(game) : null
                }
                .OfType<string>().Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();

            // #23/#169: one journaled operation for every game file, ReShade.ini, Engine.ini and the DX9 translation; the
            // Luma record carrying its id (with ReShade's, after a DX9 translation, in the same write) is the commit point.
            var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
                _journal, "Luma Framework install: " + game.Name, game, ManagedComponentKind.LumaFramework, cancellationToken).ConfigureAwait(false);
            if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
            await using var scope = opened!;

            // Downloads, staging and snapshots are done: from the first game file on, the install runs to completion
            // or full rollback (Cancel is refused).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            Dx9MigrationInside? dx9 = null;
            try
            {
                if (requiresDgVoodoo && sharedDx9Record is null)
                {
                    dx9 = await _dx9ReShade!.MigrateInsideAsync(game, scope, cancellationToken).ConfigureAwait(false);
                    dx9Migration = dx9.State;
                    var d3d9Target = Path.Combine(game.BinaryDirectory, "D3D9.dll");
                    var configTarget = Path.Combine(game.BinaryDirectory, "dgVoodoo.conf");
                    if (File.Exists(configTarget))
                        throw new IOException($"External dgVoodoo.conf already exists: {configTarget}. RHI Linux will not overwrite it automatically.");
                    await CopyIntoGameAsync(scope, preparedDgVoodoo!.D3D9Path, d3d9Target).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(d3d9Target));
                    await CopyIntoGameAsync(scope, preparedDgVoodoo.ConfigPath, configTarget).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(configTarget));
                }

                foreach (var item in plan)
                {
                    EnsureParentDirectories(item.TargetPath, game.BinaryDirectory, createdDirectories);
                    var effectiveTarget = FindCaseInsensitiveExistingPath(item.TargetPath) ?? item.TargetPath;
                    // The scope keeps a verified copy of what is there (an older Luma file) before replacing it.
                    await CopyIntoGameAsync(scope, item.StagingPath, effectiveTarget).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(effectiveTarget));
                }

                var iniTarget = AtomicFile.ResolveUserFileTarget(iniPath, iniLinkPolicy);
                await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
                await scope.TrackAllAsync(engineIniTargets, CancellationToken.None).ConfigureAwait(false);
                var iniState = LumaIniOverrideManager.Apply(game.BinaryDirectory, previousIniState);
                // A transient wiki failure must not destructively erase Engine.ini ownership from a
                // previously managed generic install. Reconcile only after a completed generic lookup
                // (including a legitimate no-match) or when switching to a named package.
                var engineIniState = shouldReconcileEngineIni
                    ? LumaEngineIniOverrideManager.Apply(game, requestedEngineIni, previousEngineIniState)
                    : previousEngineIniState;
                if (engineIniState is not null && !engineIniTargets.Contains(Path.GetFullPath(engineIniState.IniPath), StringComparer.Ordinal))
                    throw new InvalidOperationException($"Engine.ini was written at a path the install did not track: {engineIniState.IniPath}");
                foreach (var written in engineIniTargets.Prepend(iniTarget).Where(File.Exists))
                    await scope.DeclareWrittenContentAsync(written, CancellationToken.None).ConfigureAwait(false);

                // Remove old RHI-owned files no longer present in the new package only after replacement + INI succeeded.
                var newTargets = installedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
                newTargets.UnionWith(sharedDx9Targets);
                if (existingRecord is not null)
                {
                    foreach (var old in existingRecord.Files)
                    {
                        if (string.IsNullOrWhiteSpace(old.Path) || !File.Exists(old.Path)) continue;
                        if (newTargets.Contains(Path.GetFullPath(old.Path))) continue;
                        await scope.DeleteAsync(old.Path, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                var files = new List<LinuxDeployedFile>();
                foreach (var target in installedTargets)
                {
                    files.Add(new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = oldFiles.ContainsKey(target),
                        Sha256 = await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false)
                    });
                }
                foreach (var target in sharedDx9Targets)
                {
                    if (files.Any(file => Path.GetFullPath(file.Path).Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                    files.Add(new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false)
                    });
                }

                var record = new LinuxDeploymentRecord
                {
                    GameKey = LinuxDeploymentRecordStore.GameKey(game),
                    GameName = game.Name,
                    Component = ManagedComponentKind.LumaFramework,
                    BinaryDirectory = game.BinaryDirectory,
                    SourceUrl = localPackage ? null : release.AssetUrl,
                    Version = release.Version,
                    InstalledAtUtc = DateTime.UtcNow,
                    Files = files,
                    DirectoriesCreatedByRhi = createdDirectories.OrderByDescending(x => x.Length).ToList(),
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["LumaFramework.Package"] = release.AssetName,
                        [MetaLocalPackage] = localPackage ? "1" : "0",
                        ["LumaFramework.Generic"] = selection.IsNamed ? "0" : "1",
                        ["LumaFramework.CatalogName"] = selection.DisplayName,
                        ["LumaFramework.Author"] = selection.Author ?? "",
                        ["LumaFramework.SpecialNotes"] = selection.SpecialNotes ?? "",
                        ["LumaFramework.EngineHint"] = game.EngineHint ?? "",
                        ["LumaFramework.GenericName"] = selection.GenericMetadata?.Name ?? "",
                        ["LumaFramework.GenericNotes"] = selection.GenericMetadata?.Notes ?? "",
                        ["LumaFramework.LaunchArgs"] = selection.GenericMetadata?.LaunchArgs ?? "",
                        ["LumaFramework.HdrSupported"] = selection.GenericMetadata is null ? "" : (selection.GenericMetadata.HdrSupported ? "1" : "0"),
                        ["LumaFramework.DlssFsrSupported"] = selection.GenericMetadata is null ? "" : (selection.GenericMetadata.DlssFsrSupported ? "1" : "0"),
                        ["LumaFramework.DlssFsrBlocked"] = selection.GenericMetadata is null ? "" : (selection.GenericMetadata.DlssFsrBlocked ? "1" : "0"),
                        ["LumaFramework.UeVersion"] = selection.GenericMetadata?.UeVersion ?? "",
                        ["LumaFramework.EngineIniProjectOverride"] = game.EngineIniProjectOverride ?? "",
                        ["LumaFramework.PolicyAuthority"] = _policy?.GetCachedOrDefault(game).Authority ?? "",
                        ["LumaFramework.ManifestDefault"] = _policy is null ? "" : (_policy.GetCachedOrDefault(game).ManifestDefault ? "1" : "0"),
                        ["LumaFramework.RenoDxCompatible"] = _policy is null ? "" : (_policy.GetCachedOrDefault(game).RenoDxCompatible ? "1" : "0"),
                        ["LumaFramework.UserPreference"] = _policy?.GetCachedOrDefault(game).UserPreference.ToString() ?? "",
                        [MetaDx9Migration] = dx9Migration is null ? "" : JsonSerializer.Serialize(dx9Migration),
                        [MetaDgVoodooVersion] = requiresDgVoodoo ? dgVoodooVersion : "",
                        [LumaIniOverrideManager.MetadataKey] = LumaIniOverrideManager.Serialize(iniState),
                        [LumaEngineIniOverrideManager.MetadataKey] = engineIniState is null ? "" : LumaEngineIniOverrideManager.Serialize(engineIniState)
                    }
                };
                // The downloads this record deploys (#26): the package (none for a local package) and dgVoodoo2.
                download?.RecordIn(record.Metadata);
                preparedDgVoodoo?.Download?.RecordIn(record.Metadata, DgVoodooPreparedPayload.DownloadPart);
                // The commit point (with ReShade's record, after a DX9 translation, in the same write).
                _records.Commit(dx9 is null ? [scope.Stamp(record)] : [scope.Stamp(record), dx9.ReShadeRecord]);
                lumaInstalled = (record, engineIniState, installedTargets.ToArray());
            }
            catch (Exception ex)
            {
                // Compensation never honours cancellation, and each step runs even when an earlier one failed. The user's
                // ReShade.ini and Engine.ini go back first through the one user-file writer (a lock the user set
                // meanwhile is kept, #31); then the scope puts every other file back from its verified copies (the
                // package files, the files it replaced or removed, dgVoodoo2, the DX9 rename and, journaled, its
                // overrides). A rollback that cannot finish keeps every copy and says where.
                void Step(string what, Action action)
                {
                    try
                    {
                        RollbackStepFaultForTesting?.Invoke(what);
                        action();
                    }
                    catch (Exception stepError)
                    {
                        AppLog.Warn("luma-rollback", $"Could not {what} for {game.Name}", stepError);
                    }
                }
                // #204: a commit write (with ReShade's translated record, after a DX9 translation) that landed and then threw
                // is finished as committed — nothing below runs; a ledger that cannot tell keeps everything for the next start.
                var settled = await scope.SettleAfterFailureAsync().ConfigureAwait(false);
                if (settled == CommitDecision.Committed) return DeploymentScope.Landed("Luma Framework", changedFiles: [.. installedTargets]);
                if (settled == CommitDecision.NotCommitted)
                {
                    // #213: written through ReShade.ini's link rule, so a link to a shared file stays a link.
                    Step("restore ReShade.ini", () => AtomicFile.ReplaceUserFile(iniPath, iniBefore, iniLinkPolicy, readOnly: ReadOnlyUserFile.WriteKeepingLock));
                    if (engineIniSnapshotPath is not null && engineIniBeforeExisted && engineIniBefore is not null)
                        Step("restore Engine.ini", () =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(engineIniSnapshotPath)!);
                            AtomicFile.ReplaceUserFile(engineIniSnapshotPath, engineIniBefore, LumaEngineIniOverrideManager.LinkPolicy(engineIniSnapshotPath), readOnly: ReadOnlyUserFile.WriteKeepingLock);
                        });
                }
                var rollback = await scope.RollbackAsync().ConfigureAwait(false);
                if (rollback.RolledBack)
                {
                    if (dx9 is not null) _dx9ReShade!.UndoInside(game, dx9);
                    Step("remove the folders the install created", () => FileCleanup.TryDeleteEmptyDirectories(createdDirectories));
                }
                if (!rollback.Success)
                {
                    AppLog.Warn("luma-rollback", $"Rollback incomplete for {game.Name}: {string.Join("; ", rollback.Failures)}");
                    return ComponentOperationResult.Fail($"{RollbackIncompletePrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
                }
                return ComponentOperationResult.Fail($"Luma Framework installation rolled back: {ex.Message}");
            }
            await scope.CommitAsync().ConfigureAwait(false);
            var (_, installedEngineIni, changed) = lumaInstalled!.Value;
            var packageKind = localPackage
                ? $"local package {selection.DisplayName}"
                : selection.IsNamed ? $"named mod {selection.DisplayName}" : "generic Unreal package";
            var engineIniSummary = installedEngineIni is null
                ? "No RHI-owned Engine.ini keys were required/resolved."
                : $"RHI owns {installedEngineIni.Entries.Count} wiki Engine.ini key(s) at {installedEngineIni.IniPath}.";
            return ComponentOperationResult.Ok(
                $"Luma Framework {release.Version} installed from {release.AssetName} ({packageKind}). Bundled ReShade/DLSS ownership remained with RHI. {engineIniSummary}",
                changed);
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("luma", $"Staging folder could not be removed: {stagingRoot}", ex);
            }
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.LumaFramework);
        if (record is null)
            return ComponentOperationResult.Fail("Luma Framework is not managed by RHI Linux. External files are protected from removal.");

        var preflight = await PreflightManagedFilesAsync(record, cancellationToken).ConfigureAwait(false);
        if (preflight is not null) return ComponentOperationResult.Fail(preflight);

        var dx9State = ReadDx9Migration(record, MetaDx9Migration);
        var feederRecord = dx9State is null ? null : _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        var feederStillUsesDx9 = dx9State is not null && ReadDx9Migration(feederRecord, Dlss5FeederFullDeploymentService.MetaDx9Migration) is not null;
        if (dx9State is not null && !feederStillUsesDx9 && _dx9ReShade is null)
            return ComponentOperationResult.Fail("Luma DX9 removal needs the ReShade migration coordinator, but it is unavailable.");

        // #23/#169: one journaled removal (files, ReShade.ini, Engine.ini, the DX9 translation); taking the record out of
        // the ledger (with ReShade's restored record in the same write) is the commit point.
        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "Luma Framework removal: " + game.Name, game, ManagedComponentKind.LumaFramework, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;

        // Cancel is honoured only up to here: the removal never stops half-way.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var changed = new List<string>();
        LinuxDeploymentRecord? reShadeBefore = null;
        LinuxDeploymentRecord? reShadeAfter = null;
        WineOverrideRestoreResult? dx9OverrideRestore = null;
        try
        {
            var iniState = record.Metadata.TryGetValue(LumaIniOverrideManager.MetadataKey, out var iniJson)
                ? LumaIniOverrideManager.Deserialize(iniJson)
                : null;
            var engineIniState = record.Metadata.TryGetValue(LumaEngineIniOverrideManager.MetadataKey, out var engineIniJson)
                ? LumaEngineIniOverrideManager.Deserialize(engineIniJson)
                : null;
            // #213: the file behind a linked ReShade.ini is tracked, never the link.
            var lumaIni = iniState?.IniPath is { Length: > 0 } recordedIni
                ? ReShadeIniManager.ResolveWriteTarget(recordedIni, Path.GetDirectoryName(recordedIni)!, game.InstallPath)
                : null;
            var iniFiles = new[] { lumaIni, engineIniState?.IniPath }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            await scope.TrackAllAsync(iniFiles, CancellationToken.None).ConfigureAwait(false);
            LumaIniOverrideManager.Cleanup(iniState);
            LumaEngineIniOverrideManager.Cleanup(engineIniState);
            foreach (var written in iniFiles.Where(File.Exists))
                await scope.DeclareWrittenContentAsync(written, CancellationToken.None).ConfigureAwait(false);

            if (dx9State is not null && !feederStillUsesDx9)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var managed = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (managed is null || !File.Exists(managed.Path)) continue;
                    await scope.DeleteAsync(managed.Path, CancellationToken.None).ConfigureAwait(false);
                    changed.Add(managed.Path);
                }
                reShadeBefore = _records.Find(game, ManagedComponentKind.ReShade);
                (reShadeAfter, dx9OverrideRestore) = await _dx9ReShade!.RestoreInsideAsync(game, dx9State, scope, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files)
            {
                if (dx9State is not null && (Path.GetFileName(file.Path).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(file.Path).Equals("dgVoodoo.conf", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path)) continue;
                await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
                changed.Add(file.Path);
            }
            _records.Commit(reShadeAfter is null ? [] : [reShadeAfter], [(game, ManagedComponentKind.LumaFramework)]); // the commit point
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "luma", alongside: reShadeBefore is null ? null : [reShadeBefore]).ConfigureAwait(false);
            // #204: the removal (with ReShade's record back at d3d9.dll) landed and then threw: it stays removed.
            if (rollback.Committed) return DeploymentScope.Landed("Luma Framework", removal: true, [.. changed]);
            if (rollback.RolledBack && dx9OverrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored && reShadeBefore?.WineOverride is { } translated && game.PrefixPath is { } prefix)
                _dx9ReShade!.Reapply(prefix, translated);
            return ComponentOperationResult.Fail($"Luma Framework removal failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        // Past the commit point: emptied folders RHI created go (best effort; never rolled back).
        FileCleanup.TryDeleteEmptyDirectories(record.DirectoriesCreatedByRhi);
        return ComponentOperationResult.Ok("Luma Framework removed. ReShade, DLSS and user/runtime data were preserved; [Luma] and RHI-owned wiki Engine.ini settings were restored.", changed.ToArray());
    }

    internal static bool RequiresDx9RemovalBeforeUpdate(LinuxDeploymentRecord? existingRecord, bool newRequiresDgVoodoo)
        => existingRecord is not null
           && !newRequiresDgVoodoo
           && existingRecord.Metadata.TryGetValue(MetaDx9Migration, out var state)
           && !string.IsNullOrWhiteSpace(state);

    internal static async Task<IReadOnlyList<LumaPlanItem>> BuildExtractionPlanAsync(
        string archivePath, string binaryDirectory, string stagingRoot, CancellationToken cancellationToken)
    {
        var addonDirectory = RenoDxDeploymentService.ResolveAddonDeployPath(binaryDirectory);
        var result = new List<LumaPlanItem>();
        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        var index = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var normalized = NormalizeArchivePath(entry.FullName);
            var fileName = Path.GetFileName(normalized);
            if (PackageOwnedFilesSkippedByRhi.Contains(fileName)) continue;

            string target;
            if (fileName.EndsWith(".addon", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(addonDirectory, fileName);
            }
            else
            {
                var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
                target = Path.GetFullPath(Path.Combine(binaryDirectory, relative));
                var root = Path.GetFullPath(binaryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidDataException($"Archive entry escapes the game directory: {entry.FullName}");
            }

            var fullTarget = Path.GetFullPath(target);
            if (!targetKeys.Add(fullTarget))
                throw new InvalidDataException($"Package maps multiple entries to the same target: {fullTarget}");

            var staging = Path.Combine(stagingRoot, "payload", $"{index++:D4}-{SanitizeFileName(fileName)}");
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            if (File.Exists(staging)) throw new IOException($"Staging file already exists: {staging}");
            // Checked extraction (declared-size cap, fixed mode, length + CRC-32, atomic rename); never a raw copy (#42).
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, staging, DownloadSources.PayloadArchiveMaxBytes, cancellationToken).ConfigureAwait(false);
            result.Add(new LumaPlanItem(staging, fullTarget, normalized));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<LumaPlanItem>> BuildPackagePlanAsync(
        string packagePath,
        string assetName,
        string binaryDirectory,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (!IsAddonAssetName(assetName))
            return await BuildExtractionPlanAsync(packagePath, binaryDirectory, stagingRoot, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Luma package is missing.", packagePath);
        if (!BinarySignatureInspector.HasPeSignature(packagePath, MinimumArchiveSize))
            throw new InvalidDataException("Direct Luma add-on failed the local PE/minimum-size validation.");

        var addonDirectory = RenoDxDeploymentService.ResolveAddonDeployPath(binaryDirectory);
        var safeName = Path.GetFileName(assetName);
        if (string.IsNullOrWhiteSpace(safeName) || !IsAddonAssetName(safeName))
            throw new InvalidDataException($"Unsupported direct Luma add-on name: {assetName}");

        var staging = Path.Combine(stagingRoot, "payload", $"0000-{SanitizeFileName(safeName)}");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        AtomicFile.Copy(packagePath, staging);
        return [new LumaPlanItem(staging, Path.Combine(addonDirectory, safeName), safeName)];
    }

    private async Task<LumaFrameworkReleaseInfo> ResolveNamedReleaseAsync(
        LumaCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            throw new InvalidDataException($"Named Luma match '{entry.Name}' has no download URL.");
        if (!IsTrustedFiloppiUrl(entry.DownloadUrl))
            throw new InvalidDataException("Named Luma package URL is outside Filoppi's GitHub namespace.");

        var uri = new Uri(entry.DownloadUrl);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 6 && segments[0].Equals("Filoppi", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase))
        {
            var repo = segments[1];
            string? api = null;
            string? asset = null;
            if (segments[3].Equals("latest", StringComparison.OrdinalIgnoreCase)
                && segments[4].Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                asset = Uri.UnescapeDataString(segments[^1]);
                api = $"https://api.github.com/repos/Filoppi/{repo}/releases/latest";
            }
            else if (segments[3].Equals("download", StringComparison.OrdinalIgnoreCase) && segments.Length >= 6)
            {
                var tag = Uri.UnescapeDataString(segments[4]);
                asset = Uri.UnescapeDataString(segments[^1]);
                api = $"https://api.github.com/repos/Filoppi/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
            }

            if (!string.IsNullOrWhiteSpace(api) && !string.IsNullOrWhiteSpace(asset))
            {
                var resolved = await _resolver.ResolveAsync(
                    new GitHubReleaseAssetQuery(api, ExactAssetName: asset),
                    cancellationToken).ConfigureAwait(false);
                return new LumaFrameworkReleaseInfo(
                    resolved.Version, resolved.AssetUrl, resolved.AssetName, resolved.Sha256);
            }
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName) ||
            (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !IsAddonAssetName(fileName)))
            throw new InvalidDataException("Named Luma package must be a ZIP or ReShade .addon/.addon32/.addon64 download from Filoppi's GitHub namespace.");
        return new LumaFrameworkReleaseInfo("wiki", entry.DownloadUrl, fileName, null);
    }

    private static bool IsAddonAssetName(string value)
        => value.EndsWith(".addon", StringComparison.OrdinalIgnoreCase)
           || value.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
           || value.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTrustedFiloppiUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/Filoppi/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The release package from the stamped download cache under the Luma host policy, verified against GitHub's
    /// digest when published (#26, #42). A catalogue ("wiki") link has no version identity, so it is fetched again
    /// every time, as before; versioned release assets are reused while their stamp holds.
    /// </summary>
    private async Task<VerifiedDownload> DownloadAndValidateArchiveAsync(LumaFrameworkReleaseInfo release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(release.Version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var cache = Path.Combine(_cacheDir, $"{safeVersion}-{Path.GetFileName(release.AssetName)}");
        if (release.Version.Equals("wiki", StringComparison.Ordinal)) CacheStamp.Invalidate(cache);

        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, release.AssetUrl, cache, DownloadSources.Luma, release.Version, release.Sha256,
            validate: (temp, ct) =>
            {
                if (new FileInfo(temp).Length < MinimumArchiveSize)
                    throw new InvalidDataException("Downloaded Luma package is unexpectedly small.");
                if (IsAddonAssetName(release.AssetName))
                {
                    if (!BinarySignatureInspector.HasPeSignature(temp, MinimumArchiveSize))
                        throw new InvalidDataException("Downloaded direct Luma add-on failed PE/minimum-size validation.");
                }
                else
                {
                    return ReShadeArchiveExtractor.RequireReadableZip(temp, ct);
                }
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return download;
    }

    private static ReShadeDx9MigrationState? ReadDx9Migration(LinuxDeploymentRecord? record, string key)
    {
        if (record is null || !record.Metadata.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReShadeDx9MigrationState>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("luma", "Stored deployment state could not be read; it is treated as absent", ex);
            return null;
        }
    }

    private static string ReadDgVoodooVersion(LinuxDeploymentRecord record)
    {
        if (record.Component == ManagedComponentKind.Dlss5Feeder)
            return record.Metadata.GetValueOrDefault(Dlss5FeederFullDeploymentService.MetaDgVoodooVersion) ?? "";
        return record.Metadata.GetValueOrDefault(MetaDgVoodooVersion) ?? "";
    }

    private static async Task<string?> ValidateSharedDx9TranslationAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
        {
            var file = record.Files.FirstOrDefault(item => Path.GetFileName(item.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file is null) return $"{name} is not tracked by the component that owns the DX9 translation.";
            if (!File.Exists(file.Path)) return $"{name} is missing at {file.Path}.";
            if (string.IsNullOrWhiteSpace(file.Sha256)) return $"{name} has no ownership hash.";
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"{name} no longer matches its managed hash.";
        }
        return null;
    }

    private static async Task<string?> PreflightManagedFilesAsync(LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return "Luma Framework ownership hash is missing; update/remove is blocked conservatively.";
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"RHI-managed Luma Framework file was modified after installation. Operation is blocked to preserve it: {file.Path}";
        }
        return null;
    }

    private static string NormalizeArchivePath(string path)
    {
        var windowsDriveAbsolute = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
        if (path.StartsWith('/') || path.StartsWith("\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || windowsDriveAbsolute || Path.IsPathRooted(path))
            throw new InvalidDataException($"Absolute archive entry is not allowed: {path}");

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (segments.Length == 0 || segments.Any(segment => segment == ".."))
            throw new InvalidDataException($"Unsafe archive entry: {path}");
        return string.Join('/', segments);
    }

    private static string? FindCaseInsensitiveExistingPath(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return null;
        var name = Path.GetFileName(target);
        return Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureParentDirectories(string target, string binaryDirectory, HashSet<string> created)
    {
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(parent) || Directory.Exists(parent)) return;
        var missing = new Stack<string>();
        var current = parent;
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir);
            // Never claim ownership of the game binary directory itself.
            if (!Path.GetFullPath(dir).Equals(Path.GetFullPath(binaryDirectory), StringComparison.OrdinalIgnoreCase))
                created.Add(Path.GetFullPath(dir));
        }
    }

    /// <summary>Fixed start of an install failure whose rollback did not complete (PT-BR pair); the error follows.</summary>
    public const string RollbackIncompletePrefix = "Luma Framework installation failed and its rollback did not complete (details in rhi.log): ";

    /// <summary>
    /// Verified durable copy into the game folder through <paramref name="scope"/> (what is there is tracked first, the
    /// new content declared); replaces the target.
    /// </summary>
    private static async Task CopyIntoGameAsync(DeploymentScope scope, string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Luma target directory is invalid."));
        await scope.PlaceCopyAsync(source, target, CancellationToken.None).ConfigureAwait(false);
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}

public sealed record LumaPlanItem(string StagingPath, string TargetPath, string ArchivePath);
