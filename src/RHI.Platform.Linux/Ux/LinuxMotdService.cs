using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Ux;

public sealed record LinuxMotdMessage(string Text, string Sha256, bool AlreadySeen);

/// <summary>
/// Fetches the Linux fork's message of the day. A network failure or missing motd.md is a silent
/// no-op: MOTD must never block application startup. HTML comments in motd.md are maintainer
/// notes and are never shown; a file holding only comments means "no message".
/// </summary>
public sealed partial class LinuxMotdService
{
    private sealed class StateDocument
    {
        public string LastSeenSha256 { get; set; } = "";
    }

    private readonly HttpClient _http;
    private readonly JsonFileStore<StateDocument> _state;
    private readonly string _url;

    public LinuxMotdService(HttpClient http, string? statePath = null, string? url = null)
    {
        _http = http;
        // Only the last-seen hash: losing it shows the message once more, so it is a cache
        // (an unreadable file is copied aside and replaced).
        _state = new JsonFileStore<StateDocument>(
            statePath ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "motd-state.json"),
            kind: JsonStoreKind.Cache);
        _url = url ?? RhiProductInfo.MotdUrl;
    }

    public async Task<LinuxMotdMessage?> FetchAsync(bool includeSeen = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = HtmlComment().Replace(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > 32_000) text = text[..32_000];
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            var state = LoadState();
            var seen = string.Equals(state.LastSeenSha256, hash, StringComparison.OrdinalIgnoreCase);
            if (seen && !includeSeen) return null;
            return new LinuxMotdMessage(text, hash, seen);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Remembers the message as seen (atomic, durable write). Throws <see cref="IOException"/> when it cannot be saved.</summary>
    public void MarkSeen(LinuxMotdMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _state.Save(new StateDocument { LastSeenSha256 = message.Sha256 });
    }

    private StateDocument LoadState() => _state.LoadOr(() => new StateDocument());

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();
}
