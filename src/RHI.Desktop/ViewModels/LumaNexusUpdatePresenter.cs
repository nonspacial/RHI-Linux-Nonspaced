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
/// #245 (upstream 2.8.8 <c>20d3297</c>): the Luma row of the selected game when RHI installed a Luma mod the Luma wiki now
/// lists only on Nexus Mods. RHI asks Nexus (connected account's key only) whether a newer file is there and, when it is,
/// shows "Update on Nexus" in the row: it opens the mod's Nexus page through <see cref="LinkHosts.NexusMod"/> and
/// downloads nothing. Without a connected account the same place reads "Open on Nexus", and its ~2 s tooltip names
/// Settings → Accounts &amp; updates → Connect Nexus account (the control that lets RHI check). Hidden when Nexus has no
/// newer file, when Nexus could not be asked, and for every other mod. The answer is worked out off the UI thread
/// (<see cref="NexusModUpdateCheck"/> keeps Nexus's file list for an hour, shared with Update All).
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

    /// <summary>Follows the shell's component rows (once): each new Luma row of an installed mod gets its Nexus answer.</summary>
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
        foreach (var row in rows.Where(row => row.Kind == ManagedComponentKind.LumaFramework
                                              && row.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
        {
            var work = AskAsync(game, row);
            Checking = Checking.IsCompleted ? work : Task.WhenAll(Checking, work);
            UiExceptionGuard.Forget(work, "luma nexus check");
        }
    }

    private async Task AskAsync(DetectedGame game, ComponentRowViewModel row)
    {
        NexusModUpdateAnswer? answer;
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

/// <summary>#245: the Luma row's Nexus button (set by <see cref="LumaNexusUpdatePresenter"/>); hidden until Nexus answered.</summary>
public sealed class LumaNexusRowState : INotifyPropertyChanged
{
    private NexusModUpdateOutcome? _outcome;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Shown only when Nexus has a newer file, or when no Nexus account is connected (then it only opens the page).</summary>
    public bool IsVisible => Url is not null && _outcome is NexusModUpdateOutcome.Newer or NexusModUpdateOutcome.NoKey;

    public string Label => UiLocalizationService.Text(_outcome == NexusModUpdateOutcome.NoKey ? "Lu.OpenOnNexus" : "Lu.UpdateOnNexus");

    public string Tip => UiLocalizationService.Text(_outcome == NexusModUpdateOutcome.NoKey ? "Lu.OpenOnNexusNoKeyTip" : "Lu.UpdateOnNexusTip");

    /// <summary>The mod's Nexus page (the button's command parameter), once it passed <see cref="LinkHosts.NexusMod"/>.</summary>
    public string? Url { get; private set; }

    public ICommand? Command { get; private set; }

    internal void Set(NexusModUpdateAnswer answer, ICommand open)
    {
        _outcome = answer.Outcome;
        Url = GameLinkCatalogue.SafePage(answer.PageUrl, LinkHosts.NexusMod);
        Command = open;
        foreach (var name in new[] { nameof(Url), nameof(Command), nameof(Label), nameof(Tip), nameof(IsVisible) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
