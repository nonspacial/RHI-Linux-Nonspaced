using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #249: regression pins for upstream RHI 2.8.8 Beta bugs that never existed in the rewrite (items 6, 7, 8, 9 and 12;
/// the other items live beside their services' fixtures in <c>Upstream288Pins.*.cs</c>). Each test pins today's
/// behaviour through a public or internal seam, so a later change cannot bring the upstream bug in.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // Unix file modes on the token file
public sealed class Upstream288PinsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pins288-" + Guid.NewGuid().ToString("N"));

    public Upstream288PinsTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ---- item 6 (a1a4e48): two record writes at once keep both ------------------------------------------------------

    // The second Upsert starts while the first is inside its save (the store's save seam). One at a time, it waits and
    // then reads the ledger with the first record in it; without the store's lock it would save its own stale copy and
    // one of the two records would be lost.
    [Fact]
    public async Task Pin288_RecordStore_TwoWritesAtOnce_KeepBoth()
    {
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var game = new DetectedGame { Name = "Two Writes", InstallPath = Path.Combine(_root, "two-writes"), SteamAppId = 2490 };
        Task? second = null;
        var secondFinishedInsideFirstSave = false;
        var fired = 0;
        store.SaveFaultForTesting = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                second = Task.Run(() => store.Upsert(Record(game, ManagedComponentKind.ReLimiter)));
                secondFinishedInsideFirstSave = second.Wait(TimeSpan.FromSeconds(1));
            }
            return null;
        };

        store.Upsert(Record(game, ManagedComponentKind.ReShade));
        await second!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(secondFinishedInsideFirstSave); // the second write waited for the first one's save
        var fresh = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        Assert.NotNull(fresh.Find(game, ManagedComponentKind.ReShade));
        Assert.NotNull(fresh.Find(game, ManagedComponentKind.ReLimiter));
    }

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
        BinaryDirectory = game.InstallPath, Version = "1"
    };

    // ---- item 7 (a1a4e48): a renamed game keeps its Luma TAA ----------------------------------------------------------

    [Fact]
    public async Task Pin288_RenamedGame_KeepsItsLumaTaa()
    {
        var installPath = Path.Combine(_root, "luma-game");
        var bin = Directory.CreateDirectory(Path.Combine(installPath, "Binaries", "Win64")).FullName;
        Directory.CreateDirectory(Path.Combine(installPath, "Saved", "Config", "Windows"));
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[GENERAL]\nx=1\n");
        var exe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "UE Game", InstallPath = installPath, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton",
            EngineHint = "Unreal Engine 4", SteamAppId = 4200
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "luma-records.json"));
        var addon = Path.Combine(bin, "Luma.addon64");
        File.WriteAllText(addon, "x");
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.LumaFramework,
            BinaryDirectory = bin, Files = [new LinuxDeployedFile { Path = addon }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { [LumaIniOverrideManager.MetadataKey] = LumaIniOverrideManager.Serialize(LumaIniOverrideManager.Apply(bin)) }
        });
        var service = new LumaSettingsService(records, null, _ => null);
        var on = await service.SetTaaAsync(game, on: true);
        Assert.True(on.Success, on.Message);
        Assert.True(service.Read(game).TaaOn);

        // Edit game → display name (the card's name changes; the detected name and the record key do not).
        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { DisplayName = "Renamed By The User" }.Normalize());

        Assert.Equal("Renamed By The User", game.DisplayName());
        Assert.True(service.Read(game).TaaOn);
        var off = await service.SetTaaAsync(game, on: false); // and the renamed game's TAA can still be turned off
        Assert.True(off.Success, off.Message);
        Assert.False(service.Read(game).TaaOn);
    }

    // ---- item 8 (a1a4e48, b8094e6): the GitHub token goes to api.github.com only --------------------------------------

    [Theory]
    [InlineData("https://api.github.com/repos/rhi-tests/pack/releases/latest", true)]
    [InlineData("https://API.GitHub.com/repos/rhi-tests/pack/releases/latest", true)]
    [InlineData("https://codeload.github.com/rhi-tests/pack/zip/refs/heads/main", false)]
    [InlineData("https://github.com/rhi-tests/pack/archive/refs/heads/main.zip", false)]
    [InlineData("https://raw.githubusercontent.com/rhi-tests/pack/main/dlss_manifest.json", false)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", false)]
    [InlineData("https://api.github.com.example.test/repos/rhi-tests/pack", false)]
    [InlineData("http://api.github.com/repos/rhi-tests/pack/releases/latest", false)]
    public async Task Pin288_Token_IsSentOnlyToHttpsApiGitHubCom(string url, bool carriesToken)
    {
        var tokenPath = TokenFile("pin288-token");
        var seen = new List<AuthenticationHeaderValue?>();
        using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, new Recording(request =>
        {
            seen.Add(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        using var _ = await http.GetAsync(url);

        var auth = Assert.Single(seen);
        if (carriesToken) Assert.Equal(("Bearer", "pin288-token"), (auth?.Scheme, auth?.Parameter));
        else Assert.Null(auth);
    }

    // The shader pack's case: an api.github.com zipball answered with a redirect to codeload.github.com. The app's
    // HttpClient follows redirects inside the inner handler, below the token handler; the next hop must not carry the
    // token. Real TLS over loopback (both host names answered by one local server), so the platform's redirect
    // handling is what is pinned; a custom header set on the same request reaches codeload (the control).
    [Fact]
    public async Task Pin288_Token_IsNotCarriedToCodeloadAfterARedirect()
    {
        var tokenPath = TokenFile("pin288-redirect-token");
        using var cert = LoopbackCertificate("api.github.com", "codeload.github.com");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new ConcurrentQueue<(string Host, string? Authorization, string? Probe)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                _ = ServeAsync(client, cert, seen);
            }
        });
        try
        {
            var inner = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
            };
            using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, inner));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/rhi-tests/pack/zipball/main");
            request.Headers.Add("X-Rhi-Probe", "carried");

            using var response = await http.SendAsync(request, stop.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync(stop.Token));
            var hops = seen.ToArray();
            Assert.Equal(2, hops.Length);
            Assert.Equal(("api.github.com", "Bearer pin288-redirect-token", "carried"), hops[0]);
            Assert.Equal(("codeload.github.com", (string?)null, "carried"), hops[1]);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ServeAsync(TcpClient client, X509Certificate2 cert, ConcurrentQueue<(string, string?, string?)> seen)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(cert);
                var headers = await ReadHeadersAsync(tls);
                var host = headers.GetValueOrDefault("host")?.Split(':')[0] ?? "";
                seen.Enqueue((host, headers.GetValueOrDefault("authorization"), headers.GetValueOrDefault("x-rhi-probe")));
                var answer = host == "api.github.com"
                    ? "HTTP/1.1 302 Found\r\nLocation: https://codeload.github.com/rhi-tests/pack/legacy.zip/refs/heads/main\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(answer));
                await tls.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // A connection the client dropped; the assertions on what was seen decide the test.
            }
        }
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64 * 1024 && await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString([.. bytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    private static X509Certificate2 LoopbackCertificate(params string[] hosts)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + hosts[0], key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts) names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(created.Export(X509ContentType.Pfx));
    }

    private string TokenFile(string token)
    {
        var path = Path.Combine(_root, "github_api-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, token + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }

    // ---- item 13 (review F1): the production pcgw_data.json selection budget -------------------------------------------

    // PcgwCentralDataTests wait for the data file with a hang guard (de-flake), so nothing else bounds the default: a
    // default cut to zero loses every cold selection's API/engine, a long one stalls the selection on a slow download.
    [Fact]
    public void Pin288_PcgwDataSelectionBudget_IsThreeSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(3), LinuxGameRuntimeProbe.DefaultPcgwDataSelectionBudget);

    // A probe built without a budget (as AppComposition builds it) waits that default for a data file that has not
    // loaded, then carries on. Only the lower bound is tight (a wait cannot end early); the upper one is a loose bound.
    [Fact]
    public async Task Pin288_PcgwDataSelection_WithoutABudget_WaitsTheDefault_ThenCarriesOn()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Held(release.Task));
        var probe = new LinuxGameRuntimeProbe(
            pcgwData: new PcgwCentralDataService(http, Path.Combine(_root, "pcgw_data.json"), "https://example.test/pcgw_data.json", fallbackUrl: null));
        var dir = Directory.CreateDirectory(Path.Combine(_root, "pcgw-budget")).FullName;
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame { Name = "Pin288 Budget", InstallPath = dir, ExecutablePath = exe, Runner = "Proton" };
        try
        {
            var timer = Stopwatch.StartNew();
            await probe.ProbeAsync(game, GameFileIndexer.Build(dir)).WaitAsync(TimeSpan.FromMinutes(2));
            timer.Stop();

            Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(2900), $"selection waited only {timer.ElapsedMilliseconds} ms");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(45), $"selection waited {timer.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Held(Task release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await release.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    // ---- item 9 (b8094e6): shader archive members with ".." or an absolute path are refused ---------------------------

    [Theory]
    [InlineData("reshade-shaders-master/../inside.fx")]   // would land back inside the staging folder: only ".." refuses it
    [InlineData("reshade-shaders-master/Shaders/../../evil.fx")]
    [InlineData("ABSOLUTE")]                              // an absolute member under this test's own folder
    public async Task Pin288_ShaderPackSync_RefusesDotDotAndAbsoluteMembers(string member)
    {
        var outside = Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N") + ".fx");
        if (member == "ABSOLUTE") member = outside;
        using var http = new HttpClient(new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ShaderZip(("reshade-shaders-master/Shaders/Good.fx", "good"), (member, new string('X', 800))))
        }));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "shader-cache-" + Guid.NewGuid().ToString("N")),
            customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Directory.CreateDirectory(Path.Combine(_root, "shader-game-" + Guid.NewGuid().ToString("N"))).FullName;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync(game, ["CrosireMaster"]));

        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(_root, "evil.fx")));
        Assert.False(Directory.Exists(Path.Combine(game, "reshade-shaders")));
    }

    private static byte[] ShaderZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        return memory.ToArray();
    }

    // ---- item 12 (f170aa6 / 865f784): Follow newest Streamline and 2.14.16.0-v2 ---------------------------------------

    private const string SlOld = "2.14.1.0";
    private const string SlOriginal = "2.14.16.0";
    private const string SlV2 = "2.14.16.0-v2";

    // From an older build the game moves to the -v2 build once (its DLLs say 2.14.16.0); the next passes find it
    // current and neither swap nor download again (upstream looped: the DLL's 2.14.16.0 never equals "2.14.16.0-v2").
    [Fact]
    public async Task Pin288_FollowNewestStreamline_MovesToV2Once_ThenStaysPut()
    {
        var game = StreamlineGame("sl-old", SlOld);
        var fx = Follow(streamlineBaseline: SlOld);

        var first = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: false, Streamline: true));
        var payloadsAfterFirst = fx.Http.PayloadRequests;
        var interposer = File.ReadAllBytes(Path.Combine(game.InstallPath, "streamline", "sl.interposer.dll"));
        var second = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: false, Streamline: true));
        var third = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: false, Streamline: true));

        Assert.Equal(1, first.UpdatedCount);
        Assert.Equal(SlV2Bytes(), interposer);
        Assert.Equal((0, 0), (second.UpdatedCount, third.UpdatedCount));
        Assert.Equal(payloadsAfterFirst, fx.Http.PayloadRequests);
        Assert.Equal(interposer, File.ReadAllBytes(Path.Combine(game.InstallPath, "streamline", "sl.interposer.dll")));
        Assert.Equal(SlV2, fx.Store.Load()!["streamline"].Newest);
        Assert.Single(fx.Log.GetAll());
    }

    // The original 2.14.16.0 build has the same numbers as -v2: Follow newest counts it as current (no swap, no download,
    // on every pass). The manual Streamline swap still offers -v2 by its label.
    [Fact]
    public async Task Pin288_FollowNewestStreamline_OriginalBuild_IsCurrentAgainstV2()
    {
        var game = StreamlineGame("sl-original", SlOriginal);
        var before = File.ReadAllBytes(Path.Combine(game.InstallPath, "streamline", "sl.interposer.dll"));
        var fx = Follow(streamlineBaseline: SlOriginal);

        var first = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: false, Streamline: true));
        var second = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: false, Streamline: true));

        Assert.Equal((DlssFollowOutcome.Current, 0), (first.Outcome, first.UpdatedCount));
        Assert.Equal((DlssFollowOutcome.Current, 0), (second.Outcome, second.UpdatedCount));
        Assert.Equal(0, fx.Http.PayloadRequests);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game.InstallPath, "streamline", "sl.interposer.dll")));
        Assert.Empty(fx.Log.GetAll());
    }

    private static byte[] SlV2Bytes() => PeVersionReaderTests.BuildVersionedPe(SlOriginal, SlV2, size: 80_000);

    private DetectedGame StreamlineGame(string name, string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var sl = Directory.CreateDirectory(Path.Combine(dir, "streamline")).FullName;
        foreach (var dll in new[] { "sl.interposer.dll", "sl.common.dll" })
            File.WriteAllBytes(Path.Combine(sl, dll), DlssAutoFollowServiceTests.Pe(version));
        return new DetectedGame { Name = name, InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Source = "Test", Runner = "Wine" };
    }

    private sealed record FollowSetup(DlssAutoFollowService Service, DlssAutoFollowStore Store, ComponentUpdateLogStore Log, V2ManifestHandler Http);

    private FollowSetup Follow(string streamlineBaseline)
    {
        var http = new V2ManifestHandler();
        var dlss = new DlssManagerService(new HttpClient(http), new TransactionalFileDeploymentService(), Path.Combine(_root, "dlss-cache-" + Guid.NewGuid().ToString("N")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "follow-records-" + Guid.NewGuid().ToString("N") + ".json"));
        var store = new DlssAutoFollowStore(Path.Combine(_root, "follow-" + Guid.NewGuid().ToString("N") + ".json"));
        store.Save(new(StringComparer.OrdinalIgnoreCase) { ["streamline"] = new DlssFollowTrack { Newest = streamlineBaseline } });
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "follow-log-" + Guid.NewGuid().ToString("N") + ".json"));
        var service = new DlssAutoFollowService(dlss, new LinuxDlssDefaultsService(dlss, records, new NotRunning()), records, store, log, new NotRunning());
        return new(service, store, log, http);
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    /// <summary>The live manifest's shape after 865f784: Streamline 2.14.16.0-v2 (DLLs that say 2.14.16.0) and 2.14.1.0.</summary>
    internal sealed class V2ManifestHandler : HttpMessageHandler
    {
        private const string Base = "https://github.com/rhi-tests/dlss/releases/download/";
        public int PayloadRequests { get; private set; }

        public static string Manifest => $$"""
            {
              "dlss": [], "dlssd": [], "dlssg": [], "dlssnr": [],
              "streamline": [{ "version": "{{SlV2}}", "url": "{{Base}}v2/streamline.zip" }, { "version": "{{SlOld}}", "url": "{{Base}}old/streamline.zip" }]
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == DlssManagerService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") });
            if (!url.StartsWith(Base, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            PayloadRequests++;
            var v2 = url.Contains("/v2/", StringComparison.Ordinal);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var name in new[] { "sl.interposer.dll", "sl.common.dll" })
                {
                    var entry = zip.CreateEntry(name);
                    entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(v2 ? SlV2Bytes() : DlssAutoFollowServiceTests.Pe(SlOld));
                }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) });
        }
    }
}
