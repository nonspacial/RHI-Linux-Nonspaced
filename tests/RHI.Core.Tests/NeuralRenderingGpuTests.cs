using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #229 (owner decision 2026-10-22): "Latest" Neural Rendering DLL = the newest one made for the user's card generation,
/// judged by the manifest label; other generations hidden from every NR list; unknown card = everything; several cards =
/// the newest generation. The probe reads fake /proc trees with real "Model:" strings, never the real one.
/// </summary>
public sealed class NeuralRenderingGpuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-gpu-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingGpuTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---- probe: generation from the model names under /proc --------------------------------------------------------

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", RtxGeneration.Rtx40)]
    [InlineData("NVIDIA GeForce RTX 3060", RtxGeneration.Rtx30)]
    [InlineData("NVIDIA GeForce RTX 5080", RtxGeneration.Rtx50)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", RtxGeneration.Rtx20)]
    [InlineData("NVIDIA GeForce RTX 3080 Ti Laptop GPU", RtxGeneration.Rtx30)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", RtxGeneration.Rtx40)]
    [InlineData("NVIDIA RTX 5000 Ada Generation", RtxGeneration.Rtx40)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", RtxGeneration.Rtx50)]
    [InlineData("Quadro RTX 5000", RtxGeneration.Rtx20)]
    [InlineData("NVIDIA TITAN RTX", RtxGeneration.Rtx20)]
    [InlineData("NVIDIA RTX A4000", RtxGeneration.Rtx30)]
    [InlineData("NVIDIA GeForce GTX 1080", RtxGeneration.Unknown)]
    [InlineData("NVIDIA TITAN X (Pascal)", RtxGeneration.Unknown)]
    [InlineData("Tesla T4", RtxGeneration.Unknown)]
    // Review nit 5: the chip decides where the GeForce number names another generation (exceptions table).
    [InlineData("NVIDIA GeForce RTX 2050", RtxGeneration.Rtx30)]              // Ampere GA107
    [InlineData("NVIDIA GeForce RTX 3050 A Laptop GPU", RtxGeneration.Rtx40)] // Ada AD106
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", RtxGeneration.Rtx30)]
    [InlineData("NVIDIA GeForce RTX 3050 Ti Laptop GPU", RtxGeneration.Rtx30)]
    [InlineData("NVIDIA GeForce RTX 2060", RtxGeneration.Rtx20)]
    public void Probe_ReadsTheGenerationFromTheModelName(string model, RtxGeneration expected)
    {
        var facts = NvidiaGpuProbe.Read(ProcTree(_root, OpenModuleVersion, model), Path.Combine(_root, "sys"));

        Assert.Equal(expected, facts.Generation);
        Assert.True(facts.Present);
    }

    // w11 follow-up E4 (nr-gpu re-review 2): frame generation (#211's FG preset) follows the same chip table as the
    // generation, so the two never disagree: the RTX 3050 A Laptop GPU is Ada and runs it; the laptop RTX 2050 is Ampere.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 A Laptop GPU", true)] // Ada AD106
    [InlineData("NVIDIA GeForce RTX 2050", false)]             // Ampere GA107
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", false)]
    [InlineData("NVIDIA GeForce RTX 3050 Ti Laptop GPU", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", true)]
    [InlineData("Quadro RTX 4000", false)]
    public void Probe_FrameGeneration_FollowsTheChip_AsTheGenerationDoes(string model, bool frameGeneration)
    {
        var facts = NvidiaGpuProbe.Read(ProcTree(_root, OpenModuleVersion, model), Path.Combine(_root, "sys"));

        Assert.Equal(frameGeneration, facts.FrameGeneration);
        Assert.Equal(!frameGeneration, NvidiaGpuProbe.IsBeforeFrameGeneration(model));
        Assert.Equal(frameGeneration, facts.Generation >= RtxGeneration.Rtx40);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", "NVIDIA GeForce RTX 4090")]
    [InlineData("NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3060")]
    public void Probe_SeveralCards_TakesTheNewestGeneration_InEitherOrder(string first, string second)
        => Assert.Equal(RtxGeneration.Rtx40, NvidiaGpuProbe.Read(ProcTree(_root, OpenModuleVersion, first, second), Path.Combine(_root, "sys")).Generation);

    [Fact]
    public void Probe_AnUnknownCardBesideAKnownOne_KeepsTheKnownGeneration()
        => Assert.Equal(RtxGeneration.Rtx30,
            NvidiaGpuProbe.Read(ProcTree(_root, OpenModuleVersion, "NVIDIA GeForce GTX 1080", "NVIDIA RTX A4000"), Path.Combine(_root, "sys")).Generation);

    /// <summary>Review nit 4: one card's unreadable information file leaves out that card only, never every card.</summary>
    [PermissionDenialFact]
    public void Probe_OneUnreadableInformationFile_KeepsTheReadableCards()
    {
        var proc = ProcTree(_root, OpenModuleVersion, "NVIDIA GeForce RTX 5090", "NVIDIA GeForce RTX 4090");
        PermissionDenial.MakeUnreadable(Path.Combine(proc, "driver", "nvidia", "gpus", "0000:01:00.0", "information"));

        var facts = NvidiaGpuProbe.Read(proc, Path.Combine(_root, "sys"));

        Assert.Equal((true, RtxGeneration.Rtx40, "NVIDIA GeForce RTX 4090"), (facts.Present, facts.Generation, facts.Model));
    }

    [Fact]
    public void Probe_NvidiaNotLoaded_IsUnknown_WithNoDriver()
    {
        var facts = NvidiaGpuProbe.Read(Path.Combine(_root, "empty-proc"), Path.Combine(_root, "sys"));

        Assert.Equal((RtxGeneration.Unknown, (string?)null, false), (facts.Generation, facts.DriverVersion, facts.Present));
    }

    [Fact]
    public void Probe_DriverLoadedWithoutModels_IsUnknown_ButKnowsTheDriver()
    {
        var facts = NvidiaGpuProbe.Read(ProcTree(_root, OpenModuleVersion), Path.Combine(_root, "sys"));

        Assert.Equal((RtxGeneration.Unknown, "615.78.08"), (facts.Generation, facts.DriverVersion));
    }

    // ---- probe: the driver version (one reader, moved from the HDR service) -----------------------------------------

    [Theory]
    [InlineData(OpenModuleVersion, "615.78.08")]
    [InlineData("NVRM version: NVIDIA UNIX x86_64 Kernel Module  550.54.14  Thu Feb 22 01:44:30 UTC 2024\nGCC version:  gcc version 13.2.1 (GCC) \n", "550.54.14")]
    [InlineData("NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  610.57.04  Release Build  (dvs-builder@U16-I1-N03-12-2)  Wed Jul 16 2026\n", "610.57.04")]
    [InlineData("NVRM version: NVIDIA UNIX x86_64 Kernel Module  615.71.09  Tue Sep  2 21:01:52 UTC 2026\n", "615.71.09")]
    [InlineData("GCC version:  gcc version 16.2.1 20260810 (GCC) \n", null)]
    [InlineData("", null)]
    public void DriverVersion_IsReadFromTheNvrmLine_OpenAndProprietaryModules(string text, string? expected)
        => Assert.Equal(expected, NvidiaGpuProbe.ParseDriverVersion(text));

    [Fact]
    public void DriverVersion_ReadsTheFileUnderTheGivenRoot_AndNullWhenNotLoaded()
    {
        Assert.Equal("615.71.09", NvidiaGpuProbe.ReadDriverVersion(ProcTree(_root, ProprietaryModuleVersion("615.71.09"))));
        Assert.Null(NvidiaGpuProbe.ReadDriverVersion(Path.Combine(_root, "no-proc")));
    }

    // ---- policy: the manifest label decides ------------------------------------------------------------------------

    [Theory]
    [InlineData("310.8.3 (50xx)", "50")]
    [InlineData("310.8.2 (20/30/40/50)", "20,30,40,50")]
    [InlineData("310.8.4 (RTX 40 Series)", "40")]
    [InlineData("310.8.4 (40xx, 50xx)", "40,50")]
    [InlineData("310.8.4 (RTX50)", "50")]
    [InlineData("310.8.4 (30x/40x)", "30,40")]
    public void Label_NamesItsGenerations(string version, string expected)
        => Assert.Equal(expected, string.Join(",", NeuralRenderingGpuPolicy.GenerationsIn(version)!.Order().Select(g => (int)g)));

    [Theory]
    [InlineData("310.8.99")]
    [InlineData("310.8.98 (beta)")]
    [InlineData("310.8.97 (40/90)")]
    [InlineData("310.8.96 ()")]
    [InlineData("")]
    public void Label_UnlabelledOrUnreadable_CannotBeJudged_AndFitsEveryCard(string version)
    {
        Assert.Null(NeuralRenderingGpuPolicy.GenerationsIn(version));
        foreach (var card in Enum.GetValues<RtxGeneration>())
            Assert.True(NeuralRenderingGpuPolicy.Fits(version, card));
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx20, ForAll)]
    [InlineData(RtxGeneration.Rtx30, ForAll)]
    [InlineData(RtxGeneration.Rtx40, ForAll)]
    [InlineData(RtxGeneration.Rtx50, For50)]
    [InlineData(RtxGeneration.Unknown, For50)]
    public void Latest_PerGeneration_OverTheLiveList(RtxGeneration card, string latest)
        => Assert.Equal(latest, NeuralRenderingGpuPolicy.LatestFor(LiveVersions, card));

    [Fact]
    public void Lists_HideOtherGenerations_AndUnknownShowsEverythingNewestFirst()
    {
        Assert.Equal([ForAll], NeuralRenderingGpuPolicy.ForCard(LiveVersions, RtxGeneration.Rtx40));
        Assert.Equal([For50, ForAll, Old50], NeuralRenderingGpuPolicy.ForCard(LiveVersions, RtxGeneration.Rtx50));
        Assert.Equal(LiveVersions, NeuralRenderingGpuPolicy.ForCard(LiveVersions, RtxGeneration.Unknown));
    }

    [Fact]
    public void Lists_AnUnlabelledEntry_IsNeverHidden()
        => Assert.Equal(["310.9.0", ForAll], NeuralRenderingGpuPolicy.ForCard(["310.9.0", For50, ForAll], RtxGeneration.Rtx30));

    /// <summary>"Latest" = first fitting entry in manifest order: the live manifest lists NR newest first (2026-10-22).</summary>
    [Fact]
    public void LiveManifest_ListsNeuralRenderingNewestFirst()
    {
        var numbers = LiveVersions.Select(v => Version.Parse(v.Split(' ')[0])).ToArray();
        Assert.Equal(numbers.OrderByDescending(v => v), numbers);
    }

    // ---- consumers: the DLSS service ---------------------------------------------------------------------------------

    [Fact]
    public async Task DlssService_Versions_And_Latest_FollowTheCard_WhilePublishedKeepsEverything()
    {
        using var http = new HttpClient(new Server());
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx40);
        await dlss.FetchManifestAsync();

        Assert.Equal([ForAll], dlss.Versions(DlssComponentKind.NeuralRendering));
        Assert.Equal(ForAll, dlss.LatestVersion(DlssComponentKind.NeuralRendering));
        Assert.Equal(LiveVersions, dlss.PublishedVersions(DlssComponentKind.NeuralRendering));
    }

    [Fact]
    public async Task DlssService_EnsureNewestCached_StagesTheDllMadeForThisCard_AndNeverDownloadsThe50xxBuild()
    {
        var server = new Server();
        using var http = new HttpClient(server);
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx40);

        var path = await dlss.EnsureNewestCachedAsync(DlssComponentKind.NeuralRendering);

        Assert.Equal(Dll(ForAll), File.ReadAllBytes(path!));
        Assert.Equal((0, 1), (server.DownloadsOf(For50), server.DownloadsOf(ForAll)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("310.7.0 (20/30/40/50)")] // a pin no longer published
    public async Task DlssService_EnsureCachedVersion_NullOrGone_FallsBackToLatestForThisCard(string? version)
    {
        using var http = new HttpClient(new Server());
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx30);

        var cached = await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, version);

        Assert.Equal(ForAll, cached!.Value.Version);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(cached.Value.Path));
    }

    [Fact]
    public async Task DlssService_AHandPickedVersionForAnotherCard_IsStillServedByName()
    {
        using var http = new HttpClient(new Server());
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx40);

        var cached = await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, For50);

        Assert.Equal(For50, cached!.Value.Version);
    }

    [Fact]
    public async Task DlssService_UnknownCard_KeepsTodaysNewestFirst()
    {
        using var http = new HttpClient(new Server());
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Unknown);

        Assert.Equal(For50, (await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, null))!.Value.Version);
        Assert.Equal(LiveVersions, dlss.Versions(DlssComponentKind.NeuralRendering));
    }

    [Fact]
    public async Task DlssService_OtherFamilies_AreNeverFilteredByTheCard()
    {
        const string manifest = """{"dlss":[{"version":"310.9.16 (50xx)","url":"https://github.com/rhi-tests/d/releases/download/1/a.zip"}],"dlssd":[],"dlssg":[],"dlssnr":[],"streamline":[]}""";
        using var http = new HttpClient(new FixedManifest(manifest));
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx30);
        await dlss.FetchManifestAsync();

        Assert.Equal(["310.9.16 (50xx)"], dlss.Versions(DlssComponentKind.SuperResolution));
    }

    // ---- consumers: the NR swap of ShortFuse / Feeder with no pin ----------------------------------------------------

    [Fact]
    public async Task NrSwap_WithNoPin_UsesLatestForThisCard()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var game = NrGpuGames.Game(bin);
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(For50));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        NrGpuGames.SeedOwner(records, game, ManagedComponentKind.ShortFuse, nr);
        using var http = new HttpClient(new Server());

        var result = await NeuralRenderingDllSwap.SwapAsync(records, Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx40), game,
            ManagedComponentKind.ShortFuse, "ShortFuse", version: null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(nr));
        Assert.Equal(ForAll, records.Find(game, ManagedComponentKind.ShortFuse)!.Metadata[NeuralRenderingDllSwap.MetaDllVersion]);
    }

    // ---- consumers: Quick Apply / the DLSS batch (saved defaults) ---------------------------------------------------

    [Fact]
    public async Task QuickApply_ANrDefaultForAnotherCard_IsSkippedBeforeAnyChange_WhileSrStillApplies()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "quick")).FullName;
        var game = NrGpuGames.Game(bin);
        var sr = Path.Combine(bin, "nvngx_dlss.dll");
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(sr, Dll(Old50)[..70_000]);
        File.WriteAllBytes(nr, Dll(Old50));
        var server = new Server();
        using var http = new HttpClient(server);
        var dlss = Dlss(http, Path.Combine(_root, "cache"), RtxGeneration.Rtx40);
        var service = new LinuxDlssDefaultsService(dlss, new LinuxDeploymentRecordStore(Path.Combine(_root, "quick.json")));

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(SuperResolution: SrVersion, NeuralRendering: For50), [sr, nr]);

        var skipped = Assert.Single(result.Items, item => item.Component == LinuxDlssBatchComponent.NeuralRendering);
        Assert.True(skipped is { NotForThisCard: true, Skipped: true, Changed: false });
        Assert.Equal(LinuxDlssDefaultsService.NotMadeForThisCardPrefix + For50, skipped.Message);
        Assert.Equal(Dll(Old50), File.ReadAllBytes(nr));
        Assert.False(File.Exists(nr + ".original"));
        Assert.Equal(0, server.DownloadsOf(For50));
        Assert.Equal(SrDll, File.ReadAllBytes(sr)); // the SR step still ran
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx40, ForAll)]
    [InlineData(RtxGeneration.Unknown, For50)]
    [InlineData(RtxGeneration.Rtx50, For50)]
    public async Task QuickApply_ANrDefaultTheCardCanUse_IsApplied(RtxGeneration card, string version)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "quick-ok-" + card)).FullName;
        var game = NrGpuGames.Game(bin);
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(Old50));
        using var http = new HttpClient(new Server());
        var service = new LinuxDlssDefaultsService(Dlss(http, Path.Combine(_root, "cache-" + card), card),
            new LinuxDeploymentRecordStore(Path.Combine(_root, $"quick-{card}.json")));

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(NeuralRendering: version), [nr]);

        Assert.Equal(1, result.ChangedCount);
        Assert.DoesNotContain(result.Items, item => item.NotForThisCard);
        Assert.Equal(Dll(version), File.ReadAllBytes(nr));
    }

    // ---- the app's composition wires the one seam -------------------------------------------------------------------

    [Fact]
    public void Composition_PointsTheNrListsAtTheHostsCard_WithoutReadingItHere()
    {
        using var http = new HttpClient(new FixedManifest("{}"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "composition.json"));
        var wine = new WineDllOverrideService();
        var app = RHI.Desktop.AppComposition.Create(http, records, wine, LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        var unset = new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, "unset")).CardGeneration;

        // The default is one shared "unknown" delegate: the app's is the probe's (never invoked here: it reads /proc).
        Assert.NotSame(unset, app.Dlss.CardGeneration);
        Assert.NotSame(new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!, shortFuse: null!,
            dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!).NvidiaGpu, app.ManagedComponents.NvidiaGpu);
    }

    private sealed class FixedManifest(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}

/// <summary>Games and owner records the w11/nr-gpu tests share.</summary>
internal static class NrGpuGames
{
    public static DetectedGame Game(string bin, string name = "NR GPU test") => new()
    {
        Name = name, InstallPath = bin, Source = "Test", Runner = "Wine", BinaryDirectory = bin,
        ExecutablePath = Path.Combine(bin, "Game.exe")
    };

    /// <summary>An NR owner record holding <paramref name="nr"/> under its current hash (metadata optional: older records have none).</summary>
    public static LinuxDeploymentRecord SeedOwner(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind owner, string nr,
        IReadOnlyDictionary<string, string>? metadata = null, string version = "1.0")
    {
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = owner,
            BinaryDirectory = Path.GetDirectoryName(nr)!, Version = version,
            Files = [new LinuxDeployedFile { Path = nr, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(nr) }],
            Metadata = new(metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        };
        if (owner == ManagedComponentKind.Dlss5Feeder) record.Metadata["Feeder.FullStack"] = "1";
        records.Upsert(record);
        return records.Find(game, owner)!;
    }
}
