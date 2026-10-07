using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #122: manifest renodxIniOverrides/renodxExtraSettings meet the same key/value grammar as the Unity database keys.
public sealed class RenoDxIniGrammarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-grammar-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxIniGrammarTests() => Directory.CreateDirectory(_root);

    // Upstream 2.8.5 values ("Jump Space": {"Upgrade_R8G8B8A8_TYPELESS": "3"}) pass; anything that could start a line or section does not.
    [Theory]
    [InlineData("Upgrade_R8G8B8A8_TYPELESS", "3", true)]
    [InlineData("Set_Path", "1", true)]
    [InlineData("Bare_Key", "", true)]
    [InlineData("Set_Path", "1\n[Evil]", false)]
    [InlineData("Set_Path", "1\r\nx=1", false)]
    [InlineData("Set_Path", "[Evil]", false)]
    [InlineData("Set_Path", "1\u2028x", false)]
    [InlineData("Bad\nKey", "1", false)]
    [InlineData("Bad=Key", "1", false)]
    [InlineData("[renodx]", "1", false)]
    [InlineData("Spaced Key", "1", false)]
    [InlineData("", "1", false)]
    public void Grammar(string key, string value, bool safe)
        => Assert.Equal(safe, RenoDxIniGrammar.IsSafePair(key, value));

    [Fact]
    public async Task ManifestOverridesWithInjectedLines_AreDroppedAndNeverWritten()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Inject.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\my.ini\n");
        var game = new DetectedGame { Name = "Inject Game", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton" };
        using var http = new HttpClient(new Handler("""
            {
              "version": 2,
              "snapshotOverrides": { "Inject Game": "https://clshortfuse.github.io/renodx/renodx-inject.addon64" },
              "renodxIniOverrides": {
                "Inject Game": {
                  "Set_Path": "1\n[Evil]\nPresetPath=C:\\evil.ini",
                  "Bad\n[Section]\nKey": "1",
                  "Upgrade_R8G8B8A8_TYPELESS": "3"
                }
              }
            }
            """));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")),
            new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest);

        var parsed = await manifest.ResolveGameOverrideAsync(game);
        var install = await service.InstallAsync(game, [exe, Path.Combine(bin, "ReShade64.dll")]);

        Assert.Equal(["Upgrade_R8G8B8A8_TYPELESS"], parsed!.RenoDxIniOverrides.Keys);
        Assert.True(install.Success, install.Message);
        var written = File.ReadAllText(ini);
        Assert.Equal("[GENERAL]\nPresetPath=.\\my.ini\n\n[renodx]\nUpgrade_R8G8B8A8_TYPELESS=3\n", written);
    }

    [Fact]
    public void OverrideManager_DropsUnsafePairsFromAnySource()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var before = File.ReadAllBytes(ini);

        var result = RenoDxIniOverrideManager.Apply(_root, new Dictionary<string, string>
        {
            ["Set_Path"] = "1\n[Evil]",
            ["x]\n[y"] = "1"
        });

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(before, File.ReadAllBytes(ini));
    }

    [Fact]
    public async Task ExtraSettings_UnsafeKeysAndOptionValuesAreDropped()
    {
        using var http = new HttpClient(new Handler("""
            {
              "version": 2,
              "renodxExtraSettings": [
                { "key": "Good_Setting", "label": "Good", "defaultValue": "0\n[x]", "options": [ { "value": "0", "name": "Off" }, { "value": "1\n[Evil]", "name": "Bad" } ] },
                { "key": "Bad\n[Section]", "label": "Bad", "options": [ { "value": "1", "name": "On" } ] }
              ]
            }
            """));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");

        var settings = await manifest.FetchRenoDxExtraSettingsAsync();

        var setting = Assert.Single(settings);
        Assert.Equal("Good_Setting", setting.Key);
        Assert.Equal("0", setting.DefaultValue);
        Assert.Equal(["0"], setting.Options.Select(o => o.Value));
    }

    [Fact]
    public void SetCompatibilitySetting_UnsafeValue_IsRefusedAndNothingWritten()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var before = File.ReadAllBytes(ini);
        using var http = new HttpClient(new Handler("{\"version\":2}"));
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "set-records.json")),
            new RenoDxCatalogService(http), Path.Combine(_root, "set-cache"));

        var result = service.SetCompatibilitySetting(new DetectedGame { Name = "Set Game", BinaryDirectory = _root }, "Set_Path", "1\n[Evil]");

        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(ini));
        Assert.StartsWith("Configuração do RenoDX recusada:", UiLocalizationService.PresentFor("pt-BR", result.Message), StringComparison.Ordinal);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Handler(string manifestJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://manifest.test/manifest.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestJson, Encoding.UTF8, "application/json") });
            if (!url.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
