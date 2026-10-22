using System.Diagnostics;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What an external tool printed and how it ended.</summary>
public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The last non-blank line of stderr, else of stdout, else "exit code N" (for one-line failure reasons).</summary>
    public string LastMeaningfulLine()
        => LastLine(StandardError) ?? LastLine(StandardOutput) ?? $"exit code {ExitCode}";

    private static string? LastLine(string text)
        => text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
}

/// <summary>
/// The one runner for external tools the deployment layer starts (7-Zip/bsdtar, winetricks,
/// protontricks; DEP-S4, #37): every run has a timeout, a timed-out or cancelled run is killed with
/// its whole process tree (a tool left running would keep writing into a folder RHI then deletes or
/// deploys), standard input is always closed (written first when the caller passes input, which
/// keeps secrets such as an archive password out of <c>/proc/&lt;pid&gt;/cmdline</c>) so a tool can
/// never sit waiting for a keypress, and captured output is bounded.
/// </summary>
public static class ExternalProcess
{
    /// <summary>Fixed start of the timeout message (PT-BR presentation pair key); "tool (N s)" follows.</summary>
    public const string TimeoutPrefix = "An external tool took too long and was stopped: ";

    /// <summary>Most characters kept from each output stream (the tail is kept; failures print last).</summary>
    internal const int MaxCapturedChars = 256 * 1024;

    /// <summary>How long a killed tool's output pipes are drained before giving up on them.</summary>
    private static readonly TimeSpan DrainAfterKill = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the output pipes of a tool that exited are drained before the run returns what it has. A daemon the
    /// tool started (wineserver under winetricks/protontricks) inherits the pipes and holds them open for as long as
    /// it lives, so an unbounded read would outlast the time limit.
    /// </summary>
    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts <paramref name="startInfo"/> (stdin/stdout/stderr are redirected here, whatever the caller set)
    /// and waits for it. Throws <see cref="TimeoutException"/> (message <see cref="TimeoutPrefix"/> + tool and
    /// limit) after <paramref name="timeout"/>, <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> fires; the process tree is killed in both cases.
    /// <paramref name="toolName"/>: the name messages give the tool when <paramref name="startInfo"/> starts it through a
    /// wrapper (<see cref="ExternalArchiveRunner"/>'s size bound); the started file's name otherwise.
    /// </summary>
    public static async Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null,
        string? toolName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var tool = toolName ?? Path.GetFileName(startInfo.FileName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {tool}.");
        var stdout = new BoundedCapture();
        var stderr = new BoundedCapture();
        var stdoutTask = stdout.ReadAsync(process.StandardOutput);
        var stderrTask = stderr.ReadAsync(process.StandardError);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            try
            {
                if (standardInput is not null)
                    await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), limit.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The tool exited (or closed stdin) before reading its input; its exit code reports why.
            }
            finally
            {
                CloseQuietly(process.StandardInput);
            }
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process, tool);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(DrainAfterKill, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A grandchild kept the pipes open; the output is not needed for the refusal.
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{TimeoutPrefix}{tool} ({(int)timeout.TotalSeconds} s)");
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(DrainAfterExit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The tool exited but something it started still holds its output open: keep what was printed so far.
            AppLog.Warn("external-process", $"{tool} exited but a process it started kept its output open; continuing with the output read so far");
        }
        return new ExternalProcessResult(process.ExitCode, stdout.Text, stderr.Text);
    }

    /// <summary>First executable named in <paramref name="names"/> found on PATH (in that order), or null.</summary>
    public static string? FindOnPath(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var name in names)
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir, name); }
            catch (ArgumentException) { continue; } // a malformed PATH entry
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void Kill(Process process, string tool)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            AppLog.Warn("external-process", $"{tool} could not be stopped after its time limit or a cancellation", ex);
        }
    }

    private static void CloseQuietly(StreamWriter input)
    {
        try { input.Close(); }
        catch (IOException)
        {
            // Broken pipe: the tool already exited, which is all closing was for.
        }
    }

    /// <summary>
    /// One output stream read to its end, keeping at most <see cref="MaxCapturedChars"/> (the tail); what was read so
    /// far stays available when the run stops waiting for the end.
    /// </summary>
    private sealed class BoundedCapture
    {
        private readonly StringBuilder _kept = new();

        public string Text
        {
            get { lock (_kept) return _kept.ToString(); }
        }

        public async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    lock (_kept)
                    {
                        _kept.Append(buffer, 0, read);
                        if (_kept.Length > MaxCapturedChars) _kept.Remove(0, _kept.Length - MaxCapturedChars);
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // The run stopped waiting and disposed the process (pipe closed under the read): nothing more to keep.
            }
        }
    }
}
