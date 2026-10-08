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

    private const UnixFileMode Locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Fact]
    public void ApplyAndCleanup_ReadOnlyEngineIni_AreWrittenAndTheFileStaysLocked()
    {
        // Review w2-trust major 2: upstream 2.8.5 clears read-only, writes, locks again; a lock never blocks
        // install or removal.
        var game = CreateGameWithPrefixOverride("LockedProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nUserKeep=42\n");
        File.SetUnixFileMode(ini, Locked);

        var state = LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Luma.Locked", "1")]);

        Assert.NotNull(state);
        Assert.Contains("r.Luma.Locked=1", File.ReadAllText(ini));
        Assert.Equal(Locked, File.GetUnixFileMode(ini));

        LumaEngineIniOverrideManager.Cleanup(state);

        Assert.DoesNotContain("r.Luma.Locked=", File.ReadAllText(ini));
        Assert.Contains("UserKeep=42", File.ReadAllText(ini));
        Assert.Equal(Locked, File.GetUnixFileMode(ini));
    }

    [Fact]
    public void Apply_SymlinkedEngineIniInsideThePrefix_IsWrittenThroughAndTheLinkKept()
    {
        var game = CreateGameWithPrefixOverride("LinkedProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        var shared = Path.Combine(game.PrefixPath!, "drive_c", "tweaks", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "[SystemSettings]\nUserKeep=42\n");
        File.CreateSymbolicLink(ini, shared);

        LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Luma.Linked", "1")]);

        Assert.NotNull(new FileInfo(ini).LinkTarget);
        Assert.Contains("r.Luma.Linked=1", File.ReadAllText(shared));
    }

    [Fact]
    public void Apply_SymlinkedEngineIniLeavingThePrefix_IsRefusedAndTheTargetLeftAlone()
    {
        var game = CreateGameWithPrefixOverride("EscapeProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        var outside = Path.Combine(_root, "elsewhere", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "victim");
        File.CreateSymbolicLink(ini, outside);

        var refused = Assert.Throws<IOException>(() =>
            LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Luma.Escape", "1")]));

        Assert.Equal($"{LumaEngineIniOverrideManager.LinkRefusalPrefix}{ini} -> {outside}", refused.Message);
        Assert.Equal("victim", File.ReadAllText(outside));
    }

    // w2-trust re-review, finding 1: every component of the path counts, not only the file. A config folder linked to a
    // folder outside the prefix used to have the outside Engine.ini written.
    [Fact]
    public void Apply_EngineIniBehindASymlinkedFolderLeavingThePrefix_IsRefusedAndTheTargetLeftAlone()
    {
        var game = CreateGameWithPrefixOverride("FolderEscapeProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        var configFolder = Path.GetDirectoryName(ini)!;
        Directory.CreateDirectory(Path.GetDirectoryName(configFolder)!);
        var outsideFolder = Directory.CreateDirectory(Path.Combine(_root, "elsewhere-folder")).FullName;
        var outside = Path.Combine(outsideFolder, "Engine.ini");
        File.WriteAllText(outside, "victim");
        Directory.CreateSymbolicLink(configFolder, outsideFolder);

        var refused = Assert.Throws<IOException>(() =>
            LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Luma.Escape", "1")]));

        Assert.Equal($"{LumaEngineIniOverrideManager.LinkRefusalPrefix}{ini} -> {outside}", refused.Message);
        Assert.Equal("victim", File.ReadAllText(outside));
    }

    [Fact]
    public void Apply_PrefixThatIsItselfASymlink_IsWrittenInside()
    {
        // A compatdata prefix moved to another drive and linked back: the roots resolve through the same link.
        var game = CreateGameWithPrefixOverride("LinkedPrefixProject");
        var realPrefix = Path.Combine(_root, "other-drive", "pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(realPrefix)!);
        Directory.Move(game.PrefixPath!, realPrefix);
        Directory.CreateSymbolicLink(game.PrefixPath!, realPrefix);
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;

        LumaEngineIniOverrideManager.Apply(game, [new LumaEngineIniEntry("SystemSettings", "r.Luma.Moved", "1")]);

        Assert.Contains("r.Luma.Moved=1", File.ReadAllText(ini));
        Assert.Null(new FileInfo(ini).LinkTarget);
    }

    // w2-trust re-review, nit 2: the allowed roots are the nearest project folder and the prefix; the walk stops at the
    // prefix, so a library under a folder named Saved never makes its parent a root.
    [Fact]
    public void LinkPolicy_Roots_AreTheNearestProjectFolderAndThePrefix_Only()
    {
        var ini = "/mnt/Saved/library/pfx/drive_c/users/u/AppData/Local/Game/Saved/Config/Windows/Engine.ini";

        var policy = LumaEngineIniOverrideManager.LinkPolicy(ini);

        Assert.Equal(["/mnt/Saved/library/pfx/drive_c/users/u/AppData/Local/Game", "/mnt/Saved/library/pfx"], policy.AllowedRoots);
        Assert.Equal(["/mnt/Saved/library/pfx"],
            LumaEngineIniOverrideManager.LinkPolicy("/mnt/Saved/library/pfx/drive_c/Game/Engine.ini").AllowedRoots);
        Assert.Equal(["/games/Saved/Native/Project"],
            LumaEngineIniOverrideManager.LinkPolicy("/games/Saved/Native/Project/Saved/Config/Linux/Engine.ini").AllowedRoots);
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
