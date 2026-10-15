using System.Diagnostics;
using System.Text.RegularExpressions;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// Review m5 (#79): every localisation key the desktop code or XAML asks for exists in the tables the service serves
/// (checked through <see cref="UiLocalizationService.Keys"/>, not by reading the table source), and
/// <c>tools/l10n-migrate.py</c> refuses to rewrite a key table that has rows it cannot read back, instead of dropping
/// them so that <c>T("key")</c> shows the key name at runtime.
/// </summary>
public sealed class LocalizationKeyUsageTests
{
    // Key literals as the code writes them: T("Vm.X"), TF(...), Text/Format(...), and keys handed on as arguments
    // (TryPersist(..., "Ui.WhatGameLibrary")). The prefixes come from the tables themselves, so a new key family
    // (Scan.*) is checked as soon as it exists.
    private static readonly Regex KeyLiteral = new("\"((?:" + string.Join("|", KeyPrefixes()) + ")\\.[A-Za-z0-9_]+)\"");

    private static IEnumerable<string> KeyPrefixes()
        => UiLocalizationService.Keys.Where(k => k.Contains('.')).Select(k => k[..k.IndexOf('.')]).Distinct(StringComparer.Ordinal)
            .Select(Regex.Escape);

    [Fact]
    public void KeyPattern_CoversEveryKeyFamily()
    {
        foreach (var family in new[] { "Vm", "Win", "Ui", "Hist", "Key", "Scan" })
            Assert.Matches(KeyLiteral, $"T(\"{family}.Anything\")");
    }
    private static readonly Regex XamlResource = new(@"DynamicResource\s+([A-Za-z0-9_.]+)");
    // {l:T Win.Key} (TExtension): dialog texts read once when the dialog opens.
    private static readonly Regex XamlText = new(@"\{l:T\s+(?:Key=)?([A-Za-z0-9_.]+)\s*\}");

    [Fact]
    public void XamlTextPattern_FindsTheMarkupExtensionKeys()
    {
        Assert.Equal("Win.Close", XamlText.Match("Content=\"{l:T Win.Close}\"").Groups[1].Value);
        Assert.Equal("Win.Close", XamlText.Match("Text=\"{l:T Key=Win.Close}\"").Groups[1].Value);
    }
    private static readonly Regex XamlDefinedKey = new("x:Key=\"([^\"]+)\"");

    [Fact]
    public void EveryKeyUsedInCodeOrXaml_ExistsInTheTables()
    {
        var desktop = Path.Combine(RepoRoot(), "src", "RHI.Desktop");
        var known = UiLocalizationService.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        var code = SourceFiles(desktop, "*.cs").Where(f => !f.EndsWith("UiStrings.cs", StringComparison.Ordinal)).ToList();
        foreach (var file in code)
            foreach (Match m in KeyLiteral.Matches(File.ReadAllText(file)))
                if (!known.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");

        var xaml = SourceFiles(desktop, "*.axaml").ToList();
        var styleKeys = xaml.SelectMany(f => XamlDefinedKey.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in xaml)
            foreach (Match m in XamlResource.Matches(File.ReadAllText(file)))
                if (!known.Contains(m.Groups[1].Value) && !styleKeys.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        var xamlTexts = 0;
        foreach (var file in xaml)
            foreach (Match m in XamlText.Matches(File.ReadAllText(file)))
            {
                xamlTexts++;
                if (!known.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            }

        Assert.True(xamlTexts > 0, "no {l:T …} texts found in the AXAML");
        Assert.True(code.Count > 10 && xaml.Count > 0, "desktop sources not found");
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryKeyInTheTables_IsUsedByCodeOrXaml()
    {
        // The reverse check: a row nothing asks for is dead text that still has to be translated and reviewed.
        var desktop = Path.Combine(RepoRoot(), "src", "RHI.Desktop");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(desktop, "*.cs").Concat(SourceFiles(desktop, "*.axaml")))
        {
            if (file.EndsWith("UiStrings.cs", StringComparison.Ordinal)) continue;
            var text = TableDefinition.Replace(File.ReadAllText(file), "");
            foreach (Match m in KeyToken.Matches(text)) used.Add(m.Value);
        }

        Assert.Empty(UiLocalizationService.Keys.Where(k => k.Contains('.') && !used.Contains(k)).Order(StringComparer.Ordinal));
    }

    private static readonly Regex TableDefinition = new("\\[\"[A-Za-z]+\\.[A-Za-z0-9_]+\"\\]\\s*=");
    private static readonly Regex KeyToken = new("(?<![A-Za-z0-9_.])[A-Za-z]+\\.[A-Za-z0-9_]+(?![A-Za-z0-9_])");

    [Fact]
    public void MigrateScript_RoundTripsTheTable_ByteForByte()
    {
        using var copy = TableCopy.Create();
        var (exit, output) = copy.Run();
        Assert.True(exit == 0, output);
        Assert.Equal(copy.Original, File.ReadAllText(copy.TablePath));
    }

    [Theory]
    [InlineData("(\"Hand.Raw\", \"\"\"Cru\"\"\", \"\"\"Raw\"\"\"),")]
    [InlineData("(Consts.HandKey, \"Cru\", \"Raw\"),")]
    [InlineData("(\"Hand.Escape\", \"a\\qb\", \"a\\qb\"),")]
    [InlineData("(\"Hist.Today\", \"Hoje\", \"Today\"),")]
    public void MigrateScript_RowItCannotRead_AbortsAndWritesNothing(string row)
    {
        using var copy = TableCopy.Create();
        var edited = copy.Original.Replace("    [\n", "    [\n        " + row + "\n", StringComparison.Ordinal);
        Assert.NotEqual(copy.Original, edited);
        File.WriteAllText(copy.TablePath, edited);

        var (exit, output) = copy.Run();

        Assert.Equal(2, exit);
        Assert.Contains("ABORTED", output);
        Assert.Equal(edited, File.ReadAllText(copy.TablePath));
    }

    private static IEnumerable<string> SourceFiles(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "l10n-migrate.py"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A throwaway repo layout holding a copy of the two files the script reads.</summary>
    private sealed class TableCopy : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-l10n-{Guid.NewGuid():N}");

        private TableCopy() { }

        public string TablePath => Path.Combine(_root, "src", "RHI.Desktop", "Localization", "UiStrings.cs");
        public string Original { get; private set; } = "";

        public static TableCopy Create()
        {
            var copy = new TableCopy();
            var repo = RepoRoot();
            var localization = Path.Combine(copy._root, "src", "RHI.Desktop", "Localization");
            Directory.CreateDirectory(localization);
            foreach (var name in new[] { "UiStrings.cs", "UiLocalizationService.cs" })
                File.Copy(Path.Combine(repo, "src", "RHI.Desktop", "Localization", name), Path.Combine(localization, name));
            copy.Original = File.ReadAllText(copy.TablePath);
            return copy;
        }

        public (int Exit, string Output) Run()
        {
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(RepoRoot(), "tools", "l10n-migrate.py"));
            start.ArgumentList.Add(_root);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "l10n-migrate.py did not finish");
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
