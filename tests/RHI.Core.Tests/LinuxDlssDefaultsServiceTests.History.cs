using System.IO.Compression;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #289 (upstream 2.9.3 "Fixed Update History missing updates for … DLSS and Streamline files"): DLSS / Streamline swaps
/// and restores made outside Follow newest (the DLSS batch, the defaults and the DLSS panel) are in Update History, through
/// the one generic hook <see cref="ComponentHistory"/>. The managed components already were (the ledger diff).
/// </summary>
public sealed partial class LinuxDlssDefaultsServiceTests
{
    private static byte[] VersionedPayloadZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            Add(zip, "nvngx_dlss.dll", DlssAutoFollowServiceTests.Pe("9.9.9.0"));
        return output.ToArray();
    }

    [Fact]
    public async Task BatchSwap_AndRestore_AreInUpdateHistory_UnderTheDllsName()
    {
        var game = Game("history-sr");
        var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(sr, DlssAutoFollowServiceTests.Pe("310.4.0.0"));
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "update-log.json"));
        var service = NewService(new GateHandler(Manifest(), VersionedPayloadZip()));
        service.History = new ComponentHistory(log);

        await service.ApplyBatchAsync([game], new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), _ => [sr]);
        await service.ApplyBatchAsync([game], new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), _ => [sr]); // same version: nothing

        var swap = Assert.Single(log.GetAll());
        Assert.Equal(("DLSS SR (nvngx_dlss.dll)", ManagedComponentKind.Dlss, ComponentUpdateAction.Update, "310.4.0.0", "9.9.9", true),
            (swap.ComponentName, swap.Component, swap.Action, swap.FromVersion, swap.ToVersion, swap.Success));
        Assert.Equal(game.Name, swap.GameName);

        await service.RestoreBatchAsync([game], _ => [sr]);

        var restore = log.GetAll()[0];
        Assert.Equal(("DLSS SR (nvngx_dlss.dll)", "9.9.9.0", "310.4.0.0"), (restore.ComponentName, restore.FromVersion, restore.ToVersion));
    }

    [Theory]
    [InlineData("1.0", "1.1", true)]
    [InlineData("1.0", " 1.0 ", false)]
    [InlineData(null, "1.1", false)] // upstream: a first download is no update
    [InlineData("1.0", "unknown", false)]
    [InlineData("v1.0", "V1.0", false)]
    public void ShouldRecordUpdate_IsUpstreamsRule(string? from, string? to, bool recorded)
        => Assert.Equal(recorded, ComponentHistory.ShouldRecordUpdate(from, to));

    [Fact]
    public void Record_KeyedByComponent_NamesFeederAndBridgeUnderTheirOwnNames_AndAlwaysKeepsAFailure()
    {
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "update-log-2.json"));
        var history = new ComponentHistory(log);
        var game = Game("history-names");

        Assert.False(history.Record(game, ManagedComponentKind.Dlss5Feeder, ComponentUpdateAction.Update, "1.0", "1.0"));
        Assert.True(history.Record(game, ManagedComponentKind.Dlss5Feeder, ComponentUpdateAction.Update, "1.0", "2.0"));
        Assert.True(history.Record(game, ManagedComponentKind.Dlss5Dx11Bridge, ComponentUpdateAction.Update, "1.0", "1.0", success: false, message: "offline"));

        Assert.Equal(["DLSS5 DX11 Bridge", "DLSS5 Feeder"], log.GetAll().Select(entry => entry.ComponentName));
        Assert.False(log.GetAll()[0].Success);
    }
}
