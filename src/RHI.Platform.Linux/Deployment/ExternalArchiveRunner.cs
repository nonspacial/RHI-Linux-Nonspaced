using System.Diagnostics;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A host archive tool: 7-Zip (<c>7zz</c>/<c>7z</c>/<c>7za</c>) or bsdtar.</summary>
public sealed record ExternalArchiveTool(string Command, ExternalArchiveToolKind Kind);

public enum ExternalArchiveToolKind
{
    SevenZip,
    BsdTar
}

/// <summary>
/// The one way RHI extracts an archive with a host tool (DEP-S4, DEP-18 / #37): runs through
/// <see cref="ExternalProcess"/> (time limit, process-tree kill, closed stdin; a password goes through
/// stdin, never the command line) and then checks what the tool produced. 7-Zip and bsdtar refuse
/// absolute and <c>..</c> member paths and writing through links by default, but RHI does not rely on
/// that alone: after extraction every entry under the output folder is examined without following
/// links, and a symbolic link anywhere (file or folder, pointing in or out — the only way an entry can
/// resolve outside the folder) makes the whole extraction refused. The offending links are
/// removed (the link only, never its target) so nothing that points out of the folder is left behind;
/// the caller discards the output folder as it does for any other failure.
/// </summary>
public static class ExternalArchiveRunner
{
    /// <summary>Time limit for one extraction (the largest real payloads extract in seconds).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Fixed start of the "no host tool" refusal (PT-BR presentation pair key); the archive name follows.</summary>
    public const string NoToolPrefix = "This archive needs 7-Zip (7zz/7z/7za) or bsdtar, and neither is installed. Archive: ";

    /// <summary>Fixed start of the "only 7-Zip can do this" refusal (encrypted ZIP; PT-BR pair key); the archive name follows.</summary>
    public const string SevenZipRequiredPrefix = "This archive is encrypted and only 7-Zip (7zz/7z/7za) can extract it; install 7-Zip. Archive: ";

    /// <summary>Fixed start of an extraction the tool reported as failed (PT-BR pair key); the tool's last line follows.</summary>
    public const string FailedPrefix = "The archive tool could not extract the archive: ";

    /// <summary>Refusal detail (after <see cref="ReShadeArchiveExtractor.ArchiveRefusedPrefix"/>): a link was extracted.</summary>
    public const string SymlinkRefusal = "it contains a symbolic link: ";

    /// <summary>Host tool lookup; replaceable in tests through <see cref="FindToolForTesting"/>.</summary>
    public static ExternalArchiveTool? FindTool()
    {
        if (FindToolForTesting.Value is { } seam) return seam();
        if (ExternalProcess.FindOnPath("7zz", "7z", "7za") is { } sevenZip) return new ExternalArchiveTool(sevenZip, ExternalArchiveToolKind.SevenZip);
        if (ExternalProcess.FindOnPath("bsdtar") is { } bsdtar) return new ExternalArchiveTool(bsdtar, ExternalArchiveToolKind.BsdTar);
        return null;
    }

    /// <summary>Test seam: the tool <see cref="FindTool"/> returns in this flow (null result = none installed).</summary>
    internal static readonly AsyncLocal<Func<ExternalArchiveTool?>?> FindToolForTesting = new();

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="outputDirectory"/> (created if missing).
    /// <paramref name="members"/> limits extraction to those member names (7-Zip: flat, as <c>e</c>;
    /// bsdtar: as stored); null extracts everything with paths. <paramref name="password"/> is fed on stdin
    /// and needs 7-Zip. Throws on a missing tool, a failed or timed-out run, or a refused result
    /// (<see cref="InvalidDataException"/> starting with <see cref="ReShadeArchiveExtractor.ArchiveRefusedPrefix"/>).
    /// </summary>
    /// <param name="literalMembers">#216: <paramref name="members"/> are exact stored names, never wildcard patterns
    /// (7-Zip <c>-spd</c>; bsdtar: <c>\ * ? [</c> escaped), so a member named <c>renodx-*.addon64</c> extracts only itself.</param>
    public static async Task ExtractAsync(
        string archivePath,
        string outputDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? members = null,
        string? password = null,
        TimeSpan? timeout = null,
        ExternalArchiveTool? tool = null,
        bool literalMembers = false)
    {
        tool ??= FindTool() ?? throw new InvalidOperationException(NoToolPrefix + Path.GetFileName(archivePath));
        if (password is not null && tool.Kind != ExternalArchiveToolKind.SevenZip)
            throw new InvalidOperationException(SevenZipRequiredPrefix + Path.GetFileName(archivePath));

        Directory.CreateDirectory(outputDirectory);
        var startInfo = BuildStartInfo(tool, archivePath, outputDirectory, members, literalMembers);
        ExternalProcessResult result;
        try
        {
            result = await ExternalProcess.RunAsync(startInfo, timeout ?? DefaultTimeout, cancellationToken, password).ConfigureAwait(false);
        }
        catch
        {
            // A killed or failed tool may already have written a link: removed before the failure propagates. The
            // walk can fail itself (a folder the tool left unreadable); that is logged, never thrown in place of the
            // tool's own failure.
            try
            {
                VerifyContained(outputDirectory, removeLinks: true, throwOnRefusal: false);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                RHI.Core.Services.AppLog.Warn("external-archive", $"Could not check {outputDirectory} for links after {Path.GetFileName(tool.Command)} failed", cleanup);
            }
            throw;
        }
        // The link check comes first: a link is refused even when the tool reports success.
        VerifyContained(outputDirectory, removeLinks: true, throwOnRefusal: true);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{FailedPrefix}{Path.GetFileName(tool.Command)} ({result.ExitCode}): {result.LastMeaningfulLine()}");
    }

