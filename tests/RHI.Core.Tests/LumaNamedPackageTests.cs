using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LumaNamedPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-named-tests-" + Guid.NewGuid().ToString("N"));

    public LumaNamedPackageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void NamedEligibility_DoesNotRequireGenericUnrealDx11Rule()
    {
        var game = CreateGame("Named DX12 Game");
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.EngineHint = null;
        var entry = new LumaCatalogEntry(
            "Named DX12 Game", "Filoppi",
            "https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Named_DX12_Game.zip",
            "✅", null, null);

        Assert.True(LumaFrameworkDeploymentService.IsNamedGameEligible(game, entry));
        Assert.Contains("takes priority", LumaFrameworkDeploymentService.NamedEligibilityReason(game, entry), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvePackage_NamedLatestReleaseUsesExactAssetAndPublishedDigest()
    {
        var game = CreateGame("Named DX12 Game");
        game.GraphicsApi = GraphicsApi.DirectX12;
        var zip = MakeNamedZip();
        var digest = Sha256(zip);
        const string asset = "Luma-Named_DX12_Game.zip";
        const string wikiUrl = "https://github.com/rhi-tests/luma-wiki";
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            wikiUrl => Text(WikiRow(game.Name, asset), "text/html"),
            var url when url == LumaFrameworkDeploymentService.LatestReleaseApiUrl
                => Json("[" + ReleaseJson("latest-631", asset, "https://github.com/rhi-tests/named.zip", digest) + "]"),
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var service = new LumaFrameworkDeploymentService(http, NewRecords("resolve.json"), Path.Combine(_root, "cache-resolve"), catalog);

        var selection = await service.ResolvePackageAsync(game);

        Assert.NotNull(selection);
        Assert.True(selection!.IsNamed);
        Assert.Equal("Named DX12 Game", selection.DisplayName);
        Assert.Equal(asset, selection.Release.AssetName);
        Assert.Equal("latest-631", selection.Release.Version);
        Assert.Equal(digest, selection.Release.Sha256);
    }

    [Fact]
    public async Task Install_NamedPackageRecordsCatalogIdentityAndHashOwnership()
    {
        var game = CreateGame("Named DX12 Game");
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.EngineHint = "Unknown Engine";
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var records = NewRecords("install.json");
        SeedManagedReShade(records, game);
        var zip = MakeNamedZip();
        var digest = Sha256(zip);
        const string asset = "Luma-Named_DX12_Game.zip";
        const string wikiUrl = "https://github.com/rhi-tests/luma-wiki";
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            wikiUrl => Text(WikiRow(game.Name, asset), "text/html"),
            var url when url == LumaFrameworkDeploymentService.LatestReleaseApiUrl
                => Json("[" + ReleaseJson("latest-631", asset, "https://github.com/rhi-tests/named.zip", digest) + "]"),
            "https://github.com/rhi-tests/named.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) },
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-install"), catalog);

        var install = await service.InstallAsync(game);

        Assert.True(install.Success, install.Message);
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        Assert.NotNull(record);
        Assert.Equal("0", record!.Metadata["LumaFramework.Generic"]);
        Assert.Equal("Named DX12 Game", record.Metadata["LumaFramework.CatalogName"]);
        Assert.Equal("Filoppi", record.Metadata["LumaFramework.Author"]);
        Assert.Equal(asset, record.Metadata["LumaFramework.Package"]);
        Assert.NotEmpty(record.Files);
        Assert.All(record.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.Sha256)));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "addons", "Luma-Named.addon64")));

        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "addons", "Luma-Named.addon64")));
    }

    [Fact]
    public void NamedEligibility_BlocksPackageOutsideFiloppiGitHubNamespace()
    {
        var game = CreateGame("Unsafe Named Game");
        var entry = new LumaCatalogEntry(
            "Unsafe Named Game", "Unknown",
            "https://example.com/Luma-Unsafe.zip", "✅", null, null);

        Assert.False(LumaFrameworkDeploymentService.IsNamedGameEligible(game, entry));
        Assert.Contains("blocked", LumaFrameworkDeploymentService.NamedEligibilityReason(game, entry), StringComparison.OrdinalIgnoreCase);
    }

    private DetectedGame CreateGame(string name)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllBytes(exe, MakePePayload(0x8664, 4096));
        return new DetectedGame
        {
            Name = name,
            InstallPath = dir,
            BinaryDirectory = dir,
            ExecutablePath = exe,
            PrefixPath = Path.Combine(dir, "pfx"),
            Source = "Steam",
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12
        };
    }

    private LinuxDeploymentRecordStore NewRecords(string name) => new(Path.Combine(_root, name));

    private static void SeedManagedReShade(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "test"
        });
    }

    private static string WikiRow(string name, string asset) => $"""
    <table>
      <tr><th>Name</th><th>Author</th><th>Download Link</th><th>Status</th><th>Special Notes</th><th>Features</th></tr>
      <tr><td>{name}</td><td>Filoppi</td><td><a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/{asset}">Download</a></td><td>✅</td><td>Use game-specific package</td><td></td></tr>
    </table>
    """;

    private static string ReleaseJson(string tag, string asset, string url, string digest)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            assets = new[]
            {
                new { name = asset, browser_download_url = url, digest = "sha256:" + digest }
            }
        });

    private static byte[] MakeNamedZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Luma-Named.addon64", Enumerable.Range(0, 96 * 1024).Select(i => (byte)(i * 17 + 3)).ToArray());
            WriteEntry(zip, "Luma/Named.hlsl", Encoding.UTF8.GetBytes("// named luma\n" + new string('n', 4096)));
            WriteEntry(zip, "dxgi.dll", Encoding.UTF8.GetBytes("bundled proxy must stay excluded"));
            WriteEntry(zip, "ReShade.ini", Encoding.UTF8.GetBytes("bundled ini must stay excluded"));
            WriteEntry(zip, "nvngx_dlss.dll", Encoding.UTF8.GetBytes("bundled dlss must stay excluded"));
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] MakePePayload(ushort machine, int size)
    {
        var bytes = new byte[Math.Max(size, 512)];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Text(string value, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, mediaType)
    };

    private static HttpResponseMessage Json(string value) => Text(value, "application/json");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
