using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #272 ANR FG (upstream 2.9.3 AnrFgService): the newest apex-compute/ANR release's add-on, verified against GitHub's
/// digest and the release's SHA256SUMS.txt (both as served on 2026-11-16 for FG-V.B18), installed into OptiScaler's custom
/// folder of a build that loads custom providers, nr_fg.ini kept.
/// </summary>
public sealed class AnrFgProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-anr-" + Guid.NewGuid().ToString("N"));

    public AnrFgProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void TheRealRelease_ListsTheAddonsHash_AsGitHubPublishesIt()
    {
        using var release = JsonDocument.Parse(Fixture("release-apex-compute-anr-latest-2026-11-16.json"));
        var addon = release.RootElement.GetProperty("assets").EnumerateArray().Single(asset => asset.GetProperty("name").GetString()!.EndsWith("-addon.zip"));
        var listed = AnrFgProvider.ListedSha256(Fixture("anr-fg-V.B18-SHA256SUMS.txt"), addon.GetProperty("name").GetString()!);
        Assert.Equal("sha256:" + listed, addon.GetProperty("digest").GetString());
        Assert.Null(AnrFgProvider.ListedSha256(Fixture("anr-fg-V.B18-SHA256SUMS.txt"), "anr-fg-V.B18-addon.zip.sig"));
        // The package holds the provider folder RHI installs, and ANR's own settings.
        var files = Fixture("anr-fg-V.B18-addon.files.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Apex Neural Rendering FG/dlssg/nvngx_dlssg.dll", files);
        Assert.Contains("Apex Neural Rendering FG/dlssg/nr_fg.ini", files);
    }

    [Fact]
    public async Task Latest_TakesTheAddon_VerifiedByBothHashes_AndExtractsTheProvider()
    {
        var zip = AddonZip();
        var (http, _) = Server(zip, digest: Sha(zip), sums: Sha(zip));
        var provider = new AnrFgProvider(http, new ZipExtractor(), Path.Combine(_root, "cache"));

        var package = await provider.GetLatestAsync();

        Assert.Equal("FG-V.B18", package.Tag);
        Assert.True(File.Exists(Path.Combine(package.ProviderDirectory, "dlssg", "nvngx_dlssg.dll")));
        Assert.Equal(AnrFgProvider.ProviderName, Path.GetFileName(package.ProviderDirectory));
        Assert.Equal(Sha(zip), package.Download.Sha256, ignoreCase: true);
    }

    [Theory]
    [InlineData("digest-wrong", "match", AnrFgProvider.ChecksumMismatchPrefix)] // the two published hashes disagree
    [InlineData("digest-wrong", "sums-wrong", "SHA-256")] // both agree, the bytes do not: the download refuses them
    [InlineData("match", "sums-wrong", AnrFgProvider.ChecksumMismatchPrefix)]
    [InlineData("none", "none", AnrFgProvider.NotVerifiedPrefix)]
    public async Task Latest_IsRefused_UnlessAPublishedHashVouchesForIt(string digest, string sums, string expected)
    {
        var zip = AddonZip();
        var wrong = new string('a', 64);
        var (http, _) = Server(zip,
            digest: digest switch { "match" => Sha(zip), "none" => null, _ => wrong },
            sums: sums switch { "match" => Sha(zip), "none" => null, _ => wrong });
        var provider = new AnrFgProvider(http, new ZipExtractor(), Path.Combine(_root, "cache-" + digest + sums));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => provider.GetLatestAsync());

        Assert.Contains(expected, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static byte[] AddonZip(string iniText = "build = shipped\n")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes) { using var entry = zip.CreateEntry(name).Open(); entry.Write(bytes); }
            var dll = new byte[96 * 1024];
            dll[0] = (byte)'M';
            dll[1] = (byte)'Z';
            Add("Apex Neural Rendering FG/dlssg/nvngx_dlssg.dll", dll);
            Add("Apex Neural Rendering FG/dlssg/nr_fg.ini", Encoding.UTF8.GetBytes(iniText));
            Add("Apex Neural Rendering FG/dlssg/kernels/pack.nrx", [1, 2, 3]);
            Add("install.sh", Encoding.UTF8.GetBytes("#!/bin/sh\n"));
        }
        return buffer.ToArray();
    }

    /// <summary>The pinned FG-V.B18 release, its addon asset pointing at <paramref name="zip"/> with the given hashes.</summary>
    internal static (HttpClient Http, Dictionary<string, byte[]> Map) Server(byte[] zip, string? digest, string? sums)
    {
        var release = JsonNode.Parse(Fixture("release-apex-compute-anr-latest-2026-11-16.json"))!;
        string? zipUrl = null, sumsUrl = null;
        foreach (var asset in release["assets"]!.AsArray())
        {
            var name = asset!["name"]!.GetValue<string>();
            if (name.EndsWith("-addon.zip", StringComparison.Ordinal))
            {
                asset["digest"] = digest is null ? null : "sha256:" + digest;
                asset["size"] = zip.LongLength;
                zipUrl = asset["browser_download_url"]!.GetValue<string>();
            }
            else if (name == "SHA256SUMS.txt") sumsUrl = asset["browser_download_url"]!.GetValue<string>();
        }
        if (sums is null) release["assets"]!.AsArray().Remove(release["assets"]!.AsArray().First(asset => asset!["name"]!.GetValue<string>() == "SHA256SUMS.txt"));
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [AnrFgProvider.LatestReleaseApi] = Encoding.UTF8.GetBytes(release.ToJsonString()),
            [zipUrl!] = zip,
        };
        if (sums is not null) map[sumsUrl!] = Encoding.UTF8.GetBytes($"{sums}  anr-fg-V.B18-addon.zip\n{new string('b', 64)}  anr-fg-V.B18-full.zip\n");
        return (new HttpClient(new MapHandler(map)), map);
    }

    private sealed class MapHandler(Dictionary<string, byte[]> map) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(map.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }

    internal sealed class ZipExtractor : IOptiScalerArchiveExtractor
    {
        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            ZipFile.ExtractToDirectory(archivePath, outputDirectory);
            return Task.CompletedTask;
        }
    }
}

