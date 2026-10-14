using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Refs #151 parts 1–2: a merged card takes how it starts (command, launcher hint, Edit game command, detected launch)
/// from one source only, and a library file with no Edit game command is written without a <c>LaunchCommand</c> key.
/// </summary>
public sealed class LaunchPairingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-launch-pair-" + Guid.NewGuid().ToString("N"));

    public LaunchPairingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DetectedGame LauncherChild() => new()
    {
        Name = "Game", Source = "Lutris", InstallPath = "/games/Game",
        LaunchCommand = "lutris 'lutris:rungame/ubisoft-connect'",
        ParentLauncherName = "Ubisoft Connect",
        UserLaunchCommand = "my-wrapper game",
        DetectedLaunch = new DetectedLaunch("lutris 'lutris:rungame/ubisoft-connect'", "Ubisoft Connect")
    };

    [Fact]
    public void Merge_FirstWithItsOwnCommand_TakesNoLaunchFieldFromADuplicate()
    {
        var first = new DetectedGame { Name = "Game", Source = "Heroic", InstallPath = "/games/Game", LaunchCommand = "xdg-open 'heroic://launch?appName=g'" };

        var merged = GameLibraryScanner.Merge([first, LauncherChild()]);

        Assert.Equal("xdg-open 'heroic://launch?appName=g'", merged.LaunchCommand);
        // The hint, the detected launch and the Edit game command describe the Lutris command, not Heroic's.
        Assert.Null(merged.ParentLauncherName);
        Assert.Null(merged.DetectedLaunch);
        Assert.Null(merged.UserLaunchCommand);
    }

    [Fact]
    public void Merge_FirstWithoutACommand_TakesTheDuplicatesLaunchAsOneUnit()
    {
        var first = new DetectedGame { Name = "Game", Source = "Wine", InstallPath = "/games/Game" };
        var child = LauncherChild();

        var merged = GameLibraryScanner.Merge([first, child]);

        Assert.Equal(child.LaunchCommand, merged.LaunchCommand);
        Assert.Equal("Ubisoft Connect", merged.ParentLauncherName);
        Assert.Equal(child.DetectedLaunch, merged.DetectedLaunch);
        Assert.Equal("my-wrapper game", merged.UserLaunchCommand);
    }

    [Fact]
    public void Merge_ManualDuplicatesCommand_ReplacesTheWholeLaunch()
    {
        var first = LauncherChild();
        var manual = new DetectedGame
        {
            Name = "Game", Source = "Manual", InstallPath = "/games/Game", IsManuallyAdded = true, LaunchCommand = "run-game"
        };

        var merged = GameLibraryScanner.Merge([first, manual]);

        Assert.Equal("run-game", merged.LaunchCommand);
        Assert.Null(merged.ParentLauncherName);
        Assert.Null(merged.DetectedLaunch);
        Assert.Null(merged.UserLaunchCommand);
    }

    [Fact]
    public void MergedCard_ThroughIdentityResolution_RestoresItsOwnDetectedCommand()
    {
        var first = new DetectedGame { Name = "Game", Source = "Heroic", InstallPath = "/games/Game", LaunchCommand = "heroic-cmd" };
        var merged = GameLibraryScanner.Merge([first, LauncherChild()]);

        LinuxGameIdentityService.ApplyDetectedIdentity(merged, edit: null);

        Assert.Equal("heroic-cmd", merged.LaunchCommand);
        Assert.Null(merged.ParentLauncherName);
        Assert.Equal(new DetectedLaunch("heroic-cmd", null), merged.DetectedLaunch);
    }

    [Fact]
    public void LibraryFile_EditWithoutCommand_HasNoLaunchCommandKey_AndSavesBackByteForByte()
    {
        var path = Path.Combine(_root, "game-library.json");
        var store = new LinuxGameLibraryStore(path);
        var state = new LinuxGameLibraryState();
        state.GameEdits["steam:10"] = new LinuxGameEditOverride { LaunchProvider = "Heroic" };
        state.GameEdits["steam:20"] = new LinuxGameEditOverride { LaunchCommand = "run-it" };
        store.Save(state);
        var written = File.ReadAllBytes(path);

        using (var doc = JsonDocument.Parse(written))
        {
            var edits = doc.RootElement.GetProperty("GameEdits");
            Assert.False(edits.GetProperty("steam:10").TryGetProperty("LaunchCommand", out _));
            Assert.Equal("run-it", edits.GetProperty("steam:20").GetProperty("LaunchCommand").GetString());
        }

        // A file in that shape (what builds before the Edit game command wrote) is saved back unchanged.
        store.Save(store.Load());
        Assert.Equal(written, File.ReadAllBytes(path));
        Assert.Equal("run-it", store.Load().GameEdits["steam:20"].LaunchCommand);
    }
}
