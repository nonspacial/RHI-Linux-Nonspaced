using RHI.Core.Models;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class SettingsPageTests
{
    [Fact]
    public void SettingsStore_MissingFileUsesLinuxDefaults()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        var settings = store.Load();
        Assert.Equal("Stable", settings.ReShadeChannel);
        Assert.True(settings.ReShadeManageIni);
        Assert.Equal("36,0,0,0", settings.OverlayHotkey);
        Assert.Equal("44,0,0,0", settings.ScreenshotHotkey);
        Assert.True(settings.CacheAllShaders);
    }

    // w3 review major 3 (#91): every channel the Settings list offers survives a save and a load, No Addons included.
    [Fact]
    public void SettingsStore_KeepsEveryOfferedReShadeChannel_AsTheDefault()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        Assert.Contains("NoAddons", RHI.Desktop.ViewModels.OptionLists.ReShadeChannel.Values);
        foreach (var channel in RHI.Desktop.ViewModels.OptionLists.ReShadeChannel.Values)
        {
            store.Save(new LinuxAppSettings { ReShadeChannel = channel });
            Assert.Equal(channel, store.Load().ReShadeChannel);
            Assert.Equal(channel, new LinuxSettingsStore(store.SettingsPath, store.CacheRoot, store.DataRoot).Load().ReShadeChannel);
        }
    }

    [Fact]
    public void SettingsStore_SaveAndLoadRoundTripsPortableSettings()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        store.Save(new LinuxAppSettings
        {
            ReShadeChannel = "Nightly",
            ReShadeManageIni = false,
            ScreenshotPath = " /tmp/screens ",
            OverlayHotkey = "45, 1, 0, 0",
            ScreenshotHotkey = "46,0,0,0",
            PerGameScreenshotFolders = true,
            AddonWatchFolder = " /tmp/watch ",
            CacheAllShaders = false,
            VerboseLogging = true
        });

        var settings = store.Load();
        Assert.Equal("Nightly", settings.ReShadeChannel);
        Assert.False(settings.ReShadeManageIni);
        Assert.Equal("/tmp/screens", settings.ScreenshotPath);
        Assert.Equal("45,1,0,0", settings.OverlayHotkey);
        Assert.Equal("/tmp/watch", settings.AddonWatchFolder);
        Assert.True(settings.PerGameScreenshotFolders);
        Assert.True(settings.VerboseLogging);
    }


    [Fact]
    public void SettingsStore_UiLanguageRoundTripsPersistedSelection()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        store.Save(new LinuxAppSettings { UiLanguage = "en-US" });
        Assert.Equal("en-US", store.Load().UiLanguage);
        store.Save(new LinuxAppSettings { UiLanguage = "pt-BR" });
        Assert.Equal("pt-BR", store.Load().UiLanguage);
    }

    [Fact]
    public void SettingsStore_UiLanguageNormalizesSupportedFamiliesAndFallsBackToEnglish()
    {
        Assert.Equal("pt-BR", LinuxAppSettings.NormalizeUiLanguage("pt-PT"));
        Assert.Equal("en-US", LinuxAppSettings.NormalizeUiLanguage("en-GB"));
        Assert.Equal("en-US", LinuxAppSettings.NormalizeUiLanguage("fr-FR"));
    }

    [Fact]
    public void SettingsStore_CorruptJsonFailsClosedToDefaults()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, "{not json");
        Assert.Equal("Stable", store.Load().ReShadeChannel);
    }

    [Fact]
    public void SettingsStore_NormalizeRejectsUnknownChannelAndMalformedHotkeys()
    {
        var settings = new LinuxAppSettings
        {
            ReShadeChannel = "Canary",
            OverlayHotkey = "nope",
            ScreenshotHotkey = "1,2,3"
        }.Normalize();
        Assert.Equal("Stable", settings.ReShadeChannel);
        Assert.Equal("36,0,0,0", settings.OverlayHotkey);
        Assert.Equal("44,0,0,0", settings.ScreenshotHotkey);
    }

    [Theory]
    [InlineData("36,0,0,0", 36, false, false, false, "Home (Início)")]
    [InlineData("44,0,0,0", 44, false, false, false, "Print Screen")]
    [InlineData("113,0,1,0", 113, false, true, false, "Shift + F2")]
    [InlineData("65,1,1,1", 65, true, true, true, "Ctrl + Shift + Alt + A")]
    [InlineData("45,1,0,0", 45, true, false, false, "Ctrl + Insert (Inserir)")]
    [InlineData("123,0,0,0", 123, false, false, false, "F12")]
    public void ReShadeHotkeyCodec_ConvertsInternalValuesToFriendlyBindings(
        string raw, int keyCode, bool ctrl, bool shift, bool alt, string display)
    {
        Assert.True(ReShadeHotkeyCodec.TryParse(raw, out var binding));
        Assert.Equal(keyCode, binding.KeyCode);
        Assert.Equal(ctrl, binding.Ctrl);
        Assert.Equal(shift, binding.Shift);
        Assert.Equal(alt, binding.Alt);
        Assert.Equal(raw, binding.ToConfigValue());
        Assert.Equal(display, binding.ToDisplayText());
    }

    [Fact]
    public void ReShadeHotkeyCodec_InvalidValueFallsBackWithoutExposingRawInput()
    {
        var binding = ReShadeHotkeyCodec.ParseOrDefault("not-a-hotkey", "36,0,0,0");
        Assert.Equal("36,0,0,0", binding.ToConfigValue());
        Assert.Equal("Home (Início)", binding.ToDisplayText());
    }

    [Fact]
    public void ReShadeHotkeyCodec_UnknownExistingKeyPreservesBinding()
    {
        var binding = ReShadeHotkeyCodec.ParseOrDefault("200,1,0,1", "36,0,0,0");
        Assert.Equal("200,1,0,1", binding.ToConfigValue());
        Assert.Contains("Tecla existente", binding.ToDisplayText());
        Assert.Contains("Ctrl", binding.ToDisplayText());
        Assert.Contains("Alt", binding.ToDisplayText());
    }

    [Fact]
    public void SettingsStore_ResetDeletesPersistedFile()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        store.Save(new LinuxAppSettings { ReShadeChannel = "Legacy" });
        Assert.True(File.Exists(store.SettingsPath));
        var reset = store.Reset();
        Assert.False(File.Exists(store.SettingsPath));
        Assert.Equal("Stable", reset.ReShadeChannel);
    }

    [Fact]
    public void SettingsStore_PurgeCacheRemovesOnlyConfiguredCacheRoot()
    {
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        Directory.CreateDirectory(store.CacheRoot);
        File.WriteAllText(Path.Combine(store.CacheRoot, "a.bin"), "x");
        Directory.CreateDirectory(store.DataRoot);
        File.WriteAllText(Path.Combine(store.DataRoot, "keep.txt"), "keep");
        Assert.Equal(1, store.PurgeCache());
        Assert.False(Directory.Exists(store.CacheRoot));
        Assert.True(File.Exists(Path.Combine(store.DataRoot, "keep.txt")));
    }

    [Fact]
    public void ReShadeSettingsApplier_CreatesInputAndScreenshotSectionsAndPreservesUserSection()
    {
        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "ReShade.ini"), "[GENERAL]\nPresetPath=user.ini\n");
        var screens = Path.Combine(temp.Path, "screens");
        var game = Game(gameDir, "Demo Game");
        var path = ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings
        {
            ScreenshotPath = screens,
            OverlayHotkey = "45,0,0,0",
            ScreenshotHotkey = "46,0,0,0"
        }, game);

        var text = File.ReadAllText(path);
        Assert.Contains("PresetPath=user.ini", text);
        Assert.Contains("[INPUT]", text);
        Assert.Contains("KeyOverlay=45,0,0,0", text);
        Assert.Contains("KeyScreenshot=46,0,0,0", text);
        Assert.Contains("[SCREENSHOT]", text);
        Assert.Contains("SavePath=", text);
    }

    [Fact]
    public void ReShadeSettingsApplier_UpdatesOwnedPreferenceKeysWithoutDroppingUnrelatedValues()
    {
        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "ReShade.ini"),
            "[INPUT]\nKeyOverlay=36,0,0,0\nOtherKey=keep\n[SCREENSHOT]\nSavePath=old\nFormat=png\n");
        var game = Game(gameDir, "Demo Game");
        ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings
        {
            ScreenshotPath = Path.Combine(temp.Path, "new"),
            OverlayHotkey = "47,0,0,0",
            ScreenshotHotkey = "48,0,0,0"
        }, game);
        var text = File.ReadAllText(Path.Combine(gameDir, "ReShade.ini"));
        Assert.Contains("KeyOverlay=47,0,0,0", text);
        Assert.Contains("KeyScreenshot=48,0,0,0", text);
        Assert.Contains("OtherKey=keep", text);
        Assert.Contains("Format=png", text);
        Assert.DoesNotContain("SavePath=old", text);
    }

    [Fact]
    public void ReShadeSettingsApplier_PerGameScreenshotFolderUsesGameName()
    {
        using var temp = new TempDir();
        var game = Game(temp.Path, "Game Name");
        var path = ReShadeGlobalSettingsApplier.ResolveScreenshotPath(new LinuxAppSettings
        {
            ScreenshotPath = Path.Combine(temp.Path, "screens"),
            PerGameScreenshotFolders = true
        }, game);
        Assert.EndsWith(Path.Combine("screens", "Game Name"), path);
    }

    [Fact]
    public void ReShadeSettingsApplier_EmptyScreenshotPathLeavesScreenshotSectionUntouched()
    {
        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "ReShade.ini"), "[SCREENSHOT]\nSavePath=keep\n");
        ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings { ScreenshotPath = "" }, Game(gameDir, "Demo"));
        Assert.Contains("SavePath=keep", File.ReadAllText(Path.Combine(gameDir, "ReShade.ini")));
    }

    [Fact]
    public void ReShadeSettingsApplier_RecognizesCanonicalIniCaseInsensitively()
    {
        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gameDir);
        var lower = Path.Combine(gameDir, "reshade.ini");
        File.WriteAllText(lower, "[INPUT]\nKeyOverlay=1,0,0,0\n");
        var returned = ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings(), Game(gameDir, "Demo"));
        Assert.Equal(lower, returned);
        Assert.False(File.Exists(Path.Combine(gameDir, "ReShade.ini")) && !string.Equals(lower, Path.Combine(gameDir, "ReShade.ini"), StringComparison.Ordinal));
    }

    [Fact]
    public void ReShadeSettingsApplier_WritesWindowsStyleScreenshotPathForWindowsRuntime()
    {
        Assert.Equal("C:\\Games\\Shots", ReShadeGlobalSettingsApplier.ToWindowsStylePath("C:/Games/Shots"));
        Assert.Equal("Z:\\home\\user\\Shots", ReShadeGlobalSettingsApplier.ToWindowsStylePath("/home/user/Shots"));
    }

    private static LinuxSettingsStore NewStore(string root) => new(
        Path.Combine(root, "config", "settings.json"),
        Path.Combine(root, "cache"),
        Path.Combine(root, "data"));

    private static DetectedGame Game(string directory, string name) => new()
    {
        Name = name,
        InstallPath = directory,
        BinaryDirectory = directory,
        Runner = "Wine",
        Source = "Test"
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhi-settings-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
