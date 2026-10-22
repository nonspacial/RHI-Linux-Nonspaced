using RHI.Platform.Linux.Deployment;
using Xunit.Abstractions;

namespace RHI.Core.Tests;

/// <summary>
/// w10 follow-up D1: an archive member whose name starts with <c>-</c> or <c>@</c> (or is a 7-Zip switch form such as
/// <c>-x!…</c>) must never reach a host tool's command line as an option, a list file or a second output folder. Two
/// guards, each pinned on its own: the runner puts every switch before <c>--</c> (the tool reads the member as a name and
/// writes only under RHI's folder), and the listing check refuses such a name part before anything is extracted. Fixture
/// archives are small .7z files made by the host 7-Zip (<c>7z rn</c> gives the names); a test whose tools are missing
/// says so and returns.
/// </summary>
public sealed class ArchiveOptionLikeNameTests : IDisposable
{
    private const string Addon = "renodx-a.addon64";
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-option-names-" + Guid.NewGuid().ToString("N"));

    public ArchiveOptionLikeNameTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => DroppedArchiveExtractor.DeleteFolder(_root);

    /// <summary>A folder outside RHI's (bsdtar's <c>-C</c> needs it to exist, as any folder the user can write).</summary>
    private string Elsewhere => Path.Combine(_root, "elsewhere");

    /// <summary>A file 7-Zip would read as a list file naming the other member.</summary>
    private string ListFile => Path.Combine(_root, "lst", Addon);

    /// <summary>The member name for <paramref name="form"/>: <c>{E}</c> = <see cref="Elsewhere"/>, <c>{L}</c> = <see cref="ListFile"/>.</summary>
    private string Name(string form) => form.Replace("{E}", Elsewhere, StringComparison.Ordinal).Replace("{L}", ListFile, StringComparison.Ordinal);

    public static TheoryData<ExternalArchiveToolKind, string> Forms()
    {
        var rows = new TheoryData<ExternalArchiveToolKind, string>();
        foreach (var kind in new[] { ExternalArchiveToolKind.SevenZip, ExternalArchiveToolKind.BsdTar })
            foreach (var form in new[] { "-C{E}/" + Addon, "@{L}", "-x!other.bin/" + Addon, "-o{E}/" + Addon, "-i!other.bin/" + Addon })
                rows.Add(kind, form);
        return rows;
    }

    private (string Archive, byte[] AddonBytes, ExternalArchiveTool Tool)? Fixture(ExternalArchiveToolKind kind, string member)
    {
        if (DroppedArchiveExtractorTests.SevenZip() is not { } sevenZip || DroppedArchiveExtractorTests.BsdTar() is not { } bsdtar)
        {
            _output.WriteLine("SKIP: needs 7z and bsdtar on PATH");
            return null;
        }
        Directory.CreateDirectory(Path.Combine(Elsewhere, Addon)); // where bsdtar's "-C" would put everything
        Directory.CreateDirectory(Path.GetDirectoryName(ListFile)!);
        File.WriteAllText(ListFile, "other.bin\n");
        var addon = DroppedArchiveExtractorTests.AddonBytes();
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var archive = DroppedArchiveExtractorTests.SevenZipArchive(sevenZip, Path.Combine(downloads, "pack.7z"),
            (Addon, addon), ("other.bin", "other member, another size"u8.ToArray()));
        DroppedArchiveExtractorTests.Run(sevenZip, downloads, "rn", "-bd", archive, "--", Addon, member);
        return (archive, addon, new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind));
    }

    private string[] FilesUnder(string folder)
        => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray() : [];

    // The runner's guard: with the member after "--" the tool takes it as a name — exactly the add-on comes out, under
    // RHI's folder, with its own bytes; the folder outside stays empty and nothing else is written anywhere.
    [Theory]
    [MemberData(nameof(Forms))]
    public async Task TheTool_ReadsTheMemberAsAName_AndWritesOnlyUnderRhisFolder(ExternalArchiveToolKind kind, string form)
    {
        if (Fixture(kind, Name(form)) is not { } fixture) return;
        var raw = Path.Combine(_root, "rhi", "raw");
        var outside = FilesUnder(_root).ToHashSet(StringComparer.Ordinal);

        await ExternalArchiveRunner.ExtractAsync(fixture.Archive, raw, CancellationToken.None, members: [Name(form)], tool: fixture.Tool,
            literalMembers: true, limit: new ExtractionLimit(fixture.AddonBytes.Length, fixture.AddonBytes.Length));

        var only = Assert.Single(FilesUnder(raw));
        Assert.Equal(Addon, Path.GetFileName(only));
        Assert.Equal(fixture.AddonBytes, File.ReadAllBytes(only));
        Assert.Empty(FilesUnder(Elsewhere));
        Assert.Equal(outside.Order(StringComparer.Ordinal), FilesUnder(_root).Where(f => !f.StartsWith(raw + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    // The listing check's guard: such an archive is refused when it is read, before any tool extracts anything.
    [Theory]
    [MemberData(nameof(Forms))]
    public async Task SuchAnArchive_IsRefusedBeforeAnythingIsExtracted_AndTheFolderOutsideStaysEmpty(ExternalArchiveToolKind kind, string form)
    {
        if (Fixture(kind, Name(form)) is not { } fixture) return;
        var scratch = Path.Combine(_root, "cache", "dropped-archives");
        ExternalArchiveRunner.FindToolForTesting.Value = () => fixture.Tool;
        DroppedArchiveInspection inspection;
        try
        {
            inspection = await DroppedArchiveExtractor.InspectAsync(fixture.Archive, scratch);
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }
        inspection.Archive?.Dispose();
        Assert.Null(inspection.Archive);
        Assert.StartsWith(DroppedArchiveExtractor.OptionLikeRefusal, inspection.Refusal, StringComparison.Ordinal);
        Assert.Empty(FilesUnder(Elsewhere));
        Assert.Empty(FilesUnder(scratch));
    }

    [Theory]
    [InlineData("-x", true)]
    [InlineData("docs/-notes.txt", true)]       // any part, not only the first
    [InlineData("@list", true)]
    [InlineData("a\\@b\\renodx-a.addon64", true)] // a backslash name's parts too
    [InlineData("a-b/c@d.txt", false)]          // inside a part: no option
    [InlineData("renodx-a.addon64", false)]
    public void ANamePart_StartingWithADashOrAnAt_IsRefused(string name, bool refused)
        => Assert.Equal(refused ? DroppedArchiveExtractor.OptionLikeRefusal : null, DroppedArchiveExtractor.Normalize(name).Refusal);

    // D3: extraction runs in the listing's locale (a UTF-8 name it listed is written, never skipped), and every switch
    // comes before "--" for both tools.
    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip)]
    [InlineData(ExternalArchiveToolKind.BsdTar)]
    public void TheToolRuns_InTheListingsLocale_WithEverySwitchBeforeTheEndOfOptions(ExternalArchiveToolKind kind)
    {
        var start = ExternalArchiveRunner.BuildStartInfo(new ExternalArchiveTool("/bin/true", kind), "/a/pack.7z", "/out", ["-x!m"], literalMembers: true);
        Assert.Equal("C.UTF-8", start.Environment["LC_ALL"]);
        var arguments = start.ArgumentList.ToArray();
        var end = Array.IndexOf(arguments, "--");
        Assert.True(end > 0, string.Join(' ', arguments));
        Assert.Equal("-x!m", arguments[^1]);
        Assert.All(arguments[(end + 1)..], argument => Assert.DoesNotContain(argument, new[] { "-y", "-spd", "-o/out", "-C", "-xf" }));
    }
}
