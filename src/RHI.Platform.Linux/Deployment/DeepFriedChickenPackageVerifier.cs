using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using static RHI.Platform.Linux.Deployment.Dlss5FeederFullDeploymentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// DLSS5 Feeder Full (DEP-S3, #169): opens a Deep Fried Chicken package (.zip, AES .zip or .7z), checks every binary
/// against its SHA256SUMS.txt and stages the add-on, NGX bridge and config. Writes only under the staging folder.
/// </summary>
internal static class DeepFriedChickenPackageVerifier
{
    internal sealed record DeepFriedChickenBundle(string AddonPath, string BridgePath, string ConfigPath, string Version);

    internal static bool IsSupportedDeepFriedChickenArchive(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAesEncryptedZipArchive(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            if (stream.Read(header) < header.Length) return false;

            // ZIP local-file header: PK\x03\x04, general-purpose flags at 6..7 and
            // compression method at 8..9. WinZip AES uses method 99 and sets encryption bit 0.
            if (header[0] != (byte)'P' || header[1] != (byte)'K' || header[2] != 0x03 || header[3] != 0x04)
                return false;

            var flags = header[6] | (header[7] << 8);
            var method = header[8] | (header[9] << 8);
            return (flags & 0x0001) != 0 && method == 99;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static DeepFriedChickenBundle ExtractDeepFriedChickenBundle(string archivePath, string destination)
        => ExtractDeepFriedChickenBundleAsync(archivePath, destination, CancellationToken.None)
            .GetAwaiter().GetResult();

    internal static async Task<DeepFriedChickenBundle> ExtractDeepFriedChickenBundleAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(archivePath))
            throw new InvalidDataException("Deep Fried Chicken package does not exist.");
        if (!IsSupportedDeepFriedChickenArchive(archivePath))
            throw new InvalidDataException("O pacote do Deep Fried Chicken deve usar o formato oficial .zip ou .7z.");

        var extension = Path.GetExtension(archivePath);
        // Some upstream releases use a .7z filename while the payload is actually
        // an AES-encrypted ZIP. Ordinary unencrypted ZIPs stay on the native ZipArchive path;
        // encrypted ZIPs and .7z-labelled packages go through 7-Zip so AES can be handled.
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !IsAesEncryptedZipArchive(archivePath))
            return await ExtractDeepFriedChickenZipBundleAsync(archivePath, destination, cancellationToken).ConfigureAwait(false);

        return await ExtractDeepFriedChicken7zBundleAsync(archivePath, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DeepFriedChickenBundle> ExtractDeepFriedChickenZipBundleAsync(string zipPath, string destination, CancellationToken cancellationToken)
    {
        // Do not reject the outer ZIP by compressed file size. Highly compressible but
        // structurally valid packages (including deterministic test fixtures) can be
        // smaller than an arbitrary archive-size threshold. The required contents are
        // validated below, and both binaries are still subject to minimum extracted-size,
        // PE-signature and SHA-256 checks before deployment.
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        var sumsEntry = FindArchiveEntryByLeaf(archive, "SHA256SUMS.txt")
            ?? throw new InvalidDataException("Deep Fried Chicken package does not contain SHA256SUMS.txt. Use the official release archive.");
        var sumsText = ReadArchiveEntryText(sumsEntry);
        var expected = ParseDeepFriedChickenManifest(sumsText, out var version);

        var addon = await ExtractVerifiedDfcEntryAsync(archive, expected, DeepFriedAddonName, Path.Combine(destination, DeepFriedAddonName), 64 * 1024, requirePe: true, cancellationToken).ConfigureAwait(false);
        var bridge = await ExtractVerifiedDfcEntryAsync(archive, expected, DeepFriedBridgeName, Path.Combine(destination, DeepFriedBridgeName), 1024, requirePe: true, cancellationToken).ConfigureAwait(false);

        var configEntry = FindArchiveEntryByLeaf(archive, DeepFriedConfigName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedConfigName}.");
        var config = Path.Combine(destination, DeepFriedConfigName);
        await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(configEntry, config, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        ValidateDeepFriedChickenConfig(config);

        return new(addon, bridge, config, version);
    }

    private static async Task<DeepFriedChickenBundle> ExtractDeepFriedChicken7zBundleAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var extractedRoot = Path.Combine(destination, "_archive");
        Directory.CreateDirectory(extractedRoot);
        await ExtractExternalArchiveAsync(archivePath, extractedRoot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateExtractedDeepFriedChickenDirectory(extractedRoot, destination);
    }

    internal static DeepFriedChickenBundle ValidateExtractedDeepFriedChickenDirectory(string extractedRoot, string destination)
    {
        if (!Directory.Exists(extractedRoot))
            throw new InvalidDataException("Deep Fried Chicken extracted package directory does not exist.");

        var sumsPath = FindExtractedFileByLeaf(extractedRoot, "SHA256SUMS.txt")
            ?? throw new InvalidDataException("Deep Fried Chicken package does not contain SHA256SUMS.txt. Use the official release archive.");
        var sumsText = File.ReadAllText(sumsPath);
        var expected = ParseDeepFriedChickenManifest(sumsText, out var version);

        Directory.CreateDirectory(destination);
        var addonSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedAddonName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedAddonName}.");
        var bridgeSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedBridgeName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedBridgeName}.");
        var configSource = FindExtractedFileByLeaf(extractedRoot, DeepFriedConfigName)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {DeepFriedConfigName}.");

        var addon = CopyVerifiedDfcFile(addonSource, expected, DeepFriedAddonName, Path.Combine(destination, DeepFriedAddonName), 64 * 1024, requirePe: true);
        var bridge = CopyVerifiedDfcFile(bridgeSource, expected, DeepFriedBridgeName, Path.Combine(destination, DeepFriedBridgeName), 1024, requirePe: true);
        var config = Path.Combine(destination, DeepFriedConfigName);
        AtomicFile.Copy(configSource, config);
        NormalizeReadableFile(config);
        ValidateDeepFriedChickenConfig(config);

        return new(addon, bridge, config, version);
    }

