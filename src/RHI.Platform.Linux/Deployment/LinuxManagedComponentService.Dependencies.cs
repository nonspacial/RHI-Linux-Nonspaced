using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using static RHI.Platform.Linux.Deployment.ManagedDependencyPlanner;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{

    /// <summary>
    /// The managed dependencies an install of <paramref name="target"/> would add automatically right now, in install
    /// order (nested first: RE Framework before ReShade on an RE Engine game). Read from the same plan the install
    /// executes (<see cref="PlanManagedDependencies"/>), without touching anything: dependencies already managed or
    /// supplied externally are skipped, and when the install would refuse (a broken managed dependency anywhere in the
    /// graph, a conflicting component) nothing is added. Used to show the manifest warnings of those components before
    /// a user-initiated install (upstream 2.8.0 #131). May index the game folder (RE Framework check): call it off the
    /// UI thread.
    /// </summary>
    public IReadOnlyList<ManagedComponentKind> PendingAutoDependencies(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, target).Count == 0)
            return Array.Empty<ManagedComponentKind>();

        IReadOnlyList<string>? index = null;
        var plan = _dependencies.PlanManagedDependencies(game, target, () => index ??= _dependencies.BuildDependencyIndex(game, indexedFiles), cancellationToken);
        return plan.Refusal is null
            ? plan.Steps.Select(step => step.Dependency).ToArray()
            : Array.Empty<ManagedComponentKind>();
    }

    internal async Task<ComponentOperationResult> InstallWithManagedDependenciesAsync(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles,
        Func<Task<ComponentOperationResult>> targetInstaller,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? index = null;
        IReadOnlyList<string> Index() => index ??= _dependencies.BuildDependencyIndex(game, indexedFiles);
        ManagedDependencyPlan plan;
        try
        {
            plan = _dependencies.PlanManagedDependencies(game, target, Index, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ComponentOperationResult.Fail("Instalação cancelada durante a resolução automática de dependências.");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Falha inesperada durante a resolução automática de dependências: {ex.Message}");
        }
        if (plan.Refusal is not null) return plan.Refusal;

        var installedThisAttempt = new List<ManagedComponentKind>();
        var dependencyChanges = new List<string>();

        ComponentOperationResult dependencies;
        try
        {
            dependencies = await InstallPlannedDependenciesAsync(
                game, plan, Index, installedThisAttempt, dependencyChanges, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage("Instalação cancelada durante a resolução automática de dependências.", rollback));
        }
        catch (Exception ex)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage($"Falha inesperada durante a resolução automática de dependências: {ex.Message}", rollback));
        }

        if (!dependencies.Success)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage(dependencies.Message, rollback));
        }

        ComponentOperationResult result;
        try
        {
            result = await targetInstaller().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage("Instalação cancelada antes de concluir o componente solicitado.", rollback));
        }
        catch (Exception ex)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage($"Falha inesperada ao instalar {DisplayDependencyName(target)}: {ex.Message}", rollback));
        }

        if (!result.Success)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage(result.Message, rollback));
        }

        result = await ApplyAddonSetAfterReShadeAsync(game, target, installedThisAttempt, result, cancellationToken).ConfigureAwait(false); // w7/shader-sets hook (#179)
        if (installedThisAttempt.Count == 0) return result;

        var changed = dependencyChanges
            .Concat(result.ChangedFiles ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var names = string.Join(" → ", installedThisAttempt.Select(DisplayDependencyName));
        // Keep the target's structural flags (e.g. FromCachedSource) along with its message.
        return result with
        {
            Message = $"Dependências instaladas automaticamente ({names}). {result.Message}",
            ChangedFiles = changed
        };
    }

    // Executes a plan from PlanManagedDependencies step by step. Each step is re-checked right before it runs, since
    // an earlier step (or the user, meanwhile) may have changed what is there.
    private async Task<ComponentOperationResult> InstallPlannedDependenciesAsync(
        DetectedGame game,
        ManagedDependencyPlan plan,
        Func<IReadOnlyList<string>> index,
        List<ManagedComponentKind> installedThisAttempt,
        List<string> dependencyChanges,
        CancellationToken cancellationToken)
    {
        foreach (var (parent, dependency) in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var satisfaction = _dependencies.InspectDependency(game, parent, dependency, DependencyFiles(dependency, index), cancellationToken);
            if (satisfaction is DependencySatisfaction.Managed or DependencySatisfaction.External) continue;
            if (satisfaction == DependencySatisfaction.BrokenManaged)
                return ComponentOperationResult.Fail(
                    $"A dependência gerenciada {DisplayDependencyName(dependency)} ficou inconsistente durante a resolução automática.");

            var before = _records.Find(game, dependency);
            var install = await InstallDependencyCoreAsync(game, dependency, cancellationToken).ConfigureAwait(false);
            if (!install.Success)
                return ComponentOperationResult.Fail(
                    $"Não foi possível instalar automaticamente a dependência {DisplayDependencyName(dependency)} exigida por {DisplayDependencyName(parent)}: {install.Message}");

            if (before is null && _records.Find(game, dependency) is not null)
                installedThisAttempt.Add(dependency);
            if (install.ChangedFiles is { Count: > 0 })
                dependencyChanges.AddRange(install.ChangedFiles.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        return ComponentOperationResult.Ok("Dependencies ready.");
    }

    /// <summary>
    /// w7 follow-up B31 (merge review O1): ReShade is not installed automatically for another component on a game with DX9
    /// HDR (PT-BR pair) — beside DX9 HDR it goes to its Vulkan layer, which the user chooses on ReShade's row.
    /// </summary>
    public const string ReShadeDependencyBesideDxvkHdrMessage =
        "DX9 HDR is installed on this game, so RHI does not add ReShade to it on its own: beside DX9 HDR, ReShade goes to its Vulkan layer. Install ReShade on its own row first, then try again.";

    private Task<ComponentOperationResult> InstallDependencyCoreAsync(
        DetectedGame game,
        ManagedComponentKind dependency,
        CancellationToken cancellationToken)
        => dependency switch
        {
            // B31: the view the ReShade row would install through (its Vulkan layer beside DX9 HDR) differs: say so plainly
            // instead of the DLL-name refusal the raw game ran into ("choose another DLL name" was wrong there).
            ManagedComponentKind.ReShade when !ReferenceEquals(ReShadeViewForDxvkHdr(game), game)
                => Task.FromResult(ComponentOperationResult.Fail(ReShadeDependencyBesideDxvkHdrMessage)),
            ManagedComponentKind.ReShade => InstallReShadeForShaderSetAsync(game, ReShadeDeploymentService.DefaultOptionsFor(game), update: false, cancellationToken), // w7/shader-sets hook (#179 review major 2)
            ManagedComponentKind.REFramework => _reFramework.InstallAsync(game, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail(
                $"{DisplayDependencyName(dependency)} is declared as an automatic dependency but has no dependency installer."))
        };

    private async Task<string?> RollBackAutoDependenciesAsync(
        DetectedGame game,
        IReadOnlyList<ManagedComponentKind> installedThisAttempt,
        CancellationToken cancellationToken)
    {
        if (installedThisAttempt.Count == 0) return null;

        var failures = new List<string>();
        for (var index = installedThisAttempt.Count - 1; index >= 0; index--)
        {
            var dependency = installedThisAttempt[index];

            // If ReShade was also created by this attempt but could not be rolled back, keep the
            // newly created RE Framework dependency in place rather than leaving managed ReShade
            // on an RE Engine title with its required framework removed underneath it. If ReShade
            // predated this attempt, removing a newly added RE Framework restores the exact prior
            // state and remains the transactional behavior.
            if (dependency == ManagedComponentKind.REFramework &&
                installedThisAttempt.Contains(ManagedComponentKind.ReShade) &&
                _records.Find(game, ManagedComponentKind.ReShade) is not null)
            {
                failures.Add("RE Framework: preserved because the ReShade dependency created by this attempt could not be rolled back safely.");
                continue;
            }

            ComponentOperationResult result;
            try
            {
                result = dependency switch
                {
                    ManagedComponentKind.ReShade => await _reshade.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                    ManagedComponentKind.REFramework => await _reFramework.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                    _ => ComponentOperationResult.Fail("No rollback handler is available.")
                };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{DisplayDependencyName(dependency)}: {ex.Message}");
                continue;
            }

            if (!result.Success) failures.Add($"{DisplayDependencyName(dependency)}: {result.Message}");
        }

        return failures.Count == 0
            ? "As dependências instaladas nesta tentativa foram revertidas."
            : "Rollback parcial das dependências: " + string.Join(" | ", failures);
    }

    private static string AppendRollbackMessage(string message, string? rollback)
        => string.IsNullOrWhiteSpace(rollback) ? message : $"{message} {rollback}";

    /// <summary>
    /// Fixed text before "component: file" when Luma/ShortFuse would install their ReShade beside a ReShade RHI did not
    /// install (PT-BR presentation pair).
    /// </summary>
    public const string ExternalReShadeBlocksDependencyPrefix =
        "This component needs ReShade installed by RHI, but a ReShade RHI Linux did not install is already in this game folder; it was left alone and nothing was installed. Remove that ReShade first, or keep using it without this component. Component and file: ";

    public static string DisplayDependencyName(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReShade => "ReShade",
        ManagedComponentKind.REFramework => "RE Framework",
        ManagedComponentKind.RenoDX => "RenoDX HDR",
        ManagedComponentKind.RenoDXUpgrade => "RenoDX Upgrade",
        ManagedComponentKind.ReLimiter => "ReLimiter",
        ManagedComponentKind.DisplayCommander => "Display Commander",
        ManagedComponentKind.MfgAdaUnlock => "MFG Ada Unlock",
        ManagedComponentKind.Dlss5Feeder => "DLSS5 Feeder",
        ManagedComponentKind.Dlss5Dx11Bridge => "DLSS5 DX11 Bridge",
        ManagedComponentKind.ShortFuse => "ShortFuse",
        ManagedComponentKind.DofFix => "Universal UE DOF Fix",
        ManagedComponentKind.LumaFramework => "Luma Framework",
        ManagedComponentKind.OptiScaler => "OptiScaler",
        ManagedComponentKind.UltimateAsiLoader => "Ultimate ASI Loader",
        ManagedComponentKind.Dlssg2030Unlock => "RTX 20 FG Unlock",
        ManagedComponentKind.DlssEnablerStandalone => "DLSS Enabler (Standalone)",
        ManagedComponentKind.Rtx40MfgStandalone => "Universal MFG",
        ManagedComponentKind.NrCostScaler => NrCostScalerService.DisplayName, // w3/parity (#96)
        ManagedComponentKind.RtxEncore => RtxEncoreDeploymentService.DisplayName, // w4/encore (#158)
        ManagedComponentKind.ReShadeAddonManager => ReShadeAddonManagerService.DisplayName, // #194
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        ManagedComponentKind.DxvkHdr => DxvkHdrDeploymentService.DisplayName,
        // w10/dlss-presets rows
        // w10/archives rows
        // w10/journal-holds rows
        // w11/download-idle rows
        // w11/nr-gpu rows
        // w11/nightly-switch rows
        // w11/nr-check rows
        // w12/mfg rows
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows
        _ => kind.ToString()
    };
}
