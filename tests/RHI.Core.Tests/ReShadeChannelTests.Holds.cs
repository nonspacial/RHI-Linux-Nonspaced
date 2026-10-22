using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w10 final review 2 (D15 for ReShade): a game held because the undo of its own earlier ReShade install could not finish
// (RHI's proxy DLL is still in the game folder, which turned read-only, and no record) gets the hold's restart text — not
// "ReShade is already present … not installed by RHI Linux", which took RHI's own half-undone DLL for an external setup.
public sealed partial class ReShadeChannelTests
{
    [PermissionDenialTheory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task OnAGameHeldByItsOwnReShadeUndo_TheInstallGetsTheRestartText_NotTheExternalReShadeRefusal(string language)
    {
        var (game, store) = CreateGame("held-" + language);
        var bin = game.BinaryDirectory!;
        var proxy = Path.Combine(bin, "dxgi.dll");
        var stamped = Path.Combine(_root, "held-" + language, "rhi-reshade.dll");
        PeArchitectureDetectorTests.WritePe(stamped, 0x8664, 64 * 1024);
        StampReShadeSignature(stamped);
        var proxyBytes = File.ReadAllBytes(stamped);
        var (journal, overrides) = SimulatedDeath.NewJournal(Path.Combine(_root, "held-" + language, "journal"), store);
        var cache = Path.Combine(_root, "held-" + language, "cache");
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "earlier ReShade install", game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(proxy, proxyBytes);
            store.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(bin);
                return new IOException("Simulated write failure: records.json");
            };
            try
            {
                Assert.NotNull(Record.Exception(() => store.Upsert(scope.Stamp(new LinuxDeploymentRecord
                {
                    GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
                    BinaryDirectory = bin, Files = [new LinuxDeployedFile { Path = proxy, Sha256 = FileHash.Sha256HexOf(proxyBytes) }]
                }))));
            }
            finally
            {
                store.SaveFaultForTesting = null;
            }
            Assert.False((await scope.RollbackAsync()).Success); // held: RHI's DLL cannot be taken away
        }
        try
        {
            Assert.True(BinarySignatureInspector.LooksLikeReShade(proxy)); // what the external-ReShade check would see

            var install = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

            Assert.False(install.Success);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, install.Message);
            var line = UiLocalizationService.PresentFor(language, install.Message);
            Assert.Contains(language == "pt-BR" ? "Reinicie o RHI para concluir" : "Restart RHI to finish it", line, StringComparison.Ordinal);
            Assert.DoesNotContain("already present", line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
            Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
            Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
        }
        finally
        {
            PermissionDenial.MakeWritable(bin);
        }
    }
}
