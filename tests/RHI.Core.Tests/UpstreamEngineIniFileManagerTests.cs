using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class UpstreamEngineIniFileManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-upstream-engineini-tests-" + Guid.NewGuid().ToString("N"));

    public UpstreamEngineIniFileManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ParseTemplate_ParsesMinimalUpstreamEngineFile()
    {
        var entries = UpstreamEngineIniFileManager.ParseTemplate("[SystemSettings]\nr.HDR.EnableHDROutput=1\n");

        var entry = Assert.Single(entries);
        Assert.Equal("SystemSettings", entry.Section);
        Assert.Equal("r.HDR.EnableHDROutput", entry.Key);
        Assert.Equal("1", entry.Value);
    }

    [Fact]
    public void ApplyAndCleanup_PreexistingValueIsOverwrittenThenRestored()
    {
        var game = CreateGame("RestoreProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nr.HDR.EnableHDROutput=0\nUserKeep=42\n");

        var applied = UpstreamEngineIniFileManager.ApplyTemplate(
            game,
            "black-myth-wukong.ini",
            "[SystemSettings]\nr.HDR.EnableHDROutput=1\n",
            new string('A', 64),
            "https://example.test/black-myth-wukong.ini");

        Assert.NotNull(applied.State);
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(ini));
        Assert.Contains("UserKeep=42", File.ReadAllText(ini));

        UpstreamEngineIniFileManager.Cleanup(applied.State);

        var restored = File.ReadAllText(ini);
        Assert.Contains("r.HDR.EnableHDROutput=0", restored);
        Assert.Contains("UserKeep=42", restored);
    }

    [Fact]
    public void Cleanup_UserEditAfterInstallWinsAndRelinquishesOwnership()
    {
        var game = CreateGame("UserEditProject");
        var applied = UpstreamEngineIniFileManager.ApplyTemplate(
            game,
            "custom.ini",
            "[SystemSettings]\nr.RHI.Template=1\n",
            new string('B', 64),
            null);
        Assert.NotNull(applied.State);
        var ini = applied.State!.IniPath;
        File.WriteAllText(ini, File.ReadAllText(ini).Replace("r.RHI.Template=1", "r.RHI.Template=7", StringComparison.Ordinal));

        UpstreamEngineIniFileManager.Cleanup(applied.State);

        Assert.True(File.Exists(ini));
        Assert.Contains("r.RHI.Template=7", File.ReadAllText(ini));
    }

    [Fact]
    public void ApplyUpdate_ObsoleteOwnedKeyIsRestoredAndNewKeyIsTracked()
    {
        var game = CreateGame("UpdateProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nr.Old=original\nKeep=1\n");

        var first = UpstreamEngineIniFileManager.ApplyTemplate(
            game, "first.ini", "[SystemSettings]\nr.Old=managed\nr.Keep=2\n", new string('C', 64), null);
        Assert.NotNull(first.State);

        var second = UpstreamEngineIniFileManager.ApplyTemplate(
            game, "second.ini", "[SystemSettings]\nr.New=3\n", new string('D', 64), null, first.State);
        Assert.NotNull(second.State);

        var text = File.ReadAllText(ini);
        Assert.Contains("r.Old=original", text);
        Assert.DoesNotContain("r.Keep=", text);
        Assert.Contains("r.New=3", text);
        Assert.Contains("Keep=1", text);

        UpstreamEngineIniFileManager.Cleanup(second.State);
        var after = File.ReadAllText(ini);
        Assert.DoesNotContain("r.New=", after);
        Assert.Contains("r.Old=original", after);
        Assert.Contains("Keep=1", after);
    }

    [Fact]
    public void Cleanup_FileCreatedByRhiIsDeletedWhenOnlyManagedKeysRemain()
    {
        var game = CreateGame("CreatedProject");
        var applied = UpstreamEngineIniFileManager.ApplyTemplate(
            game, "created.ini", "[SystemSettings]\nr.Created=1\n", new string('E', 64), null);
        Assert.NotNull(applied.State);
        Assert.True(applied.State!.FileCreatedByRhi);
        Assert.True(File.Exists(applied.State.IniPath));

        UpstreamEngineIniFileManager.Cleanup(applied.State);

        Assert.False(File.Exists(applied.State.IniPath));
    }

    [Fact]
    public void ApplyAndCleanup_ReadOnlyEngineIni_AreWrittenAndTheFileStaysLocked()
    {
        // Review w2-trust major 2 (upstream 2.8.5 parity): a locked Engine.ini blocks neither install nor removal.
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var game = CreateGame("LockedTemplateProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "[SystemSettings]\nr.HDR.EnableHDROutput=0\nUserKeep=42\n");
        File.SetUnixFileMode(ini, locked);

        var applied = UpstreamEngineIniFileManager.ApplyTemplate(
            game, "locked.ini", "[SystemSettings]\nr.HDR.EnableHDROutput=1\n", new string('C', 64), null);

        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(ini));
        Assert.Equal(locked, File.GetUnixFileMode(ini));

        UpstreamEngineIniFileManager.Cleanup(applied.State);

        Assert.Contains("r.HDR.EnableHDROutput=0", File.ReadAllText(ini));
        Assert.Equal(locked, File.GetUnixFileMode(ini));
    }

    [Fact]
    public void RenoDxRollback_RestoresALockedEngineIniAndKeepsItLocked()
    {
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var game = CreateGame("RollbackProject");
        var ini = LumaEngineIniOverrideManager.ResolveEngineIniPath(game)!;
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "changed by the failed install");
        File.SetUnixFileMode(ini, locked);

        var notes = RenoDxDeploymentService.RestoreSnapshots(
            [RenoDxDeploymentService.FileSnapshot.EngineIni(ini, "[SystemSettings]\nUserKeep=42\n"u8.ToArray())],
            Path.Combine(_root, "fallback"));

        Assert.Empty(notes);
        Assert.Equal("[SystemSettings]\nUserKeep=42\n", File.ReadAllText(ini));
        Assert.Equal(locked, File.GetUnixFileMode(ini));
    }

    private DetectedGame CreateGame(string project)
    {
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var prefix = Path.Combine(root, "pfx");
        var binary = Path.Combine(root, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local"));
        return new DetectedGame
        {
            Name = "Engine.ini Template Test",
            InstallPath = root,
            BinaryDirectory = binary,
            ExecutablePath = Path.Combine(binary, "Game-Win64-Shipping.exe"),
            PrefixPath = prefix,
            Source = "Steam",
            Runner = "Proton",
            EngineIniProjectOverride = project
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
