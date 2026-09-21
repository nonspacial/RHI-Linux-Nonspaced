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

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
