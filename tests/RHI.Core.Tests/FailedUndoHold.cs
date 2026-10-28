using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Correction round 1 of lane w12/mfg (review F1): holds <paramref name="game"/> by its own failed undo — an earlier change
/// of <paramref name="component"/> wrote <paramref name="file"/>, its record could not be saved, and its rollback failed
/// because the game folder turned read-only — so the next operation on the game is refused with
/// <see cref="DeploymentJournal.EarlierUndoFailedMessage"/>. The file is left holding <paramref name="halfUndone"/> (RHI's
/// own half-undone bytes, which a hash check would take for a change made outside RHI). Dispose makes the folder writable.
/// </summary>
internal sealed class FailedUndoHold : IDisposable
{
    private readonly string _folder;

    private FailedUndoHold(string folder) => _folder = folder;

    public static async Task<FailedUndoHold> ArmAsync(DeploymentJournal journal, LinuxDeploymentRecordStore records, DetectedGame game,
        ManagedComponentKind component, string file, byte[] halfUndone)
    {
        var folder = Path.GetDirectoryName(file)!;
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "earlier change", game, component, CancellationToken.None);
        Assert.Null(refusal);
        var hold = new FailedUndoHold(folder);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(file, halfUndone);
            records.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(folder);
                return new IOException("Simulated write failure: deployments.json");
            };
            try { Assert.NotNull(Record.Exception(() => records.Upsert(scope.Stamp(records.Find(game, component)!)))); }
            finally { records.SaveFaultForTesting = null; }
            Assert.False((await scope.RollbackAsync()).Success);
        }
        Assert.Equal(halfUndone, File.ReadAllBytes(file));
        return hold;
    }

    public void Dispose() => PermissionDenial.MakeWritable(_folder);
}
