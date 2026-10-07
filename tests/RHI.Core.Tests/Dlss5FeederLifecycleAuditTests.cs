using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using Xunit;

namespace RHI.Core.Tests;

public sealed class Dlss5FeederLifecycleAuditTests
{
    [Fact]
    public async Task FullStack_BothConsumersInstallRemoveReinstallAndSwitchWithHdrOwnershipPreserved()
    {
        using var f = new FeederFixture();
        f.AddManagedHdr();
        // Two installs per consumer and both switch directions, each through public APIs.
        foreach (var consumer in new[] { Dlss5FeederConsumer.DeepFriedChicken, Dlss5FeederConsumer.DeepFriedChicken,
                     Dlss5FeederConsumer.RenoDxDlss5, Dlss5FeederConsumer.RenoDxDlss5, Dlss5FeederConsumer.DeepFriedChicken })
        {
            var result = await f.Install(consumer);
            Assert.True(result.Success, result.Message);
            f.AssertInstalled(consumer, expectedEarlyLoad: consumer == Dlss5FeederConsumer.DeepFriedChicken ? "1" : "0");
            Assert.True(File.Exists(f.HdrPath));
            Assert.NotNull(f.Records.Find(f.Game, ManagedComponentKind.RenoDX));
            var remove = await f.Service.RemoveAsync(f.Game);
            Assert.True(remove.Success, remove.Message);
            f.AssertClean();
            Assert.Equal(f.HdrBytes, File.ReadAllBytes(f.HdrPath));
            Assert.NotNull(f.Records.Find(f.Game, ManagedComponentKind.RenoDX));
        }
    }

    [Fact]
    public async Task FullStack_DfcPreexistingEarlyLoadTokenIsNotOwnedOrRemoved()
    {
        using var f = new FeederFixture(preexistingEarlyLoad: true);
        var result = await f.Install(Dlss5FeederConsumer.DeepFriedChicken);
        Assert.True(result.Success, result.Message);
        f.AssertInstalled(Dlss5FeederConsumer.DeepFriedChicken, expectedEarlyLoad: "0");
        var remove = await f.Service.RemoveAsync(f.Game);
        Assert.True(remove.Success, remove.Message);
        f.AssertClean(keepEarlyLoad: true);
    }

    [Fact]
    public async Task FullStack_BothConsumersRollbackAfterFilesCopiedBeforeRecordWrite()
    {
        foreach (var consumer in new[] { Dlss5FeederConsumer.DeepFriedChicken, Dlss5FeederConsumer.RenoDxDlss5 })
        {
            using var f = new FeederFixture(missingIncludes: true);
            f.AddManagedHdr();
            var result = await f.Install(consumer);
            Assert.False(result.Success);
            Assert.Contains("LumeniteFX include directory is missing", result.Message);
            f.AssertClean();
            Assert.Equal(f.HdrBytes, File.ReadAllBytes(f.HdrPath));
            Assert.NotNull(f.Records.Find(f.Game, ManagedComponentKind.RenoDX));
        }
    }

    [Fact]
    public async Task FullStack_BothConsumersRollbackWhenDeploymentRecordWriteFails()
    {
        foreach (var consumer in new[] { Dlss5FeederConsumer.DeepFriedChicken, Dlss5FeederConsumer.RenoDxDlss5 })
        {
            using var f = new FeederFixture();
            f.AddManagedHdr();
            var recordsBefore = File.ReadAllBytes(f.RecordsPath);
            ComponentOperationResult result;
            using (LedgerWriteBlocker.Block(f.Records, f.RecordsPath))
                result = await f.Install(consumer);
            Assert.False(result.Success);
            Assert.Contains("deployments.json", result.Message);
            Assert.DoesNotContain("Object reference", result.Message);
            f.AssertClean();
            Assert.Equal(recordsBefore, File.ReadAllBytes(f.RecordsPath));
            Assert.Equal(f.HdrBytes, File.ReadAllBytes(f.HdrPath));
        }
    }

