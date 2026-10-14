using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

/// <summary>#162: desktop-entry writing (Exec quoting per the spec), the shortcut files, the Steam icon.</summary>
public sealed class DesktopShortcutServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shortcut-" + Guid.NewGuid().ToString("N"));
    private string DesktopDir => Path.Combine(_root, "Desktop");
    private string MenuDir => Path.Combine(_root, "applications");
    private string IconDir => Path.Combine(_root, "icons");

    public DesktopShortcutServiceTests()
    {
        Directory.CreateDirectory(DesktopDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DesktopShortcutService Service(string? desktop = "", IReadOnlyList<string>? steamRoots = null)
        => new(_ => Task.FromResult(desktop == "" ? DesktopDir : desktop), MenuDir, IconDir,
            () => ["/opt/RHI Linux/usr/bin/RHI"], () => steamRoots ?? []);

    private static DetectedGame Game(string name, int? appId = null) => new() { Name = name, InstallPath = "/games/" + name, Source = appId is null ? "Lutris" : "Steam", SteamAppId = appId };

    [Theory]
    [InlineData("steam:1091500")]
    [InlineData("folder:Lutris|/home/u/Games/My \"Quoted\" Game")]
    [InlineData("folder:Manual|/home/u/$HOME/`whoami`/100% \\ back\\slash")]
    [InlineData("heroic:Fortnite;rm -rf ~ & echo |x| 'single' #hash")]
    public void ExecLine_ReadsBackAsTheSameArguments_PerTheDesktopEntrySpec(string key)
    {
        var entry = DesktopShortcutService.BuildEntry("Game", ["/opt/RHI Linux/usr/bin/RHI"], "rhi-linux", key);
        var exec = entry.Split('\n').Single(line => line.StartsWith("Exec=", StringComparison.Ordinal))["Exec=".Length..];

        Assert.Equal(["/opt/RHI Linux/usr/bin/RHI", "--launch", key], DesktopEntryReader.ExecArguments(exec));
    }

    [Theory]
    [InlineData("folder:Lutris|/home/u/Games/My \"Quoted\" Game")]
    [InlineData("folder:Manual|/home/u/$HOME/`whoami`/100% \\ back\\slash")]
    [InlineData("heroic:Fortnite;rm -rf ~ & echo |x| 'single' #hash")]
    public void ExecLine_GLibReadsBackTheSameArguments(string key)
    {
        var entry = DesktopShortcutService.BuildEntry("Game", ["/opt/RHI $Linux/usr/bin/RHI"], "rhi-linux", key);
        var file = Path.Combine(_root, "glib.desktop");
        File.WriteAllText(file, entry);

        if (GLibDesktopEntry.ExecArguments(file) is not { } args) return; // no gcc/GLib here: the strict reader above still holds the rule

        Assert.Equal(["/opt/RHI $Linux/usr/bin/RHI", "--launch", key], args);
    }

    [Fact]
    public void ExecLine_PassesDesktopFileValidate()
    {
        if (ExternalProcessFind("desktop-file-validate") is not { } validate) return; // the spec reader above still holds the rule
        var entry = DesktopShortcutService.BuildEntry("Cyberpunk 2077 \\ \"Ultimate\"", ["/opt/RHI Linux/usr/bin/RHI"], "rhi-linux",
            "folder:Manual|/home/u/$HOME/`x`/100% \\ \"q\"");
        var file = Path.Combine(_root, "check.desktop");
        File.WriteAllText(file, entry);
        var run = Process.Start(new ProcessStartInfo(validate, file) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        run.WaitForExit(10_000);
        var output = run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd();
        Assert.True(run.ExitCode == 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase), output);
    }

    [Fact]
    public void ControlCharacters_AreRefusedInExec_AndDroppedFromNames()
    {
        Assert.Throws<ArgumentException>(() => DesktopEntryFormat.QuoteExecArgument("a\nb"));
        Assert.Equal(@"Line\none \\ tab\t", DesktopEntryFormat.EscapeString("Line\none \\ tab\t\u0007"));
    }

    [Fact]
    public async Task OnTheDesktop_WritesAnExecutableLauncherRunningRhiWithTheGameKey()
    {
        var result = await Service().CreateAsync(Game("Night City"), "folder:Lutris|/games/Night City", ShortcutPlace.Desktop);

        Assert.Equal(ShortcutOutcome.Created, result.Outcome);
        Assert.Equal(DesktopDir, Path.GetDirectoryName(result.FilePath));
        Assert.StartsWith("rhi-linux-game-night-city-", Path.GetFileName(result.FilePath), StringComparison.Ordinal);
        var lines = File.ReadAllLines(result.FilePath!);
        Assert.Contains("Name=Night City", lines);
        Assert.Contains("Exec=\"/opt/RHI Linux/usr/bin/RHI\" \"--launch\" \"folder:Lutris|/games/Night City\"", lines);
        Assert.Contains("Icon=rhi-linux", lines);
        Assert.Contains("X-RHI-Linux-GameKey=folder:Lutris|/games/Night City", lines);
        Assert.True(File.GetUnixFileMode(result.FilePath!).HasFlag(UnixFileMode.UserExecute));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DesktopDir, "*.rhi-tmp-*"));
    }

    [Fact]
    public async Task InTheMenu_WritesOnlyThere_NotExecutable_AndNothingOnTheDesktop()
    {
        var result = await Service().CreateAsync(Game("Night City"), "steam:1091500", ShortcutPlace.ApplicationsMenu);

        Assert.Equal(MenuDir, Path.GetDirectoryName(result.FilePath));
        Assert.False(File.GetUnixFileMode(result.FilePath!).HasFlag(UnixFileMode.UserExecute));
        Assert.Empty(Directory.EnumerateFiles(DesktopDir));
    }

    [Fact]
    public async Task CreatingAgain_RewritesRhisOwnFile_SameName()
    {
        var service = Service();
        var first = await service.CreateAsync(Game("Night City"), "steam:1091500", ShortcutPlace.Desktop);
        var second = await service.CreateAsync(Game("Night City"), "steam:1091500", ShortcutPlace.Desktop);

        Assert.Equal(ShortcutOutcome.Updated, second.Outcome);
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Single(Directory.EnumerateFiles(DesktopDir));
    }

    [Fact]
    public async Task AFileRhiDidNotWrite_WithTheSameName_IsNeverOverwritten()
    {
        var name = DesktopShortcutService.FileName("Night City", "steam:1091500");
        var foreign = Path.Combine(DesktopDir, name);
        File.WriteAllText(foreign, "[Desktop Entry]\nName=Mine\nExec=mine\n");

        var result = await Service().CreateAsync(Game("Night City"), "steam:1091500", ShortcutPlace.Desktop);

        Assert.Equal(ShortcutOutcome.ForeignFileInTheWay, result.Outcome);
        Assert.Equal("[Desktop Entry]\nName=Mine\nExec=mine\n", File.ReadAllText(foreign));
    }

    [Fact]
    public async Task NoDesktopFolder_IsSaid_AndNothingIsWritten()
    {
        var result = await Service(desktop: null).CreateAsync(Game("Night City"), "steam:1", ShortcutPlace.Desktop);

        Assert.Equal(ShortcutOutcome.NoDesktopFolder, result.Outcome);
        Assert.False(Directory.Exists(MenuDir));
    }

    [Fact]
    public async Task UnusableKey_IsRefusedBeforeAnyFile()
    {
        var result = await Service().CreateAsync(Game("Odd"), "folder:Manual|/games/odd\nname", ShortcutPlace.Desktop);

        Assert.Equal(ShortcutOutcome.UnusableKey, result.Outcome);
        Assert.Empty(Directory.EnumerateFiles(DesktopDir));
    }

    [Fact]
    public async Task SteamGame_GetsItsLibraryIcon_CopiedIntoRhisOwnFolder()
    {
        var steam = Path.Combine(_root, "steam");
        var cache = Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", "1091500")).FullName;
        File.WriteAllBytes(Path.Combine(cache, "header.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(cache, "7807d6dcd71d8161465619b4f041794b0353a6d0.jpg"), [9, 9, 9]);

        var result = await Service(steamRoots: [steam]).CreateAsync(Game("Night City", 1091500), "steam:1091500", ShortcutPlace.Desktop);

        var icon = File.ReadAllLines(result.FilePath!).Single(line => line.StartsWith("Icon=", StringComparison.Ordinal))["Icon=".Length..];
        Assert.Equal(IconDir, Path.GetDirectoryName(icon));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(icon));
    }

    [Fact]
    public void DesktopFolder_FromUserDirs_ExpandsHome()
    {
        var file = Path.Combine(_root, "user-dirs.dirs");
        File.WriteAllText(file, "# comment\nXDG_DOCUMENTS_DIR=\"$HOME/Docs\"\nXDG_DESKTOP_DIR=\"$HOME/Área de trabalho\"\n");

        Assert.Equal("/home/u/Área de trabalho", DesktopShortcutService.FromUserDirs(file, "/home/u"));
    }

    [Fact]
    public void RhiCommand_PrefersTheAppImage_ThenThisProcess()
    {
        var appImage = Path.Combine(_root, "RHI.AppImage");
        File.WriteAllText(appImage, "");
        Assert.Equal([appImage], DesktopEntryFormat.RhiCommand(name => name == "APPIMAGE" ? appImage : null, "/tmp/.mount_x/usr/bin/RHI"));
        Assert.Equal(["/opt/rhi/RHI"], DesktopEntryFormat.RhiCommand(_ => null, "/opt/rhi/RHI"));
        Assert.Equal(["/usr/bin/dotnet", "/src/RHI.dll"], DesktopEntryFormat.RhiCommand(_ => null, "/usr/bin/dotnet", "/src/RHI.dll"));
    }

    private static string? ExternalProcessFind(string tool) => RHI.Platform.Linux.Deployment.ExternalProcess.FindOnPath(tool);
}

