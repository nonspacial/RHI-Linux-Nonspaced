using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Network;

/// <summary>Outcome of a GitHub token check (no wording: the UI words it).</summary>
public enum GitHubTokenCheck
{
    /// <summary>GitHub accepted the token.</summary>
    Valid,

    /// <summary>GitHub answered 401: revoked, expired or mistyped. Nothing was saved.</summary>
    Rejected,

    /// <summary>Not a token (blank, too long, whitespace inside). Nothing was sent.</summary>
    Malformed,

    /// <summary>GitHub could not be asked or gave another answer (offline, rate limit, server error). Nothing was saved.</summary>
    CouldNotCheck,

    /// <summary>No token is saved.</summary>
    NoToken,

    /// <summary>The saved file is readable by other users and could not be restricted: it is not used.</summary>
    FileExposed,

    /// <summary>The token was valid but the file could not be written (logged).</summary>
    SaveFailed
}

/// <summary>A token check: the result and, when GitHub answered, the hourly core limit and what is left of it.</summary>
public sealed record GitHubTokenStatus(GitHubTokenCheck Result, int? Limit = null, int? Remaining = null);

/// <summary>
/// Settings → paste, check and clear the GitHub token (upstream 2.8.6 <c>2975485</c> PAT part, <c>128bc14</c>
/// validate-before-apply). A pasted token is checked with one <c>GET https://api.github.com/rate_limit</c> under
/// <see cref="DownloadSources.GitHubApi"/> and saved only when GitHub accepts it, to the same <c>github_api.txt</c>
/// <see cref="GitHubApiTokenHandler"/> reads, atomically at mode 0600 (<see cref="SecretFileStore"/>); Clear deletes the
/// file. The check runs on its own client, outside the app's ETag cache (a cached answer must never vouch for another
/// token) and outside the token handler (it sends the candidate, not the saved token). The token is never logged,
/// echoed or put in a message. GitHub device-flow sign-in stays parked (#134).
/// </summary>
public sealed class GitHubTokenSettingsService
{
    public const string RateLimitUrl = "https://api.github.com/rate_limit";

    /// <summary>The token file the app's <see cref="GitHubApiTokenHandler"/> reads.</summary>
    public static string DefaultTokenPath => Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "github_api.txt");

    private readonly HttpClient _http;
    private readonly SecretFileStore _store;

    /// <param name="http">A client without the app's GitHub cache/token handlers.</param>
    public GitHubTokenSettingsService(HttpClient http, SecretFileStore store)
    {
        _http = http;
        _store = store;
    }

    public string TokenPath => _store.FilePath;

    /// <summary>Whether a token file is saved.</summary>
    public bool HasSavedToken => _store.Exists;

    /// <summary>Checks <paramref name="token"/> with GitHub and saves it only when GitHub accepts it.</summary>
    public async Task<GitHubTokenStatus> SaveAsync(string? token, CancellationToken cancellationToken = default)
    {
        var candidate = token?.Trim();
        if (!SecretFileStore.IsWellFormed(candidate)) return new GitHubTokenStatus(GitHubTokenCheck.Malformed);
        var status = await CheckAsync(candidate!, cancellationToken).ConfigureAwait(false);
        if (status.Result != GitHubTokenCheck.Valid) return status;
        try
        {
            _store.Write(candidate!);
            return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("github-token", $"Could not save the GitHub token file {_store.FilePath}", ex);
            return status with { Result = GitHubTokenCheck.SaveFailed };
        }
    }

    /// <summary>Checks the saved token with GitHub.</summary>
    public async Task<GitHubTokenStatus> CheckSavedAsync(CancellationToken cancellationToken = default)
    {
        var read = _store.Read();
        return read.State switch
        {
            SecretFileState.Missing => new GitHubTokenStatus(GitHubTokenCheck.NoToken),
            SecretFileState.Exposed => new GitHubTokenStatus(GitHubTokenCheck.FileExposed),
            SecretFileState.Malformed => new GitHubTokenStatus(GitHubTokenCheck.Malformed),
            SecretFileState.Unreadable => new GitHubTokenStatus(GitHubTokenCheck.CouldNotCheck),
            _ => await CheckAsync(read.Value!, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>Deletes the saved token; true when one was removed.</summary>
    public bool Clear() => _store.Delete();

    private async Task<GitHubTokenStatus> CheckAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadSources.GitHubApi.Validate(RateLimitUrl));
            request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(RhiProductInfo.UserAgent));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // A redirect off api.github.com (never expected) proves nothing about the token.
            DownloadSources.GitHubApi.ValidateFinal(response, RateLimitUrl);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new GitHubTokenStatus(GitHubTokenCheck.Rejected);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                AppLog.Warn("github-token", $"GitHub answered {(int)response.StatusCode} to the token check; nothing was saved");
                return new GitHubTokenStatus(GitHubTokenCheck.CouldNotCheck);
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (limit, remaining) = ReadCoreLimit(body);
            return new GitHubTokenStatus(GitHubTokenCheck.Valid, limit, remaining);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UntrustedDownloadException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppLog.Warn("github-token", "Could not reach GitHub to check the token; nothing was saved", ex);
            return new GitHubTokenStatus(GitHubTokenCheck.CouldNotCheck);
        }
    }

    /// <summary>The hourly core limit and remaining calls from a <c>/rate_limit</c> body (nulls when absent or unreadable).</summary>
    internal static (int? Limit, int? Remaining) ReadCoreLimit(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement core;
            if (!(root.TryGetProperty("resources", out var resources) && resources.TryGetProperty("core", out core))
                && !root.TryGetProperty("rate", out core))
                return (null, null);
            int? Read(string name) => core.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : null;
            return (Read("limit"), Read("remaining"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
