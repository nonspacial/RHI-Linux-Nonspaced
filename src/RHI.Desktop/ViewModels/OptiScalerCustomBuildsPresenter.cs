using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>What the custom-build card asks the user (the view answers; tests answer with fakes).</summary>
public interface IOptiScalerCustomBuildPrompts
{
    /// <summary>A name for a build (null = cancelled).</summary>
    Task<string?> AskNameAsync(string title, string suggested);

    /// <summary>A question with Cancel and <paramref name="accept"/>; true when accepted.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept);

    /// <summary>A zip, 7z, rar or DLL (null = cancelled).</summary>
    Task<string?> PickFileAsync();

    /// <summary>A folder (null = cancelled).</summary>
    Task<string?> PickFolderAsync();
}

/// <summary>One custom build as the card shows it: name, facts line ("1.0.2 · OptiScaler 10 layout · added …"), tooltip.</summary>
public sealed record OptiScalerCustomBuildRow(OptiScalerCustomBuild Build, string Name, string Facts, string Tip, bool NeedsNrDll, int UsedBy);

/// <summary>
/// #271 Settings › Components › Custom OptiScaler builds (upstream 2.9.2 <c>SettingsHandler.OsCustomBuilds</c>): add a build
/// from a link, a file, a folder or a drop; Replace (link, file or folder) offering to redeploy the games that use it; Rename;
/// Remove; the DLSS NR DLL tick. The builds live in <see cref="OptiScalerCustomBuildStore"/>; installs and redeploys go
/// through <see cref="LinuxManagedComponentService"/>. The view only renders <see cref="Rows"/> and forwards clicks.
/// </summary>
public sealed partial class OptiScalerCustomBuildsPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, OptiScalerCustomBuildsPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;

    public static OptiScalerCustomBuildsPresenter For(MainWindowViewModel shell, LinuxManagedComponentService managed)
        => Presenters.GetValue(shell, s => new OptiScalerCustomBuildsPresenter(s, managed));

    private OptiScalerCustomBuildsPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed)
    {
        _shell = shell;
        _managed = managed;
    }

    /// <summary>The builds (the process's store under RHI's data folder; a test's own store).</summary>
    internal OptiScalerCustomBuildStore Store { get; set; } = OptiScalerCustomBuilds.Store;

    /// <summary>The download client for links (the app's; null = links cannot be added, e.g. a test without one).</summary>
    internal HttpClient? Http { get; set; }

    internal IOptiScalerArchiveExtractor Extractor { get; set; } = new OptiScalerArchiveExtractor();

    /// <summary>Set by the view: name prompts, confirmations and pickers.</summary>
    public IOptiScalerCustomBuildPrompts? Prompts { get; set; }

    [ObservableProperty] private string linkText = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool statusIsError;
    [ObservableProperty] private bool isBusy;

    /// <summary>Raised when <see cref="Rows"/> changed (a build added, replaced, renamed, ticked or removed).</summary>
    public event Action? RowsChanged;

    /// <summary>The builds by name, with how many library games have each installed.</summary>
    public IReadOnlyList<OptiScalerCustomBuildRow> Rows
        => Store.All.Select(build =>
        {
            var used = GamesUsing(build.Variant).Count;
            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(build.DllVersion)) facts.Add(build.DllVersion!);
            facts.Add(T(build.IsOs10 ? "Osc.Os10Layout" : "Osc.FlatLayout"));
            facts.Add(UiLocalizationService.Format("Osc.Added", build.AddedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
            if (used > 0) facts.Add(used == 1 ? T("Osc.UsedByOne") : UiLocalizationService.Format("Osc.UsedByMany", used));
            var tip = UiLocalizationService.Format("Osc.Origin", build.Origin, build.Directory)
                      + (build.LooksLikeOptiScaler ? "" : "\n" + T("Osc.NotOptiScalerTip"));
            return new OptiScalerCustomBuildRow(build, build.Name, string.Join(" · ", facts), tip, build.NeedsNrDll, used);
        }).ToArray();

    /// <summary>Library games whose RHI-installed OptiScaler is <paramref name="build"/>.</summary>
    internal IReadOnlyList<DetectedGame> GamesUsing(OptiScalerVariant build)
        => _managed.GamesWithOptiScalerVersion(_shell.AllLibraryGames, build);

    /// <summary>"Add link": the link box's text.</summary>
    public async Task<bool> AddLinkAsync()
    {
        var link = LinkText.Trim();
        if (link.Length == 0) return Say(T("Osc.PasteLinkFirst"), error: true);
        if (!OptiScalerCustomBuildStore.IsLink(link, out _)) return Say(T("Osc.NotALink"), error: true);
        if (!await AddAsync(link, replace: null)) return false;
        LinkText = "";
        return true;
    }

    /// <summary>"Add file" / "Add folder", and a drop on the card (each dropped path in turn).</summary>
    public async Task<bool> AddPathAsync(string path) => await AddAsync(path, replace: null);

    public async Task<bool> AddFileAsync()
        => Prompts is { } prompts && await prompts.PickFileAsync() is { } file && await AddAsync(file, replace: null);

    public async Task<bool> AddFolderAsync()
        => Prompts is { } prompts && await prompts.PickFolderAsync() is { } folder && await AddAsync(folder, replace: null);

    /// <summary>Replace › From the link above.</summary>
    public async Task<bool> ReplaceFromLinkAsync(OptiScalerCustomBuild build)
    {
        var link = LinkText.Trim();
        if (!OptiScalerCustomBuildStore.IsLink(link, out _)) return Say(T("Osc.PasteNewLinkFirst"), error: true);
        if (!await AddAsync(link, build)) return false;
        LinkText = "";
        return true;
    }

    public async Task<bool> ReplaceFromFileAsync(OptiScalerCustomBuild build)
        => Prompts is { } prompts && await prompts.PickFileAsync() is { } file && await AddAsync(file, build);

    public async Task<bool> ReplaceFromFolderAsync(OptiScalerCustomBuild build)
        => Prompts is { } prompts && await prompts.PickFolderAsync() is { } folder && await AddAsync(folder, build);

    public async Task RenameAsync(OptiScalerCustomBuild build)
    {
        if (Prompts is not { } prompts) return;
        var name = await prompts.AskNameAsync(T("Osc.RenameTitle"), build.Name);
        if (name is null || name.Trim() == build.Name) return;
        if (!Store.Rename(build.Variant, name)) Say(T("Osc.CouldNotRename"), error: true);
        RowsChanged?.Invoke();
    }

    public void SetNeedsNrDll(OptiScalerCustomBuild build, bool on)
    {
        // w14 final review m6: an install reading the build's files (and this tick) runs behind the operation gate too.
        using var op = BeginBuildChange();
        if (op is null) return;
        if (!Store.SetNeedsNrDll(build.Variant, on)) Say(T("Osc.CouldNotSave"), error: true);
        RowsChanged?.Invoke();
    }

    public async Task RemoveAsync(OptiScalerCustomBuild build)
    {
        var games = GamesUsing(build.Variant);
        var message = games.Count == 0 ? T("Osc.RemoveUnused") : UiLocalizationService.Format("Osc.RemoveUsed", games.Count);
        if (Prompts is { } prompts && !await prompts.ConfirmAsync(UiLocalizationService.Format("Osc.RemoveTitle", build.Name), message, T("Vm.Remove")))
            return;
        using var op = BeginBuildChange(); // m6: never while an install copies from the build
        if (op is null) return;
        if (!Store.Remove(build.Variant)) Say(T("Osc.CouldNotRemove"), error: true);
        else Say(UiLocalizationService.Format("Osc.RemovedDone", build.Name), error: false);
        RowsChanged?.Invoke();
    }

    /// <summary>Adds (or, with <paramref name="replace"/>, replaces) a build; true when it worked (upstream <c>AddOsCustomBuildAsync</c>).</summary>
    private async Task<bool> AddAsync(string source, OptiScalerCustomBuild? replace)
    {
        if (IsBusy) return Say(T("Osc.Busy"), error: false);
        string name;
        if (replace is not null) name = replace.Name;
        else
        {
            var suggested = OptiScalerCustomBuildStore.SuggestName(source);
            var asked = Prompts is { } prompts ? await prompts.AskNameAsync(T("Osc.NameTitle"), suggested) : suggested;
            if (asked is null) return false;
            name = asked;
        }

        // w14 final review m6: a Replace swaps the build's folder that an install copies from, so it (and the redeploy it
        // offers) runs behind the app's operation gate; adding a new build touches nothing an install reads.
        using var op = replace is null ? null : BeginBuildChange();
        if (replace is not null && op is null) return false;
        IsBusy = true;
        try
        {
            Say(UiLocalizationService.Format(replace is null ? "Osc.Adding" : "Osc.Replacing", name), error: false);
            var progress = new UntilDone(line => Say(UiLocalizationService.Present(line), error: false));
            var http = Http ?? new HttpClient(new NoNetworkHandler());
            OptiScalerCustomImportResult result;
            try { result = await Task.Run(() => Store.ImportAsync(source, name, replace?.Variant, http, Extractor, progress)).ConfigureAwait(true); }
            finally { progress.Done = true; } // a progress line posted late never replaces the result
            if (result.Build is not { } build)
                return Say(UiLocalizationService.Present(result.Error ?? OptiScalerCustomBuildStore.CouldNotAddPrefix), error: true);
            var done = UiLocalizationService.Format(replace is null ? "Osc.AddedDone" : "Osc.ReplacedDone", build.Name);
            Say(result.Note is { } note ? done + " " + UiLocalizationService.Present(note) : done, error: result.Note is not null);
            RowsChanged?.Invoke();
            if (replace is not null) await OfferRedeployAsync(build).ConfigureAwait(true);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>After a Replace: offers to put the new files into the games that have the build (upstream <c>OfferOsCustomRedeployAsync</c>).</summary>
    private async Task OfferRedeployAsync(OptiScalerCustomBuild build)
    {
        var games = GamesUsing(build.Variant);
        if (games.Count == 0) return;
        var message = games.Count == 1
            ? UiLocalizationService.Format("Osc.RedeployOne", games[0].Name, build.Name)
            : UiLocalizationService.Format("Osc.RedeployMany", games.Count, build.Name);
        if (Prompts is not { } prompts || !await prompts.ConfirmAsync(T("Osc.RedeployTitle"), message, T("Vm.Update")).ConfigureAwait(true))
            return;
        var ok = 0;
        string? lastFailure = null;
        foreach (var game in games)
        {
            Say(UiLocalizationService.Format("Osc.Updating", game.Name), error: false);
            var result = await _managed.RedeployOptiScalerCustomBuildAsync(game, build.Variant).ConfigureAwait(true);
            if (result.Success) ok++;
            else lastFailure = $"{game.Name}: {UiLocalizationService.Present(result.Message)}";
        }
        Say(ok == games.Count
            ? UiLocalizationService.Format("Osc.UpdatedAll", ok)
            : UiLocalizationService.Format("Osc.UpdatedSome", ok, games.Count, lastFailure), error: ok != games.Count);
        RowsChanged?.Invoke();
    }

    /// <summary>The app's operation gate for a change to a build's files; null (said on the card) while another operation runs.</summary>
    private OperationScope? BeginBuildChange()
    {
        var op = ((IPanelHost)_shell).BeginOperation("component", cancellable: false);
        if (op is null) Say(T("Vm.AnotherRHIOperationAlreadyRunning"), error: true);
        return op;
    }

    private bool Say(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
        return false;
    }

    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>Progress shown on the caller's context until <see cref="Done"/>; later reports are dropped.</summary>
    private sealed class UntilDone(Action<string> show) : IProgress<string>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        public volatile bool Done;

        public void Report(string value)
        {
            if (Done) return;
            if (_context is null) show(value);
            else _context.Post(_ => { if (!Done) show(value); }, null);
        }
    }

    /// <summary>No app download client (a test or tool without one): a link fails as offline.</summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("No network client.");
    }
}