/// <summary>
/// A reader of desktop-entry Exec values exactly as the Desktop Entry Specification 1.5 describes it, as strict as GLib
/// (<see cref="GLibDesktopEntry"/> runs the host's real GLib where it is installed).
/// </summary>
internal static class DesktopEntryReader
{
    public static IReadOnlyList<string> ExecArguments(string value)
    {
        // 1. string-type unescape (\s \n \t \r \\). Any other escape makes the value unreadable, as GLib's key-file
        //    parser treats it (KDE keeps it, so a reader that kept it modelled KDE only).
        var text = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { text.Append(value[i]); continue; }
            if (i + 1 >= value.Length || !"sntr\\".Contains(value[i + 1]))
                throw new FormatException($"Invalid string escape at {i} (GLib refuses the whole value): {value}");
            text.Append(value[i + 1] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => '\\' });
            i++;
        }
        // 2. Exec quoting: arguments separated by spaces; inside "…", \" \` \$ \\ stand for the character; %% is %.
        var args = new List<string>();
        var s = text.ToString();
        var pos = 0;
        while (pos < s.Length)
        {
            if (s[pos] == ' ') { pos++; continue; }
            var arg = new StringBuilder();
            if (s[pos] == '"')
            {
                pos++;
                while (s[pos] != '"')
                {
                    if (s[pos] == '\\') pos++;
                    arg.Append(s[pos++]);
                }
                pos++;
            }
            else
            {
                while (pos < s.Length && s[pos] != ' ') arg.Append(s[pos++]);
            }
            // A lone % starts a field code (%u, %f…), which a launcher would expand: never part of a literal argument.
            var literal = arg.ToString().Replace("%%", "\0", StringComparison.Ordinal);
            if (literal.Contains('%')) throw new FormatException($"Field code in a literal argument: {arg}");
            args.Add(literal.Replace("\0", "%", StringComparison.Ordinal));
        }
        return args;
    }
}

