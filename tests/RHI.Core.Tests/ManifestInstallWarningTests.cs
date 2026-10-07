using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class ManifestInstallWarningTests
{
    private const string Discord = "https://discord.com/channels/1408098019194310818/1410855640112566375/threads/1553784428222877736/1553784428222877736";

    [Theory]
    [InlineData(ManagedComponentKind.ReShade, "reshade")]
    [InlineData(ManagedComponentKind.RenoDX, "renodx")]
    [InlineData(ManagedComponentKind.LumaFramework, "luma")]
    [InlineData(ManagedComponentKind.OptiScaler, "optiscaler")]
    [InlineData(ManagedComponentKind.REFramework, "reframework")]
    [InlineData(ManagedComponentKind.ReLimiter, "relimiter")]
    [InlineData(ManagedComponentKind.DisplayCommander, "dc")]
    [InlineData(ManagedComponentKind.RenoDXUpgrade, null)]
    [InlineData(ManagedComponentKind.Dlss, null)]
    [InlineData(ManagedComponentKind.UltimateAsiLoader, null)]
    public void ManifestKey_MapsUpstreamComponentKeys(ManagedComponentKind kind, string? key)
        => Assert.Equal(key, ManifestInstallWarning.ManifestKey(kind));

    [Fact]
    public async Task Find_UsesCanonicalManifestNameAndComponent()
    {
        // Shapes and texts as in upstream manifest 2.8.0 (93c19d5); detected titles differ in punctuation/™.
        var json = $$"""
        {
          "version": 4,
          "installWarnings": {
            "The Witcher 3: Wild Hunt — Remastered": { "renodx": "⚠ The RenoDX mod available here is for the original version.\n\nFor the Remastered Edition mod, join the Discord:\n{{Discord}}" },
            "Batman™: Arkham Knight": { "luma": "⚠ Experimental Luma mod." }
          },
          "wikiNameOverrides": { "The Witcher 3: Wild Hunt": "The Witcher 3" }
        }
        """;
        using var http = new HttpClient(new JsonHandler(json));
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        var games = await policy.ApplyAsync(
        [
            Game("The Witcher 3 Wild Hunt Remastered"),
            Game("The Witcher 3: Wild Hunt"),
            Game("Batman: Arkham Knight")
        ]);
        var remastered = games.Single(g => g.Name == "The Witcher 3 Wild Hunt Remastered");
        var original = games.Single(g => g.Name == "The Witcher 3: Wild Hunt");
        var batman = games.Single(g => g.Name == "Batman: Arkham Knight");

        var warning = ManifestInstallWarning.Find(remastered, ManagedComponentKind.RenoDX);
        Assert.NotNull(warning);
        Assert.EndsWith("join the Discord:", warning.Text);
        Assert.Equal(Discord, Assert.Single(warning.Links).AbsoluteUri);
        Assert.Null(ManifestInstallWarning.Find(remastered, ManagedComponentKind.ReShade));
        Assert.Null(ManifestInstallWarning.Find(original, ManagedComponentKind.RenoDX));
        Assert.Equal("⚠ Experimental Luma mod.", ManifestInstallWarning.Find(batman, ManagedComponentKind.LumaFramework)?.Text);
        Assert.Null(ManifestInstallWarning.Find(batman, ManagedComponentKind.RenoDX));
    }

    [Fact]
    public void Find_IgnoresBlankWarnings()
    {
        var game = Game("Blank");
        game.ManifestInstallWarnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["luma"] = "  " };
        Assert.Null(ManifestInstallWarning.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public void Parse_KeepsInlineLinksInTextAndListsEveryHttpLink()
    {
        var warning = ManifestInstallWarning.Parse(
            "Fix: see https://www.gta5-mods.com/scripts/directstoragefix.\r\n\r\nAlso (http://mirror.test/a) or ftp://files.test/x or javascript:alert(1)\rhttps://www.gta5-mods.com/scripts/directstoragefix");

        Assert.Equal(
            "Fix: see https://www.gta5-mods.com/scripts/directstoragefix.\n\nAlso (http://mirror.test/a) or ftp://files.test/x or javascript:alert(1)",
            warning.Text);
        Assert.Equal(
            ["https://www.gta5-mods.com/scripts/directstoragefix", "http://mirror.test/a"],
            warning.Links.Select(link => link.AbsoluteUri));
    }

    [Fact]
    public void Parse_NoLinkLeavesTextIntact()
    {
        var warning = ManifestInstallWarning.Parse("REANIMAL has native HDR.\rThere is no need to add RenoDX.  ");
        Assert.Equal("REANIMAL has native HDR.\nThere is no need to add RenoDX.", warning.Text);
        Assert.Empty(warning.Links);
    }

    private static DetectedGame Game(string name)
        => new() { Name = name, InstallPath = Path.Combine(Path.GetTempPath(), "rhi-warning-" + Guid.NewGuid().ToString("N")), Source = "Test", Runner = "Wine" };

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
