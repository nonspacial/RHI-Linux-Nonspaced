using System.Collections.Concurrent;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #245 in the app: the selected game's Luma row for a Nexus-only mod shows "Update on Nexus" (EN / PT-BR) with its ~2 s
/// tooltip only when Nexus has a newer file; pressing it opens the mod's Nexus page and downloads nothing. Without a Nexus
/// account it reads "Open on Nexus" and the tooltip names Connect Nexus account; when Nexus has nothing newer it is hidden.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LumaNexusRowTests : IDisposable
{
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-row-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData("en-US", "Update on Nexus", "Opens this mod's page on Nexus Mods; RHI cannot download it for you.")]
    [InlineData("pt-BR", "Atualizar no Nexus", "Abre a página deste mod no Nexus Mods; o RHI não pode baixá-lo por você.")]
    public async Task NewerOnNexus_TheRowSaysUpdateOnNexus_AndOpensThePage_WithNoDownload(string language, string label, string tip)
    {
        using var fx = await StartAsync(language, key: "key", newer: true);
        using var _ = Language(language);
        var opened = new List<string>();
        fx.Vm.LumaNexus.OpenLink = url => { opened.Add(url); return (true, null); };
        var row = await LumaRowAsync(fx);

        Assert.True(row.LumaNexus.IsVisible);
        Assert.Equal(label, row.LumaNexus.Label);
        Assert.Equal(tip, row.LumaNexus.Tip);
        Assert.Equal(LumaNexusOnlyTests.AndromedaPage, row.LumaNexus.Url);
        Assert.True(row.IsRemoveAction); // the row's own Remove stays where it was

        var before = _requests.Count;
        row.LumaNexus.Command!.Execute(row.LumaNexus.Url);

        Assert.Equal([LumaNexusOnlyTests.AndromedaPage], opened);
        Assert.Equal(before, _requests.Count); // nothing asked or downloaded by the press
        Assert.DoesNotContain(_requests, url => url.Contains("Mass_Effect_Andromeda", StringComparison.Ordinal) || url.Contains("nexus-cdn", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US", "Open on Nexus", "Settings → Accounts & updates → Connect Nexus account")]
    [InlineData("pt-BR", "Abrir no Nexus", "Configurações → Contas e atualizações → Conectar conta do Nexus")]
    public async Task WithoutANexusAccount_TheRowOnlyOpensThePage_AndItsTipNamesConnectNexusAccount(string language, string label, string control)
    {
        using var fx = await StartAsync(language, key: null, newer: true);
        using var _ = Language(language);
        var row = await LumaRowAsync(fx);

        Assert.True(row.LumaNexus.IsVisible);
        Assert.Equal(label, row.LumaNexus.Label);
        Assert.Contains(control, row.LumaNexus.Tip, StringComparison.Ordinal);
        Assert.DoesNotContain(_requests, url => url.Contains("api.nexusmods.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]   // Nexus has nothing newer
    [InlineData(true, false)]   // Nexus could not be asked (error)
    public async Task NothingNewer_OrNexusNotAnswering_TheButtonIsHidden(bool failNexus, bool newerFileAbsent)
    {
        using var fx = await StartAsync("en-US", key: "key", newer: !newerFileAbsent, failNexus: failNexus);
        var row = await LumaRowAsync(fx);

        Assert.False(row.LumaNexus.IsVisible);
    }

    [Fact]
    public async Task APageOutsideNexus_IsNeverOpened()
    {
        using var fx = await StartAsync("en-US", key: "key", newer: true);
        var opened = new List<string>();
        fx.Vm.LumaNexus.OpenLink = url => { opened.Add(url); return (true, null); };

        fx.Vm.LumaNexus.OpenCommand.Execute("https://example.org/masseffectandromeda/mods/1351");
        fx.Vm.LumaNexus.OpenCommand.Execute("http://www.nexusmods.com/masseffectandromeda/mods/1351");

        Assert.Empty(opened);
    }

    private sealed class LanguageScope : IDisposable
    {
        public void Dispose() => RHI.Desktop.Localization.UiLocalizationService.Apply("en-US");
    }

    private static LanguageScope Language(string language)
    {
        RHI.Desktop.Localization.UiLocalizationService.Apply(language);
        return new LanguageScope();
    }

    private async Task<ViewModelFixture> StartAsync(string language, string? key, bool newer, bool failNexus = false)
    {
        var dir = Path.Combine(_root, "Mass Effect Andromeda");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "MassEffectAndromeda.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 4096);
        var game = new DetectedGame
        {
            Name = "Mass Effect: Andromeda", InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Source = "Test",
            Runner = "Proton", PrefixPath = Path.Combine(dir, "pfx"), GraphicsApi = GraphicsApi.DirectX11
        };
        var files = LumaNexusOnlyTests.Files((1, "MAIN", LumaNexusOnlyTests.InstalledAt.AddDays(newer ? 19 : -3), "1.2"));
        var fx = await ViewModelFixture.StartAsync([game], language, httpResponse: request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Enqueue(url);
            if (url == LumaCatalogService.WikiUrl) return LumaNexusOnlyTests.Answer(LumaNexusOnlyTests.WikiSnippet, "text/html");
            if (url == LumaNexusOnlyTests.AndromedaFiles)
                return failNexus ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : LumaNexusOnlyTests.Answer(files, "application/json");
            return null;
        }, lumaNexusKey: () => key);
        var addon = Path.Combine(dir, "Luma-Mass_Effect_Andromeda.addon64");
        File.WriteAllBytes(addon, [1, 2, 3]);
        fx.Records.Upsert(LumaNexusOnlyTests.InstalledRecord(game, LumaNexusOnlyTests.AndromedaGitHubAsset, "Luma-Mass_Effect_Andromeda.zip", addon));
        return fx;
    }

    /// <summary>Selects the game afresh (the record came after start-up), then waits for the analysis and the Nexus answer.</summary>
    private static async Task<ComponentRowViewModel> LumaRowAsync(ViewModelFixture fx)
    {
        fx.Vm.LumaNexus.FollowRows();
        var selected = fx.Vm.Games.Single(row => row.Game.Name == "Mass Effect: Andromeda").Game;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == selected.Name) done.TrySetResult(); }
        fx.Vm.SelectedGame = null;
        fx.Vm.Session.Invalidate(selected);
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = selected;
            if (!fx.Vm.Session.TryGetCached(selected, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromMinutes(1));
        await fx.Vm.LumaNexus.Checking.WaitAsync(TimeSpan.FromMinutes(1));
        return fx.Vm.Components.Single(row => row.Kind == ManagedComponentKind.LumaFramework);
    }
}
