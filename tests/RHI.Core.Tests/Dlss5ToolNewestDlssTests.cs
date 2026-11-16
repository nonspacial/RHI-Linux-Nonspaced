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
/// #317 (upstream Renodx5AddonService / DetailPanelBuilder.NeuralRendering.Install <c>UpgradeDlssDllsAsync</c>): a DLSS5 Tool
/// install also brings the game's DLSS SR/RR/FG files to the newest — on Linux through the DLSS / Streamline section's own
/// swap (Quick Apply), so DLSS5 Tool never owns them: .original backups, Update History entries as the section's swaps,
/// only files the game has, and a game whose update inclusion leaves DLSS out keeps its files.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class Dlss5ToolNewestDlssTests
{
    private const string StockZipUrl = "https://github.com/RankFTW/rhi-repo/releases/download/renodx-dlss5-10.8.0/renodx-dlss5_10.8.0.zip";
    private const string DlssBase = "https://github.com/RankFTW/rhi-repo/releases/download/test/";

    // Newest first, as the manifest lists them: "Latest" is 310.5.0 for SR/RR/FG.
    private static readonly byte[] DlssManifest = Encoding.UTF8.GetBytes($$"""
        {
          "dlss":   [{ "version": "310.5.0", "url": "{{DlssBase}}dlss-new.zip" },  { "version": "310.1.0", "url": "{{DlssBase}}dlss-old.zip" }],
          "dlssd":  [{ "version": "310.5.0", "url": "{{DlssBase}}dlssd-new.zip" }, { "version": "310.1.0", "url": "{{DlssBase}}dlssd-old.zip" }],
          "dlssg":  [{ "version": "310.5.0", "url": "{{DlssBase}}dlssg-new.zip" }, { "version": "310.1.0", "url": "{{DlssBase}}dlssg-old.zip" }],
          "dlssnr": [{ "version": "9.9.9", "url": "{{DlssBase}}dlssnr.zip" }],
          "streamline": [{ "version": "2.13.0", "url": "{{DlssBase}}streamline.zip" }]
        }
        """);

    // Versioned DLLs, so Update History knows the versions (it records only a known from → to, as for the section's swaps).
    private static readonly byte[] NewSr = DlssAutoFollowServiceTests.Pe("310.5.0"), NewFg = DlssAutoFollowServiceTests.Pe("310.5.0");
    private static readonly byte[] StockAddon = CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 7);

    private static HttpResponseMessage? Answer(HttpRequestMessage request)
    {
        var stockZip = Zip(("renodx-dlss5.addon64", StockAddon));
        byte[]? body = request.RequestUri!.AbsoluteUri switch
        {
            Dlss5ToolDeploymentService.StockReleasesApiUrl => JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new { tag_name = "renodx-dlss5-10.8.0", draft = false, prerelease = false, assets = new[]
                    { new { name = "renodx-dlss5_10.8.0.zip", browser_download_url = StockZipUrl, digest = "sha256:" + Hex(stockZip) } } }
            }),
            StockZipUrl => stockZip,
            DlssManagerService.ManifestUrl => DlssManifest,
            DlssBase + "dlss-new.zip" => Zip(("nvngx_dlss.dll", NewSr)),
            DlssBase + "dlssd-new.zip" => Zip(("nvngx_dlssd.dll", Bytes(0x82, 80_500))),
            DlssBase + "dlssg-new.zip" => Zip(("nvngx_dlssg.dll", NewFg)),
            DlssBase + "dlssnr.zip" => Zip(("nvngx_dlssnr.dll", Bytes(0x84, 82_000))),
            _ => null
        };
        return body is null ? null : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    // A DX12 game with native DLSS SR and FG (no RR), Streamline, and ReShade beside it.
    private static DetectedGame Game(string root, string name)
    {
        var game = MfgUpdateAllTests.StreamlineGame(root, name);
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "nvngx_dlssg.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), ReShadeLike());
        return game;
    }

    private static string[] Files(DetectedGame game) => Directory.EnumerateFiles(game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();

    [Fact]
    public async Task Install_BringsTheGamesSrAndFgToTheNewest_ThroughTheDlssSection_KeepingTheOriginals()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer);
        var game = Game(fx.Root, "NewestGame");
        var sr = Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll");
        var fg = Path.Combine(game.BinaryDirectory!, "nvngx_dlssg.dll");
        var oldSr = File.ReadAllBytes(sr);
        var oldFg = File.ReadAllBytes(fg);
        // The shell's DLSS section is the one the install asks; its Update History as the app composes it (#289).
        Assert.Same(fx.Vm.Dlss.DefaultsService, fx.Managed.DlssSection);
        fx.Managed.DlssSection!.History = new ComponentHistory(new ComponentUpdateLogStore(
            Path.Combine(Path.GetDirectoryName(fx.Settings.SettingsPath)!, "prefs", "update-log.json")));

        var result = await fx.Managed.InstallAsync(game, ManagedComponentKind.Dlss5Tool, Files(game));

        Assert.True(result.Success, result.Message);
        Assert.Contains(LinuxManagedComponentService.DlssNewestPrefix + "SR, FG.", result.Message);
        Assert.Equal(NewSr, File.ReadAllBytes(sr));
        Assert.Equal(NewFg, File.ReadAllBytes(fg));
        Assert.Equal(oldSr, File.ReadAllBytes(sr + ".original")); // the DLSS section's backup, restorable there
        Assert.Equal(oldFg, File.ReadAllBytes(fg + ".original"));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "nvngx_dlssd.dll"))); // never a file the game lacks
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "sl.interposer.dll.original"))); // Streamline untouched
        // DLSS5 Tool does not own them: its record lists neither file.
        var record = fx.Records.Find(game, ManagedComponentKind.Dlss5Tool)!;
        Assert.DoesNotContain(record.Files, file => file.Path == sr || file.Path == fg);
        // Update History has the DLSS section's own entries for the swaps.
        var history = MfgUpdateAllTests.History(fx);
        Assert.Contains(history, entry => entry.ComponentName == ComponentHistory.DlssName(DlssComponentKind.SuperResolution) && entry.ToVersion == "310.5.0");
        Assert.Contains(history, entry => entry.ComponentName == ComponentHistory.DlssName(DlssComponentKind.FrameGeneration) && entry.ToVersion == "310.5.0");
    }

    [Fact]
    public async Task Install_WhereTheGamesUpdateInclusionLeavesDlssOut_KeepsItsDlssFiles()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer);
        var game = Game(fx.Root, "PinnedGame");
        var sr = Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll");
        var oldSr = File.ReadAllBytes(sr);
        fx.Vm.UpdatePolicy!.Inclusion!.SetExcluded(game, [ManagedComponentKind.Dlss], [ManagedComponentKind.Dlss]); // #178

        var result = await fx.Managed.InstallAsync(game, ManagedComponentKind.Dlss5Tool, Files(game));

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(LinuxManagedComponentService.DlssKeptByInclusionNote, result.Message);
        Assert.Equal(oldSr, File.ReadAllBytes(sr));
        Assert.False(File.Exists(sr + ".original"));
    }

    [Fact]
    public void TheRowSaysTheDlssSectionBringsTheDlssToTheNewest()
    {
        Assert.DoesNotContain("For the newest DLSS use the DLSS section", Dlss5ToolDeploymentService.AvailableNote);
        Assert.Contains("brings the game's DLSS SR, RR and FG files to the newest through the DLSS section", Dlss5ToolDeploymentService.AvailableNote);
    }

    private static byte[] ReShadeLike()
    {
        var bytes = CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 9);
        Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(bytes, 4096);
        return bytes;
    }

    private static byte[] Bytes(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

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
