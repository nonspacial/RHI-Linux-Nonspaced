using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-deploy-tests-" + Guid.NewGuid().ToString("N"));
    public RenoDxDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveAddonDeployPath_UsesReshadeAddonPath()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "reshade.ini"), "[GENERAL]\nfoo=bar\n[ADDON]\nAddonPath=.\\reshade-addons\n");
        var result = RenoDxDeploymentService.ResolveAddonDeployPath(bin, prefixPath: null);
        Assert.Equal(Path.GetFullPath(Path.Combine(bin, "reshade-addons")), result);
    }

    [Fact]
    public void ResolveAddonDeployPath_FallsBackToBinaryDirectory()
    {
        Assert.Equal(_root, RenoDxDeploymentService.ResolveAddonDeployPath(_root, prefixPath: null));
    }

    // ── w13 final review m2: ReShade runs in Wine, so an absolute AddonPath is a Windows drive path ───────────────

    private (string Bin, string Prefix) PrefixedGame(string addonPath)
    {
        var prefix = Path.Combine(_root, "pfx");
        var bin = Path.Combine(prefix, "drive_c", "Games", "X");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "c:"), "../drive_c");
        File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        File.WriteAllText(Path.Combine(bin, "reshade.ini"), $"[ADDON]\nAddonPath={addonPath}\n");
        return (bin, prefix);
    }

    [Fact]
    public void ResolveAddonFolder_DriveCPath_MapsIntoThePrefixDriveC()
    {
        var (bin, prefix) = PrefixedGame(@"C:\ReShade\Addons");

        var resolved = AddonPathFolder.Resolve(bin, prefix);

        Assert.Null(resolved.Refusal);
        Assert.Equal(Path.Combine(prefix, "drive_c", "ReShade", "Addons"), resolved.Folder);
        Assert.Equal(resolved.Folder, RenoDxDeploymentService.ResolveAddonDeployPath(bin, prefix));
    }

    [Fact]
    public void ResolveAddonFolder_ZDrivePath_MapsToTheLinuxFolder()
    {
        var addons = Path.Combine(_root, "shared-addons");
        Directory.CreateDirectory(addons);
        var (bin, prefix) = PrefixedGame("Z:" + addons.Replace('/', '\\'));

        var resolved = AddonPathFolder.Resolve(bin, prefix);

        Assert.Null(resolved.Refusal);
        Assert.Equal(addons, resolved.Folder);
    }

    [Fact]
    public void ResolveAddonFolder_UnmappedDrive_RefusesAndNeverJoinsTheDriveOnAsAFolder()
    {
        var (bin, prefix) = PrefixedGame(@"Q:\Addons");

        var resolved = AddonPathFolder.Resolve(bin, prefix);

        Assert.StartsWith(AddonPathFolder.UnmappedPrefix, resolved.Refusal);
        Assert.Contains(@"Q:\Addons", resolved.Refusal);
        // Probes keep looking in the game folder, never in <bin>/Q:/Addons.
        Assert.Equal(bin, resolved.Folder);
        Assert.Equal(bin, RenoDxDeploymentService.ResolveAddonDeployPath(bin, prefix));
        Assert.False(Directory.Exists(Path.Combine(bin, "Q:")));
    }

    [Fact]
    public void ResolveAddonFolder_DrivePathWithoutAKnownPrefix_Refuses()
    {
        var (bin, _) = PrefixedGame(@"C:\ReShade\Addons");

        var resolved = AddonPathFolder.Resolve(bin, prefixPath: null);

        Assert.StartsWith(AddonPathFolder.UnmappedPrefix, resolved.Refusal);
        Assert.Equal(bin, resolved.Folder);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
