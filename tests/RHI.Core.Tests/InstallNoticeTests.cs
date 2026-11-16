using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/extras-fg (#285; upstream 2.9.3 <c>ShowClosedSourceNoticeAsync</c>): before the first install of RTX Encore or 20/30
/// FG Unlock (and ANR FG, from the OptiScaler lane) RHI says the mod is closed source, with Install, Cancel and "Don't show
/// this again"; a tick is remembered per mod even on Cancel. The rows' ⓘ opens the mod's GitHub page.
/// </summary>
public sealed class InstallNoticeTests
{
    private static DetectedGame Game(string name) => new() { Name = name, InstallPath = "/nonexistent/" + name, Source = "Test" };

    [Fact]
    public void ClosedSourceNotice_SaysWhoPublishesOnlyTheDll_AndPointsAtTheRowInfo()
    {
        var notice = InstallNoticePresenter.ClosedSource(ClosedSourceMod.RtxEncore);

        Assert.Equal("RtxEncoreClosedSourceDismissed", notice.Key);
        Assert.Equal(UiLocalizationService.Format("Notice.ClosedSourceTitle", "RTX Encore"), notice.Title);
        Assert.Equal("RTX Encore is closed source", UiLocalizationService.FormatFor("en-US", "Notice.ClosedSourceTitle", "RTX Encore"));
        Assert.Equal(
            "RTX Encore is closed source: SilyNoMeta publishes only the finished DLL, not its code, so nobody outside can check what it does.",
            UiLocalizationService.FormatFor("en-US", "Notice.ClosedSourceWhat", "RTX Encore", "SilyNoMeta"));
        Assert.Equal(2, notice.Paragraphs.Count);
        Assert.Null(notice.LinkUrl);
        Assert.Equal(UiLocalizationService.Text("Vm.Install"), notice.AcceptText);

        var dlssg = InstallNoticePresenter.ClosedSource(ClosedSourceMod.Dlssg2030);
        Assert.Equal("Dlssg2030ClosedSourceDismissed", dlssg.Key);
        Assert.Equal(UiLocalizationService.Format("Notice.ClosedSourceTitle", "20/30 FG Unlock"), dlssg.Title);
        Assert.Equal(UiLocalizationService.Format("Notice.ClosedSourceWhat", "20/30 FG Unlock", "sdli1995"), dlssg.Paragraphs[0]);
    }

    // ANR FG has no row of its own (it comes with an OptiScaler preset): its notice links to its GitHub page instead.
    [Fact]
    public void AnrFgNotice_CarriesItsGitHubPage()
    {
        var notice = InstallNoticePresenter.ClosedSource(ClosedSourceMod.AnrFg);

        Assert.Equal("AnrFgClosedSourceDismissed", notice.Key);
        Assert.Equal("https://github.com/apex-compute/ANR", notice.LinkUrl);
        Assert.Equal(UiLocalizationService.Format("Notice.ClosedSourceWhat", "ANR FG", "Apex Compute"), notice.Paragraphs[0]);
        Assert.Equal(UiLocalizationService.Format("Notice.ClosedSourceLink", "Apex Compute"), notice.Paragraphs[1]);
    }

    [Fact]
    public async Task Cancel_StopsTheInstall_AndAskingAgainShowsItAgain()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var asked = new List<DismissibleNotice>();
        fx.Vm.ConfirmDismissibleAsync = notice => { asked.Add(notice); return Task.FromResult(new DismissibleNoticeAnswer(false, false)); };

        Assert.False(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Cancelled"), ManagedComponentKind.RtxEncore, "RTX Encore"));
        Assert.False(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Cancelled"), ManagedComponentKind.RtxEncore, "RTX Encore"));

        Assert.Equal(2, asked.Count);
        Assert.StartsWith(UiLocalizationService.Format("Vm.InstallationCancelled", "RTX Encore"), fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // Upstream saves the tick whatever the answer: Cancel with "Don't show this again" still stops this install, and the
    // next install goes ahead without asking. The tick is per mod.
    [Fact]
    public async Task DontShowAgain_IsRememberedPerMod_EvenOnCancel()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var asked = new List<string>();
        fx.Vm.ConfirmDismissibleAsync = notice => { asked.Add(notice.Key); return Task.FromResult(new DismissibleNoticeAnswer(false, true)); };

        Assert.False(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Ticked"), ManagedComponentKind.Dlssg2030Unlock, "20/30 FG Unlock"));
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Ticked"), ManagedComponentKind.Dlssg2030Unlock, "20/30 FG Unlock"));
        Assert.False(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Ticked"), ManagedComponentKind.RtxEncore, "RTX Encore"));

        Assert.Equal(["Dlssg2030ClosedSourceDismissed", "RtxEncoreClosedSourceDismissed"], asked);
        // Kept on disk beside the settings, so a restart remembers it.
        var store = new DismissedNoticeStore(Path.Combine(fx.Settings.ConfigRoot, "dismissed-notices.json"));
        Assert.True(store.IsDismissed("Dlssg2030ClosedSourceDismissed"));
    }

    [Fact]
    public async Task OtherComponents_AreNotAsked()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.Vm.ConfirmDismissibleAsync = _ => throw new InvalidOperationException("must not ask");

        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Other"), ManagedComponentKind.ReShade, "ReShade"));
        Assert.True(await fx.Vm.InstallNotices.ConfirmBeforeInstallAsync(Game("Other"), ManagedComponentKind.MfgAdaUnlock, "MFG Unlock"));
    }

    [Fact]
    public async Task WithoutAWindow_EveryNoticeIsAccepted()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.Vm.ConfirmDismissibleAsync = null;
        Assert.True(await fx.Vm.InstallNotices.ConfirmClosedSourceAsync(ClosedSourceMod.AnrFg));
    }

    // A store that cannot be written (an unreadable file on disk) keeps the notice showing and says why.
    [Fact]
    public async Task ADismissalThatCannotBeSaved_IsReported_AndTheNoticeShowsAgain()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        Directory.CreateDirectory(fx.Settings.ConfigRoot);
        File.WriteAllText(Path.Combine(fx.Settings.ConfigRoot, "dismissed-notices.json"), "{ not json");
        var asked = 0;
        fx.Vm.ConfirmDismissibleAsync = _ => { asked++; return Task.FromResult(new DismissibleNoticeAnswer(true, true)); };

        Assert.True(await fx.Vm.InstallNotices.ConfirmClosedSourceAsync(ClosedSourceMod.RtxEncore));
        Assert.StartsWith(UiLocalizationService.Format("Notice.DismissNotSaved", "").TrimEnd(), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.True(await fx.Vm.InstallNotices.ConfirmClosedSourceAsync(ClosedSourceMod.RtxEncore));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void Store_RoundTripsKeys_IgnoringCase_AndClears()
    {
        var path = Path.Combine(Path.GetTempPath(), "rhi-notices-" + Guid.NewGuid().ToString("N"), "dismissed-notices.json");
        try
        {
            var store = new DismissedNoticeStore(path);
            Assert.False(store.IsDismissed("A"));
            store.Dismiss("A");
            store.Dismiss("a");
            Assert.True(new DismissedNoticeStore(path).IsDismissed("a"));
            store.Clear();
            Assert.False(store.IsDismissed("A"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }
    }

    // The dialog content (upstream's ContentDialog): the paragraphs, the link only when the notice has one, the tick box,
    // Cancel then the accept button (default).
    [Fact]
    public void View_HasTextLinkTickCancelAndInstall()
    {
        string? opened = null;
        var parts = DismissibleNoticeView.Build(InstallNoticePresenter.ClosedSource(ClosedSourceMod.AnrFg), url => opened = url);

        var texts = parts.Root.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Assert.Equal(InstallNoticePresenter.ClosedSource(ClosedSourceMod.AnrFg).Paragraphs, texts);
        Assert.NotNull(parts.Link);
        Assert.Equal("github.com/apex-compute/ANR", parts.Link!.Content);
        parts.Link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://github.com/apex-compute/ANR", opened);
        Assert.Equal(UiLocalizationService.Text("Notice.DontShowAgain"), parts.DontShow.Content);
        Assert.False(parts.DontShow.IsChecked);
        Assert.True(parts.Accept.IsDefault);
        Assert.Equal(UiLocalizationService.Text("Vm.Install"), parts.Accept.Content);
        var buttons = (StackPanel)parts.Root.Children[^1];
        Assert.Equal([parts.Cancel, parts.Accept], buttons.Children.Cast<Control>());

        Assert.Null(DismissibleNoticeView.Build(InstallNoticePresenter.ClosedSource(ClosedSourceMod.RtxEncore), _ => { }).Link);
    }

    [Theory]
    [InlineData(ManagedComponentKind.RtxEncore, "https://github.com/SilyNoMeta/rtx-encore")]
    [InlineData(ManagedComponentKind.Dlssg2030Unlock, "https://github.com/sdli1995/dlssg_for_sm86")]
    [InlineData(ManagedComponentKind.Rtx40MfgStandalone, "https://github.com/dashdogy/MFG-Unlocker")]
    [InlineData(ManagedComponentKind.MfgAdaUnlock, "https://github.com/mavismmg/MFGAdaUnlock-RenoDx")]
    public void RowInfo_OpensTheGitHubPage(ManagedComponentKind kind, string url)
    {
        var row = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = "Mod", Availability = ComponentAvailability.Available, ActionAllowed = true }, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));
        Assert.Equal(url, row.GitHubInfoUrl);
        Assert.True(row.HasInfo);
        Assert.Equal(UiLocalizationService.Format("Notice.OpenGitHubTip", "Mod"), row.InfoHighlight.Tip);

        var unsupported = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = "Mod", Availability = ComponentAvailability.Unsupported }, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));
        Assert.Null(unsupported.GitHubInfoUrl);
        Assert.False(unsupported.HasInfo);
    }

    // The component row's Install goes through the notice first (w14/extras-fg hook in ManageComponentAsync): Cancel
    // installs nothing and downloads nothing.
    [Fact]
    public async Task RowInstall_ShowsTheNoticeFirst_AndCancelChangesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-notice-row-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "Encore Game", "bin");
            var prefix = Path.Combine(root, "Encore Game", "pfx");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(prefix);
            PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
            PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "nvngx_dlss.dll"), 0x8664, 4096);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
            var game = new DetectedGame
            {
                Name = "Encore Game", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
                PrefixPath = prefix, Runner = "Proton", Source = "Test", GraphicsApi = GraphicsApi.DirectX12
            };
            var requests = 0;
            using var fx = await ViewModelFixture.StartAsync([game]);
            var selected = fx.Vm.Games.Single(row => row.Game.Name == game.Name).Game;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == game.Name) done.TrySetResult(); }
            fx.Vm.Session.Completed += OnCompleted;
            try
            {
                fx.Vm.SelectedGame = selected;
                if (!fx.Vm.Session.TryGetCached(selected, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally { fx.Vm.Session.Completed -= OnCompleted; }
            var row = fx.Vm.Session.Analysis!.Components.Single(c => c.Kind == ManagedComponentKind.RtxEncore);
            Assert.True(row.ActionAllowed, row.Note);
            DismissibleNotice? asked = null;
            fx.Vm.ConfirmDismissibleAsync = notice => { asked = notice; requests++; return Task.FromResult(new DismissibleNoticeAnswer(false, false)); };

            await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.RtxEncore);

            Assert.Equal("RtxEncoreClosedSourceDismissed", asked?.Key);
            Assert.Equal(1, requests);
            Assert.Null(fx.Records.Find(selected, ManagedComponentKind.RtxEncore));
            Assert.False(File.Exists(Path.Combine(bin, "version.dll")));
            Assert.StartsWith(UiLocalizationService.Format("Vm.InstallationCancelled", row.DisplayName), fx.Vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
