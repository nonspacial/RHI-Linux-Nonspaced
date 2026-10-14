using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public enum OptiScalerVariant
{
    Stable,
    Nightly,
    DlssNr,
    /// <summary>janblade's F5 DLSSNR multi-pass fork with its bundled NR forwarder (#157, upstream 2.8.7).</summary>
    F5
}

/// <param name="Download">The verified release archive the package was extracted from (record metadata, #26); null when not downloaded through the provider.</param>
public sealed record OptiScalerPackage(
    string Version,
    string SourceUrl,
    string RootDirectory,
    OptiScalerVariant Variant = OptiScalerVariant.Stable,
    VerifiedDownload? Download = null);

public interface IOptiScalerPackageProvider
{
    Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default);

    Task<OptiScalerPackage> GetLatestAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
        => variant == OptiScalerVariant.Stable
            ? GetLatestAsync(cancellationToken)
            : Task.FromException<OptiScalerPackage>(
                new NotSupportedException($"OptiScaler package provider does not support the {variant} channel."));

    /// <summary>One nightly build by its date (<c>20261004</c>), downloaded on demand and cached per tag (#156).</summary>
    Task<OptiScalerPackage> GetNightlyBuildAsync(string build, CancellationToken cancellationToken = default)
        => Task.FromException<OptiScalerPackage>(new NotSupportedException("OptiScaler package provider does not offer pinned nightly builds."));

    /// <summary>The newest nightly builds (dates, newest first, at most <see cref="OptiScalerPackageProvider.NightlyBuildListSize"/>).</summary>
    Task<IReadOnlyList<string>> ListNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<string>>(new NotSupportedException("OptiScaler package provider does not list nightly builds."));
}

public sealed class OptiScalerPackageProvider : IOptiScalerPackageProvider
{
    public const string LatestReleaseApi = "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest";
    public const string NightlyReleasesApi = "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases";
    public const string DlssNrReleasesApi = "https://api.github.com/repos/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases";
    /// <summary>F5 DLSSNR (#157): GitHub's "Latest" release, as upstream; its asset is <see cref="F5AssetPrefix"/>*.zip.</summary>
    public const string F5ReleasesApi = "https://api.github.com/repos/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/latest";
    /// <summary>The F5 release also carries an "Optional-Vendor-Neutral-DLSS5-Forwarder.zip" companion; only this prefix is the package (upstream 8dcf55c, 4f28876).</summary>
    public const string F5AssetPrefix = "OptiScaler-DLSSNR-F5-";
    public const string ArchivePathRefusedPrefix = "The OptiScaler archive holds a path RHI will not extract: ";
    /// <summary>Nightly tags are <c>nightly-YYYYMMDD</c>; the build a game is pinned to is the date (#156).</summary>
    public const string NightlyTagPrefix = "nightly-";
    /// <summary>Upstream offers the last 30 nightly builds (GitHub's default page of the release list).</summary>
    public const int NightlyBuildListSize = 30;
    public const string NightlyBuildMismatchPrefix = "GitHub returned another OptiScaler nightly build than the one pinned: ";

    /// <summary>A nightly build as RHI pins it: the 8-digit date of its tag.</summary>
    public static bool IsNightlyBuild(string? value)
        => value is { Length: 8 } && value.All(char.IsAsciiDigit);

    public static string NightlyTagApiUrl(string build)
        => $"https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases/tags/{NightlyTagPrefix}{build}";

    private readonly HttpClient _http;
    private readonly IOptiScalerArchiveExtractor _extractor;
    private readonly string _cacheDir;

