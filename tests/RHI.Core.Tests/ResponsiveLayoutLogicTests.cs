using RHI.Desktop.Layout;
using Xunit;

namespace RHI.Core.Tests;

public sealed class ResponsiveLayoutLogicTests
{
    // Monospace stand-in for the real text measurement: 7 px per character.
    private static double Mono(string s) => s.Length * 7.0;

    private const string PopExe = "/home/nope/Games/ubisoft/drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Prince of Persia The Lost Crown/PrinceOfPersia_TLC.exe";

    [Fact]
    public void PathThatFitsIsReturnedUnchanged()
        => Assert.Equal(PopExe, PathEllipsis.Compact(PopExe, Mono(PopExe), Mono));

    [Fact]
    public void LongPathKeepsFirstFolderAndFileName()
    {
        var result = PathEllipsis.Compact(PopExe, 60 * 7, Mono);
        Assert.True(Mono(result) <= 60 * 7, result);
        Assert.StartsWith("/home/", result);
        Assert.EndsWith("/PrinceOfPersia_TLC.exe", result);
        Assert.Contains("/…/", result);
    }

    [Fact]
    public void WiderSlotKeepsMoreSegmentsAndOnlyElidesWholeSegments()
    {
        var narrow = PathEllipsis.Compact(PopExe, 60 * 7, Mono);
        var wide = PathEllipsis.Compact(PopExe, 110 * 7, Mono);
        Assert.True(wide.Length > narrow.Length);
        Assert.True(Mono(wide) <= 110 * 7, wide);
        Assert.Contains("/Prince of Persia The Lost Crown/PrinceOfPersia_TLC.exe", wide);
        Assert.Single(wide.Split('…').Skip(1));
        // Every kept segment is a whole segment of the original path.
        foreach (var segment in wide.Replace("/…/", "/").Split('/').Where(s => s.Length > 0))
            Assert.Contains("/" + segment, PopExe);
    }

    [Fact]
    public void VeryNarrowPathFallsBackToTailThenCharacters()
    {
        var tail = PathEllipsis.Compact(PopExe, 29 * 7, Mono);
        Assert.Equal("…/PrinceOfPersia_TLC.exe", tail);
        var chars = PathEllipsis.Compact(PopExe, 12 * 7, Mono);
        Assert.Equal(12, chars.Length);
        Assert.Contains("…", chars);
        Assert.EndsWith("exe", chars);
    }

    [Fact]
    public void ResultNeverExceedsWidthAcrossAllWidths()
    {
        for (var w = 7; w <= Mono(PopExe); w += 3)
        {
            var result = PathEllipsis.Compact(PopExe, w, Mono);
            Assert.True(Mono(result) <= Math.Max(w, 7), $"{w}: {result}");
        }
    }

    [Fact]
    public void CreditLineIsShortenedInTheMiddleWhenAsked()
    {
        // #139 credit line: the start (file name) and the end (author) both matter.
        const string credit = "renodx-hogwartslegacy-extended-build.addon64, renodx-hogwartslegacy-lut.addon64 · by Ritsu, ShortFuse, Musa";
        for (var w = 7; w <= Mono(credit); w += 7)
        {
            var result = PathEllipsis.Compact(credit, w, Mono, middleForText: true);
            Assert.True(Mono(result) <= Math.Max(w, 7), $"{w}: {result}");
        }
        var mid = PathEllipsis.Compact(credit, 40 * 7, Mono, middleForText: true);
        Assert.Equal(40, mid.Length);
        Assert.StartsWith("renodx-hogwartsleg", mid);
        Assert.EndsWith("ShortFuse, Musa", mid);
        Assert.Single(mid.Split('…').Skip(1));
        // Without the flag the same text keeps the end-trim behaviour.
        Assert.EndsWith("…", PathEllipsis.Compact(credit, 40 * 7, Mono));
        Assert.Equal(credit, PathEllipsis.Compact(credit, Mono(credit), Mono, middleForText: true));
    }

