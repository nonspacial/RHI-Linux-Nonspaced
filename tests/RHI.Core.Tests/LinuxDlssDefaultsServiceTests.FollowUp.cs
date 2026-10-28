using System.Net;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12 follow-up (re-review pins N1): the same game owned on two stores is two library games with one name. When both fail
/// in the DLSS batch, the result line counts and names two games (grouped by library key, never by name).
/// </summary>
public sealed partial class LinuxDlssDefaultsServiceTests
{
    [Fact]
    public async Task Batch_TwoGamesOfOneName_BothFailing_AreTwoEntriesOnTheResultLine()
    {
        var first = Game("followup-twin-steam");
        var second = Game("followup-twin-lutris");
        first.Name = second.Name = "Twin Quest";
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in new[] { first, second })
        {
            var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
            await File.WriteAllBytesAsync(sr, Bytes(0x11, 70_000));
            paths[game.InstallPath] = [sr];
        }
        var dlss = new DlssManagerService(new HttpClient(new EveryPayloadFails(Manifest())), new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-twin"));
        var service = new LinuxDlssDefaultsService(dlss, NewRecords(), new FixedRunningProbe(false));

        var result = await service.ApplyBatchAsync([first, second], new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), game => paths[game.InstallPath]);

        Assert.Equal(2, result.FailedCount);
        Assert.Equal(new[] { LinuxDeploymentRecordStore.GameKey(first), LinuxDeploymentRecordStore.GameKey(second) },
            result.FailedItems.Select(item => item.GameKey ?? "").ToArray());
        // Language-neutral (this class is not in the UI-language collection): two entries, the count says 2.
        var line = DlssPanelViewModel.FailedGamesLine(result);
        Assert.Equal(2, line.Split("Twin Quest: ").Length - 1);
        Assert.Contains("2", line.Split("Twin Quest: ")[0], StringComparison.Ordinal);
    }

    // The single-game flows (Apply defaults, Restore all) carry the key too, so every result line groups the same way.
    [Fact]
    public async Task ApplyDefaultsAndRestoreAll_ItemsCarryTheGamesLibraryKey()
    {
        var game = Game("followup-key-single");
        var service = new LinuxDlssDefaultsService(new DlssManagerService(new HttpClient(new EveryPayloadFails(Manifest())),
            new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-key")), NewRecords(), new FixedRunningProbe(false));

        var apply = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), []);
        var restore = await service.RestoreAllAsync(game, []);

        Assert.All(apply.Items.Concat(restore.Items), item => Assert.Equal(LinuxDeploymentRecordStore.GameKey(game), item.GameKey));
        Assert.NotEmpty(apply.Items);
        Assert.NotEmpty(restore.Items);
    }

    /// <summary>The DLSS manifest, then every payload request fails.</summary>
    private sealed class EveryPayloadFails(byte[] manifest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
            throw new HttpRequestException("payload offline (test)");
        }
    }
}
