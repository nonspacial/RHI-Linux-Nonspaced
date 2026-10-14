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
}
