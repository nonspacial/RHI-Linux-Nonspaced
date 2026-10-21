using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Ux;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#216: what an add-on found inside a dropped or watched archive is installed as.</summary>
public enum DroppedArchiveAddonKind
{
    /// <summary>A RenoDX game add-on: the game's RenoDX mod (<c>InstallLocalRenoDxAddonAsync</c>).</summary>
    RenoDx,
    /// <summary>Another <c>renodx-*</c> add-on upstream's drop installs beside it (<c>InstallDroppedAddonForGameAsync</c>).</summary>
    GameAddon,
    /// <summary>A Luma add-on: the whole archive goes to the Luma package install (a <c>.zip</c> only).</summary>
    Luma,
}

/// <summary>#216: one add-on inside an archive: its stored name, the file name it installs under, its listed size.</summary>
public sealed record DroppedArchiveAddon(string Member, string FileName, long Size, DroppedArchiveAddonKind Kind);

/// <summary>One archive member as the listing gives it (<paramref name="Special"/>: why it is not a plain file or folder).</summary>
internal sealed record DroppedArchiveMember(string Name, long? Size, bool IsDirectory, string? Special = null);

/// <summary>An inspected archive, or why RHI refused to open it (nothing was extracted; no folder is left).</summary>
public sealed record DroppedArchiveInspection(DroppedArchive? Archive, string? Refusal);

/// <summary>
/// #216: a dropped or watched archive RHI has read and checked. It owns a private, mode-0700 folder under RHI's cache
/// holding RHI's own copy of the archive (the bytes it listed and hashed: everything later reads that copy, never the
/// user's file) and, while an install runs, the one add-on taken out of it. <see cref="Dispose"/> deletes the folder.
/// </summary>
public sealed class DroppedArchive : IDisposable
{
    internal DroppedArchive(string sourcePath, string folder, string snapshotPath, string sha256, bool isZip, ExternalArchiveTool? tool,
        IReadOnlyList<DroppedArchiveAddon> addons, IReadOnlyList<DroppedArchiveAddon> lumaOutsideZip)
    {
        SourcePath = sourcePath;
        Folder = folder;
        SnapshotPath = snapshotPath;
        Sha256 = sha256;
        IsZip = isZip;
        Tool = tool;
        Addons = addons;
        LumaOutsideZip = lumaOutsideZip;
    }

    /// <summary>The user's archive (never changed by RHI; a watched one may be removed after a successful install).</summary>
    public string SourcePath { get; }
    public string ArchiveName => Path.GetFileName(SourcePath);
    /// <summary>RHI's private folder for this archive.</summary>
    public string Folder { get; }
    /// <summary>RHI's copy of the archive (same file name), the only file listing and extraction read.</summary>
    public string SnapshotPath { get; }
    /// <summary>SHA-256 of the bytes RHI read (its copy).</summary>
    public string Sha256 { get; }
    public bool IsZip { get; }
    internal ExternalArchiveTool? Tool { get; }
    /// <summary>The add-ons RHI offers, in archive order (one Luma entry at most).</summary>
    public IReadOnlyList<DroppedArchiveAddon> Addons { get; }
    /// <summary>Luma add-ons inside a <c>.7z</c>/<c>.rar</c>: not offered (the Luma package install takes <c>.zip</c> files).</summary>
    public IReadOnlyList<DroppedArchiveAddon> LumaOutsideZip { get; }

    /// <summary>
    /// Takes <paramref name="addon"/> out of RHI's copy into a fresh folder of its own (inside <see cref="Folder"/>) and
    /// returns its path, named <see cref="DroppedArchiveAddon.FileName"/>. Throws <see cref="InvalidDataException"/> (a
    /// refusal) or the tool's failure; the add-on's folder is gone again when it throws.
    /// </summary>
    public Task<string> ExtractAsync(DroppedArchiveAddon addon, CancellationToken cancellationToken)
        => DroppedArchiveExtractor.ExtractAsync(this, addon, cancellationToken);

