using RHI.Platform.Linux.Ux;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The upstream-parity features of wave 3 (w3/parity): GitHub token (#163), app update check (#100), Nexus Mods (#99),
/// desktop shortcuts (#162), shader-selection profiles (#93) and NR Cost Scaler (#96) presenters, built once by
/// <c>AppComposition</c> and reached by the shell through <see cref="MainWindowViewModel.Parity"/> (one hook) so the
/// shell view model holds none of their logic.
/// </summary>
public sealed class ParityFeatures
{
    public required GitHubTokenPresenter GitHubToken { get; init; }
    public required AppUpdatePresenter AppUpdate { get; init; }
    public required GameShortcutPresenter Shortcuts { get; init; }
    public required NexusAccountPresenter NexusAccount { get; init; }
    public required NexusLinkPresenter NexusLinks { get; init; }
    public required ShaderProfilePresenter ShaderProfiles { get; init; }

    /// <summary>A request a shortcut or link brought to this RHI (its own command line or the hand-off). UI thread.</summary>
    public Task HandleHandoffAsync(MainWindowViewModel vm, HandoffRequest request) => request.Kind switch
    {
        HandoffKind.Launch => Shortcuts.LaunchByKeyAsync(vm, request.Value),
        _ => NexusLinks.HandleAsync(vm, request.Value)
    };
}
