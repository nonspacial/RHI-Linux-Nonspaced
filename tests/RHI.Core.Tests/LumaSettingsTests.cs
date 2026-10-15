using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#189: Luma ⚙ — HDR on first boot (reshade.ini [Luma]) and TAA (Engine.ini), owned on Luma's record.</summary>
public sealed class LumaSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-settings-" + Guid.NewGuid().ToString("N"));

    public LumaSettingsTests() => Directory.CreateDirectory(_root);

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

    private string Bin(string ini = "[GENERAL]\nx=1\n")
    {
        var bin = Path.Combine(_root, "game", "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), ini);
        return bin;
    }

    [Fact]
    public void Hdr_InstallThenOffThenDefaultThenOn_FollowsUpstreamAndRemovalRestores()
    {
        var bin = Bin("[Luma]\nDisplayMode=2\n");
        var ini = Path.Combine(bin, "ReShade.ini");
        var installed = LumaIniOverrideManager.Apply(bin); // the install writes 1/1, as upstream
        Assert.Equal(LumaHdrMode.On, LumaIniOverrideManager.ReadHdrMode(bin));

        var off = LumaIniOverrideManager.SetHdrMode(bin, installed, LumaHdrMode.Off);
        Assert.Equal(LumaHdrMode.Off, LumaIniOverrideManager.ReadHdrMode(bin));
        Assert.Contains("DisplayMode=0", File.ReadAllText(ini));
        Assert.All(off.Entries, entry => Assert.Equal("0", entry.AppliedValue));

        var byDefault = LumaIniOverrideManager.SetHdrMode(bin, off, LumaHdrMode.Default);
        Assert.Equal(LumaHdrMode.Default, LumaIniOverrideManager.ReadHdrMode(bin));
        Assert.DoesNotContain("EnableHDR", File.ReadAllText(ini));
        Assert.DoesNotContain("DisplayMode", File.ReadAllText(ini));
        // A Luma update keeps Default: missing keys are the user's choice.
        var updated = LumaIniOverrideManager.Apply(bin, byDefault);
        Assert.Equal(LumaHdrMode.Default, LumaIniOverrideManager.ReadHdrMode(bin));

        var on = LumaIniOverrideManager.SetHdrMode(bin, updated, LumaHdrMode.On);
        Assert.Equal(LumaHdrMode.On, LumaIniOverrideManager.ReadHdrMode(bin));
        LumaIniOverrideManager.Cleanup(on); // removing Luma: the value from before RHI comes back
        Assert.Contains("DisplayMode=2", File.ReadAllText(ini));
        Assert.DoesNotContain("EnableHDR", File.ReadAllText(ini));
    }

    [Fact]
    public void Hdr_ReadOnlyReShadeIni_IsWrittenAndStaysLocked()
    {
        var bin = Bin("[Luma]\nEnableHDR=1\n");
        var ini = Path.Combine(bin, "ReShade.ini");
        File.SetUnixFileMode(ini, UnixFileMode.UserRead);
        LumaIniOverrideManager.SetHdrMode(bin, null, LumaHdrMode.Off);
        Assert.Equal(LumaHdrMode.Off, LumaIniOverrideManager.ReadHdrMode(bin));
        Assert.False(File.GetUnixFileMode(ini).HasFlag(UnixFileMode.UserWrite));
    }

    [Fact]
    public void Taa_OnWritesOnlyMissingKeys_OffRemovesOnlyRhisUnchangedOnes_InstallsKeepThem_RemovalCleans()
    {
        var engine = Path.Combine(_root, "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        File.WriteAllText(engine, "[SystemSettings]\nr.PostProcessAAQuality=3\n");

        var on = LumaEngineIniOverrideManager.SetTaa(engine, null, on: true)!;
        var text = File.ReadAllText(engine);
        Assert.Contains("r.DefaultFeature.AntiAliasing=2", text);
        Assert.Contains("r.PostProcessAAQuality=3", text); // the user's own key is not overwritten
        Assert.Single(on.Entries, entry => entry.Taa);
        Assert.True(LumaEngineIniOverrideManager.IsTaaOn(on, engine));

        // A Luma update reconciling its wiki keys keeps the setting's keys.
        var game = new DetectedGame { Name = "UE", InstallPath = _root, BinaryDirectory = _root };
        var afterUpdate = LumaEngineIniOverrideManager.Apply(game, [], on);
        Assert.Contains("r.DefaultFeature.AntiAliasing=2", File.ReadAllText(engine));

        var off = LumaEngineIniOverrideManager.SetTaa(engine, on, on: false);
        Assert.DoesNotContain("r.DefaultFeature.AntiAliasing", File.ReadAllText(engine));
        Assert.Contains("r.PostProcessAAQuality=3", File.ReadAllText(engine));
        Assert.False(LumaEngineIniOverrideManager.IsTaaOn(off, engine));

        var again = LumaEngineIniOverrideManager.SetTaa(engine, off, on: true);
        LumaEngineIniOverrideManager.Cleanup(again); // removing Luma takes the TAA key back too
        Assert.DoesNotContain("r.DefaultFeature.AntiAliasing", File.ReadAllText(engine));
        _ = afterUpdate;
    }

    [Fact]
    public void Taa_AnUpdateWithWikiKeys_ReconcilesThoseAndKeepsTheTaaKeys()
    {
        var engine = Path.Combine(_root, "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        var bin = Path.Combine(_root, "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        var game = new DetectedGame { Name = "UE", InstallPath = _root, BinaryDirectory = bin };
        var installed = LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Wiki", "1")]);
        Assert.Equal(engine, installed!.IniPath);
        var on = LumaEngineIniOverrideManager.SetTaa(engine, installed, on: true);

        var updated = LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Other", "1")], on);

        var text = File.ReadAllText(engine);
        Assert.DoesNotContain("r.Wiki", text);
        Assert.Contains("r.Other=1", text);
        Assert.Contains("r.DefaultFeature.AntiAliasing=2", text);
        Assert.Equal(2, updated!.Entries.Count(entry => entry.Taa));
    }

    [Fact]
    public void Taa_KeyTheUserChangedAfterRhi_StaysWhenTurnedOff()
    {
        var engine = Path.Combine(_root, "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        var on = LumaEngineIniOverrideManager.SetTaa(engine, null, on: true)!;
        File.WriteAllText(engine, File.ReadAllText(engine).Replace("r.PostProcessAAQuality=4", "r.PostProcessAAQuality=6"));

        LumaEngineIniOverrideManager.SetTaa(engine, on, on: false);

        Assert.Contains("r.PostProcessAAQuality=6", File.ReadAllText(engine));
        Assert.DoesNotContain("r.DefaultFeature.AntiAliasing", File.ReadAllText(engine));
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Records) Managed(bool withRecord = true)
    {
        var bin = Bin();
        var exe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "UE Game", InstallPath = Path.Combine(_root, "game"), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", EngineHint = "Unreal Engine 4", SteamAppId = 4200 };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        if (withRecord)
        {
            var addon = Path.Combine(bin, "Luma.addon64");
            File.WriteAllText(addon, "x");
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.LumaFramework,
                BinaryDirectory = bin, Files = [new LinuxDeployedFile { Path = addon }],
                Metadata = new(StringComparer.OrdinalIgnoreCase) { [LumaIniOverrideManager.MetadataKey] = LumaIniOverrideManager.Serialize(LumaIniOverrideManager.Apply(bin)) }
            });
        }
        return (game, records);
    }

    [Fact]
    public async Task Service_SavesTheStateOnLumasRecord_SoRemovalKnowsIt()
    {
        var (game, records) = Managed();
        var service = new LumaSettingsService(records, null, _ => null);

        var result = await service.SetHdrAsync(game, LumaHdrMode.Off);

        Assert.True(result.Success, result.Message);
        Assert.Equal(LumaHdrMode.Off, service.Read(game).Hdr);
        var state = LumaIniOverrideManager.Deserialize(records.Find(game, ManagedComponentKind.LumaFramework)!.Metadata[LumaIniOverrideManager.MetadataKey]);
        Assert.All(state!.Entries, entry => Assert.Equal("0", entry.AppliedValue));
    }

    [Fact]
    public async Task Service_RefusesWithoutLumaFromRhi_OrByTheGuard_AndRollsBackAFailedSave()
    {
        var (game, records) = Managed(withRecord: false);
        Assert.StartsWith(LumaSettingsService.NotManagedPrefix, (await new LumaSettingsService(records, null, _ => null).SetHdrAsync(game, LumaHdrMode.Off)).Message);

        var (managedGame, managedRecords) = Managed();
        var ini = Path.Combine(managedGame.BinaryDirectory!, "ReShade.ini");
        var before = File.ReadAllText(ini);
        Assert.Equal("busy", (await new LumaSettingsService(managedRecords, null, _ => "busy").SetHdrAsync(managedGame, LumaHdrMode.Off)).Message);
        Assert.Equal(before, File.ReadAllText(ini));

        managedRecords.SaveFaultForTesting = () => new IOException("disk full (test)");
        var failed = await new LumaSettingsService(managedRecords, null, _ => null).SetHdrAsync(managedGame, LumaHdrMode.Off);
        managedRecords.SaveFaultForTesting = null;
        Assert.False(failed.Success);
        Assert.Equal(before, File.ReadAllText(ini));
    }

    [Fact]
    public void Decorate_MarksTheLumaRowOfAnUnrealGame_AndOnlyThenItHasTheGear()
    {
        var (game, records) = Managed();
        using var http = new HttpClient(new OfflineHandler());
        var service = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!,
            renodx: new RenoDxDeploymentService(http, records, new RenoDxCatalogService(http)), renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!,
            lumaFramework: new LumaFrameworkDeploymentService(http, records), lumaPolicy: null!);
        var luma = new ManagedComponent { Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma Framework", Availability = ComponentAvailability.Installed, ManagedByRhi = true };

        service.Decorate(game, [luma]);

        Assert.True(luma.UnrealEngine);
        Assert.True(IniPresetsPresenter.HasIniSettings(luma));
        Assert.False(IniPresetsPresenter.HasIniSettings(new ManagedComponent { Kind = ManagedComponentKind.LumaFramework, DisplayName = "L", Availability = ComponentAvailability.Installed, ManagedByRhi = true }));
        Assert.False(IniPresetsPresenter.HasIniSettings(new ManagedComponent { Kind = ManagedComponentKind.LumaFramework, DisplayName = "L", Availability = ComponentAvailability.Installed, UnrealEngine = true }));
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("unit tests are offline");
    }
}
