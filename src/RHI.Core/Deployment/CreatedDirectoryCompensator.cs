using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Core.Deployment;

/// <summary>
/// Undoes a folder an operation created (#92: the Vulkan layer's folders in a prefix's <c>ProgramData</c>): journaled
/// before the folder is made, so a rollback or a crash before the commit removes it again once the operation's files are
/// gone. Only an empty folder is removed (anything else in it keeps it); a symbolic link is never followed or removed.
/// Files roll back before compensations run, and compensations run in reverse, so the innermost folder goes first.
/// </summary>
public sealed class CreatedDirectoryCompensator : IDeploymentCompensator
{
    public const string KindName = "created-directory";

    public string Kind => KindName;

    /// <summary>The journal step for <paramref name="path"/>; add it before creating the folder.</summary>
    public static string Describe(string path) => JsonSerializer.Serialize(new Step { Path = Path.GetFullPath(path) });

    public CompensationResult Compensate(string data)
    {
        var step = JsonSerializer.Deserialize<Step>(data) ?? throw new InvalidDataException("created-directory step without data");
        if (string.IsNullOrWhiteSpace(step.Path) || !Path.IsPathFullyQualified(step.Path))
            throw new InvalidDataException("created-directory step without a full path");
        var info = new DirectoryInfo(step.Path);
        if (!info.Exists || info.LinkTarget is not null) return CompensationResult.Done;
        try
        {
            if (!info.EnumerateFileSystemInfos().Any()) info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind breaks nothing; never hold a recovery back for it.
            AppLog.Warn("deployment-journal", $"Could not remove the empty folder {step.Path}", ex);
        }
        return CompensationResult.Done;
    }

    private sealed class Step
    {
        public string Path { get; set; } = "";
    }
}
