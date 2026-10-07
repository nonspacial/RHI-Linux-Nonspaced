using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
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

    [Fact]
    public async Task Find_IgnoresAManifestTitleFoundOnlyInTheFolderPath()
    {
        // "Witcher Mod Kit" resolves to the Witcher title from path evidence alone; that game's warning must not attach.
        var json = $$"""
        {
          "version": 4,
          "installWarnings": { "The Witcher 3: Wild Hunt — Remastered": { "renodx": "⚠ Original version only." } }
        }
        """;
        using var http = new HttpClient(new JsonHandler(json));
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        var tool = Game("Witcher Mod Kit");
        tool.InstallPath = Path.Combine(Path.GetTempPath(), "The Witcher 3 Wild Hunt Remastered", "modkit");
        var games = await policy.ApplyAsync([tool]);

        var applied = Assert.Single(games);
        Assert.Equal("The Witcher 3: Wild Hunt — Remastered", applied.CanonicalManifestName);
        Assert.Null(ManifestInstallWarning.Find(applied, ManagedComponentKind.RenoDX));
    }

    [Theory]
    [InlineData(ComponentAvailability.Available, true)]
    [InlineData(ComponentAvailability.Unknown, true)]
    [InlineData(ComponentAvailability.Installed, false)]
    [InlineData(ComponentAvailability.UpdateAvailable, false)]
    public void IsFirstInstall_PromptsUnlessTheComponentIsAlreadyThere(ComponentAvailability availability, bool expected)
        => Assert.Equal(expected, ManifestInstallWarning.IsFirstInstall(availability));

    [Fact]
    public void RenoDxInstall_ShowsTheWarningOfTheReShadeItAddsAutomatically()
    {
        using var temp = new TempDir();
        var game = BinGame(temp.Path, "Grand Theft Auto V Enhanced");
        game.ManifestInstallWarnings = Warnings(("reshade", "⚠ DirectStorage: https://www.gta5-mods.com/scripts/directstoragefix"));
        var service = Service(temp.Path, out var records);

        var pending = service.PendingAutoDependencies(game, ManagedComponentKind.RenoDX);
        Assert.Equal([ManagedComponentKind.ReShade], pending);
        var warning = Assert.Single(ManifestInstallWarning.ForInstall(game, ManagedComponentKind.RenoDX, installsTarget: true, pending));
        Assert.Equal((ManagedComponentKind.ReShade, true), (warning.Kind, warning.AddedAutomatically));
        Assert.Equal("https://www.gta5-mods.com/scripts/directstoragefix", Assert.Single(warning.Warning.Links).AbsoluteUri);

        // Once RHI manages ReShade, RenoDX no longer adds it and the ReShade warning is not shown again.
        var payload = Path.Combine(temp.Path, "bin", "dxgi.dll");
        File.WriteAllText(payload, "x");
        records.Upsert(Record(game, ManagedComponentKind.ReShade, payload));
        Assert.Empty(service.PendingAutoDependencies(game, ManagedComponentKind.RenoDX));
        Assert.Empty(ManifestInstallWarning.ForInstall(game, ManagedComponentKind.RenoDX, installsTarget: true, []));

        // A broken managed ReShade makes the install refuse; nothing is added, so nothing to warn about.
        File.Delete(payload);
        Assert.Empty(service.PendingAutoDependencies(game, ManagedComponentKind.RenoDX));
    }

    [Fact]
    public void ReEngineInstall_ListsEveryAddedComponentInInstallOrderThenTheTarget()
    {
        using var temp = new TempDir();
        var game = BinGame(temp.Path, "Resident Evil 4");
        game.EngineHint = "RE Engine";
        game.ManifestInstallWarnings = Warnings(("renodx", "RenoDX note."), ("reshade", "ReShade note."), ("reframework", "REF note."));
        var service = Service(temp.Path, out _);

        var pending = service.PendingAutoDependencies(game, ManagedComponentKind.RenoDX);
        Assert.Equal([ManagedComponentKind.REFramework, ManagedComponentKind.ReShade], pending);
        var warnings = ManifestInstallWarning.ForInstall(game, ManagedComponentKind.RenoDX, installsTarget: true, pending);
        Assert.Equal(
            [(ManagedComponentKind.REFramework, true, "REF note."), (ManagedComponentKind.ReShade, true, "ReShade note."), (ManagedComponentKind.RenoDX, false, "RenoDX note.")],
            warnings.Select(w => (w.Kind, w.AddedAutomatically, w.Warning.Text)));

        // ReShade profile re-apply on an installed ReShade: only the RE Framework it would add is shown.
        var reapply = ManifestInstallWarning.ForInstall(game, ManagedComponentKind.ReShade, installsTarget: false,
            service.PendingAutoDependencies(game, ManagedComponentKind.ReShade));
        Assert.Equal([ManagedComponentKind.REFramework], reapply.Select(w => w.Kind));

        // Components with no dependencies (OptiScaler) never add anything.
        Assert.Empty(service.PendingAutoDependencies(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public void InstallWarningSections_NameEachComponentWhenTheDialogShowsMoreThanTheRequestedOne()
    {
        var note = ManifestInstallWarning.Parse("Note.");
        var single = Assert.Single(MainWindowViewModel.InstallWarningSections([new ComponentInstallWarning(ManagedComponentKind.RenoDX, false, note)], "RenoDX"));
        Assert.Null(single.Heading);

        var sections = MainWindowViewModel.InstallWarningSections(
            [new ComponentInstallWarning(ManagedComponentKind.ReShade, true, note), new ComponentInstallWarning(ManagedComponentKind.RenoDX, false, note)], "RenoDX HDR");
        Assert.Equal(2, sections.Count);
        Assert.Contains("ReShade", sections[0].Heading);
        Assert.Contains("RenoDX HDR", sections[0].Heading);
        Assert.Equal("RenoDX HDR", sections[1].Heading);

        var dependencyOnly = Assert.Single(MainWindowViewModel.InstallWarningSections([new ComponentInstallWarning(ManagedComponentKind.ReShade, true, note)], "ShortFuse"));
        Assert.Contains("ReShade", dependencyOnly.Heading);
    }

    [Fact]
    public void Parse_KeepsABalancedClosingBracketInALink()
    {
        var wiki = ManifestInstallWarning.Parse("See https://en.wikipedia.org/wiki/HDR_(disambiguation)");
        Assert.Equal("See", wiki.Text);
        Assert.Equal("https://en.wikipedia.org/wiki/HDR_(disambiguation)", Assert.Single(wiki.Links).OriginalString);

        var nested = ManifestInstallWarning.Parse("Read (https://en.wikipedia.org/wiki/HDR_(disambiguation)). Then install.");
        Assert.Equal("https://en.wikipedia.org/wiki/HDR_(disambiguation)", Assert.Single(nested.Links).OriginalString);

        // A wrapped link at the very end keeps its bracket pair in the text.
        var wrapped = ManifestInstallWarning.Parse("Details (https://x.test/a)");
        Assert.Equal("Details (https://x.test/a)", wrapped.Text);
        Assert.Equal("https://x.test/a", Assert.Single(wrapped.Links).AbsoluteUri);
    }

    private static LinuxManagedComponentService Service(string root, out LinuxDeploymentRecordStore records)
    {
        records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
        // PendingAutoDependencies reads only the record store and the game folder.
        return new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);
    }

    private static DetectedGame BinGame(string root, string name)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        return new DetectedGame { Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"), Source = "Test", Runner = "Wine" };
    }

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind, string file) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
        BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = file }]
    };

    private static IReadOnlyDictionary<string, string> Warnings(params (string Key, string Text)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Text, StringComparer.OrdinalIgnoreCase);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhi-warning-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static DetectedGame Game(string name)
        => new() { Name = name, InstallPath = Path.Combine(Path.GetTempPath(), "rhi-warning-" + Guid.NewGuid().ToString("N")), Source = "Test", Runner = "Wine" };

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
