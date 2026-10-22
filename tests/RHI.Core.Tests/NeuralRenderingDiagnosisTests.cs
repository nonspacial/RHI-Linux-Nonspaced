using System.Diagnostics;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #234: the Check Neural Rendering page's log signatures, bounded log reads and anti-cheat list
/// (<see cref="NeuralRenderingDiagnosis"/>, the one owner). Every fixture lives in a temp folder; nothing outside it is read.
/// </summary>
public sealed class NeuralRenderingDiagnosisTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(20);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-check-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _bin;
    private readonly string _prefix;

    public NeuralRenderingDiagnosisTests()
    {
        _game = Directory.CreateDirectory(Path.Combine(_root, "games", "Borderlands 3")).FullName;
        _bin = Directory.CreateDirectory(Path.Combine(_game, "OakGame", "Binaries", "Win64")).FullName;
        _prefix = Directory.CreateDirectory(Path.Combine(_root, "prefix")).FullName;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private DetectedGame Game() => new()
    {
        Name = "Borderlands 3", Source = "Manual", Runner = "Wine", InstallPath = _game, BinaryDirectory = _bin,
        ExecutablePath = Path.Combine(_bin, "Borderlands3.exe"), PrefixPath = _prefix
    };

    private string[] Roots => [_game, _bin, _prefix];

    // ---- signatures -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("[DLSS5] NR skipped evaluation because feature update failed (frame 120)", NrLogSignature.NeuralRenderingSkipped, NrFixHint.NrDllForCard)]
    [InlineData("NGX: CreateFeature(18) returned 0xBAD00001 (FeatureNotSupported)", NrLogSignature.FeatureNotSupported, NrFixHint.NrDllForCard)]
    [InlineData("NGX: CreateFeature(18) returned 0xbad00001", NrLogSignature.FeatureNotSupported, NrFixHint.NrDllForCard)]
    [InlineData("shader.fx(12,3): error E5017: cannot compile", NrLogSignature.ShaderCompiler, NrFixHint.NativeD3DCompiler)]
    [InlineData("shader.fx(40,1): error E5005", NrLogSignature.ShaderCompiler, NrFixHint.NativeD3DCompiler)]
    [InlineData("DLSS5_Feed.fx: error X3506: unrecognized compiler target 'cs_5_1'", NrLogSignature.ShaderCompiler, NrFixHint.NativeD3DCompiler)]
    [InlineData("compiling with /fastopt failed", NrLogSignature.ShaderCompiler, NrFixHint.NativeD3DCompiler)]
    [InlineData("Unhandled exception 0xC0000005 at 0x00007FF6", NrLogSignature.CrashOrDeviceLost, NrFixHint.RemoveNrMethod)]
    [InlineData("vkQueueSubmit: VK_ERROR_DEVICE_LOST", NrLogSignature.CrashOrDeviceLost, NrFixHint.RemoveNrMethod)]
    [InlineData("stopped: the D3D12 device was removed (see dlss5-feed.log)", NrLogSignature.CrashOrDeviceLost, NrFixHint.RemoveNrMethod)]
    public void EachSignature_IsFound_WithItsLineAndFixHint(string line, NrLogSignature signature, NrFixHint fix)
    {
        var text = "ReShade 6.5.1 starting\nloading add-ons\n" + line + "\ndone\n";

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("ReShade.log", text));

        Assert.Equal(signature, finding.Signature);
        Assert.Equal(fix, finding.Fix);
        Assert.Equal(fix, NeuralRenderingDiagnosis.FixFor(signature));
        Assert.Equal(3, finding.Line);
        Assert.Equal("ReShade.log", finding.Log);
        Assert.Equal(line.Trim(), finding.Excerpt);
    }

    [Theory]
    [InlineData("ReShade 6.5.1 starting\nAll add-ons loaded.\nNGX feature 18 created (Success)\n")]
    [InlineData("call stack 0x00007FFDE5017A30 0x00007FFDE5005B10\n")] // codes inside a hex address are no finding
    [InlineData("0xBAD000012 is not the code; 0xC00000050 neither\n")]
    [InlineData("")]
    public void ACleanLog_HasNoFinding(string text)
        => Assert.Empty(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

    [Fact]
    public void ASignatureSeenTwice_KeepsItsLastLine_AndFindingsComeInSignatureOrder()
    {
        var text = "0xC0000005\n0xBAD00001 first\nok\n0xBAD00001 last run\n";

        var findings = NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text);

        Assert.Equal([NrLogSignature.FeatureNotSupported, NrLogSignature.CrashOrDeviceLost], findings.Select(f => f.Signature));
        Assert.Equal(4, findings[0].Line);
        Assert.Equal("0xBAD00001 last run", findings[0].Excerpt);
        Assert.Equal(1, findings[1].Line);
    }

    [Fact]
    public void AnExcerpt_IsCappedAndHasNoControlCharacters()
    {
        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("OptiScaler.log", "0xBAD00001\t\u0007" + new string('x', 1000)));

        Assert.True(finding.Excerpt.Length <= NeuralRenderingDiagnosis.MaxExcerptChars + 1);
        Assert.DoesNotContain(finding.Excerpt, char.IsControl);
    }

    // ---- reading logs -----------------------------------------------------------------------------------------------

    [Fact]
    public void ALogBesideTheExecutable_IsReadWhole_WithLineNumbers_AndAMissingOneIsMissing()
    {
        File.WriteAllText(Path.Combine(_bin, "dlss5-feed.log"), "start\r\nNGX returned 0xBAD00001\r\n");

        var logs = NeuralRenderingDiagnosis.ReadLogs(Game());

        Assert.Equal(["ReShade.log", "OptiScaler.log", "dlss5-feed.log"], logs.Select(l => l.Name));
        Assert.Equal([NrLogState.Missing, NrLogState.Missing, NrLogState.Read], logs.Select(l => l.State));
        var finding = Assert.Single(logs[2].Findings);
        Assert.Equal(2, finding.Line);
        Assert.Equal(Path.Combine(_bin, "dlss5-feed.log"), logs[2].Path);
    }

    [Fact]
    public async Task AHugeLog_OnlyItsEndIsRead_InBoundedTime()
    {
        var path = Path.Combine(_bin, "ReShade.log");
        const long size = 48L * 1024 * 1024;
        using (var stream = File.Create(path))
        {
            stream.Write(Encoding.UTF8.GetBytes("0xC0000005 at the very start (outside the end that is read)\n"));
            var filler = Encoding.UTF8.GetBytes(new string('.', 1023) + "\n");
            while (stream.Length < size) stream.Write(filler);
            stream.Write(Encoding.UTF8.GetBytes("error E5017 near the end\n"));
        }

        var watch = Stopwatch.StartNew();
        var read = Task.Run(() => NeuralRenderingDiagnosis.ReadLog("ReShade.log", path, Roots));
        Assert.True(await Task.WhenAny(read, Task.Delay(HangGuard)) == read, "a huge log must be read in bounded time");
        var log = await read;
        watch.Stop();

        Assert.Equal(NrLogState.ReadEnd, log.State);
        var finding = Assert.Single(log.Findings); // the crash at the start lies outside the 2 MiB that are read
        Assert.Equal(NrLogSignature.ShaderCompiler, finding.Signature);
        Assert.Null(finding.Line); // only the end was read: its line number is not known
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void TheCap_IsTwoMebibytes_FromTheEnd()
    {
        Assert.Equal(2L * 1024 * 1024, NeuralRenderingDiagnosis.MaxLogBytes);
        var path = Path.Combine(_bin, "OptiScaler.log");
        // The signature line lies 100 bytes before the 2 MiB that are read: not seen.
        File.WriteAllText(path, "0xBAD00001 old\n" + new string('.', 2 * 1024 * 1024 + 99) + "\n");
        Assert.Empty(NeuralRenderingDiagnosis.ReadLog("OptiScaler.log", path, Roots).Findings);
        // Now it lies inside the last 2 MiB of a file still larger than that.
        File.WriteAllText(path, new string('-', 300) + "\n0xBAD00001 recent\n" + new string('.', 2 * 1024 * 1024 - 100) + "\n");
        var log = NeuralRenderingDiagnosis.ReadLog("OptiScaler.log", path, Roots);
        Assert.Equal(NrLogState.ReadEnd, log.State);
        Assert.Equal("0xBAD00001 recent", Assert.Single(log.Findings).Excerpt);
    }

    [PermissionDenialFact]
    public void AnUnreadableLog_IsAFinding_NeverAnException()
    {
        var path = Path.Combine(_bin, "ReShade.log");
        File.WriteAllText(path, "0xBAD00001\n");
        PermissionDenial.MakeUnreadable(path);

        var log = NeuralRenderingDiagnosis.ReadLog("ReShade.log", path, Roots);

        Assert.Equal(NrLogState.Unreadable, log.State);
        Assert.Empty(log.Findings);
    }

    [Fact]
    public async Task AFifoNamedLikeALog_IsNeverOpenedForReading_AndNothingBlocks()
    {
        var path = Path.Combine(_bin, "dlss5-feed.log");
        MkFifo(path);

        var read = Task.Run(() => NeuralRenderingDiagnosis.ReadLog("dlss5-feed.log", path, Roots));
        var inTime = await Task.WhenAny(read, Task.Delay(HangGuard)) == read;
        if (!inTime) { using (File.OpenWrite(path)) { } } // unblock a read that should never have started
        Assert.True(inTime, "a FIFO must never block the page");

        Assert.Equal(NrLogState.NotRegular, (await read).State);
    }

    [Fact]
    public async Task ALinkToAFifoInsideTheGame_IsNotRead_AndNothingBlocks()
    {
        var fifo = Path.Combine(_game, "pipe");
        MkFifo(fifo);
        var path = Path.Combine(_bin, "ReShade.log");
        File.CreateSymbolicLink(path, fifo);

        var read = Task.Run(() => NeuralRenderingDiagnosis.ReadLog("ReShade.log", path, Roots));
        var inTime = await Task.WhenAny(read, Task.Delay(HangGuard)) == read;
        if (!inTime) { using (File.OpenWrite(fifo)) { } }
        Assert.True(inTime, "a link to a FIFO must never block the page");

        Assert.Equal(NrLogState.NotRegular, (await read).State);
    }

    [Fact]
    public async Task AFifoSwappedInAfterTheOpen_IsNeverRead_TheOpenFileIs()
    {
        var path = Path.Combine(_bin, "OptiScaler.log");
        File.WriteAllText(path, "NGX 0xBAD00001\n");
        NeuralRenderingDiagnosis.AfterOpenForTesting.Value = opened =>
        {
            File.Move(opened, opened + ".moved");
            MkFifo(opened);
        };
        Task<NrCheckLog> read;
        try
        {
            read = Task.Run(() => NeuralRenderingDiagnosis.ReadLog("OptiScaler.log", path, Roots));
        }
        finally
        {
            NeuralRenderingDiagnosis.AfterOpenForTesting.Value = null; // the run took its own copy of the flow
        }
        var inTime = await Task.WhenAny(read, Task.Delay(HangGuard)) == read;
        if (!inTime) { using (File.OpenWrite(path)) { } }
        Assert.True(inTime, "what was opened is read; a FIFO put at the path afterwards is never opened");

        var log = await read;
        Assert.Equal(NrLogState.Read, log.State);
        Assert.Equal(NrLogSignature.FeatureNotSupported, Assert.Single(log.Findings).Signature);
    }

    [Fact]
    public void ALinkLeavingTheGameAndItsPrefix_IsNotRead()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var target = Path.Combine(outside, "secret.log");
        File.WriteAllText(target, "0xBAD00001\n");
        var path = Path.Combine(_bin, "ReShade.log");
        File.CreateSymbolicLink(path, target);

        var log = NeuralRenderingDiagnosis.ReadLog("ReShade.log", path, Roots);

        Assert.Equal(NrLogState.OutsideGame, log.State);
        Assert.Empty(log.Findings);
    }

    [Fact]
    public void ALinkToADevice_IsNotRead()
    {
        var path = Path.Combine(_bin, "dlss5-feed.log");
        File.CreateSymbolicLink(path, "/dev/zero");

        Assert.Equal(NrLogState.OutsideGame, NeuralRenderingDiagnosis.ReadLog("dlss5-feed.log", path, Roots).State);
    }

    [Fact]
    public void ALinkThatStaysInsideTheGameOrItsPrefix_IsRead()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_prefix, "drive_c", "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "ReShade.log"), "error X3506: unrecognized compiler target 'cs_5_1'\n");
        var path = Path.Combine(_bin, "ReShade.log");
        File.CreateSymbolicLink(path, Path.Combine(logs, "ReShade.log"));

        var log = NeuralRenderingDiagnosis.ReadLog("ReShade.log", path, Roots);

        Assert.Equal(NrLogState.Read, log.State);
        Assert.Equal(NrLogSignature.ShaderCompiler, Assert.Single(log.Findings).Signature);
    }

    [Fact]
    public void ALinkedParentFolderThatLeavesTheGame_IsNotRead()
    {
        // The log's own name is no link; a folder on its way is (the game's "Logs" folder linked to somewhere else).
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere-logs")).FullName;
        File.WriteAllText(Path.Combine(outside, "ReShade.log"), "0xBAD00001\n");
        var linked = Path.Combine(_bin, "Logs");
        Directory.CreateSymbolicLink(linked, outside);

        var log = NeuralRenderingDiagnosis.ReadLog("ReShade.log", Path.Combine(linked, "ReShade.log"), Roots);

        Assert.Equal(NrLogState.OutsideGame, log.State);
    }

    [Fact]
    public void AFolderNamedLikeALog_IsNotRegular()
    {
        Directory.CreateDirectory(Path.Combine(_bin, "ReShade.log"));

        Assert.Equal(NrLogState.NotRegular, NeuralRenderingDiagnosis.ReadLog("ReShade.log", Path.Combine(_bin, "ReShade.log"), Roots).State);
    }

    [Fact]
    public void AGameWithoutAnExecutableFolder_ReadsNoLog()
        => Assert.Empty(NeuralRenderingDiagnosis.ReadLogs(new DetectedGame { Name = "x", InstallPath = _game }));

    // ---- anti-cheat ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AntiCheatFoldersAndFiles_AreListed_FromTheGameAndItsExecutableFolder()
    {
        Directory.CreateDirectory(Path.Combine(_game, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(_bin, "BEService_x64.exe"), [0x4D, 0x5A]);
        Directory.CreateDirectory(Path.Combine(_bin, "Engine")); // not anti-cheat

        var found = NeuralRenderingDiagnosis.FindAntiCheat(Game());

        Assert.Equal(["EasyAntiCheat", "BEService_x64.exe"], found.Select(item => item.Name));
        Assert.Equal(Path.Combine(_game, "EasyAntiCheat"), found[0].Path);
    }

    [Fact]
    public void NoAntiCheat_IsAnEmptyList()
        => Assert.Empty(NeuralRenderingDiagnosis.FindAntiCheat(Game()));

    [Fact]
    public void TheAntiCheatList_IsWizardkofs()
    {
        Assert.Equal(["EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "GameGuard", "XIGNCODE", "PunkBuster"], NeuralRenderingDiagnosis.AntiCheatFolders);
        Assert.Equal(["EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe", "BEService.exe", "BEService_x64.exe", "GameGuard.des", "x3.xem"],
            NeuralRenderingDiagnosis.AntiCheatFiles);
    }

    internal static void MkFifo(string path)
    {
        using var mkfifo = Process.Start("mkfifo", [path]);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }
}
