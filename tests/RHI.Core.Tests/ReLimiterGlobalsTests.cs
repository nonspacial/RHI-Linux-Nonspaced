using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#188: ReLimiter globals written into every relimiter.ini and the template; only their four keys change.</summary>
public sealed class ReLimiterGlobalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-relimiter-globals-" + Guid.NewGuid().ToString("N"));

    public ReLimiterGlobalsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Apply_ChangesOnlyItsFourKeys_AndRemovesTheCapWhenOff()
    {
        var lines = new List<string> { "; mine", "[FrameLimiter]", "target_fps=60", "fg_mode=2", "", "[Other]", "x=1" };

        Assert.True(ReLimiterIniEditor.Apply(lines, new ReLimiterGlobals("Ctrl+F12", true, false, 0)));

        Assert.Equal(["; mine", "[FrameLimiter]", "fg_mode=2", "osd_toggle_key=Ctrl+F12", "shared_presets=true", "dlss_info_hooks=false", "", "[Other]", "x=1"], lines);
        Assert.False(ReLimiterIniEditor.Apply(lines, new ReLimiterGlobals("Ctrl+F12", true, false, 0)));

        ReLimiterIniEditor.Apply(lines, new ReLimiterGlobals("F12", true, false, 116));
        Assert.Contains("target_fps=116", lines);
    }

    [Fact]
    public void Apply_ToTheShippedTemplate_AddsTheFrameLimiterSection()
    {
        var lines = IniLines.FromBytes(UserIniTemplates.Shipped(UserIniTemplate.ReLimiter));
        ReLimiterIniEditor.Apply(lines, new ReLimiterGlobals("F12", false, true, 138));
        Assert.Equal(["[UltraLimiter]", "", "[FrameLimiter]", "osd_toggle_key=F12", "shared_presets=false", "dlss_info_hooks=true", "target_fps=138"], lines);
    }

    [Theory]
    [InlineData(123, false, false, false, "F12")]
    [InlineData(123, true, true, true, "Ctrl+Alt+Shift+F12")]
    [InlineData(33, true, false, false, "Ctrl+PageUp")]
    [InlineData(107, true, false, false, "Ctrl+Num+")]
    [InlineData(96, false, false, true, "Alt+Num0")]
    [InlineData(44, false, true, false, "Shift+PrintScreen")]
    public void Hotkey_RoundTripsBetweenTheEditorAndReLimitersNames(int vk, bool ctrl, bool shift, bool alt, string name)
    {
        var binding = new ReShadeHotkeyCodec.Binding(vk, ctrl, shift, alt);
        Assert.Equal(name, ReLimiterIniEditor.HotkeyName(binding));
        Assert.True(ReLimiterIniEditor.TryParseHotkey(name.ToLowerInvariant(), out var parsed));
        Assert.Equal(binding, parsed);
        Assert.Equal(name, ReLimiterIniEditor.FromEditorValue(ReLimiterIniEditor.ToEditorValue(name)));
    }

    [Theory]
    [InlineData("", "F12")]
    [InlineData("Hyper+F12", "F12")]
    [InlineData("ctrl+pageup", "Ctrl+PageUp")]
    [InlineData("NoSuchKey", "F12")]
    public void Settings_NormaliseTheHotkeyAndTheCap(string stored, string expected)
    {
        var settings = new LinuxAppSettings { ReLimiterOsdHotkey = stored, ReLimiterTargetFps = 5 }.Normalize();
        Assert.Equal(expected, settings.ReLimiterOsdHotkey);
        Assert.Equal(0, settings.ReLimiterTargetFps);
        Assert.Equal(1000, new LinuxAppSettings { ReLimiterTargetFps = 1000 }.Normalize().ReLimiterTargetFps);
        Assert.Equal(0, new LinuxAppSettings { ReLimiterTargetFps = 1001 }.Normalize().ReLimiterTargetFps);
    }

    private DetectedGame Game(string name, bool withIni = true, bool storage = false)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        if (withIni) File.WriteAllText(Path.Combine(bin, "relimiter.ini"), "[FrameLimiter]\nkeep=1\n");
        if (storage)
        {
            Directory.CreateDirectory(Path.Combine(bin, "_storage_"));
            File.WriteAllText(Path.Combine(bin, "_storage_", "relimiter.ini"), "[FrameLimiter]\n");
        }
        return new DetectedGame { Name = name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "g.exe"), Runner = "Proton" };
    }

    [Fact]
    public async Task ApplyToAll_WritesBesideTheAddonAndInStorage_SkipsGamesWithoutReLimiter_UpdatesTheTemplate()
    {
        var plain = Game("Plain");
        var re = Game("Re", storage: true);
        var none = Game("None", withIni: false);
        var refused = Game("Refused");
        var external = Game("External"); // the user's own ReLimiter: left alone (review finding 1)
        var templates = new UserIniTemplates(Path.Combine(_root, "inis"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json"));
        foreach (var game in new[] { plain, re, none, refused }) records.Manage(game, ManagedComponentKind.ReLimiter);
        var service = new UserIniService(templates, records, game => game.Name == "Refused" ? "busy" : null);
        var globals = new ReLimiterGlobals("Ctrl+F12", false, true, 116);

        Assert.Equal([plain, re, refused], service.ReLimiterSettingsTargets([plain, re, none, refused, external]));
        var result = await service.ApplyReLimiterSettingsToAllAsync([plain, re, none, refused, external], globals);
        var template = service.ApplyReLimiterSettingsToTemplate(globals);

        Assert.Equal(new IniBatchResult(2, 1, 0), result);
        foreach (var ini in new[] { Path.Combine(plain.BinaryDirectory!, "relimiter.ini"), Path.Combine(re.BinaryDirectory!, "relimiter.ini") })
        {
            var text = File.ReadAllText(ini);
            Assert.Contains("keep=1", text);
            Assert.Contains("osd_toggle_key=Ctrl+F12", text);
            Assert.Contains("target_fps=116", text);
        }
        Assert.Contains("dlss_info_hooks=true", File.ReadAllText(Path.Combine(re.BinaryDirectory!, "_storage_", "relimiter.ini")));
        Assert.Equal("[FrameLimiter]\nkeep=1\n", File.ReadAllText(Path.Combine(refused.BinaryDirectory!, "relimiter.ini")));
        Assert.False(File.Exists(Path.Combine(none.BinaryDirectory!, "relimiter.ini")));
        Assert.Equal("[FrameLimiter]\nkeep=1\n", File.ReadAllText(Path.Combine(external.BinaryDirectory!, "relimiter.ini")));
        Assert.Contains("osd_toggle_key=Ctrl+F12", File.ReadAllText(template));
        Assert.StartsWith("[UltraLimiter]", File.ReadAllText(template));
    }

    [Fact]
    public async Task ApplyToAll_AFailedSecondWrite_PutsTheFirstBack()
    {
        // Review finding 6: "whole or not at all" holds for a write that fails after the checks, not only for links.
        var re = Game("Re", storage: true);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json")).Manage(re, ManagedComponentKind.ReLimiter);
        var service = new UserIniService(new UserIniTemplates(Path.Combine(_root, "inis")), records, _ => null);
        var storage = Path.Combine(re.BinaryDirectory!, "_storage_", "relimiter.ini");
        IniLines.WriteFaultForTesting.Value = path => path == storage ? new IOException("disk full (test)") : null;
        IniBatchResult result;
        try { result = await service.ApplyReLimiterSettingsToAllAsync([re], new ReLimiterGlobals("F11", false, false, 0)); }
        finally { IniLines.WriteFaultForTesting.Value = null; }

        Assert.Equal(new IniBatchResult(0, 0, 1), result);
        Assert.Equal("[FrameLimiter]\nkeep=1\n", File.ReadAllText(Path.Combine(re.BinaryDirectory!, "relimiter.ini")));
        Assert.Equal("[FrameLimiter]\n", File.ReadAllText(storage));
    }

    [Fact]
    public async Task ApplyToAll_AStorageLinkOutOfTheGame_FailsThatGameOnly()
    {
        var linked = Game("Linked");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "relimiter.ini"), "[FrameLimiter]\n");
        Directory.CreateSymbolicLink(Path.Combine(linked.BinaryDirectory!, "_storage_"), outside);
        var fine = Game("Fine");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json")).Manage(linked, ManagedComponentKind.ReLimiter).Manage(fine, ManagedComponentKind.ReLimiter);
        var service = new UserIniService(new UserIniTemplates(Path.Combine(_root, "inis")), records, _ => null);

        var result = await service.ApplyReLimiterSettingsToAllAsync([linked, fine], new ReLimiterGlobals("F11", false, false, 0));

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.Equal("[FrameLimiter]\n", File.ReadAllText(Path.Combine(outside, "relimiter.ini")));
        Assert.Equal("[FrameLimiter]\nkeep=1\n", File.ReadAllText(Path.Combine(linked.BinaryDirectory!, "relimiter.ini"))); // whole game or nothing
        Assert.Contains("osd_toggle_key=F11", File.ReadAllText(Path.Combine(fine.BinaryDirectory!, "relimiter.ini")));
    }
}

