using System.IO.Compression;
using System.Net;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ReShadeShaderPackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-shader-tests-" + Guid.NewGuid().ToString("N"));

    public ReShadeShaderPackServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Sync_DeploysIntoIsolatedRhiTreeAndReturnsSearchPaths()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);

        var result = await service.SyncAsync(game, ["CrosireMaster"]);

        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Textures", "Test.png")));
        Assert.Contains(@".\reshade-shaders\RHI\CrosireMaster\Shaders\**", result.EffectSearchPaths);
        Assert.Contains(@".\reshade-shaders\RHI\CrosireMaster\Textures\**", result.TextureSearchPaths);
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", ".rhi-manifest.json")));
    }

    [Fact]
    public async Task Sync_ThirdPartyHdrPack_DeploysHiddenCoreHeadersAndSearchPath()
    {
        using var http = new HttpClient(new RoutingZipHandler(
            CreateShaderZip(),
            CreateCoreHeaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "dependency-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "dependency-game");
        Directory.CreateDirectory(game);

        var result = await service.SyncAsync(game, ["MaxG2DSimpleHDR"]);

        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "_Core", "Shaders", "ReShade.fxh")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "_Core", "Shaders", "ReShadeUI.fxh")));
        Assert.Contains(@".\reshade-shaders\RHI\_Core\Shaders\**", result.EffectSearchPaths);
        Assert.DoesNotContain(result.PackIds, id => id.Equals("ReShadeCoreHeaders", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("MaxG2DSimpleHDR", Assert.Single(result.PackIds));
    }

    [Fact]
    public async Task Cleanup_RemovesHiddenCoreHeadersWhenThirdPartyPackIsRemoved()
    {
        using var http = new HttpClient(new RoutingZipHandler(
            CreateShaderZip(),
            CreateCoreHeaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "dependency-cleanup-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "dependency-cleanup-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["ClshortfuseShaders"]);

        await service.CleanupAsync(game, operation: null);

        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "_Core", "Shaders", "ReShade.fxh")));
        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "_Core", "Shaders", "ReShadeUI.fxh")));
    }

    [Fact]
    public async Task PackExclusions_PersistAcrossServiceInstancesAndRefresh()
    {
        var payload = CreateSelectiveShaderZip();
        var cache = Path.Combine(_root, "exclusion-cache");
        var game = Path.Combine(_root, "exclusion-game");
        Directory.CreateDirectory(game);

        using (var http = new HttpClient(new ZipHandler(payload)))
        {
            var service = new ReShadeShaderPackService(http, cache, customRoot: Path.Combine(_root, "custom-shaders"));
            service.SetExcludedFiles("CrosireMaster", ["Drop.fx"]);
            var first = await service.SyncAsync(game, ["CrosireMaster"]);

            Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Keep.fx")));
            Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Drop.fx")));
            Assert.Contains(@".\reshade-shaders\RHI\CrosireMaster\Shaders\**", first.EffectSearchPaths);
        }

        using (var http = new HttpClient(new ZipHandler(payload)))
        {
            var restarted = new ReShadeShaderPackService(http, cache, customRoot: Path.Combine(_root, "custom-shaders"));
            Assert.Contains("Drop.fx", restarted.GetExcludedFiles("CrosireMaster"));

            await restarted.SyncAsync(game, ["CrosireMaster"]);
            Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Keep.fx")));
            Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Drop.fx")));

            restarted.SetExcludedFiles("CrosireMaster", []);
            await restarted.SyncAsync(game, ["CrosireMaster"]);
            Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Drop.fx")));
        }
    }


    [Fact]
    public async Task PackExclusions_CanTargetTextureAndNestedShaderByRelativeSelector()
    {
        var payload = CreatePerFileShaderZip();
        var cache = Path.Combine(_root, "per-file-cache");
        var game = Path.Combine(_root, "per-file-game");
        Directory.CreateDirectory(game);
        using var http = new HttpClient(new ZipHandler(payload));
        var service = new ReShadeShaderPackService(http, cache, customRoot: Path.Combine(_root, "custom-shaders"));

        service.SetExcludedFiles("CrosireMaster", ["Shaders/Sub/Drop.fx", "Textures/LUTs/Drop.png"]);
        await service.SyncAsync(game, ["CrosireMaster"]);

        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Sub", "Keep.fx")));
        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Sub", "Drop.fx")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Textures", "LUTs", "Keep.png")));
        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Textures", "LUTs", "Drop.png")));
        Assert.Contains("Shaders/Sub/Drop.fx", service.GetExcludedFiles("CrosireMaster"));
        Assert.Contains("Textures/LUTs/Drop.png", service.GetExcludedFiles("CrosireMaster"));
    }

    [Fact]
    public async Task ListPackFiles_CustomShadersAndTexturesExposeStableSelectorsAndExclusionState()
    {
        var custom = Path.Combine(_root, "custom-picker");
        Directory.CreateDirectory(Path.Combine(custom, "Shaders", "Folder"));
        Directory.CreateDirectory(Path.Combine(custom, "Textures", "LUTs"));
        File.WriteAllText(Path.Combine(custom, "Shaders", "Folder", "Mine.fx"), "shader");
        File.WriteAllText(Path.Combine(custom, "Textures", "LUTs", "Mine.png"), "texture");
        using var http = new HttpClient(new FakeOfflineHandler());
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "custom-picker-cache"), customRoot: custom);
        service.SetExcludedFiles(ReShadeShaderPackCatalog.CustomPackId, ["Textures/LUTs/Mine.png"]);

        var files = await service.ListPackFilesAsync(ReShadeShaderPackCatalog.CustomPackId);

        var shader = Assert.Single(files, x => !x.IsTexture);
        Assert.Equal("Shaders/Folder/Mine.fx", shader.Selector);
        Assert.False(shader.IsExcluded);
        var texture = Assert.Single(files, x => x.IsTexture);
        Assert.Equal("Textures/LUTs/Mine.png", texture.Selector);
        Assert.True(texture.IsExcluded);
    }

    [Fact]
    public async Task Cleanup_RemovesUnchangedManagedFiles()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "cleanup-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "cleanup-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["CrosireMaster"]);

        await service.CleanupAsync(game, operation: null);

        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx")));
        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", ".rhi-manifest.json")));
    }

    [Fact]
    public async Task Cleanup_PreservesUserModifiedManagedFile()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "modified-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "modified-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["CrosireMaster"]);
        var shader = Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx");
        File.AppendAllText(shader, "\n// user edit");

        await service.CleanupAsync(game, operation: null);

        Assert.True(File.Exists(shader));
        Assert.Contains("user edit", File.ReadAllText(shader));
        Assert.False(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", ".rhi-manifest.json")));
    }

    [Fact]
    public async Task Sync_BlocksModifiedManagedDestinationBeforeOverwrite()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "collision-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "collision-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["CrosireMaster"]);
        var shader = Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx");
        File.AppendAllText(shader, "\n// changed outside RHI");

        var ex = await Assert.ThrowsAsync<IOException>(() => service.SyncAsync(game, ["CrosireMaster"]));

        Assert.Contains("modified or is externally owned", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changed outside RHI", File.ReadAllText(shader));
    }

    [Fact]
    public async Task Sync_UnknownPackIdIsRejectedWithoutRemovingExistingManagedFiles()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "unknown-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "unknown-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["CrosireMaster"]);
        var shader = Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(game, ["DoesNotExist"]));

        Assert.Contains("Unknown ReShade shader pack", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(shader));
    }

    [Fact]
    public async Task Sync_RejectsZipSlipEntry()
    {
        using var http = new HttpClient(new ZipHandler(CreateZipSlipArchive()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "zipslip-cache"), customRoot: Path.Combine(_root, "custom-shaders"));
        var game = Path.Combine(_root, "zipslip-game");
        Directory.CreateDirectory(game);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync(game, ["CrosireMaster"]));
        Assert.False(File.Exists(Path.Combine(_root, "evil.fx")));
    }

    // w2/vmsplit review finding 1: once the old packs start coming out of the game folder the sync runs to the end (a
    // cancel half-way used to leave copied packs that no manifest owned).
    [Fact]
    public async Task Sync_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var http = new HttpClient(new ZipHandler(CreateShaderZip()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "cancel-cache"), customRoot: Path.Combine(_root, "cancel-custom"));
        var game = Directory.CreateDirectory(Path.Combine(_root, "cancel-game")).FullName;

        var run = await CancelAtWritePhase.RunAsync(ct => service.SyncAsync(game, ["CrosireMaster"], ct), [game]);

        Assert.Contains(@".\reshade-shaders\RHI\CrosireMaster\Shaders\**", run.Result.EffectSearchPaths);
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", ".rhi-manifest.json")));
    }

    private static byte[] CreateShaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "reshade-shaders-master/Shaders/Test.fx", "float4 main() : SV_Target { return 1; }");
            WriteEntry(archive, "reshade-shaders-master/Textures/Test.png", new string('P', 800));
        }
        return memory.ToArray();
    }

    private static byte[] CreateSelectiveShaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "reshade-shaders-master/Shaders/Keep.fx", "float4 keep() : SV_Target { return 1; }");
            WriteEntry(archive, "reshade-shaders-master/Shaders/Drop.fx", "float4 drop() : SV_Target { return 0; }");
            WriteEntry(archive, "reshade-shaders-master/Textures/Keep.png", new string('T', 800));
        }
        return memory.ToArray();
    }


    private static byte[] CreatePerFileShaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "reshade-shaders-master/Shaders/Sub/Keep.fx", "keep");
            WriteEntry(archive, "reshade-shaders-master/Shaders/Sub/Drop.fx", "drop");
            WriteEntry(archive, "reshade-shaders-master/Textures/LUTs/Keep.png", new string('K', 800));
            WriteEntry(archive, "reshade-shaders-master/Textures/LUTs/Drop.png", new string('D', 800));
        }
        return memory.ToArray();
    }

    private static byte[] CreateCoreHeaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "reshade-shaders-slim/Shaders/ReShade.fxh", new string('R', 800));
            WriteEntry(archive, "reshade-shaders-slim/Shaders/ReShadeUI.fxh", new string('U', 800));
        }
        return memory.ToArray();
    }

    private static byte[] CreateZipSlipArchive()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "repo/Shaders/../../evil.fx", new string('X', 800));
        }
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class RoutingZipHandler(byte[] normalPayload, byte[] corePayload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.AbsoluteUri.Contains("slim.zip", StringComparison.OrdinalIgnoreCase) == true
                ? corePayload
                : normalPayload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class FakeOfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network is not expected for the local custom shader pack.");
    }

    private sealed class ZipHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
