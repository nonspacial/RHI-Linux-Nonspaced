using System.Security.Cryptography;

namespace RHI.Core.Deployment;

/// <summary>
/// The one SHA-256 file hasher for the deployment layer. Hex strings are upper case
/// (<see cref="Convert.ToHexString(byte[])"/>), which is what deployment records already store;
/// compare them with <see cref="HexEquals"/> (ordinal, case-insensitive) because older records and
/// published digests use lower case.
/// </summary>
public static class FileHash
{
    internal const int BufferSize = 1024 * 1024;

    public static byte[] Sha256(string path)
    {
        using var stream = OpenRead(path);
        return SHA256.HashData(stream);
    }

    public static async Task<byte[]> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(path, asynchronous: true);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static string Sha256Hex(string path) => Convert.ToHexString(Sha256(path));

    /// <summary>SHA-256 of in-memory content (upper-case hex), for bytes about to be written.</summary>
    public static string Sha256HexOf(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    public static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken = default)
        => Convert.ToHexString(await Sha256Async(path, cancellationToken).ConfigureAwait(false));

    /// <summary>Hash of <paramref name="path"/>, or null when the file does not exist.</summary>
    public static async Task<string?> Sha256HexOrNullAsync(string path, CancellationToken cancellationToken = default)
        => File.Exists(path) ? await Sha256HexAsync(path, cancellationToken).ConfigureAwait(false) : null;

    public static bool HexEquals(string? first, string? second)
        => first is not null && second is not null && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>Read stream used by every toolkit reader so <see cref="IoMeter"/> sees the bytes.</summary>
    internal static Stream OpenRead(string path, bool asynchronous = false)
    {
        var options = FileOptions.SequentialScan | (asynchronous ? FileOptions.Asynchronous : FileOptions.None);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, options);
        return IoMeter.Wrap(stream);
    }
}
