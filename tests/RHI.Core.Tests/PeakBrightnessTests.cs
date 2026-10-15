using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#182: monitor peak brightness from EDID / KDE, ToneMapPeakNits owned on RenoDX's record, UE-Extended choice.</summary>
public sealed class PeakBrightnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-peak-" + Guid.NewGuid().ToString("N"));

    public PeakBrightnessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A base block with a monitor name and a CTA-861 extension holding the given data blocks.</summary>
    internal static byte[] Edid(string? name, params byte[][] ctaBlocks)
    {
        var edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        if (name is not null)
        {
            edid[72 + 3] = 0xFC;
            var text = System.Text.Encoding.ASCII.GetBytes(name + "\n");
            Array.Copy(text, 0, edid, 72 + 5, Math.Min(13, text.Length));
        }
        edid[128] = 0x02;
        edid[129] = 0x03;
        var offset = 132;
        foreach (var block in ctaBlocks) { block.CopyTo(edid, offset); offset += block.Length; }
        edid[130] = (byte)(offset - 128);
        return edid;
    }

    // HDR Static Metadata Data Block: extended tag (7), length, code 6, EOTFs, metadata types, max/avg/min luminance.
    internal static byte[] HdrBlock(byte maxCv) => [(7 << 5) | 6, 0x06, 0x0D, 0x01, maxCv, 0x60, 0x10];

    [Theory]
    [InlineData(138, 993)]
    [InlineData(0x7F, 783)]
    [InlineData(160, 1600)]
    public void PeakNits_IsTheHdrBlocksMaxLuminance(byte cv, int nits)
        => Assert.Equal(nits, DisplayPeakBrightness.PeakNits(Edid("OLED", HdrBlock(cv))));

    [Fact]
    public void PeakNits_IsAbsent_WithoutAnHdrBlockOrWithoutItsLuminance()
    {
        Assert.Null(DisplayPeakBrightness.PeakNits(Edid("SDR")));
        // A real TV (Samsung S90D) stops its HDR block after the metadata-type byte: no luminance declared.
        // The next block (here the TV's real extended tag 0x0F block) is never read as the luminance.
        Assert.Null(DisplayPeakBrightness.PeakNits(Edid("S90D", [(7 << 5) | 3, 0x06, 0x0D, 0x01], [(7 << 5) | 2, 0x0F, 0x0F])));
        Assert.Null(DisplayPeakBrightness.PeakNits(new byte[128]));
        Assert.Equal("OLED", DisplayPeakBrightness.MonitorName(Edid("OLED")));
    }

    private string Connector(string folder, byte[] edid, string status = "connected")
    {
        var dir = Path.Combine(_root, "drm", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "edid"), edid);
        File.WriteAllText(Path.Combine(dir, "status"), status + "\n");
        return dir;
    }

    [Fact]
    public void Read_ListsConnectedMonitors_PreferringThePeakSetInKde()
    {
        Connector("card1-DP-1", Edid("HDR Monitor", HdrBlock(138)));
        var tvEdid = Edid("S90D", [(7 << 5) | 3, 0x06, 0x0D, 0x01]);
        Connector("card1-HDMI-A-1", tvEdid);
        Connector("card1-DP-2", Edid("Off", HdrBlock(160)), "disconnected");
        Connector("card1-DP-3", Edid("Plain"));
        var kwin = Path.Combine(_root, "kwinoutputconfig.json");
        var hash = Convert.ToHexString(MD5.HashData(tvEdid)).ToLowerInvariant();
        File.WriteAllText(kwin, $$"""
            [{"name":"outputs","data":[{"connectorName":"HDMI-A-1","edidHash":"{{hash}}","maxPeakBrightnessOverride":1180},
                                       {"connectorName":"X","edidHash":"00","maxPeakBrightnessOverride":null}]},
             {"name":"setups","data":[]}]
            """);

        var peaks = DisplayPeakBrightness.Read(Path.Combine(_root, "drm"), kwin);

        Assert.Equal([new DisplayPeak("DP-1", "HDR Monitor", 993, DisplayPeakSource.Monitor), new DisplayPeak("HDMI-A-1", "S90D", 1180, DisplayPeakSource.KdeDisplaySettings)], peaks);
        Assert.Empty(DisplayPeakBrightness.Read(Path.Combine(_root, "nothing")));
        Assert.Single(DisplayPeakBrightness.Read(Path.Combine(_root, "drm"), Path.Combine(_root, "no-kwin.json")));
    }

    // ---- ToneMapPeakNits ownership --------------------------------------------------------------

    private string Bin(string ini)
    {
        var bin = Path.Combine(_root, "game", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), ini);
        return bin;
    }

    [Fact]
    public void Apply_CreatesMissingPresets_KeepsOverlayEditsOnUpdate_ExplicitWriteOverrides_RemovalRestores()
    {
        var bin = Bin("[renodx-preset1]\nToneMapPeakNits=400\n[renodx-preset2]\nToneMapPeakNits=500\n");
        var ini = Path.Combine(bin, "ReShade.ini");
        var states = RenoDxPeakNits.Apply(bin, null, 1000, RenoDxPeakNits.GlobalSections([1, 3]), new Dictionary<string, RenoDxIniOverrideState>(), force: false);
        var text = File.ReadAllText(ini);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=1000", text);
        Assert.Contains("[renodx-preset2]\nToneMapPeakNits=500", text); // unchosen preset kept
        Assert.Contains("[renodx-preset3]\nToneMapPeakNits=1000", text);

        File.WriteAllText(ini, File.ReadAllText(ini).Replace("[renodx-preset1]\nToneMapPeakNits=1000", "[renodx-preset1]\nToneMapPeakNits=650")); // overlay edit
        states = RenoDxPeakNits.Apply(bin, null, 1200, RenoDxPeakNits.GlobalSections([1, 3]), states, force: false);
        Assert.Contains("ToneMapPeakNits=650", File.ReadAllText(ini));
        Assert.Contains("[renodx-preset3]\nToneMapPeakNits=1200", File.ReadAllText(ini));

        states = RenoDxPeakNits.Apply(bin, null, 1500, RenoDxPeakNits.GlobalSections([1]), states, force: true);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=1500", File.ReadAllText(ini));

        RenoDxPeakNits.Cleanup(states, null);
        var restored = File.ReadAllText(ini);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=400", restored); // the value from before RHI, not 650
        Assert.DoesNotContain("renodx-preset3", restored);
        Assert.Contains("[renodx-preset2]\nToneMapPeakNits=500", restored);
    }

    [Fact]
    public void GameSections_AreThePresetsInTheFile_OrPresetOne()
    {
        Assert.Equal(["renodx-preset2", "renodx-preset5"], RenoDxPeakNits.GameSections(Path.Combine(Bin("[renodx]\nx=1\n[renodx-preset2]\n[renodx-preset5]\n[renodx-presetX]\n"), "ReShade.ini")));
        Assert.Equal(["renodx-preset1"], RenoDxPeakNits.GameSections(Path.Combine(Bin("[renodx]\nx=1\n"), "ReShade.ini")));
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Records) Managed(string ini = "[renodx-preset1]\nToneMapPeakNits=400\n[renodx-preset4]\nA=1\n")
    {
        var bin = Bin(ini);
        var game = new DetectedGame { Name = "HDR Game", InstallPath = Path.Combine(_root, "game"), BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "g.exe"), Runner = "Proton" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        records.Upsert(new LinuxDeploymentRecord { GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX, BinaryDirectory = bin, Files = [] });
        return (game, records);
    }

    [Fact]
    public async Task SetForGame_WritesEveryPresetInTheFile_AndKeepsTheStateOnTheRecord()
    {
        var (game, records) = Managed();
        var service = new RenoDxPeakNits(records, null, _ => null);

        var result = await service.SetForGameAsync(game, 900);

        Assert.True(result.Success, result.Message);
        var text = File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"));
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=900", text);
        Assert.Contains("ToneMapPeakNits=900\n", text[text.IndexOf("[renodx-preset4]", StringComparison.Ordinal)..]);
        Assert.Equal(900, RenoDxPeakNits.CurrentNits(game.BinaryDirectory!));
        Assert.True(RenoDxPeakNits.HasReShadeIni(game.BinaryDirectory!));
        Assert.False(RenoDxPeakNits.HasReShadeIni(_root));
        Assert.Equal(2, RenoDxPeakNits.Read(records.Find(game, ManagedComponentKind.RenoDX)).Count);
    }

    [Fact]
    public async Task SetForGame_Refused_WithoutRhiRenoDx_ByTheGuard_OrWithoutReShadeIni()
    {
        var (game, records) = Managed();
        Assert.Equal("busy", (await new RenoDxPeakNits(records, null, _ => "busy").SetForGameAsync(game, 900)).Message);
        Assert.Contains("ToneMapPeakNits=400", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini")));
        records.Remove(game, ManagedComponentKind.RenoDX);
        Assert.StartsWith(RenoDxPeakNits.NotManagedPrefix, (await new RenoDxPeakNits(records, null, _ => null).SetForGameAsync(game, 900)).Message);

        var (other, otherRecords) = Managed();
        File.Delete(Path.Combine(other.BinaryDirectory!, "ReShade.ini"));
        Assert.StartsWith(RenoDxPeakNits.NoReShadeIniPrefix, (await new RenoDxPeakNits(otherRecords, null, _ => null).SetForGameAsync(other, 900)).Message);
    }

    [Fact]
    public async Task SetForGame_RecordSaveFails_TheIniGoesBack()
    {
        var (game, records) = Managed();
        var ini = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        var before = File.ReadAllText(ini);
        records.SaveFaultForTesting = () => new IOException("disk full (test)");
        var result = await new RenoDxPeakNits(records, null, _ => null).SetForGameAsync(game, 900);
        records.SaveFaultForTesting = null;
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(ini));
    }

    [Fact]
    public async Task ApplyToAll_OnlyGamesWithRhiRenoDx_ChosenPresets()
    {
        var (game, records) = Managed();
        var other = new DetectedGame { Name = "No RenoDX", InstallPath = _root, BinaryDirectory = _root, Runner = "Proton" };

        var result = await new RenoDxPeakNits(records, null, _ => null).ApplyToAllAsync([game, other], new PeakNitsSettings(1100, true, [2]));

        Assert.Equal(new IniBatchResult(1, 0, 0), result);
        var text = File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"));
        Assert.Contains("[renodx-preset2]\nToneMapPeakNits=1100", text);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=400", text);
    }

    [Fact]
    public void Settings_NormaliseNitsAndPresets()
    {
        var settings = new LinuxAppSettings { PeakNits = 20000, PeakNitsPresets = [3, 9, 1, 3] }.Normalize();
        Assert.Equal(0, settings.PeakNits);
        Assert.Equal([1, 3], settings.PeakNitsPresets);
        Assert.Equal([1, 2, 3], new LinuxAppSettings().PeakNitsPresets);
        Assert.True(new LinuxAppSettings().PeakNitsEnabled);
        Assert.False(PeakNitsSettings.From(new LinuxAppSettings()).Applies); // 0 nits: nothing written
        Assert.True(PeakNitsSettings.From(new LinuxAppSettings { PeakNits = 800 }).Applies);
        Assert.False(PeakNitsSettings.From(new LinuxAppSettings { PeakNits = 800, PeakNitsEnabled = false }).Applies);
    }

    // ---- UE-Extended ----------------------------------------------------------------------------

    [Fact]
    public void UeExtendedChoice_SwapsTheCataloguePick_ExceptWhereTheManifestBarsIt()
    {
        var game = new DetectedGame { Name = "UE", InstallPath = _root, EngineHint = "Unreal Engine 5" };
        var named = new RenoDxModEntry("Named", "https://clshortfuse.github.io/renodx/renodx-named.addon64", null, null);
        var ue = new RenoDxModEntry("UE-Extended", RenoDxDeploymentService.DefaultUeExtendedUrl, null, null);

        Assert.Equal(RenoDxDeploymentService.DefaultUeExtendedUrl, RenoDxDeploymentService.ApplyUeExtendedChoice(game, named, "on")!.SnapshotUrl64);
        Assert.Equal(RenoDxCatalogService.GenericUnrealUrl, RenoDxDeploymentService.ApplyUeExtendedChoice(game, ue, "off")!.SnapshotUrl64);
        Assert.Same(named, RenoDxDeploymentService.ApplyUeExtendedChoice(game, named, "off"));
        Assert.Same(named, RenoDxDeploymentService.ApplyUeExtendedChoice(game, named, null));
        var barred = new DetectedGame { Name = "UE", InstallPath = _root, EngineHint = "Unreal Engine 5", UeExtendedBlockedByManifest = true };
        Assert.Same(named, RenoDxDeploymentService.ApplyUeExtendedChoice(barred, named, "on"));

        var record = new LinuxDeploymentRecord { SourceUrl = named.SnapshotUrl64 };
        Assert.True(RenoDxDeploymentService.OffersUeExtendedSwitch(game, record));
        Assert.False(RenoDxDeploymentService.OffersUeExtendedSwitch(barred, record));
        Assert.False(RenoDxDeploymentService.OffersUeExtendedSwitch(new DetectedGame { Name = "Unity", InstallPath = _root }, record));
        Assert.True(RenoDxDeploymentService.OffersUeExtendedSwitch(new DetectedGame { Name = "?", InstallPath = _root }, new LinuxDeploymentRecord { SourceUrl = RenoDxCatalogService.GenericUnrealUrl }));
        Assert.False(RenoDxDeploymentService.OffersUeExtendedSwitch(game, null));
    }
}

