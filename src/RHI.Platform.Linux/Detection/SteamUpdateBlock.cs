using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Detection;

/// <summary>Where a Steam game's update block stands.</summary>
public enum SteamUpdateBlockState
{
    /// <summary>Not a Steam game, or its app manifest is not where Steam keeps it (the switch is not offered).</summary>
    Unavailable,

    /// <summary>The manifest is writable: Steam updates the game as usual.</summary>
    Allowed,

    /// <summary>
    /// The manifest is read-only (and set to "Wait until I launch the game") but not immutable: Steam can still
    /// replace it, and updates the game when it is launched.
    /// </summary>
    LaunchOnly,

    /// <summary>The manifest is immutable (confirmed with the file's flags): Steam cannot replace or write it, so it cannot
    /// update the game.</summary>
    Blocked
}

/// <summary>Why turning the block on stopped at <see cref="SteamUpdateBlockState.LaunchOnly"/> (no immutable flag).</summary>
public enum SteamUpdateBlockLimit
{
    /// <summary><c>pkexec</c> (polkit) is not installed.</summary>
    NoPkexec,

    /// <summary><c>chattr</c> (e2fsprogs) is not installed.</summary>
    NoChattr,

    /// <summary>The drive holding the Steam library has no immutable flag (its file system does not support it).</summary>
    Unsupported,

    /// <summary>The password prompt was cancelled, or authentication was refused.</summary>
    Cancelled,

    /// <summary><c>chattr</c> ran but failed; <see cref="SteamUpdateBlockResult.Detail"/> says how.</summary>
    Failed,

    /// <summary><c>chattr</c> reported success but the flag does not read as set.</summary>
    NotConfirmed
}

/// <summary>
/// What a change of the block did. <see cref="Message"/> is the plain-English reason when it did not
/// (<see cref="Success"/> false); <see cref="Limit"/> says why a block that did apply is only
/// <see cref="SteamUpdateBlockState.LaunchOnly"/>, with <see cref="Detail"/> (chattr's own words) for
/// <see cref="SteamUpdateBlockLimit.Failed"/>.
/// </summary>
public sealed record SteamUpdateBlockResult(bool Success, SteamUpdateBlockState State, string? Message = null,
    SteamUpdateBlockLimit? Limit = null, string? Detail = null);

