using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class LumaPolicyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-policy-" + Guid.NewGuid().ToString("N"));

    public LumaPolicyServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task ExplicitDisabled_SurvivesNewServiceInstance()
    {
        var manifest = """{ "version":2, "lumaDefaultGames":["Default Game"] }""";
        var storePath = Path.Combine(_root, "prefs-persist.json");
        var game = Game("Default Game");
        var first = BuildService(manifest, storePath);
        await first.PrimeAsync(game);
        first.SetPreference(game, LumaModePreference.Disabled);

        var second = BuildService(manifest, storePath);
        var policy = await second.PrimeAsync(game);

        Assert.False(policy.EffectiveModeEnabled);
        Assert.Equal(LumaModePreference.Disabled, policy.UserPreference);
        Assert.Equal("user disabled", policy.Authority);
    }

    [Fact]
    public async Task ExplicitEnabled_WinsWhenManifestHasNoDefault()
    {
        var service = BuildService("""{ "version":2 }""");
        var game = Game("Manual Game");
        await service.PrimeAsync(game);

        var policy = service.SetPreference(game, LumaModePreference.Enabled);

        Assert.True(policy.EffectiveModeEnabled);
        Assert.Equal("user enabled", policy.Authority);
    }

    [Fact]
    public async Task RenoDxCompat_IsExposedIndependentlyFromModeDefault()
    {
        var service = BuildService("""
        { "version":2, "lumaRenodxCompat":["Compat Game"] }
        """);
        var policy = await service.PrimeAsync(Game("Compat Game"));

        Assert.True(policy.RenoDxCompatible);
        Assert.False(policy.EffectiveModeEnabled);
        Assert.Contains("coexistence allowed", policy.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestMatching_IsCaseAndPunctuationTolerant()
    {
        var service = BuildService("""
        { "version":2, "lumaRenodxCompat":["Street Fighter™ 6"] }
        """);
        var policy = await service.PrimeAsync(Game("street fighter 6"));

        Assert.True(policy.RenoDxCompatible);
    }

    [Fact]
    public async Task LinuxLauncherShortTitle_CanResolveUniqueManifestIdentityFromPath()
    {
        var service = BuildService("""
        { "version":2, "lumaRenodxCompat":["Clair Obscur: Expedition 33"] }
        """);
        var game = Game("Expedition 33", Path.Combine(_root, "Clair Obscur. Expedition 33 (2025)", "Expedition 33"));

        var policy = await service.PrimeAsync(game);

        Assert.True(policy.RenoDxCompatible);
    }

    [Fact]
    public async Task AmbiguousPathEvidence_DoesNotAdoptTwoManifestTitles()
    {
        var service = BuildService("""
        { "version":2, "lumaRenodxCompat":["Alpha Game","Beta Game"] }
        """);
        var game = Game("Launcher", Path.Combine(_root, "Alpha Game", "Beta Game"));

        var policy = await service.PrimeAsync(game);

        Assert.False(policy.RenoDxCompatible);
    }

    [Fact]
    public async Task ManifestFailure_FailsClosedButKeepsPersistedUserAuthority()
    {
        var game = Game("Manual Game");
        var storePath = Path.Combine(_root, "prefs-fail.json");
        var seed = new LumaModePreferenceStore(storePath);
        seed.Set(game, LumaModePreference.Enabled);
        using var http = new HttpClient(new ThrowHandler());
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var service = new LumaPolicyService(manifest, new LumaModePreferenceStore(storePath));

        var policy = await service.PrimeAsync(game);

        Assert.True(policy.EffectiveModeEnabled);
        Assert.Equal(LumaModePreference.Enabled, policy.UserPreference);
        Assert.False(policy.RenoDxCompatible);
    }

    private LumaPolicyService BuildService(string json, string? storePath = null)
    {
        var http = new HttpClient(new StaticHandler(json));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        return new LumaPolicyService(manifest, new LumaModePreferenceStore(storePath ?? Path.Combine(_root, Guid.NewGuid() + ".json")));
    }

    private DetectedGame Game(string name, string? install = null)
    {
        var path = install ?? Path.Combine(_root, name.Replace('/', '_'));
        Directory.CreateDirectory(path);
        return new DetectedGame { Name = name, InstallPath = path, Source = "Test", Runner = "Wine" };
    }

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
