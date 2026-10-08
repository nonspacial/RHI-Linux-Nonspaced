using RHI.Core.Deployment;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed partial class StandaloneDlssEnablerDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-enabler-standalone-tests-" + Guid.NewGuid().ToString("N"));

    public StandaloneDlssEnablerDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpstreamContract_ProxyNamesMatchCurrentContract()
    {
        Assert.Equal(
            new[] { "version.dll", "dxgi.dll", "winmm.dll", "dbghelp.dll", "psapi.dll", "winhttp.dll" },
            StandaloneDlssEnablerDeploymentService.ProxyNames);
        Assert.Equal("DLSS-Enabler-", StandaloneDlssEnablerDeploymentService.TagPrefix);
        Assert.Equal("version.dll", StandaloneDlssEnablerDeploymentService.InnerDllName);
    }

    [Fact]
    public void Eligibility_RequiresX64InitializedPrefixButNotOptiScalerOrSpecificApi()
    {
        using var fixture = CreateGame("eligibility", 0x8664, GraphicsApi.DirectX11);
        Assert.True(StandaloneDlssEnablerDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.GraphicsApi = GraphicsApi.Vulkan;
        Assert.True(StandaloneDlssEnablerDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.ForcedArchitecture = PeArchitecture.X86;
        Assert.False(StandaloneDlssEnablerDeploymentService.IsGameEligible(fixture.Game));
        fixture.Game.ForcedArchitecture = PeArchitecture.X64;
        File.Delete(Path.Combine(fixture.Prefix, "user.reg"));
        Assert.False(StandaloneDlssEnablerDeploymentService.IsGameEligible(fixture.Game));
    }

    [Fact]
    public void PreferenceStore_RoundTripsProxyPerGame()
    {
        using var fixture = CreateGame("prefs", 0x8664, GraphicsApi.DirectX12);
        var store = new StandaloneDlssEnablerPreferenceStore(Path.Combine(fixture.Root, "prefs.json"));
        store.Set(fixture.Game, new StandaloneDlssEnablerPreferences { ProxyName = "winhttp.dll" });
        Assert.Equal("winhttp.dll", store.Get(fixture.Game).ProxyName);
    }

    [Fact]
    public async Task ResolveLatest_SelectsHighestVersionAndPublishedDigest()
    {
        using var fixture = CreateGame("resolve", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.9.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x49)))),
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x4A)))));
        handler.ActiveVersion = "4.10.0";
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("4.10.0", release.Version);
        Assert.Equal("DLSS-Enabler-4.10.0", release.Tag);
        Assert.Equal("DLSS-Enabler-4.10.0.zip", release.AssetName);
        Assert.Equal(handler.DigestFor("4.10.0"), release.AssetSha256);
    }

    [Fact]
    public async Task InstallAndRemove_DeploysProxyHashRecordAndWineOverride()
    {
        using var fixture = CreateGame("lifecycle", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x51)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");
        Assert.True(install.Success, install.Message);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        Assert.True(File.Exists(target));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        var record = records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone);
        Assert.NotNull(record);
        Assert.Equal("4.10.0", record!.Version);
        Assert.Equal("winmm.dll", record.Metadata["DLSSEnabler.ProxyName"]);
        Assert.Equal(Sha256(target), record.Files.Single().Sha256, ignoreCase: true);
        DownloadRecordAssert.Recorded(record, Sha256(target), VerifiedDownload.PublishedDigest);

        var remove = await service.RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }

    [Fact]
    public async Task Install_UnreadablePreferenceFile_RefusesBeforeDownloadOrOverride()
    {
        // Refs #57: the preference save comes last; an unreadable file must stop the install up front.
        using var fixture = CreateGame("unreadable-prefs", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Root, "prefs.json"), "{ not json");
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x53)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("prefs.json", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(fixture.Root, "prefs.json")));
    }

    [Fact]
    public async Task Install_BlocksExternalProxyBeforeNetwork()
    {
        using var fixture = CreateGame("foreign", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Bin, "dxgi.dll"), "foreign");
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x52)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "dxgi.dll");

        Assert.False(result.Success);
        Assert.Contains("não sobrescreve", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_BlocksAnotherRhiManagedProxyBeforeNetwork()
    {
        using var fixture = CreateGame("owned", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "winhttp.dll");
        File.WriteAllText(target, "owned-by-other-rhi-component");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = "deadbeef" }]
        });
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x53)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winhttp.dll");

        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_RejectsX86PayloadAndLeavesPrefixUntouched()
    {
        using var fixture = CreateGame("x86-payload", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x014c, 256 * 1024, 0x54)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("x64 PE", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }

    [Fact]
    public async Task Remove_ModifiedManagedProxyBlocksBeforeOverrideTeardown()
    {
        using var fixture = CreateGame("modified", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x55)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        File.AppendAllText(Path.Combine(fixture.Bin, "version.dll"), "external-change");

        var result = await service.RemoveAsync(fixture.Game);

        Assert.False(result.Success);
        Assert.Contains("modificada fora", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }

    [Fact]
    public async Task Update_ReplacesManagedPayloadKeepsProxyAndOverride()
    {
        using var fixture = CreateGame("update", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.9.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x59)))),
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x5A)))));
        handler.ActiveVersion = "4.9.0";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "psapi.dll")).Success);
        var target = Path.Combine(fixture.Bin, "psapi.dll");
        var before = Sha256(target);

        handler.ActiveVersion = "4.10.0";
        var result = await service.UpdateAsync(fixture.Game);

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(before, Sha256(target));
        var record = records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone)!;
        Assert.Equal("4.10.0", record.Version);
        Assert.Equal("psapi.dll", record.Metadata["DLSSEnabler.ProxyName"]);
        DownloadRecordAssert.Recorded(record, Sha256(target), VerifiedDownload.PublishedDigest);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "psapi", "Game.exe"));
    }

    [Fact]
    public async Task Install_CoexistsWithManagedOptiScalerWhenProxySlotIsFree()
    {
        using var fixture = CreateGame("opti-coexist", 0x8664, GraphicsApi.DirectX12);
        var optiDir = Path.Combine(fixture.Bin, "OptiScaler");
        Directory.CreateDirectory(optiDir);
        var optiFile = Path.Combine(optiDir, "OptiScaler.dll");
        File.WriteAllText(optiFile, "managed-opti");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = optiFile, Sha256 = Sha256(optiFile) }]
        });
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x56)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.OptiScaler));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }

    [Fact]
    public async Task Archive_MultipleVersionDllEntriesFailsClosedBeforeGameMutation()
    {
        using var fixture = CreateGame("ambiguous-archive", 0x8664, GraphicsApi.DirectX12);
        var pe = CreatePe(0x8664, 256 * 1024, 0x57);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("a/version.dll", pe), ("b/version.dll", pe))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("multiple ambiguous", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }

    [Fact]
    public async Task PublishedDigestMismatch_BlocksBeforeGameMutation()
    {
        using var fixture = CreateGame("digest", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(
            ("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x58)))));
        handler.DigestOverride = new string('0', 64);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
    }

    private StandaloneDlssEnablerDeploymentService CreateService(
        GameFixture fixture,
        HttpClient http,
        LinuxDeploymentRecordStore records)
        => new(
            http,
            records,
            new WineDllOverrideService(),
            new StandaloneDlssEnablerPreferenceStore(Path.Combine(fixture.Root, "prefs.json")),
            Path.Combine(fixture.Root, "cache"));

    private GameFixture CreateGame(string name, ushort machine, GraphicsApi api)
    {
        var root = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, CreatePe(machine, 64 * 1024, 0x11));
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return new GameFixture(root, bin, prefix, new DetectedGame
        {
            Name = "DLSS Enabler Test " + name,
            InstallPath = root,
            Source = "Test",
            Runner = "Wine",
            PrefixPath = prefix,
            ExecutablePath = exe,
            BinaryDirectory = bin,
            GraphicsApi = api
        });
    }

    // Final detect-deploy review, nit 5 (Refs #42): the DLSS Enabler payload leaves its downloaded ZIP through the checked
    // extraction, never ExtractToFile: the archive's Unix mode bits are not applied, and an entry declaring more than
    // the 256 MiB add-on cap is refused before anything is written.
    private static readonly int SetUidAndAllPermissions = Convert.ToInt32("104777", 8) << 16;
    private const UnixFileMode Dangerous = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                                           | UnixFileMode.SetUser | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite;

    [Fact]
    public async Task ArchivePayload_ModeBitsAreNotApplied()
    {
        using var fixture = CreateGame("archive-mode", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", DownloadedArchiveEntryTests.Zip("version.dll", CreatePe(0x8664, 256 * 1024, 0x5A), SetUidAndAllPermissions)));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(result.Success, result.Message);
        var written = Directory.EnumerateFiles(Path.Combine(fixture.Root, "cache"), "*.dll", SearchOption.AllDirectories)
            .Append(Path.Combine(fixture.Bin, "winmm.dll")).ToList();
        Assert.All(written, file => Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & Dangerous));
    }

    [Fact]
    public async Task ArchivePayload_DeclaringMoreThanTheCap_IsRefusedBeforeWriting()
    {
        const long cap = 256L * 1024 * 1024;
        using var fixture = CreateGame("archive-cap", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", DownloadedArchiveEntryTests.Zip("version.dll", CreatePe(0x8664, 256 * 1024, 0x5B), 0, declaredLength: cap + 1)));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.False(result.Success);
        Assert.Contains($"version.dll: declared size over the limit ({cap + 1} > {cap} bytes)", result.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "cache"), "*.dll", SearchOption.AllDirectories));
    }

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = Enumerable.Repeat(marker, size).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] CreateZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes, 0, bytes.Length);
            }
        }
        return memory.ToArray();
    }

    private static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed record GameFixture(string Root, string Bin, string Prefix, DetectedGame Game) : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class DlssEnablerHandler : HttpMessageHandler, IDisposable
    {
        private readonly Dictionary<string, byte[]> _assets;
        public string ActiveVersion { get; set; }
        public string? DigestOverride { get; set; }
        public int RequestCount { get; private set; }

        private DlssEnablerHandler(Dictionary<string, byte[]> assets)
        {
            _assets = assets;
            ActiveVersion = assets.Keys.OrderBy(version => Version.Parse(version)).Last();
        }

        public static DlssEnablerHandler WithVersions(params (string Version, byte[] Asset)[] versions)
            => new(versions.ToDictionary(item => item.Version, item => item.Asset, StringComparer.OrdinalIgnoreCase));

        public string DigestFor(string version) => Sha256(_assets[version]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();
            if (url == StandaloneDlssEnablerDeploymentService.ReleasesApiUrl)
                return Task.FromResult(JsonResponse(BuildReleaseList()));

            foreach (var (version, asset) in _assets)
            {
                if (url == AssetUrl(version))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(asset)
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string BuildReleaseList()
        {
            // For update tests, expose only releases up through ActiveVersion. This lets the same handler model time advancing.
            var active = Version.Parse(ActiveVersion);
            var filtered = _assets.Keys
                .Where(version => Version.Parse(version).CompareTo(active) <= 0)
                .OrderBy(version => Version.Parse(version))
                .Select(version =>
                {
                    var digest = version.Equals(ActiveVersion, StringComparison.OrdinalIgnoreCase) && DigestOverride is not null
                        ? DigestOverride
                        : DigestFor(version);
                    return $$"""
                    {
                      "tag_name":"DLSS-Enabler-{{version}}",
                      "draft":false,
                      "prerelease":false,
                      "assets":[{
                        "name":"DLSS-Enabler-{{version}}.zip",
                        "browser_download_url":"{{AssetUrl(version)}}",
                        "digest":"sha256:{{digest}}"
                      }]
                    }
                    """;
                });
            return "[" + string.Join(",", filtered) + "]";
        }

        private static string AssetUrl(string version)
            => $"https://github.com/rhi-tests/DLSS-Enabler-{version}.zip";

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
