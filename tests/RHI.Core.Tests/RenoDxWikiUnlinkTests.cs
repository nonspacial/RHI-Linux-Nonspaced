using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

// #141: upstream 2.8.5 still offers RenoDX for a wikiUnlinks title that has a manifest snapshot override
// (Death's Door, Outer Wilds, Haste: Voosh's Unity build); an unlinked title without one gets nothing.
public sealed class RenoDxWikiUnlinkTests : IDisposable
{
    private const string UnityUrl = "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64";
    private const string ManifestUrl = "https://manifest.test/manifest.json";

    // Trimmed from upstream RHI-2.8.5 manifest.json (same keys and values for these titles).
    private const string Manifest = $$"""
        {
          "version": 31,
          "wikiUnlinks": ["ARC Raiders", "Death's Door", "DOOM", "Haste", "Outer Wilds", "Stardew Valley"],
          "snapshotOverrides": {
            "Hogwarts Legacy": "https://marat569.github.io/renodx/renodx-ue-extended.addon64",
            "Death's Door": "{{UnityUrl}}",
            "Haste": "{{UnityUrl}}",
            "Outer Wilds": "{{UnityUrl}}"
          },
          "authorOverrides": { "Death's Door": "Voosh", "Haste": "Voosh", "Outer Wilds": "Voosh" }
        }
        """;

    // RenoDXdb.json entries in the upstream shape. Both unlinked titles have one; neither may be used.
    private const string Database = """
        [
          { "notes": null, "name": "Outer Wilds", "discordUrl": null, "snapshotUrl": "https://clshortfuse.github.io/renodx/renodx-outerwilds.addon64", "status": "Done", "nexusUrl": null, "author": "Someone Else", "snapshotUrl32": null, "discussionUrl": null },
          { "notes": "Use Unity generic", "name": "Stardew Valley", "discordUrl": null, "snapshotUrl": "https://clshortfuse.github.io/renodx/renodx-stardew.addon64", "status": "Done", "nexusUrl": null, "author": "Someone Else", "snapshotUrl32": null, "discussionUrl": null }
        ]
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-unlink-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Handler _handler = new();
    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly LinuxDeploymentRecordStore _store;
    private readonly RenoDxCatalogService _catalog;
    private readonly RenoDxDeploymentService _service;

    public RenoDxWikiUnlinkTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_handler);
        _manifest = new UpstreamRhiManifestService(_http, ManifestUrl);
        _store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        _catalog = new RenoDxCatalogService(_http, _manifest);
        _service = new RenoDxDeploymentService(_http, _store, _catalog, Path.Combine(_root, "cache"), _manifest);
    }

    [Theory]
    [InlineData("Outer Wilds")]
    [InlineData("Death's Door")]
    [InlineData("Haste")]
    public async Task UnlinkedTitleWithSnapshotOverride_InstallsTheOverride(string name)
    {
        var (game, files) = await UnityGameAsync(name);

        var entry = await _catalog.ResolveAsync(game, files);
        var install = await _service.InstallAsync(game, files);

        Assert.True(game.WikiUnlinkedByManifest);
        Assert.Equal(UnityUrl, entry!.SnapshotUrl64);
        Assert.Equal("Voosh", ModAuthorCredit.ForRenoDx(entry, game));
        Assert.True(install.Success, install.Message);
        Assert.Equal(UnityUrl, _store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-unityengine.addon64")));
        Assert.DoesNotContain(_handler.Requested, url => url.Contains("renodx-outerwilds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnlinkedTitleWithoutOverride_StaysWithoutRenoDxEvenWithADatabaseEntry()
    {
        var (game, files) = await UnityGameAsync("Stardew Valley");

        var entry = await _catalog.ResolveAsync(game, files);
        var install = await _service.InstallAsync(game, files);

        Assert.True(game.WikiUnlinkedByManifest);
        Assert.Null(entry);
        Assert.False(install.Success);
        Assert.Contains("wikiUnlinks", install.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon*"));
    }

    [Fact]
    public async Task ComponentRow_OverrideIsInstallableWithItsUrl_NoOverrideIsUnsupported()
    {
        var (withOverride, overrideFiles) = await UnityGameAsync("Outer Wilds");
        var (without, withoutFiles) = await UnityGameAsync("Stardew Valley");
        var managed = new LinuxManagedComponentService(_store, reshade: null!, addonManager: null!, renodx: _service, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!,
            lumaFramework: new LumaFrameworkDeploymentService(_http, _store), lumaPolicy: null!);
        await _service.PrepareCompatibilityAsync(withOverride, overrideFiles);
        await _service.PrepareCompatibilityAsync(without, withoutFiles);

        var installable = Rows();
        var hidden = Rows();
        managed.Decorate(withOverride, installable);
        managed.Decorate(without, hidden);

        var row = installable.Single(c => c.Kind == ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentAvailability.Available, row.Availability);
        Assert.True(row.ActionAllowed);
        Assert.EndsWith(UnityUrl, row.Note, StringComparison.Ordinal);
        Assert.EndsWith(UnityUrl, UiLocalizationService.PresentFor("pt-BR", row.Note), StringComparison.Ordinal);
        Assert.StartsWith("O wikiUnlinks do upstream", UiLocalizationService.PresentFor("pt-BR", row.Note), StringComparison.Ordinal);
        var blocked = hidden.Single(c => c.Kind == ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentAvailability.Unsupported, blocked.Availability);
        Assert.False(blocked.ActionAllowed);
    }

    private static List<ManagedComponent> Rows() =>
    [
        new() { Kind = ManagedComponentKind.ReShade, DisplayName = "ReShade", Availability = ComponentAvailability.Installed },
        new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Available, ActionAllowed = true }
    ];

    // A Unity game run through the real manifest policy, which sets WikiUnlinkedByManifest and the author override.
    private async Task<(DetectedGame Game, string[] Files)> UnityGameAsync(string name)
    {
        var dir = Path.Combine(_root, name.Replace(' ', '-').Replace('\'', '-'));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var unityPlayer = Path.Combine(dir, "UnityPlayer.dll");
        File.WriteAllText(unityPlayer, "MZ unity");
        var reshade = Path.Combine(dir, "ReShade64.dll");
        File.WriteAllText(reshade, "MZ ReShade crosire");
        var detected = new DetectedGame { Name = name, InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Runner = "Proton", Source = "Steam" };
        var game = Assert.Single(await new UpstreamGameLibraryPolicyService(_manifest).ApplyAsync([detected]));
        return (game, [exe, unityPlayer, reshade]);
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requested) Requested.Add(url);
            if (url == ManifestUrl) return Task.FromResult(Json(Manifest));
            if (url == RenoDxCatalogService.StructuredDbUrl) return Task.FromResult(Json(Database));
            if (!url.EndsWith(".addon64", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
