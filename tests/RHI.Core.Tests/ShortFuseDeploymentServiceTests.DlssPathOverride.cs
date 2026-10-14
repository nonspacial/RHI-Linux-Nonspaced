using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>#155: ShortFuse's \"no stacking over direct swaps\" check also reads the manifest DLSS folder.</summary>
public sealed partial class ShortFuseDeploymentServiceTests
{
    [Fact]
    public async Task Install_BlocksADirectSwapOriginalInTheManifestDlssFolder()
    {
        using var fixture = CreateGame("manifest-folder");
        fixture.Game.DlssPathOverride = DlssPathOverrideTests.DlssRel;
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        var folder = Path.Combine(fixture.Game.InstallPath, DlssPathOverrideTests.DlssRel.Replace('\\', '/'));
        Directory.CreateDirectory(folder);
        var dlss = Path.Combine(folder, "nvngx_dlss.dll");
        File.WriteAllBytes(dlss, CreatePe(0x8664, 80 * 1024, 0x22));
        File.WriteAllBytes(dlss + TransactionalFileDeploymentService.BackupExtension, CreatePe(0x8664, 80 * 1024, 0x21));

        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);

        Assert.False(result.Success);
        Assert.Contains(".original ownership cycle", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
