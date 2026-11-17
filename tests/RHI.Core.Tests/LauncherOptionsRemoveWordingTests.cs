using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Launch.LauncherOptions;

namespace RHI.Core.Tests;

/// <summary>
/// #318 (w14 last fixes): the "Remove from …" wording per launcher. PT-BR contracts "de" with the article of each launcher's
/// noun ("das opções de inicialização", "dos argumentos", "dos argumentos do jogo"), so each launcher has its own phrase.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LauncherOptionsRemoveWordingTests : IDisposable
{
    public void Dispose() => UiLocalizationService.Apply("en-US");

    [Theory]
    [InlineData("pt-BR", LauncherKind.Steam, "Remover das opções de inicialização do Steam", "Retira os argumentos do RHI das opções de inicialização do Steam:", "Quest: os argumentos do RHI saíram das opções de inicialização do Steam;")]
    [InlineData("pt-BR", LauncherKind.Lutris, "Remover dos argumentos do Lutris", "Retira os argumentos do RHI dos argumentos do Lutris:", "Quest: os argumentos do RHI saíram dos argumentos do Lutris;")]
    [InlineData("pt-BR", LauncherKind.Heroic, "Remover dos argumentos do jogo do Heroic", "Retira os argumentos do RHI dos argumentos do jogo do Heroic:", "Quest: os argumentos do RHI saíram dos argumentos do jogo do Heroic;")]
    [InlineData("en-US", LauncherKind.Steam, "Remove from Steam launch options", "Takes RHI's arguments out of Steam launch options:", "Quest: RHI's launch arguments are out of Steam launch options;")]
    [InlineData("en-US", LauncherKind.Lutris, "Remove from Lutris arguments", "Takes RHI's arguments out of Lutris arguments:", "Quest: RHI's launch arguments are out of Lutris arguments;")]
    [InlineData("en-US", LauncherKind.Heroic, "Remove from Heroic game arguments", "Takes RHI's arguments out of Heroic game arguments:", "Quest: RHI's launch arguments are out of Heroic game arguments;")]
    public void RemoveFrom_ReadsRightForEachLauncher(string language, LauncherKind kind, string button, string tipStart, string removedStart)
    {
        UiLocalizationService.Apply(language);

        Assert.Equal(button, LaunchArgumentsPresenter.RemoveText(kind));
        Assert.StartsWith(tipStart, LaunchArgumentsPresenter.RemoveTip(kind), StringComparison.Ordinal);
        Assert.StartsWith(removedStart, LaunchArgumentsPresenter.RemovedLine("Quest", kind, keptUserChange: false), StringComparison.Ordinal);
        Assert.StartsWith(removedStart, LaunchArgumentsPresenter.RemovedLine("Quest", kind, keptUserChange: true), StringComparison.Ordinal);
    }
}
