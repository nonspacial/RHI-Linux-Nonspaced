using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #245 (upstream 2.8.8 <c>20d3297</c>): the Luma row of the selected game when the Luma wiki lists the game's Luma mod
/// only on Nexus Mods. The row's own Nexus button (its own column: Install/Remove → Nexus → ⚙ → ⓘ) opens the mod's Nexus
/// page through <see cref="LinkHosts.NexusMod"/> and downloads nothing. When RHI installed the mod, RHI asks Nexus
/// (connected account's key only) whether a newer file is there: "Update on Nexus" when it is; hidden when Nexus has
/// nothing newer or could not be asked. "Open on Nexus" with a ~2 s tooltip naming the control that fixes it when no
/// account is connected (Connect Nexus account) or Nexus refused the saved key (Disconnect, then Connect Nexus account),
/// and — without asking Nexus — when RHI has not installed the mod (download it there, drop the file on RHI). The answer
/// is worked out off the UI thread (<see cref="NexusModUpdateCheck"/> keeps Nexus's file list for an hour, shared with
/// Update All).
/// </summary>
public sealed class LumaNexusUpdatePresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, LumaNexusUpdatePresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;
    private bool _following;

    /// <summary>The presenter of <paramref name="shell"/> (one per shell; the shell's one-line hook).</summary>
    public static LumaNexusUpdatePresenter For(MainWindowViewModel shell, LinuxManagedComponentService managed)
        => Presenters.GetValue(shell, s => new LumaNexusUpdatePresenter(s, managed));

    private LumaNexusUpdatePresenter(MainWindowViewModel shell, LinuxManagedComponentService managed)
    {
        _shell = shell;
        _managed = managed;
        OpenCommand = new RelayCommand<string?>(Open);
    }

    /// <summary>Opens a Nexus page (the row button's command; its parameter is the page).</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Test seam: opens a checked page; answers whether it opened and, if not, why.</summary>
    internal Func<string, (bool Opened, string? Error)> OpenLink { get; set; } = url => (LinuxDesktopOpener.TryOpenUri(url, out var error), error);

    /// <summary>The UI thread the rows are bound to (none in plain view-model tests). Test seam.</summary>
    internal (Func<bool> IsCurrent, Action<Action> Post)? UiThread { get; set; } = Avalonia.Application.Current is null
        ? null
        : (() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

    /// <summary>Every Nexus answer still being worked out (tests await it).</summary>
    public Task Checking { get; private set; } = Task.CompletedTask;

    /// <summary>Follows the shell's component rows (once): each new Luma row gets its Nexus answer.</summary>
    public void FollowRows()
    {
        if (_following) return;
        _following = true;
        _shell.Components.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && e.NewItems is { Count: > 0 } added)
                Track(added.OfType<ComponentRowViewModel>());
        };
        Track(_shell.Components.ToArray());
    }

    private void Track(IEnumerable<ComponentRowViewModel> rows)
    {
        if (_shell.SelectedGame is not { } game) return;
        foreach (var row in rows.Where(row => row.Kind == ManagedComponentKind.LumaFramework))
        {
            var work = AskAsync(game, row);
            Checking = Checking.IsCompleted ? work : Task.WhenAll(Checking, work);
            UiExceptionGuard.Forget(work, "luma nexus check");
        }
    }

    private async Task AskAsync(DetectedGame game, ComponentRowViewModel row)
    {
        LumaNexusPage? answer;
        try
        {
            answer = await Task.Run(() => _managed.AskNexusAboutLumaAsync(game)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("luma-nexus", $"Could not ask Nexus Mods about the Luma mod of {game.Name}", ex);
            return;
        }
        if (answer is null) return;

        void Apply()
        {
            // Only while the same game is selected: a later selection rebuilt the rows.
            if (ReferenceEquals(_shell.SelectedGame, game)) row.LumaNexus.Set(answer, OpenCommand);
        }
        if (UiThread is { } ui && !ui.IsCurrent()) ui.Post(Apply);
        else Apply();
    }

    private void Open(string? url)
    {
        if (GameLinkCatalogue.SafePage(url, LinkHosts.NexusMod) is not { } page) return;
        var (opened, error) = OpenLink(page);
        if (!opened) ((IPanelHost)_shell).StatusText = UiLocalizationService.Format("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }
}

/// <summary>What the Luma row's Nexus button says (#245).</summary>
public enum LumaNexusRowMode
{
    /// <summary>Not shown: not a Nexus-only mod, nothing newer on Nexus, or Nexus could not be asked.</summary>
    Hidden,
    /// <summary>"Update on Nexus": RHI installed it and Nexus has a newer file.</summary>
    Update,
    /// <summary>"Open on Nexus": RHI installed it, no Nexus account is connected.</summary>
    OpenNoKey,
    /// <summary>"Open on Nexus": RHI installed it, Nexus refused the saved key.</summary>
    OpenKeyRefused,
    /// <summary>"Open on Nexus": RHI has not installed it (the page is where to get it).</summary>
    OpenToInstall
}

/// <summary>#245: the Luma row's Nexus button (set by <see cref="LumaNexusUpdatePresenter"/>); hidden until answered.</summary>
public sealed class LumaNexusRowState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public LumaNexusRowMode Mode { get; private set; }

    /// <summary>Shown only with a page that passed <see cref="LinkHosts.NexusMod"/> and something to say (see <see cref="Mode"/>).</summary>
    public bool IsVisible => Url is not null && Mode != LumaNexusRowMode.Hidden;

    public string Label => UiLocalizationService.Text(Mode == LumaNexusRowMode.Update ? "Lu.UpdateOnNexus" : "Lu.OpenOnNexus");

    public string Tip => UiLocalizationService.Text(Mode switch
    {
        LumaNexusRowMode.OpenNoKey => "Lu.OpenOnNexusNoKeyTip",
        LumaNexusRowMode.OpenKeyRefused => "Lu.OpenOnNexusKeyRefusedTip",
        LumaNexusRowMode.OpenToInstall => "Lu.OpenOnNexusInstallTip",
        _ => "Lu.UpdateOnNexusTip"
    });

    internal static LumaNexusRowMode ModeFor(LumaNexusPage page) => page.Installed?.Outcome switch
    {
        null => LumaNexusRowMode.OpenToInstall,
        NexusModUpdateOutcome.Newer => LumaNexusRowMode.Update,
        NexusModUpdateOutcome.NoKey => LumaNexusRowMode.OpenNoKey,
        NexusModUpdateOutcome.KeyRefused => LumaNexusRowMode.OpenKeyRefused,
        _ => LumaNexusRowMode.Hidden
    };

    /// <summary>The mod's Nexus page (the button's command parameter), once it passed <see cref="LinkHosts.NexusMod"/>.</summary>
    public string? Url { get; private set; }

    public ICommand? Command { get; private set; }

    internal void Set(LumaNexusPage page, ICommand open)
    {
        Mode = ModeFor(page);
        Url = GameLinkCatalogue.SafePage(page.PageUrl, LinkHosts.NexusMod);
        Command = open;
        foreach (var name in new[] { nameof(Mode), nameof(Url), nameof(Command), nameof(Label), nameof(Tip), nameof(IsVisible) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
