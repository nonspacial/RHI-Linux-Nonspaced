using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Makes manually added games first-class scanner input so they pass through the same
/// upstream manifest policy, dedupe and runtime enrichment as launcher-detected games.
/// </summary>
public sealed class ManualGameDetector : IGameDetector
{
    private readonly LinuxGameLibraryStore _store;
    public string Name => "Manual";

    public ManualGameDetector(LinuxGameLibraryStore store) => _store = store;

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var games = _store.Load().ManualGames
            .Select(entry =>
            {
                // Never hidden: it is the user's entry. One whose folder no longer exists is listed as "folder
                // missing" (Refs #145); one saved with a folder Add/Edit now refuse ($HOME, /, a whole Wine
                // prefix…) as "folder not allowed". Either way there is no patch target, so nothing indexes or
                // patches that path; Remove or Edit repairs it.
                var missing = string.IsNullOrWhiteSpace(entry.InstallPath) || !Directory.Exists(entry.InstallPath);
                var notAllowed = !missing && !GameRootGuard.IsAcceptable(entry.InstallPath);
                var unusable = missing || notAllowed;
                var exe = !unusable && File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null;
                return new DetectedGame
                {
                    Name = entry.Name,
                    InstallPath = entry.InstallPath,
                    Source = "Manual",
                    IsManuallyAdded = true,
                    GameRootNotAllowed = notAllowed,
                    FolderMissing = missing,
                    RequiresExplicitPatchTarget = unusable,
                    ExecutablePath = exe,
                    LaunchExecutablePath = File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null,
                    BinaryDirectory = unusable
                        ? null
                        : exe is not null ? Path.GetDirectoryName(exe) : entry.InstallPath,
                    PrefixPath = Directory.Exists(entry.PrefixPath) ? entry.PrefixPath : null,
                    Runner = entry.Runner ?? "Manual Windows",
                    LaunchCommand = entry.LaunchCommand,
                    CompatibilityLayerType = Directory.Exists(entry.PrefixPath) ? "wine" : null,
                    UserGraphicsApiOverride = entry.UserGraphicsApiOverride
                };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<DetectedGame>>(games);
    }
}
