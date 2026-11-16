using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <param name="Download">The verified release archive the package was extracted from (record metadata, #26); null when not downloaded through the provider.</param>
public sealed record OptiScalerPackage(
    string Version,
    string SourceUrl,
    string RootDirectory,
    OptiScalerVariant Variant = default,
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

    /// <summary>#270: the releases of Stable, DLSS NR or F5 that publish a package RHI installs, newest first (metadata only).</summary>
    Task<IReadOnlyList<OptiScalerRelease>> ListReleasesAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<OptiScalerRelease>>(new NotSupportedException("OptiScaler package provider does not list releases."));

    /// <summary>#270: one release of Stable, DLSS NR or F5 by its tag (a pinned game), downloaded and cached per tag.</summary>
    Task<OptiScalerPackage> GetReleaseAsync(OptiScalerVariant variant, string tag, CancellationToken cancellationToken = default)
        => Task.FromException<OptiScalerPackage>(new NotSupportedException("OptiScaler package provider does not offer pinned releases."));
}

/// <summary>One release in a channel's list (#270): its tag, the version RHI records for it, and when it was published.</summary>
public sealed record OptiScalerRelease(string Tag, string Version, DateTimeOffset Published);

public sealed partial class OptiScalerPackageProvider : IOptiScalerPackageProvider
{
    public const string LatestReleaseApi = "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest";
    public const string NightlyReleasesApi = "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases";
    public const string DlssNrReleasesApi = "https://api.github.com/repos/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass/releases";
    /// <summary>F5 DLSSNR (#157): GitHub's "Latest" release, as upstream; its package is chosen by <see cref="SelectF5AssetName"/>.</summary>
    public const string F5ReleasesApi = "https://api.github.com/repos/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/latest";
    /// <summary>#260: an F5 release with more than one package zip is refused instead of guessed; the asset names follow.</summary>
    public const string F5AmbiguousAssetPrefix = "The OptiScaler F5 DLSSNR release publishes more than one package, so RHI did not choose one: ";
    /// <summary>
    /// w13 final review m4: a release with no package RHI can install (fixed text first; "OptiScaler &lt;variant&gt;
    /// &lt;tag&gt; (&lt;extension&gt;)" follows), so it has a PT-BR presentation pair.
    /// </summary>
    public const string NoRuntimeAssetPrefix = "This OptiScaler release publishes no package RHI Linux can install: ";
    public const string ArchivePathRefusedPrefix = "The OptiScaler archive holds a path RHI will not extract: ";

    /// <summary>#231: the F5 package's forwarder is not a Windows DLL (MZ + PE header); refused before any game file changes.</summary>
    public const string F5ForwarderNotPeMessage = "The OptiScaler F5 DLSSNR package's nvngx.dll_dlssnr.dll is not a Windows DLL, so nothing was installed.";

    /// <summary>#231: the F5 package has no OptiScaler/ backend folder; refused before any game file changes.</summary>
    public const string F5BackendMissingMessage = "The OptiScaler F5 DLSSNR package has no OptiScaler folder with its helper files, so nothing was installed.";
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

    /// <summary>#271: a fork's release asset that is not under that fork's own GitHub releases (the link follows).</summary>
    public const string ForkAssetElsewherePrefix = "The OptiScaler fork's download is not one of its own GitHub releases, so it was not used: ";

    /// <summary>#270: a pinned release that GitHub answered with another tag (the asked tag follows).</summary>
    public const string ReleaseMismatchPrefix = "GitHub returned another OptiScaler release than the one pinned: ";
    /// <summary>#270: Nightly has dated builds (<see cref="GetNightlyBuildAsync"/>), not a release pin.</summary>
    public const string ReleasePinNotOfferedPrefix = "OptiScaler has no release list for this version: ";

    /// <summary>The GitHub repository a channel's releases come from (Stable, DLSS NR, F5); null for Nightly.</summary>
    public static string? ReleaseRepository(OptiScalerVariant variant)
        => variant == OptiScalerVariant.Stable ? "optiscaler/OptiScaler"
            : variant == OptiScalerVariant.DlssNr ? "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass"
            : variant == OptiScalerVariant.F5 ? "janblade/OptiScaler-F5-DLSSNR-Multipass"
            : OptiScalerForks.Get(variant)?.Repository; // a custom build has no releases (#271: never updated)

    /// <summary>The release list (GitHub's newest 30). DLSS NR's is the list the latest install already reads.</summary>
    public static string ReleaseListApiUrl(OptiScalerVariant variant)
        => variant == OptiScalerVariant.DlssNr
            ? DlssNrReleasesApi
            : $"https://api.github.com/repos/{ReleaseRepository(variant) ?? throw new ArgumentException(ReleasePinNotOfferedPrefix + variant, nameof(variant))}/releases";

    public static string ReleaseTagApiUrl(OptiScalerVariant variant, string tag)
        => $"https://api.github.com/repos/{ReleaseRepository(variant) ?? throw new ArgumentException(ReleasePinNotOfferedPrefix + variant, nameof(variant))}/releases/tags/{Uri.EscapeDataString(tag)}";

    /// <summary>A tag RHI asks GitHub for: letters, digits and <c>. _ + -</c>, starting with a letter or digit.</summary>
    public static bool IsSafeReleaseTag(string? tag)
        => tag is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(tag[0]) && tag.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-');

    /// <summary>
    /// #270 "Remove old Nightly builds" (off by default): after a Nightly download, the builds to keep besides the newest
    /// (those games are pinned to), or null when the setting is off.
    /// </summary>
    public Func<IReadOnlyCollection<string>?>? NightlyCleanupKeep { get; set; }

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

    public async Task<OptiScalerPackage> GetLatestAsync(
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
    {
        // #271: a fork's newest release with a package (prereleases included, as upstream), then that release by its tag.
        if (OptiScalerForks.IsFork(variant))
        {
            var newest = (await ListReleasesAsync(variant, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                         ?? throw new InvalidOperationException($"{NoRuntimeAssetPrefix}OptiScaler {VariantName(variant)}");
            return await GetReleaseAsync(variant, newest.Tag, cancellationToken).ConfigureAwait(false);
        }
        // #271: a custom build is the copy in RHI's data folder; nothing is downloaded and it is never updated.
        if (variant.IsCustom) return CustomBuildPackage(variant);
        var (apiUrl, responseIsList) =
            variant == OptiScalerVariant.Stable ? (LatestReleaseApi, false)
            : variant == OptiScalerVariant.Nightly ? (NightlyReleasesApi, true)
            : variant == OptiScalerVariant.DlssNr ? (DlssNrReleasesApi, true)
            : variant == OptiScalerVariant.F5 ? (F5ReleasesApi, false)
            : throw new InvalidOperationException(UnknownVersionPrefix + variant.Id);
        return await FetchAsync(apiUrl, responseIsList, variant, expectedVersion: null, cancellationToken).ConfigureAwait(false);
    }

    private static string VariantName(OptiScalerVariant variant) => OptiScalerVersions.Label(variant);

    /// <summary>#271: a version this RHI does not know (a fork the manifest never listed, a removed custom build); the id follows.</summary>
    public const string UnknownVersionPrefix = "This OptiScaler version is not available in RHI any more: ";

    /// <summary>
    /// #271: the package of a custom build: its folder in RHI's data folder, recorded under its DLL version and origin. Its
    /// OptiScaler.dll must still be a Windows DLL; an NR-ticked build is installed with nvngx_dlssnr.dll like DLSS NR.
    /// </summary>
    internal static OptiScalerPackage CustomBuildPackage(OptiScalerVariant variant)
    {
        var build = OptiScalerCustomBuilds.Get(variant) ?? throw new InvalidOperationException(UnknownVersionPrefix + variant.Id);
        if (!BinarySignatureInspector.HasPeSignature(Path.Combine(build.Directory, "OptiScaler.dll"), 64 * 1024))
            throw new InvalidOperationException(OptiScalerCustomBuildStore.NotWindowsDllMessage);
        return new OptiScalerPackage(build.RecordedVersion, build.Origin, build.Directory, variant);
    }

    /// <summary>A package asset name for <paramref name="variant"/>: the built-ins' rules, or upstream's ScoreAsset for a fork.</summary>
    private static bool IsPackageCandidate(string name, OptiScalerVariant variant)
    {
        if (OptiScalerForks.Get(variant) is { } fork) return OptiScalerForks.IsPackageAsset(fork, name);
        var requiredExtension = variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5 ? ".zip" : ".7z";
        return name.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase)
               && name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase) && !IsNonRuntimeAssetName(name);
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

    /// <summary>
    /// #270 (upstream 2.9.2 <c>OsReleaseService.ListReleases</c>): non-draft releases (Stable: no prereleases, as upstream)
    /// that publish a package RHI would install, newest by publish date first.
    /// </summary>
    public async Task<IReadOnlyList<OptiScalerRelease>> ListReleasesAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
    {
        var url = ReleaseListApiUrl(variant);
        var json = await TrustedDownload.GetStringAsync(_http, url, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub release-list response was not an array.");
        var releases = new List<OptiScalerRelease>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            // Stable takes no prereleases (upstream); a fork takes them unless its manifest entry says includePrereleases: false.
            var skipPrereleases = variant == OptiScalerVariant.Stable || OptiScalerForks.Get(variant) is { IncludePrereleases: false };
            if (skipPrereleases && release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (!IsSafeReleaseTag(tag) || releases.Any(known => known.Tag == tag)) continue;
            if (!HasInstallablePackage(release, variant, tag!)) continue;
            var published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : DateTimeOffset.MinValue;
            releases.Add(new OptiScalerRelease(tag!, NormalizeVersion(tag!, variant), published));
        }
        return releases.OrderByDescending(release => release.Published).ToArray();
    }

    /// <summary>#270: a pinned release, asked by its tag (never "latest"), cached per tag like every release.</summary>
    public Task<OptiScalerPackage> GetReleaseAsync(OptiScalerVariant variant, string tag, CancellationToken cancellationToken = default)
    {
        if (ReleaseRepository(variant) is null) throw new ArgumentException(ReleasePinNotOfferedPrefix + variant, nameof(variant));
        if (!IsSafeReleaseTag(tag)) throw new ArgumentException(ReleaseMismatchPrefix + tag, nameof(tag));
        return FetchAsync(ReleaseTagApiUrl(variant, tag), responseIsList: false, variant, expectedVersion: NormalizeVersion(tag, variant), cancellationToken);
    }

    private static bool HasInstallablePackage(JsonElement release, OptiScalerVariant variant, string tag)
    {
        var names = PackageCandidateNames(release, variant);
        if (variant != OptiScalerVariant.F5) return names.Count > 0;
        try { SelectF5AssetName(names, tag); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static List<string> PackageCandidateNames(JsonElement release, OptiScalerVariant variant)
    {
        var names = new List<string>();
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return names;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name) && IsPackageCandidate(name, variant)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// #270 (upstream 2.9.2 <c>RemoveOldNightlyBuilds</c>): deletes cached Nightly builds except the newest and
    /// <paramref name="keep"/> (build dates games are pinned to). Only <c>nightly-YYYYMMDD</c> folders are touched; a folder
    /// that cannot be deleted is logged and kept. Returns how many were removed and their size.
    /// </summary>
    public (int Removed, long Bytes) RemoveOldNightlyBuilds(IReadOnlyCollection<string> keep)
    {
        var root = Path.Combine(_cacheDir, OptiScalerVariant.Nightly.CacheFolder);
        if (!Directory.Exists(root)) return (0, 0);
        var builds = Directory.GetDirectories(root)
            .Select(dir => (Dir: dir, Build: Path.GetFileName(dir).StartsWith(NightlyTagPrefix, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(dir)[NightlyTagPrefix.Length..] : ""))
            .Where(entry => IsNightlyBuild(entry.Build))
            .ToArray();
        var newest = builds.Select(entry => entry.Build).OrderByDescending(build => build, StringComparer.Ordinal).FirstOrDefault();
        var removed = 0;
        long bytes = 0;
        foreach (var (dir, build) in builds)
        {
            if (build == newest || keep.Contains(build, StringComparer.Ordinal)) continue;
            try
            {
                var size = SafeFileEnumeration.Files(dir).Sum(file => new FileInfo(file).Length);
                Directory.Delete(dir, recursive: true); // RHI's own download cache, never a game folder
                removed++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("optiscaler", $"An old OptiScaler Nightly build could not be removed from the cache: {dir}", ex);
            }
        }
        if (removed > 0) AppLog.Current?.Write("optiscaler", $"Removed {removed} old OptiScaler Nightly build(s) ({bytes / (1024 * 1024)} MB); kept {newest} and [{string.Join(", ", keep)}].");
        return (removed, bytes);
    }

    private void CleanUpNightlies(string fetched)
    {
        try
        {
            if (NightlyCleanupKeep?.Invoke() is not { } keep) return;
            RemoveOldNightlyBuilds([.. keep, fetched]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("optiscaler", "Old OptiScaler Nightly builds could not be cleaned up", ex);
        }
    }

    private async Task<OptiScalerPackage> FetchAsync(
        string apiUrl,
        bool responseIsList,
        OptiScalerVariant variant,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var requiredExtension = OptiScalerForks.IsFork(variant) ? ".7z/.zip" : variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5 ? ".zip" : ".7z";
        var json = await TrustedDownload.GetStringAsync(_http, apiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var release = SelectRelease(doc.RootElement, responseIsList);
        var rawTag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(rawTag))
            throw new InvalidOperationException($"GitHub release response did not contain an OptiScaler {VariantName(variant)} tag.");
        var version = NormalizeVersion(rawTag, variant);
        if (expectedVersion is not null && !string.Equals(version, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException((variant == OptiScalerVariant.Nightly ? NightlyBuildMismatchPrefix : ReleaseMismatchPrefix) + rawTag);

        var candidates = new List<JsonElement>();
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !IsPackageCandidate(name, variant)) continue;
                candidates.Add(asset);
            }
        }
        if (variant == OptiScalerVariant.F5)
        {
            var f5 = SelectF5AssetName(candidates.Select(asset => asset.GetProperty("name").GetString()!).ToArray(), rawTag);
            candidates = [candidates.First(asset => asset.GetProperty("name").GetString() == f5)];
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException($"{NoRuntimeAssetPrefix}OptiScaler {VariantName(variant)} {rawTag} ({requiredExtension})");

        var selected = candidates
            .OrderByDescending(asset => OptiScalerForks.IsFork(variant) ? OptiScalerForks.ScoreAsset(asset.GetProperty("name").GetString() ?? "") : 0)
            .ThenByDescending(asset => OptiScalerForks.IsFork(variant) && asset.TryGetProperty("updated_at", out var updated) ? updated.GetString() ?? "" : "", StringComparer.Ordinal)
            .ThenByDescending(asset =>
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
        // #271: a fork downloads only from its own repository's GitHub releases.
        if (OptiScalerForks.Get(variant) is { } fork
            && !sourceUrl.StartsWith($"https://github.com/{fork.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(ForkAssetElsewherePrefix + sourceUrl);
        var expectedSize = selected.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
        var digest = TrustedDownload.NormalizeDigest(selected.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null);

        var variantRoot = variant == OptiScalerVariant.Stable
            ? _cacheDir
            : Path.Combine(_cacheDir, variant.CacheFolder);
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
            throw new InvalidOperationException($"The official OptiScaler {VariantName(variant)} archive did not contain OptiScaler.dll and OptiScaler.ini in the same package root.");
        var dll = Path.Combine(packageRoot, "OptiScaler.dll");
        if (!BinarySignatureInspector.HasPeSignature(dll, 250_000) || !BinarySignatureInspector.LooksLikeOptiScaler(dll))
            throw new InvalidOperationException("The extracted OptiScaler.dll failed the local PE/signature-string sanity check.");
        if ((variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5) && !File.Exists(Path.Combine(packageRoot, "nvngx.dll_dlssnr.dll")))
            throw new InvalidOperationException($"The OptiScaler {OptiScalerDeploymentService.VariantLabel(variant)} archive is missing its required nvngx.dll_dlssnr.dll forwarder.");
        // #231 (wizardkof v0.9.25.0-beta.1 ValidatePackageRoot): F5's forwarder must be a real Windows DLL, and the package must
        // hold its OptiScaler/ backend folder (helper DLLs, after the backslash names are rebuilt); else nothing is installed.
        if (variant == OptiScalerVariant.F5)
        {
            if (PeArchitectureDetector.Detect(Path.Combine(packageRoot, "nvngx.dll_dlssnr.dll")) == RHI.Core.Models.PeArchitecture.Unknown)
                throw new InvalidOperationException(F5ForwarderNotPeMessage);
            if (!Directory.EnumerateDirectories(packageRoot).Any(dir => Path.GetFileName(dir).Equals("OptiScaler", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(F5BackendMissingMessage);
        }

        if (freshlyExtracted) CacheStamp.WriteBoundTo(extractDir, sourceUrl, rawTag, archiveSha256);
        if (variant == OptiScalerVariant.Nightly) CleanUpNightlies(version);
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

    /// <summary>
    /// #260: the F5 release's package. Its name has changed (<c>OptiScaler-DLSSNR-F5-0.1.30.zip</c> up to v0.1.36,
    /// <c>v0.1.37-Optiscaler-F5.zip</c> since; upstream 2.9.1 <c>1f8a32e</c> now takes any zip naming OptiScaler), so the
    /// package is the one <c>.zip</c> whose name has "OptiScaler" and "F5" as a word of its own, does not start with
    /// "Optional" and is no forwarder, debug, symbol or source build. Companions ("Optional-Vendor-Neutral-DLSS5-Forwarder.zip",
    /// "Optional-F5Low-Update-….zip") are never taken; no package, or more than one, is refused (upstream takes the first).
    /// </summary>
    public static string SelectF5AssetName(IReadOnlyList<string> assetNames, string releaseTag)
    {
        var packages = assetNames
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase)
                && F5Word().IsMatch(name)
                && !name.StartsWith("Optional", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("forwarder", StringComparison.OrdinalIgnoreCase)
                && !IsNonRuntimeAssetName(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return packages.Length switch
        {
            1 => packages[0],
            0 => throw new InvalidOperationException($"{NoRuntimeAssetPrefix}OptiScaler F5 {releaseTag} (.zip)"),
            _ => throw new InvalidOperationException(F5AmbiguousAssetPrefix + string.Join(", ", packages))
        };
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])F5(?![A-Za-z0-9])", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex F5Word();

    private static bool IsNonRuntimeAssetName(string name)
        => new[] { "debug", "symbols", "symbol", "source", "src", "backup" }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
}
