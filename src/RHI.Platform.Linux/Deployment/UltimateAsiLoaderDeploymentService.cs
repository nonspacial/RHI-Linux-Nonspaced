using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux-native Ultimate ASI Loader deployment for Wine/Proton targets.
///
/// The upstream project publishes dinput8.dll inside architecture-specific ZIPs.
/// RHI may deploy that binary under any loader-supported proxy name, then creates a
/// per-application Wine native,builtin override so Wine actually loads the game-local proxy.
///
/// Existing non-RHI target DLLs are preserved through Ultimate ASI Loader's documented
/// &lt;name&gt;Hooked.dll chaining convention. RHI-managed proxies are never chained over.
/// </summary>
public sealed class UltimateAsiLoaderDeploymentService
{
    public const string GitHubApiUrl = "https://api.github.com/repos/ThirteenAG/Ultimate-ASI-Loader/releases/latest";
    public const string ReleasesUrl = "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases";
    public const string DefaultInnerDllName = "dinput8.dll";

    private const string Asset32 = "Ultimate-ASI-Loader.zip";
    private const string Asset64 = "Ultimate-ASI-Loader_x64.zip";
    private const string MetaProxyName = "UAL.ProxyName";
    private const string MetaHookedPath = "UAL.HookedPath";
    private const string MetaHadOriginal = "UAL.HadOriginal";
    private const string MetaAssetSha256 = "UAL.AssetSha256";
    private const string MetaArchitecture = "UAL.Architecture";

    public static readonly IReadOnlyList<string> Win32Names =
    [
        "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll",
        "ddraw.dll", "dinput.dll", "dinput8.dll", "dsound.dll",
        "msacm32.dll", "msvfw32.dll",
        "version.dll", "wininet.dll", "winmm.dll", "winhttp.dll",
        "xlive.dll", "binkw32.dll", "bink2w32.dll", "vorbisFile.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll",
        "xinput9_1_0.dll", "xinputuap.dll",
    ];

    public static readonly IReadOnlyList<string> Win64Names =
    [
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll",
        "dinput8.dll", "dsound.dll",
        "version.dll", "wininet.dll", "winmm.dll", "winhttp.dll",
        "binkw64.dll", "bink2w64.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll",
        "xinput9_1_0.dll", "xinputuap.dll",
    ];

    public static readonly IReadOnlyList<string> RecommendedNames =
        ["version.dll", "winmm.dll", "winhttp.dll"];

    public static readonly IReadOnlyList<string> RhiConflictNames =
        ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll"];

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly string _cacheRoot;