    internal static ProcessStartInfo BuildStartInfo(
        ExternalArchiveTool tool, string archivePath, string outputDirectory, IReadOnlyList<string>? members, bool literalMembers = false)
    {
        var startInfo = new ProcessStartInfo(tool.Command) { WorkingDirectory = outputDirectory };
        if (tool.Kind == ExternalArchiveToolKind.SevenZip)
        {
            startInfo.ArgumentList.Add(members is null ? "x" : "e");
            startInfo.ArgumentList.Add(archivePath);
            if (members is not null)
                foreach (var member in members) startInfo.ArgumentList.Add(member);
            startInfo.ArgumentList.Add($"-o{outputDirectory}");
            startInfo.ArgumentList.Add("-y");
            if (literalMembers) startInfo.ArgumentList.Add("-spd"); // no wildcard matching of the member names
            // No -p switch: a password prompt reads stdin, which carries the password or is closed (EOF).
        }
        else
        {
            startInfo.ArgumentList.Add("-xf");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(outputDirectory);
            if (members is not null)
                foreach (var member in members) startInfo.ArgumentList.Add(literalMembers ? LiteralBsdTarPattern(member) : member);
        }
        return startInfo;
    }

    /// <summary>A bsdtar member pattern that matches only <paramref name="member"/> itself (backslash first).</summary>
    internal static string LiteralBsdTarPattern(string member)
        => member.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>
    /// Examines every entry under <paramref name="outputDirectory"/> without following links. A symbolic link
    /// is the only way an extracted entry can resolve outside the folder (the walk never enters one, so every
    /// other entry it reaches is inside by construction); hard links cannot point at an absolute or <c>..</c>
    /// path because both tools refuse those member names. Returns the
    /// refusal detail (null when the tree is clean); throws it as an <see cref="InvalidDataException"/> when
    /// <paramref name="throwOnRefusal"/> is set. With <paramref name="removeLinks"/> every link found is unlinked.
    /// A folder the walk cannot read is never taken as clean (.NET's enumeration would skip it silently): its
    /// <see cref="UnauthorizedAccessException"/> propagates and the extraction fails.
    /// </summary>
    internal static string? VerifyContained(string outputDirectory, bool removeLinks, bool throwOnRefusal)
    {
        if (!Directory.Exists(outputDirectory)) return null;
        var root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string? refusal = null;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = false }))
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is not null)
                {
                    refusal ??= SymlinkRefusal + relative + " -> " + entry.LinkTarget;
                    if (removeLinks) RemoveLink(entry);
                    continue;
                }
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
            }
        }
        if (refusal is not null && throwOnRefusal)
            throw new InvalidDataException(ReShadeArchiveExtractor.ArchiveRefusedPrefix + refusal);
        return refusal;
    }

    private static void RemoveLink(FileSystemInfo link)
    {
        try
        {
            // Directory.Delete on a directory link removes the link itself, never the folder it points at.
            if (link is DirectoryInfo) Directory.Delete(link.FullName, recursive: false);
            else File.Delete(link.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("archive", $"An extracted symbolic link could not be removed: {link.FullName}", ex);
        }
    }
}
