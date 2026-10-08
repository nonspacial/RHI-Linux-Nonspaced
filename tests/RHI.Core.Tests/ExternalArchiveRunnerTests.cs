using System.Diagnostics;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #37 (DEP-18, DEP-S4): host archive tools run through one runner with a time limit, a process-tree kill and a
/// post-extraction link check. The tools are fake shell scripts so the cases are deterministic: one plants a
/// symlink to /etc/hostname like a crafted tar would, one hangs, one records what it read on stdin.
/// </summary>
public sealed class ExternalArchiveRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-archive-runner-" + Guid.NewGuid().ToString("N"));

    public ExternalArchiveRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task ExtractedSymlinkOutOfTheFolder_IsRefusedAndRemoved_TargetUntouched()
    {
        var output = Path.Combine(_root, "out-link");
        // bsdtar argument order: -xf <archive> -C <output>; writes one file and a link to /etc/hostname.
        var tool = Script("link-tool", """
            mkdir -p "$4/pack"
            printf 'shader' > "$4/pack/a.fx"
            ln -s /etc/hostname "$4/pack/evil"
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), output, CancellationToken.None, tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar)));

        Assert.StartsWith(ReShadeArchiveExtractor.ArchiveRefusedPrefix + ExternalArchiveRunner.SymlinkRefusal, error.Message, StringComparison.Ordinal);
        Assert.Contains("evil -> /etc/hostname", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "pack", "evil")) || Directory.Exists(Path.Combine(output, "pack", "evil")),
            "the link must be removed");
        Assert.Null(new FileInfo(Path.Combine(output, "pack", "evil")).LinkTarget);
        Assert.True(File.Exists("/etc/hostname"));
    }

    [Fact]
    public async Task ExtractedDirectoryLinkInsideTheFolder_IsAlsoRefused()
    {
        var output = Path.Combine(_root, "out-dirlink");
        var tool = Script("dirlink-tool", """
            mkdir -p "$4/real"
            ln -s real "$4/alias"
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), output, CancellationToken.None, tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar)));
        Assert.True(Directory.Exists(Path.Combine(output, "real")), "the real folder is never deleted through the link");
        Assert.False(Directory.Exists(Path.Combine(output, "alias")));
    }

    [Fact]
    public async Task CleanExtraction_Passes()
    {
        var output = Path.Combine(_root, "out-clean");
        var tool = Script("clean-tool", """
            mkdir -p "$4/Shaders"
            printf 'x' > "$4/Shaders/a.fx"
            """);

        await ExternalArchiveRunner.ExtractAsync(Archive(), output, CancellationToken.None, tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar));
        Assert.True(File.Exists(Path.Combine(output, "Shaders", "a.fx")));
    }

    [Fact]
    public async Task HungTool_IsStoppedAtTheTimeLimit_WithItsChildren()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var tool = Script("hang-tool", $"""
            sleep 60 &
            echo $! > '{pidFile}'
            wait
            """);
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), Path.Combine(_root, "out-hang"), CancellationToken.None, timeout: TimeSpan.FromSeconds(1),
            tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar)));

        Assert.StartsWith(ExternalProcess.TimeoutPrefix, error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.False(IsRunning(int.Parse(File.ReadAllText(pidFile).Trim())), "the tool's child must be killed with it");
    }

    [Fact]
    public async Task ToolThatExitsLeavingADaemonOnItsOutput_ReturnsWithoutWaitingForTheDaemon()
    {
        // Review w2-trust minor 5: winetricks/protontricks start wineserver, which inherits the pipes; the read after
        // a normal exit waited for it (up to its lifetime) past the time limit.
        var pidFile = Path.Combine(_root, "daemon.pid");
        var tool = Script("daemon-tool", $"""
            sleep 60 &
            echo $! > '{pidFile}'
            echo "installed d3dcompiler_47"
            echo "last words" >&2
            exit 0
            """);
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await ExternalProcess.RunAsync(new ProcessStartInfo(tool), TimeSpan.FromMinutes(10), CancellationToken.None);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("installed d3dcompiler_47", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal("last words", result.LastMeaningfulLine());
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var daemon))
                try { Process.GetProcessById(daemon).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Cancellation_StopsTheToolAndThrowsCancelled()
    {
        var pidFile = Path.Combine(_root, "cancel.pid");
        var tool = Script("cancel-tool", $"""
            echo $$ > '{pidFile}'
            sleep 60
            """);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), Path.Combine(_root, "out-cancel"), cts.Token, tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar)));
        Assert.False(IsRunning(int.Parse(File.ReadAllText(pidFile).Trim())));
    }

    [Fact]
    public async Task Password_GoesThroughStdinNeverTheCommandLine()
    {
        var seen = Path.Combine(_root, "stdin.txt");
        var args = Path.Combine(_root, "args.txt");
        var tool = Script("7z-tool", $"""
            echo "$@" > '{args}'
            read -r line
            printf '%s' "$line" > '{seen}'
            """);

        await ExternalArchiveRunner.ExtractAsync(Archive(), Path.Combine(_root, "out-pass"), CancellationToken.None,
            password: "public-password", tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.SevenZip));

        Assert.Equal("public-password", File.ReadAllText(seen));
        Assert.DoesNotContain("public-password", File.ReadAllText(args), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_IsRefusedForBsdTar()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), Path.Combine(_root, "out-bsd"), CancellationToken.None, password: "x",
            tool: new ExternalArchiveTool("/bin/true", ExternalArchiveToolKind.BsdTar)));
        Assert.StartsWith(ExternalArchiveRunner.SevenZipRequiredPrefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingTool_ReportsItsLastLine()
    {
        var tool = Script("fail-tool", """
            echo "first" >&2
            echo "ERROR: Wrong password" >&2
            exit 2
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalArchiveRunner.ExtractAsync(
            Archive(), Path.Combine(_root, "out-fail"), CancellationToken.None, tool: new ExternalArchiveTool(tool, ExternalArchiveToolKind.BsdTar)));
        Assert.StartsWith(ExternalArchiveRunner.FailedPrefix, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("ERROR: Wrong password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptiScalerExtractor_GoesThroughTheRunner()
    {
        var output = Path.Combine(_root, "out-opti");
        var tool = Script("opti-tool", """
            out=""
            for a in "$@"; do case "$a" in -o*) out="${a#-o}";; esac; done
            ln -s /etc/hostname "$out/OptiScaler.dll"
            """);
        ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(tool, ExternalArchiveToolKind.SevenZip);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new OptiScalerArchiveExtractor().ExtractAllAsync(Archive(), output));
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        Assert.Null(new FileInfo(Path.Combine(output, "OptiScaler.dll")).LinkTarget);
    }

    private string Archive()
    {
        var path = Path.Combine(_root, "fixture.7z");
        if (!File.Exists(path)) File.WriteAllBytes(path, [0x37, 0x7A]);
        return path;
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_root, name + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r", "") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsRunning(int pid)
    {
        // A killed child may linger as a zombie until reaped; that is not running.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var stat = $"/proc/{pid}/stat";
            if (!File.Exists(stat)) return false;
            string text;
            try { text = File.ReadAllText(stat); }
            catch (IOException) { return false; }
            var state = text[(text.LastIndexOf(')') + 2)..].Split(' ')[0];
            if (state is "Z" or "X") return false;
            Thread.Sleep(100);
        }
        return true;
    }
}
