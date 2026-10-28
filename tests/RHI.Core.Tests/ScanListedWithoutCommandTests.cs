using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// w12 follow-up (merge review nit 1): games a detector listed without a launch command (Lutris prefixes whose store entries
/// each start another game, Wine prefixes whose runtime markers cannot be read) get their own plain line in the scan notice,
/// naming the control that fixes it, EN + PT-BR; they are never counted in the "N item(s) skipped" line.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ScanListedWithoutCommandTests
{
    [Theory]
    [InlineData("en-US", "Lutris: 2 game(s) listed without a launch command — set one in Edit game", "skipped")]
    [InlineData("pt-BR", "Lutris: 2 jogo(s) listado(s) sem comando de execução — defina um em Editar jogo", "ignorado")]
    public void ListedWithoutACommand_HasItsOwnLine_NeverTheSkippedCount(string language, string line, string skippedWord)
    {
        var scan = new GameLibraryScanResult([], [new DetectorScanStatus("Lutris", 5, null, [], ListedWithoutLaunchCommand: 2)], []);
        UiLocalizationService.Apply(language);
        try
        {
            var problems = LibraryScanStatusText.Problems(scan, null);

            Assert.NotNull(problems);
            Assert.Contains(line, problems, StringComparison.Ordinal);
            Assert.DoesNotContain(skippedWord, problems, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void ARealSkip_BesideListedGames_KeepsBothLines()
    {
        var scan = new GameLibraryScanResult([], [new DetectorScanStatus("Wine", 3, null, ["Wine prefix folder /x skipped"], ListedWithoutLaunchCommand: 1)], []);
        UiLocalizationService.Apply("en-US");

        var problems = LibraryScanStatusText.Problems(scan, null)!;

        Assert.Contains("Wine: 1 item(s) skipped", problems, StringComparison.Ordinal);
        Assert.Contains("Wine: 1 game(s) listed without a launch command — set one in Edit game", problems, StringComparison.Ordinal);
    }
}
