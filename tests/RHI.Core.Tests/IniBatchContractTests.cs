using System.Text.RegularExpressions;

namespace RHI.Core.Tests;

/// <summary>
/// w5/ini-presets review nit 10: the INI batches await each game's work; none blocks a pool thread on a task
/// (<c>.GetAwaiter().GetResult()</c>, <c>.Result</c>, <c>.Wait()</c>) in the services that run inside them.
/// </summary>
public sealed class IniBatchContractTests
{
    private static readonly Regex Blocking = new(@"\.GetAwaiter\(\)\s*\.GetResult\(\)|\.Wait\(\)|\)\s*\.Result\b", RegexOptions.Compiled);

    [Theory]
    [InlineData("UserIniService.cs")]
    [InlineData("RenoDxPeakNits.cs")]
    [InlineData("ReShadePresetService.cs")]
    [InlineData("LumaSettingsService.cs")]
    public void BatchServices_NeverBlockOnATask(string file)
    {
        var path = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Platform.Linux", "Deployment", file);
        var hits = File.ReadAllLines(path).Select((line, i) => (line, i)).Where(x => Blocking.IsMatch(x.line)).Select(x => $"{file}:{x.i + 1}: {x.line.Trim()}").ToList();
        Assert.Empty(hits);
    }
}
