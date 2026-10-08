using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// DEP-06 (#25): RenoDX and other add-on DLLs are fetched only over HTTPS from an explicit host allow-list.
public sealed class TrustedDownloadPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-trusted-download-tests-" + Guid.NewGuid().ToString("N"));

    public TrustedDownloadPolicyTests() => Directory.CreateDirectory(_root);

    [Theory]
    // Shapes taken from upstream 2.8.5 RenoDXdb.json / manifest snapshotOverrides and the redirects they follow.
    [InlineData("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sp=r")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc")]
    [InlineData("https://raw.githubusercontent.com/RankFTW/rhi-repo/main/x.addon64")]
    [InlineData("https://clshortfuse.github.io/renodx/renodx-ff7remake.addon64")]
    [InlineData("https://Marat569.GitHub.io/renodx/renodx-ue-extended.addon64")]
    [InlineData("https://renodx.com/renodx-unrealengine.addon64")]
    public void RenoDxPolicy_AcceptsUpstreamHosts(string url)
        => Assert.True(TrustedDownloadPolicy.RenoDxAddons.IsTrusted(url));

    [Theory]
    [InlineData("http://github.com/clshortfuse/renodx/releases/download/snapshot/x.addon64")]
    [InlineData("http://evil/x.addon64")]
    [InlineData("https://evil.example/x.addon64")]
    [InlineData("https://github.com.evil.example/x.addon64")]
    [InlineData("https://evilgithub.com/x.addon64")]
    [InlineData("https://a.b.github.io/x.addon64")]
    [InlineData("https://github.io/x.addon64")]
    [InlineData("https://www.renodx.com.evil.example/x.addon64")]
    [InlineData("https://user@github.com/x.addon64")]
    [InlineData("https://github.com:8443/x.addon64")]
    [InlineData("https://140.82.112.3/x.addon64")]
    [InlineData("ftp://github.com/x.addon64")]
    [InlineData("/clshortfuse/renodx/x.addon64")]
    [InlineData("")]
    [InlineData(null)]
    public void RenoDxPolicy_RefusesEverythingElse(string? url)
    {
        Assert.False(TrustedDownloadPolicy.RenoDxAddons.IsTrusted(url));
        var refused = Assert.Throws<UntrustedDownloadException>(() => TrustedDownloadPolicy.RenoDxAddons.Validate(url));
        Assert.EndsWith(url ?? "", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubPolicy_DoesNotAcceptPagesOrRenoDxCom()
    {
        Assert.True(TrustedDownloadPolicy.GitHub.IsTrusted("https://github.com/RankFTW/rhi-repo/releases/download/x/y.addon64"));
        Assert.False(TrustedDownloadPolicy.GitHub.IsTrusted("https://clshortfuse.github.io/renodx/x.addon64"));
        Assert.False(TrustedDownloadPolicy.GitHub.IsTrusted("https://renodx.com/x.addon64"));
    }

    [Fact]
    public async Task WikiLink_PlainHttpToAnyHost_IsRefusedBeforeAnyDownload()
    {
        var (game, files) = CreateGame("Wiki Game");
        var handler = new GuardHandler(url => url == RenoDxCatalogService.WikiUrl
            ? Html("<table><tr><td>Wiki Game</td><td><a href=\"http://evil/x.addon64\">snapshot</a></td></tr></table>")
            : null);
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "wiki-records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "wiki-cache"));

        var result = await service.InstallAsync(game, files);

        Assert.False(result.Success);
        Assert.EndsWith("Refused link: http://evil/x.addon64", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*"));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
    }

    [Fact]
    public async Task ManifestSnapshotOverride_PlainHttp_IsRefusedBeforeAnyDownload()
    {
        var (game, files) = CreateGame("Override Game");
        var handler = new GuardHandler(url => url == "https://manifest.test/manifest.json"
            ? Json("""{ "version": 2, "snapshotOverrides": { "Override Game": "http://github.com/x/renodx/releases/download/snapshot/renodx-override.addon64" } }""")
            : null);
        using var http = new HttpClient(handler);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "override-records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "override-cache"), manifest);

        var result = await service.InstallAsync(game, files);

        Assert.False(result.Success);
        Assert.Contains("must be HTTPS on a trusted host", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("renodx-override", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*"));
    }

    [Fact]
    public async Task DatabaseEntry_OnUnlistedHost_IsRefusedAndShownAsBlocked()
    {
        var (game, files) = CreateGame("Database Game");
        var handler = new GuardHandler(url => url == RenoDxCatalogService.StructuredDbUrl
            ? Json("""[ { "name": "Database Game", "snapshotUrl": "https://mods.evil.example/renodx-db.addon64", "author": "Someone" } ]""")
            : null);
        using var http = new HttpClient(handler);
        var catalog = new RenoDxCatalogService(http);
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "db-records.json")), catalog, Path.Combine(_root, "db-cache"));

        var result = await service.InstallAsync(game, files);
        var entry = await catalog.ResolveAsync(game, files);

        Assert.False(result.Success);
        Assert.EndsWith("https://mods.evil.example/renodx-db.addon64", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
        Assert.Equal(result.Message, LinuxManagedComponentService.UntrustedRenoDxLink(entry!));
        Assert.Null(LinuxManagedComponentService.UntrustedRenoDxLink(
            new RenoDxModEntry("Trusted", "https://clshortfuse.github.io/renodx/renodx-x.addon64", null, null)));
    }

    [Fact]
    public async Task RedirectToUnlistedHost_IsRefusedAndNothingIsCachedOrDeployed()
    {
        var (game, files) = CreateGame("Redirect Game");
        const string addon = "https://clshortfuse.github.io/renodx/renodx-redirect.addon64";
        var handler = new GuardHandler(url => url switch
        {
            RenoDxCatalogService.WikiUrl => Html($"<table><tr><td>Redirect Game</td><td><a href=\"{addon}\">snapshot</a></td></tr></table>"),
            addon => Redirected(Pe(), "https://mirror.evil.example/renodx-redirect.addon64"),
            _ => null
        });
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "redirect-cache");
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "redirect-records.json")), new RenoDxCatalogService(http), cache);

        var result = await service.InstallAsync(game, files);

        Assert.False(result.Success);
        Assert.EndsWith("Refused link: https://mirror.evil.example/renodx-redirect.addon64", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*"));
        Assert.False(Directory.Exists(cache) && Directory.EnumerateFiles(cache, "*.addon64*").Any());
    }

    [Fact]
    public async Task UpdateCheck_UntrustedCatalogLink_IsCouldNotCheckWithoutHead()
    {
        var (game, files) = CreateGame("Check Game");
        var handler = new GuardHandler(url => url == RenoDxCatalogService.WikiUrl
            ? Html("<table><tr><td>Check Game</td><td><a href=\"http://evil/renodx-check.addon64\">snapshot</a></td></tr></table>")
            : null);
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "check-records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = game.BinaryDirectory!, SourceUrl = "http://evil/renodx-check.addon64", Version = "Check Game"
        });
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "check-cache"));

        var check = await service.CheckUpdateAsync(game, files);

        Assert.True(check.CouldNotCheck);
        Assert.False(check.UpdateAvailable);
        Assert.Contains("Refused link", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenoDxUpgrade_ManifestLinkOffTheAllowList_IsRefusedBeforeDownload()
    {
        var (game, _) = CreateGame("Upgrade Game");
        var handler = new GuardHandler(url => url == "https://manifest.test/manifest.json"
            ? Json("""{ "version": 2, "addonPacks": { "renodx-upgrade": { "packageName": "RenoDX Upgrade", "description": "ITM", "downloadUrl64": "http://evil/renodx-upgrade.addon64", "repositoryUrl": "https://github.com/OopyDoopy/renodx" } } }""")
            : null);
        using var http = new HttpClient(handler);
        var service = new RenoDxUpgradeDeploymentService(http,
            new LinuxDeploymentRecordStore(Path.Combine(_root, "upgrade-records.json")),
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"),
            Path.Combine(_root, "upgrade-cache"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.EndsWith("Refused link: http://evil/renodx-upgrade.addon64", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReShadeAddonManager_AddonsIniLinkOffTheAllowList_IsRefusedBeforeDownload()
    {
        var (game, _) = CreateGame("Addon Game");
        var handler = new GuardHandler(url => url switch
        {
            ReShadeAddonManagerService.AddonsIniUrl => Text("[01]\nPackageName=Evil Addon\nDownloadUrl64=http://evil/evil.addon64\n"),
            "https://manifest.test/manifest.json" => Json("""{"version":2}"""),
            _ => null
        });
        using var http = new HttpClient(handler);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(_root, "addon-manifest.json"));
        var manager = new ReShadeAddonManagerService(http, manifest, Path.Combine(_root, "addon-cache"), Path.Combine(_root, "custom-addons"), Path.Combine(_root, "addon-state.json"));
        var id = (await manager.RefreshCatalogItemsAsync()).Single(x => x.DisplayName.Contains("Evil", StringComparison.Ordinal)).Id;

        var result = await manager.ApplySelectionAsync(game, [id]);

        Assert.False(result.Success);
        Assert.Contains("Refused link: http://evil/evil.addon64", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DofFix_ManifestTemplateOffGitHub_IsRefusedBeforeDownload()
    {
        var (game, _) = CreateGame("Dof Game");
        game.DofFixForcedByManifest = true;
        game.EngineHint = "Unreal Engine 5";
        var handler = new GuardHandler(url =>
            url.Contains("rhi-repo/releases", StringComparison.Ordinal)
                ? Json("""[ { "tag_name": "ue-dof-fix-1.0.2", "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "https://github.com/RankFTW/rhi-repo/releases/download/ue-dof-fix-1.0.2/renodx-universal_ue-dof-fix.addon64" } ] } ]""")
            : url == "https://manifest.test/manifest.json"
                ? Json("""{ "version": 2, "componentUrls": { "ueDofFix": "https://dof.evil.example/{version}/renodx-universal_ue-dof-fix.addon64" } }""")
                : null);
        using var http = new HttpClient(handler);
        var service = new DofFixDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "dof-records.json")),
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"), Path.Combine(_root, "dof-cache"));

        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => service.InstallAsync(game));

        Assert.Equal("https://dof.evil.example/1.0.2/renodx-universal_ue-dof-fix.addon64", refused.Url);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("evil", StringComparison.Ordinal));
    }

    // ---- integration w1 deploy review 1: each service's own policy, its size cap, and the RenoDX HEAD check ----

    /// <summary>The add-on cap as a product value, not the constant: a test that reads the constant moves with it.</summary>
    private const long AddonCap = 256L * 1024 * 1024;

    [Theory]
    [InlineData("https://someone.github.io/{version}/renodx-universal_ue-dof-fix.addon64")]
    [InlineData("https://renodx.com/{version}/renodx-universal_ue-dof-fix.addon64")]
    public async Task DofFix_UsesTheGitHubPolicy_NotAWiderOne(string template)
    {
        var (game, _) = CreateGame("Dof Pages Game");
        game.DofFixForcedByManifest = true;
        game.EngineHint = "Unreal Engine 5";
        var handler = new GuardHandler(url =>
            url.Contains("rhi-repo/releases", StringComparison.Ordinal)
                ? Json("""[ { "tag_name": "ue-dof-fix-1.0.2", "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "https://github.com/RankFTW/rhi-repo/releases/download/ue-dof-fix-1.0.2/renodx-universal_ue-dof-fix.addon64" } ] } ]""")
                : url == "https://manifest.test/manifest.json"
                    ? Json($$"""{ "version": 2, "componentUrls": { "ueDofFix": "{{template}}" } }""")
                    : null);
        using var http = new HttpClient(handler);
        var service = new DofFixDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "dof-pages-records.json")),
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"), Path.Combine(_root, "dof-pages-cache"));

        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => service.InstallAsync(game));

        Assert.StartsWith(TrustedDownloadPolicy.GitHub.RefusalPrefix, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url.Contains("dof-fix.addon64", StringComparison.Ordinal) && !url.Contains("github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReShadeAddonManager_UsesTheReShadeAddonPolicy_SoRenoDxComIsRefused()
    {
        var (game, _) = CreateGame("Addon Policy Game");
        const string link = "https://renodx.com/some-addon.addon64";
        var handler = new GuardHandler(url => url switch
        {
            ReShadeAddonManagerService.AddonsIniUrl => Text($"[01]\nPackageName=Policy Addon\nDownloadUrl64={link}\n"),
            "https://manifest.test/manifest.json" => Json("""{"version":2}"""),
            _ => null
        });
        using var http = new HttpClient(handler);
        var manager = new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(_root, "policy-manifest.json")),
            Path.Combine(_root, "policy-cache"), Path.Combine(_root, "policy-custom"), Path.Combine(_root, "policy-state.json"));
        var id = (await manager.RefreshCatalogItemsAsync()).Single(x => x.DisplayName.Contains("Policy", StringComparison.Ordinal)).Id;

        var result = await manager.ApplySelectionAsync(game, [id]);

        Assert.False(result.Success);
        Assert.Contains(TrustedDownloadPolicy.ReShadeAddons.RefusalPrefix + link, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requested, url => url == link);
    }

    [Fact]
    public async Task ReShadeAddonManager_AnnouncedSizeOverItsCap_IsRefused()
    {
        var (game, _) = CreateGame("Addon Size Game");
        const string link = "https://github.com/someone/big/releases/download/v1/Big.addon64";
        var handler = new GuardHandler(url => url switch
        {
            ReShadeAddonManagerService.AddonsIniUrl => Text($"[01]\nPackageName=Big Addon\nDownloadUrl64={link}\n"),
            "https://manifest.test/manifest.json" => Json("""{"version":2}"""),
            link => Announced(Pe(), AddonCap + 1),
            _ => null
        });
        using var http = new HttpClient(handler);
        var manager = new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(_root, "size-manifest.json")),
            Path.Combine(_root, "size-cache"), Path.Combine(_root, "size-custom"), Path.Combine(_root, "size-state.json"));
        var id = (await manager.RefreshCatalogItemsAsync()).Single(x => x.DisplayName.Contains("Big", StringComparison.Ordinal)).Id;

        var result = await manager.ApplySelectionAsync(game, [id]);

        Assert.False(result.Success);
        Assert.Contains($"{TrustedDownload.SizeLimitPrefix}{AddonCap}{AtomicFile.SizeLimitMiddle}{AddonCap + 1} bytes", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RenoDxInstall_AnnouncedSizeOverItsCap_IsRefused()
    {
        var (game, files) = CreateGame("Size Game");
        const string addon = "https://clshortfuse.github.io/renodx/renodx-size.addon64";
        var handler = new GuardHandler(url => url switch
        {
            RenoDxCatalogService.WikiUrl => Html($"<table><tr><td>Size Game</td><td><a href=\"{addon}\">snapshot</a></td></tr></table>"),
            addon => Announced(Pe(), AddonCap + 1),
            _ => null
        });
        using var http = new HttpClient(handler);
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "size-records.json")), new RenoDxCatalogService(http), Path.Combine(_root, "renodx-size-cache"));

        var result = await service.InstallAsync(game, files);

        Assert.False(result.Success);
        Assert.Contains($"{TrustedDownload.SizeLimitPrefix}{AddonCap}{AtomicFile.SizeLimitMiddle}", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*"));
    }

    [Fact]
    public async Task DofFix_AnnouncedSizeOverItsCap_IsRefused()
    {
        var (game, _) = CreateGame("Dof Size Game");
        game.DofFixForcedByManifest = true;
        game.EngineHint = "Unreal Engine 5";
        const string asset = "https://github.com/RankFTW/rhi-repo/releases/download/ue-dof-fix-1.0.2/renodx-universal_ue-dof-fix.addon64";
        var handler = new GuardHandler(url =>
            url == asset ? Announced(Pe(), AddonCap + 1)
            : url.Contains("rhi-repo/releases", StringComparison.Ordinal)
                ? Json($$"""[ { "tag_name": "ue-dof-fix-1.0.2", "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "{{asset}}" } ] } ]""")
                : url == "https://manifest.test/manifest.json" ? Json("""{ "version": 2 }""") : null);
        using var http = new HttpClient(handler);
        var service = new DofFixDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "dof-size-records.json")),
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"), Path.Combine(_root, "dof-size-cache"));

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(game));

        Assert.StartsWith($"{TrustedDownload.SizeLimitPrefix}{AddonCap}{AtomicFile.SizeLimitMiddle}", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamedBodyOverThePolicyCap_IsRefusedAndLeavesNothing()
    {
        // No Content-Length: the cap is enforced while the body streams (AtomicFile.StageStreamAsync).
        var dir = Directory.CreateDirectory(Path.Combine(_root, "streamed")).FullName;
        var handler = new GuardHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableStream(new byte[8192])) });
        using var http = new HttpClient(handler);

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => TrustedDownload.DownloadToTempAsync(
            http, "https://github.com/o/r/releases/download/v1/x.addon64", Path.Combine(dir, "x.addon64"), TrustedDownloadPolicy.GitHub with { MaxBytes = 4096 }));

        Assert.StartsWith($"{AtomicFile.SizeLimitPrefix}4096{AtomicFile.SizeLimitMiddle}", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task RenoDxInstall_CachedAddonWhoseHeadIsRedirectedOffTheList_Fails()
    {
        // A refused HEAD redirect must not read as "could not ask" (which reuses the cache) (deploy review 1, M22).
        var (first, firstFiles) = CreateGame("Head Game One");
        var (second, secondFiles) = CreateGame("Head Game Two");
        const string addon = "https://clshortfuse.github.io/renodx/renodx-head.addon64";
        var redirectHead = false;
        var handler = new GuardHandler((url, method) => url switch
        {
            RenoDxCatalogService.WikiUrl => Html($"<table><tr><td>Head Game One</td><td><a href=\"{addon}\">snapshot</a></td></tr><tr><td>Head Game Two</td><td><a href=\"{addon}\">snapshot</a></td></tr></table>"),
            addon when method == HttpMethod.Head && redirectHead => RedirectedHead("https://mirror.evil.example/renodx-head.addon64"),
            addon => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pe()) { Headers = { LastModified = DateTimeOffset.UnixEpoch.AddDays(20000) } } },
            _ => null
        });
        using var http = new HttpClient(handler);
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "head-records.json")), new RenoDxCatalogService(http), Path.Combine(_root, "head-cache"));
        var installed = await service.InstallAsync(first, firstFiles);
        Assert.True(installed.Success, installed.Message);

        redirectHead = true;
        var result = await service.InstallAsync(second, secondFiles);

        Assert.False(result.Success);
        Assert.EndsWith("Refused link: https://mirror.evil.example/renodx-head.addon64", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(second.BinaryDirectory!, "*.addon*"));
    }

    [Fact]
    public void SizeRefusals_ArePresentedInPortuguese()
    {
        // One message per cap, built from the producers' own constants (TrustedDownload announced size, AtomicFile streamed).
        var announced = $"{TrustedDownload.SizeLimitPrefix}268435456{AtomicFile.SizeLimitMiddle}268435457 bytes";
        Assert.Equal("O download é maior que o limite de tamanho (268435456 bytes): 268435457 bytes",
            UiLocalizationService.PresentFor("pt-BR", announced));
        Assert.Equal("O conteúdo excede o limite de tamanho (4096 bytes): x.addon64",
            UiLocalizationService.PresentFor("pt-BR", $"{AtomicFile.SizeLimitPrefix}4096{AtomicFile.SizeLimitMiddle}x.addon64"));
    }

    [Fact]
    public void RefusalMessages_ArePresentedInPortuguese()
    {
        foreach (var policy in new[] { TrustedDownloadPolicy.RenoDxAddons, TrustedDownloadPolicy.ReShadeAddons, TrustedDownloadPolicy.GitHub, TrustedDownloadPolicy.GitHubReleases, RHI.Core.Services.DlssManagerService.DownloadPolicy })
        {
            var message = new UntrustedDownloadException("http://evil/x.addon64", policy).Message;
            var pt = UiLocalizationService.PresentFor("pt-BR", message);
            Assert.StartsWith("Download recusado:", pt, StringComparison.Ordinal);
            Assert.EndsWith("Link recusado: http://evil/x.addon64", pt, StringComparison.Ordinal);
            Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
        }
    }

    private (DetectedGame Game, string[] Files) CreateGame(string name)
    {
        var bin = Path.Combine(_root, name.Replace(' ', '-'), "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var reshade = Path.Combine(bin, "ReShade64.dll");
        File.WriteAllText(reshade, "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", Source = "Steam"
        };
        return (game, [exe, reshade]);
    }

    private static byte[] Pe()
    {
        var bytes = new byte[120 * 1024];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
        return bytes;
    }

    /// <summary>A response announcing <paramref name="length"/> bytes (the body itself stays small).</summary>
    private static HttpResponseMessage Announced(byte[] bytes, long length)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage RedirectedHead(string finalUrl) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([]),
        RequestMessage = new HttpRequestMessage(HttpMethod.Head, finalUrl)
    };

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // What HttpClient reports after following a redirect: the response's request is the final hop.
    private static HttpResponseMessage Redirected(byte[] bytes, string finalUrl) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl)
    };

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // Serves the routes given; any other URL is 404. Records every request so tests can prove an untrusted link was never fetched.
    private sealed class GuardHandler(Func<string, HttpMethod, HttpResponseMessage?> route) : HttpMessageHandler
    {
        public GuardHandler(Func<string, HttpResponseMessage?> route) : this((url, _) => route(url)) { }

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            lock (Requested) Requested.Add(url);
            return Task.FromResult(route(url, request.Method) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
