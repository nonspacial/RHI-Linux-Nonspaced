using RHI.Core.Models;

namespace RHI.Core.Tests;

/// <summary>
/// <see cref="ManagedComponentKind"/> values are persisted as numbers in deployments.json: every member keeps its own
/// value and the retired slot 11 stays unused, so a record is never read back as another component (w3 integration:
/// parity's NrCostScaler = 20 checked against every other lane's additions).
/// </summary>
public sealed class ManagedComponentKindTests
{
    [Fact]
    public void EveryMember_HasItsOwnValue_AndTheRetiredSlotStaysFree()
    {
        var values = Enum.GetValues<ManagedComponentKind>().Select(kind => (int)kind).ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.DoesNotContain(11, values);
        Assert.Equal(20, (int)ManagedComponentKind.NrCostScaler);
    }
}