/// <summary>
/// The host's GLib reading a desktop entry's Exec the way GNOME/gio launchers do: <c>g_key_file_get_string</c> (string
/// unescape, refuses invalid escapes), <c>%%</c> → <c>%</c>, then <c>g_shell_parse_argv</c>. Built once per run with
/// gcc + pkg-config; null when either is missing.
/// </summary>
internal static class GLibDesktopEntry
{
    private const string Source = """
        #include <glib.h>
        #include <stdio.h>
        #include <string.h>
        int main(int argc, char **argv) {
          GKeyFile *k = g_key_file_new(); GError *e = NULL;
          if (!g_key_file_load_from_file(k, argv[1], 0, &e)) { fprintf(stderr, "load: %s\n", e->message); return 2; }
          char *v = g_key_file_get_string(k, "Desktop Entry", "Exec", &e);
          if (!v) { fprintf(stderr, "get_string: %s\n", e->message); return 3; }
          GString *x = g_string_new(NULL);
          for (char *p = v; *p; p++) { if (p[0] == '%' && p[1] == '%') { g_string_append_c(x, '%'); p++; } else g_string_append_c(x, *p); }
          int n; char **a;
          if (!g_shell_parse_argv(x->str, &n, &a, &e)) { fprintf(stderr, "parse_argv: %s\n", e->message); return 4; }
          for (int i = 0; i < n; i++) { fputs(a[i], stdout); fputc(0, stdout); }
          return 0;
        }
        """;

