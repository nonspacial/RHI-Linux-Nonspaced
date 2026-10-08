using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #42: every migrated service downloads under its own source policy. Each case hands the service a link on a host
/// that only a wider policy allows (raw.githubusercontent.com, a github.io site, api-less release metadata) and
/// requires that service's own refusal text, with nothing requested.
/// </summary>
public sealed class ServiceDownloadPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-service-policy-" + Guid.NewGuid().ToString("N"));

    public ServiceDownloadPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    public static TheoryData<string> Services() => ["ShortFuse", "Ual", "Rtx40Mfg", "DlssEnabler", "ShaderPack", "Resolver"];

    [Theory]
    [MemberData(nameof(Services))]
    public async Task Service_RefusesAHostOutsideItsOwnPolicy_BeforeAnyRequest(string service)
    {
        var handler = new Counting();
        var http = new HttpClient(handler);
        var dir = Path.Combine(_root, service);
        var records = new LinuxDeploymentRecordStore(Path.Combine(dir, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var cache = Path.Combine(dir, "cache");
        const string raw = "https://raw.githubusercontent.com/owner/repo/main/payload.bin";

        (TrustedDownloadPolicy Expected, Func<Task> Act) target = service switch
        {
            "ShortFuse" => (DownloadSources.RhiRepository, () => new ShortFuseDeploymentService(http, new TransactionalFileDeploymentService(), records, overrides,
                    new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(dir, "ual")),
                    new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(dir, "dlss")), cache,
                    new ShortFuseSettingsStore(Path.Combine(dir, "sf.json")))
                .EnsureCachedAddonAsync(new ShortFuseReleaseInfo("2.0", "renodx-dlss-SF-2.0", raw, null), CancellationToken.None)),
            "Ual" => (DownloadSources.UltimateAsiLoader, () => new UltimateAsiLoaderDeploymentService(http, records, overrides, cache)
                .EnsureCachedDllAsync(new UalReleaseInfo("9", "Ultimate-ASI-Loader_x64.zip", raw, null), PeArchitecture.X64, CancellationToken.None)),
            "Rtx40Mfg" => (DownloadSources.UniversalMfg, () => new Rtx40MfgStandaloneDeploymentService(http, records, overrides,
                    new Rtx40MfgPreferenceStore(Path.Combine(dir, "mfg.json")), cache)
                .EnsureCachedPayloadAsync(new Rtx40MfgReleaseInfo("1.0", "v1.0", "RTXMFG-1.0.zip", raw, new string('a', 64), null), CancellationToken.None)),
            "DlssEnabler" => (DownloadSources.RhiRepository, () => new StandaloneDlssEnablerDeploymentService(http, records, overrides,
                    new StandaloneDlssEnablerPreferenceStore(Path.Combine(dir, "enabler.json")), cache)
                .EnsureCachedPayloadAsync(new DlssEnablerReleaseInfo("1.0", "v1.0", "version.dll", raw, null), CancellationToken.None)),
            "ShaderPack" => (DownloadSources.ShaderPacks, () => new ReShadeShaderPackService(http, cache, null, Path.Combine(dir, "custom"))
                .EnsurePackAsync(new ReShadeShaderPackDefinition("Pages", "Pages", "test", ReShadeShaderPackSourceKind.DirectUrl,
                    "https://someone.github.io/shaders.zip"), CancellationToken.None)),
            "Resolver" => (DownloadSources.GitHubApi, () => new GitHubReleaseAssetResolver(http)
                .ResolveAsync(new GitHubReleaseAssetQuery("https://github.com/owner/repo/releases", ExactAssetName: "a.zip"))),
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        };

        var error = await Assert.ThrowsAsync<UntrustedDownloadException>(target.Act);

        Assert.StartsWith(target.Expected.RefusalPrefix, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    private sealed class Counting : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64 * 1024]) });
        }
    }
}