    /// <summary>Whether the user's file still holds the bytes RHI read (false when it is gone or unreadable).</summary>
    public async Task<bool> SourceStillMatchesAsync()
    {
        try
        {
            return File.Exists(SourcePath) && FileHash.HexEquals(await FileHash.Sha256HexAsync(SourcePath).ConfigureAwait(false), Sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => DroppedArchiveExtractor.DeleteFolder(Folder);
}

/// <summary>
/// #216 (upstream 2.8.7 <c>DragDropHandler.ProcessDroppedArchive</c>): the one way RHI opens a dropped or watched
/// <c>.zip</c>/<c>.7z</c>/<c>.rar</c>. Inspect, never trust: the archive is copied into a private folder (hashed while
/// copied, size-capped), its copy is listed — a <c>.zip</c> through <see cref="ZipArchive"/>, a <c>.7z</c>/<c>.rar</c>
/// through the host 7-Zip/bsdtar (<see cref="ExternalArchiveRunner"/>'s tool, time limit, tree kill) — and refused
/// before anything is extracted when any member is absolute, climbs out with <c>..</c>, has an empty part, is listed
/// twice (backslash names are read as folders, <see cref="OptiScalerPackageProvider.NormalizeBackslashEntries"/>'s
/// rules), is a link or a device, has a name the listing cannot give safely, or when the sizes it lists pass a cap
/// (<see cref="MaxAddonBytes"/> per add-on, <see cref="MaxListedTotalBytes"/> together, <see cref="MaxExpansionRatio"/>
/// times the archive's own size). Only the add-on chosen for an install is extracted, alone, and its size checked again
/// on disk (a header can lie). Nested archives are never opened (as upstream).
/// </summary>
public static class DroppedArchiveExtractor
{
    /// <summary>The largest archive RHI copies to read (1 GiB).</summary>
    public const long MaxArchiveBytes = 1L << 30;
    /// <summary>The largest add-on RHI takes out of an archive (a downloaded add-on's own cap, 256 MiB).</summary>
    public const long MaxAddonBytes = TrustedDownloadPolicy.AddonMaxBytes;
    /// <summary>The most every member of an archive may list together (2 GiB).</summary>
    public const long MaxListedTotalBytes = 2L << 30;
    /// <summary>The most an archive may list over its own size (a zip bomb lists far more), above <see cref="RatioFloorBytes"/>.</summary>
    public const int MaxExpansionRatio = 100;
    /// <summary>Listed totals up to this size are never taken for a bomb (16 MiB).</summary>
    public const long RatioFloorBytes = 16L << 20;

    // Refusal reasons (PT-BR presentation pairs; the UI line names the archive). Values (a member or the archive) last.
    public const string NotAFileRefusal = "it is not a plain file: ";
    public const string ArchiveTooLargeRefusal = "the archive is larger than 1 GiB: ";
    public const string UnreadableRefusal = "it could not be read (damaged, incomplete or not an archive): ";
    public const string TooManyFilesRefusal = "it lists too many files to check: ";
    public const string AbsoluteRefusal = "a file inside has an absolute path: ";
    public const string ParentRefusal = "a file inside climbs out of the archive's folder with \"..\": ";
    public const string EmptyPartRefusal = "a file inside has an empty part in its path: ";
    public const string DuplicateRefusal = "a file inside is listed twice: ";
    public const string LinkRefusal = "a file inside is a link or a device, not a plain file: ";
    public const string NameRefusal = "a file inside has a name RHI cannot read safely: ";
    public const string AddonTooLargeRefusal = "an add-on inside is larger than 256 MiB: ";
    public const string TotalTooLargeRefusal = "its files together are larger than 2 GiB: ";
    public const string RatioRefusal = "it unpacks to more than 100 times its own size: ";
    public const string SizeMismatchRefusal = "an add-on inside holds a different size than the archive lists: ";
    public const string NotExtractedRefusal = "the archive tool did not give back the add-on: ";

    private const UnixFileMode PrivateFolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Test seam: runs once the add-on's own folder exists, right before the tool or the zip reader fills it.</summary>
    internal static readonly AsyncLocal<Action?> BeforeExtractForTesting = new();

    /// <summary>
    /// Copies, lists and checks <paramref name="archivePath"/> in a new private folder under <paramref name="scratchRoot"/>.
    /// Returns the archive (the caller disposes it) or a refusal (the folder is already gone). Cancellation throws, after
    /// the folder is removed.
    /// </summary>
    public static async Task<DroppedArchiveInspection> InspectAsync(string archivePath, string scratchRoot, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(archivePath);
        // A FIFO or a device would block or never end the copy; a link to a plain file is the user's own choice.
        if (FileKind.IsRegularFile(archivePath) != true) return Refused(NotAFileRefusal + name);
        // Before any copy; the copy is capped too (a file still growing).
        if (new FileInfo(archivePath).Length > MaxArchiveBytes) return Refused(ArchiveTooLargeRefusal + name);
        var folder = CreatePrivateFolder(scratchRoot);
        try
        {
            var snapshot = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "archive"), PrivateFolderMode).FullName, name);
            StagedFile staged;
            try
            {
                var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
                await using (source.ConfigureAwait(false))
                    staged = await AtomicFile.StageStreamAsync(source, snapshot, null, null, MaxArchiveBytes, cancellationToken, PrivateFileMode).ConfigureAwait(false);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith(AtomicFile.SizeLimitPrefix, StringComparison.Ordinal))
            {
                return Fail(folder, ArchiveTooLargeRefusal + name);
            }
            AtomicFile.MoveIntoPlace(staged.Path, snapshot);

            var isZip = Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            ExternalArchiveTool? tool = null;
            IReadOnlyList<DroppedArchiveMember> members;
            if (isZip) members = ListZip(snapshot, name);
            else
            {
                tool = ExternalArchiveRunner.FindTool() ?? throw new InvalidOperationException(ExternalArchiveRunner.NoToolPrefix + name);
                members = await ListWithToolAsync(tool, snapshot, name, cancellationToken).ConfigureAwait(false);
            }

            if (Check(members, staged.Length, name, out var files) is { } refusal) return Fail(folder, refusal);
            var addons = new List<DroppedArchiveAddon>();
            var lumaOutsideZip = new List<DroppedArchiveAddon>();
            foreach (var (member, path) in files)
            {
                var leaf = path[(path.LastIndexOf('/') + 1)..];
                if (LinuxDroppedFileClassifier.ArchiveAddonKind(leaf) is not { } kind) continue;
                var addon = new DroppedArchiveAddon(member.Name, leaf, member.Size ?? 0, kind);
                if (kind != DroppedArchiveAddonKind.Luma) addons.Add(addon);
                else if (!isZip) lumaOutsideZip.Add(addon);
                else if (!addons.Any(other => other.Kind == DroppedArchiveAddonKind.Luma)) addons.Add(addon); // the whole archive installs once
            }
            return new DroppedArchiveInspection(new DroppedArchive(archivePath, folder, snapshot, staged.Sha256, isZip, tool, addons, lumaOutsideZip), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dropped-archive", $"Archive refused: {archivePath}", ex);
            return Fail(folder, ex is InvalidDataException or InvalidOperationException or TimeoutException ? ex.Message : UnreadableRefusal + name);
        }
        catch
        {
            DeleteFolder(folder);
            throw;
        }
    }

    private static DroppedArchiveInspection Refused(string refusal) => new(null, refusal);

    private static DroppedArchiveInspection Fail(string folder, string refusal)
    {
        DeleteFolder(folder);
        return Refused(refusal);
    }

    /// <summary>
    /// The member checks (any member, add-on or not, refuses the whole archive). Returns the refusal or null, and the
    /// plain files with their normalised paths (forward slashes, no <c>.</c> parts).
    /// </summary>
    internal static string? Check(IReadOnlyList<DroppedArchiveMember> members, long archiveLength, string archiveName,
        out List<(DroppedArchiveMember Member, string Path)> files)
    {
        files = [];
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal); // path → is a folder
        long total = 0;
        foreach (var member in members)
        {
            if (member.Special is not null) return LinkRefusal + Shown(member.Name);
            if (member.Name.Any(char.IsControl)) return NameRefusal + Shown(member.Name);
            if (Normalize(member.Name) is { Refusal: { } bad }) return bad + Shown(member.Name);
            var path = Normalize(member.Name).Path!;
            if (path.Length == 0) continue; // the archive's own root (".", "./")
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var parent = string.Join('/', parts[..i]);
                if (seen.TryGetValue(parent, out var parentIsFolder) && !parentIsFolder) return DuplicateRefusal + Shown(member.Name);
                seen[parent] = true;
            }
            if (seen.TryGetValue(path, out var wasFolder) && (!wasFolder || !member.IsDirectory)) return DuplicateRefusal + Shown(member.Name);
            seen[path] = member.IsDirectory;
            if (member.IsDirectory) continue;
            if (member.Size is not { } size || size < 0) return UnreadableRefusal + archiveName;
            total += size;
            files.Add((member, path));
        }
        foreach (var (member, path) in files)
            if (LinuxDroppedFileClassifier.ArchiveAddonKind(path[(path.LastIndexOf('/') + 1)..]) is not null && member.Size > MaxAddonBytes)
                return AddonTooLargeRefusal + Shown(member.Name);
        if (total > MaxListedTotalBytes) return TotalTooLargeRefusal + archiveName;
        if (total > RatioFloorBytes && total / MaxExpansionRatio > archiveLength) return RatioRefusal + archiveName;
        return null;
    }

    /// <summary>
    /// A member name as a relative path with forward slashes (a backslash is a folder separator, as on Windows), or why
    /// it is refused: absolute (<c>/x</c>, <c>\x</c>, <c>C:</c>), an empty part (<c>a//b</c>) or a <c>..</c> part.
    /// <c>.</c> parts and one trailing separator (a folder) are dropped.
    /// </summary>
    internal static (string? Path, string? Refusal) Normalize(string name)
    {
        if (name.StartsWith('/') || name.StartsWith('\\') || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'))
            return (null, AbsoluteRefusal);
        var slashed = name.Replace('\\', '/');
        if (slashed.EndsWith('/')) slashed = slashed[..^1];
        if (slashed.Length == 0) return ("", null);
        var kept = new List<string>();
        foreach (var part in slashed.Split('/'))
        {
            if (part.Length == 0) return (null, EmptyPartRefusal);
            if (part == "..") return (null, ParentRefusal);
            if (part != ".") kept.Add(part);
        }
        return (string.Join('/', kept), null);
    }

    /// <summary>A member name as the refusal shows it: control characters as <c>?</c>.</summary>
    private static string Shown(string name) => string.Concat(name.Select(c => char.IsControl(c) ? '?' : c));

    /// <summary>
    /// A zip's members from its central directory (read inside the guarded region: it is read lazily). The Unix file
    /// type in the external attributes marks links, devices, FIFOs and sockets.
    /// </summary>
    internal static IReadOnlyList<DroppedArchiveMember> ListZip(string zipPath, string archiveName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var members = new List<DroppedArchiveMember>(zip.Entries.Count);
            foreach (var entry in zip.Entries)
            {
                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                var special = type is 0 or 0x8000 or 0x4000 ? null : type == 0xA000 ? "symbolic link" : "special file";
                var isDirectory = (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || type == 0x4000) && entry.Length == 0;
                members.Add(new DroppedArchiveMember(entry.FullName, entry.Length, isDirectory, special));
            }
            return members;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or NotSupportedException)
        {
            throw new InvalidDataException(UnreadableRefusal + archiveName, ex);
        }
    }

    /// <summary>The host tool's listing of <paramref name="archive"/> (7-Zip <c>l -slt -ba</c>, bsdtar <c>-tvf</c>).</summary>
    internal static async Task<IReadOnlyList<DroppedArchiveMember>> ListWithToolAsync(ExternalArchiveTool tool, string archive, string archiveName, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool.Command) { WorkingDirectory = Path.GetDirectoryName(archive)! };
        start.Environment["LC_ALL"] = "C.UTF-8"; // fixed date format; UTF-8 names printed as they are
        foreach (var argument in tool.Kind == ExternalArchiveToolKind.SevenZip ? new[] { "l", "-slt", "-ba", "-sccUTF-8", archive } : ["-tvf", archive])
            start.ArgumentList.Add(argument);
        var result = await ExternalProcess.RunAsync(start, ExternalArchiveRunner.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            AppLog.Warn("dropped-archive", $"{Path.GetFileName(tool.Command)} could not list {archiveName} ({result.ExitCode}): {result.LastMeaningfulLine()}");
            throw new InvalidDataException(UnreadableRefusal + archiveName);
        }
        // The capture keeps only the tail of a longer listing: what was cut off could not be checked.
        if (result.StandardOutput.Length >= ExternalProcess.MaxCapturedChars) throw new InvalidDataException(TooManyFilesRefusal + archiveName);
        return (tool.Kind == ExternalArchiveToolKind.SevenZip ? ParseSevenZip(result.StandardOutput) : ParseBsdTar(result.StandardOutput))
               ?? throw new InvalidDataException(UnreadableRefusal + archiveName);
    }

    private static readonly Regex SevenZipLine = new(@"^(?<key>[A-Z][A-Za-z0-9 ]*?) =(?: (?<value>.*))?$", RegexOptions.CultureInvariant);
    private static readonly Regex UnixMode = new(@"^(?<type>[-dlcbps])[-rwxsStT]{9}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 7-Zip's <c>l -slt -ba</c>: one "Key = value" block per member, blocks apart by a blank line. Strict: a line that is
    /// not a key/value, a key twice in one block, a block without a path or a file without a size makes the whole
    /// listing unreadable (null) — a member name cannot smuggle in a line of its own.
    /// </summary>
    internal static IReadOnlyList<DroppedArchiveMember>? ParseSevenZip(string output)
    {
        var members = new List<DroppedArchiveMember>();
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n').Append(""))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (block.Count == 0) continue;
                if (SevenZipMember(block) is not { } member) return null;
                members.Add(member);
                block.Clear();
                continue;
            }
            var match = SevenZipLine.Match(line);
            if (!match.Success || !block.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value)) return null;
        }
        return members;
    }

    private static DroppedArchiveMember? SevenZipMember(Dictionary<string, string> block)
    {
        if (!block.TryGetValue("Path", out var path)) return null;
        var attributes = block.GetValueOrDefault("Attributes") ?? "";
        var unix = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => UnixMode.Match(token)).FirstOrDefault(match => match.Success);
        var isDirectory = block.GetValueOrDefault("Folder") == "+" || attributes.StartsWith('D') || unix?.Groups["type"].Value == "d";
        string? special = null;
        if (block.GetValueOrDefault("Symbolic Link") is { Length: > 0 }) special = "symbolic link";
        else if (block.GetValueOrDefault("Hard Link") is { Length: > 0 }) special = "hard link";
        else if (unix?.Groups["type"].Value is "l") special = "symbolic link";
        else if (unix?.Groups["type"].Value is "c" or "b" or "p" or "s") special = "special file";
        long? size = long.TryParse(block.GetValueOrDefault("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        if (!isDirectory && size is null) return null;
        return new DroppedArchiveMember(path, size, isDirectory, special);
    }

    // bsdtar -tv: "-rw-r--r--  0 user   group     7311374 Oct 21 22:00 name" (+ " -> target" / " link to target").
    private static readonly Regex BsdTarLine = new(
        @"^(?<type>[-dlcbphs])[-rwxsStT]{9}[+@.]?\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+[A-Z][a-z]{2}\s+\d{1,2}\s+(?:\d{1,2}:\d{2}|\d{4})\s(?<name>.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// bsdtar's <c>-tvf</c>: one line per member. A line that does not parse (a device's "major,minor" size, another
    /// locale) makes the listing unreadable (null). bsdtar escapes a backslash as <c>\\</c> and control characters as
    /// <c>\n</c>/<c>\ooo</c>: the first is read back, the others come back as a control character (refused).
    /// </summary>
    internal static IReadOnlyList<DroppedArchiveMember>? ParseBsdTar(string output)
    {
        var members = new List<DroppedArchiveMember>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var match = BsdTarLine.Match(line);
            if (!match.Success) return null;
            var type = match.Groups["type"].Value;
            var name = match.Groups["name"].Value;
            string? special = null;
            if (type == "l")
            {
                special = "symbolic link";
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) name = name[..arrow];
            }
            else if (type == "h")
            {
                special = "hard link";
                var link = name.IndexOf(" link to ", StringComparison.Ordinal);
                if (link >= 0) name = name[..link];
            }
            else if (type is "c" or "b" or "p" or "s") special = "special file";
            members.Add(new DroppedArchiveMember(Unescape(name), long.Parse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
                type == "d", special));
        }
        return members;
    }

    private static string Unescape(string name)
    {
        if (!name.Contains('\\')) return name;
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] != '\\' || i + 1 >= name.Length) { builder.Append(name[i]); continue; }
            builder.Append(name[++i] == '\\' ? '\\' : '\u0001'); // any other escape stood for a control character
        }
        return builder.ToString();
    }

    /// <summary>
    /// Extracts <paramref name="addon"/> alone into <c>&lt;folder&gt;/addon-&lt;n&gt;/</c>: a <c>.zip</c> entry through
    /// <see cref="ReShadeArchiveExtractor.ExtractEntryAsync"/> (cap, bounded copy, CRC), a <c>.7z</c>/<c>.rar</c> member
    /// through the host tool (literal member name, link check); then exactly one plain file must be there, no larger than
    /// the cap and of the size the archive listed. Returns its path under the add-on's own file name.
    /// </summary>
    internal static async Task<string> ExtractAsync(DroppedArchive archive, DroppedArchiveAddon addon, CancellationToken cancellationToken)
    {
        var folder = Directory.CreateDirectory(Path.Combine(archive.Folder, "addon-" + Guid.NewGuid().ToString("N")), PrivateFolderMode).FullName;
        try
        {
            var raw = Directory.CreateDirectory(Path.Combine(folder, "raw"), PrivateFolderMode).FullName;
            BeforeExtractForTesting.Value?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (archive.IsZip)
            {
                using var zip = ZipFile.OpenRead(archive.SnapshotPath);
                var entry = zip.Entries.Single(e => string.Equals(e.FullName, addon.Member, StringComparison.Ordinal));
                if (await ReShadeArchiveExtractor.ExtractEntryAsync(entry, Path.Combine(raw, "addon"), MaxAddonBytes, cancellationToken).ConfigureAwait(false) is { } refused)
                    throw new InvalidDataException(refused);
            }
            else
            {
                await ExternalArchiveRunner.ExtractAsync(archive.SnapshotPath, raw, cancellationToken, members: [addon.Member],
                    tool: archive.Tool, literalMembers: true).ConfigureAwait(false);
            }
            // 7-Zip extracts flat (a backslash name stays one file name), bsdtar keeps the path: either way one file.
            var produced = SafeFileEnumeration.Files(raw).ToList();
            if (produced is not [var only] || FileKind.IsRegularFile(only) != true) throw new InvalidDataException(NotExtractedRefusal + addon.FileName);
            // A header can lie: what came out must be what the archive listed (the listed size is within the cap).
            if (new FileInfo(only).Length != addon.Size) throw new InvalidDataException(SizeMismatchRefusal + addon.FileName);
            var target = Path.Combine(folder, addon.FileName);
            AtomicFile.Rename(only, target); // inside RHI's own folder: one file system
            return target;
        }
        catch
        {
            DeleteFolder(folder);
            throw;
        }
    }

    /// <summary>A new mode-0700 folder under <paramref name="root"/> (itself created 0700 when missing).</summary>
    internal static string CreatePrivateFolder(string root)
    {
        Directory.CreateDirectory(root, PrivateFolderMode);
        return Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")), PrivateFolderMode).FullName;
    }

    /// <summary>
    /// Deletes one of RHI's archive folders: links are removed without being followed, folders a tool left unwritable
    /// are made writable first. Never throws; false (logged) when something stayed.
    /// </summary>
    public static bool DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return true;
        try
        {
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                File.SetUnixFileMode(directory, PrivateFolderMode);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    if (entry.LinkTarget is not null) entry.Delete(); // the link itself, never its target
                    else if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
            }
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("dropped-archive", $"Could not remove RHI's archive folder {folder}", ex);
            return false;
        }
    }
}
