using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    public RenoDxUpgradeDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        UpstreamRhiManifestService manifest,
        string? cacheDir = null)
    {
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
            var currentHash = await Sha256Async(owned.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed RenoDX Upgrade file was modified outside RHI and will not be replaced: {owned.Path}");
        }

        try
        {
            var cached = await DownloadAndValidateAsync(sourceUrl, architecture, cancellationToken).ConfigureAwait(false);
            // Downloaded: from the copy on, Cancel is refused (a late cancel used to leave the add-on with no record).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await CopyAtomicAsync(cached, target, cancellationToken).ConfigureAwait(false);
            var targetHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);

            if (owned is not null && !PathsEqual(owned.Path, target) && File.Exists(owned.Path))
                File.Delete(owned.Path);

            _records.Upsert(new LinuxDeploymentRecord
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
            });

            return ComponentOperationResult.Ok($"RenoDX Upgrade snapshot installed → {target}", target);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"RenoDX Upgrade install failed: {ex.Message}");
        }
    }

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
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
        }

        // Cancel is honoured only up to here: the removal never stops half-way.
        _ = OperationCancellation.BeginWrites(cancellationToken);
        var changed = new List<string>();
        foreach (var file in record.Files)
        {
            if (!File.Exists(file.Path)) continue;
            File.Delete(file.Path);
            changed.Add(file.Path);
        }

        _records.Remove(game, ManagedComponentKind.RenoDXUpgrade);
        return ComponentOperationResult.Ok("RHI-managed RenoDX Upgrade removed. User/external files were left untouched.", changed.ToArray());
    }

    public static PeArchitecture ResolveArchitecture(DetectedGame game)
    {
        if (game.ForcedArchitecture is PeArchitecture.X86 or PeArchitecture.X64)
            return game.ForcedArchitecture.Value;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return PeArchitecture.Unknown;
        return PeArchitectureDetector.Detect(game.ExecutablePath);
    }

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

    private static async Task CopyAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("RenoDX Upgrade target directory is invalid."));
        var temp = target + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(source).Length != new FileInfo(temp).Length)
                throw new IOException("RenoDX Upgrade copy verification failed.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
