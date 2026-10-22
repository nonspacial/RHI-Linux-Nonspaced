using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w10 final review 2 (D15 for Luma): a game held because the undo of its own earlier Luma change could not finish (the
// package file that change placed is still there, its folder read-only) gets the hold's restart text — not "Foreign Luma
// protection", which took RHI's own half-undone file for someone else's. Both the catalogue install and the Luma package
// from an archive (the archive install hands RHI's copy of the .zip to InstallLocalPackageAsync); EN and PT-BR lines.
public sealed partial class LumaFrameworkDeploymentServiceTests
{
    [PermissionDenialTheory]
    [InlineData("en-US", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en-US", false)]
    [InlineData("pt-BR", false)]
    public async Task OnAGameHeldByItsOwnLumaUndo_TheInstallGetsTheRestartText_NotTheForeignLumaRefusal(string language, bool fromArchive)
    {
        var game = CreateGame();
        var records = NewRecords($"held-{language}-{fromArchive}.json");
        SeedManagedReShade(records, game);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var shaders = Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "Shaders")).FullName;
        var earlier = Path.Combine(shaders, "Luma.fx"); // a file the package ships, placed by the earlier change
        var zip = MakeLumaZip();
        var package = Path.Combine(_root, $"Luma_FooGame-{language}.zip");
        File.WriteAllBytes(package, zip);
        using var http = fromArchive
            ? new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("A package from an archive must not use the network.")))
            : BuildHttp(zip, Sha256(zip));
        var journal = SimulatedDeath.NewJournal(Path.Combine(_root, $"journal-{language}-{fromArchive}"), records).Journal;
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, $"cache-held-{language}-{fromArchive}"), journal: journal);

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "earlier Luma change", game, ManagedComponentKind.LumaFramework, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(earlier, "earlier luma shader"u8.ToArray());
            records.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(shaders);
                return new IOException("Simulated write failure: records.json");
            };
            try
            {
                Assert.NotNull(Record.Exception(() => records.Upsert(scope.Stamp(new LinuxDeploymentRecord
                {
                    GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.LumaFramework,
                    BinaryDirectory = game.BinaryDirectory!,
                    Files = [new LinuxDeployedFile { Path = earlier, Sha256 = FileHash.Sha256HexOf("earlier luma shader"u8.ToArray()) }]
                }))));
            }
            finally
            {
                records.SaveFaultForTesting = null;
            }
            Assert.False((await scope.RollbackAsync()).Success); // held: the earlier file cannot be taken away
        }
        try
        {
            Assert.True(File.Exists(earlier));

            var install = fromArchive ? await service.InstallLocalPackageAsync(game, package) : await service.InstallAsync(game);

            Assert.False(install.Success);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, install.Message);
            var line = UiLocalizationService.PresentFor(language, install.Message);
            Assert.Contains(language == "pt-BR" ? "Reinicie o RHI para concluir" : "Restart RHI to finish it", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Foreign Luma", line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
            Assert.Equal("earlier luma shader", File.ReadAllText(earlier));
            Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Luma.addon64"))); // nothing installed
            Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
        }
        finally
        {
            PermissionDenial.MakeWritable(shaders);
        }
    }
}
