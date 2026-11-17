using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// The ⚙ builders' writers in tests that check the rows alone (the app passes the managed service's journaled writes,
/// w14 final review M5; those are tested in ComponentSettingsFileTests).
/// </summary>
internal static class FgSettingsWriters
{
    public static Func<string, string, string, ComponentOperationResult> Encore(string path)
        => (section, key, value) => { RtxEncoreConfig.SetInFile(path, section, key, value); return ComponentOperationResult.Ok(path); };

    public static readonly Func<string, string, string, ComponentOperationResult> NotInstalled
        = (_, _, _) => ComponentOperationResult.Fail(ComponentSettingsFile.NotInstalledPrefix + RtxEncoreDeploymentService.DisplayName);
}
