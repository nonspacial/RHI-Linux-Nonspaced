using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// DLSS NR Cost Scaler (#96; upstream <c>DlssNrCostScalerService</c>, xenmods/DLSSNR-Cost-Scaler): a proxy
/// <c>nvngx_dlssnr.dll</c> that runs the Neural Rendering model at a reduced scale (75 % by default, in its
/// <c>nvngx_dlssnr.ini</c>) and loads the real runtime from <c>nvngx_dlssnr_real.dll</c>; plus its
/// <c>dlssnr-companion.addon64</c> ReShade add-on. Upstream's contract: installed after a Neural Rendering method put
/// the runtime in place (rename it to <c>_real</c>, add proxy, INI, add-on), removed before that method uninstalls
/// (proxy out, <c>_real</c> back, INI and add-on out).
/// <para>Linux follows the one-owner <c>nvngx_dlssnr.dll</c> rule (sync #157): the scaler is a <b>layer</b>, never an
/// owner. It needs an owner (ShortFuse, DLSS5 Feeder, OptiScaler DLSS NR), refuses when the owner's file no longer
/// matches the owner's record, keeps the moved runtime under its own record, and while it is installed the owner's
/// update, NR version swap, reinstall and removal are refused (<see cref="OwnerChangeRefusal"/>); removing it puts the
/// owner's file back byte for byte. Install and removal are journaled (every file tracked, writes declared,
/// <see cref="OperationCancellation.BeginWrites"/> before the first change, download before it) and roll back as a whole.</para>
/// </summary>
public sealed class NrCostScalerService
{
    public const string ReleaseApiUrl = "https://api.github.com/repos/xenmods/DLSSNR-Cost-Scaler/releases/latest";
    public const string ProxyDllName = "nvngx_dlssnr.dll";
    public const string RealDllName = "nvngx_dlssnr_real.dll";
    public const string IniFileName = "nvngx_dlssnr.ini";
    public const string CompanionAddonName = "dlssnr-companion.addon64";
    public const string DisplayName = "NR Cost Scaler";

    // Fixed messages, values last (PT-BR presentation pairs).
    public const string NoOwnerMessage = "NR Cost Scaler needs a Neural Rendering method installed by RHI first (ShortFuse, DLSS5 Feeder or OptiScaler DLSS NR).";
    public const string OwnerModifiedPrefix = "NR Cost Scaler not installed: nvngx_dlssnr.dll no longer matches what RHI installed for ";
    public const string AlreadyInstalledMessage = "NR Cost Scaler is already installed for this game.";
    public const string NotInstalledMessage = "NR Cost Scaler is not installed by RHI for this game.";
    public const string ForeignFilePrefix = "NR Cost Scaler not installed: a file RHI did not write is in the way: ";
    public const string ScalerModifiedPrefix = "NR Cost Scaler not removed: its files changed since RHI installed them (left as they are): ";
    public const string DownloadFailedPrefix = "NR Cost Scaler download failed; nothing was changed: ";
    public const string PackageIncompleteMessage = "The NR Cost Scaler release holds no nvngx_dlssnr.dll and nvngx_dlssnr.ini; nothing was changed.";
    public const string InstalledPrefix = "NR Cost Scaler installed over ";
    public const string RemovedMessage = "NR Cost Scaler removed; the Neural Rendering runtime is back as it was.";
    public const string FailedRolledBackPrefix = "NR Cost Scaler failed and was rolled back: ";
    public const string OwnerBlockedPrefix = "Remove NR Cost Scaler first: it wraps nvngx_dlssnr.dll for ";

    private const string MetaOwner = "NrCostScaler.Owner";
    private const string MetaOwnerSha = "NrCostScaler.OwnerSha256";

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal _journal;
    private readonly string _cacheDir;

