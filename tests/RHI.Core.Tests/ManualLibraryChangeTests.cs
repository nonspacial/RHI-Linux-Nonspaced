using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class ManualLibraryChangeTests
{
    [Fact]
    public void Remove_FindsTheEntryAfterAnEditMovedTheRoot()
    {
        using var tree = new DetectionTestTree();
        var saved = tree.Dir("games", "Game");
        var state = new LinuxGameLibraryState();
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Game", InstallPath = saved + "/" });
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Other", InstallPath = tree.Dir("games", "Other") });
        // Edit game set the root one level down; the library identity keeps the saved folder.
        var listed = new DetectedGame
        {
            Name = "Game", Source = "Manual", IsManuallyAdded = true,
            InstallPath = tree.Dir("games", "Game", "Inner"), LibraryIdentityPath = saved
        };

        Assert.Equal(1, state.RemoveManualEntriesFor(listed));
        Assert.Equal("Other", Assert.Single(state.ManualGames).Name);
    }

    [Fact]
    public void Remove_FindsTheEntryTheManifestMovedBeforeTheIdentityWasTaken()
    {
        using var tree = new DetectionTestTree();
        var saved = tree.Dir("games", "Game");
        var state = new LinuxGameLibraryState();
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Game", InstallPath = saved });
        var moved = tree.Dir("games", "Game", "Retail");
        var listed = new DetectedGame
        {
            Name = "Game", Source = "Manual", IsManuallyAdded = true,
            InstallPath = moved, LibraryIdentityPath = moved, ManifestInstallRoot = saved
        };

        Assert.Equal(1, state.RemoveManualEntriesFor(listed));
        Assert.Empty(state.ManualGames);
    }

    [Fact]
    public void Remove_ReportsNothingFound()
    {
        var state = new LinuxGameLibraryState();
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Other", InstallPath = "/games/other" });

        Assert.Equal(0, state.RemoveManualEntriesFor(new DetectedGame { Name = "X", InstallPath = "/games/x", Source = "Manual" }));
        Assert.Single(state.ManualGames);
    }

    [Fact]
    public async Task BusyGate_HoldsWorkUntilTheBusyOperationEnds()
    {
        var gate = new BusyGate();
        await gate.WhenIdleAsync(); // idle: immediate

        gate.SetBusy(true);
        var waiting = gate.WhenIdleAsync();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        gate.SetBusy(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
