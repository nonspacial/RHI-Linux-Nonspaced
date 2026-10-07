namespace RHI.Core.Services;

/// <summary>
/// The launch command ended straight away with a failure exit code (a missing program, a store client
/// that refused the request…). <see cref="Detail"/> is the last line the command wrote to stderr, verbatim.
/// </summary>
public sealed class GameLaunchFailedException(int exitCode, string? detail)
    : Exception($"The launch command exited with code {exitCode}" + (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"))
{
    public int ExitCode { get; } = exitCode;
    public string? Detail { get; } = detail;
}
