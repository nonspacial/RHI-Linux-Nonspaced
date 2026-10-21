namespace RHI.Core.Models;

public sealed record ComponentOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<string>? ChangedFiles = null,
    // The remote source was unreachable and nothing was changed. Update All counts this as
    // "could not be checked" rather than as an update or a component fault.
    bool SourceUnavailable = false,
    // The change was applied from a cached copy because the remote source was unreachable
    // (e.g. ReShade during a reshade.me outage); callers that rewrite the message keep saying so.
    bool FromCachedSource = false,
    // #204 (w8 follow-up C11): the operation's commit write landed and then the save reported an error; it finished as
    // committed and Message is the note saying so. A caller that words its own success line appends that note.
    bool Landed = false)
{
    public static ComponentOperationResult Ok(string message, params string[] changedFiles)
        => new(true, message, changedFiles);

    public static ComponentOperationResult Fail(string message)
        => new(false, message, Array.Empty<string>());

    public static ComponentOperationResult Unavailable(string message)
        => new(false, message, Array.Empty<string>(), SourceUnavailable: true);
}