    [Fact]
    public async Task FullStack_RemoveProtectsModifiedConsumerBeforeAnyTeardown()
    {
        foreach (var consumer in new[] { Dlss5FeederConsumer.DeepFriedChicken, Dlss5FeederConsumer.RenoDxDlss5 })
        {
            using var f = new FeederFixture();
            var install = await f.Install(consumer);
            Assert.True(install.Success, install.Message);
            var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
            var target = Path.Combine(f.Bin, consumer == Dlss5FeederConsumer.DeepFriedChicken ? "deep-fried-chicken.addon64" : "renodx-dlss5.addon64");
            var original = File.ReadAllBytes(target);
            File.AppendAllText(target, "external modification");
            var iniBefore = File.ReadAllBytes(f.IniPath);
            var result = await f.Service.RemoveAsync(f.Game);
            Assert.False(result.Success);
            Assert.Contains("Protected modified file", result.Message);
            Assert.All(record.Files, file => Assert.True(File.Exists(file.Path), file.Path));
            Assert.Equal(iniBefore, File.ReadAllBytes(f.IniPath));
            Assert.NotNull(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
            File.WriteAllBytes(target, original);
            var remove = await f.Service.RemoveAsync(f.Game);
            Assert.True(remove.Success, remove.Message);
            f.AssertClean();
        }
    }

    [Fact]
    public async Task FullStack_DfcExternalConfigIsPreservedByteForByte()
    {
        using var f = new FeederFixture();
        var config = Path.Combine(f.Bin, "Deep-Fried-Chicken.cfg");
        var bytes = Encoding.UTF8.GetBytes("arm=1\nenabled=1\nuser_setting=27\n");
        File.WriteAllBytes(config, bytes);
        var result = await f.Install(Dlss5FeederConsumer.DeepFriedChicken);
        Assert.True(result.Success, result.Message);
        Assert.Equal(bytes, File.ReadAllBytes(config));
        Assert.False(File.Exists(Path.Combine(f.Bin, "deep-fried-chicken.cfg")), "Existing case-variant DFC configuration must not be duplicated.");
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.True(Assert.Single(record.Files, x => x.Path == config).ExistedBeforeInstall);
        var remove = await f.Service.RemoveAsync(f.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.Equal(bytes, File.ReadAllBytes(config));
        f.AssertClean();
    }

    private sealed class FeederFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-feeder-lifecycle-audit-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly string _package;
        private readonly byte[] _renoAddon;
        private readonly string _dfcHash;
        private readonly string _renoHash;
        private const string UserPreset = @".\UserPreset.ini";
        public string Bin { get; }
        public string IniPath => Path.Combine(Bin, "ReShade.ini");
        public string RecordsPath { get; }
        public string HdrPath => Path.Combine(Bin, "renodx-compatible-title.addon64");
        public byte[] HdrBytes { get; } = Pe(80 * 1024);
        public DetectedGame Game { get; }
        public LinuxDeploymentRecordStore Records { get; }
        public Dlss5FeederFullDeploymentService Service { get; }
        public List<string> Requests { get; } = [];

        public FeederFixture(bool preexistingEarlyLoad = false, bool missingIncludes = false)
        {
            Bin = Path.Combine(_root, "game");
            var prefix = Path.Combine(_root, "prefix");
            Directory.CreateDirectory(Bin);
            Directory.CreateDirectory(prefix);
            var exe = Path.Combine(Bin, "Game.exe");
            File.WriteAllBytes(exe, Pe(128 * 1024));
            var reshade = Pe(2 * 1024 * 1024);
            Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 4096);
            File.WriteAllBytes(Path.Combine(Bin, "dxgi.dll"), reshade);
            var compiler = Pe(1_100_000);
            Encoding.ASCII.GetBytes("Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47.dll").CopyTo(compiler, 4096);
            File.WriteAllBytes(Path.Combine(Bin, "d3dcompiler_47.dll"), compiler);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
            File.WriteAllText(IniPath, "[GENERAL]\nPresetPath=" + UserPreset + "\nEffectSearchPaths=.\\user\\Shaders\\**\nTextureSearchPaths=.\\user\\Textures\\**\n[ADDON]\n" +
                (preexistingEarlyLoad ? "LoadFromDllMain=deep-fried-chicken.addon64\n" : ""));
            Game = new DetectedGame { Name = "Synthetic Feeder audit", InstallPath = Bin, BinaryDirectory = Bin,
                ExecutablePath = exe, PrefixPath = prefix, Source = "Audit", Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
            RecordsPath = Path.Combine(_root, "deployments.json");
            Records = new LinuxDeploymentRecordStore(RecordsPath);

            var addon = Pe(70 * 1024);
            var bridge = Pe(4 * 1024);
            var sums = $"Deep Fried Chicken 1.6.8\n{Hash(addon)}  deep-fried-chicken.addon64\n{Hash(bridge)}  deep-fried-chicken-nvngx.dll\n";
            _package = Path.Combine(_root, "Deep-Fried-Chicken-v1.6.8.zip");
            File.WriteAllBytes(_package, Zip(("deep-fried-chicken.addon64", addon), ("deep-fried-chicken-nvngx.dll", bridge),
                ("deep-fried-chicken.cfg", Encoding.UTF8.GetBytes("arm=1\nenabled=1\n")), ("SHA256SUMS.txt", Encoding.UTF8.GetBytes(sums))));
            _dfcHash = Hash(File.ReadAllBytes(_package));
            _renoAddon = Pe(70 * 1024);
            _renoHash = Hash(_renoAddon);
            var feederArchive = Zip(("addon64/dlss5-feed.addon64", Pe(70 * 1024)), ("shaders/DLSS5_Feed.fx", Encoding.UTF8.GetBytes(new string('f', 1024))));
            var lumeniteFiles = new List<(string, byte[])> { ("LumeniteFX/Shaders/lumenite_Kernel.fx", Encoding.UTF8.GetBytes(new string('k', 1024))),
                ("LumeniteFX/Textures/lumenite_test.png", new byte[2048]) };
            if (!missingIncludes) lumeniteFiles.Add(("LumeniteFX/Shaders/include/kernel.fxh", Encoding.UTF8.GetBytes("// include\n")));
            var lumenite = Zip(lumeniteFiles.ToArray());
            var headers = Zip(("Shaders/ReShade.fxh", new byte[1024]), ("Shaders/ReShadeUI.fxh", new byte[1024]), ("Shaders/DrawText.fxh", new byte[1024]));
            var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
                ["https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1"] = Release("0.13.1-beta.1", "DLSS5-Feeder-0.13.1-beta.1.zip", "https://fixtures.invalid/feeder.zip", feederArchive),
                ["https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100"] = Release("1.2.3", "renodx-dlss5.addon64", "https://fixtures.invalid/renodx.addon64", _renoAddon),
                ["https://fixtures.invalid/feeder.zip"] = feederArchive,
                ["https://fixtures.invalid/renodx.addon64"] = _renoAddon,
                [DlssManagerService.ManifestUrl] = Encoding.UTF8.GetBytes("{\"dlss\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/sr.zip\"}],\"dlssnr\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/nr.zip\"}]}"),
                ["https://github.com/fixtures/sr.zip"] = Zip(("nvngx_dlss.dll", Pe(128 * 1024))),
                ["https://github.com/fixtures/nr.zip"] = Zip(("nvngx_dlssnr.dll", Pe(128 * 1024))),
                ["https://codeload.github.com/umar-afzaal/LumeniteFX/zip/refs/heads/mainline"] = lumenite,
                ["https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip"] = headers
            };
            _http = new HttpClient(new FixtureHandler(responses, Requests));
            var dlss = new DlssManagerService(_http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));
            var manifest = new UpstreamRhiManifestService(_http, "https://fixtures.invalid/unused-manifest");
            var legacy = new UpstreamRhiAddonDeploymentService(_http, Records, manifest, new UpstreamRhiAddonDeploymentOptions(
                ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://fixtures.invalid/unused-release", 64 * 1024,
                DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "cache-legacy"));
            Service = new Dlss5FeederFullDeploymentService(_http, Records, dlss, legacy, Path.Combine(_root, "cache-feeder"), new WineDllOverrideService());
        }

