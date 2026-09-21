using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record DofFixReleaseInfo(
    string Version,
    string AssetUrl,
    string AssetName,
    string? Sha256);

/// <summary>
/// Linux port of the upstream RHI Universal UE DOF Fix deployment. Eligibility and release
/// selection intentionally mirror the pinned Windows source; file ownership/removal is
/// stricter on Linux so an external or user-modified add-on is never destroyed silently.
/// </summary>
public sealed class DofFixDeploymentService
{
    public const string AddonFileName = "renodx-universal_ue-dof-fix.addon64";
    public const string TagPrefix = "ue-dof-fix-";
    public const string GitHubApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string DefaultDownloadBaseUrl = "https://github.com/RankFTW/rhi-repo/releases/download";
    private const long MinimumPayloadSize = 128 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly string _cacheDir;

    public DofFixDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        UpstreamRhiManifestService manifest,
        string? cacheDir = null)
    {
        _http = http;
        _records = records;
        _manifest = manifest;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "ue-dof-fix");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.DofFix);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "DOF Fix is not managed by RHI Linux.");
        try
        {
            var latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                update ? $"DOF Fix update available: {record.Version ?? "unknown"} → {latest.Version}." : $"DOF Fix {record.Version ?? latest.Version} is current.",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"DOF Fix update check failed: {ex.Message}", record.Version);
        }
    }

    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => GetRecord(game) is null
            ? Task.FromResult(ComponentOperationResult.Fail("DOF Fix is not managed by RHI Linux."))
            : InstallAsync(game, cancellationToken);

    /// <summary>
    /// Mirrors upstream RHI DofFixService.IsGameEligible:
    /// explicit x86 is rejected; manifest force wins immediately; otherwise the engine must
    /// identify Unreal Engine 5, then the skip list is applied, and UE 5.0-5.6 is accepted.
    /// Unknown minor versions on an otherwise UE5 hint are treated as eligible upstream.
    /// </summary>
    public static bool IsGameEligible(DetectedGame game)
    {
        if (IsExplicitly32Bit(game)) return false;
        if (game.DofFixForcedByManifest) return true;

        var engineHint = game.EngineHint;
        if (string.IsNullOrEmpty(engineHint)) return false;
        if (!engineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.DofFixSkippedByManifest) return false;

        var idx = engineHint.IndexOf("5.", StringComparison.Ordinal);
        if (idx < 0) return true;
        var afterFive = engineHint[(idx + 2)..];
        var dotIdx = afterFive.IndexOf('.');
        var minorText = dotIdx >= 0 ? afterFive[..dotIdx] : afterFive.TrimEnd(' ', ')');
        return !int.TryParse(minorText, out var minor) || minor is >= 0 and <= 6;
    }

    public static string EligibilityReason(DetectedGame game)
    {
        if (IsExplicitly32Bit(game)) return "Universal UE DOF Fix is 64-bit only.";
        if (game.DofFixForcedByManifest)
            return "Ativado pela regra de força (force) do manifesto do RHI.";
        if (string.IsNullOrWhiteSpace(game.EngineHint))
            return "O DOF Fix é habilitado apenas para títulos explicitamente listados ou jogos Unreal Engine 5.0–5.6 detectados.";
        if (!game.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase))
            return $"DOF Fix targets Unreal Engine 5.0-5.6; detected engine hint is {game.EngineHint}.";
        if (game.DofFixSkippedByManifest)
            return "Desativado pela regra do manifesto do RHI para jogos ignorados.";
        return IsGameEligible(game)
            ? $"Eligible for Universal UE DOF Fix ({game.EngineHint})."
            : $"DOF Fix targets Unreal Engine 5.0-5.6; detected engine hint is {game.EngineHint}.";
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!IsGameEligible(game))
            return ComponentOperationResult.Fail(EligibilityReason(game));

        var deployDir = ResolveDeployDirectory(game);
        if (string.IsNullOrWhiteSpace(deployDir) || !Directory.Exists(deployDir))
            return ComponentOperationResult.Fail("Game binary/install directory is not available for DOF Fix deployment.");

        var target = Path.Combine(deployDir, AddonFileName);
        var existingRecord = GetRecord(game);
        var existingPath = FindExistingAddonPath(deployDir);
        if (existingPath is not null && existingRecord is null)
            return ComponentOperationResult.Fail("Universal UE DOF Fix already exists on disk but is not owned by RHI Linux. The external add-on is protected from overwrite.");
        if (existingRecord is not null && existingPath is not null &&
            !Path.GetFullPath(existingPath).Equals(Path.GetFullPath(target), StringComparison.Ordinal))
            return ComponentOperationResult.Fail("The RHI-managed DOF Fix path changed outside RHI. Update is blocked to avoid creating a case-variant duplicate.");

        if (existingRecord is not null && File.Exists(target))
        {
            var owned = existingRecord.Files.FirstOrDefault(file =>
                Path.GetFullPath(file.Path).Equals(Path.GetFullPath(target), StringComparison.Ordinal));
            if (owned?.Sha256 is not { Length: > 0 })
                return ComponentOperationResult.Fail("Existing RHI DOF Fix record has no ownership hash. Update is blocked conservatively.");
            var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed DOF Fix was modified after installation. Update is blocked to preserve the changed file.");
        }

        var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var cached = await DownloadAndValidateAsync(release, cancellationToken).ConfigureAwait(false);

        await CopyAtomicAsync(cached, target, cancellationToken).ConfigureAwait(false);
        var deployedHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);

        _records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.DofFix,
            BinaryDirectory = deployDir,
            SourceUrl = release.AssetUrl,
            Version = release.Version,
            InstalledAtUtc = DateTime.UtcNow,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = target,
                    ExistedBeforeInstall = false,
                    Sha256 = deployedHash,
                    AllowUserChanges = false
                }
            ],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DofFix.TagPrefix"] = TagPrefix,
                ["DofFix.AssetName"] = release.AssetName,
                ["DofFix.ManifestForced"] = game.DofFixForcedByManifest ? "1" : "0",
                ["DofFix.EngineHint"] = game.EngineHint ?? ""
            }
        });

        return ComponentOperationResult.Ok($"Universal UE DOF Fix {release.Version} installed.", target);
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Universal UE DOF Fix is not managed by RHI Linux. External files are protected from removal.");

        var paths = record.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .ToArray();
        foreach (var file in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail("DOF Fix ownership hash is missing; removal is blocked conservatively.");
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed DOF Fix was modified after installation. Remove is blocked to preserve the changed file.");
        }

        var changed = new List<string>();
        foreach (var file in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
                changed.Add(file.Path);
            }
        }

        _records.Remove(game, ManagedComponentKind.DofFix);
        return ComponentOperationResult.Ok("Universal UE DOF Fix removed. User/external files were left untouched.", changed.ToArray());
    }

    public async Task<DofFixReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RHI DOF Fix release API returned an unexpected JSON shape.");

        var candidates = new List<(string Version, Version Parsed, string? AssetUrl, string? Sha256)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var version = tag[TagPrefix.Length..];
            string? assetUrl = null;
            string? sha256 = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.Equals(name, AddonFileName, StringComparison.Ordinal)) continue;
                    assetUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha256 = digest["sha256:".Length..].Trim();
                    break;
                }
            }

            Version.TryParse(version, out var parsed);
            candidates.Add((version, parsed ?? new Version(0, 0), assetUrl, sha256));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("No ue-dof-fix-* release exists in the pinned RHI release repository response.");

        var best = candidates.OrderByDescending(candidate => candidate.Parsed).First();
        string? overrideUrl = null;
        try
        {
            overrideUrl = await _manifest.FetchComponentUrlAsync("ueDofFix", cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Manifest enrichment is fail-open upstream: the GitHub release asset remains authoritative
            // when the pinned manifest cannot be reached.
        }
        var usingOverride = !string.IsNullOrWhiteSpace(overrideUrl);
        var finalUrl = usingOverride
            ? overrideUrl!.Replace("{version}", best.Version, StringComparison.Ordinal)
            : best.AssetUrl ?? $"{DefaultDownloadBaseUrl}/{TagPrefix}{best.Version}/{AddonFileName}";

        return new DofFixReleaseInfo(
            best.Version,
            finalUrl,
            AddonFileName,
            usingOverride ? null : best.Sha256);
    }

    internal static bool IsExplicitly32Bit(DetectedGame game)
    {
        if (game.ForcedArchitecture == PeArchitecture.X86) return true;
        if (game.ForcedArchitecture == PeArchitecture.X64) return false;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) return false;
        return PeArchitectureDetector.Detect(game.ExecutablePath) == PeArchitecture.X86;
    }

    internal static string ResolveDeployDirectory(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.BinaryDirectory! : game.InstallPath;

    internal static string? FindExistingAddonPath(string deployDirectory)
    {
        if (!Directory.Exists(deployDirectory)) return null;
        return Directory.EnumerateFiles(deployDirectory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(AddonFileName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> DownloadAndValidateAsync(DofFixReleaseInfo release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(release.Version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var cache = Path.Combine(_cacheDir, $"{safeVersion}-{AddonFileName}");
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
            if (!BinarySignatureInspector.HasPeSignature(temp, MinimumPayloadSize))
                throw new InvalidDataException("Downloaded Universal UE DOF Fix is not a valid PE payload or is unexpectedly small.");

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = await Sha256Async(temp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded Universal UE DOF Fix failed the SHA-256 digest published by GitHub.");
            }

            File.Move(temp, cache, overwrite: true);
            return cache;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task CopyAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("DOF Fix target directory is invalid.");
        Directory.CreateDirectory(directory);
        var temp = target + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var sourceHash = await Sha256Async(source, cancellationToken).ConfigureAwait(false);
            var tempHash = await Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, tempHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("DOF Fix copy verification failed.");
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
