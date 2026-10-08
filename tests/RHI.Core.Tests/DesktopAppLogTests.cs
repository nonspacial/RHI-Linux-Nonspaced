using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (ui-build, incidental): recovered failures in the desktop code go to the one app log, not to
/// <c>Debug.WriteLine</c> (which a release build drops, so the automatic NR and latest-version refresh failures left no
/// trace).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DesktopAppLogTests
{
    [Fact]
    public async Task AutomaticNrVersionRefreshFailure_GoesToTheAppLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rhi-nrlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var log = new AppLog(Path.Combine(dir, "rhi.log"));
            ViewModelFixture fx;
            using (AppLog.UseInThisFlow(log))
                fx = await ViewModelFixture.StartAsync([]);
            using (fx)
            {
                var text = "";
                for (var i = 0; i < 100 && !text.Contains("[nr-versions]", StringComparison.Ordinal); i++)
                {
                    await Task.Delay(50);
                    text = File.Exists(log.FilePath) ? File.ReadAllText(log.FilePath) : "";
                }

                Assert.Contains("[nr-versions] Automatic NR version catalog refresh failed", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DesktopCode_LogsRecoveredFailuresToTheAppLog_NotToDebugOutput()
    {
        var desktop = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop");
        var offenders = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Debug.WriteLine", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

}
