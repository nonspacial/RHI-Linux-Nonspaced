using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #269 (upstream 2.8.9 OsPresetService.ManifestPresets): the recommended presets from upstream's optiscaler_presets.json
/// (as published with RHI 2.9.4), mapped to the Linux ⚙ setting ids, cached, with the built-in copies as the fallback.
/// </summary>
public sealed class OptiScalerRecommendedPresetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-recommended-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string Upstream() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "optiscaler_presets-rhi-2.9.4.json"));

    [Fact]
    public void Parse_UpstreamsFile_MapsEveryPresetToTheLinuxSettings_InApplyOrder()
    {
        var presets = OptiScalerRecommendedPresets.Parse(Upstream())!;

        Assert.Equal(["RealDlssFg", "DlssEnablerFg", "UmfgXefg", "AnrFg"], presets.Select(preset => preset.Id));
        var real = presets[0];
        Assert.Equal("Real DLSS FG", real.Name);
        Assert.StartsWith("NVIDIA's own DLSS frame generation", real.Description, StringComparison.Ordinal);
        Assert.Equal(
        [
            new(OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true"), new("StreamlineVersion", "latest"),
            new("FrameGenEnabled", "true"), new("FGInput", "upscaler"), new("FGOutput", "dlssg"), new("FGNvngxReplacement", "None"),
            new("HUDFix", "true"), new(OptiScalerDeploymentService.ForceReflexSettingId, "2"), new(OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, "false")
        ], real.Settings);
        Assert.True(presets[2].InstallUniversalMfg);
        Assert.False(presets[1].InstallUniversalMfg);
        Assert.True(presets[3].InstallAnrFg);
        Assert.Equal("Apex Neural Rendering FG", presets[3].Settings.Single(pair => pair.Key == "FGNvngxReplacement").Value);
    }

    [Fact]
    public void BuiltIn_IsUpstreamsFile()
        => Assert.Equal(OptiScalerRecommendedPresets.Parse(Upstream())!.Select(Describe), OptiScalerRecommendedPresets.BuiltIn.Select(Describe));

    private static string Describe(OptiScalerRecommendedPreset preset)
        => $"{preset.Id}|{preset.Name}|{preset.Description}|{string.Join(";", preset.Settings)}|{preset.InstallUniversalMfg}|{preset.InstallAnrFg}";

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"presets\":[]}")]
    [InlineData("{\"presets\":[{\"name\":\"\"},{\"id\":\"x\",\"name\":\"Nothing\"}]}")]
    public void Parse_NothingUsable_IsNull(string json) => Assert.Null(OptiScalerRecommendedPresets.Parse(json));

    [Fact]
    public async Task Refresh_ReadsTheFile_CachesIt_AndKeepsTheListWhenItCannotBeRead()
    {
        var body = Upstream().Replace("\"Real DLSS FG\"", "\"Real DLSS FG (new)\"", StringComparison.Ordinal);
        var serve = true;
        using var http = new HttpClient(new Responder(request => serve && request.RequestUri!.AbsoluteUri == OptiScalerRecommendedPresets.ManifestUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var cache = Path.Combine(_root, "presets.json");
        var presets = new OptiScalerRecommendedPresets(http, cache);
        Assert.Equal("Real DLSS FG", presets.Presets[0].Name); // built-in until read once

        await presets.RefreshAsync();
        Assert.Equal("Real DLSS FG (new)", presets.Presets[0].Name);
        Assert.Equal(body, File.ReadAllText(cache));

        serve = false;
        await presets.RefreshAsync();
        Assert.Equal("Real DLSS FG (new)", presets.Presets[0].Name);
        Assert.Equal("Real DLSS FG (new)", new OptiScalerRecommendedPresets(null, cache).Presets[0].Name); // the cached copy
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
