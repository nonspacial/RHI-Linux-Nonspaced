using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#135 review findings 2–5: take-over collision guard, saved progress on partial disable/removal,
/// hash check of kept files on removal, identical foreign files left unowned.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // Unix file modes (read-only folders)
public sealed partial class OptiScalerFgStackDlssgTests
{
    [Fact]
    public async Task EnableFgStack_ForeignTargetWithExistingOriginalIsRefusedAndBothStayByteIdentical()
    {
        var fx = CreateFixture("collision");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x7A));
        File.WriteAllBytes(dlssg + ".original", Dlssg(0x7C)); // some other tool's backup
        var recordBefore = File.ReadAllText(fx.RecordsPath);

        var result = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.False(result.Success);
        Assert.Contains("Backup collision protection", result.Message);
        Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(dlssg));
        Assert.Equal(Dlssg(0x7C), File.ReadAllBytes(dlssg + ".original"));
        Assert.Equal(recordBefore, File.ReadAllText(fx.RecordsPath));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll")));
    }

    [Fact]
    public async Task EnableFgStack_IdenticalForeignFileNextToAnOriginalIsLeftUnownedAndNotReportedChanged()
    {
        var fx = CreateFixture("identical");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x51)); // byte-identical to the newest manifest payload
        File.WriteAllBytes(dlssg + ".original", Dlssg(0x7C));

        var result = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Dlssg(0x51), File.ReadAllBytes(dlssg));
        Assert.Equal(Dlssg(0x7C), File.ReadAllBytes(dlssg + ".original"));
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.DoesNotContain(record.Files, file => file.Path == dlssg);
        Assert.DoesNotContain(DlssgRelative, OwnedStackFiles(record));
        Assert.DoesNotContain(dlssg, result.ChangedFiles!);
        Assert.Contains(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll"), result.ChangedFiles!);
    }

    // up-135 re-review minor 2: builds before #135 let Quick Apply / Batch Deploy / the panel's Streamline swap replace
    // RHI's own OptiScaler/Streamline/sl.* (the owned file kept as .original). Those flows no longer reach the folder, so
    // disable and removal give such a backup back instead of refusing forever.
    [Theory]
    [InlineData("disable")]
    [InlineData("remove")]
    public async Task LegacyDlssSwapOfAStackFile_IsGivenBackOnDisableAndRemoval(string action)
    {
        var fx = CreateFixture("legacy-swap-" + action);
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);
        var interposer = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll");
        Assert.True(File.Exists(interposer));
        // What the old direct swap did: keep the current (RHI-owned) file as .original, write the swapped-in DLL.
        File.Move(interposer, interposer + ".original");
        File.WriteAllBytes(interposer, Dlssg(0x6E));

        var result = action == "disable"
            ? await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false")
            : await fx.Service.RemoveAsync(fx.Game);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(interposer));
        Assert.False(File.Exists(interposer + ".original"));
        if (action == "remove") Assert.Null(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));
        else Assert.DoesNotContain("OptiScaler/Streamline/sl.interposer.dll", OwnedStackFiles(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!));
    }

    [Fact]
    public async Task ModifiedStackFileWhoseOriginalIsNotRhisFile_IsStillRefused()
    {
        var fx = CreateFixture("legacy-swap-foreign");
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);
        var interposer = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll");
        File.WriteAllBytes(interposer, Dlssg(0x6E));                   // replaced by the user
        File.WriteAllBytes(interposer + ".original", Dlssg(0x6F));     // and a backup that is not RHI's file

        var disabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");
        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.False(disabled.Success);
        Assert.Contains("modified outside RHI", disabled.Message);
        Assert.False(removed.Success);
        Assert.Equal(Dlssg(0x6E), File.ReadAllBytes(interposer));
        Assert.Equal(Dlssg(0x6F), File.ReadAllBytes(interposer + ".original"));
    }

    [Fact]
    public async Task DisableFgStack_PartialFailureSavesProgressAndARetryCompletes()
    {
        var fx = CreateFixture("partial-disable");
        var optiScaler = Path.Combine(fx.Bin, "OptiScaler");
        var dlssg = Path.Combine(optiScaler, "Streamline", "nvngx_dlssg.dll");
        var interposer = Path.Combine(optiScaler, "Streamline", "sl.interposer.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x7A));
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);

        // Longer paths go first: the Streamline files (incl. the kept dlssg) are handled, then
        // deleting OptiScaler/dlss-enabler-headless.dll fails in the read-only folder.
        ComponentOperationResult failed;
        using (ReadOnly(optiScaler))
            failed = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");

        Assert.False(failed.Success);
        Assert.Contains(DlssEnablerFile, failed.Message);
        Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(dlssg + ".original"));
        var saved = new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.DoesNotContain(saved.Files, file => file.Path == dlssg || file.Path == interposer);
        Assert.DoesNotContain(DlssgRelative, OwnedStackFiles(saved));
        Assert.DoesNotContain("OptiScaler/Streamline/sl.interposer.dll", OwnedStackFiles(saved));
        Assert.Contains("OptiScaler/" + DlssEnablerFile, OwnedStackFiles(saved));

        var retry = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");

        Assert.True(retry.Success, retry.Message);
        Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(Path.Combine(optiScaler, DlssEnablerFile)));
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("0", record.Metadata[OptiScalerDeploymentService.FgStackEnabledMetadataKey]);
        Assert.DoesNotContain(record.Files, file => file.Path.StartsWith(optiScaler + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveOptiScaler_PartialFailureSavesProgressAndARetryCompletes()
    {
        var fx = CreateFixture("partial-remove");
        var optiScaler = Path.Combine(fx.Bin, "OptiScaler");
        var dlssg = Path.Combine(optiScaler, "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x7B));
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);

        ComponentOperationResult failed;
        using (ReadOnly(optiScaler))
            failed = await fx.Service.RemoveAsync(fx.Game);

        Assert.False(failed.Success);
        Assert.Equal(Dlssg(0x7B), File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(dlssg + ".original"));
        var saved = new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.DoesNotContain(saved.Files, file => file.Path == dlssg);
        Assert.DoesNotContain(DlssgRelative, OwnedStackFiles(saved));

        var retry = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(retry.Success, retry.Message);
        Assert.Equal(Dlssg(0x7B), File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(Path.Combine(optiScaler, DlssEnablerFile)));
        Assert.Null(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task RemoveOptiScaler_RefusesToRestoreAnOriginalOverAKeptFileTheUserReplaced()
    {
        var fx = CreateFixture("remove-modified-kept");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x7A));
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);
        File.WriteAllBytes(dlssg, Dlssg(0x99)); // the user drops in another build
        var recordBefore = File.ReadAllText(fx.RecordsPath);

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.False(removed.Success);
        Assert.Contains("Protected modified file", removed.Message);
        Assert.Equal(Dlssg(0x99), File.ReadAllBytes(dlssg));
        Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(dlssg + ".original"));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile)));
        Assert.Equal(recordBefore, File.ReadAllText(fx.RecordsPath));
    }

    /// <summary>Makes <paramref name="directory"/> read-only (no entry can be deleted) until disposed.</summary>
    private static IDisposable ReadOnly(string directory)
    {
        var mode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = Path.Combine(directory, ".rhi-write-probe");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            File.SetUnixFileMode(directory, mode);
            throw new InvalidOperationException("Directory permissions are not enforced (running as root?); this test needs a normal user.");
        }
        catch (UnauthorizedAccessException) { }
        return new Restore(() => File.SetUnixFileMode(directory, mode));
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
