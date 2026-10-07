using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxCompatibilitySettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-compat-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxCompatibilitySettingsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Snapshot_MergesBaseCompatibilityKeysAndManifestExtraRows()
    {
        var (game, ini) = CreateGame(
            "[renodx]\n" +
            "Upgrade_R8G8B8A8_TYPELESS=3\n" +
            "Upgrade_CopyDestinations=2\n" +
            "Upgrade_UseSCRGB=1\n" +
            "Set_Path=1\n" +
            "DumpLUTShaders=0\n" +
            "KeepMe=abc\n");
        using var http = new HttpClient(new ManifestOnlyHandler(ManifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var snapshot = await service.GetCompatibilitySettingsAsync(game);

        Assert.True(snapshot.HasRenoDxSection);
        Assert.Equal(5, snapshot.Settings.Count);
        Assert.Contains(snapshot.Settings, x => x.Key == "Upgrade_R8G8B8A8_TYPELESS" && x.CurrentValue == "3" && !x.ManifestDriven
            && x.Options.Any(o => o.Name == "Desativado") && x.Options.Any(o => o.Name == "Qualquer tamanho"));
        Assert.Contains(snapshot.Settings, x => x.Key == "Set_Path" && x.Label == "Caminho de upgrade");
        Assert.Contains(snapshot.Settings, x => x.Key == "DumpLUTShaders" && x.Label == "Exportar shaders LUT");
        Assert.Contains(snapshot.Settings, x => x.Key == "Upgrade_CopyDestinations" && x.Label == "Atualizar destinos de cópia" && x.ManifestDriven && x.CurrentValue == "2" && x.Options.Any(o => o.Name == "Todas as cópias"));
        Assert.Contains(snapshot.Settings, x => x.Key == "ManifestToggle" && x.ManifestDriven && x.CurrentValue == "1");
        Assert.DoesNotContain(snapshot.Settings, x => x.Key == "Upgrade_UseSCRGB");
        Assert.DoesNotContain(snapshot.Settings, x => x.Key == "KeepMe");
        Assert.Equal("1", File.ReadAllLines(ini).Single(x => x.StartsWith("Set_Path=")).Split('=')[1]);
    }


    [Theory]
    [InlineData("Upgrade_R10G10B10A2_TYPELESS", "RGB 10-bit + Alpha — Genérico")]
    [InlineData("Upgrade_R10G10B10A2_UNORM", "RGB 10-bit + Alpha — Normalizado")]
    [InlineData("Upgrade_R11G11B10_FLOAT", "RGB Float 11/11/10 — Alta faixa dinâmica")]
    [InlineData("Upgrade_R16G16B16A16_TYPELESS", "RGBA 16-bit — Alta precisão")]
    [InlineData("Upgrade_R8G8B8A8_TYPELESS", "RGBA 8-bit — Genérico")]
    [InlineData("Upgrade_R8G8B8A8_UNORM", "RGBA 8-bit — Linear")]
    [InlineData("Upgrade_R8G8B8A8_UNORM_SRGB", "RGBA 8-bit — sRGB")]
    [InlineData("Upgrade_CopyDestinations", "Atualizar destinos de cópia")]
    public async Task Snapshot_UsesFriendlyUiLabelWithoutChangingInternalKey(string key, string expectedLabel)
    {
        var (game, _) = CreateGame($"[renodx]\nSet_Path=0\n{key}=0\n");
        using var http = new HttpClient(new ManifestOnlyHandler(ManifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var snapshot = await service.GetCompatibilitySettingsAsync(game);

        var setting = snapshot.Settings.Single(x => x.Key == key);
        Assert.Equal(key, setting.Key);
        Assert.Equal(expectedLabel, setting.Label);
    }

    [Fact]
    public async Task Snapshot_UsesManifestDefaultWithoutWritingMissingKey()
    {
        var (game, ini) = CreateGame("[renodx]\nUpgrade_R8G8B8A8_TYPELESS=0\n");
        using var http = new HttpClient(new ManifestOnlyHandler(ManifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var snapshot = await service.GetCompatibilitySettingsAsync(game);

        var extra = snapshot.Settings.Single(x => x.Key == "ManifestToggle");
        Assert.Equal("1", extra.CurrentValue);
        Assert.DoesNotContain("ManifestToggle=", File.ReadAllText(ini));
    }

    [Fact]
    public void SetCompatibilitySetting_WritesSelectedValueAndPreservesUnrelatedIniContent()
    {
        var (game, ini) = CreateGame("[GENERAL]\nFoo=Bar\n\n[renodx]\nKeepMe=abc\n\n[OTHER]\nX=Y\n");
        using var http = new HttpClient(new ManifestOnlyHandler(ManifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var result = service.SetCompatibilitySetting(game, "ManifestToggle", "1");

        Assert.True(result.Success, result.Message);
        var text = File.ReadAllText(ini);
        Assert.Contains("ManifestToggle=1", text);
        Assert.Contains("KeepMe=abc", text);
        Assert.Contains("Foo=Bar", text);
        Assert.Contains("[OTHER]", text);
        Assert.Contains("X=Y", text);
    }

    [Fact]
    public async Task Snapshot_ManifestFailureFailsOpenToBuiltInRows()
    {
        var (game, _) = CreateGame("[renodx]\nUpgrade_R8G8B8A8_TYPELESS=2\nSet_Path=0\n");
        using var http = new HttpClient(new AlwaysFailHandler());
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/offline.json");
        var service = MakeService(http, manifest);

        var snapshot = await service.GetCompatibilitySettingsAsync(game);

        Assert.True(snapshot.HasRenoDxSection);
        Assert.Equal(2, snapshot.Settings.Count);
        Assert.Contains(snapshot.Settings, x => x.Key == "Upgrade_R8G8B8A8_TYPELESS");
        Assert.Contains(snapshot.Settings, x => x.Key == "Set_Path");
        Assert.Contains("configurações internas", snapshot.Warning ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_WithoutRenodxSectionExplainsRunOnceRequirement()
    {
        var (game, _) = CreateGame("[GENERAL]\nFoo=Bar\n");
        using var http = new HttpClient(new ManifestOnlyHandler(ManifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var snapshot = await service.GetCompatibilitySettingsAsync(game);

        Assert.False(snapshot.HasRenoDxSection);
        Assert.Empty(snapshot.Settings);
        Assert.Contains("Execute o jogo uma vez", snapshot.Warning ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserChangeThroughCompatibilityUi_IsPreservedByManifestOwnershipOnRemove()
    {
        var (game, ini) = CreateGame("[renodx]\nSet_Path=7\nKeepMe=abc\n");
        var exe = game.ExecutablePath!;
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade64.dll"), "MZ ReShade crosire");
        using var http = new HttpClient(new FullInstallHandler());
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = MakeService(http, manifest);

        var install = await service.InstallAsync(game, [exe, Path.Combine(game.BinaryDirectory!, "ReShade64.dll")]);
        Assert.True(install.Success, install.Message);
        Assert.Contains("Set_Path=1", File.ReadAllText(ini));

        var edit = service.SetCompatibilitySetting(game, "Set_Path", "0");
        Assert.True(edit.Success, edit.Message);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        var after = File.ReadAllText(ini);
        Assert.Contains("Set_Path=0", after);
        Assert.DoesNotContain("Set_Path=7", after);
        Assert.Contains("KeepMe=abc", after);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private (DetectedGame Game, string Ini) CreateGame(string iniText)
    {
        var bin = Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "CompatGame.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, iniText);
        return (new DetectedGame
        {
            Name = "Compat Game",
            InstallPath = bin,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            Runner = "Proton",
            SteamAppId = 777001
        }, ini);
    }

    private RenoDxDeploymentService MakeService(HttpClient http, UpstreamRhiManifestService manifest)
    {
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records-" + Guid.NewGuid().ToString("N") + ".json"));
        return new RenoDxDeploymentService(
            http,
            store,
            new RenoDxCatalogService(http, manifest),
            Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")),
            manifest);
    }

    private const string ManifestJson = """
    {
      "version": 2,
      "renodxExtraSettings": [
        {
          "key": "Upgrade_CopyDestinations",
          "label": "Upgrade Copy Destinations",
          "default": "0",
          "options": [
            { "value": "0", "name": "Off" },
            { "value": "1", "name": "Output size" },
            { "value": "2", "name": "All copies" }
          ]
        },
        { "key": "ManifestToggle", "label": "Manifest Toggle", "default": "1" }
      ]
    }
    """;

    private sealed class ManifestOnlyHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class FullInstallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? "";
            if (url == "https://example.test/manifest.json")
            {
                const string json = """
                {
                  "version": 2,
                  "renodxIniOverrides": {
                    "Compat Game": { "Set_Path": "1" }
                  },
                  "renodxExtraSettings": [
                    { "key": "ManifestToggle", "default": "1" }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><td>Compat Game</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-compat.addon64\">snapshot</a></td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }

            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
