using System.Net.Http.Headers;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Adds an optional user-supplied GitHub API token only to requests whose host is exactly
/// api.github.com. The token is never attached to raw content, release asset CDNs or
/// unrelated services that share the application's HttpClient.
/// </summary>
public sealed class GitHubApiTokenHandler : DelegatingHandler
{
    private readonly string _tokenPath;
    private readonly object _gate = new();
    private DateTime _lastWriteUtc;
    private string? _cachedToken;
    private bool _loaded;

    public GitHubApiTokenHandler(string tokenPath, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        _tokenPath = tokenPath;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { Scheme: "https" } uri
            && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && request.Headers.Authorization is null)
        {
            var token = LoadToken();
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
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
                }
                return null;
            }

            var writeUtc = File.GetLastWriteTimeUtc(_tokenPath);
            lock (_gate)
            {
                if (_loaded && writeUtc == _lastWriteUtc)
                    return _cachedToken;

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
