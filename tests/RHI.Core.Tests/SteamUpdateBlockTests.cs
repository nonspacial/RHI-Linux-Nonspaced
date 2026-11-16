using RHI.Core.Models;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// #290 (upstream 2.9.4 "Block Steam updates"): the game's Steam app manifest is made read-only (Steam cannot update the
/// game) and writable again; only its mode changes; refused mid-update; never another app's manifest. Manifest text is
/// a real Steam client appmanifest (Cyberpunk 2077, 2026-11), trimmed.
/// </summary>
public sealed class SteamUpdateBlockTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-steamblock-" + Guid.NewGuid().ToString("N"))).FullName;

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

    private static string Acf(int appId, string installDir, int stateFlags = 4) => $$"""
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
        	"AutoUpdateBehavior"		"0"
        }
        """;

    private (DetectedGame Game, string Manifest) SteamGame(int appId = 1091500, string dir = "Cyberpunk 2077", int stateFlags = 4, string source = "Steam")
    {
        var steamapps = Path.Combine(_root, "library", "steamapps");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", dir)).FullName;
        var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        File.WriteAllText(manifest, Acf(appId, dir, stateFlags));
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return (new DetectedGame { Name = "Cyberpunk 2077", InstallPath = install, SteamAppId = appId, Source = source }, manifest);
    }

    [Fact]
    public void Block_MakesTheManifestReadOnly_AllowGivesWriteBack_ContentsUntouched()
    {
        var (game, manifest) = SteamGame();
        var before = File.ReadAllBytes(manifest);
        Assert.Equal(SteamUpdateBlockState.Allowed, SteamUpdateBlock.State(game));

        var blocked = SteamUpdateBlock.Set(game, blocked: true);

        Assert.True(blocked.Success);
        Assert.Equal(SteamUpdateBlockState.Blocked, blocked.State);
        Assert.Equal(0, (int)(File.GetUnixFileMode(manifest) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)));

        var allowed = SteamUpdateBlock.Set(game, blocked: false);

        Assert.Equal(SteamUpdateBlockState.Allowed, allowed.State);
        Assert.NotEqual(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
        Assert.Equal(before, File.ReadAllBytes(manifest));
    }

    [Theory]
    [InlineData(1026)] // update running
    [InlineData(1542)] // update started + paused
    public void Block_WhileSteamIsUpdatingTheGame_IsRefused(int stateFlags)
    {
        var (game, manifest) = SteamGame(stateFlags: stateFlags);

        var result = SteamUpdateBlock.Set(game, blocked: true);

        Assert.False(result.Success);
        Assert.Equal(SteamUpdateBlock.UpdatingMessage, result.Message);
        Assert.NotEqual(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
    }

    [Fact]
    public void AnUpdateQueuedButNotStarted_CanBeBlocked() // StateFlags 6: fully installed + update required
        => Assert.True(SteamUpdateBlock.Set(SteamGame(stateFlags: 6).Game, blocked: true).Success);

    [Fact]
    public void NotSteam_OrAManifestOfAnotherFolder_IsNeverTouched()
    {
        var (heroic, _) = SteamGame(source: "Heroic");
        Assert.Equal(SteamUpdateBlockState.Unavailable, SteamUpdateBlock.State(heroic));
        Assert.False(SteamUpdateBlock.Set(heroic, true).Success);

        var (game, manifest) = SteamGame(appId: 292030, dir: "The Witcher 3");
        File.WriteAllText(manifest, Acf(292030, "Some Other Folder"));
        Assert.Equal(SteamUpdateBlockState.Unavailable, SteamUpdateBlock.State(game));
        Assert.Equal(SteamUpdateBlock.NotSteamMessage, SteamUpdateBlock.Set(game, true).Message);
        Assert.NotEqual(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));

        var outside = new DetectedGame { Name = "Loose", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "games", "Loose")).FullName, SteamAppId = 1, Source = "Steam" };
        Assert.Equal(SteamUpdateBlockState.Unavailable, SteamUpdateBlock.State(outside));
    }

    [Fact]
    public void AManifestSteamReplaced_ReadsAsAllowedAgain()
    {
        var (game, manifest) = SteamGame();
        SteamUpdateBlock.Set(game, true);
        var text = File.ReadAllText(manifest);
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(manifest);
        File.WriteAllText(manifest, text); // Steam wrote a new file in its place

        Assert.Equal(SteamUpdateBlockState.Allowed, SteamUpdateBlock.State(game));
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
    [InlineData("en-US", "Block Steam updates", "Steam updates blocked for Elden Ring: the game's Steam app manifest is now read-only.", "Allow Steam updates")]
    [InlineData("pt-BR", "Bloquear atualizações da Steam", "Atualizações da Steam bloqueadas para Elden Ring: o manifesto do jogo agora é somente leitura.", "Permitir atualizações da Steam")]
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

        fx.Vm.ToggleSteamUpdatesCommand.Execute(null);

        Assert.Equal(done, fx.Vm.StatusText);
        Assert.Equal(undo, fx.Vm.BlockSteamUpdatesText);
        Assert.Equal(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
    }
}
