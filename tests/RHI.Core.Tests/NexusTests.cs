using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#99: the nxm:// trust boundary — strict link parsing.</summary>
public sealed class NxmLinkTests
{
    [Fact]
    public void ModManagerDownloadLink_IsRead()
    {
        var link = NxmLink.TryParse("nxm://cyberpunk2077/mods/107/files/12345?key=Ab_c-1&expires=1760000000&user_id=42");

        Assert.Equal(new NxmLink("cyberpunk2077", 107, 12345, "Ab_c-1", "1760000000", "42"), link);
        Assert.Equal("https://www.nexusmods.com/cyberpunk2077/mods/107", link!.ModPageUrl);
        Assert.Equal(new NxmLink("skyrimspecialedition", 1, 2, null, null, null), NxmLink.TryParse("nxm://SkyrimSpecialEdition/mods/1/files/2"));
    }

    [Theory]
    [InlineData("https://cyberpunk2077/mods/1/files/2")]
    [InlineData("nxm://user:pw@cyberpunk2077/mods/1/files/2")]
    [InlineData("nxm://cyberpunk2077:8080/mods/1/files/2")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2/extra")]
    [InlineData("nxm://cyberpunk2077/mods/one/files/2")]
    [InlineData("nxm://cyberpunk2077/mods/0/files/2")]
    [InlineData("nxm://cyberpunk2077/collections/1/files/2")]
    [InlineData("nxm://cyber_punk/mods/1/files/2")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a&expires=1&install=yes")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a&key=b&expires=1")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a%20b&expires=1")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a&expires=soon")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2#frag")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2\n")]
    [InlineData("")]
    public void AnythingNexusDoesNotWrite_IsRefused(string raw) => Assert.Null(NxmLink.TryParse(raw));

    [Fact]
    public void OverlongLink_IsRefused()
        => Assert.Null(NxmLink.TryParse("nxm://cyberpunk2077/mods/1/files/2?key=" + new string('a', 2100) + "&expires=1"));

    [Fact]
    public void SsoSlug_IsDormantUntil171_NoOverride_UpstreamsNeverBorrowed()
    {
        // Owner decision (#99): API key only; SSO waits for RHI-Linux's own Nexus registration (#171).
        Assert.Null(NexusApplication.RegisteredSsoSlug);
        var before = Environment.GetEnvironmentVariable("RHI_NEXUS_SSO_SLUG");
        Environment.SetEnvironmentVariable("RHI_NEXUS_SSO_SLUG", "rhi-linux"); // the old test override: gone
        try { Assert.Null(NexusApplication.SsoSlug); }
        finally { Environment.SetEnvironmentVariable("RHI_NEXUS_SSO_SLUG", before); }
        Assert.Equal("rhi-linux", NexusApplication.SlugFrom("rhi-linux")); // what #171 will put in, validated
        Assert.Null(NexusApplication.SlugFrom("bad slug&x=1"));
    }
}

/// <summary>#99: the Nexus v1 API client (headers, host policy, errors, the one 429 retry) against a fake Nexus.</summary>
public sealed class NexusApiClientTests
{
    private const string Key = "nexus-test-key-0123456789";