public sealed partial class OptiScalerCustomBuildTests
{
    [Fact]
    public async Task AnrFg_GoesIntoTheCustomFolder_OfABuildThatLoadsCustomProviders_KeepingNrFgIni()
    {
        var old = (await Import(WriteBuild(Path.Combine(_root, "anr-old"), os10: true, marker: "old"), "Old build")).Build!;
        var nightly = (await Import(WriteBuild(Path.Combine(_root, "anr-new"), os10: true, marker: "new", dllText: "Custom DLSSG providers"), "New nightly")).Build!;
        var (game, store) = Game("anr");
        var managed = Managed(store, out var optiscaler);
        optiscaler.AnrFg = new FakeAnr(Path.Combine(_root, "anr-package"));

        Assert.True((await managed.InstallOptiScalerAsync(game, old.Variant)).Success);
        var refused = await managed.EnsureAnrFgAsync(game);
        Assert.Equal(OptiScalerDeploymentService.AnrFgUnsupportedPrefix + "Custom: Old build" + OptiScalerDeploymentService.AnrFgUnsupportedHint, refused.Message);

        Assert.True((await managed.SwitchOptiScalerVersionAsync(game, nightly.Variant)).Success);
        var provider = Path.Combine(game.BinaryDirectory!, "OptiScaler", "custom", AnrFgProvider.ProviderName);
        Directory.CreateDirectory(Path.Combine(provider, "dlssg"));
        File.WriteAllText(Path.Combine(provider, "dlssg", "nr_fg.ini"), "build = mine\n"); // the user's own settings

        var installed = await managed.EnsureAnrFgAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.EndsWith(OptiScalerDeploymentService.AnrFgKeptIniNote, installed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(provider, "dlssg", "nvngx_dlssg.dll")));
        Assert.Equal("build = mine\n", File.ReadAllText(Path.Combine(provider, "dlssg", "nr_fg.ini")));
        Assert.Contains(AnrFgProvider.ProviderName, OptiScalerDeploymentService.FindCustomFgProviders(game.BinaryDirectory!));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("FG-V.B18", record.Metadata[OptiScalerDeploymentService.AnrFgVersionMetadataKey]);
        Assert.Contains(record.Files, file => file.Path.EndsWith("nvngx_dlssg.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(record.Files, file => file.Path.EndsWith("nr_fg.ini", StringComparison.Ordinal)); // the user's file stays theirs
        Assert.True((await managed.EnsureAnrFgAsync(game)).Success); // again: RHI's own files are replaced

        // w14 final review m14: RHI's own file the user changed since is never overwritten.
        var dlssg = Path.Combine(provider, "dlssg", "nvngx_dlssg.dll");
        File.WriteAllText(dlssg, "the user's own build");
        var changed = await managed.EnsureAnrFgAsync(game);
        Assert.Equal(OptiScalerDeploymentService.AnrFgChangedFilePrefix + dlssg, changed.Message);
        Assert.Equal("the user's own build", File.ReadAllText(dlssg));
        File.Delete(dlssg);

        // A file RHI did not put there refuses the install, changing nothing.
        File.WriteAllText(Path.Combine(provider, "dlssg", "path_table.json"), "{}");
        File.Delete(Path.Combine(provider, "dlssg", "nvngx_dlssg.dll"));
        ((FakeAnr)optiscaler.AnrFg).Extra = "dlssg/path_table.json";
        var foreign = await managed.EnsureAnrFgAsync(game);
        Assert.StartsWith(OptiScalerDeploymentService.AnrFgForeignFilePrefix, foreign.Message, StringComparison.Ordinal);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(provider, "dlssg", "path_table.json")));
    }

    private sealed class FakeAnr(string root) : IAnrFgProvider
    {
        public string? Extra { get; set; }

        public Task<AnrFgPackage> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            var provider = Path.Combine(root, AnrFgProvider.ProviderName);
            Directory.CreateDirectory(Path.Combine(provider, "dlssg"));
            WritePe(Path.Combine(provider, "dlssg", "nvngx_dlssg.dll"), "anr");
            File.WriteAllText(Path.Combine(provider, "dlssg", "nr_fg.ini"), "build = shipped\n");
            if (Extra is not null) File.WriteAllText(Path.Combine(provider, Extra), "[]");
            return Task.FromResult(new AnrFgPackage("FG-V.B18", provider, new RHI.Core.Deployment.VerifiedDownload("/cache/anr.zip", new string('A', 64), new string('A', 64))));
        }
    }
}

