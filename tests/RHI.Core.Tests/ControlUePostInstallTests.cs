using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ControlUePostInstallTests : IDisposable
{
    private const string RendererIni =
        "{\r\n  \"m_bSkipIntroVideos\": false,\r\n  \"m_eHDRPreset\": 0,\r\n  \"m_fHDRMaxNitsOverride\": -1.0\r\n}\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-control-ue-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _sources;
    private string _srVersion = "310.4.0";

    public ControlUePostInstallTests()
    {
        _sources = Path.Combine(_root, "dlss-cache");
        Directory.CreateDirectory(_sources);
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), "NEW-SR");
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlssd.dll"), "NEW-RR");
    }

    [Fact]
    public async Task Install_UpgradesDlssDeploysRrAndSetsHdrPresetInGameFolder_RemoveRestoresBytes()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var iniBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(RendererIni)).ToArray();
        File.WriteAllBytes(Path.Combine(bin, "renderer.ini"), iniBytes);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        var prefixIni = Path.Combine(game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "Remedy", "Control", "renderer.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(prefixIni)!);
        File.WriteAllText(prefixIni, RendererIni);

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Equal("NEW-RR", File.ReadAllText(Path.Combine(bin, "nvngx_dlssd.dll")));
        var patched = File.ReadAllBytes(Path.Combine(bin, "renderer.ini"));
        Assert.Equal(Encoding.UTF8.GetPreamble(), patched[..3]);
        Assert.Equal(RendererIni.Replace("\"m_eHDRPreset\": 0", "\"m_eHDRPreset\": 2"), Encoding.UTF8.GetString(patched[3..]));
        Assert.Equal(RendererIni, File.ReadAllText(prefixIni));
        Assert.Contains("driver-profile", install.Message);
        AssertNotGenericInPortuguese(install.Message);
        Assert.True(store.Find(game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(ControlUePostInstallService.MetadataKey));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Equal(iniBytes, File.ReadAllBytes(Path.Combine(bin, "renderer.ini")));
        Assert.False(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));
        AssertNotGenericInPortuguese(remove.Message);
    }

    [Fact]
    public async Task Remove_AfterGameRewroteRendererIniAndUserReplacedDlss_KeepsTheirChanges()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        var rewritten = File.ReadAllText(Path.Combine(bin, "renderer.ini")).Replace("\"m_bSkipIntroVideos\": false", "\"m_bSkipIntroVideos\": true");
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), rewritten);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "USER-SR");

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(RendererIni.Replace("\"m_bSkipIntroVideos\": false", "\"m_bSkipIntroVideos\": true"),
            File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.Equal("USER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        AssertNotGenericInPortuguese(remove.Message);
    }

    [Fact]
    public async Task Install_OverEarlierDlssSwap_RemoveReturnsTheSwappedDllAndKeepsOriginalBackup()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "SWAPPED-SR");
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll.original"), "TRUE-ORIGINAL");

        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("SWAPPED-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Empty(Directory.EnumerateFiles(bin, "*" + ControlUePostInstallService.PreviousSnapshotSuffix));
    }

    [Fact]
    public async Task Update_ReappliesNewestDll_RemoveStillRestoresThePreRhiState()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), "NEWER-SR");
        _srVersion = "310.5.0";
        var update = await service.UpdateAsync(game, [game.ExecutablePath!]);

        Assert.True(update.Success, update.Message);
        Assert.Equal("NEWER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Contains("\"m_eHDRPreset\": 2", File.ReadAllText(Path.Combine(bin, "renderer.ini")));

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal(RendererIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
    }

    [Fact]
    public async Task Install_WithoutRendererIni_StillDeploysDllsAndSaysToLaunchFirst()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.Contains("renderer.ini not found", install.Message);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renderer.ini")));
    }

    [Fact]
    public async Task Install_OtherGameWithTheSameAddon_RunsNoControlSteps()
    {
        var (game, service, store) = Arrange("Other Remedy Game");
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Equal(RendererIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.False(store.Find(game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(ControlUePostInstallService.MetadataKey));
    }

    private (DetectedGame Game, RenoDxDeploymentService Service, LinuxDeploymentRecordStore Store) Arrange(string name)
    {
        var bin = Path.Combine(_root, "steamapps", "common", "Control");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Control_DX12.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var prefix = Path.Combine(_root, "compatdata", "870780", "pfx");
        Directory.CreateDirectory(prefix);
        var game = new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", SteamAppId = 870780
        };
        var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var controlUe = new ControlUePostInstallService(new TransactionalFileDeploymentService(), (kind, _) =>
            Task.FromResult<(string, string)?>(kind == DlssComponentKind.SuperResolution
                ? (Path.Combine(_sources, "nvngx_dlss.dll"), _srVersion)
                : (Path.Combine(_sources, "nvngx_dlssd.dll"), "310.4.0")));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"), controlUe: controlUe);
        return (game, service, store);
    }

    private static void AssertNotGenericInPortuguese(string message)
        => Assert.True(UiLocalizationService.PresentFor("pt-BR", message) != UiLocalizationService.TextFor("pt-BR", "Ui.UnlocalizedPlatformMessage"), message);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                                    "<tr><td>Control Ultimate Edition</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr>" +
                                    "<tr><td>Other Remedy Game</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
