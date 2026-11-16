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

    /// <summary>
    /// w14/nr-2 (wave 14 check): the numbers users' records already hold, pinned. Wave 14 added GITC Uplift (24, nr lane),
    /// HSF (30, extras-addons lane: a deliberate gap) and DLSS5 Tool (25, nr-2). 26–29 are free and nothing reads them; the
    /// gap matters only as "not 24 or 25", which this pins.
    /// </summary>
    [Fact]
    public void PersistedValues_StayAsReleasedAndAsWave14Saved()
    {
        var expected = new Dictionary<ManagedComponentKind, int>
        {
            [ManagedComponentKind.Unknown] = -1, [ManagedComponentKind.ReShade] = 0, [ManagedComponentKind.RenoDX] = 1,
            [ManagedComponentKind.RenoDXUpgrade] = 2, [ManagedComponentKind.ReLimiter] = 3, [ManagedComponentKind.DisplayCommander] = 4,
            [ManagedComponentKind.MfgAdaUnlock] = 5, [ManagedComponentKind.Dlss5Feeder] = 6, [ManagedComponentKind.Dlss5Dx11Bridge] = 7,
            [ManagedComponentKind.OptiScaler] = 8, [ManagedComponentKind.REFramework] = 9, [ManagedComponentKind.LumaFramework] = 10,
            [ManagedComponentKind.DofFix] = 12, [ManagedComponentKind.Dlss] = 13, [ManagedComponentKind.Streamline] = 14,
            [ManagedComponentKind.UltimateAsiLoader] = 15, [ManagedComponentKind.ShortFuse] = 16, [ManagedComponentKind.Dlssg2030Unlock] = 17,
            [ManagedComponentKind.DlssEnablerStandalone] = 18, [ManagedComponentKind.Rtx40MfgStandalone] = 19, [ManagedComponentKind.NrCostScaler] = 20,
            [ManagedComponentKind.RtxEncore] = 21, [ManagedComponentKind.ReShadeAddonManager] = 22, [ManagedComponentKind.DxvkHdr] = 23,
            [ManagedComponentKind.GitcUplift] = 24, [ManagedComponentKind.Dlss5Tool] = 25, [ManagedComponentKind.Hsf] = 30,
        };
        Assert.Equal(expected.Count, Enum.GetValues<ManagedComponentKind>().Length); // a new member is added here too
        Assert.All(expected, pair => Assert.Equal(pair.Value, (int)pair.Key));
    }
}
