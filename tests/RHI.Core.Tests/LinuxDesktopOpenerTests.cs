using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class LinuxDesktopOpenerTests
{
    [Fact]
    public void BuildCandidates_PreservesDirectoryWithSpacesAsSingleArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), "The Weird Hunter Demo");
        var xdg = LinuxDesktopOpener.BuildCandidates(path).First();

        Assert.Equal("xdg-open", xdg.FileName);
        Assert.Single(xdg.ArgumentList);
        Assert.Equal(Path.GetFullPath(path), xdg.ArgumentList[0]);
    }

    [Fact]
    public void BuildCandidates_IncludesDesktopFallbacks()
    {
        var path = Path.Combine(Path.GetTempPath(), "Game Folder");
        var candidates = LinuxDesktopOpener.BuildCandidates(path);

        Assert.Contains(candidates, x => x.FileName == "gio" && x.ArgumentList.Count == 2 && x.ArgumentList[0] == "open");
        Assert.Contains(candidates, x => x.FileName == "kde-open6");
        Assert.Contains(candidates, x => x.FileName == "kioclient6" && x.ArgumentList.Count == 2 && x.ArgumentList[0] == "exec");
    }
}
