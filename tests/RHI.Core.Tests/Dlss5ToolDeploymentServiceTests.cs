using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/nr-2 (#317, #276): the DLSS5 Tool Neural Rendering method and its Foveated build (upstream RHI 2.9.4
/// Renodx5AddonService). The release listings of RankFTW/rhi-repo (renodx-dlss5-*) and maximedgoulet/FoveatedDLSS5, and
/// FoveatedDLSS5 v0.4.0's .sha256, are pinned from the real releases (2026-11-16); payloads are synthetic files in that
/// layout.
/// </summary>
public sealed class Dlss5ToolDeploymentServiceTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseStockReleases_ThePinnedListing_NewestFirst_RcByNumber_ShortFuseLeftOut()
    {
        var releases = Dlss5ToolDeploymentService.ParseStockReleases(Fixture("rhi-repo-renodx-dlss5-releases-2026-11-16.json"));

        Assert.Equal(["10.8.0", "8.5.0-rc10", "8.5.0-rc5", "8.5.0-rc4", "7.0.0-rc8", "7.0.0-rc1", "6.5.3", "6.4.1", "5.2.1",
            "4.70", "4.60", "4.55", "4.5", "3.3.4", "3.3.1", "2.5"], releases.Select(r => r.Version));
        var newest = releases[0];
        Assert.Equal("renodx-dlss5-10.8.0", newest.Tag);
        Assert.Equal("renodx-dlss5_10.8.0.zip", newest.AssetName);
        Assert.True(newest.IsArchive);
        Assert.Equal("faba7814fa90b6b21786c98a45732dbe80f01fa36745c46324472e14ac0f440d", newest.AssetSha256);
        Assert.All(releases, r => Assert.Equal(Dlss5ToolBuild.Stock, r.Build));
    }

    [Fact]
    public void ParseFoveatedReleases_ThePinnedListing_PreReleasesCount_EachWithItsSha256File()
    {
        var releases = Dlss5ToolDeploymentService.ParseFoveatedReleases(Fixture("foveated-dlss5-releases-2026-11-16.json"));

        Assert.Equal(["0.4.0", "0.3.0", "0.1.0"], releases.Select(r => r.Version));
        Assert.All(releases, r =>
        {
            Assert.Equal(Dlss5ToolBuild.Foveated, r.Build);
            Assert.Equal("renodx-dlss5.addon64", r.AssetName);
            Assert.EndsWith("/" + r.Tag + "/renodx-dlss5.addon64.sha256", r.HashUrl);
            Assert.False(r.IsArchive);
        });
    }

    [Fact]
    public void ParseFoveatedReleases_AReleaseWithoutItsSha256_IsLeftOut()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "v0.5.0", prerelease = true, draft = false, assets = new[] { new { name = "renodx-dlss5.addon64", browser_download_url = "https://github.com/x/a", digest = "sha256:" + new string('a', 64) } } },
            new { tag_name = "v0.4.9", prerelease = true, draft = true, assets = new[] { new { name = "renodx-dlss5.addon64", browser_download_url = "https://github.com/x/b", digest = (string?)null }, new { name = "renodx-dlss5.addon64.sha256", browser_download_url = "https://github.com/x/b.sha256", digest = (string?)null } } },
        });

        Assert.Empty(Dlss5ToolDeploymentService.ParseFoveatedReleases(json));
    }

    [Fact]
    public void ParseSha256File_ThePinnedFoveatedFile_GivesItsHash()
    {
        // "hash  renodx-dlss5.addon64" with a Windows line end, as published with v0.4.0.
        Assert.Equal("49e69a120a573200dd76fbe087510d01f51b94f95ffbb907608a0b242d7f7d75",
            Dlss5ToolDeploymentService.ParseSha256File(Fixture("foveated-dlss5-v0.4.0-renodx-dlss5.addon64.sha256")));
        Assert.Null(Dlss5ToolDeploymentService.ParseSha256File("not a hash  renodx-dlss5.addon64"));
    }

    [Fact]
    public async Task Install_Stock_PlacesTheAddonAndTheNrDll_ThenRemovalPutsTheGameBack()
    {
        using var f = new Dlss5ToolFixture();
        var before = f.Snapshot();

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), hasNativeDlss: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(f.Stock("10.8.0"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        Assert.True(File.Exists(Path.Combine(f.Bin, "nvngx_dlssnr.dll")));
        Assert.Contains(Dlss5ToolDeploymentService.NrDllPlacedNote.Trim(), result.Message);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool)!;
        Assert.Equal("10.8.0", record.Version);
        Assert.Equal("stock", record.Metadata["Dlss5Tool.Build"]);
        Assert.Equal("1.0", record.Metadata[NeuralRenderingDllSwap.MetaDllVersion]);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
        // The NR DLL it placed makes it the game's one nvngx_dlssnr.dll owner (#157), so the NR DLL list swaps it.
        Assert.Equal(Dlss5ToolDeploymentService.DisplayName, NeuralRenderingRuntimeOwnership.OwnerOtherThan(f.Records, f.Game, ManagedComponentKind.NrCostScaler));

        var remove = await f.Service.RemoveAsync(f.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Null(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool));
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public async Task Install_KeepsTheGamesOwnNrDll_UntouchedAndNotOwned()
    {
        using var f = new Dlss5ToolFixture();
        var gameDll = Path.Combine(f.Bin, "nvngx_dlssnr.dll");
        var bytes = Dlss5ToolFixture.Pe(150 * 1024, tag: "game's own");
        File.WriteAllBytes(gameDll, bytes);

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), hasNativeDlss: true);

        Assert.True(result.Success, result.Message);
        Assert.Contains(Dlss5ToolDeploymentService.GameNrDllKeptNote.Trim(), result.Message);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool)!;
        Assert.DoesNotContain(record.Files, file => file.Path == gameDll);
        Assert.Null(NeuralRenderingRuntimeOwnership.OwnerOtherThan(f.Records, f.Game, ManagedComponentKind.NrCostScaler));
        Assert.DoesNotContain(f.Requests, url => url.Contains("nr.zip", StringComparison.Ordinal));
        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);
        Assert.Equal(bytes, File.ReadAllBytes(gameDll));
    }

    [Theory]
    [InlineData(false, true)]  // no DLSS in the game
    [InlineData(true, false)]  // 32-bit
    public async Task Install_WhereUpstreamOffersNoDlss5Tool_IsRefusedBeforeAnyDownload(bool hasDlss, bool x64)
    {
        using var f = new Dlss5ToolFixture(x86: !x64);

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), hasDlss);

        Assert.False(result.Success);
        Assert.Equal(hasDlss ? Dlss5ToolDeploymentService.ArchitectureMessage : Dlss5ToolDeploymentService.NeedsDlssMessage, result.Message);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("renodx-dlss5.addon64")]
    [InlineData("renodx-dlss.addon64")]
    public async Task Install_ARenoDxDlssAddonRhiDidNotPlace_IsRefused_NeverOverwrittenOrDeleted(string name)
    {
        using var f = new Dlss5ToolFixture();
        var path = Path.Combine(f.Bin, name);
        File.WriteAllBytes(path, Dlss5ToolFixture.Pe(64 * 1024, tag: "user's"));
        var bytes = File.ReadAllBytes(path);

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), hasNativeDlss: true);

        Assert.False(result.Success);
        Assert.StartsWith(Dlss5ToolDeploymentService.ExternalPrefix, result.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task Install_Foveated_TakesTheFoveatedRelease_CheckedAgainstItsSha256()
    {
        using var f = new Dlss5ToolFixture();

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Foveated, null, null), hasNativeDlss: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(f.Foveated("0.4.0"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        Assert.Contains(f.Requests, url => url.EndsWith("/v0.4.0/renodx-dlss5.addon64.sha256", StringComparison.Ordinal));
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool)!;
        Assert.Equal("foveated", record.Metadata["Dlss5Tool.Build"]);
        Assert.Equal("0.4.0", record.Version);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
    }

    [Fact]
    public async Task Install_Foveated_AnAddonThatDoesNotMatchItsSha256_IsRefused_AndNothingChanges()
    {
        using var f = new Dlss5ToolFixture(tamperFoveatedHash: true);
        var before = f.Snapshot();

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Foveated, null, null), hasNativeDlss: true);

        Assert.False(result.Success);
        Assert.StartsWith(Dlss5ToolDeploymentService.DownloadFailedPrefix, result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Null(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool));
    }

    [Fact]
    public async Task Install_Foveated_WithoutItsSha256File_IsRefused_FailClosed()
    {
        using var f = new Dlss5ToolFixture();
        f.Remove("https://github.com/maximedgoulet/FoveatedDLSS5/releases/download/v0.4.0/renodx-dlss5.addon64.sha256");

        var result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Foveated, null, null), hasNativeDlss: true);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        Assert.DoesNotContain(f.Requests, url => url.EndsWith("/v0.4.0/renodx-dlss5.addon64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Foveated_StockReleasesNeverReplaceIt_ItsOwnLatestDoes()
    {
        // Upstream: "Latest keeps it current, and stock updates never replace it". A Foveated install on 0.3.0, a stock
        // 10.8.0 out: the check compares with the Foveated releases only, and Update moves it to Foveated 0.4.0.
        using var f = new Dlss5ToolFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Foveated, "0.3.0", null), true)).Success);

        var check = await f.Service.CheckUpdateAsync(f.Game, pin: null);
        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal("0.4.0", check.LatestVersion);

        var update = await f.Service.UpdateAsync(f.Game, Dlss5ToolBuild.Foveated, pin: null);

        Assert.True(update.Success, update.Message);
        Assert.Equal(f.Foveated("0.4.0"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        var after = await f.Service.CheckUpdateAsync(f.Game, pin: null);
        Assert.False(after.UpdateAvailable, after.Message);
        Assert.Equal("foveated", f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool)!.Metadata["Dlss5Tool.Build"]);
    }

    [Fact]
    public async Task SwitchingBuild_WhileInstalled_SwapsTheAddonInPlace_AndTheNrDllStays()
    {
        using var f = new Dlss5ToolFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), true)).Success);
        var dll = File.ReadAllBytes(Path.Combine(f.Bin, "nvngx_dlssnr.dll"));

        var toFoveated = await f.Service.UpdateAsync(f.Game, Dlss5ToolBuild.Foveated, pin: null);

        Assert.True(toFoveated.Success, toFoveated.Message);
        Assert.StartsWith(Dlss5ToolDeploymentService.SwappedPrefix, toFoveated.Message);
        Assert.Equal(f.Foveated("0.4.0"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(f.Bin, "nvngx_dlssnr.dll")));
        Assert.Equal(Dlss5ToolBuild.Foveated, f.Service.InstalledBuild(f.Game));

        var back = await f.Service.UpdateAsync(f.Game, Dlss5ToolBuild.Stock, pin: null);

        Assert.True(back.Success, back.Message);
        Assert.Equal(f.Stock("10.8.0"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        Assert.Equal(Dlss5ToolBuild.Stock, f.Service.InstalledBuild(f.Game));
        Assert.True((await f.Service.RemoveAsync(f.Game)).Success); // the record still owns what is on disk
    }

    [Fact]
    public async Task PickedVersion_SwapsTheAddonInPlace_AndStaysPinned()
    {
        using var f = new Dlss5ToolFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), true)).Success);

        var swap = await f.Service.UpdateAsync(f.Game, Dlss5ToolBuild.Stock, pin: "8.5.0-rc10");

        Assert.True(swap.Success, swap.Message);
        Assert.Equal(f.Stock("8.5.0-rc10"), File.ReadAllBytes(Path.Combine(f.Bin, "renodx-dlss5.addon64")));
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Tool)!;
        Assert.Equal("8.5.0-rc10", record.Metadata["GitHub.ReleaseTagPin"]);
        Assert.False((await f.Service.CheckUpdateAsync(f.Game, "8.5.0-rc10")).UpdateAvailable);
    }

    [Fact]
    public async Task Swap_AnAddonChangedOutsideRhi_IsRefused_AndKept()
    {
        using var f = new Dlss5ToolFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), true)).Success);
        var addon = Path.Combine(f.Bin, "renodx-dlss5.addon64");
        File.WriteAllBytes(addon, Dlss5ToolFixture.Pe(80 * 1024, tag: "hand-edited"));
        var bytes = File.ReadAllBytes(addon);

        var swap = await f.Service.UpdateAsync(f.Game, Dlss5ToolBuild.Foveated, pin: null);
        var remove = await f.Service.RemoveAsync(f.Game);

        Assert.False(swap.Success);
        Assert.False(remove.Success);
        Assert.StartsWith(Dlss5ToolDeploymentService.ModifiedPrefix, remove.Message);
        Assert.Equal(bytes, File.ReadAllBytes(addon));
    }

    [Fact]
    public async Task Install_WhenTheRecordCannotBeSaved_RollsEverythingBack()
    {
        using var f = new Dlss5ToolFixture();
        var before = f.Snapshot();
        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(f.Records, f.RecordsPath))
            result = await f.Service.InstallAsync(f.Game, new Dlss5ToolOptions(Dlss5ToolBuild.Stock, null, null), true);

        Assert.False(result.Success);
        Assert.Equal(before, f.Snapshot());
    }

    internal sealed class Dlss5ToolFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss5tool-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _stock = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _foveated = new(StringComparer.Ordinal);
        public string Bin { get; }
        public string RecordsPath { get; }
        public DetectedGame Game { get; }
        public LinuxDeploymentRecordStore Records { get; }
        public Dlss5ToolDeploymentService Service { get; }
        public List<string> Requests { get; } = [];

        public Dlss5ToolFixture(bool x86 = false, bool tamperFoveatedHash = false)
        {
            Bin = Path.Combine(_root, "game");
            Directory.CreateDirectory(Bin);
            var exe = Path.Combine(Bin, "Game.exe");
            File.WriteAllBytes(exe, Pe(128 * 1024, x86 ? (ushort)0x14c : (ushort)0x8664));
            var reshade = Pe(2 * 1024 * 1024);
            Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 4096);
            File.WriteAllBytes(Path.Combine(Bin, "dxgi.dll"), reshade);
            File.WriteAllText(Path.Combine(Bin, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");
            File.WriteAllBytes(Path.Combine(Bin, "nvngx_dlss.dll"), Pe(96 * 1024, tag: "game DLSS"));
            Game = new DetectedGame { Name = "Synthetic DLSS5 Tool game", InstallPath = Bin, BinaryDirectory = Bin, ExecutablePath = exe,
                PrefixPath = Path.Combine(_root, "prefix"), Source = "Test", Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
            Directory.CreateDirectory(Game.PrefixPath);
            RecordsPath = Path.Combine(_root, "deployments.json");
            Records = new LinuxDeploymentRecordStore(RecordsPath);

            // Stock: the pinned listing, each zip holding one renodx-dlss5.addon64 (its GitHub digest recomputed).
            using var listing = JsonDocument.Parse(Fixture("rhi-repo-renodx-dlss5-releases-2026-11-16.json"));
            var stockReleases = new List<object>();
            foreach (var release in listing.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString()!;
                var asset = release.GetProperty("assets")[0];
                var url = asset.GetProperty("browser_download_url").GetString()!;
                if (tag.StartsWith("renodx-dlss5-", StringComparison.Ordinal))
                {
                    var addon = Pe(64 * 1024, tag: tag);
                    _stock[tag["renodx-dlss5-".Length..]] = addon;
                    var zip = Zip(("renodx-dlss5.addon64", addon));
                    _responses[url] = zip;
                    stockReleases.Add(new { tag_name = tag, draft = false, prerelease = false, assets = new[]
                        { new { name = asset.GetProperty("name").GetString(), browser_download_url = url, digest = "sha256:" + Hash(zip) } } });
                }
                else
                {
                    stockReleases.Add(new { tag_name = tag, draft = false, prerelease = false, assets = new[]
                        { new { name = asset.GetProperty("name").GetString(), browser_download_url = url, digest = (string?)null } } });
                }
            }
            _responses[Dlss5ToolDeploymentService.StockReleasesApiUrl] = JsonSerializer.SerializeToUtf8Bytes(stockReleases);

            // Foveated: the pinned listing, each add-on with its .sha256 ("hash  name", CRLF as published).
            using var foveated = JsonDocument.Parse(Fixture("foveated-dlss5-releases-2026-11-16.json"));
            var foveatedReleases = new List<object>();
            foreach (var release in foveated.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString()!;
                var addon = Pe(72 * 1024, tag: "foveated " + tag);
                _foveated[tag.TrimStart('v')] = addon;
                var addonUrl = $"https://github.com/maximedgoulet/FoveatedDLSS5/releases/download/{tag}/renodx-dlss5.addon64";
                _responses[addonUrl] = addon;
                _responses[addonUrl + ".sha256"] = Encoding.ASCII.GetBytes((tamperFoveatedHash ? new string('0', 64) : Hash(addon)) + "  renodx-dlss5.addon64\r\n");
                foveatedReleases.Add(new { tag_name = tag, draft = false, prerelease = true, assets = new object[]
                {
                    new { name = "renodx-dlss5.addon64", browser_download_url = addonUrl, digest = (string?)null },
                    new { name = "renodx-dlss5.addon64.sha256", browser_download_url = addonUrl + ".sha256", digest = (string?)null }
                } });
            }
            _responses[Dlss5ToolDeploymentService.FoveatedReleasesApiUrl] = JsonSerializer.SerializeToUtf8Bytes(foveatedReleases);

            _responses[DlssManagerService.ManifestUrl] = Encoding.UTF8.GetBytes("{\"dlss\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/sr.zip\"}],\"dlssnr\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/nr.zip\"}]}");
            _responses["https://github.com/fixtures/nr.zip"] = Zip(("nvngx_dlssnr.dll", Pe(128 * 1024, tag: "NR 1.0")));

            _http = new HttpClient(new Handler(_responses, Requests));
            var dlss = new DlssManagerService(_http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));
            Service = new Dlss5ToolDeploymentService(_http, Records, dlss, Path.Combine(_root, "cache-dlss5tool"));
        }

        public byte[] Stock(string version) => _stock[version];
        public byte[] Foveated(string version) => _foveated[version];
        public void Remove(string url) => _responses.Remove(url);

        /// <summary>Every file under the game folder, by relative path, with its bytes' hash.</summary>
        public IReadOnlyDictionary<string, string> Snapshot()
            => Directory.EnumerateFiles(Bin, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(Bin, p), p => Hash(File.ReadAllBytes(p)), StringComparer.Ordinal)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);

        public static byte[] Pe(int length, ushort machine = 0x8664, string? tag = null)
            => GitcUpliftDeploymentServiceTests.UpliftFixture.Pe(length, machine, tag);

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

        public void Dispose()
        {
            _http.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private sealed class Handler(Dictionary<string, byte[]> responses, List<string> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                return Task.FromResult(responses.TryGetValue(url, out var payload)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
