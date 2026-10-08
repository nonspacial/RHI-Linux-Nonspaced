using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly string _cacheDir;

    public LumaFrameworkDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        string? cacheDir = null,
        LumaCatalogService? catalog = null,
        LumaPolicyService? policy = null,
        UpstreamRhiManifestService? manifest = null,
        DgVoodooDeploymentService? dgVoodoo = null,
        Dx9TranslationReShadeCoordinator? dx9ReShade = null)
    {
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
                record.Version);
        try
        {
            var selection = await ResolvePackageAsync(game, cancellationToken).ConfigureAwait(false);
            if (selection is null)
                return new ManagedUpdateCheck(true, false, "No current trusted Luma package matches this game; installed payload was preserved.", record.Version);
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
        if (game.ForcedArchitecture == PeArchitecture.X86) return false;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) return false;
        if (PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64) return false;
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
        if (game.ForcedArchitecture == PeArchitecture.X86)
            return "Generic Luma is currently enabled for 64-bit Windows games.";
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath) ||
            PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
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
        var detected = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
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
        var detected = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
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
            var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
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

            var hash = await Sha256Async(fullLocalPath, cancellationToken).ConfigureAwait(false);
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
                archivePath = await DownloadAndValidateArchiveAsync(release, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Could not resolve/download Luma Framework: {ex.Message}");
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"rhi-luma-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        DgVoodooPreparedPayload? preparedDgVoodoo = null;
        var dx9TranslationCreatedThisAttempt = false;
        var sharedDx9Targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dgVoodooVersion = sharedDx9Record is null ? "" : ReadDgVoodooVersion(sharedDx9Record);
        try
        {
            if (requiresDgVoodoo && sharedDx9Record is null)
            {
                var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath!);
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
            try { plan = BuildPackagePlan(archivePath, release.AssetName, game.BinaryDirectory, stagingRoot); }
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

            var backupRoot = Path.Combine(stagingRoot, "rollback");
            Directory.CreateDirectory(backupRoot);
            var installedTargets = new List<string>();
            var overwrittenBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allOldBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var createdDirectories = new HashSet<string>(
                existingRecord?.DirectoriesCreatedByRhi ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (existingRecord is not null)
            {
                foreach (var old in existingRecord.Files)
                {
                    if (string.IsNullOrWhiteSpace(old.Path) || !File.Exists(old.Path)) continue;
                    var full = Path.GetFullPath(old.Path);
                    var backup = Path.Combine(backupRoot, $"old-{allOldBackups.Count:D4}.bak");
                    File.Copy(old.Path, backup, overwrite: false);
                    allOldBackups[full] = backup;
                }
            }
            var iniPath = RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory)!;
            var iniBefore = await File.ReadAllBytesAsync(iniPath, cancellationToken).ConfigureAwait(false);
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

            try
            {
                if (requiresDgVoodoo && sharedDx9Record is null)
                {
                    dx9Migration = await _dx9ReShade!.MigrateAsync(game, cancellationToken).ConfigureAwait(false);
                    dx9TranslationCreatedThisAttempt = true;
                    var d3d9Target = Path.Combine(game.BinaryDirectory, "D3D9.dll");
                    var configTarget = Path.Combine(game.BinaryDirectory, "dgVoodoo.conf");
                    if (File.Exists(configTarget))
                        throw new IOException($"External dgVoodoo.conf already exists: {configTarget}. RHI Linux will not overwrite it automatically.");
                    await CopyAtomicAsync(preparedDgVoodoo!.D3D9Path, d3d9Target, cancellationToken).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(d3d9Target));
                    await CopyAtomicAsync(preparedDgVoodoo.ConfigPath, configTarget, cancellationToken).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(configTarget));
                }

                foreach (var item in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureParentDirectories(item.TargetPath, game.BinaryDirectory, createdDirectories);
                    var existingPath = FindCaseInsensitiveExistingPath(item.TargetPath);
                    var effectiveTarget = existingPath ?? item.TargetPath;
                    if (existingPath is not null)
                    {
                        var fullExisting = Path.GetFullPath(existingPath);
                        if (allOldBackups.TryGetValue(fullExisting, out var oldBackup))
                        {
                            overwrittenBackups[fullExisting] = oldBackup;
                        }
                        else
                        {
                            var backup = Path.Combine(backupRoot, $"target-{overwrittenBackups.Count:D4}.bak");
                            File.Copy(existingPath, backup, overwrite: false);
                            overwrittenBackups[fullExisting] = backup;
                        }
                    }
                    await CopyAtomicAsync(item.StagingPath, effectiveTarget, cancellationToken).ConfigureAwait(false);
                    installedTargets.Add(Path.GetFullPath(effectiveTarget));
                }

                var iniState = LumaIniOverrideManager.Apply(game.BinaryDirectory, previousIniState);
                // A transient wiki failure must not destructively erase Engine.ini ownership from a
                // previously managed generic install. Reconcile only after a completed generic lookup
                // (including a legitimate no-match) or when switching to a named package.
                var engineIniState = shouldReconcileEngineIni
                    ? LumaEngineIniOverrideManager.Apply(game, requestedEngineIni, previousEngineIniState)
                    : previousEngineIniState;

                // Remove old RHI-owned files no longer present in the new package only after replacement + INI succeeded.
                var newTargets = installedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
                newTargets.UnionWith(sharedDx9Targets);
                if (existingRecord is not null)
                {
                    foreach (var old in existingRecord.Files)
                    {
                        if (string.IsNullOrWhiteSpace(old.Path) || !File.Exists(old.Path)) continue;
                        var full = Path.GetFullPath(old.Path);
                        if (newTargets.Contains(full)) continue;
                        File.Delete(old.Path);
                    }
                }

                var files = new List<LinuxDeployedFile>();
                foreach (var target in installedTargets)
                {
                    files.Add(new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = oldFiles.ContainsKey(target),
                        Sha256 = await Sha256Async(target, cancellationToken).ConfigureAwait(false)
                    });
                }
                foreach (var target in sharedDx9Targets)
                {
                    if (files.Any(file => Path.GetFullPath(file.Path).Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                    files.Add(new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = await Sha256Async(target, cancellationToken).ConfigureAwait(false)
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
                _records.Upsert(record);
                var packageKind = localPackage
                    ? $"local package {selection.DisplayName}"
                    : selection.IsNamed ? $"named mod {selection.DisplayName}" : "generic Unreal package";
                var engineIniSummary = engineIniState is null
                    ? "No RHI-owned Engine.ini keys were required/resolved."
                    : $"RHI owns {engineIniState.Entries.Count} wiki Engine.ini key(s) at {engineIniState.IniPath}.";
                return ComponentOperationResult.Ok(
                    $"Luma Framework {release.Version} installed from {release.AssetName} ({packageKind}). Bundled ReShade/DLSS ownership remained with RHI. {engineIniSummary}",
                    installedTargets.ToArray());
            }
            catch (Exception ex)
            {
                try
                {
                    foreach (var target in installedTargets.AsEnumerable().Reverse())
                    {
                        if (overwrittenBackups.TryGetValue(target, out var backup))
                            File.Copy(backup, target, overwrite: true);
                        else if (File.Exists(target))
                            File.Delete(target);
                    }
                    foreach (var pair in allOldBackups)
                    {
                        if (!File.Exists(pair.Key))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                            File.Copy(pair.Value, pair.Key, overwrite: false);
                        }
                    }
                    await File.WriteAllBytesAsync(iniPath, iniBefore, cancellationToken).ConfigureAwait(false);
                    if (engineIniSnapshotPath is not null)
                    {
                        if (engineIniBeforeExisted && engineIniBefore is not null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(engineIniSnapshotPath)!);
                            await File.WriteAllBytesAsync(engineIniSnapshotPath, engineIniBefore, cancellationToken).ConfigureAwait(false);
                        }
                        else if (File.Exists(engineIniSnapshotPath))
                        {
                            File.Delete(engineIniSnapshotPath);
                        }
                    }
                    if (dx9TranslationCreatedThisAttempt && dx9Migration is not null && _dx9ReShade is not null)
                    {
                        try { await _dx9ReShade.RestoreAsync(game, dx9Migration, cancellationToken).ConfigureAwait(false); } catch { }
                    }
                    CleanupEmptyDirectories(createdDirectories);
                }
                catch { }
                return ComponentOperationResult.Fail($"Luma Framework installation rolled back: {ex.Message}");
            }
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); } catch { }
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

        try
        {
            var iniState = record.Metadata.TryGetValue(LumaIniOverrideManager.MetadataKey, out var iniJson)
                ? LumaIniOverrideManager.Deserialize(iniJson)
                : null;
            var engineIniState = record.Metadata.TryGetValue(LumaEngineIniOverrideManager.MetadataKey, out var engineIniJson)
                ? LumaEngineIniOverrideManager.Deserialize(engineIniJson)
                : null;
            LumaIniOverrideManager.Cleanup(iniState);
            LumaEngineIniOverrideManager.Cleanup(engineIniState);

            var changed = new List<string>();
            if (dx9State is not null && !feederStillUsesDx9)
            {
                foreach (var name in new[] { "D3D9.dll", "dgVoodoo.conf" })
                {
                    var managed = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (managed is null || !File.Exists(managed.Path)) continue;
                    File.Delete(managed.Path);
                    changed.Add(managed.Path);
                }
                await _dx9ReShade!.RestoreAsync(game, dx9State, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dx9State is not null && (Path.GetFileName(file.Path).Equals("D3D9.dll", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(file.Path).Equals("dgVoodoo.conf", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path)) continue;
                File.Delete(file.Path);
                changed.Add(file.Path);
            }
            CleanupEmptyDirectories(record.DirectoriesCreatedByRhi);
            _records.Remove(game, ManagedComponentKind.LumaFramework);
            return ComponentOperationResult.Ok("Luma Framework removed. ReShade, DLSS and user/runtime data were preserved; [Luma] and RHI-owned wiki Engine.ini settings were restored.", changed.ToArray());
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Luma Framework removal failed: {ex.Message}");
        }
    }

    internal static bool RequiresDx9RemovalBeforeUpdate(LinuxDeploymentRecord? existingRecord, bool newRequiresDgVoodoo)
        => existingRecord is not null
           && !newRequiresDgVoodoo
           && existingRecord.Metadata.TryGetValue(MetaDx9Migration, out var state)
           && !string.IsNullOrWhiteSpace(state);

    internal static IReadOnlyList<LumaPlanItem> BuildExtractionPlan(string archivePath, string binaryDirectory, string stagingRoot)
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
            using (var input = entry.Open())
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            result.Add(new LumaPlanItem(staging, fullTarget, normalized));
        }
        return result;
    }

    internal static IReadOnlyList<LumaPlanItem> BuildPackagePlan(
        string packagePath,
        string assetName,
        string binaryDirectory,
        string stagingRoot)
    {
        if (!IsAddonAssetName(assetName))
            return BuildExtractionPlan(packagePath, binaryDirectory, stagingRoot);

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
        File.Copy(packagePath, staging, overwrite: true);
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

    private async Task<string> DownloadAndValidateArchiveAsync(LumaFrameworkReleaseInfo release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(release.Version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var cache = Path.Combine(_cacheDir, $"{safeVersion}-{release.AssetName}");
        var temp = cache + $".tmp-{Guid.NewGuid():N}";

        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

        try
        {
            if (new FileInfo(temp).Length < MinimumArchiveSize)
                throw new InvalidDataException("Downloaded Luma package is unexpectedly small.");
            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = await Sha256Async(temp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded Luma package failed the SHA-256 digest published by GitHub.");
            }
            if (IsAddonAssetName(release.AssetName))
            {
                if (!BinarySignatureInspector.HasPeSignature(temp, MinimumArchiveSize))
                    throw new InvalidDataException("Downloaded direct Luma add-on failed PE/minimum-size validation.");
            }
            else
            {
                using var archive = ZipFile.OpenRead(temp);
                _ = archive.Entries.Count;
            }
            File.Move(temp, cache, overwrite: true);
            return cache;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static ReShadeDx9MigrationState? ReadDx9Migration(LinuxDeploymentRecord? record, string key)
    {
        if (record is null || !record.Metadata.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReShadeDx9MigrationState>(json); }
        catch { return null; }
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
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
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
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
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

    private static async Task CopyAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Luma target directory is invalid.");
        Directory.CreateDirectory(parent);
        var temp = target + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(source).Length != new FileInfo(temp).Length)
                throw new IOException("Luma copy verification failed.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void CleanupEmptyDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}

public sealed record LumaPlanItem(string StagingPath, string TargetPath, string ArchivePath);