        public Task<ComponentOperationResult> Install(Dlss5FeederConsumer consumer)
            => Service.InstallAsync(Game, consumer, consumer == Dlss5FeederConsumer.DeepFriedChicken ? _package : null, null);

        public void AddManagedHdr()
        {
            File.WriteAllBytes(HdrPath, HdrBytes);
            Records.Upsert(new LinuxDeploymentRecord { GameKey = LinuxDeploymentRecordStore.GameKey(Game), GameName = Game.Name,
                Component = ManagedComponentKind.RenoDX, BinaryDirectory = Bin, Version = "synthetic compatible HDR profile",
                Files = [new LinuxDeployedFile { Path = HdrPath, Sha256 = Hash(HdrBytes), ExistedBeforeInstall = false }] });
        }

        public void AssertInstalled(Dlss5FeederConsumer consumer, string expectedEarlyLoad)
        {
            var dfc = consumer == Dlss5FeederConsumer.DeepFriedChicken;
            var record = Records.Find(Game, ManagedComponentKind.Dlss5Feeder);
            Assert.NotNull(record);
            Assert.Equal(dfc ? "Deep Fried Chicken" : "RenoDX DLSS5", record.Metadata["Feeder.Consumer"]);
            Assert.Equal(dfc ? "1.6.8" : "1.2.3", record.Metadata["Feeder.ConsumerVersion"]);
            Assert.Equal(dfc ? _dfcHash : _renoHash, record.Metadata["Feeder.ConsumerPackageSha256"]);
            Assert.Equal(expectedEarlyLoad, record.Metadata["Feeder.LoadFromDllMainAdded"]);
            Assert.All(record.Files, file => Assert.Equal(file.Sha256, Hash(File.ReadAllBytes(file.Path))));
            foreach (var name in new[] { "dlss5-feed.addon64", "nvngx_dlss.dll", "nvngx_dlssnr.dll", "RHI-DLSS5-Feeder.ini" })
                Assert.True(File.Exists(Path.Combine(Bin, name)), name);
            foreach (var name in new[] { "deep-fried-chicken.addon64", "deep-fried-chicken-nvngx.dll", "deep-fried-chicken.cfg" })
                Assert.Equal(dfc, File.Exists(Path.Combine(Bin, name)));
            foreach (var name in new[] { "DLSS5_Feed.fx", "lumenite_Kernel.fx", "include/kernel.fxh", "ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh" })
                Assert.True(File.Exists(Path.Combine(Bin, "reshade-shaders", "RHI", "DLSS5FeederFull", "Shaders", name)), name);
            Assert.Equal(!dfc, File.Exists(Path.Combine(Bin, "renodx-dlss5.addon64")));
            Assert.Equal(dfc, File.ReadAllText(IniPath).Contains("deep-fried-chicken.addon64", StringComparison.Ordinal));
            Assert.DoesNotContain(record.Files, file => file.Path == HdrPath);
        }

