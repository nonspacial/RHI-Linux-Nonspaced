using System.Diagnostics;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

/// <summary>Who opens nxm:// links on this desktop.</summary>
public enum NxmHandlerState
{
    /// <summary>No application is the default for nxm:// links.</summary>
    None,

    /// <summary>RHI-Linux is (its own entry, or its main menu entry).</summary>
    Rhi,

    /// <summary>Another application (Vortex under Wine, Mod Organizer 2, a browser helper…) is.</summary>
    Other,

    /// <summary>xdg-mime is not installed; the desktop cannot be asked or told.</summary>
    Unknown
}

/// <summary>
/// Makes RHI-Linux the desktop's handler for Nexus Mods "Mod Manager Download" (nxm://) links on the user's request
/// (#99; upstream registers <c>HKCU\Software\Classes\nxm</c> from Settings and asks first when another manager owns it).
/// Linux: a hidden desktop entry <c>$XDG_DATA_HOME/applications/rhi-linux-nxm.desktop</c>
/// (<c>MimeType=x-scheme-handler/nxm;</c>, <c>Exec="&lt;RHI&gt;" --nxm %u</c>, so it points at this very install whatever
/// it is — AppImage, extracted tree, install-user.sh) made the default with <c>xdg-mime default</c>. Unregistering
/// deletes that entry (the desktop then falls back to whatever else declares nxm). RHI's main menu entry also
/// declares nxm (packaging), so a desktop with no other handler may already pick RHI; every link is confirmed in RHI.
/// The entry carries a marker line (<see cref="MarkerLine"/>, the shortcut service's rule): a file of that name RHI did
/// not write is never overwritten or deleted.
/// </summary>
public sealed class NxmHandlerRegistration
{
    public const string EntryName = "rhi-linux-nxm.desktop";
    public const string MainEntryName = "rhi-linux.desktop";
    public const string MimeType = "x-scheme-handler/nxm";

    /// <summary>The line that marks the entry as RHI's own.</summary>
    public const string MarkerLine = "X-RHI-Linux-Nxm=true";

    private readonly string _applicationsFolder;
    private readonly Func<IReadOnlyList<string>?> _rhiCommand;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)?>> _run;

    /// <param name="applicationsFolder">Test seam (default: <c>$XDG_DATA_HOME/applications</c>).</param>
    /// <param name="rhiCommand">Test seam (default: <see cref="DesktopEntryFormat.RhiCommand"/>).</param>
    /// <param name="run">Test seam: runs a desktop tool (xdg-mime, update-desktop-database); null when it is not installed.</param>
    public NxmHandlerRegistration(string? applicationsFolder = null, Func<IReadOnlyList<string>?>? rhiCommand = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)?>>? run = null)
    {
        _applicationsFolder = applicationsFolder ?? DesktopEntryFormat.ApplicationsFolder;
        _rhiCommand = rhiCommand ?? (() => DesktopEntryFormat.RhiCommand());
        _run = run ?? RunToolAsync;
    }

    public string EntryPath => Path.Combine(_applicationsFolder, EntryName);

    /// <summary>A file at <see cref="EntryPath"/> RHI did not write (no marker line): left as it is.</summary>
    public bool HasForeignEntry => File.Exists(EntryPath) && !IsOurs(EntryPath);

    private static bool IsOurs(string path)
        => new FileInfo(path).Length <= 64 * 1024 && File.ReadLines(path).Any(line => line.Trim() == MarkerLine);

    /// <summary>The current default handler and its desktop entry name (null when none or unknown).</summary>
    public async Task<(NxmHandlerState State, string? Handler)> QueryAsync(CancellationToken cancellationToken = default)
    {
        var answer = await _run(["xdg-mime", "query", "default", MimeType], cancellationToken).ConfigureAwait(false);
        if (answer is null) return (NxmHandlerState.Unknown, null);
        var handler = answer.Value.Output.Trim();
        if (answer.Value.ExitCode != 0 || handler.Length == 0) return (NxmHandlerState.None, null);
        return handler is EntryName or MainEntryName ? (NxmHandlerState.Rhi, handler) : (NxmHandlerState.Other, handler);
    }

    /// <summary>Writes RHI's nxm entry and makes it the default. False when xdg-mime is missing or refused (logged).</summary>
    public async Task<bool> RegisterAsync(CancellationToken cancellationToken = default)
    {
        if (_rhiCommand() is not { Count: > 0 } command) return false;
        if (HasForeignEntry)
        {
            AppLog.Warn("nxm", $"{EntryPath} was not written by RHI; it is left as it is and RHI is not registered");
            return false;
        }
        var entry = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.5",
            "Name=RHI-Linux (Nexus Mods links)",
            $"Exec={DesktopEntryFormat.ExecLine([.. command, "--nxm"], "%u")}",
            "Icon=rhi-linux",
            "Terminal=false",
            "NoDisplay=true",
            $"MimeType={MimeType};",
            MarkerLine,
            "");
        Directory.CreateDirectory(_applicationsFolder);
        AtomicFile.WriteStoreFile(EntryPath, Encoding.UTF8.GetBytes(entry),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await RefreshDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var set = await _run(["xdg-mime", "default", EntryName, MimeType], cancellationToken).ConfigureAwait(false);
        if (set is not { ExitCode: 0 })
        {
            AppLog.Warn("nxm", $"xdg-mime could not make {EntryName} the nxm:// handler ({(set is null ? "not installed" : $"exit {set.Value.ExitCode}")})");
            return false;
        }
        return true;
    }

    /// <summary>Removes RHI's nxm entry (only RHI's own file, by its marker line); true when one was removed.</summary>
    public async Task<bool> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(EntryPath)) return false;
        if (!IsOurs(EntryPath))
        {
            AppLog.Warn("nxm", $"{EntryPath} was not written by RHI; it is left as it is");
            return false;
        }
        File.Delete(EntryPath);
        await RefreshDatabaseAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RefreshDatabaseAsync(CancellationToken cancellationToken)
    {
        if (await _run(["update-desktop-database", "-q", _applicationsFolder], cancellationToken).ConfigureAwait(false) is { ExitCode: not 0 } failed)
            AppLog.Warn("nxm", $"update-desktop-database exited {failed.ExitCode}; the change may show after the next login");
    }

    private static async Task<(int ExitCode, string Output)?> RunToolAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        if (ExternalProcess.FindOnPath(command[0]) is not { } tool) return null;
        var start = new ProcessStartInfo(tool);
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        try
        {
            var result = await ExternalProcess.RunAsync(start, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return (result.ExitCode, result.StandardOutput);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("nxm", $"{command[0]} failed", ex);
            return null;
        }
    }
}
