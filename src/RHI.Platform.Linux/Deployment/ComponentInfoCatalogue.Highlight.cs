using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Why a component's ⓘ button is highlighted (#210), or <see cref="None"/> when it shows general text only.</summary>
public enum ComponentInfoHighlight
{
    /// <summary>Only RHI's general description of the component: the button keeps its plain look.</summary>
    None,
    /// <summary>The manifest's note for this game, or the game's wiki/database entry (upstream's Manifest/Wiki tiers).</summary>
    GameNotes,
    /// <summary>ReLimiter / Display Commander without a manifest note: their change log (upstream always highlights them).</summary>
    Changelog
}

public sealed partial class ComponentInfoCatalogue
{
    /// <summary>
    /// #210 (upstream <c>HasRealInfoContent</c>): whether the component's ⓘ dialog has content for this game. Reads only the
    /// shared lists the dialog reads (waiting for a list still loading, each within its time limit), never a per-game
    /// request; never throws for a remote source. A component without an ⓘ dialog gives <see cref="ComponentInfoHighlight.None"/>.
    /// </summary>
    public async Task<ComponentInfoHighlight> HighlightAsync(DetectedGame game, ManagedComponentKind kind, CancellationToken cancellationToken = default)
    {
        if (!HasInfo(kind)) return ComponentInfoHighlight.None;
        var info = await ResolveCoreAsync(game, kind, installedVersion: null, perGameFetches: false, cancellationToken).ConfigureAwait(false);
        if (info.Source == ComponentInfoSource.Manifest) return ComponentInfoHighlight.GameNotes;
        if (kind is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander) return ComponentInfoHighlight.Changelog;
        return info.Source == ComponentInfoSource.Wiki ? ComponentInfoHighlight.GameNotes : ComponentInfoHighlight.None;
    }
}
