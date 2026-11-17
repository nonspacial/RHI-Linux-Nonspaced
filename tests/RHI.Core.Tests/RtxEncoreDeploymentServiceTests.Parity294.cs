using System.Net;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #280 (upstream 2.8.9–2.9.3): RTX Encore — no longer Beta, NR file 310.9.16 (RHI's older 310.8.0 copy moved over by
/// Update), Intel's XeSS files placed as RTX Encore Hub does, Bink names with the game's file kept as *Hooked.dll, the
/// separate ASI build, the manifest's recommended DLL name, and its ⚙ settings in rtx-encore.jsonc.
/// </summary>
public sealed partial class RtxEncoreDeploymentServiceTests
{
    [Fact]
    public void Row_IsNoLongerBeta_AndNrIs310_9_16()
    {
        Assert.Equal("RTX Encore", ComponentProbe.RtxEncoreDisplayName);
        Assert.Equal("310.9.16", RtxEncoreDeploymentService.NeuralRenderingVersion);
        Assert.Equal("CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED", RtxEncoreDeploymentService.NeuralRenderingSha256);
        Assert.True(RtxEncoreDeploymentService.IsRequiredNeuralRenderingVersion("310.9.16 (20/30/40/50)")); // the DLSS list's label
    }

    // Pinned from the upstream manifest at RHI-2.9.4: "rtxEncoreDllNames": { "Euro Truck Simulator 2": "winmm.dll" }.
    [Fact]
    public async Task ManifestRecommendation_IsReadAndListedFirst()
    {
        const string manifest = """{ "version": 1, "rtxEncoreDllNames": { "Euro Truck Simulator 2": "winmm.dll" } }""";
        var client = new HttpClient(new GameLinkCatalogueTests.Fake(_ => ComponentInfoTests.Text(manifest)));
        var policy = await new UpstreamRhiManifestService(client, "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json").FetchLibraryPolicyAsync();
        Assert.Equal("winmm.dll", policy.RtxEncoreDllNames["euro truck simulator 2"]);
        Assert.Contains("Euro Truck Simulator 2", policy.KnownGameNames);

        var service = Service(new EncoreHandler());
        Assert.Equal("version.dll", service.ChosenName(_game));
        _game.RtxEncoreRecommendedDllName = "WinMM.dll";
        Assert.Equal("winmm.dll", RtxEncoreDeploymentService.RecommendedName(_game));
        Assert.Equal("winmm.dll", service.OfferedNames(_game)[0]);
        Assert.Equal("winmm.dll", service.ChosenName(_game));
        Assert.True(service.SetName(_game, "dinput8.dll").Success);
        Assert.Equal("dinput8.dll", service.ChosenName(_game)); // the user's choice wins

        _game.RtxEncoreRecommendedDllName = "rtx-encore.asi";
        Assert.Null(RtxEncoreDeploymentService.RecommendedName(_game));
        _game.RtxEncoreRecommendedDllName = "notadll.dll";
        Assert.Null(RtxEncoreDeploymentService.RecommendedName(_game));
    }

