using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class LutrisGameDetectorTests
{
    [Fact]
    public void WineSection_DetectsLutrisWineRunner()
    {
        var yaml = """
                   game:
                     exe: /games/Test/Game.exe
                     prefix: /games/Test/prefix
                   wine:
                     version: wine-ge-8-26-x86_64
                   """;

        var runtime = LutrisGameDetector.ParseWineRuntimeForTests(yaml);

        Assert.Equal("wine-ge-8-26-x86_64", runtime.Version);
        Assert.Equal("wine", runtime.LayerType);
    }

    [Fact]
    public void WineSection_DetectsLutrisProtonRunner()
    {
        var yaml = """
                   game:
                     exe: /games/Test/Game.exe
                     prefix: /games/Test/prefix
                   wine:
                     version: GE-Proton10-20
                   """;

        var runtime = LutrisGameDetector.ParseWineRuntimeForTests(yaml);

        Assert.Equal("GE-Proton10-20", runtime.Version);
        Assert.Equal("proton", runtime.LayerType);
    }

    [Fact]
    public void RelativeExe_ResolvesAgainstPrefix_NotWholePrefix()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "rhi-lutris-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        var exe = Path.Combine(gameDir, "Battle.net Launcher.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var resolved = LutrisGameDetector.ResolveRelativeExe(
                "drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe", prefix, null);
            Assert.Equal(exe, resolved);

            Assert.Equal(exe, LutrisGameDetector.ResolveRelativeExe("$GAMEDIR/drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe", prefix, null));
            Assert.Null(LutrisGameDetector.ResolveRelativeExe("_xXx_AUTO_WIN32_xXx_", prefix, null));
            Assert.Null(LutrisGameDetector.ResolveRelativeExe("drive_c/missing.exe", prefix, null));
            Assert.True(LutrisGameDetector.IsWinePrefixRoot(prefix));
            Assert.False(LutrisGameDetector.IsWinePrefixRoot(gameDir));
        }
        finally
        {
            try { Directory.Delete(prefix, recursive: true); } catch { }
        }
    }
}
