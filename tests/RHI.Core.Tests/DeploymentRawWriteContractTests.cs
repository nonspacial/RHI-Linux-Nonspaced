using System.Text.RegularExpressions;

namespace RHI.Core.Tests;

/// <summary>
/// #31 (DEP-12) ratchet over the source of <c>src/RHI.Platform.Linux/Deployment</c> (every sub-folder): it refuses the
/// raw calls that copy, move, replace or write a whole file without the toolkit's fsync and verification (and, on a game
/// path, outside any journal):
/// <list type="bullet">
/// <item><c>File.Copy/Move/Replace/WriteAll*/AppendAll*(</c>, also qualified (<c>System.IO.File.</c>, <c>global::</c>)
/// and split across lines or spaces;</item>
/// <item><c>FileInfo.CopyTo/MoveTo(</c> on a <c>new FileInfo(…)</c> or on a variable declared as one;</item>
/// <item><c>using static System.IO.File;</c> and aliases of <c>System.IO.File</c>/<c>FileInfo</c>, which would hide the
/// calls above.</item>
/// </list>
/// Line comments are skipped; anything else (code in strings, block comments) counts, so the scan fails closed. It does
/// not see <c>FileStream</c>/<c>FileMode</c> writes or <c>Directory.Move</c>. The files listed below still carry raw calls
/// owned by other lanes or tickets; every other deployment file must stay clean, and a listed file that has been cleaned
/// must leave the list.
/// </summary>
public sealed class DeploymentRawWriteContractTests
{
    private static readonly Regex RawCall = new(@"(?<!\w)File\s*\.\s*(Copy|Move|Replace|WriteAll\w*|AppendAll\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex NewFileInfoCall = new(@"(?<!\w)FileInfo\s*\([^;]*?\)\s*\.\s*(CopyTo|MoveTo)\s*\(", RegexOptions.Compiled);
    private static readonly Regex FileInfoVariable = new(
        @"(?<!\w)FileInfo\s+(\w+)\s*=|var\s+(\w+)\s*=\s*new\s+(?:global::)?(?:System\.IO\.)?FileInfo\s*\(", RegexOptions.Compiled);
    private static readonly Regex HiddenFile = new(@"using\s+(?:static\s+(?:global::)?System\.IO\.File|\w+\s*=\s*(?:global::)?System\.IO\.File(?:Info)?)\s*;", RegexOptions.Compiled);

    // Not yet converted (owner in brackets). Remove a file once its raw calls are gone.
    private static readonly HashSet<string> NotYetConverted = new(StringComparer.Ordinal)
    {
        "ComponentUpdateLog.cs",                       // update-history store, DEP-01 handling
        "OptiScalerDeploymentService.cs",              // w3/dllnames phase B
        "OptiScalerDeploymentService.Streamline.cs",   // w3/dllnames phase B
        "RenoDxUnityDatabaseService.cs",               // catalogue cache
        "ReShadeDeploymentService.cs",                 // w3/dllnames
        "ReShadeIniManager.cs",                        // w3/dllnames
    };

    private static IEnumerable<(string File, int Line, string Text)> RawCalls()
        => RawCallsUnder(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Platform.Linux", "Deployment"));

    internal static IEnumerable<(string File, int Line, string Text)> RawCallsUnder(string dir)
    {
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            foreach (var call in RawCallsIn(File.ReadAllText(path)))
                yield return (Path.GetFileName(path), call.Line, call.Text);
    }

    internal static IEnumerable<(int Line, string Text)> RawCallsIn(string source)
    {
        // Line comments become blank lines (line numbers stay right); everything else is scanned as one text.
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line).ToArray();
        var code = string.Join('\n', lines);
        var matches = RawCall.Matches(code).Concat(NewFileInfoCall.Matches(code)).Concat(HiddenFile.Matches(code)).ToList();
        var fileInfos = FileInfoVariable.Matches(code)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Distinct(StringComparer.Ordinal);
        foreach (var name in fileInfos)
            matches.AddRange(new Regex($@"(?<![\w.]){Regex.Escape(name)}\s*\.\s*(CopyTo|MoveTo)\s*\(").Matches(code));
        foreach (var match in matches.OrderBy(m => m.Index))
        {
            var line = code.AsSpan(0, match.Index).Count('\n');
            yield return (line + 1, lines[line].Trim());
        }
    }

    // Journal re-review nit 2: shapes the old line-by-line `File.X(` scan could not see.
    [Theory]
    [InlineData("File.Copy(a, b);")]
    [InlineData("System.IO.File.Move(a, b);")]
    [InlineData("global::System.IO.File.WriteAllBytes(a, b);")]
    [InlineData("File.Replace(a, b, null);")]
    [InlineData("File\n    .Copy(a, b);")]
    [InlineData("File.Copy\n(a, b);")]
    [InlineData("new FileInfo(a).CopyTo(b);")]
    [InlineData("new System.IO.FileInfo(a).MoveTo(b);")]
    [InlineData("var info = new FileInfo(a);\ninfo.MoveTo(b);")]
    [InlineData("FileInfo info = new(a);\ninfo.CopyTo(b, true);")]
    [InlineData("using static System.IO.File;")]
    [InlineData("using F = System.IO.File;")]
    [InlineData("using FI = global::System.IO.FileInfo;")]
    public void RawShapes_AreCaught(string source)
        => Assert.NotEmpty(RawCallsIn(source));

    [Fact]
    public void FilesInSubFolders_AreScannedToo()
    {
        var dir = Directory.CreateTempSubdirectory("rhi-raw-write-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Sub"));
            File.WriteAllText(Path.Combine(dir, "Sub", "Nested.cs"), "class C { void M() { File.Copy(a, b); } }");
            Assert.Equal("Nested.cs", Assert.Single(RawCallsUnder(dir)).File);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("AtomicFile.Copy(a, b);")]
    [InlineData("AtomicFile.Replace(a, b);")]
    [InlineData("// File.Copy(a, b);")]
    [InlineData("stream.CopyTo(other);")]
    [InlineData("using var download = await TrustedDownload.DownloadToTempAsync(x);\ndownload.MoveTo(target);")]
    [InlineData("var length = new FileInfo(a).Length;")]
    [InlineData("using System.IO;")]
    public void ToolkitAndReadOnlyShapes_AreNotFlagged(string source)
        => Assert.Empty(RawCallsIn(source));

    [Fact]
    public void DeploymentFiles_UseTheToolkitForEveryCopyMoveAndWrite()
    {
        var offending = RawCalls().Where(call => !NotYetConverted.Contains(call.File))
            .Select(call => $"{call.File}:{call.Line}: {call.Text}").ToArray();
        Assert.True(offending.Length == 0, "Raw file writes outside the toolkit:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void TheNotYetConvertedList_HoldsOnlyFilesThatStillNeedIt()
    {
        var stillRaw = RawCalls().Select(call => call.File).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(NotYetConverted, file => !stillRaw.Contains(file));
    }
}
