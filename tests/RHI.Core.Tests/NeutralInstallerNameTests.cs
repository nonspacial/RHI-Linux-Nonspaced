using System.Text.RegularExpressions;
using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// Owner decision: the Xbox installer app is a private app; RHI's own text, code and tests never name it. Its name lives in
/// one functional constant (<see cref="XboxInstallerApp"/>), which is how that app spells its own files and folders.
/// </summary>
public sealed class NeutralInstallerNameTests
{
    [Fact]
    public void SourcesAndTests_NameTheInstallerApp_OnlyInItsOneConstant()
    {
        var root = LocalizationKeyUsageTests.RepoRoot();
        var name = new Regex(Regex.Escape(XboxInstallerApp.ProductTag), RegexOptions.IgnoreCase);
        var hits = new[] { "src", "tests" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           Path.GetExtension(file) is ".cs" or ".axaml" or ".json" or ".sql" or ".xml" or ".md" or ".resx")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, n: i + 1)))
            .Where(hit => name.IsMatch(hit.line))
            .Select(hit => $"{Path.GetRelativePath(root, hit.file)}:{hit.n}")
            .ToList();

        var only = Assert.Single(hits);
        Assert.StartsWith(Path.Combine("src", "RHI.Core", "Services", "XboxInstallerApp.cs") + ":", only);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void UiTexts_NeverNameTheInstallerApp(string language)
    {
        foreach (var key in UiLocalizationService.Keys)
            Assert.DoesNotContain(XboxInstallerApp.ProductTag, UiLocalizationService.TextFor(language, key), StringComparison.OrdinalIgnoreCase);
    }
}
