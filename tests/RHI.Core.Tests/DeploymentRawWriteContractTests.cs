using System.Text.RegularExpressions;

namespace RHI.Core.Tests;

/// <summary>
/// #31 (DEP-12) ratchet: deployment code writes, copies and moves files only through the toolkit (<c>AtomicFile</c>:
/// verified, durable, atomic; <c>DeploymentScope</c> for game files). A raw <c>File.Copy/Move/WriteAll*/AppendAll*</c>
/// copies without fsync or verification and, onto a game path, outside any journal. The files listed below still carry
/// raw calls owned by other lanes or tickets; every other deployment file must stay clean, and a listed file that has
/// been cleaned must leave the list.
/// </summary>
public sealed class DeploymentRawWriteContractTests
{
    private static readonly Regex RawCall = new(@"(?<!\w)File\.(Copy|Move|WriteAll\w*|AppendAll\w*)\(", RegexOptions.Compiled);

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
    {
        var dir = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Platform.Linux", "Deployment");
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (RawCall.IsMatch(code)) yield return (Path.GetFileName(path), i + 1, code);
            }
        }
    }

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
