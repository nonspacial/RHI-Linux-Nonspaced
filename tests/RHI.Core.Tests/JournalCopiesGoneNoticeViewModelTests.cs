using RHI.Core.Deployment;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w10 review finding 1 (coordinator decision): an interrupted change whose undo can never finish because RHI's saved copy
/// of the earlier file is gone is closed, not held; the status line names the file, says why a restart cannot help and
/// that the game's mods may need checking (Remove, then Install again) — EN and PT-BR, and never the "will try again at
/// the next start" text of an undo that can still be retried.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class JournalCopiesGoneNoticeViewModelTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task ACopyThatIsGone_TheStatusLineNamesTheFile_AndSaysToCheckTheGamesMods(string language)
    {
        UiLocalizationService.Apply(language);
        try
        {
            using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language);
            var file = Path.Combine(fx.Root, "Game", "dxgi.dll");
            var log = Path.Combine(fx.Root, "recovery.log");
            var entry = new JournalRecoveryEntry("0a1b", "ReShade install", JournalRecoveryOutcome.CopiesGone,
                [$"{file}: no surviving copy with the pre-operation content (aa11) was found; every copy was kept."], [file])
            {
                CopiesGone = [file]
            };

            fx.Vm.ReportDeploymentRecovery(LinuxDeploymentRecovery.RecordSettled(entry, log));

            var line = fx.Vm.StatusText;
            Assert.Contains(UiLocalizationService.Format("Jh.CopiesGoneNotice", file, log), line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.Format("Vm.RHICouldNotUndoInterruptedInstall", 1, log), line, StringComparison.Ordinal);
            if (language == "en-US")
            {
                Assert.Contains($"RHI could not put back {file} after an interrupted change: its saved copy of the earlier file is gone", line, StringComparison.Ordinal);
                Assert.Contains("Check that game's mods: use Remove and then Install again", line, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"O RHI não conseguiu restaurar {file} depois de uma alteração interrompida", line, StringComparison.Ordinal);
                Assert.Contains("use Remover e depois Instalar de novo", line, StringComparison.Ordinal);
                Assert.DoesNotContain("could not put back", line, StringComparison.Ordinal);
            }
            Assert.Contains(file, File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }
}
