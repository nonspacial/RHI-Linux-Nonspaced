using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review nit: trashing is <c>rename(2)</c>; a copy happens only when the kernel says EXDEV (another file system
/// the mount table did not tell apart), and any other failure leaves the file where it is with its .trashinfo removed.
/// </summary>
public sealed class FreedesktopTrashRenameTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-trash-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private (FreedesktopTrash Trash, string Home, string File) Setup(Func<string, string, int> rename, bool folder = false)
    {
        var home = Path.Combine(_root, "home-trash");
        var trash = new FreedesktopTrash(home, _ => "/", () => new DateTime(2026, 11, 17, 9, 0, 0), "1000") { RenameForTesting = rename };
        var path = Path.Combine(_root, "shots", folder ? "Shots" : "Shot.png");
        if (folder)
        {
            Directory.CreateDirectory(Path.Combine(path, "sub"));
            File.WriteAllText(Path.Combine(path, "sub", "a.png"), "a");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "png");
        }
        return (trash, home, path);
    }

    [Fact]
    public void ARenameRefused_LeavesTheFile_AndNoCopy()
    {
        var (trash, home, file) = Setup((_, _) => 13 /* EACCES */);

        Assert.Null(trash.Trash(file, out var error));

        Assert.StartsWith(FreedesktopTrash.NotTrashedPrefix, error, StringComparison.Ordinal);
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(home, "files")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(home, "info")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exdev_IsCopiedWhole_ThenTheOriginalGoes(bool folder)
    {
        var (trash, home, path) = Setup((_, _) => 18 /* EXDEV */, folder);

        var moved = trash.Trash(path, out var error);

        Assert.Null(error);
        Assert.False(File.Exists(path) || Directory.Exists(path));
        Assert.Equal(folder ? "a" : "png", File.ReadAllText(folder ? Path.Combine(moved!, "sub", "a.png") : moved!));
        Assert.True(File.Exists(Path.Combine(home, "info", Path.GetFileName(path) + ".trashinfo")));
    }

    [Fact]
    public void TheRealRename_MovesOnOneFileSystem()
    {
        var (trash, home, file) = Setup(null!);
        trash.RenameForTesting = null;
        Assert.Equal(Path.Combine(home, "files", "Shot.png"), trash.Trash(file, out _));
        Assert.False(File.Exists(file));
    }
}
