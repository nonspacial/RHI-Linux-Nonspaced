using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #207: the Vulkan layer's install journals a linked ReShade.ini at the shared file it writes, never the link.
public sealed partial class ReShadeChannelTests
{
    [Theory]
    [InlineData("ledger-blocked")]
    [InlineData("crash")]
    public async Task VulkanLayer_LinkedReShadeIni_InstallFailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var name = "vk-linked-" + failure;
        var (game, store) = CreateVulkanGame(name);
        var shared = ReShadeLinkedIniJournalTests.LinkSharedIni(game.BinaryDirectory!);
        var journalDir = Path.Combine(_root, name + "-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, name + "-cache"), overrides, journal);
        service.IniDefaults = (_, _) => new ReShadeIniDefaults { Template = Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.Template) };
        var ledger = Path.Combine(_root, name, "records.json"); // CreateGame's ledger

        await ReShadeLinkedIniJournalTests.FailAsync(store, journal, journalDir, ledger, failure, saveThatFails: 1,
            () => service.InstallAsync(game, LayerOptions()),
            () => ReShadeLinkedIniJournalTests.AssertLinked(game.BinaryDirectory!, shared, Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore)));
    }
}
