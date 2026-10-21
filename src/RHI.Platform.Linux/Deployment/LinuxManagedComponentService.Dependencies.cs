using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    private IReadOnlyList<string> BuildDependencyIndex(DetectedGame game, IReadOnlyList<string>? indexedFiles = null)
    {
        if (indexedFiles is not null) return indexedFiles;
        var root = Directory.Exists(game.InstallPath)
            ? game.InstallPath
            : game.BinaryDirectory;
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? Array.Empty<string>()
            : GameFileIndexer.Build(root, 8);
    }

    private enum DependencySatisfaction
    {
        Missing,
        Managed,
        External,
        BrokenManaged
    }

    private DependencySatisfaction InspectDependency(
        DetectedGame game,
        ManagedComponentKind target,
        ManagedComponentKind dependency,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        var record = _records.Find(game, dependency);
        if (record is not null)
        {
            var hasPayload = record.Files.Any(file =>
                !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path));
            return hasPayload ? DependencySatisfaction.Managed : DependencySatisfaction.BrokenManaged;
        }

        if (LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(target, dependency))
            return DependencySatisfaction.Missing;

        if (dependency == ManagedComponentKind.ReShade &&
            !string.IsNullOrWhiteSpace(game.BinaryDirectory) &&
            GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return DependencySatisfaction.External;

        if (dependency == ManagedComponentKind.REFramework &&
            ComponentProbe.Probe(game, indexedFiles, cancellationToken)
                .Any(component => component.Kind == ManagedComponentKind.REFramework &&
                                  component.Availability == ComponentAvailability.Installed))
            return DependencySatisfaction.External;

        return DependencySatisfaction.Missing;
    }

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
        var plan = PlanManagedDependencies(game, target, () => index ??= BuildDependencyIndex(game, indexedFiles), cancellationToken);
        return plan.Refusal is null
            ? plan.Steps.Select(step => step.Dependency).ToArray()
            : Array.Empty<ManagedComponentKind>();
    }

    /// <summary>One automatic dependency to install, and the component that requires it.</summary>
    internal readonly record struct ManagedDependencyStep(ManagedComponentKind Parent, ManagedComponentKind Dependency);

    /// <summary>What an install of a target does to its managed dependencies: the steps in install order, or a refusal.</summary>
    internal sealed record ManagedDependencyPlan(IReadOnlyList<ManagedDependencyStep> Steps, ComponentOperationResult? Refusal);

    /// <summary>
    /// The single automatic-dependency walk shared by <see cref="PendingAutoDependencies"/> and the install. Reads the
    /// record store and the game folder only. Order: nested dependencies first, each Missing dependency once.
    /// </summary>
    internal ManagedDependencyPlan PlanManagedDependencies(
        DetectedGame game,
        ManagedComponentKind target,
        Func<IReadOnlyList<string>> index,
        CancellationToken cancellationToken)
    {
        var refusal = DependencyConflictGuard(game, target);
        if (refusal is not null) return new ManagedDependencyPlan(Array.Empty<ManagedDependencyStep>(), refusal);

        var steps = new List<ManagedDependencyStep>();
        var visiting = new HashSet<ManagedComponentKind>();
        refusal = Walk(target);
        return refusal is null
            ? new ManagedDependencyPlan(steps, null)
            : new ManagedDependencyPlan(Array.Empty<ManagedDependencyStep>(), refusal);

        ComponentOperationResult? Walk(ManagedComponentKind component)
        {
            if (!visiting.Add(component))
                return ComponentOperationResult.Fail($"Dependency cycle detected while preparing {DisplayDependencyName(component)}.");
            try
            {
                foreach (var dependency in LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, component))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var satisfaction = InspectDependency(game, component, dependency, DependencyFiles(dependency, index), cancellationToken);
                    if (satisfaction == DependencySatisfaction.BrokenManaged)
                        return ComponentOperationResult.Fail(
                            $"A dependência gerenciada {DisplayDependencyName(dependency)} possui registro de ownership, mas seu payload não está presente. Repare ou remova esse estado antes de instalar {DisplayDependencyName(component)}.");

                    // An external dependency is a preservation boundary. It may satisfy ordinary add-ons,
                    // but RHI does not recurse into or adopt the external stack. RHI-managed dependencies,
                    // on the other hand, must keep their own transitive dependency graph valid.
                    if (satisfaction == DependencySatisfaction.External) continue;

                    var nested = Walk(dependency);
                    if (nested is not null) return nested;
                    if (satisfaction == DependencySatisfaction.Missing && !steps.Any(step => step.Dependency == dependency))
                        steps.Add(new ManagedDependencyStep(component, dependency));
                }
                return null;
            }
            finally
            {
                visiting.Remove(component);
            }
        }
    }

    // Only the RE Framework check reads the file index; other dependencies are checked by record or fixed file names.
    private static IReadOnlyList<string> DependencyFiles(ManagedComponentKind dependency, Func<IReadOnlyList<string>> index)
        => dependency == ManagedComponentKind.REFramework ? index() : Array.Empty<string>();

    internal async Task<ComponentOperationResult> InstallWithManagedDependenciesAsync(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles,
        Func<Task<ComponentOperationResult>> targetInstaller,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? index = null;
        IReadOnlyList<string> Index() => index ??= BuildDependencyIndex(game, indexedFiles);
        ManagedDependencyPlan plan;
        try
        {
            plan = PlanManagedDependencies(game, target, Index, cancellationToken);
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
            var satisfaction = InspectDependency(game, parent, dependency, DependencyFiles(dependency, index), cancellationToken);
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

    private ComponentOperationResult? DependencyConflictGuard(DetectedGame game, ManagedComponentKind target)
    {
        // w3/dllnames (#91): a ReShade add-on cannot load in the No Addons build.
        if (LinuxComponentDependencyPolicy.IsReShadeAddon(target) && ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade)))
            return ComponentOperationResult.Fail(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + DisplayDependencyName(target));

        if (target == ManagedComponentKind.ReLimiter && _records.Find(game, ManagedComponentKind.DisplayCommander) is not null)
            return ComponentOperationResult.Fail("Display Commander já está ativo. ReLimiter e Display Commander são mutuamente exclusivos.");
        if (target == ManagedComponentKind.DisplayCommander && _records.Find(game, ManagedComponentKind.ReLimiter) is not null)
            return ComponentOperationResult.Fail("ReLimiter já está ativo. Display Commander e ReLimiter são mutuamente exclusivos.");

        if (target == ManagedComponentKind.RenoDX && _records.Find(game, ManagedComponentKind.LumaFramework) is not null &&
            !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
            return ComponentOperationResult.Fail("Luma Framework já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.");
        if (target == ManagedComponentKind.LumaFramework && _records.Find(game, ManagedComponentKind.RenoDX) is not null &&
            !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
            return ComponentOperationResult.Fail("RenoDX HDR já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.");

        if (target == ManagedComponentKind.MfgAdaUnlock && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock.");
        if (target == ManagedComponentKind.MfgAdaUnlock && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock.");

        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("OptiScaler já está ativo. ShortFuse não reescreve automaticamente essa cadeia ReShade/OptiScaler.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.");
        // w4/encore (#158): never switch away from RTX Encore's Neural Rendering file; the user removes it first. Refused
        // here, before any dependency (ReShade) is installed.
        if (target is ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder
            && NeuralRenderingRuntimeOwnership.RtxEncoreOwner(_records, game) is { } encoreOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(encoreOwner));

        if ((target is ManagedComponentKind.LumaFramework or ManagedComponentKind.ShortFuse) &&
            _records.Find(game, ManagedComponentKind.ReShade) is null &&
            !string.IsNullOrWhiteSpace(game.BinaryDirectory) &&
            Directory.Exists(game.BinaryDirectory) &&
            GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
        {
            // dllnames re-review nit 4: the same plain next step as the direct install's refusal, with the file.
            return ComponentOperationResult.Fail(ExternalReShadeBlocksDependencyPrefix +
                $"{DisplayDependencyName(target)}: {ProxyPayloadLocator.FindReShadeOnDisk(game.BinaryDirectory) ?? game.BinaryDirectory}");
        }

        if (target == ManagedComponentKind.Dlss5Feeder &&
            NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } nrOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));

        return null;
    }

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
        _ => kind.ToString()
    };
}
