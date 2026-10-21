using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #217: the RenoDX ⚙ shows "Fix colour grading updates (Unreal)" only for an Unreal game whose RenoDX RHI installed (not for
/// a Unity game); Off runs as one RenoDX operation, says what changed, and the dropdown then shows what is in effect.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class RenoDxEngineIniDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-lut-dialog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("en-US", "Fix colour grading updates (Unreal)", "Engine.ini: RHI's colour grading line was removed.")]
    [InlineData("pt-BR", "Corrigir a atualização da gradação de cor (Unreal)", "Engine.ini: a linha de gradação de cor do RHI foi removida.")]
    public async Task TheRow_ShowsForAnUnrealGame_NotForAUnityGame_AndOffTakesRhisLineOut(string language, string label, string removed)
    {
        var unreal = Game("Unreal Lut", "Unreal Engine 4");
        var unity = Game("Unity Lut", "Unity");
        using var fx = await ViewModelFixture.StartAsync([unreal, unity], language);
        UiLocalizationService.Apply(language);
        try
        {
        foreach (var game in new[] { unreal, unity }) Seed(fx, game);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == unity.Name).Game;
        Assert.Empty(fx.Vm.RenoDxEngineIni.RowsFor(_ => { }));

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == unreal.Name).Game;
        var lines = new List<string>();
        var row = Assert.Single(fx.Vm.RenoDxEngineIni.RowsFor(lines.Add));
        Assert.Equal(label, row.Label);
        Assert.Contains("r.LUT.UpdateEveryFrame=1", row.Tooltip); // the key only in the tooltip
        Assert.DoesNotContain("r.LUT", row.Label);
        Assert.Equal(1, row.SelectedIndex);

        row.SelectedIndex = 0;
        await row.Pending.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([removed], lines);
        Assert.Equal(0, row.SelectedIndex);
        Assert.DoesNotContain("r.LUT.UpdateEveryFrame", File.ReadAllText(EngineIni(unreal)));
        Assert.Equal(RenoDxEngineIni.Off, fx.Records.Find(unreal, ManagedComponentKind.RenoDX)!.Metadata[RenoDxEngineIni.LutChoiceMetadataKey]);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // The operation gate is held (another operation runs): the change is refused, says so, and the dropdown goes back.
    [Fact]
    public async Task WhileAnotherOperationRuns_TheChangeIsRefused_AndTheDropdownGoesBack()
    {
        var unreal = Game("Busy Lut", "Unreal Engine 5");
        using var fx = await ViewModelFixture.StartAsync([unreal]);
        Seed(fx, unreal);
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        var lines = new List<string>();
        var row = Assert.Single(fx.Vm.RenoDxEngineIni.RowsFor(lines.Add));
        using (fx.Vm.Operations.TryBegin("test"))
        {
            row.SelectedIndex = 0;
            await row.Pending.WaitAsync(TimeSpan.FromSeconds(60));
        }

        Assert.Equal([UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning")], lines);
        Assert.Equal(1, row.SelectedIndex);
        Assert.Contains("r.LUT.UpdateEveryFrame=1", File.ReadAllText(EngineIni(unreal)));
    }

    // #218: UE-Extended RenoDX whose install found the HDR keys apply gets the HDR row too; a generic Unreal add-on does not.
    [Fact]
    public async Task TheHdrRow_ShowsOnlyForUeExtended_WhereItsKeysApply()
    {
        var uee = Game("Uee Rows", "Unreal Engine 5");
        var generic = Game("Generic Rows", "Unreal Engine 5");
        using var fx = await ViewModelFixture.StartAsync([uee, generic]);
        Seed(fx, uee, ueExtended: true);
        Seed(fx, generic);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == uee.Name).Game;
        var rows = fx.Vm.RenoDxEngineIni.RowsFor(_ => { });
        Assert.Equal(["Fix colour grading updates (Unreal)", "Turn on the game's HDR output (UE-Extended)"], rows.Select(row => row.Label));
        Assert.Contains("r.HDR.EnableHDROutput=1", rows[1].Tooltip);
        Assert.Equal(0, rows[1].SelectedIndex); // nothing written yet: Off

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == generic.Name).Game;
        Assert.Equal(["Fix colour grading updates (Unreal)"], fx.Vm.RenoDxEngineIni.RowsFor(_ => { }).Select(row => row.Label));
    }

    private DetectedGame Game(string name, string engine)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Proj", "Binaries", "Win64")).FullName;
        var exe = Path.Combine(bin, "Proj-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var prefix = Path.Combine(root, "pfx");
        var game = new DetectedGame
        {
            Name = name, InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix, Runner = "Proton",
            Source = "Heroic", GraphicsApi = GraphicsApi.DirectX12, EngineHint = engine, EngineIniProjectOverride = "Proj"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(EngineIni(game))!);
        File.WriteAllText(EngineIni(game), "[SystemSettings]\nUserKeep=1\n");
        return game;
    }

    private static string EngineIni(DetectedGame game)
        => Path.Combine(game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "Proj", "Saved", "Config", "Windows", "Engine.ini");

    /// <summary>RHI's RenoDX on the game: its add-on and record, with RHI's LUT key in Engine.ini where it applies.</summary>
    private static void Seed(ViewModelFixture fx, DetectedGame game, bool ueExtended = false)
    {
        var addon = Path.Combine(game.BinaryDirectory!, "renodx-lut.addon64");
        PeArchitectureDetectorTests.WritePe(addon, 0x8664, 96 * 1024);
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = game.BinaryDirectory!, Version = "Lut",
            SourceUrl = ueExtended ? RenoDxDeploymentService.DefaultUeExtendedUrl : RenoDxCatalogService.GenericUnrealUrl,
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(addon) }]
        };
        if (ueExtended) record.Metadata[RenoDxEngineIni.HdrAppliesMetadataKey] = "1";
        if (RenoDxEngineIni.LutApplies(game))
            RenoDxEngineIni.Write(record, RenoDxEngineIni.Apply(EngineIni(game),
                new Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> { [RenoDxEngineIni.LutGroup] = RenoDxEngineIni.LutKeys },
                new HashSet<string>(), null));
        fx.Records.Upsert(record);
    }
}
