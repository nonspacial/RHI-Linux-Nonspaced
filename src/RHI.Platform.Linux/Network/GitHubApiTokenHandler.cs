using System.Net;
using System.Net.Http.Headers;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Adds an optional user-supplied GitHub API token only to requests whose host is exactly
/// api.github.com. The token is never attached to raw content, release asset CDNs or
/// unrelated services that share the application's HttpClient.
/// A token GitHub answers with 401 (revoked/expired) is dropped for the rest of the session,
/// and the request is replayed anonymously when that is safe, until the token file is
/// rewritten. The file itself is never modified and the token is never logged.
/// </summary>
public sealed class GitHubApiTokenHandler : DelegatingHandler
{
    private readonly string _tokenPath;
    private readonly object _gate = new();
    private DateTime _lastWriteUtc;
    private string? _cachedToken;
    private bool _loaded;
    private bool _rejected;

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
            lock (_gate)
            {
                if (_loaded && writeUtc == _lastWriteUtc)
                    return _rejected ? null : _cachedToken;

                var token = File.ReadAllText(_tokenPath).Trim();
                if (string.IsNullOrWhiteSpace(token)
                    || token.Length > 512
                    || token.Any(char.IsWhiteSpace))
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
