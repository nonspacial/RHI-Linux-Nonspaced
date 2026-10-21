using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using static RHI.Platform.Linux.Deployment.LinuxManagedComponentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Automatic managed dependencies (#212): what an install would add, in order, or why it refuses. Reads the
/// ledger and the game folder only.
/// </summary>
internal sealed class ManagedDependencyPlanner
{
    private readonly LinuxDeploymentRecordStore _records;
    private readonly LumaPolicyService _lumaPolicy;

    internal ManagedDependencyPlanner(
        LinuxDeploymentRecordStore records,
        LumaPolicyService lumaPolicy)
    {
        _records = records;
        _lumaPolicy = lumaPolicy;
    }

    internal IReadOnlyList<string> BuildDependencyIndex(DetectedGame game, IReadOnlyList<string>? indexedFiles = null)
    {
        if (indexedFiles is not null) return indexedFiles;
        var root = Directory.Exists(game.InstallPath)
            ? game.InstallPath
            : game.BinaryDirectory;
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? Array.Empty<string>()
            : GameFileIndexer.Build(root, 8);
    }

    internal enum DependencySatisfaction
    {
        Missing,
        Managed,
        External,
        BrokenManaged
    }

    internal DependencySatisfaction InspectDependency(
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
    internal static IReadOnlyList<string> DependencyFiles(ManagedComponentKind dependency, Func<IReadOnlyList<string>> index)
        => dependency == ManagedComponentKind.REFramework ? index() : Array.Empty<string>();

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
}
