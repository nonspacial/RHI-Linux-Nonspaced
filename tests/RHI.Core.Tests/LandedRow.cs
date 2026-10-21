using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #204 rows for the operations wave 8's other lanes made journaled (w8 integration): the operation's ledger write number
/// <c>at</c> lands and then throws, in <paramref name="shape"/>; the operation must report success with the landed note
/// (<see cref="DeploymentScope.Landed"/>), not a failure for a change the ledger kept.
/// </summary>
internal static class LandedRow
{
    public static async Task<ComponentOperationResult> RunAsync(LinuxDeploymentRecordStore records, LandedShape shape, int at,
        Func<Task<ComponentOperationResult>> act, bool removal = false)
    {
        LandedLedgerWrite.Arm(records, shape, at);
        ComponentOperationResult result;
        try { result = await act(); }
        finally { LandedLedgerWrite.Disarm(records); }
        Assert.True(result.Success, result.Message);
        Assert.StartsWith(removal ? DeploymentScope.LandedRemovalPrefix : DeploymentScope.LandedChangePrefix, result.Message, StringComparison.Ordinal);
        return result;
    }

    /// <summary>Every shape with every name in <paramref name="operations"/>.</summary>
    public static TheoryData<LandedShape, string> ShapesTimes(params string[] operations)
    {
        var data = new TheoryData<LandedShape, string>();
        foreach (var shape in new[] { LandedShape.AfterCache, LandedShape.InsideWrite })
            foreach (var operation in operations) data.Add(shape, operation);
        return data;
    }
}
