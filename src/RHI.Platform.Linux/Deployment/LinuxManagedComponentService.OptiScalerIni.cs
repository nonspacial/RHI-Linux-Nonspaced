using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #272 (upstream 2.9.3): OptiScaler ⚙ All Settings and the saved-INI library. Applied edits and the saved INI a game uses
/// are written again after OptiScaler is installed there again (a reinstall or a version switch), and forgotten when the
/// user removes OptiScaler, as upstream.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>Per-game applied edits and saved-INI choice (the app's file; tests set their own).</summary>
    public OptiScalerIniChoicesStore OptiScalerIniChoices { get; set; } = new();

    /// <summary>The user's saved OptiScaler.ini files.</summary>
    public OptiScalerIniLibrary OptiScalerIniLibrary { get; set; } = new();

    /// <summary>The note added to an install's message when saved choices were written again (the count follows).</summary>
    public const string IniChoicesReappliedPrefix = " Your OptiScaler.ini choices were written again: ";
    public const string SaveIniNameRequiredMessage = "Type a name first.";
    public const string SaveIniNoIniMessage = "This game has no OptiScaler.ini to save.";
    /// <summary>The name follows.</summary>
    public const string SaveIniSavedPrefix = "Saved this game's OptiScaler.ini as ";

    public IReadOnlyList<OptiScalerIniEntry>? GetOptiScalerIniEntries(DetectedGame game) => _optiscaler.GetIniEntries(game);

    public OptiScalerIniChoices GetOptiScalerIniChoices(DetectedGame game) => OptiScalerIniChoices.Get(game);

    /// <summary>The folder Apply, Load saved ini and a version switch back the game's OptiScaler.ini up to.</summary>
    public string OptiScalerIniBackupFolder(DetectedGame game) => _optiscaler.IniBackupFolder(game);

    /// <summary>All Settings › Apply: the changes are written (the old file backed up) and kept for this game.</summary>
    public async Task<ComponentOperationResult> ApplyOptiScalerIniEditsAsync(DetectedGame game, IReadOnlyDictionary<string, string> edits, CancellationToken cancellationToken = default)
    {
        if (PatchTargetGuard(game) is { } guard) return guard;
        var values = edits.Select(edit => OptiScalerIniCatalog.SplitId(edit.Key) is var (section, key) ? (section, key, edit.Value) : default).ToArray();
        var result = await _optiscaler.ApplyIniValuesAsync(game, values, backup: true, cancellationToken).ConfigureAwait(false);
        if (result.Success) OptiScalerIniChoices.AddEdits(game, edits);
        return result;
    }

    /// <summary>All Settings › Forget saved edits: OptiScaler.ini stays as it is; a reinstall no longer writes them.</summary>
    public void ForgetOptiScalerIniEdits(DetectedGame game) => OptiScalerIniChoices.ForgetEdits(game);

    /// <summary>"Save ini…": the game's OptiScaler.ini goes into the library under <paramref name="name"/>, and the game uses that name.</summary>
    public ComponentOperationResult SaveOptiScalerIni(DetectedGame game, string? name)
    {
        if (OptiScalerIniLibrary.CleanName(name) is not { } clean) return ComponentOperationResult.Fail(SaveIniNameRequiredMessage);
        if (_optiscaler.ManagedIniPath(game) is not { } ini) return ComponentOperationResult.Fail(SaveIniNoIniMessage);
        OptiScalerIniLibrary.Save(clean, ini);
        OptiScalerIniChoices.SetSavedIni(game, clean);
        return ComponentOperationResult.Ok(SaveIniSavedPrefix + $"\"{clean}\".");
    }

    /// <summary>"Load saved ini…" › Use for this game.</summary>
    public async Task<ComponentOperationResult> LoadSavedOptiScalerIniAsync(DetectedGame game, string name, CancellationToken cancellationToken = default)
    {
        if (PatchTargetGuard(game) is { } guard) return guard;
        if (OptiScalerIniLibrary.CleanName(name) is not { } clean || !OptiScalerIniLibrary.Exists(clean))
            return ComponentOperationResult.Fail(OptiScalerDeploymentService.SavedIniMissingPrefix + name);
        var result = await _optiscaler.ReplaceIniWithSavedAsync(game, OptiScalerIniLibrary.PathFor(clean), clean, cancellationToken).ConfigureAwait(false);
        if (result.Success) OptiScalerIniChoices.SetSavedIni(game, clean);
        return result;
    }

    /// <summary>Deletes a saved INI; a game that used it goes back to RHI's template at its next install.</summary>
    public void DeleteSavedOptiScalerIni(string name, DetectedGame? game)
    {
        OptiScalerIniLibrary.Delete(name);
        if (game is not null && string.Equals(OptiScalerIniChoices.Get(game).SavedIni, OptiScalerIniLibrary.CleanName(name), StringComparison.OrdinalIgnoreCase))
            OptiScalerIniChoices.SetSavedIni(game, null);
    }

    /// <summary>
    /// After OptiScaler was installed on <paramref name="game"/>: the saved INI it uses, then its applied edits, are written
    /// again (upstream <c>OsIniLibrary.Deploy</c> + <c>OsIniEditor.ApplySaved</c>). The note for the install's message ("" when none).
    /// </summary>
    private async Task<string> ReapplyOptiScalerIniChoicesAsync(DetectedGame game)
    {
        OptiScalerIniChoices choices;
        try { choices = OptiScalerIniChoices.Get(game); }
        catch (IOException ex)
        {
            RHI.Core.Services.AppLog.Warn("optiscaler", "Saved OptiScaler.ini choices could not be read", ex);
            return "";
        }
        var written = 0;
        if (choices.SavedIni is { } saved && OptiScalerIniLibrary.Exists(saved)
            && (await _optiscaler.ReplaceIniWithSavedAsync(game, OptiScalerIniLibrary.PathFor(saved), saved, CancellationToken.None).ConfigureAwait(false)).Success)
            written++;
        if (choices.Edits.Count > 0)
        {
            var values = choices.Edits.Select(edit => OptiScalerIniCatalog.SplitId(edit.Key) is var (section, key) ? (section, key, edit.Value) : default).ToArray();
            if ((await _optiscaler.ApplyIniValuesAsync(game, values, backup: false, CancellationToken.None).ConfigureAwait(false)).Success)
                written += values.Length;
        }
        return written == 0 ? "" : IniChoicesReappliedPrefix + written;
    }

    /// <summary>The user removed OptiScaler: its applied edits and saved-INI choice are forgotten (upstream).</summary>
    private async Task<ComponentOperationResult> RemoveOptiScalerForgettingIniChoicesAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var result = await RemoveOptiScalerMovingUniversalMfgAsync(game, cancellationToken).ConfigureAwait(false); // w14/extras-fg (#278)
        if (result.Success)
        {
            try { OptiScalerIniChoices.Clear(game); }
            catch (IOException ex) { RHI.Core.Services.AppLog.Warn("optiscaler", "Saved OptiScaler.ini choices were not cleared", ex); }
        }
        return result;
    }

    /// <summary>#273: a version switch removes OptiScaler keeping the saved INI choices (they are written again after the install).</summary>
    private Task<ComponentOperationResult> RemoveOptiScalerKeepingIniChoicesAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Remove, () => RemoveOptiScalerMovingUniversalMfgAsync(game, cancellationToken));
    }

    /// <summary>#272: ANR FG for the OptiScaler preset that installs it (see <see cref="OptiScalerDeploymentService.EnsureAnrFgAsync"/>).</summary>
    public Task<ComponentOperationResult> EnsureAnrFgAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard) : _optiscaler.EnsureAnrFgAsync(game, cancellationToken);
}