    internal sealed class FakeNexus(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    internal static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    [Fact]
    public async Task ValidateKey_SendsTheKeyAndTheApplicationHeaders_OnlyToApiNexusmodsCom()
    {
        var fake = new FakeNexus(_ => Json(new { user_id = 7, name = "Tester", is_premium = true }));

        var user = await new NexusApiClient(new HttpClient(fake)).ValidateKeyAsync(Key);

        Assert.Equal(new NexusUser(7, "Tester", true), user);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://api.nexusmods.com/v1/users/validate.json", request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, request.Headers.GetValues("apikey").Single());
        Assert.Equal("RHI-Linux", request.Headers.GetValues("Application-Name").Single());
        Assert.True(request.Headers.Contains("Application-Version"));
    }

    [Fact]
    public async Task RejectedKey_IsAStructuredRefusal_WithoutTheKeyInTheMessage()
    {
        var fake = new FakeNexus(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<NexusApiException>(() => new NexusApiClient(new HttpClient(fake)).ValidateKeyAsync(Key));

        Assert.True(error.Rejected);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectOffApiNexusmodsCom_IsRefused()
    {
        var fake = new FakeNexus(request =>
        {
            var answer = Json(new { user_id = 1, name = "x", is_premium = false });
            answer.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/v1/users/validate.json");
            return answer;
        });

        await Assert.ThrowsAsync<RHI.Core.Deployment.UntrustedDownloadException>(() => new NexusApiClient(new HttpClient(fake)).ValidateKeyAsync(Key));
    }

    [Fact]
    public async Task RedirectAnswer_IsRefused_AndNothingIsSentToItsTarget()
    {
        var fake = new FakeNexus(_ =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://elsewhere.example/v1/users/validate.json");
            return moved;
        });

        var error = await Assert.ThrowsAsync<NexusApiException>(() => new NexusApiClient(new HttpClient(fake)).ValidateKeyAsync(Key));

        Assert.Equal(HttpStatusCode.Found, error.Status);
        Assert.Equal("api.nexusmods.com", Assert.Single(fake.Requests).RequestUri!.Host);
    }

    [Fact]
    public void TheAppsNexusClient_NeverFollowsARedirect_TheApikeyHeaderWouldGoAlong()
    {
        Assert.False(NexusApiClient.CreateDefault().OwnHandler!.AllowAutoRedirect);

        using var http = new HttpClient(new FakeNexus(_ => throw new HttpRequestException("offline")));
        var wineOverrides = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(), wineOverrides,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wineOverrides));
        Assert.False(services.Parity!.NexusAccount.Api.OwnHandler?.AllowAutoRedirect ?? true, "the app's Nexus client must be NexusApiClient.CreateDefault()");
    }

    /// <summary>A body of <paramref name="length"/> bytes with no announced length (chunked), produced as it is read.</summary>
    private sealed class Unannounced(long length) : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - Served);
            for (var i = 0; i < n; i++) buffer[offset + i] = (byte)(Served + i == 0 ? '[' : Served + i == length - 1 ? ']' : ' ');
            Served += n;
            return n;
        }
    }

    [Fact]
    public async Task AnswerWithoutContentLength_IsCappedAt16MiB_AsItStreams()
    {
        const long cap = 16L * 1024 * 1024;
        var over = new Unannounced(cap + 1);
        var fake = new FakeNexus(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(over) });
        var link = NxmLink.TryParse("nxm://cyberpunk2077/mods/107/files/12345?key=abc&expires=99")!;

        var error = await Assert.ThrowsAsync<NexusApiException>(() => new NexusApiClient(new HttpClient(fake)).GetDownloadLinksAsync(Key, link));

        Assert.Equal(NexusApiClient.OversizedMessage, error.Message);
        Assert.True(over.Served <= cap + 81920, $"read {over.Served} bytes");

        var exactly = new FakeNexus(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unannounced(cap)) });
        Assert.Empty(await new NexusApiClient(new HttpClient(exactly)).GetDownloadLinksAsync(Key, link)); // at the cap: read whole ("[  …  ]")
    }

    [Fact]
    public async Task RateLimited_IsRetriedOnceAfterRetryAfter_CappedAt30Seconds()
    {
        var calls = 0;
        var fake = new FakeNexus(_ =>
        {
            if (calls++ > 0) return Json(new { user_id = 1, name = "x", is_premium = false });
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return limited;
        });
        var waits = new List<TimeSpan>();

        await new NexusApiClient(new HttpClient(fake), (wait, _) => { waits.Add(wait); return Task.CompletedTask; }).ValidateKeyAsync(Key);

        Assert.Equal([TimeSpan.FromSeconds(30)], waits);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task DownloadLinks_UseTheLinksKey_PreferNexusCdn_AndDropHostsOffNexusCdn()
    {
        var fake = new FakeNexus(_ => Json(new object[]
        {
            new { name = "Elsewhere", short_name = "Else", URI = "https://evil.example/file.zip" },
            new { name = "Amsterdam", short_name = "Amsterdam", URI = "https://amsterdam.nexus-cdn.com/1/file.zip" },
            new { name = "Nexus CDN", short_name = "Nexus", URI = "https://cf-files.nexus-cdn.com/1/file.zip" }
        }));
        var link = NxmLink.TryParse("nxm://cyberpunk2077/mods/107/files/12345?key=abc&expires=99")!;

        var links = await new NexusApiClient(new HttpClient(fake)).GetDownloadLinksAsync(Key, link);

        Assert.Equal(["https://cf-files.nexus-cdn.com/1/file.zip", "https://amsterdam.nexus-cdn.com/1/file.zip"], links.Select(l => l.AbsoluteUri));
        Assert.Equal("https://api.nexusmods.com/v1/games/cyberpunk2077/mods/107/files/12345/download_link?key=abc&expires=99",
            Assert.Single(fake.Requests).RequestUri!.AbsoluteUri);
    }
}

