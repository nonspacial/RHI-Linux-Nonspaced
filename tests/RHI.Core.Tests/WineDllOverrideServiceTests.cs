using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class WineDllOverrideServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-wine-reg-tests-" + Guid.NewGuid().ToString("N"));
    public WineDllOverrideServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EnsureAndRestore_PreservesPreviousOverride()
    {
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\AppDefaults\\\\Game.exe\\\\DllOverrides] 1\n#time=1\n\"dxgi\"=\"builtin\"\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        Assert.True(record.ChangedByRhi);
        Assert.Equal("builtin", record.PreviousValue);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));

        service.Restore(prefix, record);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));
    }

    [Fact]
    public void Ensure_AddsSectionWhenMissing_AndRestoreRemovesRhiCreatedSection()
    {
        var prefix = Path.Combine(_root, "pfx2");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "d3d9.dll", "OldGame.exe");
        Assert.True(record.SectionCreatedByRhi);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "d3d9", "OldGame.exe"));
        service.Restore(prefix, record);
        var restored = File.ReadAllText(reg);
        Assert.Null(WineDllOverrideService.TryReadOverride(restored, "d3d9", "OldGame.exe"));
        Assert.DoesNotContain(@"[Software\\Wine\\AppDefaults\\OldGame.exe\\DllOverrides]", restored, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"x\"=\"y\"", restored);
    }


    [Fact]
    public void Restore_ExistingEmptySection_PreservesSection()
    {
        var prefix = Path.Combine(_root, "pfx3");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        const string section = @"[Software\\Wine\\AppDefaults\\Existing.exe\\DllOverrides]";
        File.WriteAllText(reg, $"WINE REGISTRY Version 2\n\n{section} 1\n#time=1\n\n[Software\\Wine\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Existing.exe");
        Assert.False(record.SectionCreatedByRhi);
        service.Restore(prefix, record);

        var restored = File.ReadAllText(reg);
        Assert.Contains(section, restored, StringComparison.OrdinalIgnoreCase);
        Assert.Null(WineDllOverrideService.TryReadOverride(restored, "dxgi", "Existing.exe"));
    }

    [Fact]
    public void Restore_RhiCreatedSectionWithThirdPartyValue_PreservesSectionAndThirdPartyValue()
    {
        var prefix = Path.Combine(_root, "pfx4");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        const string section = @"[Software\\Wine\\AppDefaults\\Shared.exe\\DllOverrides]";
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\Wine\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Shared.exe");
        Assert.True(record.SectionCreatedByRhi);

        var text = File.ReadAllText(reg);
        text = WineDllOverrideService.SetOverride(text, "xinput1_3.dll", "native,builtin", "Shared.exe");
        File.WriteAllText(reg, text);

        service.Restore(prefix, record);
        var restored = File.ReadAllText(reg);
        Assert.Contains(section, restored, StringComparison.OrdinalIgnoreCase);
        Assert.Null(WineDllOverrideService.TryReadOverride(restored, "dxgi", "Shared.exe"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(restored, "xinput1_3", "Shared.exe"));
    }

    [Fact]
    public void Restore_LegacyRecordWithoutOwnershipFlag_DoesNotDeleteSection()
    {
        var prefix = Path.Combine(_root, "pfx5");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        const string section = @"[Software\\Wine\\AppDefaults\\Legacy.exe\\DllOverrides]";
        File.WriteAllText(reg, $"WINE REGISTRY Version 2\n\n{section} 1\n#time=1\n\"dxgi\"=\"native,builtin\"\n");
        var service = new WineDllOverrideService();
        var legacyRecord = new WineOverrideRecord
        {
            DllName = "dxgi",
            PreviousValue = null,
            ChangedByRhi = true,
            ApplicationName = "Legacy.exe"
            // SectionCreatedByRhi defaults false for legacy records deserialized after upgrade.
        };

        service.Restore(prefix, legacyRecord);
        var restored = File.ReadAllText(reg);
        Assert.Contains(section, restored, StringComparison.OrdinalIgnoreCase);
        Assert.Null(WineDllOverrideService.TryReadOverride(restored, "dxgi", "Legacy.exe"));
    }

    // #30 (DEP-11): restore acts only while the override still holds the value RHI wrote.
    [Fact]
    public void Restore_AfterTheUserChangedTheOverride_KeepsTheUsersValue()
    {
        var prefix = Path.Combine(_root, "pfx-user");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();
        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        Assert.Equal(WineDllOverrideService.RhiValue, record.WrittenValue);
        // The user (winecfg, protontricks) sets their own value after RHI's install.
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "dxgi", "builtin", "Game.exe"));

        var result = service.Restore(prefix, record);

        Assert.Equal(WineOverrideRestoreOutcome.KeptLaterValue, result.Outcome);
        Assert.Equal("builtin", result.CurrentValue);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));
        Assert.Equal(" RHI left this Wine override as it is, because it was changed after RHI set it: dxgi = 'builtin'.",
            WineDllOverrideService.KeptValueNotes(result));
    }

    [Fact]
    public void Restore_AfterTheUserDeletedTheOverride_DoesNotPutTheEarlierValueBack()
    {
        var prefix = Path.Combine(_root, "pfx-deleted");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1\n\"dxgi\"=\"builtin\"\n");
        var service = new WineDllOverrideService();
        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll");
        File.WriteAllText(reg, WineDllOverrideService.RemoveOverride(File.ReadAllText(reg), "dxgi"));

        Assert.Equal(WineOverrideRestoreOutcome.KeptLaterValue, service.Restore(prefix, record).Outcome);
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi"));
    }

    [Fact]
    public void Restore_IsIdempotent_AndALegacyRecordMeansNativeBuiltin()
    {
        var prefix = Path.Combine(_root, "pfx-idem");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\AppDefaults\\\\Game.exe\\\\DllOverrides] 1\n\"dxgi\"=\"native,builtin\"\n");
        var service = new WineDllOverrideService();
        // Written before WrittenValue existed: RHI only ever wrote native,builtin.
        var legacy = new WineOverrideRecord { DllName = "dxgi", PreviousValue = "builtin", ChangedByRhi = true, ApplicationName = "Game.exe" };

        Assert.Equal(WineOverrideRestoreOutcome.Restored, service.Restore(prefix, legacy).Outcome);
        var afterFirst = File.ReadAllBytes(reg);
        Assert.Equal(WineOverrideRestoreOutcome.AlreadyRestored, service.Restore(prefix, legacy).Outcome);

        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));
        Assert.Equal(afterFirst, File.ReadAllBytes(reg));
    }

    [Fact]
    public void Reapply_PutsRhisValueBackOnlyWhereTheRestoreLeftTheEarlierValue()
    {
        var prefix = Path.Combine(_root, "pfx-reapply");
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, "WINE REGISTRY Version 2\n\n[Software\\\\Other] 1\n\"x\"=\"y\"\n");
        var service = new WineDllOverrideService();
        var record = service.EnsureNativeBuiltin(prefix, "winmm.dll", "Game.exe");
        service.Restore(prefix, record);

        Assert.Equal(WineOverrideRestoreOutcome.Restored, service.Reapply(prefix, record).Outcome);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "winmm", "Game.exe"));
        Assert.Equal(WineOverrideRestoreOutcome.AlreadyRestored, service.Reapply(prefix, record).Outcome);

        // Someone set their own value after the restore: a rollback must not overwrite it.
        service.Restore(prefix, record);
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "winmm", "disabled", "Game.exe"));
        Assert.Equal(WineOverrideRestoreOutcome.KeptLaterValue, service.Reapply(prefix, record).Outcome);
        Assert.Equal("disabled", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "winmm", "Game.exe"));
    }

    // w2 wine review, nit 8: a value Wine wrapped over several lines (trailing backslash) is replaced or removed whole.
    [Fact]
    public void SetAndRemove_AMultiLineValue_LeaveNoOrphanedContinuationLines()
    {
        const string section = "[Software\\\\Wine\\\\AppDefaults\\\\Game.exe\\\\DllOverrides] 1\n";
        var hive = "WINE REGISTRY Version 2\n\n" + section + "\"dxgi\"=hex(2):6e,00,61,00,\\\n  74,00,69,00,00,00\n\"x\"=\"y\"\n";

        var set = WineDllOverrideService.SetOverride(hive, "dxgi", "native,builtin", "Game.exe");
        var removed = WineDllOverrideService.RemoveOverride(hive, "dxgi", "Game.exe");

        Assert.Equal("WINE REGISTRY Version 2\n\n" + section + "\"dxgi\"=\"native,builtin\"\n\"x\"=\"y\"\n", set);
        Assert.Equal("WINE REGISTRY Version 2\n\n" + section + "\"x\"=\"y\"\n", removed);
    }

    // w2 wine review, nit 8: create + remove of a section gives back the exact bytes, final newline or not, and a value
    // added to the hive's last section goes after its last value (no blank line, final newline kept).
    [Theory]
    [InlineData("WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"")]
    [InlineData("WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n")]
    [InlineData("WINE REGISTRY Version 2\r\n\r\n[Software\\\\Wine\\\\Other] 1\r\n\"x\"=\"y\"")]
    public void EnsureThenRestore_GivesBackTheExactHive_WithOrWithoutAFinalNewline(string original)
    {
        var prefix = Path.Combine(_root, "pfx-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefix);
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, original);
        var service = new WineDllOverrideService();

        var created = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        service.Restore(prefix, created);
        Assert.Equal(original, File.ReadAllText(reg));

        var global = service.EnsureNativeBuiltin(prefix, "d3d9.dll");
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "d3d9"));
        service.Restore(prefix, global);
        Assert.Equal(original, File.ReadAllText(reg));
    }

    [Fact]
    public void NewValueInTheHivesLastSection_GoesAfterItsLastValue_KeepingTheFinalNewline()
    {
        const string hive = "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1\n\"x\"=\"builtin\"\n";

        var set = WineDllOverrideService.SetOverride(hive, "dxgi", "native,builtin");

        Assert.Equal("WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1\n\"x\"=\"builtin\"\n\"dxgi\"=\"native,builtin\"\n", set);
        Assert.Equal(hive, WineDllOverrideService.RemoveOverride(set, "dxgi"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