        public void AssertClean(bool keepEarlyLoad = false, bool keepConfig = false)
        {
            Assert.Null(Records.Find(Game, ManagedComponentKind.Dlss5Feeder));
            foreach (var name in new[] { "dlss5-feed.addon64", "nvngx_dlss.dll", "nvngx_dlssnr.dll", "RHI-DLSS5-Feeder.ini",
                         "deep-fried-chicken.addon64", "deep-fried-chicken-nvngx.dll", "renodx-dlss5.addon64" })
                Assert.False(File.Exists(Path.Combine(Bin, name)), name);
            Assert.Equal(keepConfig, File.Exists(Path.Combine(Bin, "deep-fried-chicken.cfg")));
            var tree = Path.Combine(Bin, "reshade-shaders", "RHI", "DLSS5FeederFull");
            Assert.False(Directory.Exists(tree));
            var ini = File.ReadAllText(IniPath);
            Assert.Contains("PresetPath=" + UserPreset, ini);
            Assert.Contains(@"EffectSearchPaths=.\user\Shaders\**", ini);
            Assert.Contains(@"TextureSearchPaths=.\user\Textures\**", ini);
            Assert.DoesNotContain("DLSS5FeederFull", ini);
            Assert.Equal(keepEarlyLoad, ini.Contains("deep-fried-chicken.addon64", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(Bin, "dxgi.dll")));
            Assert.True(File.Exists(Path.Combine(Bin, "d3dcompiler_47.dll")));
            Assert.DoesNotContain("d3dcompiler_47", File.ReadAllText(Path.Combine(Game.PrefixPath!, "user.reg")));
        }

        public static byte[] Pe(int length)
        {
            var bytes = new byte[length];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
            BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
            return bytes;
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var item in entries)
                {
                    var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
                    using var output = entry.Open();
                    output.Write(item.Bytes);
                }
            return stream.ToArray();
        }
        private static byte[] Release(string version, string name, string url, byte[] payload)
            => JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v" + version,
                assets = new[] { new { name, browser_download_url = url, digest = "sha256:" + Hash(payload) } } } });

        public void Dispose()
        {
            _http.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixtureHandler(Dictionary<string, byte[]> responses, List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            if (!responses.TryGetValue(url, out var payload))
                throw new InvalidOperationException("Unexpected synthetic HTTP request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