    private static IReadOnlyDictionary<string, string> ParseDeepFriedChickenManifest(string sumsText, out string version)
    {
        var versionMatch = Regex.Match(sumsText, @"(?im)^\s*Deep\s+Fried\s+Chicken\s+([^\r\n]+?)\s*$");
        if (!versionMatch.Success)
            throw new InvalidDataException("Deep Fried Chicken SHA256SUMS.txt does not contain a recognizable release version.");
        version = versionMatch.Groups[1].Value.Trim();

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(sumsText, @"(?im)^\s*([0-9A-F]{64})\s+([^\s]+)\s*$"))
            expected[ArchiveLeafName(match.Groups[2].Value)] = match.Groups[1].Value.ToUpperInvariant();

        if (!expected.ContainsKey(DeepFriedAddonName) || !expected.ContainsKey(DeepFriedBridgeName))
            throw new InvalidDataException("Deep Fried Chicken SHA256SUMS.txt does not authenticate the required add-on and NGX bridge.");
        return expected;
    }

    private static void ValidateDeepFriedChickenConfig(string configPath)
    {
        var configText = File.ReadAllText(configPath);
        if (!Regex.IsMatch(configText, @"(?im)^\s*arm\s*=\s*1\s*$"))
            throw new InvalidDataException("Deep Fried Chicken config is not armed (expected arm=1 in the official package).");
    }

    private static string CopyVerifiedDfcFile(
        string source,
        IReadOnlyDictionary<string, string> expected,
        string name,
        string destination,
        long minimumSize,
        bool requirePe)
    {
        var actual = AtomicFile.Copy(source, destination);
        NormalizeReadableFile(destination);
        ValidateDownloadedPayload(destination, name, minimumSize, requirePe);
        if (!actual.Equals(expected[name], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Deep Fried Chicken package failed SHA-256 verification for {name}.");
        return destination;
    }

    private static string? FindExtractedFileByLeaf(string root, string name)
        => SafeFileEnumeration.Files(root)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A .7z (or AES-encrypted ZIP) Deep Fried Chicken package through the shared host-tool runner (time limit,
    /// process-tree kill, link check; #37). An AES package gets its public upstream password on stdin, never on
    /// the command line (visible to other local processes through /proc), and therefore needs 7-Zip.
    /// </summary>
    private static Task ExtractExternalArchiveAsync(
        string archivePath,
        string outputDirectory,
        CancellationToken cancellationToken)
        => ExternalArchiveRunner.ExtractAsync(
            archivePath, outputDirectory, cancellationToken,
            password: IsAesEncryptedZipArchive(archivePath) ? DeepFriedChickenArchivePassword : null,
            timeout: DeepFriedChickenExtractionTimeout);

    private static async Task<string> ExtractVerifiedDfcEntryAsync(
        ZipArchive archive,
        IReadOnlyDictionary<string, string> expected,
        string name,
        string destination,
        long minimumSize,
        bool requirePe,
        CancellationToken cancellationToken)
    {
        var entry = FindArchiveEntryByLeaf(archive, name)
            ?? throw new InvalidDataException($"Deep Fried Chicken package does not contain {name}.");
        await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, destination, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        ValidateDownloadedPayload(destination, name, minimumSize, requirePe);
        var actual = await FileHash.Sha256HexAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(expected[name], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Deep Fried Chicken package failed SHA-256 verification for {name}.");
        return destination;
    }

    private static ZipArchiveEntry? FindArchiveEntryByLeaf(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(entry.FullName) &&
            ArchiveLeafName(entry.FullName).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ReadArchiveEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
