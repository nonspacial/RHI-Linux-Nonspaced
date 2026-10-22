using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>#229: a ShortFuse install with no NR pin places "Latest" made for the card, never a DLL for another generation.</summary>
public sealed partial class ShortFuseDeploymentServiceTests
{
    private const string NrFor50Url = "https://github.com/example/rhi/releases/download/1/nr-50xx.zip";

    /// <summary>The full fixture's manifest with a newer 50xx-only NR entry listed first (the live manifest's shape).</summary>
    private sealed class Nr50xxFirst(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == NrFor50Url)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateZip(("nvngx_dlssnr.dll", CreatePe(0x8664, 80 * 1024, 0x7A)))) };
            var response = await base.SendAsync(request, cancellationToken);
            if (url != DlssManagerService.ManifestUrl) return response;
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            json = json.Replace("\"dlssnr\":[{\"version\":\"9.0\"", $"\"dlssnr\":[{{\"version\":\"9.1 (50xx)\",\"url\":\"{NrFor50Url}\"}},{{\"version\":\"9.0 (20/30/40/50)\"", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
        }
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx40, 0x74)]
    [InlineData(RtxGeneration.Rtx50, 0x7A)]
    [InlineData(RtxGeneration.Unknown, 0x7A)]
    public async Task NrGpu_InstallWithoutPin_PlacesLatestForThisCard(RtxGeneration card, int marker)
    {
        using var fixture = CreateGame("nrgpu-" + card);
        var ctx = CreateContext(fixture, fullNetwork: true, wrapHandler: inner => new Nr50xxFirst(inner));
        ctx.ShortFuse.Dlss.CardGeneration = () => card;
        AddManagedReShade(ctx.Records, fixture);

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: false);

        Assert.True(install.Success, install.Message);
        Assert.Equal(CreatePe(0x8664, 80 * 1024, (byte)marker), File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssnr.dll")));
    }
}
