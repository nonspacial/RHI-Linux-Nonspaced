using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

/// <summary>#180: preset parsing, pack resolution, install / remove into a game through ReShade's record.</summary>
public sealed class ReShadePresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-preset-" + Guid.NewGuid().ToString("N"));
    private const string Preset = "[GENERAL]\nTechniques=SMAA@SMAA.fx,Clarity@qUINT_Clarity.fx,Blank,Tonemap@Tonemap.fx\nTechniqueSorting=SMAA@SMAA.fx\n\n[SMAA.fx]\nEdgeDetectionThreshold=0.1\n";

    public ReShadePresetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Parser_ReadsEveryTechniquesLine_AndRecognisesAPreset()
    {
        var effects = ReShadePresetService.RequiredEffects(Preset + "techniques = Extra@extra.FX\n");
        Assert.Equal(["SMAA.fx", "qUINT_Clarity.fx", "Tonemap.fx", "extra.FX"], effects.ToArray());
        Assert.True(ReShadePresetService.IsPreset(Preset));
        Assert.False(ReShadePresetService.IsPreset("[GENERAL]\nEffectSearchPaths=.\\x\n"));
        Assert.False(ReShadePresetService.IsPreset("Techniques=Name@notes.txt\n"));
    }

    [Fact]
    public void Resolve_MatchesEffectFileNamesInAnyCase_AndListsTheRest()
    {
        var packs = new Dictionary<string, IReadOnlyList<string>>
        {
            ["CrosireMaster"] = ["Shaders/smaa.fx", "Shaders/Tonemap.fx"],
            ["qUINT"] = [@"Shaders\qUINT_Clarity.fx"],
            ["Unused"] = ["Shaders/Other.fx"]
        };

        var (ids, unresolved) = ReShadePresetService.Resolve(["SMAA.fx", "qUINT_Clarity.fx", "Tonemap.fx", "Missing.fx"], packs);

        Assert.Equal(["CrosireMaster", "qUINT"], ids);
        Assert.Equal(["Missing.fx"], unresolved);
    }

    [Fact]
    public void StorePreset_KeepsAPresetInTheFolder_RefusesOtherFiles()
    {
        var folder = Path.Combine(_root, "inis", "reshade-presets");
        var source = Path.Combine(_root, "Mine.ini");
        File.WriteAllText(source, Preset);
        var notes = Path.Combine(_root, "notes.ini");
        File.WriteAllText(notes, "[x]\ny=1\n");
        var huge = Path.Combine(_root, "Huge.ini");
        using (var stream = File.Create(huge)) stream.SetLength(1024 * 1024 + 1);

        Assert.Equal(Path.Combine(folder, "Mine.ini"), ReShadePresetService.StorePreset(source, folder));
        Assert.Equal(Preset, File.ReadAllText(Path.Combine(folder, "Mine.ini")));
        Assert.StartsWith(ReShadePresetService.NotAPresetPrefix, Assert.Throws<InvalidDataException>(() => ReShadePresetService.StorePreset(notes, folder)).Message);
        Assert.StartsWith(ReShadePresetService.TooLargePrefix, Assert.Throws<InvalidDataException>(() => ReShadePresetService.StorePreset(huge, folder)).Message);
        Assert.Equal([Path.Combine(folder, "Mine.ini")], ReShadePresetService.ListPresets(folder));
    }

    [Fact]
    public void StorePreset_NeverOverwritesAPresetInTheFolder_SameContentIsReused_OtherContentGetsANumberedName()
    {
        // Review finding 3: ReShade names every preset ReShadePreset.ini; a second game's drop must not wipe the first.
        var folder = Path.Combine(_root, "inis", "reshade-presets");
        Directory.CreateDirectory(folder);
        var mine = Path.Combine(folder, "ReShadePreset.ini");
        File.WriteAllText(mine, "Techniques=Mine@Mine.fx\nMyTweak=1\n");
        var drop = Path.Combine(_root, "drop", "ReShadePreset.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(drop)!);
        File.WriteAllText(drop, Preset);
        var same = Path.Combine(_root, "same", "reshadepreset.INI"); // same content, another case
        Directory.CreateDirectory(Path.GetDirectoryName(same)!);
        File.WriteAllText(same, "Techniques=Mine@Mine.fx\nMyTweak=1\n");

        var stored = ReShadePresetService.StorePreset(drop, folder);
        var again = ReShadePresetService.StorePreset(drop, folder);
        var reused = ReShadePresetService.StorePreset(same, folder);

        Assert.Equal("Techniques=Mine@Mine.fx\nMyTweak=1\n", File.ReadAllText(mine));
        Assert.Equal(Path.Combine(folder, "ReShadePreset (2).ini"), stored);
        Assert.Equal(Preset, File.ReadAllText(stored));
        Assert.Equal(stored, again);
        Assert.Equal(mine, reused);
        Assert.Equal([mine, stored], ReShadePresetService.ListPresets(folder).OrderBy(x => x.Length).ToArray());

        // A name taken in another case counts as taken (Wine finds either).
        var lower = Path.Combine(_root, "lower", "reshadepreset.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(lower)!);
        File.WriteAllText(lower, "Techniques=Other@Other.fx\n");
        Assert.Equal(Path.Combine(folder, "reshadepreset (3).ini"), ReShadePresetService.StorePreset(lower, folder));
        Assert.Equal("Techniques=Mine@Mine.fx\nMyTweak=1\n", File.ReadAllText(mine));
    }

    [Fact]
    public async Task Install_ANameTakenInAnotherCase_IsTheSameName()
    {
        // Review finding 4: the game holds the user's ReShadePreset.ini; a preset called reshadepreset.ini must not land
        // beside it as a second file Wine might load instead.
        var (game, records, _) = Setup();
        var theirs = Path.Combine(game.BinaryDirectory!, "ReShadePreset.ini");
        File.WriteAllText(theirs, "Techniques=Theirs@Theirs.fx\n");
        var preset = Path.Combine(_root, "reshadepreset.ini");
        File.WriteAllText(preset, Preset);
        var service = new ReShadePresetService(records, null, _ => null);

        var refused = await service.InstallAsync(game, preset);

        Assert.Equal(ReShadePresetService.NameTakenPrefix + theirs, refused.Message);
        Assert.Equal(["ReShade.ini", "ReShadePreset.ini"], Directory.EnumerateFiles(game.BinaryDirectory!).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());

        // The same content under the other case is RHI's to take over: it keeps the game's spelling.
        File.WriteAllText(theirs, Preset);
        var installed = await service.InstallAsync(game, preset);
        Assert.True(installed.Success, installed.Message);
        Assert.Equal(["ReShade.ini", "ReShadePreset.ini"], Directory.EnumerateFiles(game.BinaryDirectory!).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("ReShadePreset.ini", Assert.Single(service.Installed(game)).FileName);
        Assert.Contains(@"PresetPath=.\ReShadePreset.ini", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
    }

    [Fact]
    public void Classifier_OffersADroppedIniAsAPreset()
        => Assert.Equal(LinuxDroppedFileKind.ReShadePreset, LinuxDroppedFileClassifier.Classify("/x/Mine.INI"));

    private (DetectedGame Game, LinuxDeploymentRecordStore Records, string Preset) Setup(bool managed = true)
    {
        var bin = Path.Combine(_root, "game", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\x\n");
        var game = new DetectedGame { Name = "Preset Game", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "g.exe"), Runner = "Proton", SteamAppId = 4100 };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        if (managed)
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.ReShade,
                BinaryDirectory = bin,
                Files = [new LinuxDeployedFile { Path = Path.Combine(bin, "dxgi.dll"), Sha256 = "00" }],
                Metadata = new(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = "Stable" }
            });
        var preset = Path.Combine(_root, "Mine.ini");
        File.WriteAllText(preset, Preset);
        return (game, records, preset);
    }

    [Fact]
    public async Task Install_CopiesTheFile_SelectsItInReShadeIni_AndRecordsIt()
    {
        var (game, records, preset) = Setup();
        var service = new ReShadePresetService(records, null, _ => null);

        var result = await service.InstallAsync(game, preset);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Preset, File.ReadAllText(Path.Combine(game.BinaryDirectory!, "Mine.ini")));
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        Assert.Contains(@"EffectSearchPaths=.\x", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        var installed = Assert.Single(service.Installed(game));
        Assert.Equal("Mine.ini", installed.FileName);
        Assert.Equal("Stable", records.Find(game, ManagedComponentKind.ReShade)!.Metadata["ReShade.Channel"]); // the rest of the record stays
    }

    [Fact]
    public async Task Install_Refused_WithoutRhiReShade_OrByTheWriteGuard_WritesNothing()
    {
        var (game, records, preset) = Setup(managed: false);
        var unmanaged = await new ReShadePresetService(records, null, _ => null).InstallAsync(game, preset);
        Assert.StartsWith(ReShadePresetService.NoManagedReShadePrefix, unmanaged.Message);

        var (game2, records2, preset2) = Setup();
        var guarded = await new ReShadePresetService(records2, null, _ => "busy").InstallAsync(game2, preset2);
        Assert.Equal("busy", guarded.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Mine.ini")));
        Assert.DoesNotContain("PresetPath", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
    }

    [Fact]
    public async Task Install_NeverOverwritesAFileRhiDidNotWrite_OrOneTheUserChanged()
    {
        var (game, records, preset) = Setup();
        var service = new ReShadePresetService(records, null, _ => null);
        var target = Path.Combine(game.BinaryDirectory!, "Mine.ini");
        File.WriteAllText(target, "[GENERAL]\nTechniques=Mine@Mine.fx\n");

        var foreign = await service.InstallAsync(game, preset);
        Assert.StartsWith(ReShadePresetService.NameTakenPrefix, foreign.Message);
        Assert.Equal("[GENERAL]\nTechniques=Mine@Mine.fx\n", File.ReadAllText(target));

        File.Delete(target);
        Assert.True((await service.InstallAsync(game, preset)).Success);
        File.WriteAllText(preset, Preset + "[New.fx]\nx=1\n");
        Assert.True((await service.InstallAsync(game, preset)).Success, "RHI's own unchanged copy is replaced by the newer preset");
        Assert.Contains("[New.fx]", File.ReadAllText(target));

        File.AppendAllText(target, "UserTweak=1\n"); // the user adjusts it in the overlay
        File.WriteAllText(preset, Preset);
        Assert.StartsWith(ReShadePresetService.NameTakenPrefix, (await service.InstallAsync(game, preset)).Message);
        Assert.Contains("UserTweak=1", File.ReadAllText(target));
    }

    [Fact]
    public async Task Install_RecordSaveFails_TheCopyAndTheIniGoBack()
    {
        var (game, records, preset) = Setup();
        var ini = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        var before = File.ReadAllText(ini);
        records.SaveFaultForTesting = () => new IOException("disk full (test)");

        var result = await new ReShadePresetService(records, null, _ => null).InstallAsync(game, preset);

        records.SaveFaultForTesting = null;
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Mine.ini")));
        Assert.Equal(before, File.ReadAllText(ini));
        Assert.Empty(new ReShadePresetService(records, null, _ => null).Installed(game));
    }

    [Fact]
    public async Task Remove_DeletesAnUnchangedCopy_KeepsAChangedOne_AndClearsOnlyItsPresetPath()
    {
        var (game, records, preset) = Setup();
        var service = new ReShadePresetService(records, null, _ => null);
        await service.InstallAsync(game, preset);
        var other = Path.Combine(_root, "Other.ini");
        File.WriteAllText(other, Preset);
        await service.InstallAsync(game, other); // PresetPath now names Other.ini

        var removed = await service.RemoveAsync(game, "Mine.ini");
        Assert.StartsWith(ReShadePresetService.RemovedPrefix, removed.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Mine.ini")));
        Assert.Contains(@"PresetPath=.\Other.ini", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));

        File.AppendAllText(Path.Combine(game.BinaryDirectory!, "Other.ini"), "UserTweak=1\n");
        var kept = await service.RemoveAsync(game, "Other.ini");
        Assert.StartsWith(ReShadePresetService.KeptChangedPrefix, kept.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "Other.ini")));
        Assert.DoesNotContain("PresetPath", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        Assert.Empty(service.Installed(game));
        Assert.False(records.Find(game, ManagedComponentKind.ReShade)!.Metadata.ContainsKey(ReShadePresetService.MetadataKey));
        Assert.StartsWith(ReShadePresetService.NotInstalledPrefix, (await service.RemoveAsync(game, "Other.ini")).Message);
    }

    [Fact]
    public void Installed_IgnoresEntriesThatNameAnotherFolder()
    {
        var record = new LinuxDeploymentRecord { Metadata = new(StringComparer.OrdinalIgnoreCase) };
        record.Metadata[ReShadePresetService.MetadataKey] = JsonSerializer.Serialize(new[]
        {
            new InstalledReShadePreset("../../evil.ini", "00"),
            new InstalledReShadePreset("Fine.ini", "00")
        });
        Assert.Equal(["Fine.ini"], ReShadePresetService.Read(record).Select(p => p.FileName).ToArray());
    }
}
