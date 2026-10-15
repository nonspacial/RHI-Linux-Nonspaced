using System.Runtime.Versioning;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class RenoDxInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-install-tests-" + Guid.NewGuid().ToString("N"));
    public RenoDxInstallTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task InstallAndRemove_SpecificCatalogMatch_IsTrackedAndProtected()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "StreetFighter6.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Street Fighter™ 6", InstallPath = _root, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 1364780
        };
        var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"));

        var install = await service.InstallAsync(game, new[] { exe, Path.Combine(bin, "ReShade64.dll") });
        Assert.True(install.Success, install.Message);
        var target = Path.Combine(bin, "renodx-sf6.addon64");
        Assert.True(File.Exists(target));
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDX));

        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
    }


    [Fact]
    public async Task Install_ManifestExternalAndWikiUnlinkPoliciesBlockDirectDeployment()
    {
        using var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "policy-records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "policy-cache"));

        var external = new DetectedGame
        {
            Name = "External Game", Runner = "Proton", ExternalModUrl = "https://mods.test/external", ExternalModLabel = "Get Mod"
        };
        var unlinked = new DetectedGame
        {
            Name = "Unlinked Game", Runner = "Proton", WikiUnlinkedByManifest = true
        };

        var externalResult = await service.InstallAsync(external, []);
        var unlinkedResult = await service.InstallAsync(unlinked, []);

        Assert.False(externalResult.Success);
        Assert.Contains("forceExternalOnly", externalResult.Message);
        Assert.False(unlinkedResult.Success);
        Assert.Contains("wikiUnlinks", unlinkedResult.Message);
    }

    [Fact]
    public async Task InstallAndRemove_ManifestRenoDxIniOverride_RestoresPriorValueAndPreservesUnrelatedKeys()
    {
        var bin = Path.Combine(_root, "manifest-bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "ManifestGame.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\nKeepMe=abc\n");
        var game = new DetectedGame
        {
            Name = "Manifest Game", InstallPath = _root, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 424242
        };
        using var http = new HttpClient(new ManifestHandler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "manifest-records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var catalog = new RenoDxCatalogService(http, manifest);
        var service = new RenoDxDeploymentService(http, store, catalog, Path.Combine(_root, "manifest-cache"), manifest);

        var install = await service.InstallAsync(game, new[] { exe, Path.Combine(bin, "ReShade64.dll") });

        Assert.True(install.Success, install.Message);
        var during = File.ReadAllText(ini);
        Assert.Contains("Set_Path=1", during);
        Assert.Contains("KeepMe=abc", during);
        var record = store.Find(game, ManagedComponentKind.RenoDX);
        Assert.NotNull(record);
        Assert.True(record!.Metadata.ContainsKey(RenoDxIniOverrideManager.MetadataKey));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        var after = File.ReadAllText(ini);
        Assert.Contains("Set_Path=0", after);
        Assert.Contains("KeepMe=abc", after);
        Assert.False(File.Exists(Path.Combine(bin, "renodx-manifest.addon64")));
    }

    // #121: when removal fails after the [renodx] keys were taken out, ReShade.ini goes back to the installed state,
    // so the record still describes the disk and a retry finishes the job.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Remove_AddonDeleteFails_PutsReShadeIniBackAndKeepsTheRecord()
    {
        var bin = Path.Combine(_root, "remove-fail-bin");
        var addons = Path.Combine(bin, "addons");
        Directory.CreateDirectory(addons);
        var exe = Path.Combine(bin, "ManifestGame.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[ADDON]\nAddonPath=.\\addons\n[renodx]\nSet_Path=0\n");
        var game = new DetectedGame
        {
            Name = "Manifest Game", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", SteamAppId = 434343
        };
        using var http = new HttpClient(new ManifestHandler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "remove-fail-records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "remove-fail-cache"), manifest);
        Assert.True((await service.InstallAsync(game, [exe, Path.Combine(bin, "ReShade64.dll")])).Success);
        var installed = File.ReadAllBytes(ini);

        File.SetUnixFileMode(addons, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        ComponentOperationResult failed;
        try { failed = await service.RemoveAsync(game); }
        finally { File.SetUnixFileMode(addons, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        Assert.False(failed.Success);
        Assert.Equal(installed, File.ReadAllBytes(ini));
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDX));
        var retry = await service.RemoveAsync(game);
        Assert.True(retry.Success, retry.Message);
        Assert.Contains("Set_Path=0", File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(addons, "renodx-manifest.addon64")));
    }

    [Fact]
    public async Task InstallAndRemove_EngineIniFileTemplate_IsTransactionalAndOwnedPerKey()
    {
        var root = Path.Combine(_root, "engine-template-game");
        var bin = Path.Combine(root, "Game", "Binaries", "Win64");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local"));
        var exe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Engine Template Game",
            InstallPath = root,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Runner = "Proton",
            Source = "Steam",
            EngineIniProjectOverride = "EngineTemplateProject",
            EngineIniFileOverride = "engine-template.ini"
        };
        using var http = new HttpClient(new EngineTemplateHandler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/engine-manifest.json");
        var catalog = new RenoDxCatalogService(http, manifest);
        var service = new RenoDxDeploymentService(http, store, catalog, Path.Combine(root, "cache"), manifest);

        var install = await service.InstallAsync(game, [exe, Path.Combine(bin, "ReShade64.dll")]);

        Assert.True(install.Success, install.Message);
        var engineIni = UpstreamEngineIniFileManager.ResolveTargetPath(game)!;
        Assert.True(File.Exists(engineIni));
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(engineIni));
        var record = store.Find(game, ManagedComponentKind.RenoDX);
        Assert.NotNull(record);
        Assert.True(record!.Metadata.ContainsKey(UpstreamEngineIniFileManager.MetadataKey));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(engineIni));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
    }

    // w2 merge review, minor 1 and nit 5: the upstream Engine.ini template is fetched before the write gate, under its
    // own host policy, and applied after it; nothing is downloaded once the add-on is being copied.
    [Fact]
    public async Task Install_EngineIniTemplate_IsFetchedBeforeTheWriteGate()
    {
        var root = Path.Combine(_root, "engine-template-gate");
        var bin = Path.Combine(root, "Game", "Binaries", "Win64");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local"));
        var exe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Engine Template Game", InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", Source = "Steam", EngineIniProjectOverride = "EngineTemplateProject", EngineIniFileOverride = "engine-template.ini"
        };
        var watch = new GateWatchingHandler(new EngineTemplateHandler());
        using var http = new HttpClient(watch);
        var store = new LinuxDeploymentRecordStore(Path.Combine(root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/engine-manifest.json");
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(root, "cache"), manifest);

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, [exe, Path.Combine(bin, "ReShade64.dll")], ct),
            [Path.Combine(root, "Game"), prefix], atWritePhase: watch.CloseGate);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Empty(watch.LateRequests);
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(UpstreamEngineIniFileManager.ResolveTargetPath(game)!));
    }

    [Fact]
    public async Task EngineIniTemplate_IsFetchedUnderItsHostPolicy()
    {
        Assert.Equal(["raw.githubusercontent.com"], DownloadSources.EngineIniTemplates.AllowedHosts);
        Assert.Equal(256L * 1024, DownloadSources.EngineIniTemplates.MaxBytes);
        // A redirect off raw.githubusercontent.com is refused (no cache to fall back to): the policy's own refusal.
        using var http = new HttpClient(new RedirectingHandler("https://example.test/engine-files/x.ini"));
        var manager = new UpstreamEngineIniFileManager(http, Path.Combine(_root, "engine-policy-cache"));

        var refused = await Assert.ThrowsAsync<RHI.Core.Deployment.UntrustedDownloadException>(() => manager.FetchAsync("x.ini"));

        Assert.StartsWith(DownloadSources.EngineIniTemplates.RefusalPrefix, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers every request as if it had been redirected to <paramref name="finalUrl"/>.</summary>
    private sealed class RedirectingHandler(string finalUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new StringContent("[SystemSettings]\nr.Probe=1\n")
            });
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; a late cancel used to throw after
    // the add-on and its INI keys were in place, leaving them with no record.
    private (RenoDxDeploymentService Service, LinuxDeploymentRecordStore Store, DetectedGame Game, string[] Files) CancelFixture(string name)
    {
        var gameRoot = Path.Combine(_root, name, "game");
        var bin = Path.Combine(gameRoot, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "StreetFighter6.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Street Fighter™ 6", InstallPath = gameRoot, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 1364780
        };
        var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, name, "cache"));
        return (service, store, game, [exe, Path.Combine(bin, "ReShade64.dll")]);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (service, store, game, files) = CancelFixture("cancel-install");

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, files, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDX));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-sf6.addon64")));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var (service, store, game, files) = CancelFixture("cancel-remove");
        Assert.True((await service.InstallAsync(game, files)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-sf6.addon64")));
    }

    private sealed class EngineTemplateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url == "https://example.test/engine-manifest.json")
            {
                const string json = """
                {
                  "version": 3,
                  "snapshotOverrides": {
                    "Engine Template Game": "https://clshortfuse.github.io/renodx/renodx-engine-template.addon64"
                  },
                  "engineIniFiles": {
                    "Engine Template Game": "engine-template.ini"
                  }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url == UpstreamEngineIniFileManager.BaseUrl + "engine-template.ini")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[SystemSettings]\nr.HDR.EnableHDROutput=1\n", Encoding.UTF8, "text/plain")
                });
            }
            if (url == "https://clshortfuse.github.io/renodx/renodx-engine-template.addon64")
            {
                var bytes = new byte[120 * 1024];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ManifestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url == "https://example.test/manifest.json")
            {
                const string json = """
                {
                  "version": 2,
                  "renodxIniOverrides": {
                    "Manifest Game": { "Set_Path": "1" }
                  }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><td>Manifest Game</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-manifest.addon64\">snapshot</a></td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }

            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><th>Name</th><th>Links</th><th>Status</th></tr><tr><td>Street Fighter 6</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-sf6.addon64\">snapshot</a></td><td>✅</td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
