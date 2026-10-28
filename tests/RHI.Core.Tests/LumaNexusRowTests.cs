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
/// Correction round 1: also "Open on Nexus" for a Nexus-only mod RHI has not installed (F2) and when Nexus refused the saved
/// key (F3, tooltip names Settings → Accounts &amp; updates); an Unreal game keeps its Luma ⚙ beside it (F1).
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

    // Review F1 / correction 2: an Unreal Engine game (Mass Effect, Borderlands 2 … are UE3) shows the Luma Engine.ini ⚙
    // (#189) and the Nexus button on the same row. Laid out by the row template with this row's own visibility, both are
    // whole, apart and each the hit target at its centre, in the order Nexus → ⚙ → Install/Remove → ⓘ, at every width.
    [Theory]
    [InlineData("en-US", null)]
    [InlineData("pt-BR", null)]
    [InlineData("en-US", "key")]
    public async Task UnrealLumaGame_ShowsTheEngineIniGear_AndTheNexusButton(string language, string? key)
    {
        using var fx = await StartAsync(language, key, newer: true, engineHint: "Unreal Engine 3");
        using var _ = Language(language);
        var row = await LumaRowAsync(fx);

        Assert.True(row.HasIniSettings);
        Assert.True(row.LumaNexus.IsVisible);
        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Text(key is null ? "Lu.OpenOnNexus" : "Lu.UpdateOnNexus"), row.LumaNexus.Label);
        foreach (var width in new[] { 2061.0, 1600.0, 900.0, 520.0, 300.0 })
            LumaNexusRowLayoutTests.AssertNexusFirst_BothWholeAndHitTargets(LumaNexusRowLayoutTests.FromTemplate(path => Shown(row, path)), width);
    }

    /// <summary>The row's value for a template <c>IsVisible</c> binding path ("LumaNexus.IsVisible", "HasIniSettings" …).</summary>
    private static bool Shown(ComponentRowViewModel row, string path)
    {
        object? value = row;
        foreach (var name in path.Split('.'))
            value = value?.GetType().GetProperty(name)?.GetValue(value) ?? throw new Xunit.Sdk.XunitException($"No '{path}' on the row");
        return (bool)value!;
    }

    // Review F2: a Nexus-only mod RHI has not installed: "Open on Nexus" opens its page (where to get it); Nexus is not
    // asked, account or not.
    [Theory]
    [InlineData("en-US", null, "Open on Nexus", "Download it there and drop the file on RHI to install it.")]
    [InlineData("pt-BR", null, "Abrir no Nexus", "Baixe-o lá e solte o arquivo no RHI para instalá-lo.")]
    [InlineData("en-US", "key", "Open on Nexus", "Download it there and drop the file on RHI to install it.")]
    public async Task NotInstalledByRhi_TheRowOpensTheNexusPage_WithoutAskingNexus(string language, string? key, string label, string tip)
    {
        using var fx = await StartAsync(language, key, newer: true, installed: false);
        using var _ = Language(language);
        var opened = new List<string>();
        fx.Vm.LumaNexus.OpenLink = url => { opened.Add(url); return (true, null); };
        var row = await LumaRowAsync(fx);

        Assert.True(row.LumaNexus.IsVisible);
        Assert.Equal(label, row.LumaNexus.Label);
        Assert.EndsWith(tip, row.LumaNexus.Tip, StringComparison.Ordinal);
        var before = _requests.Count;
        row.LumaNexus.Command!.Execute(row.LumaNexus.Url);
        Assert.Equal([LumaNexusOnlyTests.AndromedaPage], opened);
        Assert.Equal(before, _requests.Count);
        Assert.DoesNotContain(_requests, url => url.Contains("api.nexusmods.com", StringComparison.Ordinal));
    }

    // Review F3: Nexus refused the saved key (401/403): "Open on Nexus", its tooltip names the control that fixes it.
    [Theory]
    [InlineData("en-US", System.Net.HttpStatusCode.Unauthorized, "Settings → Accounts & updates → Disconnect, then Connect Nexus account")]
    [InlineData("pt-BR", System.Net.HttpStatusCode.Forbidden, "Configurações → Contas e atualizações → Desconectar e depois Conectar conta do Nexus")]
    public async Task RefusedKey_TheRowOpensThePage_AndItsTipNamesAccountsAndUpdates(string language, System.Net.HttpStatusCode status, string control)
    {
        using var fx = await StartAsync(language, key: "revoked", newer: true, failNexus: true, failStatus: status);
        using var _ = Language(language);
        var row = await LumaRowAsync(fx);

        Assert.True(row.LumaNexus.IsVisible);
        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Text("Lu.OpenOnNexus"), row.LumaNexus.Label);
        Assert.Contains(control, row.LumaNexus.Tip, StringComparison.Ordinal);
    }

    // Review F4 (R4): a wiki link to Nexus that is not an https Nexus page is never offered (nor opened), installed or not.
    [Fact]
    public async Task NotInstalled_ANexusLinkThatIsNotAnHttpsNexusPage_IsHidden()
    {
        var wiki = LumaNexusOnlyTests.WikiSnippet.Replace("https://www.nexusmods.com/masseffectandromeda/mods/1351", "http://www.nexusmods.com/masseffectandromeda/mods/1351", StringComparison.Ordinal);
        using var fx = await StartAsync("en-US", key: null, newer: true, installed: false, wiki: wiki);
        var row = await LumaRowAsync(fx);

        Assert.False(row.LumaNexus.IsVisible);
    }

    // Luma is never deployed to a native Linux game: no Nexus button there.
    [Fact]
    public async Task NativeLinuxGame_HasNoNexusButton()
    {
        using var fx = await StartAsync("en-US", key: null, newer: true, installed: false, runner: "Native Linux");
        fx.Vm.LumaNexus.FollowRows();
        var row = await LumaRowOrNullAsync(fx);

        Assert.False(row?.LumaNexus.IsVisible ?? false);
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

    private async Task<ViewModelFixture> StartAsync(string language, string? key, bool newer, bool failNexus = false,
        System.Net.HttpStatusCode failStatus = System.Net.HttpStatusCode.InternalServerError, bool installed = true, string? engineHint = null,
        string? wiki = null, string runner = "Proton")
    {
        var dir = Path.Combine(_root, "Mass Effect Andromeda");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "MassEffectAndromeda.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 4096);
        var game = new DetectedGame
        {
            Name = "Mass Effect: Andromeda", InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Source = "Test",
            Runner = runner, PrefixPath = Path.Combine(dir, "pfx"), GraphicsApi = GraphicsApi.DirectX11, EngineHint = engineHint
        };
        var files = LumaNexusOnlyTests.Files((1, "MAIN", LumaNexusOnlyTests.InstalledAt.AddDays(newer ? 19 : -3), "1.2"));
        var fx = await ViewModelFixture.StartAsync([game], language, httpResponse: request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Enqueue(url);
            if (url == LumaCatalogService.WikiUrl) return LumaNexusOnlyTests.Answer(wiki ?? LumaNexusOnlyTests.WikiSnippet, "text/html");
            if (url == LumaNexusOnlyTests.AndromedaFiles)
                return failNexus ? new HttpResponseMessage(failStatus) : LumaNexusOnlyTests.Answer(files, "application/json");
            return null;
        }, lumaNexusKey: () => key);
        if (!installed) return fx;
        var addon = Path.Combine(dir, "Luma-Mass_Effect_Andromeda.addon64");
        File.WriteAllBytes(addon, [1, 2, 3]);
        fx.Records.Upsert(LumaNexusOnlyTests.InstalledRecord(game, LumaNexusOnlyTests.AndromedaGitHubAsset, "Luma-Mass_Effect_Andromeda.zip", addon));
        return fx;
    }

    private static async Task<ComponentRowViewModel> LumaRowAsync(ViewModelFixture fx)
        => await LumaRowOrNullAsync(fx) ?? throw new Xunit.Sdk.XunitException("No Luma row");

    /// <summary>Selects the game afresh (the record came after start-up), then waits for the analysis and the Nexus answer.</summary>
    private static async Task<ComponentRowViewModel?> LumaRowOrNullAsync(ViewModelFixture fx)
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
        return fx.Vm.Components.SingleOrDefault(row => row.Kind == ManagedComponentKind.LumaFramework);
    }
}
