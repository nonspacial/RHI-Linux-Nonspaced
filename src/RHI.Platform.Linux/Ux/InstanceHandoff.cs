using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Ux;

/// <summary>What a second RHI started with asks the running one to do.</summary>
public enum HandoffKind
{
    /// <summary>Start a library game through the normal Play path (<c>--launch &lt;GameKey&gt;</c>, desktop shortcuts #162).</summary>
    Launch,

    /// <summary>Handle a Nexus Mods "Mod Manager Download" link (<c>nxm://…</c>, #99); always confirmed by the user.</summary>
    Nxm
}

/// <summary>A hand-off request. Untrusted input wherever it comes from: the receiver validates <see cref="Value"/> again.</summary>
public sealed record HandoffRequest(HandoffKind Kind, string Value)
{
    /// <summary>Longest game key or link accepted.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>A value with no control characters (newline, NUL…) and at most <see cref="MaxValueLength"/> characters.</summary>
    public static bool IsAcceptableValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl);

    /// <summary>
    /// Reads the command line: <c>--launch &lt;key&gt;</c>, <c>--nxm &lt;link&gt;</c>, or a bare <c>nxm://…</c> argument (what a
    /// desktop entry's <c>%u</c> passes). Null when there is none or it is malformed (logged; RHI then starts normally).
    /// </summary>
    public static HandoffRequest? FromArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--launch" or "--nxm")
            {
                var value = i + 1 < args.Count ? args[i + 1] : null;
                if (!IsAcceptableValue(value))
                {
                    AppLog.Warn("handoff", $"Ignored {arg}: no usable value follows it");
                    return null;
                }
                return new HandoffRequest(arg == "--launch" ? HandoffKind.Launch : HandoffKind.Nxm, value!);
            }
            if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                return IsAcceptableValue(arg) ? new HandoffRequest(HandoffKind.Nxm, arg) : null;
        }
        return null;
    }
}