    public NrCostScalerService(HttpClient http, LinuxDeploymentRecordStore records, DeploymentJournal journal, string? cacheDir = null)
    {
        _http = http;
        _records = records;
        _journal = journal;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "nr-cost-scaler");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.NrCostScaler);

    /// <summary>
    /// The component that owns <c>nvngx_dlssnr.dll</c> for the game and its recorded file, or null. Provisional copy of
    /// sync #157's <c>NeuralRenderingRuntimeOwnership.OwnerOtherThan</c> owner list (ShortFuse, DLSS5 Feeder, OptiScaler
    /// DLSS NR; F5 DLSSNR joins with it) — replaced by that one rule when integration/w3 carries it (MERGE NOTE).
    /// </summary>
    public (string Owner, LinuxDeployedFile File)? FindOwner(DetectedGame game)
    {
        foreach (var (kind, name) in new[] { (ManagedComponentKind.ShortFuse, "ShortFuse"), (ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder"), (ManagedComponentKind.OptiScaler, "OptiScaler DLSS NR") })
        {
            var record = _records.Find(game, kind);
            if (record is null) continue;
            if (kind == ManagedComponentKind.OptiScaler && OptiScalerDeploymentService.GetInstalledVariant(record) != OptiScalerVariant.DlssNr) continue;
            var file = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(ProxyDllName, StringComparison.OrdinalIgnoreCase));
            if (file is not null) return (name, file);
        }
        return null;
    }

    /// <summary>
    /// The refusal an owner's update, NR swap, reinstall or removal gets while the scaler wraps its runtime (the one-owner
    /// rule's "layer first"); null when <paramref name="owner"/> is not a runtime owner or no scaler is recorded.
    /// </summary>
    public static string? OwnerChangeRefusal(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind owner)
    {
        if (owner is not (ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.OptiScaler)) return null;
        if (records.Find(game, ManagedComponentKind.NrCostScaler) is not { } scaler) return null;
        return OwnerBlockedPrefix + (scaler.Metadata.TryGetValue(MetaOwner, out var name) ? name : owner.ToString());
    }

    public static bool IsInstalledIn(string directory) => File.Exists(Path.Combine(directory, RealDllName));

    /// <summary>
    /// Where the content <paramref name="record"/> lists for <paramref name="file"/> really is on disk. While an NR Cost
    /// Scaler record of the same game lists the same path (its proxy now carries the owner's name), the owner's runtime
    /// sits beside it as <c>nvngx_dlssnr_real.dll</c>, so that is the file to check against the owner's hash; every other
    /// file is where its record says. For any check that verifies every record's files on disk (Repair after move).
    /// </summary>
    /// <param name="gameRecords">All of the game's records (the scaler's among them when it is installed).</param>
    public static string ContentPath(LinuxDeploymentRecord record, LinuxDeployedFile file, IEnumerable<LinuxDeploymentRecord> gameRecords)
    {
        if (record.Component == ManagedComponentKind.NrCostScaler
            || !Path.GetFileName(file.Path).Equals(ProxyDllName, StringComparison.OrdinalIgnoreCase)) return file.Path;
        var wrapped = gameRecords.Any(other => other.Component == ManagedComponentKind.NrCostScaler
                                               && other.Files.Any(f => string.Equals(f.Path, file.Path, StringComparison.Ordinal)));
        return wrapped ? Path.Combine(Path.GetDirectoryName(file.Path)!, RealDllName) : file.Path;
    }

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (FindOwner(game) is not { } owner) return ComponentOperationResult.Fail(NoOwnerMessage);
        var proxyPath = owner.File.Path;
        var directory = Path.GetDirectoryName(proxyPath)!;
        if (!File.Exists(proxyPath) || owner.File.Sha256 is not { Length: > 0 } ownerSha || !FileHash.HexEquals(FileHash.Sha256Hex(proxyPath), ownerSha))
            return ComponentOperationResult.Fail(OwnerModifiedPrefix + owner.Owner);
        var realPath = Path.Combine(directory, RealDllName);
        var iniPath = Path.Combine(directory, IniFileName);
        var addonPath = Path.Combine(directory, CompanionAddonName);
        foreach (var inTheWay in new[] { realPath, iniPath, addonPath })
            if (File.Exists(inTheWay) || Directory.Exists(inTheWay)) return ComponentOperationResult.Fail(ForeignFilePrefix + inTheWay);

        // Everything from the network first: nothing in the game changes before the package is staged and checked.
        Staged staged;
        try
        {
            staged = await StageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or IOException or InvalidDataException or JsonException or UntrustedDownloadException or TaskCanceledException)
        {
            AppLog.Warn("nr-cost-scaler", "The NR Cost Scaler download failed", ex);
            return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
        }

        try
        {
            return await PlaceAsync(game, owner.Owner, ownerSha, staged, proxyPath, realPath, iniPath, addonPath, directory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The staged copies are this install's only; the verified zip stays in the stamped cache.
            if (FileCleanup.TryDeleteAll(Directory.EnumerateFiles(staged.Folder)).Count > 0 || !FileCleanup.TryDeleteEmptyDirectory(staged.Folder))
                AppLog.Warn("nr-cost-scaler", $"Could not remove the staging folder {staged.Folder}");
        }
    }

    private async Task<ComponentOperationResult> PlaceAsync(DetectedGame game, string ownerName, string ownerSha, Staged staged,
        string proxyPath, string realPath, string iniPath, string addonPath, string directory, CancellationToken cancellationToken)
    {
        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(
            _journal, "NR Cost Scaler install: " + game.Name, game, ManagedComponentKind.NrCostScaler, cancellationToken).ConfigureAwait(false);
        if (refusal is not null || operation is null) return ComponentOperationResult.Fail(refusal ?? NoOwnerMessage);
        await using var journaled = operation;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var placed = new List<(string Source, string Target)> { (staged.Proxy, proxyPath), (staged.Ini, iniPath) };
            if (staged.Addon is not null) placed.Add((staged.Addon, addonPath));
            foreach (var path in new[] { proxyPath, realPath, iniPath, addonPath })
                await operation.TrackFileAsync(path, CancellationToken.None).ConfigureAwait(false);

            // The owner's runtime moves aside unchanged (same directory: a rename), then the proxy takes its name.
            await operation.RecordWriteAsync(realPath, ownerSha, CancellationToken.None).ConfigureAwait(false);
            AtomicFile.Rename(proxyPath, realPath);
            var files = new List<LinuxDeployedFile> { new() { Path = realPath, Sha256 = ownerSha, ExistedBeforeInstall = false } };
            foreach (var (source, target) in placed)
            {
                var sha = FileHash.Sha256Hex(source);
                await operation.RecordWriteAsync(target, sha, CancellationToken.None).ConfigureAwait(false);
                await AtomicFile.CopyAsync(source, target, sha, CancellationToken.None, overwrite: false).ConfigureAwait(false);
                // The INI holds the user's scale; RHI removes it with the scaler whatever it says now.
                files.Add(new LinuxDeployedFile { Path = target, Sha256 = sha, ExistedBeforeInstall = false, AllowUserChanges = target == iniPath });
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MetaOwner] = ownerName, [MetaOwnerSha] = ownerSha };
            VerifiedDownload.Record(metadata, staged.ArchiveSha256, staged.DigestVerified);
            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.NrCostScaler,
                BinaryDirectory = directory,
                SourceUrl = staged.Url,
                Version = staged.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = files,
                Metadata = metadata
            }));
            await operation.CommitAsync().ConfigureAwait(false);
            return ComponentOperationResult.Ok(InstalledPrefix + ownerName + $" ({staged.Version}).", files.Select(f => f.Path).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rollback = await operation.RollbackAsync().ConfigureAwait(false);
            AppLog.Warn("nr-cost-scaler", "NR Cost Scaler install failed; rolled back" + (rollback.Failures.Count > 0 ? ": " + string.Join("; ", rollback.Failures) : ""), ex);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message);
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        var directory = record.BinaryDirectory;
        var proxyPath = Path.Combine(directory, ProxyDllName);
        var realPath = Path.Combine(directory, RealDllName);
        // Nothing is removed unless every file is still what RHI wrote (the INI may hold the user's own scale).
        var changed = record.Files.Where(f => !f.AllowUserChanges && (!File.Exists(f.Path) || f.Sha256 is null || !FileHash.HexEquals(FileHash.Sha256Hex(f.Path), f.Sha256)))
            .Select(f => f.Path).ToList();
        if (changed.Count > 0) return ComponentOperationResult.Fail(ScalerModifiedPrefix + string.Join(", ", changed));

        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(
            _journal, "NR Cost Scaler removal: " + game.Name, game, ManagedComponentKind.NrCostScaler, cancellationToken).ConfigureAwait(false);
        if (refusal is not null || operation is null) return ComponentOperationResult.Fail(refusal ?? NotInstalledMessage);
        await using var journaled = operation;
        _ = OperationCancellation.BeginWrites(cancellationToken); // removal runs to completion or full rollback
        try
        {
            foreach (var file in record.Files) await operation.TrackFileAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
            foreach (var file in record.Files.Where(f => f.Path != realPath)) File.Delete(file.Path);
            var realSha = record.Files.First(f => f.Path == realPath).Sha256!;
            await operation.RecordWriteAsync(proxyPath, realSha, CancellationToken.None).ConfigureAwait(false);
            AtomicFile.Rename(realPath, proxyPath);
            _records.Remove(game, ManagedComponentKind.NrCostScaler);
            await operation.CommitAsync().ConfigureAwait(false);
            return ComponentOperationResult.Ok(RemovedMessage, record.Files.Select(f => f.Path).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = await operation.RollbackAsync().ConfigureAwait(false);
            AppLog.Warn("nr-cost-scaler", "NR Cost Scaler removal failed; rolled back" + (rollback.Failures.Count > 0 ? ": " + string.Join("; ", rollback.Failures) : ""), ex);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message);
        }
    }

    private sealed record Staged(string Folder, string Proxy, string Ini, string? Addon, string Version, string Url, string ArchiveSha256, bool DigestVerified);

    /// <summary>Latest release, its zip through the stamped cache (digest checked), the three files extracted fresh.</summary>
    private async Task<Staged> StageAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ReleaseApiUrl, DownloadSources.GitHubApi, accept: "application/vnd.github+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException(PackageIncompleteMessage);
        string? url = null, digest = null, assetName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) != true) continue;
                assetName = name;
                url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                break;
            }
        }
        if (url is null || assetName is null) throw new InvalidDataException(PackageIncompleteMessage);

        var safeVersion = string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        var entry = Path.Combine(_cacheDir, safeVersion, string.Concat(assetName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')));
        var verified = await TrustedDownload.EnsureVerifiedAsync(_http, url, entry, DownloadSources.NrCostScaler, version, digest,
            validate: ReShadeArchiveExtractor.RequireReadableZip, cancellationToken: cancellationToken).ConfigureAwait(false);

        var staging = Path.Combine(_cacheDir, "staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? proxy = null, ini = null, addon = null;
        using (var zip = ZipFile.OpenRead(verified.Path))
        {
            foreach (var zipEntry in zip.Entries)
            {
                var name = zipEntry.Name;
                if (!(name.Equals(ProxyDllName, StringComparison.OrdinalIgnoreCase) || name.Equals(IniFileName, StringComparison.OrdinalIgnoreCase)
                      || name.Equals(CompanionAddonName, StringComparison.OrdinalIgnoreCase))) continue;
                var target = Path.Combine(staging, name.ToLowerInvariant());
                if (File.Exists(target)) continue; // first of a name wins
                await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(zipEntry, target, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
                if (name.Equals(ProxyDllName, StringComparison.OrdinalIgnoreCase)) proxy = target;
                else if (name.Equals(IniFileName, StringComparison.OrdinalIgnoreCase)) ini = target;
                else addon = target;
            }
        }
        if (proxy is null || ini is null)
        {
            FileCleanup.TryDeleteAll(Directory.EnumerateFiles(staging));
            FileCleanup.TryDeleteEmptyDirectory(staging);
            throw new InvalidDataException(PackageIncompleteMessage);
        }
        return new Staged(staging, proxy, ini, addon, version, url, verified.Sha256, verified.DigestVerified);
    }
}