/// <summary>Runs one program as root through the desktop's polkit prompt (<c>pkexec</c>). A test seam.</summary>
public interface IPrivilegedRunner
{
    /// <summary>Absolute path of <paramref name="tool"/> on PATH, or null when it is not installed.</summary>
    string? Find(string tool);

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> as separate argv entries (never through a
    /// shell) and waits for it.
    /// </summary>
    Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Reads a file's immutable flag. A test seam.</summary>
public interface IFileFlagsReader
{
    /// <summary>True / false: the file's immutable flag; null when its file system has no such flag (or it cannot be read).</summary>
    bool? IsImmutable(string path);
}

/// <summary>The real runner: <c>pkexec</c> started directly with an argv list.</summary>
public sealed class PkexecRunner : IPrivilegedRunner
{
    /// <summary>Long enough to type a password; a prompt left open is closed after this.</summary>
    private static readonly TimeSpan PromptLimit = TimeSpan.FromMinutes(5);

    public string? Find(string tool) => ExternalProcess.FindOnPath(tool);

    public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => ExternalProcess.RunAsync(StartInfo(fileName, arguments), PromptLimit, cancellationToken);

    /// <summary>The process start: <paramref name="fileName"/> itself, every argument its own argv entry, no shell.</summary>
    public static ProcessStartInfo StartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}

/// <summary>The real flags reader: <c>FS_IOC_GETFLAGS</c> on the open file, as <c>lsattr</c> reads it.</summary>
public sealed class IoctlFileFlagsReader : IFileFlagsReader
{
    private const int ImmutableFlag = 0x10; // FS_IMMUTABLE_FL

    public bool? IsImmutable(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        using var handle = OpenRegular(path);
        if (handle is null) return null;
        // FS_IOC_GETFLAGS = _IOR('f', 1, long): 0x80086601 on 64-bit Linux, 0x80046601 on 32-bit.
        nuint request = IntPtr.Size == 8 ? 0x80086601u : 0x80046601u;
        long flags = 0;
        int result;
        try
        {
            result = Libc.ioctl((int)handle.DangerousGetHandle(), request, ref flags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        // ENOTTY / EOPNOTSUPP / EINVAL: this file system has no inode flags.
        if (result != 0) return null;
        return ((int)flags & ImmutableFlag) != 0;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenRegular(string path) => FileKind.OpenRegularFileForReading(path);

    private static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, nuint request, ref long value);
    }
}

/// <summary>
/// #290 (upstream 2.9.4 "Block Steam updates"): stops Steam updating a game. Upstream makes the game's
/// <c>appmanifest_&lt;appid&gt;.acf</c> read-only, which holds on Windows; on Linux Steam replaces the manifest by renaming a
/// new file over it, which a read-only file does not stop. So here turning it on:
/// <list type="number">
/// <item>sets the manifest's <c>AutoUpdateBehavior</c> to <c>"1"</c> ("Wait until I launch the game"), editing
/// that one value in place;</item>
/// <item>makes the manifest read-only (upstream's step);</item>
/// <item>makes it immutable (<c>pkexec chattr +i &lt;manifest&gt;</c>, the desktop's polkit password prompt), which a rename
/// cannot replace, and confirms the flag.</item>
/// </list>
/// Where the last step cannot be done (no pkexec or chattr, a file system without the flag, the prompt cancelled) the
/// first two stay and the state is <see cref="SteamUpdateBlockState.LaunchOnly"/>: Steam may still update the game when it
/// is launched. Turning it off runs <c>pkexec chattr -i</c> first, then puts back the mode and <c>AutoUpdateBehavior</c>
/// recorded when it was turned on. The manifest is found from the game's folder (<c>steamapps/common/&lt;installdir&gt;</c>,
/// native or Flatpak Steam) and checked against its own <c>appid</c> and <c>installdir</c>. Refused while Steam is
/// updating the game.
/// </summary>
public sealed class SteamUpdateBlockService
{
    // StateFlags bits (Steam's EAppState): 256 update running, 512 update paused, 1024 update started.
    private const long UpdateInProgressFlags = 256 | 512 | 1024;

    private const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>Steam's "Wait until I launch the game".</summary>
    public const string LaunchOnlyBehavior = "1";

    public const string NotSteamMessage = "Block Steam updates is only for Steam games whose Steam app manifest RHI can find.";
    public const string UpdatingMessage = "Steam is updating this game right now. Wait until it finishes (or cancel it in Steam), then try again.";
    public const string FailedPrefix = "The Steam app manifest could not be changed: ";
    public const string UnlockCancelledMessage = "The password prompt was cancelled, so the Steam app manifest stays locked.";
    public const string UnlockFailedPrefix = "The Steam app manifest could not be unlocked: ";

    // pkexec: 126 the dialog was dismissed / not authorised, 127 authentication failed.
    private static readonly int[] AuthRefusedExitCodes = [126, 127];

    private readonly IPrivilegedRunner _runner;
    private readonly IFileFlagsReader _flags;
    private readonly JsonFileStore<Dictionary<string, Original>> _originals;

    public SteamUpdateBlockService(IPrivilegedRunner? runner = null, IFileFlagsReader? flags = null, string? originalsPath = null)
    {
        _runner = runner ?? new PkexecRunner();
        _flags = flags ?? new IoctlFileFlagsReader();
        _originals = new JsonFileStore<Dictionary<string, Original>>(
            originalsPath ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "steam-update-blocks.json"),
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>What the manifest held before RHI blocked it: its mode and <c>AutoUpdateBehavior</c> (null: no such line).</summary>
    public sealed record Original(int Mode, string? AutoUpdateBehavior);

    /// <summary>The game's app manifest, or null when it is not a Steam game or the manifest is not where Steam keeps it.</summary>
    public static string? ManifestPath(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.SteamAppId is not int appId || string.IsNullOrWhiteSpace(game.InstallPath)) return null;
        if (!string.Equals(game.Source, "Steam", StringComparison.OrdinalIgnoreCase)) return null;
        string install;
        try { install = Path.GetFullPath(game.InstallPath).TrimEnd('/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var common = Path.GetDirectoryName(install);
        if (common is null || !string.Equals(Path.GetFileName(common), "common", StringComparison.Ordinal)) return null;
        var steamapps = Path.GetDirectoryName(common);
        if (steamapps is null || !string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.Ordinal)) return null;
        var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        // The exact file chattr gets: a regular file at that path, not a link to somewhere else (Flatpak Steam's library
        // under ~/.var/app/com.valvesoftware.Steam is a real folder on the host, so it resolves like a native one).
        if (new FileInfo(manifest).LinkTarget is not null || FileKind.IsRegularFile(manifest) != true) return null;
        var values = Read(manifest);
        if (values is null) return null;
        // The manifest must describe this game's folder: never touch another app's file.
        if (values.Value("appid") != appId.ToString(CultureInfo.InvariantCulture)) return null;
        if (!string.Equals(values.Value("installdir"), Path.GetFileName(install), StringComparison.Ordinal)) return null;
        return manifest;
    }

    public SteamUpdateBlockState State(DetectedGame game)
        => ManifestPath(game) is { } manifest ? StateOf(manifest) : SteamUpdateBlockState.Unavailable;

    private SteamUpdateBlockState StateOf(string manifest)
    {
        try
        {
            if (_flags.IsImmutable(manifest) == true) return SteamUpdateBlockState.Blocked;
            return (File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite) == 0 ? SteamUpdateBlockState.LaunchOnly : SteamUpdateBlockState.Allowed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SteamUpdateBlockState.Unavailable;
        }
    }

    /// <summary>Blocks (immutable, else launch-only) or allows Steam updates of <paramref name="game"/>.</summary>
    public async Task<SteamUpdateBlockResult> SetAsync(DetectedGame game, bool blocked, CancellationToken cancellationToken = default)
    {
        if (ManifestPath(game) is not { } manifest) return new(false, SteamUpdateBlockState.Unavailable, NotSteamMessage);
        var current = StateOf(manifest);
        if (blocked ? current == SteamUpdateBlockState.Blocked : current == SteamUpdateBlockState.Allowed) return new(true, current);
        try
        {
            return blocked
                ? await BlockAsync(game, manifest, current, cancellationToken).ConfigureAwait(false)
                : await AllowAsync(game, manifest, current, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            AppLog.Warn("steam-update-block", $"Could not change {manifest}", ex);
            return new(false, StateOf(manifest), FailedPrefix + ex.Message);
        }
    }

    private async Task<SteamUpdateBlockResult> BlockAsync(DetectedGame game, string manifest, SteamUpdateBlockState current, CancellationToken cancellationToken)
    {
        if (IsUpdating(manifest)) return new(false, current, UpdatingMessage);

        var text = File.ReadAllText(manifest);
        var behavior = Read(manifest)?.Value("AutoUpdateBehavior");
        var mode = File.GetUnixFileMode(manifest);
        // Record what to put back before changing anything. A record from an earlier turn-on is kept while the manifest
        // still carries RHI's value (a retry after a cancelled prompt, or a manifest Steam replaced with the value in it).
        var originals = Originals();
        if (!(originals.ContainsKey(manifest) && behavior == LaunchOnlyBehavior))
        {
            originals[manifest] = new Original((int)mode, behavior);
            _originals.Save(originals);
        }

        if (behavior != LaunchOnlyBehavior)
            AtomicFile.ReplaceUserFile(manifest, Encoding.UTF8.GetBytes(WithAutoUpdateBehavior(text, LaunchOnlyBehavior)), readOnly: ReadOnlyUserFile.WriteKeepingLock);
        File.SetUnixFileMode(manifest, File.GetUnixFileMode(manifest) & ~WriteBits);
        AppLog.Warn("steam-update-block", $"Steam updates of {game.Name} set to launch only, manifest read-only: {manifest}");

        // The immutable flag: only where it can work.
        var pkexec = _runner.Find("pkexec");
        if (pkexec is null) return LaunchOnly(SteamUpdateBlockLimit.NoPkexec);
        var chattr = _runner.Find("chattr");
        if (chattr is null) return LaunchOnly(SteamUpdateBlockLimit.NoChattr);
        if (_flags.IsImmutable(manifest) is null) return LaunchOnly(SteamUpdateBlockLimit.Unsupported);
        if (IsUpdating(manifest)) return LaunchOnly(SteamUpdateBlockLimit.Cancelled, UpdatingMessage);

        var run = await _runner.RunAsync(pkexec, [chattr, "+i", manifest], cancellationToken).ConfigureAwait(false);
        if (AuthRefusedExitCodes.Contains(run.ExitCode)) return LaunchOnly(SteamUpdateBlockLimit.Cancelled);
        if (run.ExitCode != 0) return LaunchOnly(SteamUpdateBlockLimit.Failed, run.LastMeaningfulLine());
        if (_flags.IsImmutable(manifest) != true) return LaunchOnly(SteamUpdateBlockLimit.NotConfirmed);
        AppLog.Warn("steam-update-block", $"Blocked Steam updates of {game.Name}: {manifest} is immutable");
        return new(true, SteamUpdateBlockState.Blocked);

        SteamUpdateBlockResult LaunchOnly(SteamUpdateBlockLimit limit, string? detail = null)
        {
            AppLog.Warn("steam-update-block", $"{manifest} not made immutable ({limit}{(detail is null ? "" : ": " + detail)}); Steam may still update {game.Name} at launch");
            return new(true, StateOf(manifest), Limit: limit, Detail: detail);
        }
    }

    private async Task<SteamUpdateBlockResult> AllowAsync(DetectedGame game, string manifest, SteamUpdateBlockState current, CancellationToken cancellationToken)
    {
        if (current == SteamUpdateBlockState.Blocked)
        {
            var pkexec = _runner.Find("pkexec");
            var chattr = _runner.Find("chattr");
            if (pkexec is null || chattr is null)
                return new(false, current, UnlockFailedPrefix + (pkexec is null ? "pkexec" : "chattr") + " is not installed.");
            var run = await _runner.RunAsync(pkexec, [chattr, "-i", manifest], cancellationToken).ConfigureAwait(false);
            if (AuthRefusedExitCodes.Contains(run.ExitCode)) return new(false, current, UnlockCancelledMessage);
            if (run.ExitCode != 0 || _flags.IsImmutable(manifest) == true)
                return new(false, StateOf(manifest), UnlockFailedPrefix + run.LastMeaningfulLine());
        }

        var originals = Originals();
        originals.TryGetValue(manifest, out var original);
        var behavior = Read(manifest)?.Value("AutoUpdateBehavior");
        if (original is not null && behavior == LaunchOnlyBehavior && original.AutoUpdateBehavior != LaunchOnlyBehavior)
        {
            var text = File.ReadAllText(manifest);
            AtomicFile.ReplaceUserFile(manifest, Encoding.UTF8.GetBytes(WithAutoUpdateBehavior(text, original.AutoUpdateBehavior)), readOnly: ReadOnlyUserFile.WriteKeepingLock);
        }
        // The original mode, with the owner's write bit in any case: a manifest left read-only would still be blocked.
        File.SetUnixFileMode(manifest, (original is null ? File.GetUnixFileMode(manifest) : (UnixFileMode)original.Mode) | UnixFileMode.UserWrite);
        if (originals.Remove(manifest)) _originals.Save(originals);
        AppLog.Warn("steam-update-block", $"Allowed Steam updates of {game.Name}: {manifest}");
        return new(true, StateOf(manifest));
    }

    private Dictionary<string, Original> Originals() => new(_originals.Load().Value ?? [], StringComparer.Ordinal);

    private static bool IsUpdating(string manifest)
        => Read(manifest)?.Value("StateFlags") is { } flags && long.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out var state)
            && (state & UpdateInProgressFlags) != 0;

    /// <summary>
    /// <paramref name="text"/> with <c>AppState</c>'s <c>AutoUpdateBehavior</c> set to <paramref name="value"/> (null: the
    /// line removed), everything else byte for byte: only the value between its quotes changes; a missing line is added
    /// before <c>AppState</c>'s closing brace with the indentation and separator of the key line above it.
    /// </summary>
    public static string WithAutoUpdateBehavior(string text, string? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n'); // a "\r" stays on its line, so CRLF files keep CRLF
        var depth = 0;
        var inAppState = false;
        string? lastKeyLine = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var cr = lines[i].Length > line.Length ? "\r" : "";
            var trimmed = line.Trim();
            if (depth == 0 && string.Equals(trimmed, "\"AppState\"", StringComparison.OrdinalIgnoreCase)) inAppState = true;
            if (trimmed == "{") { depth++; continue; }
            if (trimmed == "}")
            {
                if (depth == 1 && inAppState)
                {
                    if (value is null) return text;
                    var indent = lastKeyLine is null ? "\t" : lastKeyLine[..(lastKeyLine.Length - lastKeyLine.TrimStart().Length)];
                    var separator = lastKeyLine is null ? "\t\t" : Quotes(lastKeyLine) is [_, var keyEnd, var valueStart, _] ? lastKeyLine[(keyEnd + 1)..valueStart] : "\t\t";
                    return string.Join('\n', lines.Take(i).Append($"{indent}\"AutoUpdateBehavior\"{separator}\"{value}\"{cr}").Concat(lines.Skip(i)));
                }
                depth--;
                continue;
            }
            if (depth != 1 || !inAppState) continue;
            var quotes = Quotes(line);
            if (quotes.Length != 4) continue;
            lastKeyLine = line;
            if (!string.Equals(line[(quotes[0] + 1)..quotes[1]], "AutoUpdateBehavior", StringComparison.OrdinalIgnoreCase)) continue;
            if (value is null) return string.Join('\n', lines.Take(i).Concat(lines.Skip(i + 1)));
            lines[i] = line[..(quotes[2] + 1)] + value + line[quotes[3]..] + cr;
            return string.Join('\n', lines);
        }
        return text;
    }

    private static int[] Quotes(string line) => Enumerable.Range(0, line.Length).Where(index => line[index] == '"').ToArray();

    private static SteamKeyValues? Read(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            var root = SteamKeyValues.Parse(File.ReadAllText(manifest));
            return root.Child("AppState");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
