using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>w5/ini-presets tests: a minimal record saying RHI installed a component in a game.</summary>
internal static class IniTestRecords
{
    public static LinuxDeploymentRecordStore Manage(this LinuxDeploymentRecordStore records, DetectedGame game, params ManagedComponentKind[] kinds)
    {
        foreach (var kind in kinds)
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = kind,
                BinaryDirectory = game.BinaryDirectory!,
                Files = []
            });
        return records;
    }
}
