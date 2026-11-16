using RHI.Platform.Linux.Detection.CustomFolders;

namespace RHI.Core.Tests;

/// <summary>
/// #293 (upstream 2.9.0 custom game folders, pinned from upstream's <c>CustomGameFolderTests</c> at RHI-2.9.4): the exe
/// picker, the folder-name cleaner, the folder validation and the candidate roots. Exes are fake files (not PE), so the
/// probe reports no graphics imports and not console: the layouts alone must pick the right exe.
/// </summary>
public sealed class CustomGameFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-cgf-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Game(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    internal static void Put(string dir, string rel, int kb)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[kb * 1024]);
    }

    private static ExeRanking Rank(string dir) => CustomGameExeScorer.Rank(dir, probe: _ => new ExeProbe(false, false));

    [Fact]
    public void Unreal_ShippingExeBeatsRootStub_AndTheCrashReporterIsLeftOut()
    {
        var g = Game("MyGame");
        Put(g, "MyGame.exe", 300);
        Put(g, "MyGame/Binaries/Win64/MyGame-Win64-Shipping.exe", 4000);
        Put(g, "Engine/Binaries/Win64/CrashReportClient.exe", 2000);
        var r = Rank(g);
        Assert.EndsWith("MyGame-Win64-Shipping.exe", r.Exes[0].FullPath);
        Assert.Equal("MyGame/Binaries/Win64/MyGame-Win64-Shipping.exe", r.Exes[0].RelativePath);
        Assert.False(r.IsAmbiguous);
        Assert.DoesNotContain(r.Exes, e => e.FullPath.EndsWith("CrashReportClient.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Unity_CrashHandlerExcluded()
    {
        var g = Game("Cool Unity Game");
        Put(g, "Cool Unity Game.exe", 650);
        Put(g, "UnityPlayer.dll", 1);
        Put(g, "UnityCrashHandler64.exe", 1200);
        var r = Rank(g);
        Assert.Single(r.Exes);
        Assert.EndsWith("Cool Unity Game.exe", r.Exes[0].FullPath);
    }

    [Fact]
    public void LauncherLosesToGame()
    {
        var g = Game("Space Thing");
        Put(g, "launcher.exe", 3000);
        Put(g, "game.exe", 2000);
        Assert.EndsWith("game.exe", Rank(g).Exes[0].FullPath);
    }

    [Fact]
    public void Dx12Variant_IsAmbiguous_CheckTheExe()
    {
        var g = Game("Game");
        Put(g, "Game.exe", 5000);
        Put(g, "Game_DX12.exe", 5000);
        var r = Rank(g);
        Assert.Equal(2, r.Exes.Count);
        Assert.True(r.IsAmbiguous);
    }

    [Fact]
    public void InstallersAndRedistsExcluded()
    {
        var g = Game("Quiet Game");
        Put(g, "QuietGame.exe", 2000);
        Put(g, "unins000.exe", 1000);
        Put(g, "vc_redist.x64.exe", 1000);
        Put(g, "_CommonRedist/DirectX/DXSETUP.exe", 500);
        Put(g, "redist/something.exe", 500);
        var r = Rank(g);
        Assert.Single(r.Exes);
        Assert.EndsWith("QuietGame.exe", r.Exes[0].FullPath);
    }

    [Fact]
    public void AnUpperCaseExeExtension_IsStillAnExe()
    {
        // Linux addition: Windows games copied from a FAT/NTFS drive keep "GAME.EXE"; the filesystem here is case sensitive.
        var g = Game("Old Game");
        Put(g, "GAME.EXE", 2000);
        Assert.Single(Rank(g).Exes);
    }

    [Fact]
    public void NoExe_NoCandidates()
    {
        var g = Game("Soundtrack Only");
        Put(g, "track01.flac", 10);
        Assert.Empty(Rank(g).Exes);
    }

    [Fact]
    public void ConsoleAndGraphicsProbeAffectScore()
    {
        var g = Game("Probe");
        Put(g, "a.exe", 1000);
        Put(g, "b.exe", 1000);
        var r = CustomGameExeScorer.Rank(g, probe: p => p.EndsWith("a.exe", StringComparison.Ordinal) ? new ExeProbe(false, true) : new ExeProbe(true, false));
        Assert.EndsWith("b.exe", r.Exes[0].FullPath);
        Assert.False(r.IsAmbiguous);
    }

    [Fact]
    public void Probe_ReadsGraphicsImportsAndTheConsoleSubsystem_FromARealPe()
    {
        var g = Game("Pe");
        var exe = Path.Combine(g, "game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var probe = CustomGameExeScorer.ProbeExe(exe);
        Assert.False(probe.IsConsole);
        Assert.False(CustomGameExeScorer.ProbeExe(Path.Combine(g, "missing.exe")).HasGraphicsImports);
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("vc_redist.x64.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("UE4PrereqSetup_x64.exe", true)]
    [InlineData("EasyAntiCheat_EOS_Setup.exe", true)]
    [InlineData("Game.exe", false)]
    [InlineData("Cyberpunk2077.exe", false)]
    public void ExcludedNames(string name, bool excluded) => Assert.Equal(excluded, CustomGameExeScorer.IsExcludedExeName(name));

    [Theory]
    [InlineData("Hollow Knight [GOG]", "Hollow Knight")]
    [InlineData("Cyberpunk.2077.v2.1-GOG", "Cyberpunk 2077")]
    [InlineData("Hades (v1.38290)", "Hades")]
    [InlineData("Hollow_Knight_v1.5.78.11833", "Hollow Knight")]
    [InlineData("Stardew Valley Build 12345 Repack", "Stardew Valley")]
    [InlineData("The Witcher 3 Wild Hunt GOTY", "The Witcher 3 Wild Hunt GOTY")]
    [InlineData("Some.Game-CODEX", "Some Game")]
    [InlineData("S.T.A.L.K.E.R. Shadow of Chernobyl", "S.T.A.L.K.E.R. Shadow of Chernobyl")]
    [InlineData("Disco Elysium - The Final Cut (DRM Free)", "Disco Elysium The Final Cut")]
    public void NameCleaner(string folder, string expected) => Assert.Equal(expected, CustomGameNameCleaner.Clean(folder));

    [Fact]
    public void Validate_RefusesBadFolders()
    {
        var home = Game("home");
        var lib = Game("home/Lib");
        var inner = Game("home/Lib/Sub");
        Assert.Null(CustomFolderScanService.ValidateFolder(lib, [], home));
        Assert.Equal(CustomFolderProblem.AlreadyListed, CustomFolderScanService.ValidateFolder(lib + "/", [lib], home)?.Problem);
        Assert.Equal(new CustomFolderRefusal(CustomFolderProblem.InsideListed, lib), CustomFolderScanService.ValidateFolder(inner, [lib], home));
        Assert.Equal(new CustomFolderRefusal(CustomFolderProblem.ContainsListed, inner), CustomFolderScanService.ValidateFolder(lib, [inner], home));
        Assert.Equal(CustomFolderProblem.WholeDrive, CustomFolderScanService.ValidateFolder("/", [], home)?.Problem);
        Assert.Equal(CustomFolderProblem.WholeDrive, CustomFolderScanService.ValidateFolder("/usr", [], home)?.Problem);
        Assert.Equal(CustomFolderProblem.HomeFolder, CustomFolderScanService.ValidateFolder(home, [], home)?.Problem);
        Assert.Equal(CustomFolderProblem.SteamLibrary, CustomFolderScanService.ValidateFolder(Game("home/steamapps/common"), [], home)?.Problem);
        Assert.Equal(CustomFolderProblem.Missing, CustomFolderScanService.ValidateFolder(Path.Combine(_root, "missing"), [], home)?.Problem);
        Assert.Equal(CustomFolderProblem.NoFolder, CustomFolderScanService.ValidateFolder(" ", [], home)?.Problem);
        // ~/Games is the usual place for such games: it is no game root, but it is a fine custom folder.
        Assert.Null(CustomFolderScanService.ValidateFolder(Game("home/Games"), [], home));
    }

    [Fact]
    public void Validate_RefusesAWholeWinePrefix_ButNotAFolderOfGamesInsideIt()
    {
        var prefix = Game("pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "");
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "");
        Assert.Equal(CustomFolderProblem.WinePrefix, CustomFolderScanService.ValidateFolder(prefix, [], _root + "/nohome")?.Problem);
        Assert.Equal(CustomFolderProblem.WinePrefix, CustomFolderScanService.ValidateFolder(Path.Combine(prefix, "drive_c"), [], _root + "/nohome")?.Problem);
        Assert.Null(CustomFolderScanService.ValidateFolder(Game("pfx/drive_c/Games"), [], _root + "/nohome"));
    }

    [Fact]
    public void CandidateRoots_LibraryVsSingleGame()
    {
        var lib = Game("Library");
        Put(lib, "One/one.exe", 500);
        Put(lib, "Two/two.exe", 500);
        Assert.Equal(2, CustomFolderScanService.GetCandidateRoots(lib).Count);
        Assert.Equal(Path.Combine(lib, "Two"), CustomFolderScanService.FindCandidateRoot(Path.Combine(lib, "Two", "bin"), [lib]));
        var single = Game("SingleGame");
        Put(single, "single.exe", 500);
        Put(single, "data/readme.txt", 1);
        Assert.Equal([CustomFolderScanService.NormalizePath(single)], CustomFolderScanService.GetCandidateRoots(single));
        Assert.Null(CustomFolderScanService.FindCandidateRoot(Path.Combine(_root, "Elsewhere"), [lib]));
    }

    [Fact]
    public void Scan_OffersNewGamesOnly_WithTheBestExeFirst_AndLeavesOutKnownIgnoredAndExeLessFolders()
    {
        var lib = Game("Games");
        Put(lib, "Hades.v1.38290-GOG/Hades.exe", 2000);
        Put(lib, "Hades.v1.38290-GOG/launcher.exe", 3000);
        Put(lib, "Already Listed/listed.exe", 2000);
        Put(lib, "Known By Name/known.exe", 2000);
        Put(lib, "Ignored Game/ignored.exe", 2000);
        Put(lib, "Soundtrack/track.flac", 1);
        Put(lib, ".hidden/h.exe", 2000);
        var found = CustomFolderScanService.Scan(new CustomFolderScanRequest
        {
            Folders = [lib + "/"],
            LibraryPaths = [Path.Combine(lib, "Already Listed")],
            KnownNames = new HashSet<string>(StringComparer.Ordinal) { CustomGameExeScorer.Norm("Known By Name") },
            IgnoredFolders = [Path.Combine(lib, "Ignored Game")],
            Probe = _ => new ExeProbe(false, false)
        });
        var hades = Assert.Single(found);
        Assert.Equal("Hades", hades.SuggestedName);
        Assert.Equal(Path.Combine(lib, "Hades.v1.38290-GOG"), hades.FolderPath);
        Assert.Equal(lib, hades.CustomFolder);
        Assert.Equal("Hades.exe", Path.GetFileName(hades.Exes[0].FullPath));
        Assert.Equal(2, hades.Exes.Count);
    }

    [Fact]
    public void Scan_TakesTheCanonicalTitle_AndAGenericFolderIsNamedAfterItsExe()
    {
        var lib = Game("Games2");
        Put(lib, "hollow_knight_v1.5.78/hollow_knight.exe", 2000);
        Put(lib, "game/Binaries/Win64/CoolProject-Win64-Shipping.exe", 4000);
        var found = CustomFolderScanService.Scan(new CustomFolderScanRequest
        {
            Folders = [lib],
            CanonicalTitle = name => name == "hollow knight" ? "Hollow Knight" : null,
            Probe = _ => new ExeProbe(false, false)
        });
        Assert.Equal(["Cool Project", "Hollow Knight"], found.Select(c => c.SuggestedName).Order(StringComparer.Ordinal));
        Assert.Equal("Unreal Engine", found.Single(c => c.SuggestedName == "Cool Project").Engine);
    }
}
