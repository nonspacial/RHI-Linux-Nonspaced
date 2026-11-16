using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/nr-2 (#317, #276): the DLSS5 Tool method through the app's real services — its Managed components row (told apart
/// from DLSS5 Feeder's consumer, a DX11 Bridge left without it), and the per-game Foveated switch (saved for the install,
/// or a swap in place that goes back to what is installed when it fails).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class Dlss5ToolMethodTests
{
    private const string StockZipUrl = "https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss5-10.8.0/renodx-dlss5_10.8.0.zip";
    private const string FoveatedUrl = "https://github.com/maximedgoulet/FoveatedDLSS5/releases/download/v0.4.0/renodx-dlss5.addon64";

    private static DetectedGame Game(string root, string name, bool withDlss = true)
    {
        var game = MfgUpdateAllTests.StreamlineGame(root, name);
        if (withDlss) File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 3));
        return game;
    }

    [Fact]
    public async Task Row_ADlss5FeederConsumer_IsTheFeedersFile_NotAnExternalDlss5Tool()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "FeederGame");
        var consumer = Path.Combine(game.BinaryDirectory!, "renodx-dlss5.addon64");
        File.WriteAllBytes(consumer, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 5));
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = game.BinaryDirectory!, Version = "2.0.0", Files = [new LinuxDeployedFile { Path = consumer }]
        });

        var row = MfgUpdateAllTests.Row(fx, game, ManagedComponentKind.Dlss5Tool);

        Assert.Equal(ComponentAvailability.Available, row.Availability);
        Assert.StartsWith(Dlss5ToolDeploymentService.FeederOwnsNote, row.Note);
        Assert.Contains(Dlss5ToolDeploymentService.SwitchMethodNote.Trim(), row.Note);
        Assert.True(row.ActionAllowed); // installing DLSS5 Tool switches the method, as upstream
    }

    [Fact]
    public async Task Row_AnRenodxDlss5AddonWithoutAnyRecord_IsProtectedAsExternal()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "ManualGame");
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "renodx-dlss5.addon64"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 5));

        var row = MfgUpdateAllTests.Row(fx, game, ManagedComponentKind.Dlss5Tool);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.False(row.ActionAllowed);
    }

    [Fact]
    public async Task Row_WithoutNativeDlss_SaysWhyItCannotGoThere()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "NoDlssGame", withDlss: false);

        var row = MfgUpdateAllTests.Row(fx, game, ManagedComponentKind.Dlss5Tool);

        Assert.Equal(ComponentAvailability.Available, row.Availability);
        Assert.False(row.ActionAllowed);
        Assert.Equal(Dlss5ToolDeploymentService.NeedsDlssMessage, row.Note);
    }

    [Fact]
    public async Task Row_ADx11BridgeInstalledBeforeTheDlss5ToolMethods_SaysItNeedsDlss5Tool()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "BridgeGame");
        game.GraphicsApi = GraphicsApi.DirectX11;
        var bridge = Path.Combine(game.BinaryDirectory!, "dlss5-bridge.addon64");
        File.WriteAllBytes(bridge, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 6));
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.Dlss5Dx11Bridge,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0", Files = [new LinuxDeployedFile { Path = bridge }]
        });

        var row = MfgUpdateAllTests.Row(fx, game, ManagedComponentKind.Dlss5Dx11Bridge);

        Assert.Contains(LinuxManagedComponentService.BridgeWithoutToolNote, row.Note);
    }

    [Fact]
    public async Task FoveatedSwitch_NotInstalled_IsSavedForTheInstall_AndTheVersionStartsFromLatest()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "SwitchGame");
        fx.Managed.SetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Tool, "8.5.0-rc10");

        var on = await fx.Managed.SetDlss5ToolFoveatedAsync(game, true);

        Assert.True(on.Success, on.Message);
        Assert.Equal(LinuxManagedComponentService.Dlss5ToolFoveatedSavedMessage, on.Message);
        Assert.Equal(Dlss5ToolBuild.Foveated, fx.Managed.GetDlss5ToolBuild(game));
        Assert.Null(fx.Managed.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Tool)); // stock and Foveated have their own releases

        Assert.True((await fx.Managed.SetDlss5ToolFoveatedAsync(game, false)).Success);
        Assert.Equal(Dlss5ToolBuild.Stock, fx.Managed.GetDlss5ToolBuild(game));
    }

    [Fact]
    public async Task FoveatedSwitch_Installed_SwapsInPlace_AndAFailedSwapPutsTheSwitchAndPinBack()
    {
        var foveatedHash = true;
        var stockAddon = CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 7);
        var foveatedAddon = CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 8);
        var stockZip = Zip(("renodx-dlss5.addon64", stockAddon));
        HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            byte[]? body = request.RequestUri!.AbsoluteUri switch
            {
                Dlss5ToolDeploymentService.StockReleasesApiUrl => JsonSerializer.SerializeToUtf8Bytes(new[]
                {
                    new { tag_name = "renodx-dlss5-10.8.0", draft = false, prerelease = false, assets = new[]
                        { new { name = "renodx-dlss5_10.8.0.zip", browser_download_url = StockZipUrl, digest = "sha256:" + Hex(stockZip) } } }
                }),
                StockZipUrl => stockZip,
                Dlss5ToolDeploymentService.FoveatedReleasesApiUrl => JsonSerializer.SerializeToUtf8Bytes(new[]
                {
                    new { tag_name = "v0.4.0", draft = false, prerelease = true, assets = new[]
                    {
                        new { name = "renodx-dlss5.addon64", browser_download_url = FoveatedUrl },
                        new { name = "renodx-dlss5.addon64.sha256", browser_download_url = FoveatedUrl + ".sha256" }
                    } }
                }),
                FoveatedUrl => foveatedAddon,
                FoveatedUrl + ".sha256" when foveatedHash => Encoding.ASCII.GetBytes(Hex(foveatedAddon) + "  renodx-dlss5.addon64\r\n"),
                _ => null
            };
            return body is null ? null : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer);
        var game = Game(fx.Root, "InstalledGame");
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), ReShadeLike());
        Assert.True((await fx.Managed.Dlss5Tool!.InstallAsync(game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), true)).Success);
        var addon = Path.Combine(game.BinaryDirectory!, "renodx-dlss5.addon64");

        var on = await fx.Managed.SetDlss5ToolFoveatedAsync(game, true);

        Assert.True(on.Success, on.Message);
        Assert.Equal(foveatedAddon, File.ReadAllBytes(addon));
        Assert.Equal(Dlss5ToolBuild.Foveated, fx.Managed.GetDlss5ToolBuild(game));

        // Back to stock works; then Foveated again with its .sha256 gone: refused, nothing changes, and the switch and
        // the pin go back to what is installed.
        Assert.True((await fx.Managed.SetDlss5ToolFoveatedAsync(game, false)).Success);
        Assert.Equal(stockAddon, File.ReadAllBytes(addon));
        fx.Managed.SetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Tool, "10.8.0");
        foveatedHash = false;

        var failed = await fx.Managed.SetDlss5ToolFoveatedAsync(game, true);

        Assert.False(failed.Success);
        Assert.Equal(stockAddon, File.ReadAllBytes(addon));
        Assert.Equal(Dlss5ToolBuild.Stock, fx.Managed.GetDlss5ToolBuild(game));
        Assert.Equal("10.8.0", fx.Managed.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Tool));
        Assert.True((await fx.Managed.RemoveAsync(game, ManagedComponentKind.Dlss5Tool)).Success);
        Assert.Equal(Dlss5ToolBuild.Stock, fx.Managed.GetDlss5ToolBuild(game)); // the saved switch was put back too
    }

    private static byte[] ReShadeLike()
    {
        var bytes = CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 9);
        Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(bytes, 4096);
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                output.Write(bytes);
            }
        return stream.ToArray();
    }
}

