using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #282 (upstream 2.8.9 Display Commander ⚙): the main settings are read from and written to the game's
/// DisplayCommander.ini under [DisplayCommander], with Display Commander's key names and upstream's value lists; one key
/// per change, every other line kept; a value Display Commander does not take changes nothing; journaled on DC's record.
/// </summary>
public sealed class DisplayCommanderSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dc-settings-" + Guid.NewGuid().ToString("N"));

    public DisplayCommanderSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Read_AbsentKeysAreDisplayCommandersDefaults()
    {
        var (game, records, _) = Fixture("defaults", "[DisplayCommander]\n");
        var settings = new DisplayCommanderSettingsService(records, null, _ => null).Read(game);

        Assert.Equal(0, settings.FpsLimit);
        Assert.Equal(0, settings.FpsLimiterMode);
        Assert.False(settings.InjectReflex);
        Assert.Equal(0, settings.WindowMode);
        Assert.False(settings.ReflexEnableSleep);
        Assert.Equal(1, settings.WinUpGraceSeconds); // Display Commander's default
        Assert.True(settings.IniExists);
    }

    [Fact]
    public async Task Set_WritesOneKey_KeepsEveryOtherLine_AndReadsBack()
    {
        var (game, records, ini) = Fixture("write", "; user note\n[DisplayCommander]\nTargetDisplay=DP-1\nfps_limit=0\n[Other]\nx=1\n");
        var service = new DisplayCommanderSettingsService(records, null, _ => null);

        Assert.True((await service.SetAsync(game, DisplayCommanderSettingsService.FpsLimitKey, "116")).Success);
        Assert.True((await service.SetAsync(game, DisplayCommanderSettingsService.WindowModeKey, "3")).Success);
        Assert.True((await service.SetAsync(game, DisplayCommanderSettingsService.ReflexEnableSleepKey, "1")).Success);
        Assert.True((await service.SetAsync(game, DisplayCommanderSettingsService.FpsLimitKey, "239,9904")).Success); // custom, 3 decimals

        var text = File.ReadAllText(ini);
        Assert.Contains("; user note", text);
        Assert.Contains("TargetDisplay=DP-1", text);
        Assert.Contains("[Other]\nx=1", text);
        var settings = service.Read(game);
        Assert.Equal(239.99, settings.FpsLimit);
        Assert.Equal(3, settings.WindowMode);
        Assert.True(settings.ReflexEnableSleep);
        Assert.Contains("fps_limit=239.99\n", text);
    }

    [Theory]
    [InlineData(DisplayCommanderSettingsService.FpsLimitKey, "10")]       // below 20
    [InlineData(DisplayCommanderSettingsService.FpsLimitKey, "1000.5")]   // above 1000
    [InlineData(DisplayCommanderSettingsService.FpsLimiterModeKey, "2")]
    [InlineData(DisplayCommanderSettingsService.WindowModeKey, "4")]
    [InlineData(DisplayCommanderSettingsService.WinUpGraceSecondsKey, "3")]
    [InlineData(DisplayCommanderSettingsService.InjectReflexKey, "2")]
    [InlineData("TargetDisplay", "1")]                                    // not a ⚙ key: never written
    public async Task Set_AValueDisplayCommanderDoesNotTake_ChangesNothing(string key, string value)
    {
        var (game, records, ini) = Fixture("invalid-" + key + value, "[DisplayCommander]\nfps_limit=60\n");
        var before = File.ReadAllBytes(ini);

        var result = await new DisplayCommanderSettingsService(records, null, _ => null).SetAsync(game, key, value);

        Assert.False(result.Success);
        Assert.StartsWith(DisplayCommanderSettingsService.InvalidValuePrefix, result.Message);
        Assert.Equal(before, File.ReadAllBytes(ini));
    }

    [Fact]
    public async Task Set_NoIniYet_CreatesItWithTheSection_AndNotManagedOrRefused_ChangesNothing()
    {
        var (game, records, ini) = Fixture("absent", null);
        var service = new DisplayCommanderSettingsService(records, null, _ => null);
        Assert.False(service.Read(game).IniExists);

        Assert.True((await service.SetAsync(game, DisplayCommanderSettingsService.InjectReflexKey, "1")).Success);
        Assert.Equal("[DisplayCommander]\ninject_reflex=1\n", File.ReadAllText(ini));

        var refused = await new DisplayCommanderSettingsService(records, null, _ => "busy").SetAsync(game, DisplayCommanderSettingsService.InjectReflexKey, "0");
        Assert.Equal("busy", refused.Message);
        records.Remove(game, ManagedComponentKind.DisplayCommander);
        var notManaged = await service.SetAsync(game, DisplayCommanderSettingsService.InjectReflexKey, "0");
        Assert.StartsWith(DisplayCommanderSettingsService.NotManagedPrefix, notManaged.Message);
        Assert.Equal("[DisplayCommander]\ninject_reflex=1\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task Set_IsJournaledOnDisplayCommandersRecord_AndACrashBeforeTheSavePutsTheFileBack()
    {
        var (game, records, ini) = Fixture("crash", "[DisplayCommander]\nfps_limit=60\n");
        var (journal, _) = SimulatedDeath.NewJournal(Path.Combine(_root, "crash-journal"), records);
        var before = File.ReadAllBytes(ini);
        var service = new DisplayCommanderSettingsService(records, journal, _ => null);

        using (var death = new SimulatedDeath(journal))
        {
            records.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => service.SetAsync(game, DisplayCommanderSettingsService.FpsLimitKey, "144"));
            Assert.True(died);
            Assert.Contains("fps_limit=144", File.ReadAllText(ini));
            var report = await SimulatedDeath.RecoverAsync(Path.Combine(_root, "crash-journal"), Path.Combine(_root, "crash-records.json"));
            Assert.Equal(1, report!.RolledBack);
        }
        Assert.Equal(before, File.ReadAllBytes(ini));
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Records, string Ini) Fixture(string name, string? ini)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "DC " + name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Test" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        var addon = Path.Combine(bin, "zzz_display_commander.addon64");
        File.WriteAllText(addon, "dc");
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DisplayCommander,
            BinaryDirectory = bin, Version = "1", Files = [new LinuxDeployedFile { Path = addon }]
        });
        var path = Path.Combine(bin, "DisplayCommander.ini");
        if (ini is not null) File.WriteAllText(path, ini);
        return (game, records, path);
    }
}
