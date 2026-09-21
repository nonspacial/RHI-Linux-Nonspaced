using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LumaEngineIniOverrideManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-engineini-tests-" + Guid.NewGuid().ToString("N"));

    public LumaEngineIniOverrideManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Apply_ExistingUserKeyIsNotOverwrittenOrOwned()
    {
        var game = CreateGameWithPrefixOverride("MetaProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nr.UserOwned=9\n");

        var state = LumaEngineIniOverrideManager.Apply(game,
        [
            new LumaEngineIniEntry("SystemSettings", "r.UserOwned", "1"),
            new LumaEngineIniEntry("SystemSettings", "r.RhiOwned", "2")
        ]);

        Assert.NotNull(state);
        Assert.Single(state!.Entries);
        Assert.Equal("r.RhiOwned", state.Entries[0].Key);
        var text = File.ReadAllText(ini);
        Assert.Contains("r.UserOwned=9", text);
        Assert.Contains("r.RhiOwned=2", text);
    }

    [Fact]
    public void ApplyAndCleanup_RemovesOnlyInsertedKeysAndPreservesUserContent()
    {
        var game = CreateGameWithPrefixOverride("CleanupProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nUserKeep=42\n");
        var state = LumaEngineIniOverrideManager.Apply(game,
            [new LumaEngineIniEntry("SystemSettings", "r.Luma.Test", "1")]);

        LumaEngineIniOverrideManager.Cleanup(state);

        var after = File.ReadAllText(ini);
        Assert.Contains("UserKeep=42", after);
        Assert.DoesNotContain("r.Luma.Test=", after);
    }

    [Fact]
    public void Cleanup_PreservesUserEditMadeAfterInstall()
    {
        var game = CreateGameWithPrefixOverride("UserEditProject");
        var state = LumaEngineIniOverrideManager.Apply(game,
            [new LumaEngineIniEntry("SystemSettings", "r.Luma.UserEdit", "1")]);
        Assert.NotNull(state);
        var ini = state!.IniPath;
        File.WriteAllText(ini, File.ReadAllText(ini).Replace("r.Luma.UserEdit=1", "r.Luma.UserEdit=7", StringComparison.Ordinal));

        LumaEngineIniOverrideManager.Cleanup(state);

        Assert.Contains("r.Luma.UserEdit=7", File.ReadAllText(ini));
    }

    [Fact]
    public void ApplyUpdate_RemovesObsoleteOwnedKeyAndAddsCurrentMetadata()
    {
        var game = CreateGameWithPrefixOverride("UpdateProject");
        var first = LumaEngineIniOverrideManager.Apply(game,
        [
            new LumaEngineIniEntry("SystemSettings", "r.Luma.Old", "1"),
            new LumaEngineIniEntry("SystemSettings", "r.Luma.Keep", "2")
        ]);
        Assert.NotNull(first);

        var second = LumaEngineIniOverrideManager.Apply(game,
        [
            new LumaEngineIniEntry("SystemSettings", "r.Luma.Keep", "3"),
            new LumaEngineIniEntry("/Script/Engine.RendererSettings", "r.Luma.New", "4")
        ], first);

        Assert.NotNull(second);
        var text = File.ReadAllText(second!.IniPath);
        Assert.DoesNotContain("r.Luma.Old=", text);
        Assert.Contains("r.Luma.Keep=3", text);
        Assert.Contains("r.Luma.New=4", text);
        Assert.Equal(2, second.Entries.Count);
    }

    [Fact]
    public void ResolveEngineIniPath_ManifestLocalAppDataOverrideMapsInsideWinePrefix()
    {
        var game = CreateGameWithPrefixOverride(@"%LOCALAPPDATA%\PinnedProject\Saved\Config");

        var path = LumaEngineIniOverrideManager.ResolveEngineIniPath(game);

        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine("PinnedProject", "Saved", "Config", "Windows", "Engine.ini"), path!, StringComparison.Ordinal);
        Assert.StartsWith(game.PrefixPath!, path, StringComparison.Ordinal);

        game.EngineIniProjectOverride = "../escape";
        Assert.Null(LumaEngineIniOverrideManager.ResolveEngineIniPath(game));
    }

    [Fact]
    public void ResolveEngineIniPath_LocalUnrealProjectUsesExistingSavedConfigWithoutManifestOverride()
    {
        var project = Path.Combine(_root, "LocalGame", "Sandfall");
        var binaries = Path.Combine(project, "Binaries", "Win64");
        var config = Path.Combine(project, "Saved", "Config", "Windows");
        Directory.CreateDirectory(binaries);
        Directory.CreateDirectory(config);
        var ini = Path.Combine(config, "Engine.ini");
        File.WriteAllText(ini, "[SystemSettings]\nKeep=1\n");
        var game = new DetectedGame
        {
            Name = "Local Unreal",
            InstallPath = Path.Combine(_root, "LocalGame"),
            BinaryDirectory = binaries,
            ExecutablePath = Path.Combine(binaries, "Local-Win64-Shipping.exe"),
            Runner = "Wine"
        };

        var resolved = LumaEngineIniOverrideManager.ResolveEngineIniPath(game);

        Assert.Equal(ini, resolved);
    }

    private DetectedGame CreateGameWithPrefixOverride(string projectOverride)
    {
        var gameRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(gameRoot, "GameProject", "Binaries", "Win64");
        var prefix = Path.Combine(gameRoot, "pfx");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local"));
        return new DetectedGame
        {
            Name = "Generic UE Metadata Test",
            InstallPath = gameRoot,
            BinaryDirectory = binary,
            ExecutablePath = Path.Combine(binary, "GameProject-Win64-Shipping.exe"),
            PrefixPath = prefix,
            Source = "Steam",
            Runner = "Proton",
            EngineIniProjectOverride = projectOverride
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