/// <summary>
/// The one single-instance hand-off (w3/parity owns it; desktop shortcuts #162 and nxm:// links #99): a second RHI
/// started with <c>--launch</c> or an nxm link passes it to the RHI already running and exits, so the game starts
/// through that instance's Play path and the link's confirmation shows in its window. A plain second start is
/// unchanged (several instances may run, as before). Transport: a Unix socket in a folder only the user can enter
/// (<c>$XDG_RUNTIME_DIR/rhi-linux</c>, mode 0700, owned by the user; otherwise no hand-off), the sender's uid checked
/// with <c>SO_PEERCRED</c>, one JSON line each way, 8 KiB cap and short timeouts. What arrives is untrusted input: the
/// receiver re-validates it (game key looked up in the library only; nxm links parsed strictly and confirmed).
/// </summary>
public static class InstanceHandoff
{
    private const int MaxMessageBytes = 8 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The socket a running RHI listens on (null when the folder cannot be made private).</summary>
    public static string DefaultSocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var root = !string.IsNullOrWhiteSpace(runtime) && Path.IsPathRooted(runtime) && Directory.Exists(runtime)
                ? runtime
                : Path.Combine(LinuxPaths.StateHome, "rhi-linux");
            return Path.Combine(root, "rhi-linux", "handoff.sock");
        }
    }

    /// <summary>Passes <paramref name="request"/> to a running RHI; true when one took it (this process can exit).</summary>
    public static bool TrySend(HandoffRequest request, string? socketPath = null)
    {
        socketPath ??= DefaultSocketPath;
        if (!File.Exists(socketPath) || !IsPrivateFolder(Path.GetDirectoryName(socketPath)!)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = socket.ReceiveTimeout = (int)IoTimeout.TotalMilliseconds;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            if (!PeerIsThisUser(socket)) return false;
            socket.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Wire(request.Kind, request.Value), Json) + "\n"));
            return ReadLine(socket) is "ok";
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            return false; // nothing listening: this process handles the request itself
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            AppLog.Warn("handoff", "Could not pass the request to the running RHI; this one handles it", ex);
            return false;
        }
    }

    /// <summary>
    /// Starts listening (once per process) and calls <paramref name="onRequest"/> on a background thread for each
    /// request another RHI passes. Null when another RHI already listens or the folder cannot be made private (logged).
    /// </summary>
    public static HandoffListener? Listen(Action<HandoffRequest> onRequest, string? socketPath = null)
    {
        socketPath ??= DefaultSocketPath;
        var folder = Path.GetDirectoryName(socketPath)!;
        try
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!IsPrivateFolder(folder))
            {
                AppLog.Warn("handoff", $"{folder} is not private to this user; desktop shortcuts and nxm links will start a new RHI");
                return null;
            }
            // One listener per user: a socket someone answers on is left alone; a stale one (crash) is replaced.
            using var gate = new FileStream(Path.Combine(folder, "handoff.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(socketPath))
            {
                if (Answers(socketPath)) return null;
                File.Delete(socketPath);
            }
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(8);
            return new HandoffListener(socket, socketPath, onRequest);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("handoff", "Could not listen for desktop shortcuts and nxm links; each will start a new RHI", ex);
            return null;
        }
    }

    internal static HandoffRequest? ReadRequest(Socket client)
    {
        if (!PeerIsThisUser(client))
        {
            AppLog.Warn("handoff", "Refused a hand-off from another user");
            return null;
        }
        var line = ReadLine(client);
        if (line is null) return null;
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(line, Json);
            return wire is { Value: { } value } && Enum.IsDefined(wire.Kind) && HandoffRequest.IsAcceptableValue(value)
                ? new HandoffRequest(wire.Kind, value)
                : null;
        }
        catch (JsonException ex)
        {
            AppLog.Warn("handoff", "Refused a malformed hand-off message", ex);
            return null;
        }
    }

    internal static void Acknowledge(Socket client, bool accepted)
        => client.Send(Encoding.UTF8.GetBytes(accepted ? "ok\n" : "refused\n"));

    private static bool Answers(string socketPath)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>A line of at most <see cref="MaxMessageBytes"/> bytes, or null (too long, closed, timed out).</summary>
    private static string? ReadLine(Socket socket)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        var deadline = DateTime.UtcNow + IoTimeout;
        socket.ReceiveTimeout = (int)IoTimeout.TotalMilliseconds;
        while (length <= MaxMessageBytes && DateTime.UtcNow < deadline)
        {
            int read;
            try { read = socket.Receive(buffer, length, buffer.Length - length, SocketFlags.None); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { return null; }
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0) return Encoding.UTF8.GetString(buffer, 0, newline);
        }
        return null;
    }

    /// <summary>The folder exists, is not a symlink, belongs to this user and no one else may enter or list it.</summary>
    internal static bool IsPrivateFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || info.LinkTarget is not null) return false;
            if ((info.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                return false;
            return Native.OwnerUid(folder) is { } owner && owner == Native.geteuid();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Warn("handoff", $"Could not check {folder}", ex);
            return false;
        }
    }

    /// <summary>The process at the other end runs as this user (SO_PEERCRED).</summary>
    internal static bool PeerIsThisUser(Socket socket)
    {
        Span<byte> cred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        const int SolSocket = 1, SoPeerCred = 17;
        if (socket.GetRawSocketOption(SolSocket, SoPeerCred, cred) < 12) return false;
        return BitConverter.ToUInt32(cred[4..8]) == Native.geteuid();
    }

    private sealed record Wire(HandoffKind Kind, string? Value);

    private static class Native
    {
        private const int AtFdCwd = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint StatxUid = 0x8;
        private const int UidOffset = 20; // struct statx: stx_mask u32, stx_blksize u32, stx_attributes u64, stx_nlink u32, stx_uid u32

        [DllImport("libc", SetLastError = true)]
        public static extern uint geteuid();

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [In, Out] byte[] buffer);

        /// <summary>The owner of <paramref name="path"/> (not following a final symlink); statx's layout is the same on every Linux architecture.</summary>
        public static uint? OwnerUid(string path)
        {
            var buffer = new byte[256];
            if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxUid, buffer) != 0) return null;
            if ((BitConverter.ToUInt32(buffer, 0) & StatxUid) == 0) return null;
            return BitConverter.ToUInt32(buffer, UidOffset);
        }
    }
}

/// <summary>A running hand-off listener; dispose to stop and remove the socket.</summary>
public sealed class HandoffListener : IDisposable
{
    private readonly Socket _socket;
    private readonly string _path;
    private readonly Action<HandoffRequest> _onRequest;
    private readonly Thread _thread;
    private volatile bool _stopped;

    internal HandoffListener(Socket socket, string path, Action<HandoffRequest> onRequest)
    {
        _socket = socket;
        _path = path;
        _onRequest = onRequest;
        _thread = new Thread(Run) { IsBackground = true, Name = "rhi-handoff" };
        _thread.Start();
    }

    public string SocketPath => _path;

    private void Run()
    {
        while (!_stopped)
        {
            Socket client;
            try { client = _socket.Accept(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (!_stopped) AppLog.Warn("handoff", "The hand-off listener stopped", ex);
                return;
            }
            using (client)
            {
                try
                {
                    var request = InstanceHandoff.ReadRequest(client);
                    InstanceHandoff.Acknowledge(client, request is not null);
                    if (request is not null) _onRequest(request);
                }
                catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                {
                    AppLog.Warn("handoff", "A hand-off failed", ex);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        try { _socket.Dispose(); }
        finally
        {
            if (!RHI.Core.Deployment.FileCleanup.TryDelete(_path, out var error))
                AppLog.Warn("handoff", $"Could not remove {_path}", error);
        }
    }
}
