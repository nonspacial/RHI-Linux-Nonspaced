using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204 for the Addon Manager's ledger record (#168): applying a selection, changing it and clearing it (a removal). For
// every ledger write of each, dying right after the write landed and throwing right after it landed leave the same
// add-on files, the same older-build copy and the same record, in both seam shapes; a landed commit write reports success.
public sealed partial class AddonManagerLedgerTests
{
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task ApplyChangeAndClear_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var instances = new List<AddonManagerLedgerTests>();
        try
        {
            async Task<CommitPointCase> Make(string[]? first, string[] then)
            {
                var t = new AddonManagerLedgerTests();
                instances.Add(t);
                var s = t.Fixture(journaled: true);
                if (first is not null) Assert.True((await s.Service.ApplySelectionAsync(s.Game, first)).Success);
                // The older-build copy holds absolute paths and a stamp over them: compared by the add-ons it lists.
                return new CommitPointCase(t._root, [t.P("game")], s.Records, t.LedgerPath, s.Journal!, t.JournalDir,
                    () => s.Service.ApplySelectionAsync(s.Game, then),
                    Extra: () => !File.Exists(t.CopyPath) ? "no copy"
                        : string.Join(",", (JsonSerializer.Deserialize<OlderCopy>(File.ReadAllText(t.CopyPath))!.Games.GetValueOrDefault(t.Key(s.Game))?.Files
                            ?? []).Select(f => Path.GetFileName(f.Path)).Order(StringComparer.Ordinal)));
            }

            var apply = await CommitPointAgreement.AssertEveryWriteAsync(_ => Make(null, ["custom-Alpha"]), shape);
            var change = await CommitPointAgreement.AssertEveryWriteAsync(_ => Make(["custom-Alpha"], ["custom-Beta"]), shape);
            var clear = await CommitPointAgreement.AssertEveryWriteAsync(_ => Make(["custom-Alpha"], []), shape);

            Assert.True(apply[^1].Success);
            Assert.True(change[^1].Success);
            Assert.True(clear[^1].Success);
            Assert.StartsWith(DeploymentScope.LandedRemovalPrefix, clear[^1].Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var t in instances) t.Dispose();
        }
    }
}
