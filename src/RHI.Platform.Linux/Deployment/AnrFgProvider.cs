using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A downloaded, verified ANR FG release: its tag and the extracted provider folder (<c>Apex Neural Rendering FG</c>).</summary>
public sealed record AnrFgPackage(string Tag, string ProviderDirectory, VerifiedDownload Download);

public interface IAnrFgProvider
{
    Task<AnrFgPackage> GetLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// #272 ANR FG (Apex Neural Rendering frame generation, upstream 2.9.3 <c>AnrFgService</c>): the newest release of
/// apex-compute/ANR, its <c>*-addon.zip</c> only, from that repository's GitHub releases. The zip is checked against the
/// digest GitHub publishes and against the release's own <c>SHA256SUMS.txt</c>; at least one must vouch for it and none
/// may disagree. Cached per tag behind a stamp bound to the zip's hash.
/// </summary>
public sealed class AnrFgProvider : IAnrFgProvider
{
    public const string Repository = "apex-compute/ANR";
    public const string LatestReleaseApi = "https://api.github.com/repos/apex-compute/ANR/releases/latest";
    /// <summary>The provider's folder name, which is also its <c>FGNvngxReplacement</c> value.</summary>
    public const string ProviderName = "Apex Neural Rendering FG";
    public const string ProviderDllRelativePath = "dlssg/nvngx_dlssg.dll";
    /// <summary>ANR's own settings; a copy the user has is kept (upstream).</summary>
    public const string UserIniRelativePath = "dlssg/nr_fg.ini";
    private const string AddonSuffix = "-addon.zip";
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    public const string NoAddonPrefix = "The ANR FG release has no add-on package RHI can install: ";
    public const string NotVerifiedPrefix = "The ANR FG download could not be checked against a published hash, so it was not used: ";
    public const string ChecksumMismatchPrefix = "The ANR FG download does not match the release's SHA256SUMS.txt, so it was not used: ";
    public const string AssetElsewherePrefix = "The ANR FG download is not one of apex-compute/ANR's GitHub releases, so it was not used: ";
    public const string LayoutPrefix = "The ANR FG package does not hold the provider RHI installs: ";

    private readonly HttpClient _http;
    private readonly IOptiScalerArchiveExtractor _extractor;
    private readonly string _cacheDir;

    public AnrFgProvider(HttpClient http, IOptiScalerArchiveExtractor extractor, string? cacheDir = null)
    {
        _http = http;
        _extractor = extractor;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "anr-fg");
    }

    public async Task<AnrFgPackage> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var json = await TrustedDownload.GetStringAsync(_http, LatestReleaseApi, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!OptiScalerPackageProvider.IsSafeReleaseTag(tag)) throw new InvalidOperationException(NoAddonPrefix + tag);
        string? zipUrl = null, zipName = null, digest = null, sumsUrl = null;
        long size = 0;
        var addons = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null) continue;
                if (name.EndsWith(AddonSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    addons++;
                    (zipName, zipUrl) = (name, url);
                    digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
            }
        if (addons != 1 || zipUrl is null || zipName is null) throw new InvalidOperationException(NoAddonPrefix + tag);
        foreach (var url in new[] { zipUrl, sumsUrl }.OfType<string>())
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(AssetElsewherePrefix + url);

        // The release's own list (upstream checks it); GitHub's digest is checked by the download itself.
        string? listed = null;
        if (sumsUrl is not null)
        {
            var sums = await TrustedDownload.GetStringAsync(_http, sumsUrl, DownloadSources.AnrFg, "text/plain", cancellationToken).ConfigureAwait(false);
            listed = ListedSha256(sums, zipName);
        }
        var published = TrustedDownload.NormalizeDigest(digest);
        if (published is null && listed is null) throw new InvalidOperationException(NotVerifiedPrefix + zipName);
        if (published is not null && listed is not null && !FileHash.HexEquals(published, listed))
            throw new InvalidOperationException(ChecksumMismatchPrefix + zipName);

        var versionDir = Path.Combine(_cacheDir, string.Concat(tag!.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')));
        Directory.CreateDirectory(versionDir);
        var archive = Path.Combine(versionDir, Path.GetFileName(zipName));
        var download = await TrustedDownload.EnsureVerifiedAsync(_http, zipUrl, archive, DownloadSources.AnrFg, tag, published ?? listed,
            validate: (temp, _) => size <= 0 || new FileInfo(temp).Length == size
                ? Task.CompletedTask
                : throw new InvalidOperationException(ChecksumMismatchPrefix + zipName),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listed is not null && !FileHash.HexEquals(download.Sha256, listed))
            throw new InvalidOperationException(ChecksumMismatchPrefix + zipName);

        var extract = Path.Combine(versionDir, "extract");
        var provider = CacheStamp.IsValidBoundTo(extract, zipUrl, tag, download.Sha256) ? FindProvider(extract) : null;
        if (provider is null)
        {
            CacheStamp.Invalidate(extract);
            if (Directory.Exists(extract)) Directory.Delete(extract, recursive: true); // RHI's own cache folder
            Directory.CreateDirectory(extract);
            await _extractor.ExtractAllAsync(archive, extract, cancellationToken).ConfigureAwait(false);
            OptiScalerPackageProvider.NormalizeBackslashEntries(extract);
            provider = FindProvider(extract) ?? throw new InvalidOperationException(LayoutPrefix + zipName);
            CacheStamp.WriteBoundTo(extract, zipUrl, tag, download.Sha256);
        }
        return new AnrFgPackage(tag!, provider, download);
    }

    /// <summary>The hash <c>SHA256SUMS.txt</c> lists for <paramref name="fileName"/> (<c>&lt;hex&gt;  &lt;name&gt;</c>, a <c>*</c> before the name allowed), or null.</summary>
    public static string? ListedSha256(string sums, string fileName)
        => sums.Split('\n')
            .Select(line => line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*').Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase)
                            && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            .Select(parts => parts[0])
            .FirstOrDefault();

    /// <summary>The <c>Apex Neural Rendering FG</c> folder whose <c>dlssg/nvngx_dlssg.dll</c> is a Windows DLL (links never followed).</summary>
    internal static string? FindProvider(string extractDirectory)
        => SafeFileEnumeration.Directories(extractDirectory)
            .Where(dir => Path.GetFileName(dir).Equals(ProviderName, StringComparison.Ordinal))
            .FirstOrDefault(dir => BinarySignatureInspector.HasPeSignature(Path.Combine(dir, ProviderDllRelativePath), 64 * 1024));
}
