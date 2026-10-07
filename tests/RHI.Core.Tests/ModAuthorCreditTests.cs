using System.Windows.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// Upstream 2.8.1: the detail shows the installed RenoDX/Luma add-on file names and both mod authors (#139).
public sealed class ModAuthorCreditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rhi-mod-credit-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("Musa", "Musa")]
    [InlineData("ShortFuse & Musa", "ShortFuse, Musa")]
    [InlineData("OopyDoopy (Jon)", "Jon")]
    [InlineData("OopyDoopy and Voosh", "Jon, Voosh")]
    [InlineData("Ritsu, & ShortFuse", "Ritsu, ShortFuse")]
    [InlineData("Marat + Ritsu", "Marat, Ritsu")]
    [InlineData("theonlyames (Riha)", "theonlyames (Riha)")]
    [InlineData("  ", null)]
    public void Format_SplitsCatalogueAuthorFieldsIntoDisplayNames(string raw, string? expected)
        => Assert.Equal(expected, ModAuthorCredit.Format(raw));

    [Fact]
    public void RenoDxAuthor_PrefersCatalogueEntry_ThenManifestOverride()
    {
        var game = new DetectedGame { Name = "Assassin's Creed", InstallPath = "/g", AuthorOverride = "Musa" };

        Assert.Equal("Ritsu", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("AC", "https://x.test/a.addon64", null, null, Author: "Ritsu"), game));
        Assert.Equal("Musa", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("AC", "https://x.test/a.addon64", null, null), game));
        Assert.Equal("Musa", ModAuthorCredit.ForRenoDx(null, game));
    }

    [Fact]
    public void LumaAuthor_FollowsTheRecordThenTheCatalogue()
    {
        LinuxDeploymentRecord Record(params (string Key, string Value)[] metadata) => new()
        {
            Component = ManagedComponentKind.LumaFramework,
            Metadata = metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase)
        };
        var named = new LumaCatalogEntry("Prey", "Ersh & Pumbo", null, "✅", null, null);

        Assert.Equal("Ersh, Pumbo", ModAuthorCredit.ForLuma(Record(("LumaFramework.Author", "Ersh & Pumbo"), ("LumaFramework.Generic", "0")), null));
        Assert.Equal("Pumbo", ModAuthorCredit.ForLuma(Record(("LumaFramework.Author", ""), ("LumaFramework.Generic", "1")), named));
        // A dropped local package is not attributed to anyone, even when a catalogue match exists.
        Assert.Null(ModAuthorCredit.ForLuma(Record(("LumaFramework.Generic", "1"), ("LumaFramework.LocalPackage", "1")), named));
        Assert.Equal("Ersh, Pumbo", ModAuthorCredit.ForLuma(null, named));
        Assert.Equal("Pumbo", ModAuthorCredit.ForLuma(null, null));
    }

    [Fact]
    public void Decorate_FillsAddonFilesFromRecordsAndAuthors_AndRowShowsThem()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        // Explicit patch target required but not confirmed: Decorate skips the per-component rules, credits still apply.
        var game = new DetectedGame { Name = "Prey", InstallPath = bin, BinaryDirectory = bin, RequiresExplicitPatchTarget = true, AuthorOverride = "Musa" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        // File names come from what was written (here a renamed add-on), never re-derived from the catalogue.
        records.Upsert(Record(game, ManagedComponentKind.RenoDX, [Path.Combine(bin, "renodx-prey-dlss.addon64"), Path.Combine(bin, "renodx.ini")]));
        records.Upsert(Record(game, ManagedComponentKind.LumaFramework, [Path.Combine(bin, "Luma-Prey.addon"), Path.Combine(bin, "Luma.ini")],
            ("LumaFramework.Author", "Pumbo")));
        using var http = new HttpClient(new OfflineHandler());
        var service = Service(records, http);
        var components = new List<ManagedComponent>
        {
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Installed },
            new() { Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma Framework", Availability = ComponentAvailability.Installed },
            new() { Kind = ManagedComponentKind.ReShade, DisplayName = "ReShade", Availability = ComponentAvailability.Installed }
        };

        service.Decorate(game, components);

        Assert.Equal(["renodx-prey-dlss.addon64"], components[0].InstalledAddonFiles);
        Assert.Equal("Musa", components[0].Author);
        Assert.Equal(["Luma-Prey.addon"], components[1].InstalledAddonFiles);
        Assert.Equal("Pumbo", components[1].Author);
        Assert.Empty(components[2].InstalledAddonFiles);
        Assert.Null(components[2].Author);

        UiLocalizationService.Apply("en-US");
        var rows = components.Select(c => new ComponentRowViewModel(c, new NoOpCommand())).ToArray();
        Assert.Equal("renodx-prey-dlss.addon64 · by Musa", rows[0].Credit);
        Assert.Equal("Luma-Prey.addon · by Pumbo", rows[1].Credit);
        Assert.False(rows[2].HasCredit);
    }

    [Fact]
    public void Decorate_AfterRemoval_DropsFileNames_AndCreditsOnlyWhatRhiWouldInstall()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        // Native Linux title: generic Luma is ineligible, so it is not credited to anyone.
        var game = new DetectedGame { Name = "Native", InstallPath = bin, Runner = "Native Linux", RequiresExplicitPatchTarget = true, AuthorOverride = "Voosh" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        using var http = new HttpClient(new OfflineHandler());
        var components = new List<ManagedComponent>
        {
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Available },
            new() { Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma Framework", Availability = ComponentAvailability.Available },
            // Installed outside RHI: neither the file list nor an author is trustworthy.
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Installed }
        };

        Service(records, http).Decorate(game, components);

        Assert.Empty(components[0].InstalledAddonFiles);
        Assert.Equal("Voosh", components[0].Author);
        Assert.Null(components[1].Author);
        Assert.Null(components[2].Author);
    }

    private static LinuxManagedComponentService Service(LinuxDeploymentRecordStore records, HttpClient http)
        // Only the services the credit pass reaches are real; the rest are never touched for an unconfirmed target.
        => new(records, reshade: null!, addonManager: null!,
            renodx: new RenoDxDeploymentService(http, records, new RenoDxCatalogService(http)), renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!,
            lumaFramework: new LumaFrameworkDeploymentService(http, records), lumaPolicy: null!);

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind, string[] files, params (string Key, string Value)[] metadata) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory ?? "",
        Files = files.Select(path => new LinuxDeployedFile { Path = path }).ToList(),
        Metadata = metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase)
    };

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("unit tests are offline");
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
