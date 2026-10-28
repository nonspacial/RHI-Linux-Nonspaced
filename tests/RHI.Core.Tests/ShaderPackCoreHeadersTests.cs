using System.IO.Compression;
using System.Net;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #239 (upstream <c>ShaderPackService.Deploy.cs</c> <c>EnsureReShadeHeaders</c>, c1076d3): ReShade's core
/// headers (<c>ReShade.fxh</c>, <c>ReShadeUI.fxh</c>) go with EVERY shader pack, on a search path the packs' effects reach,
/// owned through the game's <c>.rhi-manifest.json</c>, kept while one RHI pack remains and removed with the last. Before
/// the fix only ClshortfuseShaders and MaxG2DSimpleHDR brought them, so Prod80 failed to compile on the owner's
/// Borderlands 3 ("could not open included file 'ReShade.fxh'").
/// </summary>
public sealed class ShaderPackCoreHeadersTests : IDisposable
{
    internal const string CoreSearchPath = @".\reshade-shaders\RHI\_Core\Shaders\**";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-w12-headers-" + Guid.NewGuid().ToString("N"));

    public ShaderPackCoreHeadersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (ReShadeShaderPackService Service, string Game) Fixture(string name, PackServer? server = null)
    {
        var http = new HttpClient(server ?? new PackServer());
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, name + "-cache"), customRoot: Path.Combine(_root, name + "-custom"));
        var game = Directory.CreateDirectory(Path.Combine(_root, name, "game")).FullName;
        return (service, game);
    }

    private static string Rhi(string game, params string[] parts) => Path.Combine([game, "reshade-shaders", "RHI", .. parts]);

    private static IReadOnlyList<string> ManifestPaths(string game)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Rhi(game, ".rhi-manifest.json")));
        return doc.RootElement.GetProperty("Files").EnumerateArray()
            .Select(file => file.GetProperty("RelativePath").GetString()!.Replace('\\', '/')).ToArray();
    }

    // Prove-first (red on 8f15e44): a pack outside the old two-pack rule whose effect includes the headers gets them.
    [Fact]
    public async Task Sync_AnyPack_DeploysTheCoreHeadersOnASearchPathItsEffectsUse()
    {
        var (service, game) = Fixture("prod80");

        var result = await service.SyncAsync(game, ["Prod80"]);

        Assert.Equal(PackServer.HeaderText("ReShade.fxh"), File.ReadAllText(Rhi(game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.Equal(PackServer.HeaderText("ReShadeUI.fxh"), File.ReadAllText(Rhi(game, "_Core", "Shaders", "ReShadeUI.fxh")));
        Assert.Contains(CoreSearchPath, result.EffectSearchPaths);
        Assert.Contains(@".\reshade-shaders\RHI\Prod80\Shaders\**", result.EffectSearchPaths);
        Assert.Contains("_Core/Shaders/ReShade.fxh", ManifestPaths(game));
        Assert.Contains("_Core/Shaders/ReShadeUI.fxh", ManifestPaths(game));
        Assert.Equal("Prod80", Assert.Single(result.PackIds)); // the headers are no pack of their own
    }

    [Fact]
    public async Task Sync_TheLocalCustomPackAlone_AlsoGetsTheHeaders()
    {
        var (service, game) = Fixture("custom");
        File.WriteAllText(Path.Combine(service.CustomRoot, "Shaders", "Mine.fx"), "#include \"ReShade.fxh\"\n");

        var result = await service.SyncAsync(game, [ReShadeShaderPackCatalog.CustomPackId]);

        Assert.True(File.Exists(Rhi(game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.Contains(CoreSearchPath, result.EffectSearchPaths);
    }

    [Fact]
    public async Task Sync_HeadersStayWhileOnePackRemains_AndGoWithTheLast()
    {
        var (service, game) = Fixture("lifecycle");
        await service.SyncAsync(game, ["Prod80", "CrosireMaster"]);
        var header = Rhi(game, "_Core", "Shaders", "ReShade.fxh");
        Assert.True(File.Exists(header));

        var one = await service.SyncAsync(game, ["CrosireMaster"]);
        Assert.True(File.Exists(header));
        Assert.Contains(CoreSearchPath, one.EffectSearchPaths);
        Assert.Contains("_Core/Shaders/ReShade.fxh", ManifestPaths(game));

        var none = await service.SyncAsync(game, []);
        Assert.False(File.Exists(header));
        Assert.Empty(none.EffectSearchPaths);
        Assert.False(Directory.Exists(Rhi(game))); // nothing of RHI's is left, no empty folder either
    }

    [Fact]
    public async Task Sync_AndRemoval_NeverTouchTheUsersOwnHeadersElsewhere()
    {
        var (service, game) = Fixture("user-headers");
        var mine = Path.Combine(game, "reshade-shaders", "Shaders", "ReShade.fxh");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "// the user's own ReShade.fxh");

        await service.SyncAsync(game, ["Prod80"]);
        Assert.Equal("// the user's own ReShade.fxh", File.ReadAllText(mine));
        await service.CleanupAsync(game, operation: null);

        Assert.Equal("// the user's own ReShade.fxh", File.ReadAllText(mine));
        Assert.False(File.Exists(Rhi(game, "_Core", "Shaders", "ReShade.fxh")));
    }

    // As upstream (a header missing from staging is skipped): headers that cannot be staged never stop the packs.
    [Fact]
    public async Task Sync_HeadersThatCannotBeStaged_StillDeploysThePacks()
    {
        var server = new PackServer { HeadersMissing = true };
        var (service, game) = Fixture("no-headers", server);

        var result = await service.SyncAsync(game, ["Prod80"]);

        Assert.True(File.Exists(Rhi(game, "Prod80", "Shaders", "Effect.fx")));
        Assert.False(File.Exists(Rhi(game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.DoesNotContain(CoreSearchPath, result.EffectSearchPaths);
        Assert.DoesNotContain(ManifestPaths(game), path => path.StartsWith("_Core/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Serves a fake pack zip for every pack URL (an effect that includes both headers, a texture) and the core headers
    /// for crosire's <c>slim.zip</c>, on GitHub hosts as the shader-pack policy requires. Counts requests per URL.
    /// </summary>
    internal sealed class PackServer : HttpMessageHandler
    {
        public bool HeadersMissing { get; set; }
        public Dictionary<string, Func<byte[]>> Packs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Requests { get; } = [];

        public static string HeaderText(string name) => $"// {name} (core header fixture)\n" + new string('h', 600);

        public static string EffectText(string pack) =>
            $"#include \"ReShade.fxh\"\n#include \"ReShadeUI.fxh\"\n// {pack} effect\nfloat4 PS() : SV_Target {{ return 1; }}\n";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            byte[] payload;
            if (url.EndsWith("/slim.zip", StringComparison.OrdinalIgnoreCase))
                payload = HeadersMissing
                    ? Zip(("reshade-shaders-slim/Shaders/Other.fxh", new string('o', 900)))
                    : Zip(("reshade-shaders-slim/Shaders/ReShade.fxh", HeaderText("ReShade.fxh")),
                        ("reshade-shaders-slim/Shaders/ReShadeUI.fxh", HeaderText("ReShadeUI.fxh")));
            else
            {
                var pack = Packs.Keys.FirstOrDefault(key => url.Contains(key, StringComparison.OrdinalIgnoreCase));
                payload = pack is not null ? Packs[pack]()
                    : Zip(("pack-main/Shaders/Effect.fx", EffectText(url)), ("pack-main/Textures/Lut.png", new string('t', 900)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }

        public static byte[] Zip(params (string Name, string Content)[] entries)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var (name, content) in entries)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            return memory.ToArray();
        }
    }
}
