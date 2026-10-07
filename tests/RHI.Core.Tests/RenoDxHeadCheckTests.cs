using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #108: the cache HEAD check keeps the cached add-on only when the network fails or times out.
public sealed class RenoDxHeadCheckTests : IDisposable
{
    private const string AddonUrl = "https://clshortfuse.github.io/renodx/renodx-headcheck.addon64";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-head-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HeadHandler _handler = new();
    private readonly RenoDxDeploymentService _service;
    private readonly DetectedGame _game;
    private readonly string[] _files;

    public RenoDxHeadCheckTests()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "HeadCheck.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var reshade = Path.Combine(bin, "ReShade64.dll");
        File.WriteAllText(reshade, "MZ ReShade crosire");
        _game = new DetectedGame { Name = "Head Check", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton" };
        _files = [exe, reshade];
        var http = new HttpClient(_handler);
        _service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")),
            new RenoDxCatalogService(http), Path.Combine(_root, "cache"));
    }

    [Fact]
    public async Task NetworkFailureOnHead_ReusesTheCachedAddon()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        _handler.HeadFault = new HttpRequestException("connection refused");

        var reinstall = await _service.InstallAsync(_game, _files);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.Equal(1, _handler.Gets);
    }

    [Fact]
    public async Task ProgrammingFaultOnHead_IsNotSwallowed()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        _handler.HeadFault = new InvalidOperationException("handler bug");

        var reinstall = await _service.InstallAsync(_game, _files);

        Assert.False(reinstall.Success);
        Assert.EndsWith("handler bug", reinstall.Message, StringComparison.Ordinal);
        Assert.Equal(1, _handler.Gets);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class HeadHandler : HttpMessageHandler
    {
        public Exception? HeadFault { get; set; }
        public int Gets { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == RenoDxCatalogService.WikiUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<table><tr><td>Head Check</td><td><a href=\"{AddonUrl}\">snapshot</a></td></tr></table>")
                });
            if (url != AddonUrl) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head && HeadFault is not null) throw HeadFault;
            if (request.Method == HttpMethod.Get) Gets++;
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
            return Task.FromResult(response);
        }
    }
}
