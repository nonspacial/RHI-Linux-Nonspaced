using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Network;

/// <summary>The SSO socket: one text message out, text messages in (a seam so tests never reach Nexus).</summary>
public interface INexusSsoTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next text message, or null when the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Where an SSO login stands (the window words it).</summary>
public enum NexusSsoStage
{
    Connecting,
    WaitingForBrowser,
    Done,
    Failed,
    TimedOut,
    Cancelled
}

/// <summary>
/// The Nexus Mods single sign-on flow (upstream <c>NexusSsoService</c>; Nexus's documented WebSocket SSO): connect to
/// <c>wss://sso.nexusmods.com</c>, send <c>{"id": &lt;uuid&gt;, "token": null, "protocol": 2}</c>, open
/// <c>https://www.nexusmods.com/sso?id=&lt;uuid&gt;&amp;application=&lt;slug&gt;</c> in the browser, wait (pings every 30 s) for the
/// message carrying the API key. The slug is the application's registration with Nexus (<see cref="NexusApplication.SsoSlug"/>);
/// without one the flow is not offered. The key returned is checked by the caller before it is saved.
/// </summary>
public sealed class NexusSsoClient
{
    public static readonly Uri Endpoint = new("wss://sso.nexusmods.com");
    public const int MaxMessageBytes = 16 * 1024;

    private readonly Func<INexusSsoTransport> _transport;

    /// <param name="transport">Test seam (default: a <see cref="ClientWebSocket"/> with 30 s keep-alive pings).</param>
    public NexusSsoClient(Func<INexusSsoTransport>? transport = null) => _transport = transport ?? (() => new WebSocketTransport());

    /// <summary>The browser page that asks the user to authorise <paramref name="slug"/> for session <paramref name="sessionId"/>.</summary>
    public static string AuthorisationUrl(string sessionId, string slug)
        => $"https://www.nexusmods.com/sso?id={Uri.EscapeDataString(sessionId)}&application={Uri.EscapeDataString(slug)}";

    /// <summary>
    /// Runs the flow: <paramref name="openBrowser"/> gets the authorisation page once the session is registered. Returns
    /// the API key, or null (with <paramref name="stage"/> saying why) on cancel, timeout, a closed or garbled exchange.
    /// </summary>
    public async Task<(string? Key, NexusSsoStage Stage)> AuthoriseAsync(string slug, Action<string> openBrowser, Action<NexusSsoStage> stage,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var transport = _transport();
        await using (transport.ConfigureAwait(false))
        {
            try
            {
                stage(NexusSsoStage.Connecting);
                await transport.ConnectAsync(Endpoint, limit.Token).ConfigureAwait(false);
                await transport.SendAsync(JsonSerializer.Serialize(new { id = sessionId, token = (string?)null, protocol = 2 }), limit.Token).ConfigureAwait(false);
                openBrowser(AuthorisationUrl(sessionId, slug));
                stage(NexusSsoStage.WaitingForBrowser);
                while (true)
                {
                    var message = await transport.ReceiveAsync(limit.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        AppLog.Warn("nexus-sso", "Nexus closed the login connection before sending a key");
                        return (null, NexusSsoStage.Failed);
                    }
                    switch (Read(message))
                    {
                        case (MessageKind.Key, var key):
                            return (key, NexusSsoStage.Done);
                        case (MessageKind.Error, _):
                            AppLog.Warn("nexus-sso", "Nexus reported an error during login");
                            return (null, NexusSsoStage.Failed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return (null, cancellationToken.IsCancellationRequested ? NexusSsoStage.Cancelled : NexusSsoStage.TimedOut);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
            {
                AppLog.Warn("nexus-sso", "The Nexus login connection failed", ex);
                return (null, NexusSsoStage.Failed);
            }
        }
    }

    private enum MessageKind { Other, Key, Error }

    /// <summary>
    /// One SSO message: <c>{"success":true,"data":{"api_key":"…"}}</c> carries the key; <c>data.connection_token</c> is the
    /// session's bookkeeping (ignored); <c>"success":false</c> is an error. A bare string body is taken as the key, as upstream does.
    /// </summary>
    private static (MessageKind Kind, string? Key) Read(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return Key(root.GetString());
            if (root.ValueKind != JsonValueKind.Object) return (MessageKind.Other, null);
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return (MessageKind.Error, null);
            if (!root.TryGetProperty("data", out var data)) return (MessageKind.Other, null);
            if (data.ValueKind == JsonValueKind.String) return Key(data.GetString());
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("api_key", out var apiKey) && apiKey.ValueKind == JsonValueKind.String)
                return Key(apiKey.GetString());
            return (MessageKind.Other, null);
        }
        catch (JsonException)
        {
            return Key(message.Trim());
        }

        static (MessageKind, string?) Key(string? value)
            => Persistence.SecretFileStore.IsWellFormed(value?.Trim()) ? (MessageKind.Key, value!.Trim()) : (MessageKind.Other, null);
    }

    private sealed class WebSocketTransport : INexusSsoTransport
    {
        private readonly ClientWebSocket _socket = new();

        public WebSocketTransport()
        {
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); // Nexus closes idle SSO sockets; upstream pings every 30 s
            _socket.Options.SetRequestHeader("User-Agent", RHI.Core.RhiProductInfo.UserAgent);
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) => _socket.ConnectAsync(endpoint, cancellationToken);

        public Task SendAsync(string text, CancellationToken cancellationToken)
            => _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new IOException("The Nexus login sent an oversized message.");
                if (result.EndOfMessage)
                {
                    if (result.MessageType != WebSocketMessageType.Text) { message.SetLength(0); continue; }
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or IOException)
                {
                    AppLog.Warn("nexus-sso", "Could not close the Nexus login connection cleanly", ex);
                }
            }
            _socket.Dispose();
        }
    }
}
