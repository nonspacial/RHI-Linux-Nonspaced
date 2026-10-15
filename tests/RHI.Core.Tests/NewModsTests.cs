using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Persistence;

namespace RHI.Core.Tests;

/// <summary>
/// #185: the "New mods" notice — RenoDX, Luma and Ultra+ mods that were not in their list when the user last marked them
/// as seen. The first read of a list only remembers it (no notice on first start); a list that cannot be read is skipped
/// and never remembered empty; "Mark as seen" remembers and hides the button, Close keeps it.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NewModsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-new-mods-" + Guid.NewGuid().ToString("N"))).FullName;
    private string SeenPath => Path.Combine(_root, "data", "seen-mods.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private NewModsSources Sources(IReadOnlyList<string>? renoDx = null, IReadOnlyList<string>? luma = null, IReadOnlyList<string>? ultraPlus = null,
        bool lumaFails = false)
        => new(new SeenModsStore(SeenPath),
            _ => Task.FromResult(renoDx ?? (IReadOnlyList<string>)[]),
            _ => lumaFails ? throw new HttpRequestException("offline") : Task.FromResult(luma ?? (IReadOnlyList<string>)[]),
            _ => Task.FromResult(ultraPlus ?? (IReadOnlyList<string>)[]));

    [Fact]
    public async Task FirstStart_RemembersEveryList_WithoutANotice_ThenOnlyAddedModsAreNew()
    {
        var first = new NewModsPresenter();
        await first.CheckAsync(Task.CompletedTask, Sources(["Alpha", "Beta"], ["Luma One"], ["Ultra One"]));
        Assert.False(first.HasNewMods);
        Assert.True(first.IsHidden);
        var seen = new SeenModsStore(SeenPath).Load()!;
        Assert.Equal(["Alpha", "Beta"], seen.RenoDx);
        Assert.Equal(["Luma One"], seen.Luma);
        Assert.Equal(["Ultra One"], seen.UltraPlus);

        var later = new NewModsPresenter();
        await later.CheckAsync(Task.CompletedTask, Sources(["Alpha", "beta", "Gamma", "Delta"], ["Luma One", "Luma Two"], ["Ultra One"]));
        Assert.True(later.HasNewMods);
        Assert.False(later.IsHidden);
        Assert.Equal(["Delta", "Gamma"], later.NewRenoDx); // sorted; names compare ignoring case
        Assert.Equal(["Luma Two"], later.NewLuma);
        Assert.Empty(later.NewUltraPlus);
        Assert.Equal("3 RenoDX, Luma or Ultra+ mods appeared since you last marked them as seen", later.ButtonTip);
    }

    [Fact]
    public async Task ButtonTip_WordsOneNewModInTheSingular_InBothLanguages()
    {
        await new NewModsPresenter().CheckAsync(Task.CompletedTask, Sources(["Alpha"], ["Luma One"], ["Ultra One"]));
        var one = new NewModsPresenter();
        await one.CheckAsync(Task.CompletedTask, Sources(["Alpha", "Beta"], ["Luma One"], ["Ultra One"]));
        Assert.Equal("1 RenoDX, Luma or Ultra+ mod appeared since you last marked the mods as seen", one.ButtonTip);
        var two = new NewModsPresenter();
        await two.CheckAsync(Task.CompletedTask, Sources(["Alpha", "Beta"], ["Luma One", "Luma Two"], ["Ultra One"]));
        Assert.Equal("2 RenoDX, Luma or Ultra+ mods appeared since you last marked them as seen", two.ButtonTip);

        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("1 mod de RenoDX, Luma ou Ultra+ apareceu desde a última vez que você marcou os mods como vistos", one.ButtonTip);
            Assert.Equal("2 mods de RenoDX, Luma ou Ultra+ apareceram desde a última vez que você os marcou como vistos", two.ButtonTip);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task MarkAsSeen_RemembersAndHides_CloseKeepsTheButton()
    {
        await new NewModsPresenter().CheckAsync(Task.CompletedTask, Sources(["Alpha"], ["Luma One"], ["Ultra One"]));
        var presenter = new NewModsPresenter();
        await presenter.CheckAsync(Task.CompletedTask, Sources(["Alpha", "Beta"], ["Luma One"], ["Ultra One", "Ultra Two"]));
        IReadOnlyList<NewModsSection>? shown = null;
        presenter.Show = sections => { shown = sections; return Task.FromResult(false); }; // Close

        Assert.Null(await presenter.OpenAsync());
        Assert.Equal([UiLocalizationService.Format("Cat.NewModsRenoDx", 1), UiLocalizationService.Format("Cat.NewModsUltraPlus", 1)], shown!.Select(s => s.Heading));
        Assert.True(presenter.HasNewMods);

        presenter.Show = _ => Task.FromResult(true); // Mark as seen
        Assert.Null(await presenter.OpenAsync());
        Assert.False(presenter.HasNewMods);
        var again = new NewModsPresenter();
        await again.CheckAsync(Task.CompletedTask, Sources(["Alpha", "Beta"], ["Luma One"], ["Ultra One", "Ultra Two"]));
        Assert.False(again.HasNewMods);
    }

    [Fact]
    public async Task AListThatCannotBeRead_IsNeverRememberedEmpty_AndIsSeededWhenItFirstLoads()
    {
        var offline = new NewModsPresenter();
        await offline.CheckAsync(Task.CompletedTask, Sources(["Alpha"], lumaFails: true));
        Assert.Empty(new SeenModsStore(SeenPath).Load()!.Luma);

        var online = new NewModsPresenter();
        await online.CheckAsync(Task.CompletedTask, Sources(["Alpha"], ["Luma One", "Luma Two"]));
        Assert.False(online.HasNewMods); // its first successful read only seeds it
        Assert.Equal(["Luma One", "Luma Two"], new SeenModsStore(SeenPath).Load()!.Luma);
    }

    [Fact]
    public async Task AnUnreadableSeenFile_ShowsNothing_AndIsNeverOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeenPath)!);
        File.WriteAllText(SeenPath, "{ not json");
        var presenter = new NewModsPresenter();
        await presenter.CheckAsync(Task.CompletedTask, Sources(["Alpha"], ["Luma One"]));
        Assert.False(presenter.HasNewMods);
        Assert.Equal("{ not json", File.ReadAllText(SeenPath));
        Assert.Equal(UiLocalizationService.Text("Cat.NewModsNotSaved"), presenter.MarkSeen());
    }

    [Fact]
    public async Task Sections_ShowThirtyNamesEach_InUpstreamsOrder()
    {
        await new NewModsPresenter().CheckAsync(Task.CompletedTask, Sources(["Seed"], ["Seed"], ["Seed"]));
        var many = Enumerable.Range(1, 35).Select(i => $"Mod {i:00}").ToArray();
        var presenter = new NewModsPresenter();
        await presenter.CheckAsync(Task.CompletedTask, Sources(["Seed", .. many], ["Seed", "New Luma"], ["Seed", "New Ultra"]));
        var sections = presenter.Sections();
        Assert.Equal(["New RenoDX mods: 35", "New Ultra+ mods: 1", "New Luma mods: 1"], sections.Select(s => s.Heading));
        Assert.Equal(30, sections[0].Names.Count);
        Assert.Equal("… and 5 more", sections[0].More);
        Assert.Null(sections[1].More);
    }

    [Fact]
    public async Task TheNotice_WaitsForTheLibraryToLoad()
    {
        var initialization = new TaskCompletionSource();
        var asked = false;
        var sources = new NewModsSources(new SeenModsStore(SeenPath), _ => { asked = true; return Task.FromResult((IReadOnlyList<string>)["A"]); },
            _ => Task.FromResult((IReadOnlyList<string>)[]), _ => Task.FromResult((IReadOnlyList<string>)[]));
        var check = new NewModsPresenter().CheckAsync(initialization.Task, sources);
        await Task.Delay(50);
        Assert.False(asked);
        initialization.SetResult();
        await check;
        Assert.True(asked);
    }

    [Fact]
    public async Task TheApp_WiresTheNoticesLists_AndTheViewModelHasThePresenter()
    {
        using var http = new HttpClient(new GameLinkCatalogueTests.Fake(_ => throw new HttpRequestException("offline test")));
        var wine = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        Assert.NotNull(services.NewModsSources);
        Assert.Empty(await services.NewModsSources!.UltraPlus(CancellationToken.None)); // offline: nothing, never a throw
        using var fx = await ViewModelFixture.StartAsync([]);
        Assert.NotNull(fx.Vm.NewMods);
        Assert.True(fx.Vm.NewMods.IsHidden);
    }
}