// #182 through the real RenoDX install path: the global peak brightness at install, its removal, the UE-Extended choice.
public sealed partial class RenoDxInstallTests
{
    private (RenoDxDeploymentService Service, LinuxDeploymentRecordStore Store, DetectedGame Game, string Ini) PeakFixture(string name, string ini)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "StreetFighter6.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), ini);
        var game = new DetectedGame { Name = "Street Fighter™ 6", InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", SteamAppId = 1364780 };
        var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json"));
        return (new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, name, "cache")), store, game, Path.Combine(bin, "ReShade.ini"));
    }

    [Fact]
    public async Task Install_WritesTheGlobalPeakBrightness_RemovalPutsTheOldValueBack()
    {
        var (service, _, game, ini) = PeakFixture("peak", "[renodx-preset1]\nToneMapPeakNits=400\n");
        service.PeakNits = () => new PeakNitsSettings(1000, true, [1, 2]);

        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=1000", File.ReadAllText(ini));
        Assert.Contains("[renodx-preset2]\nToneMapPeakNits=1000", File.ReadAllText(ini));

        service.PeakNits = () => new PeakNitsSettings(1000, false, [1, 2]);
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success); // an update with the setting off keeps ownership
        Assert.True((await service.RemoveAsync(game)).Success);
        var text = File.ReadAllText(ini);
        Assert.Contains("[renodx-preset1]\nToneMapPeakNits=400", text);
        Assert.DoesNotContain("renodx-preset2", text);
    }

    [Fact]
    public async Task Install_WithUeExtendedOn_DeploysUeExtended_AndTheChoiceSurvivesAnUpdate()
    {
        var (service, store, game, _) = PeakFixture("ue", "[GENERAL]\n");
        game.EngineHint = "Unreal Engine 4";
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        var record = store.Find(game, ManagedComponentKind.RenoDX)!;
        record.Metadata[RenoDxDeploymentService.UeExtendedMetadataKey] = "on";
        store.Upsert(record);

        Assert.True((await service.UpdateAsync(game, [game.ExecutablePath!])).Success);

        var updated = store.Find(game, ManagedComponentKind.RenoDX)!;
        Assert.Equal(RenoDxDeploymentService.DefaultUeExtendedUrl, updated.SourceUrl);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-ue-extended.addon64")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-sf6.addon64")));
        Assert.Equal("on", updated.Metadata[RenoDxDeploymentService.UeExtendedMetadataKey]);
        var check = await service.CheckUpdateAsync(game, [game.ExecutablePath!]);
        Assert.False(check.UpdateAvailable, check.Message); // the choice is not reported as an update back to the catalogue's pick
    }
}