/// <summary>#188: the Settings snapshot keeps the ReLimiter globals; the presenter's Apply writes games and the template.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ReLimiterGlobalsPresenterTests
{
    [Fact]
    public async Task SettingsSnapshot_CarriesTheReLimiterGlobals()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.Equal("F12", settings.ReLimiterOsdHotkey);
        settings.ReLimiterOsdHotkey = "Ctrl+F11";
        settings.ReLimiterSharedPresets = true;
        settings.ReLimiterDlssHooks = true;
        settings.ReLimiterTargetFps = 138;
        settings.UiLanguage = "en-US";

        fx.Vm.Settings.SaveLinuxSettings(settings);

        var again = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.Equal(("Ctrl+F11", true, true, 138), (again.ReLimiterOsdHotkey, again.ReLimiterSharedPresets, again.ReLimiterDlssHooks, again.ReLimiterTargetFps));
    }

    [Fact]
    public async Task ApplyToAll_WritesTheTemplateToo()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.IniPresets.SavedSettings();
        settings.ReLimiterTargetFps = 157;

        var line = await fx.Vm.IniPresets.ApplyReLimiterSettingsToAllAsync(settings);

        Assert.Equal(UiLocalizationService.Format("Ini.BatchNone", UiLocalizationService.Text("Ini.ReLimiterSettingsWhat")), line);
        Assert.Contains("target_fps=157", File.ReadAllText(Path.Combine(fx.Vm.IniPresets.TemplatesFolder, "relimiter.ini")));
    }
}