    [Fact]
    public void NonPathTextIsTrimmedAtTheEnd()
    {
        const string status = "nvngx_dlss.dll missing — direct swap blocked";
        var result = PathEllipsis.Compact(status, 20 * 7, Mono);
        Assert.Equal("nvngx_dlss.dll miss…", result);
        Assert.False(PathEllipsis.LooksLikePath(status));
    }

    [Fact]
    public void WindowsAndHomePathsAreRecognised()
    {
        Assert.True(PathEllipsis.LooksLikePath(@"C:\Games\Tunic\Tunic.exe"));
        Assert.True(PathEllipsis.LooksLikePath("~/Games/Tunic"));
        var result = PathEllipsis.Compact(@"C:\Games\Very Long Folder\Another Folder\Tunic.exe", 26 * 7, Mono);
        Assert.Equal(@"C:\Games\…\Tunic.exe", result);
    }

    [Fact]
    public void EmptyAndTinyInputs()
    {
        Assert.Equal("", PathEllipsis.Compact(null, 10, Mono));
        Assert.Equal("…", PathEllipsis.Compact(PopExe, 1, Mono));
        Assert.Equal(PopExe, PathEllipsis.Compact(PopExe, double.PositiveInfinity, Mono));
    }

    // Toolbar: QuickStart, HDR mods, |, Refresh, Add game, Shaders, |, Update all, Batch DLSS
    private static readonly double[] Widths = [90, 140, 9, 70, 80, 120, 9, 85, 95];
    private static readonly bool[] Separators = [false, false, true, false, false, false, true, false, false];

    [Fact]
    public void EverythingFitsNoOverflow()
    {
        var total = Widths.Sum() + 8 * (Widths.Length - 1);
        Assert.Equal(9, ToolbarOverflow.VisibleCount(Widths, Separators, 8, total, 30));
        Assert.Empty(ToolbarOverflow.Hidden(Separators, 9));
    }

    [Fact]
    public void ItemsOverflowFromTheRightInOrder()
    {
        var total = Widths.Sum() + 8 * (Widths.Length - 1);
        // One pixel short: only the last item goes; the first eight plus '…' (659 + 8 + 30) still fit.
        var kept = ToolbarOverflow.VisibleCount(Widths, Separators, 8, total - 1, 30);
        Assert.Equal(8, kept);
        Assert.Equal(new[] { 8 }, ToolbarOverflow.Hidden(Separators, kept));
        // 650 px: Update all no longer fits with '…'; the separator before it is dropped from the row as well.
        kept = ToolbarOverflow.VisibleCount(Widths, Separators, 8, 650, 30);
        Assert.Equal(6, kept);
        Assert.Equal(new[] { 7, 8 }, ToolbarOverflow.Hidden(Separators, kept));
    }

    [Fact]
    public void KeptRowNeverEndsWithASeparator()
    {
        for (var available = 0; available < 900; available += 5)
        {
            var kept = ToolbarOverflow.VisibleCount(Widths, Separators, 8, available, 30);
            if (kept > 0) Assert.False(Separators[kept - 1], $"available {available} kept {kept}");
            if (kept < Widths.Length)
            {
                var used = Widths.Take(kept).Sum() + 8 * Math.Max(0, kept - 1) + (kept > 0 ? 8 : 0) + 30;
                Assert.True(used <= available || kept == 0, $"available {available} kept {kept} used {used}");
            }
        }
    }

    [Fact]
    public void NothingFitsKeepsNone()
    {
        Assert.Equal(0, ToolbarOverflow.VisibleCount(Widths, Separators, 8, 60, 30));
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 7, 8 }, ToolbarOverflow.Hidden(Separators, 0));
    }

    [Fact]
    public void MismatchedFlagsAreRejected()
        => Assert.Throws<ArgumentException>(() => ToolbarOverflow.VisibleCount([1, 2], [false], 8, 100, 10));
}