    public UltimateAsiLoaderDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        string? cacheRoot = null)
    {
        _http = http;
        _records = records;
        _overrides = overrides;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "ual");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.UltimateAsiLoader);

    public string? InstalledProxyName(DetectedGame game)
        => GetRecord(game)?.Metadata.GetValueOrDefault(MetaProxyName);

    public string? InstalledVersion(DetectedGame game)
        => GetRecord(game)?.Version;

    public static IReadOnlyList<string> SupportedNamesFor(DetectedGame game)
        => ResolveArchitecture(game) switch
        {
            PeArchitecture.X86 => Win32Names,
            PeArchitecture.X64 => Win64Names,
            _ => []
        };

    public static bool IsSupportedProxyName(DetectedGame game, string? dllName)
        => !string.IsNullOrWhiteSpace(dllName)
           && SupportedNamesFor(game).Contains(Path.GetFileName(dllName.Trim()), StringComparer.OrdinalIgnoreCase);

    public static string RecommendedDefaultFor(DetectedGame game)
    {
        var supported = SupportedNamesFor(game);
        foreach (var candidate in new[] { "version.dll", "winmm.dll", "winhttp.dll", "dinput8.dll" })
            if (supported.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        return supported.FirstOrDefault() ?? "";
    }

    public async Task<UalUpdateStatus> CheckLatestAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return new UalUpdateStatus(false, InstalledVersion(game), null, false, "ASI Loader requires a detected x86 or x64 Windows executable.");

        try
        {
            var release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
            var current = InstalledVersion(game);
            var hasUpdate = !string.IsNullOrWhiteSpace(current)
                            && !string.Equals(current, release.Version, StringComparison.OrdinalIgnoreCase);
            return new UalUpdateStatus(true, current, release.Version, hasUpdate,
                string.IsNullOrWhiteSpace(current)
                    ? $"Latest Ultimate ASI Loader: {release.Version}"
                    : hasUpdate
                        ? $"Update available: {current} → {release.Version}"
                        : $"Ultimate ASI Loader {current} is current.");
        }
        catch (Exception ex)
        {
            return new UalUpdateStatus(false, InstalledVersion(game), null, false, $"Could not check Ultimate ASI Loader: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string dllName,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("Ultimate ASI Loader is a Windows DLL proxy and is not applicable to a native Linux target.");

        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is already managed for this game. Use Update/Reinstall or Remove first.");

        var preflight = ValidateTarget(game, dllName);
        if (!preflight.Success)
            return ComponentOperationResult.Fail(preflight.Message);

        var architecture = ResolveArchitecture(game);
        var binaryDir = ResolveBinaryDirectory(game)!;
        var proxyName = Path.GetFileName(dllName.Trim());
        var targetPath = Path.Combine(binaryDir, proxyName);
        var hookedPath = Path.Combine(binaryDir, HookedName(proxyName));
        var hadOriginal = File.Exists(targetPath);

        if (IsRhiManagedPath(game, targetPath))
            return ComponentOperationResult.Fail(
                $"{proxyName} is already owned by another RHI-managed component. Choose a different ASI Loader proxy name; RHI will not reuse or chain over another managed proxy path.");

        if (hadOriginal && File.Exists(hookedPath))
            return ComponentOperationResult.Fail(
                $"Cannot chain {proxyName}: {Path.GetFileName(hookedPath)} already exists. RHI will not overwrite an existing Hooked backup.");

        UalReleaseInfo release;
        string stagedDll;
        try
        {
            release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
            stagedDll = await EnsureCachedDllAsync(release, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Ultimate ASI Loader download/validation failed: {ex.Message}");
        }

        WineOverrideRecord? wineOverride = null;
        var movedOriginal = false;
        var copiedProxy = false;

        try
        {
            wineOverride = _overrides.EnsureNativeBuiltin(game.PrefixPath!, proxyName, Path.GetFileName(game.ExecutablePath));

            if (hadOriginal)
            {
                File.Move(targetPath, hookedPath);
                movedOriginal = true;
            }

            File.Copy(stagedDll, targetPath, overwrite: false);
            copiedProxy = true;

            var targetHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaProxyName] = proxyName,
                [MetaHadOriginal] = hadOriginal ? "1" : "0",
                [MetaArchitecture] = architecture.ToString(),
                [MetaAssetSha256] = release.AssetSha256 ?? "",
            };
            if (hadOriginal)
                metadata[MetaHookedPath] = hookedPath;

            _records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.UltimateAsiLoader,
                BinaryDirectory = binaryDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = targetPath,
                        ExistedBeforeInstall = false,
                        Sha256 = targetHash,
                        AllowUserChanges = false
                    }
                ],
                Metadata = metadata
            });

            var chainText = hadOriginal
                ? $" Existing {proxyName} was preserved as {Path.GetFileName(hookedPath)} and will be restored on Remove."
                : "";
            return ComponentOperationResult.Ok(
                $"Ultimate ASI Loader {release.Version} installed as {proxyName}; Wine override {Path.GetFileName(game.ExecutablePath)}:{Path.GetFileNameWithoutExtension(proxyName)}=native,builtin applied.{chainText}");
        }
        catch (Exception ex)
        {
            try
            {
                if (copiedProxy && File.Exists(targetPath))
                    File.Delete(targetPath);
                if (movedOriginal && File.Exists(hookedPath) && !File.Exists(targetPath))
                    File.Move(hookedPath, targetPath);
            }
            catch { }

            try
            {
                if (wineOverride is not null)
                    _overrides.Restore(game.PrefixPath!, wineOverride);
            }
            catch { }

            return ComponentOperationResult.Fail($"Ultimate ASI Loader install rolled back: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is not managed by RHI Linux for this game.");

        if (!record.Metadata.TryGetValue(MetaProxyName, out var proxyName) || string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing its proxy filename. Remove is safer than adopting this record.");

        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(f => Path.GetFullPath(f.Path).Equals(Path.GetFullPath(targetPath), StringComparison.Ordinal));
        if (fileRecord is null || string.IsNullOrWhiteSpace(fileRecord.Sha256))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing the managed proxy hash.");

        if (!File.Exists(targetPath))
            return ComponentOperationResult.Fail($"Managed ASI Loader proxy is missing: {targetPath}. RHI will not recreate it without an explicit Remove/reinstall cycle.");

        var currentHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Managed ASI Loader proxy was modified outside RHI: {targetPath}. Update is blocked to protect the external change.");

        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail("Could not resolve x86/x64 architecture for the selected game.");

        UalReleaseInfo release;
        string stagedDll;
        try
        {
            release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
            stagedDll = await EnsureCachedDllAsync(release, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Ultimate ASI Loader update download/validation failed: {ex.Message}");
        }

        if (string.Equals(record.Version, release.Version, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Ok($"Ultimate ASI Loader {record.Version} is already current.");

        var temp = targetPath + $".rhi-update-{Guid.NewGuid():N}";
        try
        {
            File.Copy(stagedDll, temp, overwrite: false);
            var stagedHash = await Sha256FileAsync(temp, cancellationToken).ConfigureAwait(false);
            File.Move(temp, targetPath, overwrite: true);

            fileRecord.Sha256 = stagedHash;
            record.Version = release.Version;
            record.SourceUrl = release.AssetUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaAssetSha256] = release.AssetSha256 ?? "";
            _records.Upsert(record);
            return ComponentOperationResult.Ok($"Ultimate ASI Loader updated to {release.Version} as {proxyName}.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return ComponentOperationResult.Fail($"Ultimate ASI Loader update failed: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is not managed by RHI Linux. Removal is locked to protect external files.");

        if (!record.Metadata.TryGetValue(MetaProxyName, out var proxyName) || string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing its proxy filename.");

        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(f => Path.GetFullPath(f.Path).Equals(Path.GetFullPath(targetPath), StringComparison.Ordinal));
        if (fileRecord is null || string.IsNullOrWhiteSpace(fileRecord.Sha256))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing the managed proxy hash.");

        var hadOriginal = record.Metadata.GetValueOrDefault(MetaHadOriginal) == "1";
        var hookedPath = record.Metadata.GetValueOrDefault(MetaHookedPath);
        if (hadOriginal && (string.IsNullOrWhiteSpace(hookedPath) || !File.Exists(hookedPath)))
            return ComponentOperationResult.Fail(
                $"Original chained DLL is missing ({hookedPath ?? HookedName(proxyName)}). Remove is blocked before touching the managed proxy.");

        if (File.Exists(targetPath))
        {
            var currentHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(
                    $"Managed ASI Loader proxy was modified outside RHI: {targetPath}. Remove is blocked before teardown.");
        }

        var tempManaged = targetPath + $".rhi-remove-{Guid.NewGuid():N}";
        var movedManagedAside = false;
        var restoredOriginal = false;

        try
        {
            if (File.Exists(targetPath))
            {
                File.Move(targetPath, tempManaged);
                movedManagedAside = true;
            }

            if (hadOriginal)
            {
                File.Move(hookedPath!, targetPath);
                restoredOriginal = true;
            }

            _overrides.Restore(game.PrefixPath!, record.WineOverride);

            if (movedManagedAside && File.Exists(tempManaged))
                File.Delete(tempManaged);

            _records.Remove(game, ManagedComponentKind.UltimateAsiLoader);
            return ComponentOperationResult.Ok(
                hadOriginal
                    ? $"Ultimate ASI Loader removed; original {proxyName} restored and Wine override rolled back."
                    : $"Ultimate ASI Loader removed; Wine override for {proxyName} rolled back.");
        }
        catch (Exception ex)
        {
            // Best-effort rollback to the managed-installed state so a retry remains possible.
            try
            {
                if (restoredOriginal && File.Exists(targetPath) && !File.Exists(hookedPath!))
                    File.Move(targetPath, hookedPath!);
                if (movedManagedAside && File.Exists(tempManaged) && !File.Exists(targetPath))
                    File.Move(tempManaged, targetPath);
            }
            catch { }

            return ComponentOperationResult.Fail($"Ultimate ASI Loader removal failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    internal static PeArchitecture ResolveArchitecture(DetectedGame game)
    {
        if (game.ForcedArchitecture is PeArchitecture.X86 or PeArchitecture.X64)
            return game.ForcedArchitecture.Value;
        return PeArchitectureDetector.Detect(game.ExecutablePath);
    }

    internal static string? ResolveBinaryDirectory(DetectedGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.BinaryDirectory) && Directory.Exists(game.BinaryDirectory))
            return Path.GetFullPath(game.BinaryDirectory);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            return Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath));
        return null;
    }

    public static string HookedName(string dllName)
        => $"{Path.GetFileNameWithoutExtension(dllName)}Hooked{Path.GetExtension(dllName)}";

    internal UalTargetValidation ValidateTarget(DetectedGame game, string? dllName)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return new(false, "Start the Windows game once so its Wine/Proton prefix exists before installing Ultimate ASI Loader.");
        if (!File.Exists(Path.Combine(game.PrefixPath, "user.reg")))
            return new(false, "Wine/Proton user.reg is missing. Start the game once so the prefix is initialized.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return new(false, "A Windows game executable could not be resolved for Ultimate ASI Loader.");
        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return new(false, "Ultimate ASI Loader requires a detected x86 or x64 Windows executable.");
        var binaryDir = ResolveBinaryDirectory(game);
        if (binaryDir is null)
            return new(false, "The game binary directory could not be resolved.");
        if (string.IsNullOrWhiteSpace(dllName) || !IsSupportedProxyName(game, dllName))
            return new(false, $"'{dllName}' is not a supported {architecture} Ultimate ASI Loader proxy name.");
        return new(true, "Ready");
    }

    private bool IsRhiManagedPath(DetectedGame game, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var record in _records.FindAll(game))
        {
            if (record.Component == ManagedComponentKind.UltimateAsiLoader) continue;
            if (record.Files.Any(file =>
                    Path.GetFullPath(file.Path).Equals(full, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private async Task<UalReleaseInfo> FetchLatestReleaseAsync(
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseReleaseJson(json, architecture);
    }

    internal static UalReleaseInfo ParseReleaseJson(string json, PeArchitecture architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
                  ?? throw new InvalidDataException("Ultimate ASI Loader release has no tag_name.");
        var version = tag.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Ultimate ASI Loader release tag is empty.");

        var assetName = architecture == PeArchitecture.X86 ? Asset32 : Asset64;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidDataException($"{assetName} has no browser_download_url.");

            string? digest = null;
            if (asset.TryGetProperty("digest", out var digestElement))
            {
                var raw = digestElement.GetString();
                if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = raw["sha256:".Length..].Trim().ToLowerInvariant();
            }

            return new UalReleaseInfo(version, assetName, url, digest);
        }

        throw new InvalidDataException($"Latest Ultimate ASI Loader release does not contain {assetName}.");
    }

    private async Task<string> EnsureCachedDllAsync(
        UalReleaseInfo release,
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        var archFolder = architecture == PeArchitecture.X86 ? "x86" : "x64";
        var versionDir = Path.Combine(_cacheRoot, archFolder, SanitizePathPart(release.Version));
        var cachedDll = Path.Combine(versionDir, DefaultInnerDllName);
        var metadataPath = Path.Combine(versionDir, "source.json");

        if (File.Exists(cachedDll) && PeArchitectureDetector.Detect(cachedDll) == architecture)
            return cachedDll;

        Directory.CreateDirectory(versionDir);
        var zipPath = Path.Combine(versionDir, release.AssetName + ".download");
        var tempDll = cachedDll + $".tmp-{Guid.NewGuid():N}";

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl))
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var archiveHash = await Sha256FileAsync(zipPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(release.AssetSha256) &&
                !string.Equals(archiveHash, release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Ultimate ASI Loader archive SHA256 mismatch for {release.AssetName}: expected {release.AssetSha256}, got {archiveHash}.");

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.FullName).Equals(DefaultInnerDllName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{DefaultInnerDllName} was not found in {release.AssetName}.");

                if (entry.Length < 32 * 1024)
                    throw new InvalidDataException($"{DefaultInnerDllName} is unexpectedly small ({entry.Length} bytes).");

                await using var source = entry.Open();
                await using var destination = File.Create(tempDll);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (PeArchitectureDetector.Detect(tempDll) != architecture)
                throw new InvalidDataException(
                    $"Extracted {DefaultInnerDllName} has the wrong PE architecture for {architecture}.");

            File.Move(tempDll, cachedDll, overwrite: true);
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
            {
                release.Version,
                release.AssetName,
                release.AssetUrl,
                release.AssetSha256,
                DownloadedSha256 = archiveHash,
                CachedAtUtc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));

            return cachedDll;
        }
        finally
        {
            try { if (File.Exists(tempDll)) File.Delete(tempDll); } catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizePathPart(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
}

public sealed record UalReleaseInfo(
    string Version,
    string AssetName,
    string AssetUrl,
    string? AssetSha256);

public sealed record UalUpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);

internal sealed record UalTargetValidation(bool Success, string Message);
