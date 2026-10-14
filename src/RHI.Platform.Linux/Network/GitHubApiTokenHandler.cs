using System.Net;
using System.Net.Http.Headers;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Adds an optional user-supplied GitHub API token only to requests whose host is exactly
/// api.github.com. The token is never attached to raw content, release asset CDNs or
/// unrelated services that share the application's HttpClient.
/// A token GitHub answers with 401 (revoked/expired) is dropped for the rest of the session,
/// and the request is replayed anonymously when that is safe, until the token file is
/// rewritten. The token file must be private to the user (mode 0600): a file other users can
/// read or write is restricted to 0600 before the token is used, and when that is impossible
/// the token is not used at all. The file's contents are never modified and the token is never logged.
/// </summary>
public sealed class GitHubApiTokenHandler : DelegatingHandler
{
    private readonly string _tokenPath;
    private readonly object _gate = new();
    private DateTime _lastWriteUtc;
    private string? _cachedToken;
    private bool _loaded;
    private bool _rejected;
    private DateTime _exposureReportedFor = DateTime.MinValue;

    /// <summary>Group/other permission bits; any of them set exposes the token to other local users.</summary>
    internal const UnixFileMode ExposingBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public GitHubApiTokenHandler(string tokenPath, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        _tokenPath = tokenPath;
    }

    /// <summary>
    /// Raised once per token file version when GitHub rejects the token with 401.
    /// May be raised on any thread; carries no token data.
    /// </summary>
    public event EventHandler? TokenRejected;

    /// <summary>
    /// Raised once per token file version when other users could access it. <see cref="GitHubTokenFileExposedEventArgs.Restricted"/>
    /// says whether RHI set it to 0600 (token used) or could not (token ignored). May be raised on any thread; carries no token data.
    /// </summary>
    public event EventHandler<GitHubTokenFileExposedEventArgs>? TokenFileExposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? sentToken = null;
        var originalUri = request.RequestUri;
        if (originalUri is { Scheme: "https" }
            && originalUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && request.Headers.Authorization is null)
        {
            sentToken = LoadToken();
            if (sentToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sentToken);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Only 401 means bad credentials; 403/429 rate-limit or abuse responses keep the token.
        // A redirected request lost its Authorization header on the way, so its 401 says
        // nothing about the token.
        if (sentToken is null
            || response.StatusCode != HttpStatusCode.Unauthorized
            || request.RequestUri != originalUri)
            return response;

        if (MarkRejected(sentToken))
            TokenRejected?.Invoke(this, EventArgs.Empty);

        // Replay only requests without side effects or a body to resend.
        if (!IsReplayable(request))
            return response;

        response.Dispose();
        request.Headers.Authorization = null;
        request.RequestUri = originalUri; // the inner handler rewrites it on redirects
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Seam so tests can make the chmod fail (as for a file owned by another user); production always uses chmod(2).
    // Never called on Windows (EnsurePrivate returns first); the guard keeps the call site analysable.
    internal Action<string, UnixFileMode> SetUnixFileMode { get; set; } = static (path, mode) =>
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    };

    /// <summary>
    /// True when the token file is private to the user, restricting it to 0600 first when other
    /// users could access it. False (token must not be used) when it stays exposed.
    /// </summary>
    private bool EnsurePrivate(DateTime writeUtc)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(_tokenPath);
        if ((mode & ExposingBits) == 0) return true;

        var restricted = false;
        try
        {
            SetUnixFileMode(_tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            restricted = (File.GetUnixFileMode(_tokenPath) & ExposingBits) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to change (e.g. owned by another user): reported below, token not used.
        }

        bool report;
        lock (_gate)
        {
            report = _exposureReportedFor != writeUtc;
            _exposureReportedFor = writeUtc;
        }
        if (report)
            TokenFileExposed?.Invoke(this, new GitHubTokenFileExposedEventArgs(restricted));
        return restricted;
    }

    private static bool IsReplayable(HttpRequestMessage request) =>
        request.Content is null
        && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);

    /// <summary>True when this call is the first to reject the current token version.</summary>
    private bool MarkRejected(string sentToken)
    {
        lock (_gate)
        {
            // The file may have been rewritten while the request was in flight; a token
            // that has not been sent yet has not been proven bad.
            if (_rejected || !string.Equals(_cachedToken, sentToken, StringComparison.Ordinal))
                return false;
            _rejected = true;
            return true;
        }
    }

    private string? LoadToken()
    {
        try
        {
            if (!File.Exists(_tokenPath))
            {
                lock (_gate)
                {
                    _loaded = true;
                    _lastWriteUtc = DateTime.MinValue;
                    _cachedToken = null;
                    _rejected = false;
                }
                return null;
            }

            var writeUtc = File.GetLastWriteTimeUtc(_tokenPath);
            // Checked on every use, not only on reload: a chmod after loading must not keep
            // handing out a token other users can now read.
            if (!EnsurePrivate(writeUtc))
                return null;

            lock (_gate)
            {
                if (_loaded && writeUtc == _lastWriteUtc)
                    return _rejected ? null : _cachedToken;

                var token = File.ReadAllText(_tokenPath).Trim();
                // The rule Settings → GitHub token saves by (#163): one rule for what counts as a token.
                if (!Persistence.SecretFileStore.IsWellFormed(token))
                {
                    token = string.Empty;
                }

                _cachedToken = token.Length == 0 ? null : token;
                _lastWriteUtc = writeUtc;
                _loaded = true;
                _rejected = false; // a rewritten file gets a fresh chance
                return _cachedToken;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The GitHub token file could be accessed by other users.</summary>
/// <param name="restricted">True when RHI set it to 0600 and uses the token; false when it could not, and ignores the token.</param>
public sealed class GitHubTokenFileExposedEventArgs(bool restricted) : EventArgs
{
    public bool Restricted { get; } = restricted;
}
