using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>w14/nr (#274): GITC Uplift as a Neural Rendering method — install/update/remove go through the usual guards.</summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The GITC Uplift service (set by the app's composition; null in builds and tests constructed without it).</summary>
    public GitcUpliftDeploymentService? GitcUplift { get; set; }

    /// <summary>Settings › GITC Uplift › NR on/off key (a Windows virtual-key code; 0 = none), read at install time.</summary>
    public Func<int>? GitcUpliftEnableKey { get; set; }

    /// <summary>The game's iMMERSE Launchpad choice for Uplift: the saved one, else on in games without DLSS (upstream default).</summary>
    public bool GetUpliftLaunchpadChoice(DetectedGame game, bool gameHasDlss)
        => GitcUplift?.GetRecord(game) is not null
            ? GitcUplift.LaunchpadOn(game)
            : _neuralVersionPins.GetUpliftLaunchpad(game) ?? GitcUpliftDeploymentService.LaunchpadDefault(gameHasDlss);

    /// <summary>Saves the game's Launchpad choice and, when Uplift is installed, applies it now (logged in the update history).</summary>
    public Task<ComponentOperationResult> SetUpliftLaunchpadAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
    {
        if (GitcUplift is null) return Task.FromResult(ComponentOperationResult.Fail($"{GitcUpliftDeploymentService.DisplayName} installation is not available yet."));
        if (PatchTargetGuard(game) is { } guard) return Task.FromResult(guard);
        _neuralVersionPins.SetUpliftLaunchpad(game, on);
        return GitcUplift.GetRecord(game) is null
            ? GitcUplift.SetLaunchpadAsync(game, on, cancellationToken)
            : Track(game, ManagedComponentKind.GitcUplift, ComponentUpdateAction.Update, () => GitcUplift.SetLaunchpadAsync(game, on, cancellationToken));
    }

    private async Task<ComponentOperationResult> InstallGitcUpliftCoreAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (GitcUplift is null) return ComponentOperationResult.Fail($"{ManagedComponentKind.GitcUplift} installation is not available yet.");
        // One Neural Rendering tool at a time (Uplift's README: remove other DLSS-NR add-ons first).
        if (NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } nrOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));
        var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.GitcUplift, cancellationToken).ConfigureAwait(false);
        if (cleanup is not null) return cleanup;
        var hasDlss = HasNativeDlssForNeuralRendering(game, RtxEncoreDeploymentService.HasGameDlss(game));
        var options = new GitcUpliftOptions(
            _neuralVersionPins.Get(game, ManagedComponentKind.GitcUplift),
            _neuralVersionPins.GetNeuralRenderingDllVersion(game),
            _neuralVersionPins.GetUpliftLaunchpad(game) ?? GitcUpliftDeploymentService.LaunchpadDefault(hasDlss),
            GitcUpliftEnableKey?.Invoke() ?? 0);
        return await GitcUplift.InstallAsync(game, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// "Write key to all Uplift games": the NR on/off key into every listed game where RHI installed Uplift. Returns how many
    /// games took it and the first failure.
    /// </summary>
    public async Task<(int Written, IReadOnlyList<(string Game, string Message)> Failures)> WriteUpliftKeyToAllAsync(
        IReadOnlyList<DetectedGame> games, int vk, CancellationToken cancellationToken = default)
    {
        var failures = new List<(string, string)>();
        var written = 0;
        if (GitcUplift is null) return (0, failures);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GitcUplift.GetRecord(game) is null) continue;
            if (PatchTargetGuard(game) is { } guard) { failures.Add((game.Name, guard.Message)); continue; }
            var result = await GitcUplift.WriteEnableKeyAsync(game, vk, cancellationToken).ConfigureAwait(false);
            if (result.Success) written++;
            else failures.Add((game.Name, result.Message));
        }
        return (written, failures);
    }

    /// <summary>iMMERSE Launchpad back at the top of the preset in every listed game with Uplift and Launchpad (games not running only).</summary>
    public async Task<int> KeepLaunchpadFirstAsync(IReadOnlyList<DetectedGame> games, CancellationToken cancellationToken = default)
    {
        if (GitcUplift is null) return 0;
        var moved = 0;
        foreach (var game in games)
            if (GitcUplift.GetRecord(game) is not null && game.HasConfirmedPatchTarget
                && await GitcUplift.KeepLaunchpadFirstAsync(game, cancellationToken).ConfigureAwait(false))
                moved++;
        return moved;
    }
}