    public OptiScalerPackageProvider(HttpClient http, IOptiScalerArchiveExtractor extractor, string? cacheDir = null)
    {
        _http = http;
        _extractor = extractor;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "optiscaler");
    }

    public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
        => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

    public Task<OptiScalerPackage> GetLatestAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var (apiUrl, responseIsList) = variant switch
        {
            OptiScalerVariant.Stable => (LatestReleaseApi, false),
            OptiScalerVariant.Nightly => (NightlyReleasesApi, true),
            OptiScalerVariant.DlssNr => (DlssNrReleasesApi, true),
            OptiScalerVariant.F5 => (F5ReleasesApi, false),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
        };
        return FetchAsync(apiUrl, responseIsList, variant, expectedVersion: null, cancellationToken);
    }

    /// <summary>
    /// A pinned nightly build (#156, upstream 2.8.6): asked by its tag, so builds older than the listed 30 still install;
    /// the archive and extracted tree are cached per tag behind their <see cref="CacheStamp"/>, like every release.
    /// </summary>
    public Task<OptiScalerPackage> GetNightlyBuildAsync(string build, CancellationToken cancellationToken = default)
    {
        if (!IsNightlyBuild(build))
            throw new ArgumentException(OptiScalerDeploymentService.NightlyBuildInvalidPrefix + build, nameof(build));
        return FetchAsync(NightlyTagApiUrl(build), responseIsList: false, OptiScalerVariant.Nightly, expectedVersion: build, cancellationToken);
    }

    /// <summary>The newest nightly builds by date, newest first (the release list upstream's dropdown shows).</summary>
    public async Task<IReadOnlyList<string>> ListNightlyBuildsAsync(CancellationToken cancellationToken = default)
    {
        var json = await TrustedDownload.GetStringAsync(_http, NightlyReleasesApi, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub release-list response was not an array.");
        var builds = new List<string>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var build = NormalizeVersion(tag, OptiScalerVariant.Nightly);
            if (IsNightlyBuild(build) && !builds.Contains(build)) builds.Add(build);
        }
        return builds.OrderByDescending(build => build, StringComparer.Ordinal).Take(NightlyBuildListSize).ToArray();
    }

    private async Task<OptiScalerPackage> FetchAsync(
        string apiUrl,
        bool responseIsList,
        OptiScalerVariant variant,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var requiredExtension = variant is OptiScalerVariant.DlssNr or OptiScalerVariant.F5 ? ".zip" : ".7z";
        var json = await TrustedDownload.GetStringAsync(_http, apiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var release = SelectRelease(doc.RootElement, responseIsList);
        var rawTag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(rawTag))
            throw new InvalidOperationException($"GitHub release response did not contain an OptiScaler {variant} tag.");
        var version = NormalizeVersion(rawTag, variant);
        if (expectedVersion is not null && !string.Equals(version, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(NightlyBuildMismatchPrefix + rawTag);

        var candidates = new List<JsonElement>();
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNonRuntimeAssetName(name)) continue;
                if (variant == OptiScalerVariant.F5 && !name.StartsWith(F5AssetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(asset);
            }
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException($"OptiScaler {variant} {rawTag} does not publish a runtime {requiredExtension} asset that RHI Linux can install.");

        var selected = candidates
            .OrderByDescending(asset =>
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (variant == OptiScalerVariant.DlssNr && name.Contains("DLSSNR", StringComparison.OrdinalIgnoreCase)) return 2;
                return name.Contains("final", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            })
            .ThenByDescending(asset => asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0)
            .First();

        var assetName = selected.GetProperty("name").GetString()!;
        var sourceUrl = selected.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(sourceUrl))
            throw new InvalidOperationException("OptiScaler release asset is missing browser_download_url.");
        var expectedSize = selected.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
        var digest = TrustedDownload.NormalizeDigest(selected.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null);

        var variantRoot = variant == OptiScalerVariant.Stable
            ? _cacheDir
            : Path.Combine(_cacheDir, variant.ToString().ToLowerInvariant());
        var versionDir = Path.Combine(variantRoot, Sanitize(rawTag));
        var archive = Path.Combine(versionDir, assetName);
        var extractDir = Path.Combine(versionDir, "extract");
        Directory.CreateDirectory(versionDir);

        // Reuse the archive and its extracted tree only when this provider stamped them for exactly
        // this release asset (#142); anything else in the cache is replaced, never deployed. The stamp
        // re-verifies the archive's SHA-256 and that it recorded the published digest, so the digest is
        // not hashed a second time here.
        // A digest GitHub publishes is verified (a mismatch is refused); without one the archive is accepted on
        // HTTPS + the OptiScaler host policy and its SHA-256 is recorded in the stamp (#26, upstream verifies none).
        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, sourceUrl, archive, variant == OptiScalerVariant.F5 ? DownloadSources.OptiScalerF5 : DownloadSources.OptiScaler, rawTag, digest,
            validate: (temp, _) => HasPlausibleArchiveSize(temp, expectedSize)
                ? Task.CompletedTask
                : throw new InvalidOperationException("Downloaded OptiScaler archive failed size/SHA-256 validation."),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var archiveSha256 = download.Sha256;

        // The extracted tree's stamp records the SHA-256 of the archive it came from, so a tree
        // extracted from any other archive bytes is never reused, whatever order the calls ran in. The
        // bound form rejects a blank archive hash outright instead of skipping the comparison.
        var packageRoot = CacheStamp.IsValidBoundTo(extractDir, sourceUrl, rawTag, archiveSha256) ? FindPackageRoot(extractDir) : null;
        var freshlyExtracted = false;
        if (packageRoot is null)
        {
            CacheStamp.Invalidate(extractDir);
            // RHI's own cache folder: a stale tree that cannot be removed must not be extracted over.
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);
            await _extractor.ExtractAllAsync(archive, extractDir, cancellationToken).ConfigureAwait(false);
            NormalizeBackslashEntries(extractDir);
            packageRoot = FindPackageRoot(extractDir);
            freshlyExtracted = true;
        }

        if (packageRoot is null)
            throw new InvalidOperationException($"The official OptiScaler {variant} archive did not contain OptiScaler.dll and OptiScaler.ini in the same package root.");
        var dll = Path.Combine(packageRoot, "OptiScaler.dll");
        if (!BinarySignatureInspector.HasPeSignature(dll, 250_000) || !BinarySignatureInspector.LooksLikeOptiScaler(dll))
            throw new InvalidOperationException("The extracted OptiScaler.dll failed the local PE/signature-string sanity check.");
        if (NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant) && !File.Exists(Path.Combine(packageRoot, "nvngx.dll_dlssnr.dll")))
            throw new InvalidOperationException($"The OptiScaler {OptiScalerDeploymentService.VariantLabel(variant)} archive is missing its required nvngx.dll_dlssnr.dll forwarder.");

        if (freshlyExtracted) CacheStamp.WriteBoundTo(extractDir, sourceUrl, rawTag, archiveSha256);
        return new OptiScalerPackage(version, sourceUrl, packageRoot, variant, download);
    }

    private static JsonElement SelectRelease(JsonElement root, bool responseIsList)
    {
        if (!responseIsList)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("GitHub latest-release response was not an object.");
            return root;
        }

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub release-list response was not an array.");
        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            return release;
        }
        throw new InvalidOperationException("GitHub release list did not contain a usable release.");
    }

    internal static string NormalizeVersion(string tag, OptiScalerVariant variant)
    {
        var value = tag.Trim();
        if (variant == OptiScalerVariant.Nightly && value.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase))
            value = value["nightly-".Length..];
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value;
    }

    private static bool HasPlausibleArchiveSize(string path, long expectedSize)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return length >= 1_000_000 && (expectedSize <= 0 || length == expectedSize);
    }

    /// <summary>
    /// Windows-made zips may name entries with backslashes (<c>OptiScaler\libxess.dll</c>; F5 DLSSNR does), which 7-Zip
    /// and bsdtar on Linux extract as one flat file name. Such files are moved to the folders the names mean, as Windows
    /// tools extract them (upstream reads them on Windows). A name with an empty, <c>.</c> or <c>..</c> part, one that would
    /// leave the extraction folder, or one that collides with another entry refuses the whole archive.
    /// </summary>
    internal static void NormalizeBackslashEntries(string extractDirectory)
    {
        foreach (var file in SafeFileEnumeration.Files(extractDirectory).Where(path => Path.GetFileName(path).Contains('\\')).ToList())
        {
            var name = Path.GetFileName(file);
            var parts = name.Split('\\');
            if (parts.Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidOperationException(ArchivePathRefusedPrefix + name);
            var target = Path.GetFullPath(Path.Combine([Path.GetDirectoryName(file)!, .. parts]));
            if (!PathIdentity.IsSameOrInside(target, extractDirectory) || File.Exists(target) || Directory.Exists(target))
                throw new InvalidOperationException(ArchivePathRefusedPrefix + name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Rename(file, target); // inside RHI's own extraction folder: a rename on one file system (toolkit, #31 ratchet)
        }
    }

    internal static string? FindPackageRoot(string extractDirectory)
    {
        if (!Directory.Exists(extractDirectory)) return null;
        return SafeFileEnumeration.Files(extractDirectory, "OptiScaler.dll")
            .Select(path => Path.GetDirectoryName(path))
            .OfType<string>()
            .Where(path => File.Exists(Path.Combine(path, "OptiScaler.ini")))
            .OrderBy(path => Path.GetRelativePath(extractDirectory, path).Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
    }

    private static bool IsNonRuntimeAssetName(string name)
        => new[] { "debug", "symbols", "symbol", "source", "src", "backup" }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
}
