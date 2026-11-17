using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// A fake <c>pkexec</c> and file flags for #290: <c>chattr +i/-i</c> set and clear an in-memory immutable flag (the file
/// itself is never touched beyond what RHI does), and every call is recorded as the argv it was given. Never runs anything.
/// </summary>
internal sealed class FakePrivilegedRunner : IPrivilegedRunner, IFileFlagsReader
{
    public const string Pkexec = "/usr/bin/pkexec";
    public const string Chattr = "/usr/bin/chattr";

    public bool HasPkexec { get; set; } = true;
    public bool HasChattr { get; set; } = true;
    public bool Supported { get; set; } = true;

    /// <summary>The exit code pkexec gives (126: prompt dismissed, 127: authentication failed); 0 runs chattr.</summary>
    public int ExitCode { get; set; }

    /// <summary>chattr says it worked but sets nothing.</summary>
    public bool Ignores { get; set; }

    public List<(string FileName, string[] Arguments)> Calls { get; } = [];
    public HashSet<string> Immutable { get; } = new(StringComparer.Ordinal);

    public string? Find(string tool) => tool switch
    {
        "pkexec" => HasPkexec ? Pkexec : null,
        "chattr" => HasChattr ? Chattr : null,
        _ => null
    };

    public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments.ToArray()));
        if (ExitCode != 0) return Task.FromResult(new ExternalProcessResult(ExitCode, "", "Error executing command as another user: Request dismissed"));
        if (fileName == Pkexec && arguments is [Chattr, var flag, var path] && !Ignores)
        {
            if (flag == "+i") Immutable.Add(path);
            else if (flag == "-i") Immutable.Remove(path);
        }
        return Task.FromResult(new ExternalProcessResult(0, "", ""));
    }

    public bool? IsImmutable(string path) => Supported ? Immutable.Contains(path) : null;

    public SteamUpdateBlockService Service(string originalsPath) => new(this, this, originalsPath);
}