/// <summary>#99: the SSO (login window) flow against a fake socket; never reaches Nexus.</summary>
public sealed class NexusSsoClientTests
{
    internal sealed class FakeSocket(params string?[] incoming) : INexusSsoTransport
    {
        private readonly Queue<string?> _incoming = new(incoming);
        public List<string> Sent { get; } = [];
        public Uri? Connected { get; private set; }
        public bool Hang { get; init; }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) { Connected = endpoint; return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken cancellationToken) { Sent.Add(text); return Task.CompletedTask; }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Hang || _incoming.Count == 0) { await Task.Delay(Timeout.Infinite, cancellationToken); return null; }
            return _incoming.Dequeue();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Login_RegistersTheSession_OpensTheNexusPageForThisSlug_AndReturnsTheKey()
    {
        var socket = new FakeSocket("""{"success":true,"data":{"connection_token":"tok"},"error":null}""", """{"success":true,"data":{"api_key":"the-sso-key-123"},"error":null}""");
        var opened = new List<string>();
        var stages = new List<NexusSsoStage>();

        var (key, stage) = await new NexusSsoClient(() => socket).AuthoriseAsync("rhi-linux-test", opened.Add, stages.Add, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(("the-sso-key-123", NexusSsoStage.Done), (key, stage));
        Assert.Equal(new Uri("wss://sso.nexusmods.com"), socket.Connected);
        using var handshake = JsonDocument.Parse(Assert.Single(socket.Sent));
        var id = handshake.RootElement.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(2, handshake.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal([$"https://www.nexusmods.com/sso?id={id}&application=rhi-linux-test"], opened);
        Assert.Equal([NexusSsoStage.Connecting, NexusSsoStage.WaitingForBrowser], stages);
    }

    [Fact]
    public async Task ServerClosesOrReportsAnError_Fails()
    {
        Assert.Equal(NexusSsoStage.Failed, (await new NexusSsoClient(() => new FakeSocket([null]))
            .AuthoriseAsync("s", _ => { }, _ => { }, TimeSpan.FromSeconds(10), CancellationToken.None)).Stage);
        Assert.Equal(NexusSsoStage.Failed, (await new NexusSsoClient(() => new FakeSocket("""{"success":false,"data":null,"error":"bad"}"""))
            .AuthoriseAsync("s", _ => { }, _ => { }, TimeSpan.FromSeconds(10), CancellationToken.None)).Stage);
    }

    [Fact]
    public async Task NoAuthorisation_TimesOut_AndCancelIsCancel()
    {
        var timedOut = await new NexusSsoClient(() => new FakeSocket { Hang = true })
            .AuthoriseAsync("s", _ => { }, _ => { }, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        using var cancel = new CancellationTokenSource(100);
        var cancelled = await new NexusSsoClient(() => new FakeSocket { Hang = true })
            .AuthoriseAsync("s", _ => { }, _ => { }, TimeSpan.FromMinutes(5), cancel.Token);

        Assert.Equal((null, NexusSsoStage.TimedOut), timedOut);
        Assert.Equal((null, NexusSsoStage.Cancelled), cancelled);
    }
}

/// <summary>#99: the nxm:// handler entry (fake xdg-mime) and the files RHI offers from a download.</summary>
public sealed class NexusHandlerAndDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nexus-" + Guid.NewGuid().ToString("N"));

    public NexusHandlerAndDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (NxmHandlerRegistration Registration, List<string> Commands) Registration(string defaultHandler, bool xdgMime = true)
    {
        var commands = new List<string>();
        var registration = new NxmHandlerRegistration(Path.Combine(_root, "applications"), () => ["/opt/RHI Linux/RHI"], (command, _) =>
        {
            commands.Add(string.Join(' ', command));
            if (!xdgMime && command[0] == "xdg-mime") return Task.FromResult<(int, string)?>(null);
            return Task.FromResult<(int, string)?>((0, command is ["xdg-mime", "query", ..] ? defaultHandler + "\n" : ""));
        });
        return (registration, commands);
    }

    [Fact]
    public async Task Register_WritesAHiddenNxmEntryForThisInstall_AndMakesItTheDefault()
    {
        var (registration, commands) = Registration("");

        Assert.True(await registration.RegisterAsync());

        var lines = File.ReadAllLines(registration.EntryPath);
        Assert.Contains("MimeType=x-scheme-handler/nxm;", lines);
        Assert.Contains("NoDisplay=true", lines);
        Assert.Contains("Exec=\"/opt/RHI Linux/RHI\" \"--nxm\" %u", lines);
        Assert.Contains("xdg-mime default rhi-linux-nxm.desktop x-scheme-handler/nxm", commands);
    }

    [Theory]
    [InlineData("rhi-linux-nxm.desktop", NxmHandlerState.Rhi)]
    [InlineData("rhi-linux.desktop", NxmHandlerState.Rhi)]
    [InlineData("modorganizer2-nxm-handler.desktop", NxmHandlerState.Other)]
    [InlineData("", NxmHandlerState.None)]
    public async Task Query_TellsWhoHandlesNxmLinks(string answer, NxmHandlerState expected)
        => Assert.Equal(expected, (await Registration(answer).Registration.QueryAsync()).State);

    [Fact]
    public async Task NoXdgMime_RegisterSaysNo_AndQueryIsUnknown()
    {
        var (registration, _) = Registration("", xdgMime: false);

        Assert.False(await registration.RegisterAsync());
        Assert.Equal(NxmHandlerState.Unknown, (await registration.QueryAsync()).State);
    }

    [Fact]
    public async Task Unregister_RemovesOnlyRhisEntry()
    {
        var (registration, _) = Registration("");
        await registration.RegisterAsync();
        var other = Path.Combine(_root, "applications", "vortex-nxm.desktop");
        File.WriteAllText(other, "[Desktop Entry]\n");

        Assert.True(await registration.UnregisterAsync());

        Assert.False(File.Exists(registration.EntryPath));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task AnEntryOfThatNameRhiDidNotWrite_IsNeverOverwrittenOrDeleted()
    {
        var (registration, commands) = Registration("");
        Directory.CreateDirectory(Path.Combine(_root, "applications"));
        const string theirs = "[Desktop Entry]\nType=Application\nName=Someone else's\nExec=other %u\nMimeType=x-scheme-handler/nxm;\n";
        File.WriteAllText(registration.EntryPath, theirs);

        Assert.True(registration.HasForeignEntry);
        Assert.False(await registration.RegisterAsync());
        Assert.Equal(theirs, File.ReadAllText(registration.EntryPath));
        Assert.DoesNotContain(commands, c => c.StartsWith("xdg-mime default", StringComparison.Ordinal));
        Assert.False(await registration.UnregisterAsync());
        Assert.Equal(theirs, File.ReadAllText(registration.EntryPath));

        File.Delete(registration.EntryPath);
        Assert.True(await registration.RegisterAsync()); // RHI's own entry carries the marker: re-registered and removed
        Assert.Contains(NxmHandlerRegistration.MarkerLine, File.ReadAllLines(registration.EntryPath));
        Assert.False(registration.HasForeignEntry);
        Assert.True(await registration.RegisterAsync());
        Assert.True(await registration.UnregisterAsync());
    }

    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    [Fact]
    public async Task ZipWithAddOns_OffersEachAddOnFlat_InsideTheDownloadFolder_NothingElse()
    {
        var zip = Zip("mod.zip", ("renodx-cyberpunk2077/renodx-cyberpunk2077.addon64", "a"), ("../../escape.addon32", "b"), ("readme.txt", "c"), ("dll/winmm.dll", "d"));
        var folder = Path.Combine(_root, "dl");

        var offered = await NexusDownloadService.InstallableAsync(zip, folder, CancellationToken.None);

        Assert.Equal([Path.Combine(folder, "add-ons", "renodx-cyberpunk2077.addon64"), Path.Combine(folder, "add-ons", "escape.addon32")], offered);
        Assert.All(offered, path => Assert.StartsWith(folder + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal));
        Assert.Equal("a", File.ReadAllText(offered[0]));
        Assert.False(File.Exists(Path.Combine(_root, "escape.addon32")));
    }

    [Fact]
    public async Task LumaPackage_IsOfferedWhole_OtherArchivesOfferNothing()
    {
        var luma = Zip("Luma-Prey.zip", ("Luma-Prey.addon64", "x"));
        var seven = Path.Combine(_root, "mod.7z");
        File.WriteAllText(seven, "7z");

        Assert.Equal([luma], await NexusDownloadService.InstallableAsync(luma, _root, CancellationToken.None));
        Assert.Empty(await NexusDownloadService.InstallableAsync(seven, _root, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("dir\\evil.addon64", "evil.addon64")]
    [InlineData(".hidden.addon64", "hidden.addon64")]
    [InlineData("Mod $(rm) ;x.zip", "Mod _(rm) _x.zip")]
    public void DownloadNames_StayPlainFileNames(string given, string expected)
        => Assert.Equal(expected, NexusDownloadService.SafeFileName(given));

    [Fact]
    public async Task Download_FetchesTheLinkOnNexusCdn_IntoItsOwnCacheFolder()
    {
        var fake = new NexusApiClientTests.FakeNexus(request => request.RequestUri!.Host == "api.nexusmods.com"
            ? NexusApiClientTests.Json(new object[] { new { short_name = "Nexus", URI = "https://cf-files.nexus-cdn.com/9/renodx-mod.addon64" } })
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var http = new HttpClient(fake);
        var service = new NexusDownloadService(http, new NexusApiClient(http), Path.Combine(_root, "cache"));
        var link = NxmLink.TryParse("nxm://cyberpunk2077/mods/107/files/5?key=k&expires=1")!;

        var download = await service.DownloadAsync("key-123456", link, null);

        Assert.Equal(Path.Combine(_root, "cache", "cyberpunk2077-107-5", "renodx-mod.addon64"), download.Downloaded);
        Assert.Equal([download.Downloaded], download.Installable);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(download.Downloaded));
        Assert.False(fake.Requests[1].Headers.Contains("apikey")); // the key goes to the API only, never to the file server
    }

    [Fact]
    public async Task Download_NoLinkOnNexusCdn_DownloadsNothing()
    {
        var fake = new NexusApiClientTests.FakeNexus(_ => NexusApiClientTests.Json(new object[] { new { short_name = "X", URI = "https://evil.example/f.zip" } }));
        var http = new HttpClient(fake);
        var service = new NexusDownloadService(http, new NexusApiClient(http), Path.Combine(_root, "cache"));

        var error = await Assert.ThrowsAsync<NexusApiException>(() => service.DownloadAsync("key-123456", NxmLink.TryParse("nxm://g/mods/1/files/2?key=k&expires=1")!, null));

        Assert.Equal(NexusDownloadService.NoLinkMessage, error.Message);
        Assert.Single(fake.Requests);
    }
}

/// <summary>#99 in the app: the account section and a link arriving at RHI, confirmed before any download.</summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class NexusPresenterTests : IDisposable
{
    private const string Key = "nexus-presenter-key-0123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nexus-vm-" + Guid.NewGuid().ToString("N"));

    public NexusPresenterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string KeyPath => Path.Combine(_root, "nexus_api.txt");

    private static byte[] ZipWith(string entry, string content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(entry).Open())
            stream.Write(Encoding.UTF8.GetBytes(content));
        return buffer.ToArray();
    }

    private (NexusAccountPresenter Account, GameLinksPresenter Links, NexusApiClientTests.FakeNexus Http) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, NexusSsoClientTests.FakeSocket? socket = null, string? slug = null,
        Func<string, (bool Opened, string? Error)>? openLink = null)
    {
        var fake = new NexusApiClientTests.FakeNexus(respond ?? (request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/users/validate.json" => NexusApiClientTests.Json(new { user_id = 3, name = "Robbie", is_premium = false }),
            var p when p.EndsWith("/files.json", StringComparison.Ordinal) => NexusApiClientTests.Json(new { files = new[] { new { file_id = 5, name = "RenoDX Cyberpunk", version = "1.2", category_name = "MAIN", size_kb = 2048, file_name = "renodx-cp.zip" } } }),
            var p when p.EndsWith("/download_link", StringComparison.Ordinal) => NexusApiClientTests.Json(new object[] { new { short_name = "Nexus", URI = "https://cf-files.nexus-cdn.com/x/renodx-cp.addon64" } }),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipWith("renodx-cp/renodx-cyberpunk2077.addon64", "addon")) }
        }));
        var http = new HttpClient(fake);
        var api = new NexusApiClient(http);
        var handler = new NxmHandlerRegistration(Path.Combine(_root, "apps"), () => ["/opt/rhi/RHI"], (_, _) => Task.FromResult<(int, string)?>((0, "")));
        var account = new NexusAccountPresenter(new SecretFileStore(KeyPath), api, new AccountsPreferenceStore(Path.Combine(_root, "accounts.json")), handler,
            socket is null ? null : () => new NexusSsoClient(() => socket), slug, openLink ?? (_ => (true, null)));
        return (account, new GameLinksPresenter(account, api, new NexusDownloadService(http, api, Path.Combine(_root, "cache"))), fake);
    }

    [Fact]
    public async Task PastedKey_IsCheckedThenSavedPrivate_AndTheAccountIsShown()
    {
        var (account, _, _) = Build();
        Assert.Equal("Not connected.", account.AccountText);
        Assert.False(account.SsoAvailable); // no slug: the login button is hidden, paste only

        Assert.True(await account.SaveKeyAsync(" " + Key + " "));

        Assert.Equal(Key, File.ReadAllText(KeyPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal("Account: Robbie (Free account)", account.AccountText);
        Assert.DoesNotContain(Key, account.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectNexusAccount_OpensTheKeyPage_ShowsThePasteBox_HiddenOnceTheKeyIsSaved()
    {
        var opened = new List<string>();
        var (account, _, _) = Build(openLink: url => { opened.Add(url); return (true, null); });
        Assert.False(account.IsKeyEntryShown); // not connected: only the Connect button

        account.Connect();

        Assert.Equal(["https://www.nexusmods.com/users/myaccount?tab=api"], opened);
        Assert.True(account.IsKeyEntryShown);
        Assert.Equal("Nexus's API key page is open in your browser. Copy your personal API key there, paste it below and choose Check and save.", account.StatusText);
        Assert.True(await account.SaveKeyAsync(Key));
        Assert.False(account.IsKeyEntryShown);
        Assert.True(account.IsConnected);
    }

    [Fact]
    public void ConnectNexusAccount_BrowserDoesNotOpen_StillShowsTheBox_AndNamesThePage_InPortuguese()
    {
        UiLocalizationService.Apply("pt-BR");
        var (account, _, _) = Build(openLink: _ => (false, "xdg-open not found"));

        account.Connect();

        Assert.True(account.IsKeyEntryShown);
        Assert.StartsWith("O navegador não pôde ser aberto (", account.StatusText, StringComparison.Ordinal);
        Assert.EndsWith("). Abra https://www.nexusmods.com/users/myaccount?tab=api, copie a sua chave pessoal de API, cole aqui embaixo e escolha Verificar e salvar.",
            account.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyNexusDoesNotKnow_IsNotSaved_InPortugueseSaysSo()
    {
        UiLocalizationService.Apply("pt-BR");
        var (account, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.False(await account.SaveKeyAsync(Key));

        Assert.False(File.Exists(KeyPath));
        Assert.Equal("O Nexus Mods não reconhece esta chave; nada foi salvo.", account.StatusText);
    }

    [Fact]
    public async Task SsoLogin_SavesTheKeyNexusSent_AfterCheckingIt()
    {
        var socket = new NexusSsoClientTests.FakeSocket("""{"success":true,"data":{"api_key":"nexus-sso-key-456789"}}""");
        var (account, _, http) = Build(socket: socket, slug: "rhi-linux-test");
        Assert.True(account.SsoAvailable);

        var sso = account.CreateSso();
        Assert.True(await sso.RunAsync());

        Assert.Equal("nexus-sso-key-456789", File.ReadAllText(KeyPath));
        Assert.Equal("nexus-sso-key-456789", http.Requests.Single().Headers.GetValues("apikey").Single());
        Assert.StartsWith("https://www.nexusmods.com/sso?id=", sso.AuthorisationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerEntryRhiDidNotWrite_RegisterAndUnregister_LeaveIt_AndSaySo()
    {
        var (account, _, _) = Build();
        var entry = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "apps")).FullName, NxmHandlerRegistration.EntryName);
        File.WriteAllText(entry, "[Desktop Entry]\nName=theirs\n");
        const string said = "An rhi-linux-nxm.desktop that RHI did not write is already in the applications folder; RHI left it as it is. Remove it yourself if it is no longer needed.";

        await account.RegisterHandlerAsync();
        Assert.Equal(said, account.StatusText);
        await account.UnregisterHandlerAsync();
        Assert.Equal(said, account.StatusText);
        Assert.Equal("[Desktop Entry]\nName=theirs\n", File.ReadAllText(entry));
    }

    [Fact]
    public async Task Disconnect_DeletesTheKeyAndForgetsTheAccount()
    {
        var (account, _, _) = Build();
        await account.SaveKeyAsync(Key);

        account.Disconnect();

        Assert.False(File.Exists(KeyPath));
        Assert.Equal("Not connected.", account.AccountText);
        Assert.Null(new AccountsPreferenceStore(Path.Combine(_root, "accounts.json")).Load().NexusUserName);
    }

    private static DetectedGame Cyberpunk(string root) => new()
    {
        Name = "Cyberpunk 2077", Source = "Steam", SteamAppId = 1091500, InstallPath = Directory.CreateDirectory(Path.Combine(root, "cp")).FullName,
        NexusUrlOverride = "https://www.nexusmods.com/games/cyberpunk2077"
    };

    [Fact]
    public async Task NxmLink_IsConfirmedWithTheFileAndGameNamed_ThenDownloadedAndOfferedForInstall()
    {
        var (account, links, http) = Build();
        await account.SaveKeyAsync(Key);
        using var fx = await ViewModelFixture.StartAsync([Cyberpunk(_root)]);
        NxmConfirmation? asked = null;
        var offered = new List<string>();
        links.Confirm = c => { asked = c; return Task.FromResult(true); };
        links.OfferFile = path => { offered.Add(path); return Task.CompletedTask; };

        await links.HandleAsync(fx.Vm, "nxm://cyberpunk2077/mods/107/files/5?key=k&expires=1");

        Assert.Equal("RenoDX Cyberpunk", asked!.File!.Name);
        Assert.Equal("Cyberpunk 2077", asked.ForGame);
        var file = Assert.Single(offered); // the add-on taken out of the zip Nexus served, for the import prompt
        Assert.Equal("renodx-cyberpunk2077.addon64", Path.GetFileName(file));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "cyberpunk2077-107-5", "renodx-cp.zip"))); // saved under the name Nexus lists
        Assert.Equal("Cyberpunk 2077", fx.Vm.SelectedGame?.Name);
        Assert.Contains(http.Requests, r => r.RequestUri!.Host == "cf-files.nexus-cdn.com");
    }

    [Fact]
    public async Task NxmLink_Declined_DownloadsNothing()
    {
        var (account, links, http) = Build();
        await account.SaveKeyAsync(Key);
        using var fx = await ViewModelFixture.StartAsync([Cyberpunk(_root)]);
        links.Confirm = _ => Task.FromResult(false);

        await links.HandleAsync(fx.Vm, "nxm://cyberpunk2077/mods/107/files/5?key=k&expires=1");

        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/download_link", StringComparison.Ordinal) || r.RequestUri.Host.EndsWith("nexus-cdn.com", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
        Assert.StartsWith("Nexus Mods download cancelled; nothing was downloaded.", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NxmLink_Malformed_OrNoAccount_AsksNothing()
    {
        var (account, links, http) = Build();
        using var fx = await ViewModelFixture.StartAsync([]);
        var asked = 0;
        links.Confirm = _ => { asked++; return Task.FromResult(true); };

        await links.HandleAsync(fx.Vm, "nxm://cyberpunk2077/mods/1/files/2?install=now");
        Assert.Contains("is not a valid \"Mod Manager Download\" link", fx.Vm.StatusText, StringComparison.Ordinal);
        await links.HandleAsync(fx.Vm, "nxm://cyberpunk2077/mods/1/files/2?key=k&expires=1");
        Assert.Contains("no account is connected", fx.Vm.StatusText, StringComparison.Ordinal);

        Assert.Equal(0, asked);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void NexusPage_OnlyHttpsNexusmodsCom()
    {
        Assert.Equal("https://www.nexusmods.com/games/cyberpunk2077", GameLinksPresenter.PageFor(Cyberpunk(_root)));
        Assert.Null(GameLinksPresenter.PageFor(new DetectedGame { Name = "x", NexusUrlOverride = "http://www.nexusmods.com/games/x" }));
        Assert.Null(GameLinksPresenter.PageFor(new DetectedGame { Name = "x", NexusUrlOverride = "https://nexusmods.com.evil.example/games/x" }));
        Assert.Equal("cyberpunk2077", GameLinksPresenter.GameDomainOf("https://www.nexusmods.com/games/cyberpunk2077"));
        Assert.Equal("skyrim", GameLinksPresenter.GameDomainOf("https://www.nexusmods.com/skyrim/mods/12"));
    }
}

/// <summary>#99 packaging: install-user.sh's menu entry declares nxm links and passes the link (%u).</summary>
public sealed class InstallUserNxmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-install-user-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MenuEntry_DeclaresNxm_AndPassesTheLink()
    {
        var home = Install(Path.Combine(_root, "home"));

        var lines = File.ReadAllLines(Path.Combine(home, "share", "applications", "rhi-linux.desktop"));
        Assert.Contains($"Exec=\"{Path.Combine(home, ".local", "opt", "rhi-linux", "RHI")}\" %u", lines);
        Assert.Contains("MimeType=x-scheme-handler/nxm;", lines);
    }

    [Fact]
    public void MenuEntry_ExecPath_WithShellAndDesktopSpecials_ReadsBackExactly_AsTheSpecAndGLibRead()
    {
        var home = Install(Path.Combine(_root, "h $x \"q\" `b` \\ 100%"));
        var file = Path.Combine(home, "share", "applications", "rhi-linux.desktop");
        var exec = File.ReadAllLines(file).Single(line => line.StartsWith("Exec=", StringComparison.Ordinal))["Exec=".Length..];
        var rhi = Path.Combine(home, ".local", "opt", "rhi-linux", "RHI");

        Assert.EndsWith(" %u", exec, StringComparison.Ordinal);
        Assert.Equal([rhi], DesktopEntryReader.ExecArguments(exec[..^" %u".Length]));
        if (GLibDesktopEntry.ExecArguments(file) is { } glib) Assert.Equal([rhi, "%u"], glib);
    }

    private string Install(string homePath)
    {
        var repo = LocalizationKeyUsageTests.RepoRoot();
        var tree = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        File.Copy(Path.Combine(repo, "install-user.sh"), Path.Combine(tree, "install-user.sh"));
        Directory.CreateDirectory(Path.Combine(tree, "packaging"));
        File.Copy(Path.Combine(repo, "packaging", "rhi-linux.desktop"), Path.Combine(tree, "packaging", "rhi-linux.desktop"));
        var publish = Directory.CreateDirectory(Path.Combine(tree, "artifacts", "linux-x64")).FullName;
        File.WriteAllText(Path.Combine(publish, "RHI"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(publish, "RHI"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = Directory.CreateDirectory(homePath).FullName;
        var start = new System.Diagnostics.ProcessStartInfo("bash", [Path.Combine(tree, "install-user.sh")])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.Environment["HOME"] = home;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, "share");
        start.Environment["PATH"] = "/usr/bin:/bin";
        using var run = System.Diagnostics.Process.Start(start)!;
        run.WaitForExit(30_000);
        Assert.True(run.ExitCode == 0, run.StandardError.ReadToEnd());
        return home;
    }
}
