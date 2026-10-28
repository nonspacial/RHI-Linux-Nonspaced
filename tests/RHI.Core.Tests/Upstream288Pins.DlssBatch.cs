using System.Net;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 item 2 (upstream 2.8.8 <c>d61a696</c>): a game whose DLSS swap fails does not stop the DLSS batch. Upstream's
/// Batch Deploy stopped at the first failing game; here one game is one unit and the batch carries on, listing the
/// failed game by name in its items, with that game's file untouched.
/// </summary>
public sealed partial class LinuxDlssDefaultsServiceTests
{
    [Fact]
    public async Task Pin288_Batch_CarriesOnPastAFailingGame_AndListsItByName()
    {
        var failing = Game("pin288-batch-failing");
        var failingSr = Path.Combine(failing.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(failingSr, Bytes(0x11, 70_000));
        var good = Game("pin288-batch-good");
        var goodSr = Path.Combine(good.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(goodSr, Bytes(0x11, 70_000));
        var handler = new Pin288FirstPayloadFails(Manifest(), PayloadZip());
        var dlss = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-pin288"));
        var service = new LinuxDlssDefaultsService(dlss, NewRecords(), new FixedRunningProbe(false));
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [failing.Name] = [failingSr], [good.Name] = [goodSr] };

        var result = await service.ApplyBatchAsync([failing, good], new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), game => indexes[game.Name]);

        Assert.Equal(1, handler.FailedPayloads);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.ChangedCount);
        var failed = Assert.Single(result.Items, item => !item.Success);
        Assert.Equal(failing.Name, failed.GameName);
        Assert.Contains("payload offline", failed.Message, StringComparison.Ordinal);
        Assert.Equal(70_000L, new FileInfo(failingSr).Length); // the failing game keeps its own DLL
        Assert.False(File.Exists(failingSr + ".original"));
        Assert.Equal(80_000L, new FileInfo(goodSr).Length);    // the next game was still swapped
        Assert.True(File.Exists(goodSr + ".original"));
    }

    /// <summary>The DLSS manifest, then payload zips; the first payload request fails (a download error for one game).</summary>
    private sealed class Pin288FirstPayloadFails(byte[] manifest, byte[] zip) : HttpMessageHandler
    {
        public int FailedPayloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
            if (FailedPayloads++ == 0) throw new HttpRequestException("payload offline (test)");
            FailedPayloads = 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }
    }
}
