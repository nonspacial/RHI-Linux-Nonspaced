using RHI.Desktop.Diagnostics;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The parity presenters' store writes: the shell view model's <c>TryPersist</c> rule for presenters that do not hold
/// the shell — the save runs, a failure is logged with its stack and comes back as the same localised line
/// (<see cref="MainWindowViewModel.SaveFailedText"/>: "could not save …; nothing was changed", a refused unreadable
/// store named as such), never thrown into a click handler.
/// </summary>
internal static class ParityPersistence
{
    /// <summary>Runs <paramref name="save"/>; null when it saved, else the line saying why not.</summary>
    public static string? TryPersist(Action save, string whatKey)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiExceptionGuard.Log.TryWrite("save " + whatKey, ex);
            return MainWindowViewModel.SaveFailedText(ex, whatKey, sessionOnly: false);
        }
    }
}
