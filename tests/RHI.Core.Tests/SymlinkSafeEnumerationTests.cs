using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>Recursive walks stay inside their tree when it contains a directory symlink (DET-25).</summary>
public sealed class SymlinkSafeEnumerationTests
{
    private static (DetectionTestTree Tree, string Root, string Outside) Fixture()
    {
        var tree = new DetectionTestTree();
        var root = tree.Dir("root");
        var outside = tree.Dir("outside");
        tree.File("{}", "root", "a.json");
        tree.File("{}", "root", "sub", "b.json");
        tree.File("{}", "outside", "secret.json");
        tree.Dir("outside", "empty-deep", "empty");
        tree.Link(outside, "root", "sub", "escape");
        File.CreateSymbolicLink(tree.Path_("root", "linked.json"), tree.Path_("outside", "secret.json"));
        return (tree, root, outside);
    }

    [Fact]
    public void Files_Directories_Entries_NeverLeaveTheTree()
    {
        var (tree, root, outside) = Fixture();
        using var _ = tree;

        var files = SafeFileEnumeration.Files(root, "*.json").Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal);
        Assert.Equal(["a.json", "linked.json", Path.Combine("sub", "b.json")], files);
        Assert.Equal([Path.Combine(root, "sub")], SafeFileEnumeration.Directories(root));
        var entries = SafeFileEnumeration.Entries(root).Select(e => e.FullName).ToList();
        Assert.Contains(Path.Combine(root, "sub", "escape"), entries); // reported, so callers can refuse it
        Assert.DoesNotContain(entries, e => e.Contains("secret", StringComparison.Ordinal) && !e.EndsWith("linked.json", StringComparison.Ordinal));
        Assert.All(entries, e => Assert.StartsWith(root, e));
    }

    [Fact]
    public void PurgeCache_CountsAndDeletesOnlyTheCache()
    {
        var (tree, root, outside) = Fixture();
        using var _ = tree;
        var store = new LinuxSettingsStore(tree.Path_("settings.json"), root, tree.Path_("data"));

        Assert.Equal(3, store.PurgeCache()); // a.json, sub/b.json and the linked.json link itself
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(outside, "secret.json")));
        Assert.True(Directory.Exists(Path.Combine(outside, "empty-deep", "empty")));
    }

    [Fact]
    public async Task WinePrefixSearch_FollowsALinkedPrefix_ButNoOtherLink()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        // A prefix kept on another disk and linked into ~/Games is still found.
        var realPrefix = tree.Dir("disk", "far-prefix");
        tree.File("", "disk", "far-prefix", "system.reg");
        tree.Exe("disk", "far-prefix", "drive_c", "Games", "Far Game", "FarGame.exe");
        tree.Link(realPrefix, "home", "Games", "far-prefix");
        // A link to some other tree (here one holding a prefix two levels down) is not walked.
        tree.File("", "elsewhere", "deep", "pfx", "system.reg");
        tree.Exe("elsewhere", "deep", "pfx", "drive_c", "Games", "Stray", "Stray.exe");
        tree.Link(tree.Path_("elsewhere"), "home", "Games", "everything");

        var games = await new WinePrefixDetector(home, []).DetectAsync();

        Assert.Equal("Far Game", Assert.Single(games).Name);
    }
}
