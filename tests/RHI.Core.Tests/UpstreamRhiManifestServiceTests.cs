using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class UpstreamRhiManifestServiceTests
{
    [Fact]
    public async Task FetchAddonPacks_ParsesReleaseApiAndDeployFilenameFields()
    {
        var json = """
        {
          "version": 2,
          "addonPacks": {
            "dlss5-feed": {
              "packageName": "DLSS5 Feeder",
              "description": "Synthetic DLSS contract feeder",
              "releaseApiUrl": "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest",
              "repositoryUrl": "https://github.com/jlrouzies-fr/DLSS5-Feeder",
              "deployFileName": "dlss5-feed.addon64"
            },
            "disabled-addon": {
              "packageName": "Disabled",
              "downloadUrl64": "https://example.test/disabled.addon64",
              "disabled": true
            }
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var packs = await service.FetchAddonPacksAsync();

        var feeder = packs["dlss5-feed"];
        Assert.Equal("DLSS5 Feeder", feeder.PackageName);
        Assert.Equal("https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest", feeder.ReleaseApiUrl);
        Assert.Equal("dlss5-feed.addon64", feeder.DeployFileName);
        Assert.False(feeder.Disabled);
        Assert.True(packs["disabled-addon"].Disabled);
    }

    [Fact]
    public async Task FetchAddonPacks_IgnoresEntriesWithoutPackageName()
    {
        var json = """
        {
          "version": 2,
          "addonPacks": {
            "valid": { "packageName": "Valid Addon" },
            "invalid": { "description": "missing name" }
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var packs = await service.FetchAddonPacksAsync();

        Assert.Single(packs);
        Assert.True(packs.ContainsKey("valid"));
    }


    [Fact]
    public async Task FetchLumaPolicyFields_ParsesRenoDxCompatCaseInsensitively()
    {
        var json = """
        {
          "version": 2,
          "lumaRenodxCompat": [ "Compat Game", " compat game " ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var compat = await service.FetchLumaRenoDxCompatAsync();

        Assert.Single(compat);
        Assert.True(compat.Contains("COMPAT GAME"));
    }

    [Fact]
    public async Task FetchLumaDx9Fields_ParsesDgVoodooGamesAndVersionMap()
    {
        var json = """
        {
          "version": 2,
          "lumaRequiresDgVoodoo": [ "Borderlands 2", " borderlands 2 ", "Vanquish" ],
          "dgVoodooVersions": {
            "2.87.4": "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.87.4/dgVoodoo2_87_4.zip",
            "2.87.3": "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.87.3/dgVoodoo2_87_3.zip"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var games = await service.FetchLumaRequiresDgVoodooAsync();
        var versions = await service.FetchDgVoodooVersionsAsync();

        Assert.Equal(2, games.Count);
        Assert.True(games.Contains("BORDERLANDS 2"));
        Assert.True(games.Contains("vanquish"));
        Assert.Equal(2, versions.Count);
        Assert.Contains("dgVoodoo2_87_4.zip", versions["2.87.4"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesEngineSnapshotAndBitnessFields()
    {
        var json = """
        {
          "version": 2,
          "engineHintOverrides": {
            "Hogwarts Legacy": "Unreal Engine 4.27"
          },
          "snapshotOverrides": {
            "Hogwarts Legacy": "https://marat569.github.io/renodx/renodx-ue-extended.addon64"
          },
          "thirtyTwoBitGames": [ "Legacy X86 Game" ],
          "sixtyFourBitGames": [ "Baldur's Gate 3" ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var overrides = await service.FetchGameOverridesAsync();

        Assert.Equal("Unreal Engine 4.27", overrides["Hogwarts Legacy"].EngineHint);
        Assert.Equal(
            "https://marat569.github.io/renodx/renodx-ue-extended.addon64",
            overrides["Hogwarts Legacy"].SnapshotUrl);
        Assert.Equal(RHI.Core.Models.PeArchitecture.X64, overrides["Baldur's Gate 3"].ForcedArchitecture);
        Assert.Equal(RHI.Core.Models.PeArchitecture.X86, overrides["Legacy X86 Game"].ForcedArchitecture);
    }


    [Fact]
    public async Task FetchLibraryPolicy_ParsesEngineIniFilesAndAddsTheirGameNamesToCanonicalAuthority()
    {
        var json = """
        {
          "version": 3,
          "engineIniFiles": {
            "Black Myth: Wukong": "black-myth-wukong.ini"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var policy = await service.FetchLibraryPolicyAsync();

        Assert.Equal("black-myth-wukong.ini", policy.EngineIniFiles["black myth: wukong"]);
        Assert.Contains(policy.KnownGameNames, name => name.Equals("Black Myth: Wukong", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("DirectX12", RHI.Core.Models.GraphicsApi.DirectX12)]
    [InlineData("DX12", RHI.Core.Models.GraphicsApi.DirectX12)]
    [InlineData("d3d11", RHI.Core.Models.GraphicsApi.DirectX11)]
    [InlineData("Vulkan", RHI.Core.Models.GraphicsApi.Vulkan)]
    [InlineData("OpenGL", RHI.Core.Models.GraphicsApi.OpenGL)]
    public void ParseGraphicsApi_AcceptsPublishedAndCompatibilitySpellings(string value, RHI.Core.Models.GraphicsApi expected)
        => Assert.Equal(expected, UpstreamRhiManifestService.ParseGraphicsApi(value));

    [Fact]
    public void ParseGraphicsApis_AcceptsUpstreamDualApiAliases()
    {
        var values = UpstreamRhiManifestService.ParseGraphicsApis("DX12, VLK, OGL, DX12");

        Assert.Equal(3, values.Count);
        Assert.Equal(RHI.Core.Models.GraphicsApi.DirectX12, values[0]);
        Assert.Equal(RHI.Core.Models.GraphicsApi.Vulkan, values[1]);
        Assert.Equal(RHI.Core.Models.GraphicsApi.OpenGL, values[2]);
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesGraphicsApiOverrides()
    {
        var json = """
        {
          "version": 2,
          "graphicsApiOverrides": {
            "Clair Obscur: Expedition 33": "DirectX12",
            "PRAGMATA": "DX12",
            "Legacy Renderer": "Vulkan",
            "Dual Renderer": "DX12, VLK"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var overrides = await service.FetchGameOverridesAsync();

        Assert.Equal(RHI.Core.Models.GraphicsApi.DirectX12, overrides["Clair Obscur: Expedition 33"].GraphicsApiOverride);
        Assert.Equal(RHI.Core.Models.GraphicsApi.DirectX12, overrides["PRAGMATA"].GraphicsApiOverride);
        Assert.Equal(RHI.Core.Models.GraphicsApi.Vulkan, overrides["Legacy Renderer"].GraphicsApiOverride);
        Assert.Equal(
            new[] { RHI.Core.Models.GraphicsApi.DirectX12, RHI.Core.Models.GraphicsApi.Vulkan },
            overrides["Dual Renderer"].GraphicsApiOverrides);
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesEngineIniPathOverrideAuthority()
    {
        var json = """
        {
          "version": 2,
          "engineIniPathOverrides": {
            "Directive 8020": "Directive8020",
            "Echoes of Aincrad": "%LOCALAPPDATA%\\EchoesofAincrad\\Saved\\Config"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var overrides = await service.FetchGameOverridesAsync();

        Assert.Equal("Directive8020", overrides["Directive 8020"].EngineIniProjectOverride);
        Assert.Equal(@"%LOCALAPPDATA%\EchoesofAincrad\Saved\Config", overrides["Echoes of Aincrad"].EngineIniProjectOverride);
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesEngineOverrideForReEngineParity()
    {
        var json = """
        {
          "version": 2,
          "engineOverrides": {
            "Street Fighter™ 6": "RE Engine"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var resolved = await service.ResolveGameOverrideAsync("street fighter™ 6");

        Assert.NotNull(resolved);
        Assert.Equal("RE Engine", resolved!.EngineOverride);
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesRenoDxIniOverridesAsCaseInsensitivePerGameValues()
    {
        var json = """
        {
          "version": 2,
          "renodxIniOverrides": {
            "Stellar Blade™": {
              "Set_Path": "1",
              "Upgrade_R8G8B8A8_TYPELESS": "3"
            }
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var resolved = await service.ResolveGameOverrideAsync("stellar blade™");

        Assert.NotNull(resolved);
        Assert.Equal("1", resolved!.RenoDxIniOverrides["set_path"]);
        Assert.Equal("3", resolved.RenoDxIniOverrides["upgrade_r8g8b8a8_typeless"]);
    }

    [Fact]
    public async Task FetchRenoDxExtraSettings_ParsesCustomOptionsAndFallbackToggle()
    {
        var json = """
        {
          "version": 2,
          "renodxExtraSettings": [
            {
              "key": "Upgrade_CopyDestinations",
              "label": "Upgrade Copy Destinations",
              "default": "2",
              "options": [
                { "value": "0", "name": "Off" },
                { "value": "2", "name": "All copies" }
              ]
            },
            { "key": "ManifestToggle", "default": "1" },
            { "key": "   " }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var settings = await service.FetchRenoDxExtraSettingsAsync();

        Assert.Equal(2, settings.Count);
        var copy = settings.Single(x => x.Key == "Upgrade_CopyDestinations");
        Assert.Equal("Upgrade Copy Destinations", copy.Label);
        Assert.Equal("2", copy.DefaultValue);
        Assert.Equal(new[] { "0", "2" }, copy.Options.Select(x => x.Value).ToArray());
        var toggle = settings.Single(x => x.Key == "ManifestToggle");
        Assert.Equal("ManifestToggle", toggle.Label);
        Assert.Equal(new[] { "Off", "On" }, toggle.Options.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task FetchGameOverrides_ParsesDofFixForceSkipAndComponentUrl()
    {
        var json = """
        {
          "version": 2,
          "dofFixForceGames": [ "Clair Obscur: Expedition 33" ],
          "dofFixSkipGames": [ "Known Broken UE5 Game" ],
          "componentUrls": {
            "ueDofFix": "https://cdn.example.test/{version}/fix.addon64"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var expedition = await service.ResolveGameOverrideAsync("clair obscur: expedition 33");
        var skipped = await service.ResolveGameOverrideAsync("KNOWN BROKEN UE5 GAME");
        var url = await service.FetchComponentUrlAsync("UEDOFFIX");

        Assert.NotNull(expedition);
        Assert.True(expedition!.DofFixForced);
        Assert.False(expedition.DofFixSkipped);
        Assert.NotNull(skipped);
        Assert.True(skipped!.DofFixSkipped);
        Assert.Equal("https://cdn.example.test/{version}/fix.addon64", url);
    }

    [Fact]
    public async Task ResolveGameOverride_LinuxSideloadUsesCanonicalManifestTitleFromPath()
    {
        var json = """
        {
          "version": 2,
          "dofFixForceGames": [ "Clair Obscur: Expedition 33" ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var game = new RHI.Core.Models.DetectedGame
        {
            Name = "Expedition 33",
            InstallPath = "/home/user/Downloads/Clair Obscur. Expedition 33 (2025)/Expedition 33",
            BinaryDirectory = "/home/user/Downloads/Clair Obscur. Expedition 33 (2025)/Expedition 33/Sandfall/Binaries/Win64",
            ExecutablePath = "/home/user/Downloads/Clair Obscur. Expedition 33 (2025)/Expedition 33/Sandfall/Binaries/Win64/SandFall-Win64-Shipping.exe"
        };

        var resolved = await service.ResolveGameOverrideAsync(game);

        Assert.NotNull(resolved);
        Assert.Equal("Clair Obscur: Expedition 33", resolved!.GameName);
        Assert.True(resolved.DofFixForced);
    }

    [Fact]
    public async Task ManifestDocument_IsCachedAcrossAddonAndGameOverrideReads()
    {
        var json = """
        {
          "version": 2,
          "addonPacks": {
            "mfgunlock": { "packageName": "MFG Ada Unlock" }
          },
          "sixtyFourBitGames": [ "Baldur's Gate 3" ]
        }
        """;
        var handler = new CountingHandler(json);
        using var http = new HttpClient(handler);
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        _ = await service.FetchAddonPacksAsync();
        var bg3 = await service.ResolveGameOverrideAsync("baldur's gate 3");

        Assert.NotNull(bg3);
        Assert.Equal(RHI.Core.Models.PeArchitecture.X64, bg3!.ForcedArchitecture);
        Assert.Equal(1, handler.RequestCount);
    }


    [Fact]
    public async Task FetchLumaNameOverrides_ParsesCaseInsensitivePinnedMappings()
    {
        var json = """
        {
          "version": 2,
          "lumaNameOverrides": {
            "Batman™: Arkham Knight": "Batman Arkham Knight",
            "Nioh: Complete Edition": "Nioh"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var overrides = await service.FetchLumaNameOverridesAsync();

        Assert.Equal("Batman Arkham Knight", overrides["batman™: arkham knight"]);
        Assert.Equal("Nioh", overrides["NIOH: COMPLETE EDITION"]);
    }

    [Fact]
    public async Task ResolveLumaNameOverride_LinuxPathCanSupplyUniqueCanonicalManifestIdentity()
    {
        var json = """
        {
          "version": 2,
          "lumaNameOverrides": {
            "Batman™: Arkham Knight": "Batman Arkham Knight"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var game = new RHI.Core.Models.DetectedGame
        {
            Name = "ArkhamKnight",
            InstallPath = "/games/Batman™: Arkham Knight",
            BinaryDirectory = "/games/Batman™: Arkham Knight/Binaries/Win64",
            ExecutablePath = "/games/Batman™: Arkham Knight/Binaries/Win64/BatmanAK.exe"
        };

        var mapped = await service.ResolveLumaNameOverrideAsync(game);

        Assert.Equal("Batman Arkham Knight", mapped);
    }


    [Fact]
    public async Task FetchLibraryPolicy_ParsesCrossPlatformPolicySurface()
    {
        var json = """
        {
          "version": 7,
          "blacklist": ["Tool"],
          "blacklistPrefixes": ["Battle.net."],
          "wikiNameOverrides": {"Game A":"Wiki A"},
          "installPathOverrides": {"Game A":"bin\\x64"},
          "splitGames": {"Bundle":[{"name":"Child","subPath":"ChildDir"}]},
          "steamAppIdOverrides": {"Game A":1234},
          "wikiUnlinks": ["Game B"],
          "dlssSkipGames": ["Game C"],
          "ueExtendedGames": ["Game D"],
          "nativeHdrGames": ["Game E"],
          "noUeExtendedGames": ["Game F"],
          "ueExtendedCompatibility": {"Game G":{"hdr":false,"lut":true}},
          "forceExternalOnly": {"Game A":{"url":"https://mods.test/a","label":"Get Mod"}},
          "dllNameOverrides": {"Game A":{"reshade":"winmm.dll","dc":"version.dll"}},
          "optiScalerDllOverrides": {"Game A":"dxgi.dll"},
          "launchExeOverrides": {"Game A":"GameA.exe"},
          "pdUpscalerGames": {"Game A":"RE4"},
          "wikiStatusOverrides": {"Game A":"✅"},
          "nexusUrlOverrides": {"Game A":"https://nexus.test/a"},
          "pcgwUrlOverrides": {"Game A":"https://pcgw.test/a"},
          "uwFixUrlOverrides": {"Game A":"https://uw.test/a"},
          "ultraPlusUrlOverrides": {"Game A":"https://ultra.test/a"},
          "authorOverrides": {"Game A":"Author"},
          "installWarnings": {"Game A":{"reshade":"warning"}},
          "gameNotes": {"Game A":{"notes":"note","notesUrl":"https://note.test","notesUrlLabel":"Read"}}
        }
        """;
        using var http = new HttpClient(new CountingHandler(json));
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");

        var policy = await service.FetchLibraryPolicyAsync();

        Assert.Equal(7, policy.Version);
        Assert.True(policy.KnownGameNames.Contains("game a"));
        Assert.True(policy.KnownGameNames.Contains("child"));
        Assert.True(policy.Blacklist.Contains("tool"));
        Assert.Equal("Wiki A", policy.WikiNameOverrides["game a"]);
        Assert.Equal("Child", Assert.Single(policy.SplitGames["bundle"]).Name);
        Assert.Equal(1234, policy.SteamAppIdOverrides["GAME A"]);
        Assert.Equal(true, policy.UeExtendedCompatibility["game g"].Lut);
        Assert.Equal(false, policy.UeExtendedCompatibility["game g"].Hdr);
        Assert.Equal("winmm.dll", policy.DllNameOverrides["game a"].ReShade);
        Assert.Equal("warning", policy.InstallWarnings["game a"]["RESHade"]);
        Assert.Equal("note", policy.GameNotes["game a"].Notes);
    }

    [Fact]
    public async Task ManifestFetch_UsesLastKnownGoodCacheWhenRemoteIsOffline()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "rhi-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(cachePath, "{\"version\":9,\"blacklist\":[\"Cached Tool\"]}");
            using var http = new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));
            var service = new UpstreamRhiManifestService(
                http,
                "https://offline.test/manifest.json",
                cachePath: cachePath);

            var policy = await service.FetchLibraryPolicyAsync();

            Assert.Equal(9, policy.Version);
            Assert.True(policy.Blacklist.Contains("cached tool"));
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Fact]
    public async Task ManifestFetch_DefaultAuthorityFallsBackToPinnedUpstreamSnapshot()
    {
        var handler = new RouteHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri == UpstreamRhiManifestService.LiveManifestUrl)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri?.AbsoluteUri == UpstreamRhiManifestService.PinnedManifestUrl)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":2,\"lumaRenodxCompat\":[\"Fallback Game\"]}", Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var cachePath = Path.Combine(Path.GetTempPath(), "rhi-manifest-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var service = new UpstreamRhiManifestService(http, fallbackManifestUrl: UpstreamRhiManifestService.PinnedManifestUrl, cachePath: cachePath);

        var compat = await service.FetchLumaRenoDxCompatAsync();

        Assert.True(compat.Contains("fallback game"));
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }


    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(route(request));
        }
    }

    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