/// <summary>#182: the Settings snapshot carries the peak brightness settings.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class PeakBrightnessSettingsTests
{
    [Fact]
    public async Task SettingsSnapshot_CarriesThePeakBrightness()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.PeakNits = 1180;
        settings.PeakNitsEnabled = false;
        settings.PeakNitsPresets = [2];
        settings.UiLanguage = "en-US";

        fx.Vm.Settings.SaveLinuxSettings(settings);

        var again = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.Equal((1180, false), (again.PeakNits, again.PeakNitsEnabled));
        Assert.Equal([2], again.PeakNitsPresets);
        Assert.Equal(UiLocalizationService.Text("Ini.PeakNotSet"), await fx.Vm.IniPresets.ApplyPeakNitsToAllAsync(again));
    }

    private static async Task<(ViewModelFixture Fx, DetectedGame Card, string Bin)> RenoDxCardAsync()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-peak-card-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[renodx-preset1]\nToneMapPeakNits=400\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Peak Card", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77004, Source = "Steam", EngineHint = "Unreal Engine 5"
        };
        var fx = await ViewModelFixture.StartAsync([game]);
        var card = fx.Vm.AllLibraryGames.Single();
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(card), GameName = card.Name, Component = ManagedComponentKind.RenoDX, BinaryDirectory = bin,
            SourceUrl = "https://clshortfuse.github.io/renodx/renodx-peak.addon64",
            Files = [new LinuxDeployedFile { Path = Path.Combine(bin, "renodx-peak.addon64"), Sha256 = "00" }] // missing file: an update fails
        });
        fx.Vm.SelectedGame = card;
        return (fx, card, bin);
    }

    [Fact]
    public async Task GameNits_HoldsTheGate_AndSaysWhatWasSet()
    {
        var (fx, _, bin) = await RenoDxCardAsync();
        using (fx)
        {
            using (fx.Vm.Operations.TryBegin("test"))
                Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), await fx.Vm.IniPresets.SetGameNitsAsync(900));
            Assert.Equal(400, fx.Vm.IniPresets.GameNits());
            Assert.Equal(UiLocalizationService.Format("Ini.PeakGameSet", 900), await fx.Vm.IniPresets.SetGameNitsAsync(900));
            Assert.Equal(900, fx.Vm.IniPresets.GameNits());
            Assert.Equal(UiLocalizationService.Text("Ini.PeakInvalid"), await fx.Vm.IniPresets.SetGameNitsAsync(0));
        }
    }

    [Fact]
    public async Task UeExtendedSwitch_AFailedReinstall_PutsTheEarlierChoiceBack()
    {
        var (fx, card, _) = await RenoDxCardAsync();
        using (fx)
        {
            Assert.True(fx.Vm.IniPresets.OffersUeExtended());
            Assert.False(fx.Vm.IniPresets.UeExtendedInstalled());

            await fx.Vm.IniPresets.SetUeExtendedAsync(true);

            Assert.False(fx.Records.Find(card, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxDeploymentService.UeExtendedMetadataKey));
            Assert.False(fx.Vm.IsBusy);
        }
    }
}
