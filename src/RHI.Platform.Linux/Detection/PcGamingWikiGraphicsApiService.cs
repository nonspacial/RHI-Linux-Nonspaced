using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Cache-backed PCGamingWiki API-table fallback used only when the local runtime probe
/// cannot identify a graphics API with stronger evidence.
/// </summary>
public sealed partial class PcGamingWikiGraphicsApiService
{
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private Dictionary<string, string[]>? _cache;

    public PcGamingWikiGraphicsApiService(HttpClient http, string? cachePath = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RHI", "pcgw_graphics_api_cache.json");
    }

    public async Task<GraphicsApiDetection> ResolveAndFetchAsync(
        string gameName,
        int? steamAppId,
        string? pageUrlOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(pageUrlOverride))
            return await FetchAsync(gameName, pageUrlOverride, cancellationToken).ConfigureAwait(false);

        if (steamAppId is int appId && appId > 0)
        {
            var appIdUrl = $"https://www.pcgamingwiki.com/api/appid.php?appid={appId}";
            return await FetchAsync(gameName, appIdUrl, cancellationToken).ConfigureAwait(false);
        }

        var pageUrl = await ResolveByOpenSearchAsync(gameName, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(pageUrl)
            ? GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki page not resolved for {gameName}" }
            : await FetchAsync(gameName, pageUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphicsApiDetection> FetchAsync(
        string gameName,
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidatePageUrl(pageUrl, out var uri))
            return GraphicsApiDetection.Unknown with { Evidence = "PCGamingWiki URL rejected by safety policy" };

        var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cache.TryGetValue(uri.AbsoluteUri, out var cachedNames))
            return FromNames(cachedNames, $"PCGamingWiki API table: {string.Join(", ", cachedNames)}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(8));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        var apis = ParseWindowsApis(html);
        if (apis.Count == 0)
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki API table contained no Windows renderer for {gameName}" };

        var names = apis.Select(static api => api.ToString()).ToArray();
        await StoreAsync(uri.AbsoluteUri, names, cancellationToken).ConfigureAwait(false);
        return FromApis(apis, $"PCGamingWiki API table: {string.Join(", ", apis)}");
    }

    internal static IReadOnlyList<GraphicsApi> ParseWindowsApis(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var tableMatch = ApiTableRegex().Match(html);
        if (!tableMatch.Success)
            return [];

        var found = new HashSet<GraphicsApi>();
        foreach (Match row in TableRowRegex().Matches(tableMatch.Groups[1].Value))
        {
            var rowHtml = row.Groups[1].Value;
            var abbr = AbbrRegex().Match(rowHtml);
            if (abbr.Success)
            {
                var platform = WebUtility.HtmlDecode(StripTags(abbr.Groups[1].Value));
                if (!platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var text = WebUtility.HtmlDecode(StripTags(rowHtml));
            AddApis(text, found);
        }

        return found
            .OrderByDescending(static api => Priority(api))
            .ThenBy(static api => api.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string?> ResolveByOpenSearchAsync(string gameName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;

        var url = $"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(gameName)}&limit=5&format=json";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 4)
            return null;

        var urls = doc.RootElement[3];
        if (urls.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var candidate in urls.EnumerateArray())
        {
            var value = candidate.GetString();
            if (value is not null && TryValidatePageUrl(value, out var safe))
                return safe.AbsoluteUri;
        }

        return null;
    }

    private async Task<Dictionary<string, string[]>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
            return _cache;

        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
                return _cache;

            if (!File.Exists(_cachePath))
                return _cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
                _cache = parsed is null
                    ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string[]>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                _cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            }

            return _cache;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task StoreAsync(string pageUrl, string[] names, CancellationToken cancellationToken)
    {
        await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache![pageUrl] = names;
            var parent = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            var temp = _cachePath + ".tmp";
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static GraphicsApiDetection FromNames(IReadOnlyList<string> names, string evidence)
    {
        var apis = names
            .Select(static name => Enum.TryParse<GraphicsApi>(name, ignoreCase: true, out var api) ? api : GraphicsApi.Unknown)
            .Where(static api => api != GraphicsApi.Unknown)
            .Distinct()
            .OrderByDescending(static api => Priority(api))
            .ToArray();
        return apis.Length == 0 ? GraphicsApiDetection.Unknown : FromApis(apis, evidence);
    }

    private static GraphicsApiDetection FromApis(IReadOnlyList<GraphicsApi> apis, string evidence)
        => new(apis[0], apis, GraphicsApiConfidence.Medium, evidence);

    private static bool TryValidatePageUrl(string pageUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = parsed.IdnHost;
        if (!host.Equals("pcgamingwiki.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".pcgamingwiki.com", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    private static void AddApis(string text, ISet<GraphicsApi> apis)
    {
        var normalized = Regex.Replace(text, @"\s+", " ");
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*12\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX12);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*11\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX11);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*10(?:\.1)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX10);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*9(?:\.0c)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX9);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*8(?:\.1)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX8);
        if (Regex.IsMatch(normalized, @"\bVulkan\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.Vulkan);
        if (Regex.IsMatch(normalized, @"\bOpenGL\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.OpenGL);
    }

    private static int Priority(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => 7,
        GraphicsApi.Vulkan => 6,
        GraphicsApi.DirectX11 => 5,
        GraphicsApi.DirectX10 => 4,
        GraphicsApi.OpenGL => 3,
        GraphicsApi.DirectX9 => 2,
        GraphicsApi.DirectX8 => 1,
        _ => 0
    };

    private static string StripTags(string input) => HtmlTagRegex().Replace(input, " ");

    [GeneratedRegex("<table\\b[^>]*\\bid\\s*=\\s*[\\\"']table-api[\\\"'][^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ApiTableRegex();

    [GeneratedRegex("<tr\\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex("<abbr\\b[^>]*\\btitle\\s*=\\s*[\\\"']([^\\\"']*)[\\\"'][^>]*>.*?</abbr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AbbrRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
