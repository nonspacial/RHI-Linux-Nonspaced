using System.Net.Http.Headers;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux deployment backend for upstream RHI's RenoDX Upgrade add-on.
/// The upstream manifest owns the 32/64-bit snapshot URLs; Linux adds strict
/// ReShade dependency, architecture validation, foreign-file protection and
/// hash-guarded ownership/removal.
/// </summary>
public sealed class RenoDxUpgradeDeploymentService
{
    public const string SectionId = "renodx-upgrade";
    public const string AddonFileName32 = "renodx-upgrade.addon32";
    public const string AddonFileName64 = "renodx-upgrade.addon64";
    private const long MinimumPayloadSize = 64 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;

    public RenoDxUpgradeDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        UpstreamRhiManifestService manifest,
        string? cacheDir = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _manifest = manifest;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "renodx-upgrade");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.RenoDXUpgrade);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var preflight = ValidateTarget(game);
        if (preflight is not null) return preflight;

        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail("Could not determine game bitness for RenoDX Upgrade.");

        UpstreamAddonPackDefinition pack;
        try
        {
            var packs = await _manifest.FetchAddonPacksAsync(cancellationToken).ConfigureAwait(false);
            if (!packs.TryGetValue(SectionId, out var resolved))
                return ComponentOperationResult.Fail($"Upstream RHI manifest does not contain addonPacks/{SectionId}.");
            if (resolved.Disabled)
                return ComponentOperationResult.Fail("RenoDX Upgrade is disabled by the upstream RHI manifest.");
            pack = resolved;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"Could not load the upstream RenoDX Upgrade manifest entry: {ex.Message}");
        }

        var sourceUrl = architecture == PeArchitecture.X86 ? pack.DownloadUrl32 : pack.DownloadUrl64;
        sourceUrl ??= pack.DownloadUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return ComponentOperationResult.Fail($"Upstream RHI manifest has no {ArchitectureLabel(architecture)} RenoDX Upgrade download URL.");
        if (!TrustedDownloadPolicy.RenoDxAddons.IsTrusted(sourceUrl))
            return ComponentOperationResult.Fail(new UntrustedDownloadException(sourceUrl, TrustedDownloadPolicy.RenoDxAddons).Message);

        var deployDir = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!);
        Directory.CreateDirectory(deployDir);
        var deployFileName = architecture == PeArchitecture.X86 ? AddonFileName32 : AddonFileName64;
        var target = Path.Combine(deployDir, deployFileName);
        var record = GetRecord(game);
        var owned = record?.Files.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Path));

        foreach (var candidate in FindUpgradeAddonFiles(deployDir))
        {
            if (owned is not null && PathsEqual(candidate, owned.Path))
                continue;
            return ComponentOperationResult.Fail($"External RenoDX Upgrade installation detected at {candidate}. RHI Linux will not overwrite, remove or stack it automatically.");
        }

        if (File.Exists(target) && owned is null)
            return ComponentOperationResult.Fail($"External RenoDX Upgrade installation detected at {target}. RHI Linux will not overwrite it.");

        if (owned is not null && File.Exists(owned.Path))
        {
            if (string.IsNullOrWhiteSpace(owned.Sha256))
                return ComponentOperationResult.Fail("Existing RHI RenoDX Upgrade record has no ownership hash. Update is blocked conservatively.");
            var currentHash = await FileHash.Sha256HexAsync(owned.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed RenoDX Upgrade file was modified outside RHI and will not be replaced: {owned.Path}");
        }

        string cached;
        try
        {
            cached = await DownloadAndValidateAsync(sourceUrl, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"RenoDX Upgrade install failed: {ex.Message}");
        }

        // Journaled (#23): the add-on (and a previous build under the other bitness' name) is tracked before it changes;
        // the record carrying the operation id is the commit point. A failure after the copy no longer leaves the add-on
        // without a record (#31).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RenoDX Upgrade install: " + game.Name, game, ManagedComponentKind.RenoDXUpgrade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            // Downloaded: from the copy on, Cancel is refused (a late cancel used to leave the add-on with no record).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("RenoDX Upgrade target directory is invalid."));
            // The verified copy returns the hash of what it placed: no second read of the target.
            var targetHash = await scope.PlaceCopyAsync(cached, target, cancellationToken).ConfigureAwait(false);

            if (owned is not null && !PathsEqual(owned.Path, target) && File.Exists(owned.Path))
                await scope.DeleteAsync(owned.Path, cancellationToken).ConfigureAwait(false);

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.RenoDXUpgrade,
                BinaryDirectory = game.BinaryDirectory!,
                SourceUrl = sourceUrl,
                Version = "snapshot",
                InstalledAtUtc = DateTime.UtcNow,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = targetHash,
                        AllowUserChanges = false
                    }
                ],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RenoDxUpgrade.SectionId"] = SectionId,
                    ["RenoDxUpgrade.Architecture"] = ArchitectureLabel(architecture),
                    ["RenoDxUpgrade.Source"] = sourceUrl
                }
            }));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX Upgrade", false, target); // #204
            return ComponentOperationResult.Fail($"RenoDX Upgrade install failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"RenoDX Upgrade snapshot installed → {target}", target);
    }

    /// <summary>Fixed text of a failed, rolled-back removal (PT-BR row under the journal anchor); the reason follows.</summary>
    public const string RemovalRolledBackPrefix = "RenoDX Upgrade removal failed and was rolled back: ";

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RenoDX Upgrade is not managed by RHI Linux. External files are protected from removal.");

        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail("RenoDX Upgrade ownership hash is missing; removal is blocked conservatively.");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
        }

        // Journaled (#23): a crash before the record is gone puts the add-on back as the record still describes it.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "RenoDX Upgrade removal: " + game.Name, game, ManagedComponentKind.RenoDXUpgrade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Cancel is honoured only up to here: the removal never stops half-way.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var changed = new List<string>();
        try
        {
            foreach (var file in record.Files)
            {
                if (!File.Exists(file.Path)) continue;
                await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                changed.Add(file.Path);
            }
            _records.Remove(game, ManagedComponentKind.RenoDXUpgrade);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "renodx-upgrade").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX Upgrade", true, changed.ToArray()); // #204
            return ComponentOperationResult.Fail($"{RemovalRolledBackPrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok("RHI-managed RenoDX Upgrade removed. User/external files were left untouched.", changed.ToArray());
    }

    public static PeArchitecture ResolveArchitecture(DetectedGame game) => GameArchitecture.Of(game);

    internal static IReadOnlyList<string> FindUpgradeAddonFiles(string deployDir)
    {
        if (!Directory.Exists(deployDir)) return [];
        return Directory.EnumerateFiles(deployDir, "*.addon*", SearchOption.TopDirectoryOnly)
            .Where(path => IsUpgradeAddonFileName(Path.GetFileName(path)))
            .ToArray();
    }

    public static bool IsUpgradeAddonFileName(string fileName)
        => fileName.Equals(AddonFileName32, StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals(AddonFileName64, StringComparison.OrdinalIgnoreCase) ||
           (fileName.Contains("renodx", StringComparison.OrdinalIgnoreCase) &&
            fileName.Contains("upgrade", StringComparison.OrdinalIgnoreCase) &&
            (fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)));

    private ComponentOperationResult? ValidateTarget(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RenoDX Upgrade is a Windows ReShade add-on and is not deployed to native Linux games.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (!GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install ReShade with full add-on support before RenoDX Upgrade.");
        return null;
    }

    private async Task<string> DownloadAndValidateAsync(
        string sourceUrl,
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var fileName = architecture == PeArchitecture.X86 ? AddonFileName32 : AddonFileName64;
        var cache = Path.Combine(_cacheDir, fileName);
        // HTTPS on the RenoDX add-on hosts (request and redirects), size cap, verified temp (TrustedDownload).
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, sourceUrl, cache, TrustedDownloadPolicy.RenoDxAddons,
            userAgent: ProductInfoHeaderValue.Parse(RHI.Core.RhiProductInfo.UserAgent),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!BinarySignatureInspector.HasPeSignature(download.Path, MinimumPayloadSize))
            throw new InvalidDataException("Downloaded RenoDX Upgrade add-on is not a valid PE payload or is unexpectedly small.");

        var payloadArchitecture = PeArchitectureDetector.Detect(download.Path);
        if (payloadArchitecture != architecture)
            throw new InvalidDataException($"Downloaded RenoDX Upgrade payload has the wrong architecture (expected {ArchitectureLabel(architecture)}): {ArchitectureLabel(payloadArchitecture)}");

        download.MoveTo(cache);
        return cache;
    }

    private static string ArchitectureLabel(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X86 => "32-bit",
        PeArchitecture.X64 => "64-bit",
        PeArchitecture.Arm64 => "ARM64",
        _ => "unknown architecture"
    };

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);

}
