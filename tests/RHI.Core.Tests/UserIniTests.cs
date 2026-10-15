using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#181: user INI templates, the template merge into ReShade.ini, Deploy X.ini and the "all games" batches.</summary>
public sealed class UserIniTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-user-ini-tests-" + Guid.NewGuid().ToString("N"));

    public UserIniTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private UserIniTemplates Templates() => new(Path.Combine(_root, "inis"));

    private DetectedGame Game(string name = "Demo Game", string runner = "Proton")
    {
        var bin = Path.Combine(_root, "games", name.Replace(' ', '_'), "bin");
        Directory.CreateDirectory(bin);
        return new DetectedGame
        {
            Name = name,
            InstallPath = Path.GetDirectoryName(bin)!,
            BinaryDirectory = bin,
            ExecutablePath = Path.Combine(bin, "Game.exe"),
            Runner = runner,
            SteamAppId = Math.Abs(name.GetHashCode()) % 100000 + 1
        };
    }

    private static string Ini(DetectedGame game) => Path.Combine(game.BinaryDirectory!, "ReShade.ini");

    // ---- templates ----------------------------------------------------------------------------

    [Fact]
    public void Templates_AreSeededOnceAndNeverOverwritten()
    {
        var templates = Templates();
        var seeded = templates.SeedAll();

        Assert.Equal(5, seeded.Count);
        Assert.Equal(["DisplayCommander.ini", "relimiter.ini", "reshade.ini", "reshade.rdr2.ini", "reshade.vulkan.ini"],
            Directory.EnumerateFiles(templates.Directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("VariableListUseTabs=1", File.ReadAllText(templates.PathOf(UserIniTemplate.ReShade)));
        Assert.Contains("KeyOverlay=35,0,0,0", File.ReadAllText(templates.PathOf(UserIniTemplate.ReShadeRdr2)));
        Assert.Equal("[UltraLimiter]\n", File.ReadAllText(templates.PathOf(UserIniTemplate.ReLimiter)));

        File.WriteAllText(templates.PathOf(UserIniTemplate.ReShade), "[OVERLAY]\nMine=1\n");
        templates.SeedAll();
        Assert.Equal("[OVERLAY]\nMine=1\n", File.ReadAllText(templates.PathOf(UserIniTemplate.ReShade)));
        Assert.Equal("[OVERLAY]\nMine=1\n", System.Text.Encoding.UTF8.GetString(templates.Read(UserIniTemplate.ReShade, out var note)));
        Assert.Null(note);
    }

    [Fact]
    public void Templates_ShippedCopiesCarryNoByteOrderMark()
    {
        foreach (var template in Enum.GetValues<UserIniTemplate>())
            Assert.NotEqual(0xEF, UserIniTemplates.Shipped(template)[0]);
    }

    [Fact]
    public void Templates_UnwritableFolder_ReadFallsBackToTheShippedCopy()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var templates = new UserIniTemplates(blocker);

        var bytes = templates.Read(UserIniTemplate.ReShade, out var note);

        Assert.Equal(UserIniTemplates.Shipped(UserIniTemplate.ReShade), bytes);
        Assert.NotNull(note);
    }

    [Fact]
    public void Templates_OversizedFile_IsNotUsed()
    {
        var templates = Templates();
        Directory.CreateDirectory(templates.Directory);
        using (var stream = File.Create(templates.PathOf(UserIniTemplate.ReShade))) stream.SetLength(1024 * 1024 + 1);

        var bytes = templates.Read(UserIniTemplate.ReShade, out var note);

        Assert.Equal(UserIniTemplates.Shipped(UserIniTemplate.ReShade), bytes);
        Assert.StartsWith(UserIniTemplates.TooLargePrefix, note);
    }

    [Theory]
    [InlineData("Red Dead Redemption 2", UserIniTemplate.ReShadeRdr2)]
    [InlineData("RDR2", UserIniTemplate.ReShadeRdr2)]
    [InlineData("Max Payne 3", UserIniTemplate.ReShadeRdr2)]
    [InlineData("Red Dead Redemption", UserIniTemplate.ReShade)]
    [InlineData("Cyberpunk 2077", UserIniTemplate.ReShade)]
    public void ReShadeTemplateFor_PicksUpstreamsDedicatedTemplate(string name, UserIniTemplate expected)
        => Assert.Equal(expected, UserIniTemplates.ReShadeTemplateFor(new DetectedGame { Name = name, InstallPath = _root }));

    // ---- install defaults ---------------------------------------------------------------------

    [Fact]
    public void InstallDefaults_WriteOnlyWhatDiffersFromReShadesOwnDefaults()
    {
        var game = Game();
        var plain = ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), game, Templates());
        Assert.Null(plain.OverlayHotkey);
        Assert.Null(plain.ScreenshotHotkey);
        Assert.Null(plain.VariableListUseTabs);
        Assert.Null(plain.ScreenshotSavePath);
        Assert.False(plain.SkipPreprocessorDefinitions);

        var custom = ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings
        {
            OverlayHotkey = "45,0,0,0",
            ScreenshotHotkey = "46,0,0,0",
            ReShadeVariableListUseTabs = false,
            ScreenshotPath = "/home/u/Shots"
        }, game, Templates());
        Assert.Equal("45,0,0,0", custom.OverlayHotkey);
        Assert.Equal("46,0,0,0", custom.ScreenshotHotkey);
        Assert.False(custom.VariableListUseTabs);
        Assert.Equal(@"Z:\home\u\Shots", custom.ScreenshotSavePath);
    }

    [Fact]
    public void InstallDefaults_Rdr2_UsesItsTemplateWithoutVulkanDepthSettings_ExceptForTheVulkanLayer()
    {
        var rdr2 = Game("Red Dead Redemption 2");
        var dx = ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), rdr2, Templates());
        Assert.True(dx.SkipPreprocessorDefinitions);
        Assert.Contains("KeyOverlay=35,0,0,0", System.Text.Encoding.UTF8.GetString(dx.Template!));

        var vulkan = ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), rdr2, Templates(), vulkan: true);
        Assert.False(vulkan.SkipPreprocessorDefinitions);

        var other = ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), Game("Doom"), Templates(), vulkan: true);
        Assert.Contains("PreprocessorDefinitions=", System.Text.Encoding.UTF8.GetString(other.Template!));
    }

    // ---- template merge -----------------------------------------------------------------------

    [Fact]
    public void Apply_NewFile_IsACopyOfTheTemplatePlusRhiPaths()
    {
        var game = Game();
        var templates = Templates();
        Directory.CreateDirectory(templates.Directory);
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReShade), "; my template\n[GENERAL]\nEffectSearchPaths=.\\mine\\**\n\n[OVERLAY]\nTutorialProgress=4\n");
        var effect = @".\reshade-shaders\RHI\_Core\Shaders\**";

        var result = ReShadeIniManager.Apply(game.BinaryDirectory!, [effect], [],
            ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), game, templates));

        Assert.True(result.CreatedByRhi);
        Assert.Equal($"; my template\n[GENERAL]\nEffectSearchPaths=.\\mine\\**,{effect}\n\n[OVERLAY]\nTutorialProgress=4\n", File.ReadAllText(result.Path));
        Assert.Equal([effect], result.AddedEffectPaths);
    }

    [Fact]
    public void Apply_ExistingFile_TemplateKeysWin_OtherLinesStay_PathsAreAddedAndTakenBackOnCleanup()
    {
        var game = Game();
        File.WriteAllText(Ini(game), "; user\n[ADDON]\nDisabledAddons=Generic Depth\nAddonPath=.\\addons\n[GENERAL]\nEffectSearchPaths=.\\user\\**\n[INPUT]\nKeyOverlay=50,0,0,0\n");

        var result = ReShadeIniManager.Apply(game.BinaryDirectory!, [], [],
            ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), game, Templates()));

        var text = File.ReadAllText(Ini(game));
        Assert.Contains("; user", text);
        Assert.Contains("AddonPath=.\\addons", text);
        Assert.Contains("DisabledAddons=Generic Depth,Effect Runtime Sync", text);
        Assert.Contains(@"EffectSearchPaths=.\user\**,.\reshade-shaders\Shaders\**", text);
        Assert.Contains(@"TextureSearchPaths=.\reshade-shaders\Textures\**", text);
        Assert.Contains("VariableListUseTabs=1", text);
        Assert.Contains("KeyOverlay=50,0,0,0", text); // a default-Home setting never overwrites the game's key
        Assert.False(result.CreatedByRhi);

        ReShadeIniManager.Cleanup(result.Path, result.CreatedByRhi, result.Sha256, result.AddedEffectPaths, result.AddedTexturePaths,
            result.EffectKeyCreated, result.TextureKeyCreated);
        var cleaned = File.ReadAllText(Ini(game));
        Assert.Contains(@"EffectSearchPaths=.\user\**", cleaned);
        Assert.DoesNotContain(@"reshade-shaders\Shaders", cleaned);
        Assert.DoesNotContain("TextureSearchPaths", cleaned);
    }

    [Fact]
    public void Apply_Rdr2OnDirectX_LeavesTheTemplatesDepthSettingsOut_ButKeepsTheGamesOwn()
    {
        var fresh = Game("Red Dead Redemption 2");
        ReShadeIniManager.Apply(fresh.BinaryDirectory!, [], [], ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), fresh, Templates()));
        var text = File.ReadAllText(Ini(fresh));
        Assert.DoesNotContain("PreprocessorDefinitions", text);
        Assert.Contains("KeyOverlay=35,0,0,0", text);

        var existing = Game("Max Payne 3");
        File.WriteAllText(Ini(existing), "[GENERAL]\nPreprocessorDefinitions=MINE=1\n");
        ReShadeIniManager.Apply(existing.BinaryDirectory!, [], [], ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), existing, Templates()));
        Assert.Contains("PreprocessorDefinitions=MINE=1", File.ReadAllText(Ini(existing)));
    }

    [Fact]
    public void Apply_ReadOnlyReShadeIni_IsWrittenAndStaysLocked()
    {
        var game = Game();
        File.WriteAllText(Ini(game), "[OVERLAY]\nTutorialProgress=0\n");
        File.SetUnixFileMode(Ini(game), UnixFileMode.UserRead);

        ReShadeIniManager.Apply(game.BinaryDirectory!, [], [], ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), game, Templates()));

        Assert.Contains("TutorialProgress=4", File.ReadAllText(Ini(game)));
        Assert.False(File.GetUnixFileMode(Ini(game)).HasFlag(UnixFileMode.UserWrite));
    }

    [Fact]
    public void Apply_WithoutDefaults_KeepsTheOldConservativeBehaviour()
    {
        var game = Game();
        File.WriteAllText(Ini(game), "[ADDON]\nDisabledAddons=\n");
        ReShadeIniManager.Apply(game.BinaryDirectory!, [], []);
        Assert.Equal("[ADDON]\nDisabledAddons=\n", File.ReadAllText(Ini(game)));
    }

    // ---- apply to game / all games --------------------------------------------------------------

    [Fact]
    public void ApplyToGame_WritesEffectListStyle_AndEveryNumberedIni_ButNotThePreset()
    {
        var game = Game();
        File.WriteAllText(Ini(game), "[OVERLAY]\nVariableListUseTabs=1\n");
        var second = Path.Combine(game.BinaryDirectory!, "ReShade2.ini");
        File.WriteAllText(second, "[INPUT]\nKeyOverlay=36,0,0,0\n");
        var preset = Path.Combine(game.BinaryDirectory!, "ReShadePreset.ini");
        File.WriteAllText(preset, "Techniques=SMAA@SMAA.fx\n");

        ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings { ReShadeVariableListUseTabs = false, OverlayHotkey = "45,0,0,0" }, game);

        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Ini(game)));
        Assert.Contains("KeyOverlay=45,0,0,0", File.ReadAllText(second));
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(second));
        Assert.Equal("Techniques=SMAA@SMAA.fx\n", File.ReadAllText(preset));
    }

    [Fact]
    public void ApplyToGame_Rdr2_KeepsItsOverlayKey()
    {
        var game = Game("Red Dead Redemption 2");
        File.WriteAllText(Ini(game), "[INPUT]\nKeyOverlay=35,0,0,0\n");
        ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings { OverlayHotkey = "45,0,0,0", ScreenshotHotkey = "46,0,0,0" }, game);
        var text = File.ReadAllText(Ini(game));
        Assert.Contains("KeyOverlay=35,0,0,0", text);
        Assert.Contains("KeyScreenshot=46,0,0,0", text);
    }

    private UserIniService Service(Func<DetectedGame, string?>? refusal = null, LinuxDeploymentRecordStore? records = null)
        => new(Templates(), records ?? new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), refusal ?? (_ => null));

    [Fact]
    public async Task ApplyToAll_WritesEveryGameWithRhisReShadeAndAReShadeIni_CountsRefusedAndSkipsTheRest()
    {
        var a = Game("A");
        var b = Game("B");
        var locked = Game("Locked");
        var none = Game("None");
        var native = Game("Native", runner: "Native Linux");
        var external = Game("External"); // the user's own ReShade: left alone (review finding 1)
        foreach (var game in new[] { a, b, locked, native, external }) File.WriteAllText(Ini(game), "[OVERLAY]\n");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        foreach (var game in new[] { a, b, locked, none, native }) records.Manage(game, ManagedComponentKind.ReShade);
        var service = Service(game => game.Name == "Locked" ? "busy" : null, records);

        Assert.Equal([a, b, locked, native], service.ReShadeSettingsTargets([a, b, locked, none, native, external]));
        var result = await service.ApplyReShadeSettingsToAllAsync([a, b, locked, none, native, external], new LinuxAppSettings { ReShadeVariableListUseTabs = false });

        Assert.Equal(new IniBatchResult(2, 2, 0), result);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Ini(a)));
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Ini(b)));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Ini(locked)));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Ini(native)));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Ini(external)));
        Assert.False(File.Exists(Ini(none)));
    }

    [Fact]
    public async Task ApplyToAll_CancelStopsBetweenGames_TheGameInHandFinishes()
    {
        var a = Game("A");
        var b = Game("B");
        foreach (var game in new[] { a, b }) File.WriteAllText(Ini(game), "[OVERLAY]\n");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")).Manage(a, ManagedComponentKind.ReShade).Manage(b, ManagedComponentKind.ReShade);
        using var cts = new CancellationTokenSource();
        // The user presses Cancel the moment A's write starts: A finishes, B is never started.
        OperationCancellation.WritePhaseObserverForTesting = _ => cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Service(records: records).ApplyReShadeSettingsToAllAsync([a, b], new LinuxAppSettings { ReShadeVariableListUseTabs = false }, cts.Token));
        }
        finally { OperationCancellation.WritePhaseObserverForTesting = null; }

        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Ini(a)));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Ini(b)));
    }

    [Fact]
    public async Task ApplyToAll_OneUnwritableGame_FailsAloneAndTheOthersAreWritten()
    {
        var bad = Game("Bad");
        var good = Game("Good");
        var outside = Path.Combine(_root, "outside", "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "[OVERLAY]\n");
        File.CreateSymbolicLink(Ini(bad), outside); // a link out of the game folder is refused
        File.WriteAllText(Ini(good), "[OVERLAY]\n");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")).Manage(bad, ManagedComponentKind.ReShade).Manage(good, ManagedComponentKind.ReShade);

        var result = await Service(records: records).ApplyReShadeSettingsToAllAsync([bad, good], new LinuxAppSettings { ReShadeVariableListUseTabs = false });

        Assert.Equal(new IniBatchResult(1, 0, 1), result);
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(outside));
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Ini(good)));
    }

    [Fact]
    public void Deploy_WithoutRhisComponent_IsRefusedAndTheUsersFilesStay()
    {
        // Review finding 1: a ReLimiter / Display Commander / ReShade the user installed is theirs.
        var game = Game();
        var templates = Templates();
        Directory.CreateDirectory(templates.Directory);
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReLimiter), "[FrameLimiter]\ntarget_fps=117\n");
        var relimiter = Path.Combine(game.BinaryDirectory!, "ReLimiter.ini");
        File.WriteAllText(relimiter, "[FrameLimiter]\nmy_tuning=1\n");
        File.WriteAllText(Ini(game), "[OVERLAY]\nMine=1\n");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json"));
        var service = new UserIniService(templates, records, _ => null);

        var addon = service.DeployAddonIni(game, ManagedComponentKind.ReLimiter);
        var reshade = service.DeployReShadeIni(game, new LinuxAppSettings());

        Assert.False(addon.Success);
        Assert.Equal(UserIniService.NotManagedPrefix + game.Name, addon.Message);
        Assert.Equal(UserIniService.NotManagedPrefix + game.Name, reshade.Message);
        Assert.Equal("[FrameLimiter]\nmy_tuning=1\n", File.ReadAllText(relimiter));
        Assert.Equal("[OVERLAY]\nMine=1\n", File.ReadAllText(Ini(game)));

        // RHI's ReShade alone does not open the ReLimiter file.
        records.Manage(game, ManagedComponentKind.ReShade);
        Assert.False(service.DeployAddonIni(game, ManagedComponentKind.ReLimiter).Success);
        Assert.Equal("[FrameLimiter]\nmy_tuning=1\n", File.ReadAllText(relimiter));
        Assert.True(service.DeployReShadeIni(game, new LinuxAppSettings()).Success);
    }

    [Fact]
    public void DeployAddonIni_WhereRhiManagesIt_ReplacesTheGamesFileWithTheTemplate()
    {
        var game = Game();
        var templates = Templates();
        Directory.CreateDirectory(templates.Directory);
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReLimiter), "[FrameLimiter]\ntarget_fps=117\n");
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReLimiter.ini"), "[FrameLimiter]\nold=1\n");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json")).Manage(game, ManagedComponentKind.ReLimiter);

        var result = new UserIniService(templates, records, _ => null).DeployAddonIni(game, ManagedComponentKind.ReLimiter);

        Assert.True(result.Success, result.Message);
        Assert.Equal("[FrameLimiter]\ntarget_fps=117\n", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReLimiter.ini")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "relimiter.ini")) &&
                     !File.Exists(Path.Combine(game.BinaryDirectory!, "ReLimiter.ini")));
    }

    [Fact]
    public void Deploy_RefusedGame_WritesNothing()
    {
        var game = Game();
        var service = Service(_ => "Alvo de modificações não definido.");

        var reshade = service.DeployReShadeIni(game, new LinuxAppSettings());
        var addon = service.DeployAddonIni(game, ManagedComponentKind.DisplayCommander);

        Assert.False(reshade.Success);
        Assert.False(addon.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(game.BinaryDirectory!));
        Assert.StartsWith(UserIniService.NativeGamePrefix, Service().DeployReShadeIni(Game("N", "Native Linux"), new LinuxAppSettings()).Message);
    }

    [Fact]
    public async Task DeployToAll_OnlyGamesWhereRhiManagesTheComponent()
    {
        var managed = Game("Managed");
        var other = Game("Other");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(managed),
            GameName = managed.Name,
            Component = ManagedComponentKind.DisplayCommander,
            BinaryDirectory = managed.BinaryDirectory!,
            Files = []
        });

        var result = await Service(records: records).DeployToAllAsync([managed, other], ManagedComponentKind.DisplayCommander, new LinuxAppSettings());

        Assert.Equal(new IniBatchResult(1, 0, 0), result);
        Assert.Equal("[DisplayCommander]\n", File.ReadAllText(Path.Combine(managed.BinaryDirectory!, "DisplayCommander.ini")));
        Assert.False(File.Exists(Path.Combine(other.BinaryDirectory!, "DisplayCommander.ini")));
    }

    [Fact]
    public void Deploy_EntersTheWritePhaseBeforeItWrites()
    {
        var game = Game();
        var phases = 0;
        OperationCancellation.WritePhaseObserverForTesting = _ => phases++;
        try
        {
            using var op = new OperationCancellation();
            var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")).Manage(game, ManagedComponentKind.ReShade);
            Service(records: records).DeployReShadeIni(game, new LinuxAppSettings(), op.Token);
            Assert.Equal(1, phases);
        }
        finally { OperationCancellation.WritePhaseObserverForTesting = null; }
    }
}
