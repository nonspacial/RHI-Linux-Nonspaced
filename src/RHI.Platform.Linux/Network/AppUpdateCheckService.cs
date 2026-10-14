using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Network;

/// <summary>What an app update check found (no wording: the UI words it).</summary>
public enum AppUpdateCheckResult
{
    /// <summary>The newest published release is not newer than this build.</summary>
    UpToDate,

    /// <summary>A newer release is published; see <see cref="AppUpdateCheck.Latest"/>.</summary>
    UpdateAvailable,

    /// <summary>Neither repository publishes a release this build can compare with.</summary>
    NoReleases,

    /// <summary>GitHub could not be asked (offline, rate limit, unreadable answer).</summary>
    CouldNotCheck
}

/// <summary>A published release: its version text, the repository and its release page on github.com.</summary>
public sealed record AppRelease(string Version, string Repository, string PageUrl, bool IsPrerelease);

/// <summary>The outcome of a check, with the running version and the newest release found (when any).</summary>
public sealed record AppUpdateCheck(AppUpdateCheckResult Result, string CurrentVersion, AppRelease? Latest = null);

/// <summary>
/// RHI-Linux's own update check (#100; upstream <c>UpdateService</c>, which checks RankFTW/RHI's releases and offers its
/// installer). Owner decision: this fork's releases are checked and the user is only told — nothing is downloaded,
/// swapped or installed; the owner installs builds himself. The fork (<see cref="PrimaryRepository"/>) is asked first;
/// only when it publishes no release is the Linux port it comes from (<see cref="FallbackRepository"/>) asked. Release
/// lists are read under <see cref="DownloadSources.GitHubApi"/> (so the session's ETag cache and the GitHub token
/// apply); drafts are skipped and pre-releases count only while the running build is itself a pre-release (upstream
/// offers betas only to beta opt-ins; every build of this fork so far is a beta).
/// </summary>
public sealed class AppUpdateCheckService
{
    public const string PrimaryRepository = "nonspacial/RHI-Linux-Nonspaced";
    public const string FallbackRepository = "wizardkof/RHI-Linux";

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly IReadOnlyList<string> _repositories;

    /// <param name="currentVersion">Test seam (default: <see cref="RhiProductInfo.Version"/>).</param>
    /// <param name="repositories">Test seam (default: the fork, then the port it comes from).</param>
    public AppUpdateCheckService(HttpClient http, string? currentVersion = null, IReadOnlyList<string>? repositories = null)
    {
        _http = http;
        _currentVersion = currentVersion ?? RhiProductInfo.Version;
        _repositories = repositories ?? [PrimaryRepository, FallbackRepository];
    }

    public string CurrentVersion => _currentVersion;

    public static string ReleasesUrl(string repository) => $"https://api.github.com/repos/{repository}/releases?per_page=20";

    public async Task<AppUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = AppVersion.TryParse(_currentVersion);
        foreach (var repository in _repositories)
        {
            IReadOnlyList<AppRelease>? releases;
            try
            {
                releases = await ListAsync(repository, current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UntrustedDownloadException
                                           or InvalidDataException or JsonException or IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppLog.Warn("app-update", $"Could not read the releases of {repository}", ex);
                return new AppUpdateCheck(AppUpdateCheckResult.CouldNotCheck, _currentVersion);
            }
            if (releases is null || releases.Count == 0) continue; // this repository publishes nothing: ask the next

            var newest = releases.MaxBy(release => AppVersion.TryParse(release.Version)!, AppVersion.Comparer)!;
            var newer = current is null || AppVersion.Comparer.Compare(AppVersion.TryParse(newest.Version), current) > 0;
            return new AppUpdateCheck(newer ? AppUpdateCheckResult.UpdateAvailable : AppUpdateCheckResult.UpToDate, _currentVersion, newest);
        }
        return new AppUpdateCheck(AppUpdateCheckResult.NoReleases, _currentVersion);
    }

    /// <summary>The comparable releases of <paramref name="repository"/>; null when the repository does not exist (404).</summary>
    private async Task<IReadOnlyList<AppRelease>?> ListAsync(string repository, AppVersion? current, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await TrustedDownload.GetStringAsync(_http, ReleasesUrl(repository), DownloadSources.GitHubApi,
                accept: "application/vnd.github+json", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        return Parse(json, repository, includePrereleases: current is null || current.IsPrerelease);
    }

    internal static IReadOnlyList<AppRelease> Parse(string json, string repository, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub's release list is not a list.");
        var releases = new List<AppRelease>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft")) continue;
            var prerelease = Bool(release, "prerelease");
            if (prerelease && !includePrereleases) continue;
            var tag = String(release, "tag_name");
            var version = AppVersion.TryParse(tag) is not null ? tag! : String(release, "name");
            if (AppVersion.TryParse(version) is null) continue;
            releases.Add(new AppRelease(AppVersion.Display(version!), repository, PageUrl(repository, String(release, "html_url"), tag ?? version!), prerelease));
        }
        return releases;
    }

    /// <summary>The release page GitHub names, if it is on github.com under this repository; otherwise the tag's page.</summary>
    internal static string PageUrl(string repository, string? htmlUrl, string tag)
    {
        var prefix = $"https://github.com/{repository}/releases/";
        if (htmlUrl is not null && Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host == "github.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
            return uri.AbsoluteUri;
        return prefix + "tag/" + Uri.EscapeDataString(tag);
    }

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// An RHI-Linux version (<c>0.9.23.4-beta.1</c>, tags <c>v0.9.23.4-beta.1</c>): up to four numbers, then an optional
/// SemVer pre-release label. Ordered as SemVer orders them: numbers first, a release above any pre-release of the same
/// numbers, labels compared identifier by identifier (numbers numerically, below words; more identifiers above fewer).
/// </summary>
public sealed record AppVersion(int[] Numbers, string[] Label)
{
    private static readonly Regex Pattern = new(@"(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?", RegexOptions.CultureInvariant);

    public bool IsPrerelease => Label.Length > 0;

    public static IComparer<AppVersion?> Comparer { get; } = Comparer<AppVersion?>.Create(Compare);

    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Pattern.Match(text);
        if (!match.Success) return null;
        var numbers = new int[4];
        var parts = match.Groups[1].Value.Split('.');
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        var label = match.Groups[2].Success ? match.Groups[2].Value.Split('.') : [];
        return new AppVersion(numbers, label);
    }

    /// <summary>The version part of a tag or name (<c>v0.9.24-beta.1</c> → <c>0.9.24-beta.1</c>).</summary>
    public static string Display(string text) => Pattern.Match(text) is { Success: true } match ? match.Value : text.Trim();

    private static int Compare(AppVersion? a, AppVersion? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
        for (var i = 0; i < 4; i++)
            if (a.Numbers[i] != b.Numbers[i]) return a.Numbers[i].CompareTo(b.Numbers[i]);
        if (a.Label.Length == 0 || b.Label.Length == 0) return Math.Sign(b.Label.Length.CompareTo(a.Label.Length));
        for (var i = 0; i < Math.Min(a.Label.Length, b.Label.Length); i++)
        {
            var x = a.Label[i];
            var y = b.Label[i];
            var xNumeric = int.TryParse(x, out var xn);
            var yNumeric = int.TryParse(y, out var yn);
            var order = (xNumeric, yNumeric) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x, y)
            };
            if (order != 0) return order;
        }
        return a.Label.Length.CompareTo(b.Label.Length);
    }
}
