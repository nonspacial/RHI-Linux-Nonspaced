using System.Globalization;
using System.Text.RegularExpressions;

namespace RHI.Core.Tests;

// Wave-10 integration (#222 item 4): the docs match the build. The CHANGELOG shipped as What's new keeps every line within
// 120 columns and its wave entries newest first; the README's Safety text no longer promises the landed note "wherever the
// line comes from" or the old "before the game's next change, or at the next start" wording, and says what the holds say.
public sealed partial class WaveTenIntegrationTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "validate.sh")) && File.Exists(Path.Combine(dir.FullName, name)))
                return Path.Combine(dir.FullName, name);
        throw new FileNotFoundException("not found above the test binaries", name);
    }

    [Fact]
    public void TheChangelog_KeepsEveryLineWithin120Columns_AndItsWavesNewestFirst()
    {
        var text = RHI.Desktop.Views.ChangelogText.Load();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        Assert.DoesNotContain(lines.Select((line, i) => (Number: i + 1, line.Length)), l => l.Length > 120);
        var waves = lines.Select(line => Regex.Match(line, @"^## Wave (\d+) — (\d{4}-\d{2}-\d{2})")).Where(m => m.Success)
            .Select(m => (Wave: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Date: m.Groups[2].Value)).ToList();
        Assert.True(waves.Count >= 10);
        Assert.Equal(waves.OrderByDescending(w => w.Wave).ToList(), waves);
        Assert.Equal(waves.OrderByDescending(w => w.Date, StringComparer.Ordinal).Select(w => w.Date).ToList(), waves.Select(w => w.Date).ToList());
        Assert.Equal(text, File.ReadAllText(RepoFile("CHANGELOG.md"))); // the embedded copy is this tree's file
    }

    [Fact]
    public void TheReadme_SafetyText_SaysWhatTheHoldsSay_AndNoLongerOverclaims()
    {
        var readme = File.ReadAllText(RepoFile("README.md"));
        var safety = readme[readme.IndexOf("## Safety", StringComparison.Ordinal)..readme.IndexOf("## Platforms & launchers", StringComparison.Ordinal)];

        Assert.DoesNotContain("wherever the line comes from", safety, StringComparison.Ordinal);
        Assert.DoesNotContain("before the game's next change, or at the next start", safety, StringComparison.Ordinal);
        Assert.Contains("that game is held", safety, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(safety, "the message says to restart RHI to finish it").Count);
        Assert.Contains("a preset installed into every game with its shader packs", safety, StringComparison.Ordinal);
    }
}
