using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    // w3/dllnames (#120): the per-game ReShade DLL name.
    /// <summary>The store of per-game proxy DLL names chosen before install.</summary>
    public DllNamePreferenceStore ReShadeDllNames => _reshade.DllNames;

    /// <summary>
    /// The names OptiScaler's DLL-name dropdown offers on <paramref name="game"/> (#172): OptiScaler's names except those a
    /// file of another RHI component sits under (RHI's own ReShade moves aside for OptiScaler, so its name stays offered).
    /// </summary>
    public IReadOnlyList<string> OptiScalerProxyNamesFor(DetectedGame game)
    {
        var held = OptiScalerNamesHeld(game);
        return OptiScalerDeploymentService.ProxyNames.Where(name => !held.ContainsKey(name)).ToArray();
    }

    /// <summary>
    /// The name "Automatic" stands for on <paramref name="game"/> (#172): <see cref="OptiScalerDeploymentService.DefaultProxyName"/>,
    /// or the first of OptiScaler's names no other RHI component holds when that one is held (dxgi.dll again when every
    /// name is held; the install then refuses with the holder's name).
    /// </summary>
    public string OptiScalerAutomaticProxyName(DetectedGame game)
        => OptiScalerProxyNamesFor(game).FirstOrDefault() ?? OptiScalerDeploymentService.DefaultProxyName;

    /// <summary>
    /// The name OptiScaler is (or will be) installed under on <paramref name="game"/> (#172): the recorded one while it is
    /// installed, else the saved choice while it is still offered, else <see cref="OptiScalerAutomaticProxyName"/>.
    /// </summary>
    public string OptiScalerProxyNameFor(DetectedGame game)
    {
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is { } record) return OptiScalerDeploymentService.GetProxyName(record);
        // No ReShade service (a test wiring OptiScaler alone) = no saved choice.
        return _reshade?.DllNames.GetOptiScaler(game) is { } chosen && OptiScalerProxyNamesFor(game).Contains(chosen, StringComparer.OrdinalIgnoreCase)
            ? chosen
            : OptiScalerAutomaticProxyName(game);
    }

    /// <summary>
    /// The saved OptiScaler name the next install skips because another RHI component holds it, with that component's
    /// name ("winmm.dll (Ultimate ASI Loader)"); null when OptiScaler is installed, nothing is saved or the name is free.
    /// </summary>
    public string? OptiScalerSkippedChoice(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is null
           && _reshade?.DllNames.GetOptiScaler(game) is { } chosen
           && OptiScalerNamesHeld(game).TryGetValue(chosen, out var holder)
            ? $"{chosen} ({DisplayDependencyName(holder)})"
            : null;

    /// <summary>Appended to an OptiScaler install that skipped the saved DLL name (#172); saved name and component follow.</summary>
    public const string OptiScalerSavedNameSkippedPrefix =
        " The DLL name saved for OptiScaler is used by another component RHI installed on this game, so OptiScaler was installed under the automatic name instead. Saved name and component: ";

    // OptiScaler's names that a file of another RHI component sits under in the game folder, with that component.
    private Dictionary<string, ManagedComponentKind> OptiScalerNamesHeld(DetectedGame game)
    {
        var held = new Dictionary<string, ManagedComponentKind>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory)) return held;
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.BinaryDirectory));
        foreach (var record in _records.FindAll(game).Where(record => record.Component is not (ManagedComponentKind.OptiScaler or ManagedComponentKind.ReShade)))
            foreach (var file in record.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)))
            {
                var full = Path.GetFullPath(file.Path);
                if (string.Equals(Path.GetDirectoryName(full), directory, StringComparison.Ordinal)
                    && OptiScalerDeploymentService.ProxyNames.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                    held.TryAdd(Path.GetFileName(full), record.Component);
            }
        return held;
    }

    // Every OptiScaler install path (#172): the name OptiScalerProxyNameFor decides, and a plain sentence when the saved
    // choice was skipped because another RHI component holds it.
    private async Task<ComponentOperationResult> InstallOptiScalerUnderChosenNameAsync(
        DetectedGame game, OptiScalerVariant variant, string? nightlyBuild, CancellationToken cancellationToken)
    {
        var skipped = OptiScalerSkippedChoice(game);
        var result = await _optiscaler.InstallAsync(game, variant, nightlyBuild, OptiScalerProxyNameFor(game), cancellationToken).ConfigureAwait(false);
        return result.Success && skipped is not null
            ? result with { Message = result.Message + OptiScalerSavedNameSkippedPrefix + skipped }
            : result;
    }

    /// <summary>The name the next ReShade install on <paramref name="game"/> uses, and why; null when its API has no proxy.</summary>
    public ProxyNameResolution? ResolveReShadeProxyName(DetectedGame game) => _reshade.ResolveProxyName(game);

    /// <summary>The file name RHI's ReShade is installed under on <paramref name="game"/>, or null when RHI has not installed it.</summary>
    public string? InstalledReShadeProxyName(DetectedGame game)
        => ProxyPayloadLocator.ReShadePayload(_records.Find(game, ManagedComponentKind.ReShade)) is { } payload ? Path.GetFileName(payload.Path) : null;

    // w7/reshade (#196): in-place DLL rename and Reset DLL names (upstream DllOverrideService, Reset DLL Names).
    /// <summary>The name ReShade's "Automatic" stands for on <paramref name="game"/> (the saved choice left out), or null.</summary>
    public string? ReShadeAutomaticProxyName(DetectedGame game) => _reshade.AutomaticProxyName(game);

    /// <summary>
    /// What the ReShade DLL-name dropdown offers while RHI's ReShade is installed (#196): the offered names it can be
    /// renamed to now, or the refusal that keeps its name.
    /// </summary>
    public (IReadOnlyList<string> Targets, string? Refusal) ReShadeRenameOptions(DetectedGame game)
        => _reshade.RenameOptions(game, ProxyDllNames.OfferedFor(GameArchitecture.Of(game) == PeArchitecture.X86));

    /// <summary>What OptiScaler's DLL-name dropdown offers while RHI's OptiScaler is installed (#196).</summary>
    public (IReadOnlyList<string> Targets, string? Refusal) OptiScalerRenameOptions(DetectedGame game)
        => _optiscaler.RenameOptions(game, OptiScalerProxyNamesFor(game));

    /// <summary>
    /// Renames RHI's installed ReShade on <paramref name="game"/> to <paramref name="name"/> (null = Automatic) in place, in
    /// one journaled operation with its Wine override, record and saved choice (#196).
    /// </summary>
    public async Task<ComponentOperationResult> RenameReShadeProxyAsync(DetectedGame game, string? name, CancellationToken cancellationToken = default)
        => InstallGuard(game) ?? await _reshade.RenameProxyAsync(game, name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Renames RHI's installed OptiScaler on <paramref name="game"/> to <paramref name="name"/> (null = Automatic,
    /// <see cref="OptiScalerAutomaticProxyName"/>) in place (#196); the same name only saves the choice.
    /// </summary>
    public async Task<ComponentOperationResult> RenameOptiScalerProxyAsync(DetectedGame game, string? name, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        if (_optiscaler.RenameBlocker(game) is { } blocked) return ComponentOperationResult.Fail(blocked);
        var installed = OptiScalerDeploymentService.GetProxyName(_records.Find(game, ManagedComponentKind.OptiScaler)!);
        var target = name ?? OptiScalerAutomaticProxyName(game);
        var choice = name is null ? null : target;
        try
        {
            if (target.Equals(installed, StringComparison.OrdinalIgnoreCase))
            {
                _reshade?.DllNames.SetOptiScaler(game, choice);
                return ComponentOperationResult.Ok(ProxyRename.NameKeptPrefix + installed);
            }
            var staged = _reshade?.DllNames.StageChoices(game, choices => choices.OptiScaler = choice);
            return await _optiscaler.RenameProxyAsync(game, target, staged, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ComponentOperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Whether "Reset DLL names" has anything to do on <paramref name="game"/> (#196): a saved ReShade or OptiScaler name,
    /// or RHI's ReShade / OptiScaler installed under another name than its automatic one that Reset can rename back. A name
    /// another component decides (the OptiScaler chain's ReShade64.dll, ShortFuse, the DirectX 9 translation, an XNA GAC
    /// install; review 1) is not Reset's to change, so it does not count. Reads the ledger and the choice file only.
    /// </summary>
    public bool HasDllNamesToReset(DetectedGame game)
        => _reshade?.DllNames.HasReShadeOrOptiScalerChoice(game) == true
           || ReShadeNameToReset(game) || OptiScalerNameToReset(game);

    // RHI's ReShade sits under another name than its automatic one and nothing else decides its name (review 1).
    private bool ReShadeNameToReset(DetectedGame game)
        => _reshade is { } reShade && InstalledReShadeProxyName(game) is { } installed && reShade.AutomaticProxyName(game) is { } automatic
           && !installed.Equals(automatic, StringComparison.OrdinalIgnoreCase)
           && reShade.RenameBlocker(game) is null;

    // RHI's OptiScaler sits under another name than its automatic one and may be renamed in place (review 1).
    private bool OptiScalerNameToReset(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is { } optiScaler
           && !OptiScalerDeploymentService.GetProxyName(optiScaler).Equals(OptiScalerAutomaticProxyName(game), StringComparison.OrdinalIgnoreCase)
           && _optiscaler.RenameBlocker(game) is null;

    /// <summary>
    /// "Reset DLL names" (#196, upstream): every ReShade / OptiScaler DLL-name choice of <paramref name="game"/> goes back to
    /// Automatic, and an installed one under another name is renamed back to its automatic name in place (OptiScaler first,
    /// as upstream; each its own journaled rename). A rename that is refused keeps that component's name and choice; the
    /// result says so, the other one still goes back.
    /// </summary>
    public async Task<ComponentOperationResult> ResetDllNamesAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        var renamed = new List<string>();
        var refused = new List<string>();
        async Task RenameAsync(Func<Task<ComponentOperationResult>> rename)
        {
            var result = await rename().ConfigureAwait(false);
            (result.Success ? renamed : refused).Add(result.Message);
        }
        try
        {
            // A name another component decides stays; only the saved choice goes back to automatic (review 1).
            if (OptiScalerNameToReset(game))
                await RenameAsync(() => RenameOptiScalerProxyAsync(game, null, cancellationToken)).ConfigureAwait(false);
            else _reshade?.DllNames.SetOptiScaler(game, null);
            if (_reshade is { } reShadeService)
            {
                if (ReShadeNameToReset(game))
                    await RenameAsync(() => reShadeService.RenameProxyAsync(game, null, cancellationToken)).ConfigureAwait(false);
                else reShadeService.DllNames.SetReShade(game, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refused.Add(ex.Message);
        }
        return refused.Count > 0
            ? ComponentOperationResult.Fail(string.Join(" ", refused.Concat(renamed)))
            : ComponentOperationResult.Ok(string.Join(" ", renamed.Prepend(DllNamesResetMessage)));
    }

    /// <summary>The line of a Reset DLL names that put everything back (PT-BR presentation pair); rename lines follow.</summary>
    public const string DllNamesResetMessage = "Every DLL name of this game is back to automatic.";
}