    // RTX Encore's docs: "With a Bink name, keep the game's own file beside it, renamed binkw64Hooked.dll".
    [Fact]
    public async Task BinkName_KeepsTheGamesBinkAsHooked_AndRemovalPutsItBack()
    {
        File.WriteAllText(Path.Combine(_bin, "binkw64.dll"), "the game's Bink");
        var handler = new EncoreHandler();
        var service = Service(handler);
        Assert.True(service.SetName(_game, "binkw64.dll").Success);

        var installed = await service.InstallAsync(_game);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal("the game's Bink", File.ReadAllText(Path.Combine(_bin, "binkw64Hooked.dll")));
        Assert.Equal(handler.Payload(Rc3), File.ReadAllBytes(Path.Combine(_bin, "binkw64.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "binkw64.dll.original")));

        Assert.True((await service.RemoveAsync(_game)).Success);
        Assert.Equal("the game's Bink", File.ReadAllText(Path.Combine(_bin, "binkw64.dll")));
        Assert.False(File.Exists(Path.Combine(_bin, "binkw64Hooked.dll")));
    }

    // "Fixed the rtx-encore.asi option installing the wrong file. It now uses the separate ASI build" (upstream 2.9.3); an
    // .asi install from before gets it through Update even without a new release.
    [Fact]
    public async Task AsiName_UsesTheSeparateAsiBuild_AndUpdateReplacesAnOlderRenamedDll()
    {
        _records.Upsert(Record(ManagedComponentKind.UltimateAsiLoader, Path.Combine(_bin, "winmm.dll")));
        var asiBuild = RtxEncoreIdentityTests.RtxEncoreDll(Rc3, 301_000);
        var handler = new EncoreHandler { AsiBuild = asiBuild };
        var service = Service(handler);
        Assert.True(service.SetName(_game, RtxEncoreNames.AsiName).Success);

        Assert.True((await service.InstallAsync(_game)).Success);
        Assert.Equal(asiBuild, File.ReadAllBytes(Path.Combine(_bin, RtxEncoreNames.AsiName)));

        // An install made by an older build: the DLL build under the .asi name, no build marker.
        var record = _records.Find(_game, ManagedComponentKind.RtxEncore)!;
        record.Metadata.Remove("RtxEncore.Build");
        File.WriteAllBytes(Path.Combine(_bin, RtxEncoreNames.AsiName), handler.Payload(Rc3));
        record.Files[0].Sha256 = FileHash.Sha256HexOf(handler.Payload(Rc3));
        _records.Upsert(record);

        var check = await service.CheckManagedUpdateAsync(_game);
        Assert.True(check.UpdateAvailable, check.Message);
        Assert.True((await service.UpdateAsync(_game)).Success);
        Assert.Equal(asiBuild, File.ReadAllBytes(Path.Combine(_bin, RtxEncoreNames.AsiName)));
        Assert.False((await service.CheckManagedUpdateAsync(_game)).UpdateAvailable);
    }

    private (IntelXessFiles Xess, Dictionary<string, byte[]> Bytes) EncoreXess()
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["libxess.dll"] = Pe(0x41, 4000), ["libxess_fg.dll"] = Pe(0x42, 4000), ["libxell.dll"] = Pe(0x43, 4000),
            ["LICENSE.txt"] = "Intel licence"u8.ToArray()
        };
        IntelXessFile Fake(IntelXessFile file) => file with { Size = bytes[file.Name].Length, Sha256 = FileHash.Sha256HexOf(bytes[file.Name]) };
        var http = new HttpClient(new GameLinkCatalogueTests.Fake(request =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[Path.GetFileName(request.RequestUri!.AbsolutePath)]), RequestMessage = request }));
        var xess = new IntelXessFiles(http, Path.Combine(_root, "xess-cache"))
        {
            EncoreFiles = [Fake(IntelXessFiles.LibXess), Fake(IntelXessFiles.LibXessFg), Fake(IntelXessFiles.LibXell)],
            LicenseFile = Fake(IntelXessFiles.License)
        };
        return (xess, bytes);
    }

    private RtxEncoreDeploymentService ServiceWithXess(EncoreHandler handler, IntelXessFiles xess)
    {
        var (created, overrides) = SimulatedDeath.NewJournal(JournalDir, _records);
        return new(new HttpClient(handler), _records, overrides, _names,
            new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache"), created)
        {
            RequiredNeuralRenderingSha256 = FileHash.Sha256HexOf(NrAccepted),
            Xess = xess
        };
    }

    // "If the game already has its own copies (Unreal games often keep them under Engine\Plugins), RHI replaces them where
    // they are … and puts the game's copies back when you remove RTX Encore. Files the game doesn't have, and Intel's
    // licence, go in RTX Encore's own folder" (upstream 2.9.3).
    [Fact]
    public async Task XessFiles_ReplaceTheGamesCopiesInPlace_OthersGoInRtxEncoresFolder_AndRemovalRestores()
    {
        var plugin = Directory.CreateDirectory(Path.Combine(_root, "game", "Engine", "Plugins", "XeSS", "Binaries", "ThirdParty", "Win64")).FullName;
        var gamesXess = PeVersionReaderTests.BuildVersionedPe("1.3.0.4", "1.3.0.4", 4096, extraStrings: [("CompanyName", "Intel Corporation")]);
        File.WriteAllBytes(Path.Combine(plugin, "libxess.dll"), gamesXess);
        File.WriteAllBytes(Path.Combine(plugin, "libxell.dll"), "not Intel's"u8.ToArray()); // no Intel version resource: not the game's copy
        var (xess, bytes) = EncoreXess();
        var service = ServiceWithXess(new EncoreHandler(), xess);

        var installed = await service.InstallAsync(_game);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(bytes["libxess.dll"], File.ReadAllBytes(Path.Combine(plugin, "libxess.dll")));
        Assert.Equal(gamesXess, File.ReadAllBytes(Path.Combine(plugin, "libxess.dll.encore-original")));
        var own = Path.Combine(_bin, "rtx-encore-runtimes", "intel");
        Assert.Equal(bytes["libxess_fg.dll"], File.ReadAllBytes(Path.Combine(own, "libxess_fg.dll")));
        Assert.Equal(bytes["libxell.dll"], File.ReadAllBytes(Path.Combine(own, "libxell.dll")));
        Assert.Equal("Intel licence", File.ReadAllText(Path.Combine(own, "license.txt")));
        Assert.Equal(new RtxEncoreXessStatus(true, 1), service.XessStatus(_game));

        var removed = await service.RemoveAsync(_game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(gamesXess, File.ReadAllBytes(Path.Combine(plugin, "libxess.dll")));
        Assert.False(File.Exists(Path.Combine(plugin, "libxess.dll.encore-original")));
        Assert.Equal("not Intel's", File.ReadAllText(Path.Combine(plugin, "libxell.dll")));
        Assert.False(Directory.Exists(Path.Combine(_bin, "rtx-encore-runtimes")));
    }

    // Games that already have RTX Encore get the XeSS files, and RHI's 310.8.0 runtime becomes 310.9.16, through Update.
    [Fact]
    public async Task AnInstallFromBefore_GetsTheXessFilesAndNr310_9_16_ThroughUpdate()
    {
        var oldNr = Pe(0x55);
        var (xess, _) = EncoreXess();
        var service = ServiceWithXess(new EncoreHandler(), xess);
        Assert.True(service.SetNeuralRenderingPreference(_game, true).Success);
        Assert.True((await service.InstallAsync(_game)).Success);
        var record = _records.Find(_game, ManagedComponentKind.RtxEncore)!;
        record.Metadata.Remove("RtxEncore.IntelXess");
        record.Metadata["RtxEncore.NeuralRenderingVersion"] = "310.8.0";
        var nr = record.Files.Single(f => Path.GetFileName(f.Path) == "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr.Path, oldNr);
        nr.Sha256 = FileHash.Sha256HexOf(oldNr);
        _records.Upsert(record);
        foreach (var file in Directory.GetFiles(Path.Combine(_bin, "rtx-encore-runtimes", "intel"))) File.Delete(file);

        var check = await service.CheckManagedUpdateAsync(_game);
        Assert.True(check.UpdateAvailable);
        Assert.Equal(RtxEncoreDeploymentService.UpgradeDueMessage, check.Message);

        var updated = await service.UpdateAsync(_game);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(NrAccepted, File.ReadAllBytes(nr.Path));
        Assert.True(File.Exists(Path.Combine(_bin, "rtx-encore-runtimes", "intel", "libxell.dll")));
        Assert.Equal("310.9.16", _records.Find(_game, ManagedComponentKind.RtxEncore)!.Metadata["RtxEncore.NeuralRenderingVersion"]);
        Assert.False((await service.CheckManagedUpdateAsync(_game)).UpdateAvailable);
    }

    // Upstream's RtxEncoreConfigTests sample: an edit changes only the value token, keeps comments and the newline style.
    private const string Sample =
        "{\r\n" +
        "  \"configVersion\": 4,                        // Settings apply live.\r\n" +
        "  \"renderingEngines\": {\r\n" +
        "    \"reconstructionEngine\": \"game\",          // game or xess. See https://example.com/a//b\r\n" +
        "    \"frameGenerationEngine\": \"game\"          // game, dlss-fg or xefg. Restart.\r\n" +
        "  },\r\n" +
        "  \"frameGeneration\": {\r\n" +
        "    \"mode\": \"game\",                          // fixed, dynamic or game.\r\n" +
        "    \"multiplier\": 4,                         // 2 to 6.\r\n" +
        "    \"flipMeteringBypass\": false              // Restart.\r\n" +
        "  },\r\n" +
        "  \"other\": {\r\n" +
        "    \"nested\": {                             // A nested object with a same-named key.\r\n" +
        "      \"multiplier\": 9\r\n" +
        "    },\r\n" +
        "    \"multiplier\": 7\r\n" +
        "  }\r\n" +
        "}\r\n";

    private static void ValidJsonc(string text)
    {
        using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    [Fact]
    public void Config_ExistingKey_ChangesOnlyItsValue()
    {
        var text = RtxEncoreConfig.Set(Sample, "renderingEngines", "frameGenerationEngine", RtxEncoreConfig.Token("xefg"));
        text = RtxEncoreConfig.Set(text, "frameGeneration", "multiplier", RtxEncoreConfig.Token("3"));
        text = RtxEncoreConfig.Set(text, "frameGeneration", "flipMeteringBypass", RtxEncoreConfig.Token("true"));

        Assert.Equal("xefg", RtxEncoreConfig.Get(text, "renderingEngines", "frameGenerationEngine"));
        Assert.Equal("3", RtxEncoreConfig.Get(text, "frameGeneration", "multiplier"));
        Assert.Equal("7", RtxEncoreConfig.Get(text, "other", "multiplier"));
        Assert.Equal("9", RtxEncoreConfig.Get(text, "other/nested", "multiplier"));
        var (before, after) = (Sample.Split("\r\n"), text.Split("\r\n"));
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(3, before.Zip(after).Count(pair => pair.First != pair.Second));
        Assert.Contains("// game, dlss-fg or xefg. Restart.", after[4]);
        Assert.Contains("https://example.com/a//b", after[3]);
        ValidJsonc(text);
    }

    [Fact]
    public void Config_MissingKeySectionAndFile_AreAdded()
    {
        var withKey = RtxEncoreConfig.Set(Sample, "frameGeneration", "dynamicExperimental56", "true");
        Assert.Equal("true", RtxEncoreConfig.Get(withKey, "frameGeneration", "dynamicExperimental56"));
        ValidJsonc(withKey);
        var withSection = RtxEncoreConfig.Set(Sample, "neuralRendering/optimizations", "nrPrecision", RtxEncoreConfig.Token("fast"));
        Assert.Equal("fast", RtxEncoreConfig.Get(withSection, "neuralRendering/optimizations", "nrPrecision"));
        ValidJsonc(withSection);
        var created = RtxEncoreConfig.Set(null, "xefg", "xefgUiHandling", RtxEncoreConfig.Token("game"));
        Assert.StartsWith(RtxEncoreConfig.CreatedHeader, created, StringComparison.Ordinal);
        Assert.Contains("  \"configVersion\": 4,", created.Split("\r\n"));
        Assert.Equal("game", RtxEncoreConfig.Get(created, "xefg", "xefgUiHandling"));
        ValidJsonc(created);
        Assert.Throws<InvalidDataException>(() => RtxEncoreConfig.Set("// no object", "a", "b", "1"));
    }

    // The ⚙: greyed out until installed; a choice is written into rtx-encore.jsonc at once; dependent rows follow
    // (Multiplier only in Fixed mode, the XeFG engine rows only with XeFG).
    [Fact]
    public void Settings_WriteTheJsonc_AndFollowTheirDependencies()
    {
        var path = Path.Combine(_bin, RtxEncoreConfig.FileName);
        File.WriteAllText(path, Sample);
        var sections = FgSettingsBuilder.RtxEncore(path, FgSettingsWriters.Encore(path));
        var rows = sections.SelectMany(section => section.Rows).ToArray();
        string? shown = null;
        foreach (var row in rows) { row.Changed = line => shown = line; row.Refresh(); }
        var (engine, mode, multiplier, xefgUi) = (sections[0].Rows[0], sections[0].Rows[1], sections[0].Rows[2], sections[1].Rows[2]);

        Assert.Equal(7, sections.Count); // Linux: no Smooth Motion section
        Assert.False(multiplier.IsEnabled);
        Assert.False(xefgUi.IsEnabled);
        mode.SelectedIndex = 1; // fixed
        engine.SelectedIndex = 2; // xefg
        foreach (var row in rows) row.Refresh();

        Assert.Equal(UiLocalizationService.Text("Fg.Saved"), shown);
        var text = File.ReadAllText(path);
        Assert.Equal(("fixed", "xefg"), (RtxEncoreConfig.Get(text, "frameGeneration", "mode"), RtxEncoreConfig.Get(text, "renderingEngines", "frameGenerationEngine")));
        Assert.True(multiplier.IsEnabled);
        Assert.True(xefgUi.IsEnabled);

        var frameLimit = sections[0].Rows.Single(row => row.IsNumber && row.Label == UiLocalizationService.Text("Enc.FrameLimit"));
        frameLimit.NumberText = "2000";
        Assert.Equal(UiLocalizationService.Format("Fg.NumberRange", frameLimit.Label, 0, 1000), shown);
        Assert.Equal("0", frameLimit.NumberText);
        frameLimit.NumberText = "141";
        Assert.Equal("141", RtxEncoreConfig.Get(File.ReadAllText(path), "nativeMenuFeatures", "reflexFrameLimit"));

        var notInstalled = FgSettingsBuilder.RtxEncore(null, FgSettingsWriters.NotInstalled).SelectMany(section => section.Rows).ToArray();
        foreach (var row in notInstalled) row.Refresh();
        Assert.All(notInstalled, row => Assert.False(row.IsEnabled));
        Assert.Contains(ComponentDialogLine.Warning(UiLocalizationService.Text("Enc.NotInstalled")), FgSettingsBuilder.RtxEncoreLines(false, false, null, null, gameRunning: false));
    }
}