    private static readonly object Gate = new();
    private static string? _tool;
    private static bool _tried;

    public static IReadOnlyList<string>? ExecArguments(string desktopFile)
    {
        if (Tool() is not { } tool) return null;
        var start = new ProcessStartInfo(tool, [desktopFile]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var run = Process.Start(start)!;
        var output = run.StandardOutput.ReadToEnd();
        var error = run.StandardError.ReadToEnd();
        run.WaitForExit(10_000);
        Assert.True(run.ExitCode == 0, $"GLib refused the entry: {error}");
        return output.Split('\0')[..^1];
    }

    private static string? Tool()
    {
        lock (Gate)
        {
            if (_tried) return _tool;
            _tried = true;
            if (RHI.Platform.Linux.Deployment.ExternalProcess.FindOnPath("gcc") is not { } gcc
                || RHI.Platform.Linux.Deployment.ExternalProcess.FindOnPath("pkg-config") is not { } pkgConfig) return null;
            var flags = Run(pkgConfig, ["--cflags", "--libs", "glib-2.0"]);
            if (flags is null) return null;
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-glib-exec-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(dir, "exec.c"), Source);
            var output = Path.Combine(dir, "exec");
            if (Run(gcc, [Path.Combine(dir, "exec.c"), "-o", output, .. flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)]) is null) return null;
            return _tool = output;
        }
    }

    private static string? Run(string tool, IEnumerable<string> args)
    {
        using var run = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = run.StandardOutput.ReadToEnd();
        run.StandardError.ReadToEnd();
        run.WaitForExit(60_000);
        return run.ExitCode == 0 ? output.Trim() : null;
    }
}

/// <summary>#162 + #99 transport: the single-instance hand-off and the command line it carries.</summary>
public sealed class InstanceHandoffTests : IDisposable
{
    // Short: a Unix socket path must fit in 108 bytes.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "h" + Guid.NewGuid().ToString("N")[..8]);
    private string Socket => Path.Combine(_dir, "handoff.sock");

