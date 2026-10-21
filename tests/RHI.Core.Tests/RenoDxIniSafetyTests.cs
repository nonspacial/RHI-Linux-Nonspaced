using System.Net;
using System.Runtime.Versioning;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// Review w1-renodx-p0 findings 3, 4 and 7: a locked ReShade.ini is refused with a reason, a symlinked one is only
// written through to a reshade.ini in the game or the user's ReShade config folder, and a rollback always finishes.
[SupportedOSPlatform("linux")]
public sealed class RenoDxIniSafetyTests : IDisposable
{
    private const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-ini-safety-" + Guid.NewGuid().ToString("N"));

    public RenoDxIniSafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Apply_ReadOnlyIni_IsRefusedAndLeftAlone()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        File.SetUnixFileMode(ini, ReadOnly);

        var refused = Assert.Throws<IOException>(() =>
            RenoDxIniOverrideManager.Apply(_root, new Dictionary<string, string> { ["Set_Path"] = "1" }));

        Assert.Equal(AtomicFile.ReadOnlyRefusalPrefix + ini, refused.Message);
        Assert.Equal("[renodx]\nSet_Path=0\n", File.ReadAllText(ini));
        Assert.Equal(ReadOnly, File.GetUnixFileMode(ini));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void SetCompatibilitySetting_ReadOnlyIni_FailsWithTheReasonInBothLanguages()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        File.SetUnixFileMode(ini, ReadOnly);
        using var http = new HttpClient(new Handler("{\"version\":2}"));
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")),
            new RenoDxCatalogService(http), Path.Combine(_root, "cache"));

        var result = service.SetCompatibilitySetting(new DetectedGame { Name = "Locked", InstallPath = _root, BinaryDirectory = bin }, "Set_Path", "1");

        Assert.False(result.Success);
        Assert.Contains(AtomicFile.ReadOnlyRefusalPrefix + ini, result.Message, StringComparison.Ordinal);
        Assert.Contains("Arquivo somente leitura não substituído", UiLocalizationService.PresentFor("pt-BR", result.Message), StringComparison.Ordinal);
        Assert.Contains("Read-only file not replaced", UiLocalizationService.PresentFor("en-US", result.Message), StringComparison.Ordinal);
        Assert.Equal("[renodx]\nSet_Path=0\n", File.ReadAllText(ini));
    }

    [Fact]
    public void Apply_LinkIntoTheUserReShadeConfigFolder_IsWrittenThrough()
    {
        // A folder of its own inside the user's ReShade folder: other tests keep their shared ReShade.ini files there too,
        // and deleting the whole folder took theirs away mid-run (w7/reshade #207 flake).
        var shared = Path.Combine(RenoDxIniOverrideManager.UserReShadeConfigDirectories[0], Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shared);
        var real = Path.Combine(shared, "ReShade.ini");
        try
        {
            File.WriteAllText(real, "[renodx]\nSet_Path=0\n");
            var game = Path.Combine(_root, "game");
            Directory.CreateDirectory(game);
            var link = Path.Combine(game, "ReShade.ini");
            File.CreateSymbolicLink(link, real);

            RenoDxIniOverrideManager.Apply(game, new Dictionary<string, string> { ["Set_Path"] = "1" }, gameRoot: game);

            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal("[renodx]\nSet_Path=1\n", File.ReadAllText(real));
        }
        finally
        {
            Directory.Delete(shared, true);
        }
    }

    [Fact]
    public void Apply_LinkOutsideTheGameAndConfigFolders_IsRefusedAndTheTargetUntouched()
    {
        var game = Path.Combine(_root, "game");
        var outside = Path.Combine(_root, "home");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "reshade.ini");
        File.WriteAllText(victim, "[renodx]\nSet_Path=0\n");
        var link = Path.Combine(game, "ReShade.ini");
        File.CreateSymbolicLink(link, victim);

        var refused = Assert.Throws<IOException>(() =>
            RenoDxIniOverrideManager.Apply(game, new Dictionary<string, string> { ["Set_Path"] = "1" }, gameRoot: game));

        Assert.Equal($"{RenoDxIniOverrideManager.LinkRefusalPrefix}{link} -> {victim}", refused.Message);
        Assert.Equal("[renodx]\nSet_Path=0\n", File.ReadAllText(victim));
    }

    [Fact]
    public async Task Install_LinkedIniOutsideTheGame_FailsWithTheReasonAndRollsTheAddonBack()
    {
        var game = Path.Combine(_root, "game");
        var bin = Path.Combine(game, "bin");
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outside);
        var exe = Path.Combine(bin, "Linked.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var victim = Path.Combine(outside, "ReShade.ini");
        File.WriteAllText(victim, "[renodx]\nSet_Path=0\n");
        File.CreateSymbolicLink(Path.Combine(bin, "ReShade.ini"), victim);
        var detected = new DetectedGame { Name = "Linked Game", InstallPath = game, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton" };
        using var http = new HttpClient(new Handler("""
            {
              "version": 2,
              "snapshotOverrides": { "Linked Game": "https://clshortfuse.github.io/renodx/renodx-linked.addon64" },
              "renodxIniOverrides": { "Linked Game": { "Set_Path": "1" } }
            }
            """));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest);

        var install = await service.InstallAsync(detected, [exe, Path.Combine(bin, "ReShade64.dll")]);

        Assert.False(install.Success);
        Assert.Contains(RenoDxIniOverrideManager.LinkRefusalPrefix, install.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Rollback incomplete", install.Message, StringComparison.Ordinal);
        Assert.StartsWith("Falha na instalação do RenoDX: Link do ReShade.ini não seguido:",
            UiLocalizationService.PresentFor("pt-BR", install.Message), StringComparison.Ordinal);
        Assert.Equal("[renodx]\nSet_Path=0\n", File.ReadAllText(victim));
        Assert.Empty(Directory.EnumerateFiles(game, "*.addon64", SearchOption.AllDirectories));
        Assert.Null(store.Find(detected, ManagedComponentKind.RenoDX));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Handler(string manifestJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://manifest.test/manifest.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestJson, Encoding.UTF8, "application/json") });
            if (!url.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