/// <summary>
/// #290 (upstream 2.9.4 "Block Steam updates"): on Linux the block is the manifest's "Only update this game when I launch
/// it", read-only, and immutable through pkexec chattr +i (a fake here); the fallback without the flag says so; turning
/// it off puts everything back; refused mid-update; never another app's manifest. Manifest text is a real Steam client
/// appmanifest (Cyberpunk 2077, 2026-11), trimmed.
/// </summary>
public sealed class SteamUpdateBlockTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-steamblock-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakePrivilegedRunner _pkexec = new();
    private SteamUpdateBlockService Service => _pkexec.Service(Path.Combine(_root, "config", "steam-update-blocks.json"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private static string Acf(int appId, string installDir, int stateFlags = 4, string autoUpdate = "0") => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"universe"		"1"
        	"name"		"Cyberpunk 2077"
        	"StateFlags"		"{{stateFlags}}"
        	"installdir"		"{{installDir}}"
        	"LastUpdated"		"1762430000"
        	"SizeOnDisk"		"91235432110"
        	"buildid"		"20412345"
        	"AutoUpdateBehavior"		"{{autoUpdate}}"
        	"InstalledDepots"
        	{
        		"1091501"
        		{
        			"manifest"		"4986512345678901234"
        			"size"		"91235432110"
        		}
        	}
        }
        """;

    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private (DetectedGame Game, string Manifest) SteamGame(int appId = 1091500, string dir = "Cyberpunk 2077", int stateFlags = 4, string source = "Steam", string? text = null)
    {
        var steamapps = Path.Combine(_root, "library", "steamapps");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", dir)).FullName;
        var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        File.WriteAllText(manifest, text ?? Acf(appId, dir, stateFlags));
        File.SetUnixFileMode(manifest, Mode644);
        return (new DetectedGame { Name = "Cyberpunk 2077", InstallPath = install, SteamAppId = appId, Source = source }, manifest);
    }

    private static string? AutoUpdate(string manifest)
        => File.ReadAllLines(manifest).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("\"AutoUpdateBehavior\"", StringComparison.Ordinal));

    private static bool Writable(string manifest) => (File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite) != 0;

    [Fact]
    public async Task Block_SetsLaunchOnly_ReadOnly_AndImmutable_ThroughPkexecChattr()
    {
        var (game, manifest) = SteamGame();
        var before = File.ReadAllText(manifest);
        Assert.Equal(SteamUpdateBlockState.Allowed, Service.State(game));

        var result = await Service.SetAsync(game, blocked: true);

        Assert.True(result.Success);
        Assert.Null(result.Limit);
        Assert.Equal(SteamUpdateBlockState.Blocked, result.State);
        Assert.Equal(SteamUpdateBlockState.Blocked, Service.State(game));
        var call = Assert.Single(_pkexec.Calls);
        Assert.Equal(FakePrivilegedRunner.Pkexec, call.FileName);
        Assert.Equal([FakePrivilegedRunner.Chattr, "+i", manifest], call.Arguments);
        Assert.False(Writable(manifest));
        // Only the AutoUpdateBehavior value changed, formatting kept byte for byte.
        Assert.Equal(before.Replace("\"AutoUpdateBehavior\"\t\t\"0\"", "\"AutoUpdateBehavior\"\t\t\"1\"", StringComparison.Ordinal), File.ReadAllText(manifest));
    }

    [Fact]
    public async Task Allow_UnlocksFirst_ThenPutsBackTheModeAndAutoUpdateBehavior()
    {
        var (game, manifest) = SteamGame();
        var before = File.ReadAllBytes(manifest);
        await Service.SetAsync(game, blocked: true);

        var result = await Service.SetAsync(game, blocked: false);

        Assert.True(result.Success);
        Assert.Equal(SteamUpdateBlockState.Allowed, result.State);
        Assert.Equal([FakePrivilegedRunner.Chattr, "-i", manifest], _pkexec.Calls[^1].Arguments);
        Assert.Empty(_pkexec.Immutable);
        Assert.Equal(Mode644, File.GetUnixFileMode(manifest));
        Assert.Equal(before, File.ReadAllBytes(manifest));
    }

    [Fact]
    public async Task Allow_OfAManifestThatHadNoAutoUpdateLine_RemovesTheOneRhiAdded()
    {
        var text = Acf(1091500, "Cyberpunk 2077").Replace("\t\"AutoUpdateBehavior\"\t\t\"0\"\n", "", StringComparison.Ordinal);
        var (game, manifest) = SteamGame(text: text);
        Assert.Null(AutoUpdate(manifest));

        await Service.SetAsync(game, blocked: true);
        Assert.Equal("\"AutoUpdateBehavior\"\t\t\"1\"", AutoUpdate(manifest));
        await Service.SetAsync(game, blocked: false);

        Assert.Equal(text, File.ReadAllText(manifest));
    }

    [Theory]
    [InlineData(126)] // the polkit dialog dismissed
    [InlineData(127)] // authentication failed
    public async Task CancelledAuth_LeavesLaunchOnlyAndReadOnly_AndSaysSo(int exitCode)
    {
        var (game, manifest) = SteamGame();
        _pkexec.ExitCode = exitCode;

        var result = await Service.SetAsync(game, blocked: true);

        Assert.True(result.Success);
        Assert.Equal(SteamUpdateBlockLimit.Cancelled, result.Limit);
        Assert.Equal(SteamUpdateBlockState.LaunchOnly, result.State);
        Assert.Equal(SteamUpdateBlockState.LaunchOnly, Service.State(game));
        Assert.False(Writable(manifest));
        Assert.Equal("\"AutoUpdateBehavior\"\t\t\"1\"", AutoUpdate(manifest));
    }

    [Fact]
    public async Task MissingPkexec_FallsBack_WithoutRunningAnything()
    {
        var (game, manifest) = SteamGame();
        _pkexec.HasPkexec = false;

        var result = await Service.SetAsync(game, blocked: true);

        Assert.Equal(SteamUpdateBlockLimit.NoPkexec, result.Limit);
        Assert.Equal(SteamUpdateBlockState.LaunchOnly, result.State);
        Assert.Empty(_pkexec.Calls);
        Assert.False(Writable(manifest));
        Assert.Equal("\"AutoUpdateBehavior\"\t\t\"1\"", AutoUpdate(manifest));
    }

    [Fact]
    public async Task MissingChattr_FallsBack_WithoutRunningAnything()
    {
        var (game, _) = SteamGame();
        _pkexec.HasChattr = false;

        var result = await Service.SetAsync(game, blocked: true);

        Assert.Equal(SteamUpdateBlockLimit.NoChattr, result.Limit);
        Assert.Empty(_pkexec.Calls);
    }

    [Fact]
    public async Task UnsupportedFileSystem_FallsBack_WithoutAskingForAPassword()
    {
        var (game, manifest) = SteamGame();
        _pkexec.Supported = false;

        var result = await Service.SetAsync(game, blocked: true);

        Assert.Equal(SteamUpdateBlockLimit.Unsupported, result.Limit);
        Assert.Equal(SteamUpdateBlockState.LaunchOnly, result.State);
        Assert.Empty(_pkexec.Calls);
        Assert.False(Writable(manifest));
    }

    [Fact]
    public async Task AFlagThatDoesNotReadAsSet_IsNotShownAsBlocked()
    {
        var (game, _) = SteamGame();
        _pkexec.Ignores = true;

        var result = await Service.SetAsync(game, blocked: true);

        Assert.Equal(SteamUpdateBlockLimit.NotConfirmed, result.Limit);
        Assert.Equal(SteamUpdateBlockState.LaunchOnly, Service.State(game));
    }

    [Fact]
    public async Task Allow_FromTheFallback_RestoresEverything_WithoutPkexec()
    {
        var (game, manifest) = SteamGame();
        var before = File.ReadAllBytes(manifest);
        _pkexec.HasPkexec = false;
        await Service.SetAsync(game, blocked: true);

        var result = await Service.SetAsync(game, blocked: false);

        Assert.Equal(SteamUpdateBlockState.Allowed, result.State);
        Assert.Empty(_pkexec.Calls);
        Assert.Equal(Mode644, File.GetUnixFileMode(manifest));
        Assert.Equal(before, File.ReadAllBytes(manifest));
    }

    [Fact]
    public async Task Allow_WhenTheUnlockPromptIsCancelled_ChangesNothing()
    {
        var (game, manifest) = SteamGame();
        await Service.SetAsync(game, blocked: true);
        var locked = File.ReadAllBytes(manifest);
        _pkexec.ExitCode = 126;

        var result = await Service.SetAsync(game, blocked: false);

        Assert.False(result.Success);
        Assert.Equal(SteamUpdateBlockService.UnlockCancelledMessage, result.Message);
        Assert.Equal(SteamUpdateBlockState.Blocked, Service.State(game));
        Assert.False(Writable(manifest));
        Assert.Equal(locked, File.ReadAllBytes(manifest));
    }

    [Fact]
    public async Task ARetryAfterACancelledPrompt_KeepsTheFirstOriginals()
    {
        var (game, manifest) = SteamGame();
        var before = File.ReadAllBytes(manifest);
        _pkexec.ExitCode = 126;
        await Service.SetAsync(game, blocked: true);
        _pkexec.ExitCode = 0;

        Assert.Equal(SteamUpdateBlockState.Blocked, (await Service.SetAsync(game, blocked: true)).State);
        await Service.SetAsync(game, blocked: false);

        Assert.Equal(before, File.ReadAllBytes(manifest));
        Assert.Equal(Mode644, File.GetUnixFileMode(manifest));
    }

    [Fact]
    public async Task TheManifestPath_ReachesChattrAsOneArgvEntry_Verbatim()
    {
        var dir = "Odd $(touch pwned); `id` 'q' & name";
        var (game, manifest) = SteamGame(appId: 2050650, dir: dir);

        await Service.SetAsync(game, blocked: true);

        var call = Assert.Single(_pkexec.Calls);
        Assert.Equal(3, call.Arguments.Length);
        Assert.Equal(manifest, call.Arguments[2]);
    }

    [Fact]
    public void PkexecRunner_StartsPkexecItself_WithEachArgumentItsOwnArgvEntry_NoShell()
    {
        var path = "/home/u/.var/app/com.valvesoftware.Steam/data/Steam/steamapps/appmanifest_1.acf; rm -rf ~ $(id)";

        var start = PkexecRunner.StartInfo("/usr/bin/pkexec", ["/usr/bin/chattr", "+i", path]);

        Assert.Equal("/usr/bin/pkexec", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Equal(["/usr/bin/chattr", "+i", path], start.ArgumentList);
    }

    [Fact]
    public async Task PkexecRunner_HandsTheArgvOverUnchanged()
    {
        // A stand-in for pkexec that prints each argv entry on its own line: nothing a shell would expand is expanded.
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        var echo = Path.Combine(tools, "argv");
        File.WriteAllText(echo, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done\n");
        File.SetUnixFileMode(echo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = Path.Combine(_root, "pwned");
        var path = $"{_root}/a b/$(touch {marker}); `touch {marker}`";

        var run = await new PkexecRunner().RunAsync(echo, ["/usr/bin/chattr", "+i", path], CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(["/usr/bin/chattr", "+i", path], run.StandardOutput.TrimEnd('\n').Split('\n'));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void WithAutoUpdateBehavior_KeepsCrlfAndIndentation_AndAddsAMissingLineLikeItsNeighbours()
    {
        var crlf = "\"AppState\"\r\n{\r\n    \"appid\"  \"1\"\r\n    \"AutoUpdateBehavior\"  \"2\"\r\n}\r\n";
        Assert.Equal(crlf.Replace("\"2\"", "\"1\"", StringComparison.Ordinal), SteamUpdateBlockService.WithAutoUpdateBehavior(crlf, "1"));

        var missing = "\"AppState\"\n{\n  \"appid\" \"1\"\n  \"UserConfig\"\n  {\n    \"language\" \"english\"\n  }\n}\n";
        Assert.Equal("\"AppState\"\n{\n  \"appid\" \"1\"\n  \"UserConfig\"\n  {\n    \"language\" \"english\"\n  }\n  \"AutoUpdateBehavior\" \"1\"\n}\n",
            SteamUpdateBlockService.WithAutoUpdateBehavior(missing, "1"));
    }

    [Theory]
    [InlineData(1026)] // update running
    [InlineData(1542)] // update started + paused
    public async Task Block_WhileSteamIsUpdatingTheGame_IsRefused(int stateFlags)
    {
        var (game, manifest) = SteamGame(stateFlags: stateFlags);
        var before = File.ReadAllBytes(manifest);

        var result = await Service.SetAsync(game, blocked: true);

        Assert.False(result.Success);
        Assert.Equal(SteamUpdateBlockService.UpdatingMessage, result.Message);
        Assert.True(Writable(manifest));
        Assert.Equal(before, File.ReadAllBytes(manifest));
        Assert.Empty(_pkexec.Calls);
    }

    [Fact]
    public async Task AnUpdateQueuedButNotStarted_CanBeBlocked() // StateFlags 6: fully installed + update required
        => Assert.Equal(SteamUpdateBlockState.Blocked, (await Service.SetAsync(SteamGame(stateFlags: 6).Game, blocked: true)).State);

    [Fact]
    public async Task NotSteam_OrAManifestOfAnotherFolder_IsNeverTouched()
    {
        var (heroic, _) = SteamGame(source: "Heroic");
        Assert.Equal(SteamUpdateBlockState.Unavailable, Service.State(heroic));
        Assert.False((await Service.SetAsync(heroic, true)).Success);

        var (game, manifest) = SteamGame(appId: 292030, dir: "The Witcher 3");
        File.WriteAllText(manifest, Acf(292030, "Some Other Folder"));
        Assert.Equal(SteamUpdateBlockState.Unavailable, Service.State(game));
        Assert.Equal(SteamUpdateBlockService.NotSteamMessage, (await Service.SetAsync(game, true)).Message);
        Assert.True(Writable(manifest));
        Assert.Empty(_pkexec.Calls);

        var outside = new DetectedGame { Name = "Loose", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "games", "Loose")).FullName, SteamAppId = 1, Source = "Steam" };
        Assert.Equal(SteamUpdateBlockState.Unavailable, Service.State(outside));
    }

    [Fact]
    public async Task AManifestThatIsALink_IsNotOffered()
    {
        var (game, manifest) = SteamGame();
        var real = Path.Combine(_root, "elsewhere.acf");
        File.Move(manifest, real);
        File.CreateSymbolicLink(manifest, real);

        Assert.Equal(SteamUpdateBlockState.Unavailable, Service.State(game));
        Assert.False((await Service.SetAsync(game, true)).Success);
        Assert.Empty(_pkexec.Calls);
    }

    [Fact]
    public async Task AFallbackManifestSteamReplaced_ReadsAsAllowedAgain()
    {
        var (game, manifest) = SteamGame();
        _pkexec.HasPkexec = false;
        await Service.SetAsync(game, true);
        var text = File.ReadAllText(manifest);
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(manifest);
        File.WriteAllText(manifest, text); // Steam wrote a new file in its place (what immutable prevents)

        Assert.Equal(SteamUpdateBlockState.Allowed, Service.State(game));
    }

    [Fact]
    public void TheRealFlagsReader_ReadsAFileThisFileSystemHas()
    {
        var file = Path.Combine(_root, "plain.acf");
        File.WriteAllText(file, "x");

        // tmpfs and the usual disk file systems answer (false); one without inode flags answers null. Never true here.
        Assert.NotEqual(true, new IoctlFileFlagsReader().IsImmutable(file));
        Assert.Null(new IoctlFileFlagsReader().IsImmutable(Path.Combine(_root, "missing.acf")));
    }
}

/// <summary>#290 in the shell: the header action shows for Steam games only and says what it did, in EN and PT-BR.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SteamUpdateBlockViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-steamblock-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        RHI.Desktop.Localization.UiLocalizationService.Apply("en-US");
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("en-US", "Block Steam updates", "Steam updates blocked for Elden Ring: the game's Steam app manifest is locked (immutable), so Steam can't update the game.", "Allow Steam updates")]
    [InlineData("pt-BR", "Bloquear atualizações da Steam", "Atualizações da Steam bloqueadas para Elden Ring: o manifesto do jogo está travado (imutável), então a Steam não pode atualizar o jogo.", "Permitir atualizações da Steam")]
    public async Task SteamGame_OffersTheSwitch_AndBlocks(string language, string offer, string done, string undo)
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", "ELDEN RING")).FullName;
        var exe = Path.Combine(install, "eldenring.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var manifest = Path.Combine(steamapps, "appmanifest_1245620.acf");
        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"1245620\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"ELDEN RING\"\n}\n");
        var steam = new DetectedGame { Name = "Elden Ring", InstallPath = install, ExecutablePath = exe, BinaryDirectory = install, SteamAppId = 1245620, Source = "Steam", Runner = "Proton" };
        var other = new DetectedGame { Name = "Manual One", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "manual")).FullName, Source = "Manual", Runner = "Manual Windows" };
        using var fx = await ViewModelFixture.StartAsync([steam, other], uiLanguage: language);
        RHI.Desktop.Localization.UiLocalizationService.Apply(language);

        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Manual One");
        Assert.False(fx.Vm.CanBlockSteamUpdates);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Elden Ring");
        Assert.True(fx.Vm.CanBlockSteamUpdates);
        Assert.Equal(offer, fx.Vm.BlockSteamUpdatesText);

        await fx.Vm.ToggleSteamUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(done, fx.Vm.StatusText);
        Assert.Equal(undo, fx.Vm.BlockSteamUpdatesText);
        Assert.Equal(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
        Assert.Contains(manifest, fx.SteamPrivileged.Immutable);
        Assert.StartsWith(language == "en-US" ? "Blocked:" : "Bloqueado:", fx.Vm.SteamUpdateBlockStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Steam updates only partly blocked for Elden Ring: pkexec (polkit) is not installed, so RHI could not lock the manifest. The manifest is set to \"Only update this game when I launch it\" and read-only, but Steam may still update the game when you launch it.", "Partly blocked:")]
    [InlineData("pt-BR", "Atualizações da Steam só em parte bloqueadas para Elden Ring: o pkexec (polkit) não está instalado, então o RHI não pôde travar o manifesto. O manifesto está em \"Só atualizar este jogo quando eu o iniciar\" e somente leitura, mas a Steam ainda pode atualizar o jogo quando você o iniciar.", "Só em parte:")]
    public async Task WithoutPkexec_TheSwitchIsOn_ButNeverSaysBlocked(string language, string done, string status)
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", "ELDEN RING")).FullName;
        var exe = Path.Combine(install, "eldenring.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1245620.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1245620\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"ELDEN RING\"\n}\n");
        var steam = new DetectedGame { Name = "Elden Ring", InstallPath = install, ExecutablePath = exe, BinaryDirectory = install, SteamAppId = 1245620, Source = "Steam", Runner = "Proton" };
        using var fx = await ViewModelFixture.StartAsync([steam], uiLanguage: language);
        RHI.Desktop.Localization.UiLocalizationService.Apply(language);
        fx.SteamPrivileged.HasPkexec = false;
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Elden Ring");

        await fx.Vm.ToggleSteamUpdatesCommand.ExecuteAsync(null);

        Assert.StartsWith(done, fx.Vm.StatusText, StringComparison.Ordinal); // a start-up note may follow
        Assert.True(fx.Vm.SteamUpdatesBlocked);
        Assert.StartsWith(status, fx.Vm.SteamUpdateBlockStatus, StringComparison.Ordinal);
    }
}
