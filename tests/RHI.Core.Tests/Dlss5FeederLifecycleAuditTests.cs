using RHI.Core.Deployment;
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

    // w2/vmsplit review finding 1: the Feeder install honours Cancel only until its first change; a failure after that
    // rolls everything back with its own token, the DX9 translation migration included (it used to be skipped when the
    // operation's token was cancelled).
    [Fact]
    public async Task FullStack_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var f = new FeederFixture();

        var run = await CancelAtWritePhase.RunAsync(ct => f.Install(Dlss5FeederConsumer.RenoDxDlss5, ct), [f.Bin, f.Game.PrefixPath!]);

        Assert.True(run.Result.Success, run.Result.Message);
        f.AssertInstalled(Dlss5FeederConsumer.RenoDxDlss5, expectedEarlyLoad: "0");
    }

    // w2 merge review, minor 1 (vmsplit re-review nit 5): on a 32-bit game the host64 helper's 64-bit ReShade (a
    // reshade.me download) is staged with the other downloads, before the write gate; nothing is fetched after it.
    [Fact]
    public async Task FullStack32Bit_DownloadsTheHost64ReShadeBeforeTheWriteGate()
    {
        using var f = new FeederFixture(x86: true);

        var run = await CancelAtWritePhase.RunAsync(ct => f.Install(Dlss5FeederConsumer.RenoDxDlss5, ct), [f.Bin, f.Game.PrefixPath!],
            atWritePhase: f.Watch.CloseGate);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.True(File.Exists(Path.Combine(f.Bin, "host64", "dxgi.dll")));
        Assert.Contains(f.Requests, url => url.StartsWith("https://reshade.me", StringComparison.Ordinal));
        Assert.Empty(f.Watch.LateRequests);
    }

    [Fact]
    public async Task FullStackDx9_CancelAfterTheFirstWrite_ThenAFailure_RestoresTheDx9Migration()
    {
        using var f = new FeederFixture(missingIncludes: true, dx9: true);

        var run = await CancelAtWritePhase.RunAsync(ct => f.Install(Dlss5FeederConsumer.RenoDxDlss5, ct), [f.Bin, f.Game.PrefixPath!]);

        Assert.False(run.Result.Success);
        Assert.Contains("LumeniteFX include directory is missing", run.Result.Message);
        CancelAtWritePhase.AssertUnchanged(run.Before, f.Bin, f.Game.PrefixPath!);
        Assert.Null(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
        var reshade = f.Records.Find(f.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(reshade.Files).Path);
    }

    [Fact]
    public async Task FullStack_Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        using var f = new FeederFixture();
        Assert.True((await f.Install(Dlss5FeederConsumer.RenoDxDlss5)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => f.Service.RemoveAsync(f.Game, ct), [f.Bin, f.Game.PrefixPath!]);

        Assert.True(run.Result.Success, run.Result.Message);
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
        private readonly string _feederHash, _lumeniteHash, _headersHash;
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
        /// <summary>Records requests sent after the write gate (close it from CancelAtWritePhase's atWritePhase).</summary>
        public GateWatchingHandler Watch { get; }

        /// <param name="x86">A 32-bit game: the Feeder deploys its host64 helper with a 64-bit ReShade from reshade.me.</param>
        public FeederFixture(bool preexistingEarlyLoad = false, bool missingIncludes = false, bool dx9 = false, bool x86 = false)
        {
            Bin = Path.Combine(_root, "game");
            var prefix = Path.Combine(_root, "prefix");
            Directory.CreateDirectory(Bin);
            Directory.CreateDirectory(prefix);
            var exe = Path.Combine(Bin, "Game.exe");
            File.WriteAllBytes(exe, x86 ? Pe(128 * 1024, 0x14c) : Pe(128 * 1024));
            var reshade = Pe(2 * 1024 * 1024);
            Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 4096);
            File.WriteAllBytes(Path.Combine(Bin, dx9 ? "d3d9.dll" : "dxgi.dll"), reshade);
            var compiler = x86 ? Pe(1_100_000, 0x14c) : Pe(1_100_000);
            Encoding.ASCII.GetBytes("Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47.dll").CopyTo(compiler, 4096);
            File.WriteAllBytes(Path.Combine(Bin, "d3dcompiler_47.dll"), compiler);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
            File.WriteAllText(IniPath, "[GENERAL]\nPresetPath=" + UserPreset + "\nEffectSearchPaths=.\\user\\Shaders\\**\nTextureSearchPaths=.\\user\\Textures\\**\n[ADDON]\n" +
                (preexistingEarlyLoad ? "LoadFromDllMain=deep-fried-chicken.addon64\n" : ""));
            Game = new DetectedGame { Name = "Synthetic Feeder audit", InstallPath = Bin, BinaryDirectory = Bin,
                ExecutablePath = exe, PrefixPath = prefix, Source = "Audit", Runner = "Wine", GraphicsApi = dx9 ? GraphicsApi.DirectX9 : x86 ? GraphicsApi.DirectX11 : GraphicsApi.DirectX12 };
            RecordsPath = Path.Combine(_root, "deployments.json");
            Records = new LinuxDeploymentRecordStore(RecordsPath);
            if (dx9)
            {
                // RHI-managed ReShade as d3d9.dll, which the DX9 translation moves to dxgi.dll while dgVoodoo2 owns d3d9.dll.
                var d3d9 = Path.Combine(Bin, "d3d9.dll");
                Records.Upsert(new LinuxDeploymentRecord { GameKey = LinuxDeploymentRecordStore.GameKey(Game), GameName = Game.Name,
                    Component = ManagedComponentKind.ReShade, BinaryDirectory = Bin, Version = "6.5.1",
                    WineOverride = new WineDllOverrideService().EnsureNativeBuiltin(prefix, "d3d9", "Game.exe"),
                    Files = [new LinuxDeployedFile { Path = d3d9, Sha256 = Hash(reshade), ExistedBeforeInstall = false }] });
            }

            var addon = Pe(70 * 1024);
            var bridge = Pe(4 * 1024);
            var sums = $"Deep Fried Chicken 1.6.8\n{Hash(addon)}  deep-fried-chicken.addon64\n{Hash(bridge)}  deep-fried-chicken-nvngx.dll\n";
            _package = Path.Combine(_root, "Deep-Fried-Chicken-v1.6.8.zip");
            File.WriteAllBytes(_package, Zip(("deep-fried-chicken.addon64", addon), ("deep-fried-chicken-nvngx.dll", bridge),
                ("deep-fried-chicken.cfg", Encoding.UTF8.GetBytes("arm=1\nenabled=1\n")), ("SHA256SUMS.txt", Encoding.UTF8.GetBytes(sums))));
            _dfcHash = Hash(File.ReadAllBytes(_package));
            _renoAddon = Pe(70 * 1024);
            _renoHash = Hash(_renoAddon);
            var feederArchive = x86
                ? Zip(("addon32/dlss5-feed.addon32", Pe(70 * 1024, 0x14c)), ("host64/dlss5-feed-host64.exe", Pe(70 * 1024)),
                    ("shaders/DLSS5_Feed.fx", Encoding.UTF8.GetBytes(new string('f', 1024))))
                : Zip(("addon64/dlss5-feed.addon64", Pe(70 * 1024)), ("shaders/DLSS5_Feed.fx", Encoding.UTF8.GetBytes(new string('f', 1024))));
            _feederHash = Hash(feederArchive);
            var lumeniteFiles = new List<(string, byte[])> { ("LumeniteFX/Shaders/lumenite_Kernel.fx", Encoding.UTF8.GetBytes(new string('k', 1024))),
                ("LumeniteFX/Textures/lumenite_test.png", new byte[2048]) };
            if (!missingIncludes) lumeniteFiles.Add(("LumeniteFX/Shaders/include/kernel.fxh", Encoding.UTF8.GetBytes("// include\n")));
            var lumenite = Zip(lumeniteFiles.ToArray());
            var headers = Zip(("Shaders/ReShade.fxh", new byte[1024]), ("Shaders/ReShadeUI.fxh", new byte[1024]), ("Shaders/DrawText.fxh", new byte[1024]));
            _lumeniteHash = Hash(lumenite);
            _headersHash = Hash(headers);
            var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
                ["https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1"] = Release("0.13.1-beta.1", "DLSS5-Feeder-0.13.1-beta.1.zip", "https://github.com/rhi-tests/feeder.zip", feederArchive),
                ["https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100"] = Release("1.2.3", "renodx-dlss5.addon64", "https://github.com/rhi-tests/renodx.addon64", _renoAddon),
                ["https://github.com/rhi-tests/feeder.zip"] = feederArchive,
                ["https://github.com/rhi-tests/renodx.addon64"] = _renoAddon,
                [DlssManagerService.ManifestUrl] = Encoding.UTF8.GetBytes("{\"dlss\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/sr.zip\"}],\"dlssnr\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/nr.zip\"}]}"),
                ["https://github.com/fixtures/sr.zip"] = Zip(("nvngx_dlss.dll", Pe(128 * 1024))),
                ["https://github.com/fixtures/nr.zip"] = Zip(("nvngx_dlssnr.dll", Pe(128 * 1024))),
                ["https://codeload.github.com/umar-afzaal/LumeniteFX/zip/refs/heads/mainline"] = lumenite,
                ["https://github.com/crosire/reshade-shaders/archive/refs/heads/slim.zip"] = headers
            };
            var dgVoodooZip = Zip(("MS/x64/D3D9.dll", Pe(128 * 1024)), ("MS/x86/D3D9.dll", Pe(128 * 1024)));
            const string dgVoodooUrl = "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.86/dgVoodoo2_86.zip";
            responses["https://api.github.com/repos/dege-diosg/dgVoodoo2/releases/latest"]
                = Release("2.86", "dgVoodoo2_86.zip", dgVoodooUrl, dgVoodooZip);
            responses[dgVoodooUrl] = dgVoodooZip;
            // The host64 helper's 64-bit ReShade (32-bit games): reshade.me's page and setup, extracted by a stand-in.
            responses[ReShadeDeploymentService.HomeUrl + "/"] = Encoding.UTF8.GetBytes("<a href=\"/downloads/ReShade_Setup_9.9.9_Addon.exe\">Download</a>");
            responses[ReShadeDeploymentService.HomeUrl] = responses[ReShadeDeploymentService.HomeUrl + "/"];
            responses["https://reshade.me/downloads/ReShade_Setup_9.9.9_Addon.exe"] = Pe(600_000);
            Watch = new GateWatchingHandler(new FixtureHandler(responses, Requests));
            _http = new HttpClient(Watch);
            var dlss = new DlssManagerService(_http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));
            var manifest = new UpstreamRhiManifestService(_http, "https://github.com/rhi-tests/unused-manifest");
            var legacy = new UpstreamRhiAddonDeploymentService(_http, Records, manifest, new UpstreamRhiAddonDeploymentOptions(
                ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://github.com/rhi-tests/unused-release", 64 * 1024,
                DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "cache-legacy"));
            var overrides = new WineDllOverrideService();
            var reShade = new ReShadeDeploymentService(_http, new TransactionalFileDeploymentService(), Records, overrides, new StandInExtractor(),
                Path.Combine(_root, "cache-reshade"), new ReShadeShaderPackService(_http, Path.Combine(_root, "cache-packs"), customRoot: Path.Combine(_root, "custom")));
            Service = new Dlss5FeederFullDeploymentService(_http, Records, dlss, legacy, Path.Combine(_root, "cache-feeder"), overrides,
                reShade: reShade,
                dgVoodoo: new DgVoodooDeploymentService(_http, Path.Combine(_root, "cache-dgvoodoo")),
                dx9ReShade: new Dx9TranslationReShadeCoordinator(Records, overrides));
        }

        public Task<ComponentOperationResult> Install(Dlss5FeederConsumer consumer, CancellationToken cancellationToken = default)
            => Service.InstallAsync(Game, consumer, consumer == Dlss5FeederConsumer.DeepFriedChicken ? _package : null, null, cancellationToken);

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
            // Every download the record deploys, named the same way as in every other service (review w2-trust minor 6).
            DownloadRecordAssert.Recorded(record, _feederHash, VerifiedDownload.PublishedDigest);
            DownloadRecordAssert.Recorded(record, _lumeniteHash, VerifiedDownload.RecordedOnly, Dlss5FeederFullDeploymentService.LumeniteDownloadPart);
            DownloadRecordAssert.Recorded(record, _headersHash, VerifiedDownload.RecordedOnly, Dlss5FeederFullDeploymentService.HeadersDownloadPart);
            if (dfc) DownloadRecordAssert.NotRecorded(record, Dlss5FeederFullDeploymentService.ConsumerDownloadPart); // the user's own package
            else DownloadRecordAssert.Recorded(record, _renoHash, VerifiedDownload.PublishedDigest, Dlss5FeederFullDeploymentService.ConsumerDownloadPart);
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

        public static byte[] Pe(int length, ushort machine = 0x8664)
        {
            var bytes = new byte[length];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
            BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
            return bytes;
        }

        /// <summary>Extracts a synthetic ReShade64/ReShade32 pair from any setup.</summary>
        private sealed class StandInExtractor : IReShadeArchiveExtractor
        {
            public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
            {
                Directory.CreateDirectory(outputDirectory);
                foreach (var (name, machine) in new[] { ("ReShade64.dll", (ushort)0x8664), ("ReShade32.dll", (ushort)0x14c) })
                {
                    var dll = Pe(1_100_000, machine);
                    Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(dll, 512);
                    File.WriteAllBytes(Path.Combine(outputDirectory, name), dll);
                }
                return Task.CompletedTask;
            }
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
