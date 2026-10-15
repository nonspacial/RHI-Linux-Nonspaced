using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One block of a component's ⓘ dialog: a heading line or a paragraph shown as given.</summary>
public sealed record ComponentInfoBlock(string Text, bool IsHeading = false);

/// <summary>One link button of a component's ⓘ dialog (its tooltip is the link).</summary>
public sealed record ComponentInfoLinkView(string Label, string Url);

/// <summary>What a component's ⓘ dialog shows (#184).</summary>
public sealed record ComponentInfoView(string Title, IReadOnlyList<ComponentInfoBlock> Blocks, IReadOnlyList<ComponentInfoLinkView> Links);

/// <summary>
/// The ⓘ button on a component row (#184; upstream's Info buttons): RenoDX, Luma, OptiScaler, ReLimiter, Display
/// Commander, REFramework and ReShade, on a row that applies to the game (hidden on an unsupported row). It opens a small
/// dialog with that component's notes for the selected game from <see cref="ComponentInfoCatalogue"/>, its links, and
/// RHI's own short description of the component where there is nothing game-specific (upstream's general text).
/// </summary>
public sealed class ComponentInfoPresenter(ComponentInfoCatalogue catalogue)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>Set by the window: shows the dialog.</summary>
    public Func<ComponentInfoView, Task>? Show { get; set; }

    /// <summary>Whether a row gets the ⓘ button: a component upstream has an Info button for, on a row that applies.</summary>
    public static bool HasInfo(ManagedComponentKind kind, ComponentAvailability availability)
        => ComponentInfoCatalogue.HasInfo(kind) && availability != ComponentAvailability.Unsupported;

    /// <summary>The ⓘ button's tooltip.</summary>
    public static string TipFor(string componentName) => TF("Cat.InfoTip", componentName);

    /// <summary>Opens the ⓘ dialog of a row for the selected game. UI thread.</summary>
    public async Task OpenAsync(DetectedGame? game, ManagedComponentKind kind, string componentName, string? installedVersion)
    {
        if (game is null || Show is null || !ComponentInfoCatalogue.HasInfo(kind)) return;
        var info = await catalogue.ResolveAsync(game, kind, installedVersion);
        await Show(Compose(info, game.Name, componentName));
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
        ComponentInfoLinkKind.NexusMods => T("Cat.InfoNexusMods"),
        ComponentInfoLinkKind.Discord => T("Cat.InfoDiscord"),
        _ => T("Cat.InfoReleases")
    };

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