    public InstanceHandoffTests()
    {
        Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CommandLine_LaunchKey_NxmFlag_AndBareNxmLink()
    {
        Assert.Equal(new HandoffRequest(HandoffKind.Launch, "steam:1091500"), HandoffRequest.FromArguments(["--launch", "steam:1091500"]));
        Assert.Equal(new HandoffRequest(HandoffKind.Nxm, "nxm://cyberpunk2077/mods/1/files/2?key=a&expires=1"),
            HandoffRequest.FromArguments(["--nxm", "nxm://cyberpunk2077/mods/1/files/2?key=a&expires=1"]));
        Assert.Equal(HandoffKind.Nxm, HandoffRequest.FromArguments(["nxm://skyrim/mods/1/files/2"])!.Kind);
        Assert.Null(HandoffRequest.FromArguments([]));
        Assert.Null(HandoffRequest.FromArguments(["--launch"]));
        Assert.Null(HandoffRequest.FromArguments(["--launch", "steam:1\nrm"]));
        Assert.Null(HandoffRequest.FromArguments(["--launch", new string('k', 4097)]));
    }

    [Fact]
    public async Task SecondRhi_PassesTheRequestToTheListeningOne()
    {
        var received = new TaskCompletionSource<HandoffRequest>();
        using var listener = InstanceHandoff.Listen(request => received.TrySetResult(request), Socket);
        Assert.NotNull(listener);

        Assert.True(InstanceHandoff.TrySend(new HandoffRequest(HandoffKind.Launch, "folder:Lutris|/g/My \"Game\""), Socket));

        Assert.Equal(new HandoffRequest(HandoffKind.Launch, "folder:Lutris|/g/My \"Game\""), await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void OnlyOneListener_AndAfterItStopsTheNextStartHandlesItsOwnRequest()
    {
        var first = InstanceHandoff.Listen(_ => { }, Socket);
        Assert.NotNull(first);
        Assert.Null(InstanceHandoff.Listen(_ => { }, Socket));

        first!.Dispose();

        Assert.False(File.Exists(Socket));
        Assert.False(InstanceHandoff.TrySend(new HandoffRequest(HandoffKind.Launch, "steam:1"), Socket));
    }

    [Fact]
    public void StaleSocketFromACrash_IsReplaced()
    {
        // Bound, never listening: what a killed RHI leaves (connects are refused). Kept open, because .NET removes the
        // file when a bound socket is disposed and a killed process never does.
        using var stale = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stale.Bind(new UnixDomainSocketEndPoint(Socket));
        Assert.True(File.Exists(Socket));
        var received = new TaskCompletionSource<HandoffRequest>();

        using var listener = InstanceHandoff.Listen(request => received.TrySetResult(request), Socket);

        Assert.NotNull(listener);
        Assert.True(InstanceHandoff.TrySend(new HandoffRequest(HandoffKind.Launch, "steam:1"), Socket));
    }

    [Fact]
    public void FolderOthersCanEnter_NoHandoff()
    {
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        Assert.Null(InstanceHandoff.Listen(_ => { }, Socket));
        Assert.False(InstanceHandoff.TrySend(new HandoffRequest(HandoffKind.Launch, "steam:1"), Socket));
    }

    [Fact]
    public void SymlinkedFolder_EvenToAPrivateOne_NoHandoff()
    {
        var real = Directory.CreateDirectory(Path.Combine(_dir, "r"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        var link = Path.Combine(_dir, "l");
        Directory.CreateSymbolicLink(link, real);
        Assert.True(InstanceHandoff.IsPrivateFolder(real)); // the target itself would do

        Assert.False(InstanceHandoff.IsPrivateFolder(link));
        Assert.Null(InstanceHandoff.Listen(_ => { }, Path.Combine(link, "handoff.sock")));
        Assert.False(File.Exists(Path.Combine(real, "handoff.sock")));
    }

    /// <summary>Sends one raw line of exactly <paramref name="lineBytes"/> bytes (a valid request padded by an unknown field).</summary>
    private async Task<(string Answer, int Handled)> SendLineOf(int lineBytes)
    {
        var handled = 0;
        using var listener = InstanceHandoff.Listen(_ => Interlocked.Increment(ref handled), Socket);
        var head = "{\"Kind\":\"Launch\",\"Value\":\"steam:1\",\"Pad\":\"";
        var line = head + new string('x', lineBytes - head.Length - 2) + "\"}";
        Assert.Equal(lineBytes, Encoding.UTF8.GetByteCount(line));
        using var client = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(Socket));
        client.Send(Encoding.UTF8.GetBytes(line + "\n"));
        var buffer = new byte[16];
        var read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
        listener!.Dispose();
        return (Encoding.UTF8.GetString(buffer, 0, read), Volatile.Read(ref handled));
    }

    [Fact]
    public async Task MessageLongerThan8KiB_IsRefused_8KiBIsRead()
    {
        Assert.Equal(("ok\n", 1), await SendLineOf(8 * 1024));
        Assert.Equal(("refused\n", 0), await SendLineOf(8 * 1024 + 1));
    }

    [Fact]
    public async Task MalformedMessage_IsRefused_AndNothingIsHandled()
    {
        var handled = 0;
        using var listener = InstanceHandoff.Listen(_ => Interlocked.Increment(ref handled), Socket);
        using var client = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(Socket));
        client.Send(Encoding.UTF8.GetBytes("{\"Kind\":\"Launch\",\"Value\":\"a\\u0000b\"}\n"));
        var buffer = new byte[16];
        var read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("refused\n", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.Equal(0, handled);
    }
}

/// <summary>#162 in the app: the right-click choice's status line, and a shortcut launch going through Play with its HDR plan.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GameShortcutLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shortcut-vm-" + Guid.NewGuid().ToString("N"));

    public GameShortcutLaunchTests() => Directory.CreateDirectory(Path.Combine(_root, "Desktop"));

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GameShortcutPresenter Presenter() => new(new DesktopShortcutService(_ => Task.FromResult<string?>(Path.Combine(_root, "Desktop")),
        Path.Combine(_root, "applications"), Path.Combine(_root, "icons"), () => ["/opt/rhi/RHI"], () => []));

    private DetectedGame Game(string name, int appId)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe, SteamAppId = appId,
            Runner = "Proton", CompatibilityTool = "GE-Proton11-3", CompatibilityLayerType = "proton", LaunchCommand = "WINEPREFIX=/tmp/pfx wine Game.exe"
        };
    }

    private static LinuxHdrLaunchProfileService Gamescope() => new(
        environment: name => name switch { "XDG_SESSION_TYPE" => "wayland", "WAYLAND_DISPLAY" => "wayland-0", "XDG_CURRENT_DESKTOP" => "KDE", _ => null },
        commandExists: command => command == "gamescope", nvidiaVersion: () => null);

    private static async Task SettleAsync(ViewModelFixture fx)
    {
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
    }

    [Fact]
    public async Task ShortcutLaunch_SelectsTheGame_AndPlaysItWithItsSavedHdrProfile()
    {
        var launcher = new Recording();
        using var fx = await ViewModelFixture.StartAsync([Game("Night City", 1091500), Game("Other", 42)], launcher: launcher, hdrProfiles: Gamescope());
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Night City").Game;
        await SettleAsync(fx);
        fx.Vm.Runtime.SelectedHdrMode = fx.Vm.Runtime.HdrModeOptions.ForDisplay(UiLocalizationService.Text("Ui.HdrGamescope")); // saved for this game
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Other").Game;
        await SettleAsync(fx);

        await Presenter().LaunchByKeyAsync(fx.Vm, "steam:1091500");

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal("Night City", launch.Game);
        Assert.Equal("gamescope --hdr-enabled --", launch.Options?.CommandPrefix);
        Assert.Equal("Night City", fx.Vm.SelectedGame?.Name);
    }

    [Fact]
    public async Task ShortcutForAGameNoLongerInTheLibrary_SaysSo_AndLaunchesNothing()
    {
        var launcher = new Recording();
        using var fx = await ViewModelFixture.StartAsync([Game("Night City", 1091500)], launcher: launcher);

        await Presenter().LaunchByKeyAsync(fx.Vm, "steam:999");

        Assert.Empty(launcher.Launches);
        Assert.Contains("A shortcut asked for a game that is no longer in the library", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFromTheCard_InPortuguese_SaysWhereTheShortcutIs()
    {
        UiLocalizationService.Apply("pt-BR");
        var game = Game("Night City", 1091500);

        var line = await Presenter().CreateAsync(game, ShortcutPlace.Desktop);

        var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "Desktop")));
        Assert.Equal($"Night City: atalho criado na área de trabalho ({file}).", line);
        Assert.Contains("X-RHI-Linux-GameKey=steam:1091500", File.ReadAllLines(file));
    }

    private sealed class Recording : IGameLaunchOptionsLauncher
    {
        public List<(string Game, GameLaunchOptions? Options)> Launches { get; } = [];
        public bool CanLaunch(DetectedGame game) => true;

        public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, null));
            return Task.CompletedTask;
        }

        public Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, options));
            return Task.CompletedTask;
        }
    }
}
