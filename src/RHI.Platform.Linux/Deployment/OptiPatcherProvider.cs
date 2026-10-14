using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One OptiPatcher build: the rolling release's build commit and the verified cache entry of its <c>.asi</c>.</summary>
public sealed record OptiPatcherBuild(string Version, string SourceUrl, VerifiedDownload Download);

/// <summary>The newest OptiPatcher build as the release names it (metadata only; nothing downloaded).</summary>
public sealed record OptiPatcherRelease(string Version, string SourceUrl, string? PublishedDigest);

/// <summary>
/// The newest OptiPatcher (#98); OptiScaler deploys it beside itself. Split in two so an update check reads the
/// release's build and digest only (<see cref="ResolveLatestAsync"/>, as upstream's <c>CheckOptiPatcherUpdateAsync</c>)
/// and the <c>.asi</c> is downloaded only by an install or update (<see cref="FetchAsync"/>).
/// </summary>
public interface IOptiPatcherProvider
{
    /// <summary>The newest build's version, link and published digest (one API request; never the file).</summary>
    Task<OptiPatcherRelease> ResolveLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads (or reuses the stamped cache entry of) <paramref name="release"/>'s <c>.asi</c>, verified.</summary>
    Task<OptiPatcherBuild> FetchAsync(OptiPatcherRelease release, CancellationToken cancellationToken = default);
}

/// <summary>
/// OptiPatcher (upstream 2.8.7 <c>OptiScalerService.EnsureOptiPatcherStagingAsync</c>, #98): the <c>rolling</c> release
/// of <c>optiscaler/OptiPatcher</c> on GitHub. The build is the "Build commit" its release notes name (as upstream; the
/// asset digest when the notes name none); the single <c>OptiPatcher.asi</c> asset is cached per build through
/// <see cref="TrustedDownload.EnsureVerifiedAsync"/> (GitHub's published SHA-256 checked, stamp reused only for the same
/// build) and must be a 64-bit Windows DLL. Metadata through <see cref="DownloadSources.GitHubApi"/>, the file through
/// <see cref="DownloadSources.OptiPatcher"/>.
/// </summary>
public sealed class OptiPatcherProvider : IOptiPatcherProvider
{
    public const string ReleaseApiUrl = "https://api.github.com/repos/optiscaler/OptiPatcher/releases/tags/rolling";
    public const string FileName = "OptiPatcher.asi";
    public const string ReleaseIncompleteMessage = "The OptiPatcher rolling release lists no OptiPatcher.asi download.";
    public const string NotWindowsDllMessage = "The downloaded OptiPatcher.asi is not a 64-bit Windows DLL.";

    private static readonly Regex BuildCommit = new(@"\*{0,2}Build commit:\*{0,2}\s*([0-9a-f]{7,40})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public OptiPatcherProvider(HttpClient http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
    }

    public async Task<OptiPatcherRelease> ResolveLatestAsync(CancellationToken cancellationToken = default)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ReleaseApiUrl, DownloadSources.GitHubApi, accept: "application/vnd.github+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var (version, url, digest) = ParseRelease(json);
        return new OptiPatcherRelease(version, url, digest);
    }

    public async Task<OptiPatcherBuild> FetchAsync(OptiPatcherRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var entry = Path.Combine(_cacheDir, release.Version, FileName);
        var verified = await TrustedDownload.EnsureVerifiedAsync(_http, release.SourceUrl, entry, DownloadSources.OptiPatcher, release.Version,
            release.PublishedDigest,
            validate: (path, _) => PeArchitectureDetector.Detect(path) == PeArchitecture.X64
                ? Task.CompletedTask
                : Task.FromException(new InvalidDataException(NotWindowsDllMessage)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new OptiPatcherBuild(release.Version, release.SourceUrl, verified);
    }

    /// <summary>The newest build, downloaded and verified (<see cref="ResolveLatestAsync"/> then <see cref="FetchAsync"/>).</summary>
    public async Task<OptiPatcherBuild> GetLatestAsync(CancellationToken cancellationToken = default)
        => await FetchAsync(await ResolveLatestAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>The build (lower-case commit, else the asset digest's first 12 hex), the asset link and its digest.</summary>
    internal static (string Version, string Url, string? Digest) ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? url = null, digest = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!(asset.TryGetProperty("name", out var name) && string.Equals(name.GetString(), FileName, StringComparison.OrdinalIgnoreCase))) continue;
                url = asset.TryGetProperty("browser_download_url", out var link) ? link.GetString() : null;
                digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException(ReleaseIncompleteMessage);

        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var commit = BuildCommit.Match(body);
        var hex = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : digest;
        var version = commit.Success
            ? commit.Groups[1].Value.ToLowerInvariant()
            : hex is { Length: >= 12 } && hex.All(Uri.IsHexDigit) ? "sha256-" + hex[..12].ToLowerInvariant()
            : throw new InvalidDataException(ReleaseIncompleteMessage);
        return (version, url, digest);
    }
}
