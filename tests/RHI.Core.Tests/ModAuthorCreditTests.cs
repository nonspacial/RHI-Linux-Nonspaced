using System.Runtime.InteropServices;
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
    [InlineData("Forge & Fyren & Jon & Shortfuse", "Forge, Fyren, Jon, Shortfuse")]
    [InlineData("Ritsu & OopyDoopy (Jon)", "Ritsu, Jon")]
    // Upstream SplitAuthors only splits on "&" and "and": these stay one name, as upstream shows them.
    [InlineData("Marat + Ritsu", "Marat + Ritsu")]
    [InlineData("Ritsu/marat569", "Ritsu/marat569")]
    [InlineData("Anderson", "Anderson")]
    [InlineData("theonlyames (Riha)", "theonlyames (Riha)")]
    [InlineData("  ", null)]
    public void Format_SplitsCatalogueAuthorFieldsIntoDisplayNames(string raw, string? expected)
        => Assert.Equal(expected, ModAuthorCredit.Format(raw));

    [Fact]
    public void RenoDxAuthor_FollowsUpstreamPrecedence()
    {
        const string ueExtended = "https://marat569.github.io/renodx/renodx-ue-extended.addon64";
        const string genericUnreal = RenoDxCatalogService.GenericUnrealUrl;
        const string genericUnity = RenoDxCatalogService.GenericUnityUrl32;
        var withOverride = new DetectedGame { Name = "Assassin's Creed", InstallPath = "/g", AuthorOverride = "Musa" };
        var plain = new DetectedGame { Name = "Some Game", InstallPath = "/g" };

        // 1. UE-Extended is Marat's build, whatever the database or the manifest say.
        Assert.Equal("Marat", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("X", ueExtended, null, null, Author: "Ritsu"), withOverride));
        // 2. The mod's maintainer: the RHI database author beats the manifest authorOverrides entry.
        Assert.Equal("Ritsu", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("AC", "https://x.test/a.addon64", null, null, Author: "Ritsu"), withOverride));
        // 3. No maintainer: the manifest authorOverrides entry, also over a generic engine download.
        Assert.Equal("Musa", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("AC", "https://x.test/a.addon64", null, null), withOverride));
        Assert.Equal("Musa", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("AC", genericUnreal, null, null), withOverride));
        Assert.Equal("Musa", ModAuthorCredit.ForRenoDx(null, withOverride));
        // 4. Nothing else: the generic Unreal / Unity add-on's author.
        Assert.Equal("ShortFuse", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("X", genericUnreal, null, null), plain));
        Assert.Equal("Voosh", ModAuthorCredit.ForRenoDx(new RenoDxModEntry("X", null, genericUnity, null), plain));
        Assert.Null(ModAuthorCredit.ForRenoDx(new RenoDxModEntry("X", "https://x.test/a.addon64", null, null), plain));
    }

    [Fact]
    public async Task RenoDxAuthor_GenericFallbackKeepsItsEngineMaintainer()
    {
        // Upstream's generic fallback mods carry Maintainer ShortFuse / Voosh, so a manifest authorOverrides entry
        // (only applied when the maintainer is empty) never replaces it.
        using var http = new HttpClient(new OfflineHandler());
        var catalogue = new RenoDxCatalogService(http);
        var game = new DetectedGame { Name = "Unlisted Unreal Game", InstallPath = "/g", AuthorOverride = "RankFTW" };

        var unreal = await catalogue.ResolveAsync(game, ["/g/Engine/Binaries/Win64/Game-Win64-Shipping.exe"]);
        var unity = await catalogue.ResolveAsync(game, ["/g/UnityPlayer.dll"]);

        Assert.Equal("ShortFuse", ModAuthorCredit.ForRenoDx(unreal, game));
        Assert.Equal("Voosh", ModAuthorCredit.ForRenoDx(unity, game));
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
    public void Decorate_UnconfirmedTarget_CreditsNothingRhiHasNotInstalled()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        // Explicit patch target required but not confirmed: RHI installs nothing here, so not-installed rows get no credit.
        var game = new DetectedGame { Name = "Launcher Game", InstallPath = bin, RequiresExplicitPatchTarget = true, AuthorOverride = "Voosh" };
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

        Assert.All(components, c =>
        {
            Assert.Empty(c.InstalledAddonFiles);
            Assert.Null(c.Author);
        });
    }

    [Fact]
    public void Decorate_ConfirmedTarget_CreditsWhatRhiWouldInstall_AndDropsNamesOfRemovedOrStaleFiles()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        // Confirmed target (no explicit split required). Native Linux: Decorate makes generic Luma Unsupported.
        var game = new DetectedGame { Name = "Native", InstallPath = bin, BinaryDirectory = bin, Runner = "Native Linux", AuthorOverride = "Voosh" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        // A record left behind after the add-on was deleted by hand: the probe no longer sees it, the row is Available.
        records.Upsert(Record(game, ManagedComponentKind.RenoDX, [Path.Combine(bin, "renodx-native.addon64")]));
        using var http = new HttpClient(new OfflineHandler());
        var components = new List<ManagedComponent>
        {
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Available },
            new() { Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma Framework", Availability = ComponentAvailability.Available }
        };

        Service(records, http).Decorate(game, components);

        Assert.Empty(components[0].InstalledAddonFiles);
        Assert.Equal("Voosh", components[0].Author);
        Assert.Equal(ComponentAvailability.Unsupported, components[1].Availability);
        Assert.Null(components[1].Author);
    }

    [Fact]
    public void Decorate_InstalledRow_ShowsOnlyRecordedFilesTheProbeFound()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var game = new DetectedGame { Name = "Prey", InstallPath = bin, RequiresExplicitPatchTarget = true };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        records.Upsert(Record(game, ManagedComponentKind.RenoDX, [Path.Combine(bin, "renodx-prey.addon64"), Path.Combine(bin, "renodx-prey-old.addon64")]));
        using var http = new HttpClient(new OfflineHandler());
        var components = new List<ManagedComponent>
        {
            // The probe found one of the two recorded add-ons (the other was replaced or deleted).
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Installed,
                DetectedAddonFiles = ["RENODX-PREY.addon64", "renodx-other.addon64"] }
        };

        Service(records, http).Decorate(game, components);

        Assert.Equal(["renodx-prey.addon64"], components[0].InstalledAddonFiles);
    }

    [Fact]
    public void Probe_ReportsTheRenoDxAndLumaAddonFilesItFound()
    {
        var game = new DetectedGame { Name = "Prey", InstallPath = "/g", BinaryDirectory = "/g/bin" };
        var components = RHI.Core.Services.ComponentProbe.Probe(game,
            ["/g/bin/renodx-prey.addon64", "/g/bin/renodx-dlss.addon64", "/g/bin/Luma-Prey.addon64", "/g/bin/dxgi.dll"]);

        Assert.Equal(["renodx-prey.addon64"], components.Single(c => c.Kind == ManagedComponentKind.RenoDX).DetectedAddonFiles);
        Assert.Equal(["Luma-Prey.addon64"], components.Single(c => c.Kind == ManagedComponentKind.LumaFramework).DetectedAddonFiles);
        Assert.Empty(components.Single(c => c.Kind == ManagedComponentKind.ReShade).DetectedAddonFiles);
    }

    [Fact]
    public async Task Decorate_CreditPassNeverReadsTheGameExecutable()
    {
        // Decorate runs on the UI thread. The game executable is a FIFO with no writer: opening it to read the PE
        // header (as the old Luma eligibility re-check did) blocks, so any read of it shows up as a hang.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var fifo = Path.Combine(bin, "Launcher.exe");
        Assert.Equal(0, mkfifo(fifo, 0x180));
        var game = new DetectedGame
        {
            Name = "UE Launcher Game", InstallPath = bin, RequiresExplicitPatchTarget = true, ExecutablePath = fifo,
            GraphicsApi = GraphicsApi.DirectX11, EngineHint = "Unreal Engine 4"
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        using var http = new HttpClient(new OfflineHandler());
        var components = new List<ManagedComponent>
        {
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX HDR", Availability = ComponentAvailability.Available },
            new() { Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma Framework", Availability = ComponentAvailability.Available }
        };

        var decorate = Task.Run(() => Service(records, http).Decorate(game, components));
        var finished = await Task.WhenAny(decorate, Task.Delay(TimeSpan.FromSeconds(5))) == decorate;
        if (!finished)
        {
            // Release the blocked reader so the test process can exit.
            using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write);
        }

        Assert.True(finished, "Decorate opened the game executable");
        Assert.Null(components[1].Author);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

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
