using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #113 / w2-launch review, minors 4 and 5: with manual Update All checking every family first, a check that cannot
/// tell (offline, no matching package, a legacy install nothing is compared against) must read as "could not be
/// checked", never "current". One row per Update All family through the app's real services (offline), plus the
/// families' other could-not-tell paths, so dropping the flag from any one of them fails here.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdateCheckCouldNotCheckTests
{
    public static TheoryData<ManagedComponentKind, string> Cases()
    {
        var data = new TheoryData<ManagedComponentKind, string>();
        foreach (var kind in LinuxUpdateAllService.SupportedComponents) data.Add(kind, "offline");
        // Luma: the offline catalog finds no package for a game outside its generic Unreal rule (the default row);
        // a UE4 DX11 game gets as far as the release lookup, which fails offline; a drag/drop install has no source.
        data.Add(ManagedComponentKind.LumaFramework, "generic-ue4");
        data.Add(ManagedComponentKind.LumaFramework, "local-package");
        // DLSS5 Feeder from before the full stack: never compared against a release (#115).
        data.Add(ManagedComponentKind.Dlss5Feeder, "legacy-addon-only");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CheckThatCannotTell_IsCouldNotBeChecked_InBothUpdateAllModes(ManagedComponentKind kind, string variant)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, variant);
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1.0/x.zip"
        };
        if (kind == ManagedComponentKind.Dlss5Feeder && variant != "legacy-addon-only") record.Metadata["Feeder.FullStack"] = "1";
        if (variant == "local-package") record.Metadata[LumaFrameworkDeploymentService.MetaLocalPackage] = "1";
        fx.Records.Upsert(record);

        var check = await fx.Managed.CheckManagedUpdateAsync(game, kind, []);

        Assert.True(check.Managed, check.Message);
        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.Blocked, check.Message);
        Assert.True(check.CouldNotCheck, $"{kind} ({variant}) read as current: {check.Message}");

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            // Manual mode refreshes ReShade without a check (upstream parity); every other family is checked first.
            if (mode == LinuxUpdateRunMode.Manual && kind == ManagedComponentKind.ReShade) continue;
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        }
    }

    private static DetectedGame Game(string root, string variant)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "games", "Checked Game")).FullName;
        var exe = Path.Combine(dir, "CheckedGame.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var game = new DetectedGame
        {
            Name = "Checked Game", Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            GraphicsApi = GraphicsApi.DirectX11
        };
        if (variant == "generic-ue4") game.EngineHint = "Unreal Engine 4";
        return game;
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