/// <summary>w14/nr-2 (#317/#276): the DLSS5 Tool's controls sit with the other Neural Rendering versions (upstream's NR section).</summary>
public sealed class Dlss5ToolLayoutTests
{
    [Fact]
    public void MainWindow_PutsDlss5ToolVersionWithTheOtherNrVersions_AndTheFoveatedSwitchUnderThem()
    {
        var path = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml");
        var named = System.Xml.Linq.XDocument.Load(path).Descendants()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Name"))
            .ToDictionary(e => (string)e.Attributes().First(a => a.Name.LocalName == "Name"));

        var version = named["Dlss5ToolVersion"];
        Assert.Equal("{Binding SelectedDlss5ToolVersion, Mode=TwoWay}", (string?)version.Attribute("SelectedItem"));
        Assert.Equal("{Binding Dlss5ToolVersionOptions}", (string?)version.Attribute("ItemsSource"));
        // Its label follows the switch ("DLSS5 Tool Version (Foveated)"), as upstream's.
        Assert.Contains(version.Parent!.Elements(), e => (string?)e.Attribute("Text") == "{Binding Dlss5ToolVersionLabel}");
        var wrap = version.Parent!.Parent!;
        Assert.Contains(wrap.Elements(), e => e.Descendants().Any(d => (string?)d.Attribute("SelectedItem") == "{Binding SelectedGitcUpliftVersion, Mode=TwoWay}"));
        Assert.Equal("{Binding Dlss5ToolFoveated, Mode=TwoWay}", (string?)named["Dlss5ToolFoveated"].Attribute("IsChecked"));
        Assert.Equal("{Binding OpenFoveatedDlss5LinkCommand}", (string?)named["FoveatedDlss5Link"].Attribute("Command"));
    }
}