/// <summary>
/// w14 final review m2: ANR FG's files are on OptiScaler's record, so a version switch removes them; the switch puts ANR
/// FG back for the new version, or — when the new version can't load it — sets FG Nvngx Replacement to None and says so.
/// </summary>
public sealed partial class OptiScalerCustomBuildTests
{
    private const string FrameGenIni = "[FrameGen]\nFGNvngxReplacement=None\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchVersion_CarriesAnrFgAcross_OrClearsItsProvider(bool newBuildLoadsProviders)
    {
        var a = (await Import(WriteBuild(Path.Combine(_root, "anr-sa"), os10: true, marker: "A", iniExtra: FrameGenIni, dllText: "Custom DLSSG providers"), "Build A")).Build!;
        var b = (await Import(WriteBuild(Path.Combine(_root, "anr-sb"), os10: true, marker: "B", iniExtra: FrameGenIni, dllText: newBuildLoadsProviders ? "Custom DLSSG providers" : ""), "Build B")).Build!;
        var (game, store) = Game("anr-switch-" + newBuildLoadsProviders);
        var managed = Managed(store, out var optiscaler);
        optiscaler.AnrFg = new FakeAnr(Path.Combine(_root, "anr-switch-package-" + newBuildLoadsProviders));
        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);
        var anrIn = await managed.EnsureAnrFgAsync(game);
        Assert.True(anrIn.Success, anrIn.Message);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        OptiScalerIniEditor.SetValuesInFile(ini, [("FrameGen", "FGNvngxReplacement", AnrFgProvider.ProviderName)]);
        var dll = Path.Combine(game.BinaryDirectory!, "OptiScaler", "custom", AnrFgProvider.ProviderName, "dlssg", "nvngx_dlssg.dll");

        var switched = await managed.SwitchOptiScalerVersionAsync(game, b.Variant);

        Assert.True(switched.Success, switched.Message);
        Assert.Equal(b.Variant, managed.GetInstalledOptiScalerVariant(game));
        if (newBuildLoadsProviders)
        {
            Assert.EndsWith(LinuxManagedComponentService.SwitchAnrFgCarriedNote, switched.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(dll));
            Assert.Contains(store.Find(game, ManagedComponentKind.OptiScaler)!.Files, file => file.Path == dll);
            Assert.Equal(AnrFgProvider.ProviderName, OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGNvngxReplacement"));
        }
        else
        {
            Assert.Contains(LinuxManagedComponentService.SwitchAnrFgDroppedPrefix + OptiScalerDeploymentService.AnrFgUnsupportedPrefix, switched.Message, StringComparison.Ordinal);
            Assert.EndsWith(LinuxManagedComponentService.SwitchAnrFgClearedNote, switched.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(dll));
            Assert.Equal("None", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGNvngxReplacement"));
        }
    }
}
