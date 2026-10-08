using System.IO.Compression;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux/Wine backend for the upstream RHI RE Framework flow.
/// Upstream deploys the monolithic praydog REFramework.zip dinput8.dll for RE Engine games.
/// The Linux port adds per-game Wine override ownership and hash-guarded teardown so external
/// or user-modified dinput8 proxies are never overwritten or removed silently.
/// </summary>
public sealed class REFrameworkDeploymentService
{
    public const string DllFileName = "dinput8.dll";
    public const string MonolithicZipName = "REFramework.zip";
    public const string ReleasesApiUrl = "https://api.github.com/repos/praydog/REFramework-nightly/releases/latest";
    private const long MinimumDllSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly GitHubReleaseAssetResolver _resolver;
    private readonly string _cacheDir;

    public REFrameworkDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        string? cacheDir = null)
    {
        _http = http;
        _records = records;
        _overrides = overrides;
        _resolver = new GitHubReleaseAssetResolver(http);
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "reframework");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.REFramework);

    public static bool IsGameEligible(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.ForcedArchitecture == PeArchitecture.X86) return false;
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath) &&
            game.ForcedArchitecture is null && PeArchitectureDetector.Detect(game.ExecutablePath) == PeArchitecture.X86)
            return false;
        return !string.IsNullOrWhiteSpace(game.EngineHint) &&
               game.EngineHint.Contains("RE Engine", StringComparison.OrdinalIgnoreCase);
    }

    public static string EligibilityReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "RE Framework is a Windows dinput8 injection component and is not used for native-Linux targets.";
        if (game.ForcedArchitecture == PeArchitecture.X86 ||
            (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath) &&
             game.ForcedArchitecture is null && PeArchitectureDetector.Detect(game.ExecutablePath) == PeArchitecture.X86))
            return "The current monolithic RE Framework nightly package is enabled only for 64-bit RE Engine games.";
        if (string.IsNullOrWhiteSpace(game.EngineHint) ||
            !game.EngineHint.Contains("RE Engine", StringComparison.OrdinalIgnoreCase))
            return "RE Framework está disponível para títulos RE Engine detectados.";
        return $"Eligible for RE Framework ({game.EngineHint}).";
    }

    public async Task<GitHubResolvedReleaseAsset> ResolveLatestAsync(CancellationToken cancellationToken = default)
        => await _resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            ReleasesApiUrl,
            ExactAssetName: MonolithicZipName), cancellationToken).ConfigureAwait(false);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!IsGameEligible(game)) return ComponentOperationResult.Fail(EligibilityReason(game));
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !File.Exists(Path.Combine(game.PrefixPath, "user.reg")))
            return ComponentOperationResult.Fail("Start the RE Engine game once so its Wine/Proton prefix exists before installing RE Framework.");

        var deployDir = ResolveDeployDirectory(game);
        if (string.IsNullOrWhiteSpace(deployDir) || !Directory.Exists(deployDir))
            return ComponentOperationResult.Fail("Game binary/install directory is not available for RE Framework deployment.");

        var target = FindCaseInsensitiveFile(deployDir, DllFileName) ?? Path.Combine(deployDir, DllFileName);
        var existingRecord = GetRecord(game);
        if (File.Exists(target) && existingRecord is null)
            return ComponentOperationResult.Fail("dinput8.dll already exists beside the game executable but is not owned by RHI Linux. The external proxy is protected from overwrite.");
        if (existingRecord is not null)
            return ComponentOperationResult.Fail("RE Framework is already managed by RHI Linux for this game. Remove it before reinstalling.");

        var release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        var cachedDll = await DownloadExtractAndValidateAsync(release, cancellationToken).ConfigureAwait(false);
        var applicationName = Path.GetFileName(game.ExecutablePath ?? "");
        WineOverrideRecord? overrideRecord = null;
        var deployed = false;
        // Downloaded and validated: from the override on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            overrideRecord = _overrides.EnsureNativeBuiltin(game.PrefixPath!, DllFileName, applicationName);
            await CopyAtomicAsync(cachedDll, target, cancellationToken).ConfigureAwait(false);
            deployed = true;
            var deployedHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);

            _records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.REFramework,
                BinaryDirectory = deployDir,
                SourceUrl = release.AssetUrl,
                Version = NormalizeVersion(release.Version),
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = overrideRecord,
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
                    ["REFramework.AssetName"] = release.AssetName,
                    ["REFramework.EngineHint"] = game.EngineHint ?? "",
                    ["REFramework.ApplicationName"] = applicationName
                }
            });
            return ComponentOperationResult.Ok($"RE Framework {NormalizeVersion(release.Version)} installed with per-game dinput8 Wine override.", target);
        }
        catch
        {
            if (deployed) TryDelete(target);
            if (overrideRecord is not null)
            {
                try { _overrides.Restore(game.PrefixPath!, overrideRecord); } catch { }
            }
            throw;
        }
    }

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "RE Framework is not managed by RHI Linux.");
        try
        {
            var latest = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            var latestVersion = NormalizeVersion(latest.Version);
            var update = !string.Equals(record.Version, latestVersion, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                update ? $"RE Framework update available: {record.Version ?? "unknown"} → {latestVersion}." : $"RE Framework {record.Version ?? latestVersion} is current.",
                record.Version, latestVersion);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"RE Framework update check failed: {ex.Message}", record.Version);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RE Framework is not managed by RHI Linux.");
        var owned = record.Files.SingleOrDefault(file =>
            Path.GetFileName(file.Path).Equals(DllFileName, StringComparison.OrdinalIgnoreCase));
        if (owned is null || string.IsNullOrWhiteSpace(owned.Sha256) || !File.Exists(owned.Path))
            return ComponentOperationResult.Fail("RE Framework managed dinput8.dll ownership is incomplete; update is blocked conservatively.");
        var currentHash = await Sha256Async(owned.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RHI-managed RE Framework dinput8.dll was modified after installation. Update is blocked to preserve the changed file.");

        GitHubResolvedReleaseAsset release;
        string cachedDll;
        try
        {
            release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            cachedDll = await DownloadExtractAndValidateAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"RE Framework update preparation failed: {ex.Message}");
        }

        var backup = owned.Path + $".rhi-update-backup-{Guid.NewGuid():N}";
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            File.Copy(owned.Path, backup, overwrite: false);
            await ReplaceAtomicAsync(cachedDll, owned.Path, cancellationToken).ConfigureAwait(false);
            owned.Sha256 = await Sha256Async(owned.Path, cancellationToken).ConfigureAwait(false);
            record.SourceUrl = release.AssetUrl;
            record.Version = NormalizeVersion(release.Version);
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata["REFramework.AssetName"] = release.AssetName;
            _records.Upsert(record);
            TryDelete(backup);
            return ComponentOperationResult.Ok($"RE Framework updated to {record.Version}; existing Wine override ownership was preserved.", owned.Path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(backup)) File.Copy(backup, owned.Path, overwrite: true); } catch { }
            TryDelete(backup);
            return ComponentOperationResult.Fail($"RE Framework update rolled back: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RE Framework is not managed by RHI Linux. External dinput8.dll files are protected from removal.");

        var owned = record.Files.SingleOrDefault(file =>
            Path.GetFileName(file.Path).Equals(DllFileName, StringComparison.OrdinalIgnoreCase));
        if (owned is null || string.IsNullOrWhiteSpace(owned.Sha256))
            return ComponentOperationResult.Fail("RE Framework ownership hash is missing; removal is blocked conservatively.");

        if (File.Exists(owned.Path))
        {
            var current = await Sha256Async(owned.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed RE Framework dinput8.dll was modified after installation. Remove is blocked to preserve the changed file.");
        }

        if (string.IsNullOrWhiteSpace(game.PrefixPath))
            return ComponentOperationResult.Fail("The Wine/Proton prefix is unavailable, so the dinput8 override cannot be restored safely.");

        try
        {
            _overrides.Restore(game.PrefixPath!, record.WineOverride);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RE Framework removal stopped before deleting dinput8.dll because the Wine override could not be restored: {ex.Message}");
        }

        try
        {
            if (File.Exists(owned.Path)) File.Delete(owned.Path);
        }
        catch (Exception ex)
        {
            // Keep the game usable if filesystem teardown failed after registry restoration.
            try
            {
                var app = record.WineOverride?.ApplicationName ?? Path.GetFileName(game.ExecutablePath ?? "");
                _overrides.EnsureNativeBuiltin(game.PrefixPath!, DllFileName, app);
            }
            catch { }
            return ComponentOperationResult.Fail($"RE Framework dinput8.dll could not be removed: {ex.Message}");
        }

        _records.Remove(game, ManagedComponentKind.REFramework);
        return ComponentOperationResult.Ok("RE Framework removed and the previous per-game dinput8 Wine override restored.", owned.Path);
    }

    internal static string ResolveDeployDirectory(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.BinaryDirectory! : game.InstallPath;

    internal static string? FindCaseInsensitiveFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        foreach (var part in version.Split('-', StringSplitOptions.RemoveEmptyEntries))
            if (part.All(char.IsDigit)) return part;
        return version;
    }

    private async Task<string> DownloadExtractAndValidateAsync(
        GitHubResolvedReleaseAsset release,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(NormalizeVersion(release.Version).Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var zipPath = Path.Combine(_cacheDir, $"{safeVersion}-{MonolithicZipName}");
        var zipTemp = zipPath + $".tmp-{Guid.NewGuid():N}";
        var dllPath = Path.Combine(_cacheDir, $"{safeVersion}-{DllFileName}");
        var dllTemp = dllPath + $".tmp-{Guid.NewGuid():N}";

        using (var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl))
        {
            request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(zipTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = await Sha256Async(zipTemp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded REFramework.zip failed the SHA-256 digest published by GitHub.");
            }

            using (var archive = ZipFile.OpenRead(zipTemp))
            {
                var entries = archive.Entries
                    .Where(entry => entry.Name.Equals(DllFileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (entries.Length != 1)
                    throw new InvalidDataException($"REFramework.zip must contain exactly one {DllFileName}; found {entries.Length}.");
                await using var input = entries[0].Open();
                await using var output = new FileStream(dllTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (!BinarySignatureInspector.HasPeSignature(dllTemp, MinimumDllSize))
                throw new InvalidDataException("Extracted RE Framework dinput8.dll is not a valid PE payload or is unexpectedly small.");
            if (PeArchitectureDetector.Detect(dllTemp) != PeArchitecture.X64)
                throw new InvalidDataException("Extracted RE Framework dinput8.dll is not the expected 64-bit payload.");

            File.Move(zipTemp, zipPath, overwrite: true);
            File.Move(dllTemp, dllPath, overwrite: true);
            return dllPath;
        }
        catch
        {
            TryDelete(zipTemp);
            TryDelete(dllTemp);
            throw;
        }
    }

    private static async Task CopyAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var temp = target + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var sourceHash = await Sha256Async(source, cancellationToken).ConfigureAwait(false);
            var tempHash = await Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, tempHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("RE Framework copy verification failed.");
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task ReplaceAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var temp = target + $".rhi-update-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var sourceHash = await Sha256Async(source, cancellationToken).ConfigureAwait(false);
            var tempHash = await Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, tempHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("RE Framework update copy verification failed.");
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
