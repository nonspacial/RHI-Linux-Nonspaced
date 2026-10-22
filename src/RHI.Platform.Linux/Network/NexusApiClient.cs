using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Network;

/// <summary>The Nexus account a key belongs to, as Nexus reports it.</summary>
public sealed record NexusUser(int UserId, string Name, bool IsPremium);

/// <summary>A file of a Nexus mod (what the confirmation names before anything is downloaded).</summary>
public sealed record NexusModFile(int FileId, string Name, string? Version, string FileName, long SizeKb, string Category);

/// <summary>A Nexus API call that failed; <see cref="Rejected"/> is true for 401/403 (bad key, not allowed).</summary>
public sealed class NexusApiException(string message, HttpStatusCode? status, Exception? inner = null) : IOException(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    public bool Rejected => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
}

/// <summary>
/// The Nexus Mods v1 REST API, as upstream <c>NexusDownloadService</c> uses it: <c>users/validate</c> (key check),
/// <c>games/{game}/mods/{mod}/files</c> (file names), <c>…/files/{file}/download_link</c> (with an nxm link's
/// key+expires, or none for Premium). Every request is HTTPS to api.nexusmods.com under <see cref="DownloadSources.NexusApi"/>
/// (host checked before sending and on the answer), carries the key in the <c>apikey</c> header and the
/// <c>Application-Name</c>/<c>Application-Version</c> Nexus asks for, and is answered with a structured result or a
/// <see cref="NexusApiException"/> whose message never holds the key. A 429 is retried once after Nexus's Retry-After
/// (at most 30 s), as upstream (60 s there).
/// <para>Redirects are never followed: <c>apikey</c> is a custom header, which .NET's redirect handling carries to the
/// next host (it strips only <c>Authorization</c>), so the app's client comes from <see cref="CreateDefault"/> and
/// any 3xx answer is refused. Answers are read up to the policy's metadata cap whether or not they announce a length.</para>
/// </summary>
public sealed class NexusApiClient
{
    public const string BaseUrl = "https://api.nexusmods.com/v1";

    // Fixed messages (PT-BR presentation pairs; the status code follows AnsweredPrefix). Never the key.
    public const string UnreachableMessage = "Nexus Mods could not be reached.";
    public const string NoAccountMessage = "Nexus Mods answered the key check without an account.";
    public const string OversizedMessage = "Nexus Mods sent more than a metadata answer.";
    public const string AnsweredPrefix = "Nexus Mods answered ";

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Largest answer read (the Nexus API policy's cap, at most the metadata cap).</summary>
    public static long MaxAnswerBytes => Math.Min(DownloadSources.NexusApi.MaxBytes, TrustedDownload.MetadataMaxBytes);

    /// <summary>The app's Nexus API client: its own HTTP stack that never follows a redirect (see the class summary).</summary>
    public static NexusApiClient CreateDefault()
    {
        var handler = CreateHandler();
        return new NexusApiClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }) { OwnHandler = handler };
    }

    /// <summary>The handler <see cref="CreateDefault"/> built (null for a client handed in); wiring tests read it.</summary>
    internal SocketsHttpHandler? OwnHandler { get; private init; }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };

    /// <param name="delay">Test seam for the 429 wait.</param>
    public NexusApiClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    public async Task<NexusUser> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync($"{BaseUrl}/users/validate.json", apiKey, cancellationToken).ConfigureAwait(false);
        var user = JsonSerializer.Deserialize<ValidateResponse>(body) ?? throw new NexusApiException(NoAccountMessage, null);
        return new NexusUser(user.UserId, user.Name ?? "", user.IsPremium);
    }

    public async Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync($"{BaseUrl}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json", apiKey, cancellationToken).ConfigureAwait(false);
        var files = JsonSerializer.Deserialize<FilesResponse>(body)?.Files ?? [];
        return files.Select(f => new NexusModFile(f.FileId, f.Name ?? "", f.Version, f.FileName ?? "", f.SizeKb, f.CategoryName ?? "")).ToList();
    }

    /// <summary>Download URIs for a file: with an nxm link's key and expiry (any account), or without (Premium only).</summary>
    public async Task<IReadOnlyList<Uri>> GetDownloadLinksAsync(string apiKey, NxmLink link, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/games/{Uri.EscapeDataString(link.GameDomain)}/mods/{link.ModId}/files/{link.FileId}/download_link"; // no .json: the form upstream found Nexus answers for nxm keys
        if (link.Key is not null && link.Expires is not null)
            url += $"?key={Uri.EscapeDataString(link.Key)}&expires={Uri.EscapeDataString(link.Expires)}";
        var body = await GetAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        var links = JsonSerializer.Deserialize<List<DownloadLink>>(body) ?? [];
        // Nexus's own CDN first (upstream prefers "Nexus"), then the order given; only links the file policy allows.
        return links.OrderByDescending(l => string.Equals(l.ShortName, "Nexus", StringComparison.OrdinalIgnoreCase))
            .Select(l => Uri.TryCreate(l.Uri, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && DownloadSources.NexusFiles.Allows(uri))
            .Select(uri => uri!)
            .ToList();
    }

    private async Task<string> GetAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadSources.NexusApi.Validate(url));
            request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(RhiProductInfo.UserAgent));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            request.Headers.TryAddWithoutValidation("Application-Name", NexusApplication.Name);
            request.Headers.TryAddWithoutValidation("Application-Version", NexusApplication.Version);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new NexusApiException(UnreachableMessage, null, ex);
            }
            using (response)
            {
                DownloadSources.NexusApi.ValidateFinal(response, url);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    AppLog.Warn("nexus", $"Nexus Mods is limiting requests; trying once more in {(int)wait.TotalSeconds} s");
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // A 3xx is never followed (the key would go along): refused like any other non-success answer.
                if (!response.IsSuccessStatusCode)
                    throw new NexusApiException($"{AnsweredPrefix}{(int)response.StatusCode}.", response.StatusCode);
                try
                {
                    return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
                }
                // #233: a stalled answer is this client's failure type (its callers catch NexusApiException), keeping the stall's text.
                catch (DownloadStalledException ex)
                {
                    throw new NexusApiException(ex.Message, null, ex);
                }
            }
        }
    }

    /// <summary>The answer as UTF-8 text, refused past <see cref="MaxAnswerBytes"/> (announced or as it streams in).</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var cap = MaxAnswerBytes;
        if (response.Content.Headers.ContentLength is long announced && announced > cap)
            throw new NexusApiException(OversizedMessage, response.StatusCode);
        // #233: the client's 30 s timeout stops at the headers; the idle limit bounds the answer's body.
        await using var body = IdleTimeoutStream.Wrap(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), DownloadSources.NexusApi.Name);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap) throw new NexusApiException(OversizedMessage, response.StatusCode);
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ValidateResponse
    {
        [JsonPropertyName("user_id")] public int UserId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("is_premium")] public bool IsPremium { get; set; }
    }

    private sealed class FilesResponse
    {
        [JsonPropertyName("files")] public List<FileEntry>? Files { get; set; }
    }

    private sealed class FileEntry
    {
        [JsonPropertyName("file_id")] public int FileId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
        [JsonPropertyName("size_kb")] public long SizeKb { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
    }

    private sealed class DownloadLink
    {
        [JsonPropertyName("short_name")] public string? ShortName { get; set; }
        [JsonPropertyName("URI")] public string? Uri { get; set; }
    }
}
