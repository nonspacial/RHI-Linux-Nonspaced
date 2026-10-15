using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One block of a component's ⓘ dialog: a heading line or a paragraph shown as given.</summary>
public sealed record ComponentInfoBlock(string Text, bool IsHeading = false);

/// <summary>One link button of a component's ⓘ dialog (its tooltip is the link).</summary>
public sealed record ComponentInfoLinkView(string Label, string Url);

/// <summary>What a component's ⓘ dialog shows (#184).</summary>
public sealed record ComponentInfoView(string Title, IReadOnlyList<ComponentInfoBlock> Blocks, IReadOnlyList<ComponentInfoLinkView> Links);

/// <summary>An open ⓘ dialog (set up by the window): its content can be replaced, it can be brought forward, and it says when it closed.</summary>
public interface IComponentInfoDialog
{
    /// <summary>Replaces what the dialog shows (the loading line, then the notes).</summary>
    void Show(ComponentInfoView view);

    /// <summary>Brings the dialog to the front.</summary>
    void Activate();

    /// <summary>Completes when the user closed the dialog.</summary>
    Task Closed { get; }
}

/// <summary>
/// The ⓘ button on a component row (#184; upstream's Info buttons): RenoDX, Luma, OptiScaler, ReLimiter, Display
/// Commander, REFramework and ReShade, on a row that applies to the game (hidden on an unsupported row). It opens a small
/// dialog with that component's notes for the selected game from <see cref="ComponentInfoCatalogue"/>, its links, and
/// RHI's own short description of the component where there is nothing game-specific (upstream's general text).
/// The dialog opens at once with a loading line and fills in when the sources answer; while it is open (loading or not) a
/// further ⓘ click brings it forward instead of opening another.
/// </summary>
public sealed class ComponentInfoPresenter(ComponentInfoCatalogue catalogue)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>Set by the window: opens a dialog showing the given content and returns it (it stays open until the user closes it).</summary>
    public Func<ComponentInfoView, IComponentInfoDialog>? Open { get; set; }

    private IComponentInfoDialog? _open;

    /// <summary>True while an ⓘ dialog is open (loading or showing notes).</summary>
    public bool IsOpen => _open is not null;

    /// <summary>Whether a row gets the ⓘ button: a component upstream has an Info button for, on a row that applies.</summary>
    public static bool HasInfo(ManagedComponentKind kind, ComponentAvailability availability)
        => ComponentInfoCatalogue.HasInfo(kind) && availability != ComponentAvailability.Unsupported;

    /// <summary>Full Refresh: the ⓘ dialogs' lists and texts are fetched again (never awaited).</summary>
    public Task RefreshListsAsync() => catalogue.RefreshAsync();

    /// <summary>The ⓘ button's tooltip.</summary>
    public static string TipFor(string componentName) => TF("Cat.InfoTip", componentName);

    /// <summary>
    /// Opens the ⓘ dialog of a row for the selected game: at once with a loading line, then with the notes once the sources
    /// answered (each within its time limit). While a dialog is open a further click brings it forward. Completes when the
    /// dialog is closed. UI thread.
    /// </summary>
    public async Task OpenAsync(DetectedGame? game, ManagedComponentKind kind, string componentName, string? installedVersion)
    {
        if (game is null || Open is null || !ComponentInfoCatalogue.HasInfo(kind)) return;
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        var title = TF("Cat.InfoTitle", componentName, game.Name);
        var dialog = Open(new ComponentInfoView(title, [new(T("Cat.InfoLoading"))], []));
        _open = dialog;
        try
        {
            var closing = new CancellationTokenSource();
            var resolve = catalogue.ResolveAsync(game, kind, installedVersion, closing.Token);
            if (await Task.WhenAny(resolve, dialog.Closed) == resolve)
            {
                dialog.Show(await ComposeOrGeneralAsync(resolve, game.Name, componentName, kind, title));
            }
            else
            {
                // Closed while loading: stop waiting for the sources.
                closing.Cancel();
                _ = resolve.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            await dialog.Closed;
        }
        finally
        {
            if (ReferenceEquals(_open, dialog)) _open = null;
        }
    }

    private static async Task<ComponentInfoView> ComposeOrGeneralAsync(Task<ComponentInfo> resolve, string gameName, string componentName,
        ManagedComponentKind kind, string title)
    {
        try
        {
            return Compose(await resolve, gameName, componentName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The catalogue never fails for a remote source; anything else still leaves the general text, never a loading line.
            AppLog.Warn("component-info", $"ⓘ content for {componentName} could not be worked out", ex);
            return new ComponentInfoView(title, [new(GeneralText(kind))], []);
        }
    }

    /// <summary>The dialog's localised text for resolved content: native-HDR note, compatibility rows, texts, change log, general text.</summary>
    public static ComponentInfoView Compose(ComponentInfo info, string gameName, string componentName)
    {
        var blocks = new List<ComponentInfoBlock>();
        if (info.NativeHdr) blocks.Add(new(T("Cat.InfoNativeHdr")));
        AddCompatibility(blocks, info.Compatibility, "Cat.InfoOptiScalerCompat");
        AddCompatibility(blocks, info.Fsr4Compatibility, "Cat.InfoFsr4Compat");
        foreach (var text in info.Texts) blocks.Add(new(text));
        if (info.Changelog is { } changelog)
        {
            blocks.Add(new(T("Cat.InfoChangelog"), IsHeading: true));
            blocks.Add(new(changelog));
        }
        if (info.GeneralText) blocks.Add(new(GeneralText(info.Kind)));
        var links = info.Links.Select(link => new ComponentInfoLinkView(LinkLabel(link), link.Url)).ToArray();
        return new ComponentInfoView(TF("Cat.InfoTitle", componentName, gameName), blocks, links);
    }

    private static void AddCompatibility(List<ComponentInfoBlock> blocks, OptiScalerCompatEntry? entry, string headingKey)
    {
        if (entry is null) return;
        blocks.Add(new(TF(headingKey, StatusText(entry.Status)), IsHeading: true));
        blocks.Add(new(TF("Cat.InfoUpscalers", entry.Upscalers.Count > 0 ? string.Join(", ", entry.Upscalers) : T("Cat.InfoUpscalersNone"))));
        if (!string.IsNullOrWhiteSpace(entry.Notes)) blocks.Add(new(entry.Notes!));
    }

    private static string StatusText(string status) => status switch
    {
        OptiScalerWikiCatalogue.Working => T("Cat.InfoStatusWorking"),
        OptiScalerWikiCatalogue.NotWorking => T("Cat.InfoStatusNotWorking"),
        OptiScalerWikiCatalogue.Partial => T("Cat.InfoStatusPartial"),
        _ => T("Cat.InfoStatusUnknown")
    };

    private static string LinkLabel(ComponentInfoLink link) => link.Kind switch
    {
        ComponentInfoLinkKind.Notes => link.Label ?? T("Card.OpenNotesLink"),
        ComponentInfoLinkKind.WikiPage => T("Cat.InfoWikiPage"),
        ComponentInfoLinkKind.HdrDatabase => T("Cat.InfoHdrDatabase"),
        ComponentInfoLinkKind.ModPage => T(IsGameBanana(link.Url) ? "Cat.InfoGameBanana" : "Cat.InfoNexusMods"),
        ComponentInfoLinkKind.Discord => T("Cat.InfoDiscord"),
        _ => T("Cat.InfoReleases")
    };

    /// <summary>The database's <c>nexusUrl</c> also holds GameBanana pages: the button names the site it really opens.</summary>
    private static bool IsGameBanana(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.EndsWith("gamebanana.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Upstream's general text per component (<c>AddonInfoResolver.Fallback*</c>), localised.</summary>
    public static string GeneralText(ManagedComponentKind kind) => T(kind switch
    {
        ManagedComponentKind.RenoDX => "Cat.InfoAboutRenoDx",
        ManagedComponentKind.LumaFramework => "Cat.InfoAboutLuma",
        ManagedComponentKind.OptiScaler => "Cat.InfoAboutOptiScaler",
        ManagedComponentKind.ReLimiter => "Cat.InfoAboutReLimiter",
        ManagedComponentKind.DisplayCommander => "Cat.InfoAboutDisplayCommander",
        ManagedComponentKind.REFramework => "Cat.InfoAboutReFramework",
        _ => "Cat.InfoAboutReShade"
    });
}
