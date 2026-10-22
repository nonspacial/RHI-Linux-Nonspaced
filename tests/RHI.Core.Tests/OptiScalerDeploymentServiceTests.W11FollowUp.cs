using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Wave 11 follow-up E7: OptiScaler's removal asks the hold before its hash checks (the toolkit rule since w10 D15). A
/// game held after a failed undo whose half-undone file is the chained <c>ReShade64.dll</c> got "Managed ReShade64.dll
/// was modified outside RHI", which took RHI's own file for the user's change.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    [PermissionDenialTheory]
    [InlineData("en-US", ManagedComponentKind.ReShade)]
    [InlineData("pt-BR", ManagedComponentKind.ReShade)]
    [InlineData("en-US", ManagedComponentKind.OptiScaler)]
    [InlineData("pt-BR", ManagedComponentKind.OptiScaler)]
    public async Task FollowUp_Remove_OnAGameHeldWithAHalfUndoneChainedReShade64_GetsTheRestartText(string language, ManagedComponentKind heldBy)
    {
        var name = $"w11fu-remove-held-{language}-{heldBy}";
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled(name, store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage(name + "-package")), j);
        Assert.True((await service.InstallAsync(game)).Success);
        var bin = game.BinaryDirectory!;
        var reshade64 = Path.Combine(bin, "ReShade64.dll");
        var reShadeRecord = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.GetFullPath(reshade64), Path.GetFullPath(Assert.Single(reShadeRecord.Files).Path)); // the chain
        var halfUndone = File.ReadAllBytes(reshade64);
        halfUndone[^1] ^= 0x5A;

        // An earlier change of this game (ReShade's in the chain, or OptiScaler's) whose undo could not finish: RHI's
        // half-undone ReShade64.dll sits in a game folder that turned read-only.
        var (opened, refusal) = await DeploymentScope.BeginAsync(j.Journal, "earlier change", game, heldBy, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(reshade64, halfUndone);
            store.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(bin);
                return new IOException("Simulated write failure: records.json");
            };
            try
            {
                Assert.NotNull(Record.Exception(() => store.Upsert(scope.Stamp(store.Find(game, heldBy)!))));
            }
            finally
            {
                store.SaveFaultForTesting = null;
            }
            Assert.False((await scope.RollbackAsync()).Success); // held: RHI's ReShade64.dll cannot be put back
        }
        try
        {
            // What the chain check would see.
            Assert.False(FileHash.HexEquals(FileHash.Sha256Hex(reshade64), reShadeRecord.Files[0].Sha256));
            var recordsJson = JsonSerializer.Serialize(store.FindAll(game));
            var optiFiles = store.Find(game, ManagedComponentKind.OptiScaler)!.Files.Select(file => file.Path).ToArray();

            var result = await service.RemoveAsync(game);

            Assert.False(result.Success);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, result.Message);
            var line = UiLocalizationService.PresentFor(language, result.Message);
            Assert.Contains(language == "pt-BR" ? "Reinicie o RHI para concluir" : "Restart RHI to finish it", line, StringComparison.Ordinal);
            Assert.DoesNotContain("modified outside RHI", line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
            if (language == "pt-BR") Assert.DoesNotContain("could not finish undoing", line, StringComparison.Ordinal);
            Assert.Equal(halfUndone, File.ReadAllBytes(reshade64));
            Assert.All(optiFiles, path => Assert.True(File.Exists(path), path));
            Assert.Equal(recordsJson, JsonSerializer.Serialize(store.FindAll(game)));
        }
        finally
        {
            PermissionDenial.MakeWritable(bin);
        }
    }
}
