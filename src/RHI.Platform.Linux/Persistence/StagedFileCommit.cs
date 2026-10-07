using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Persistence;

/// <summary>
/// One file of a multi-file change: the bytes it held when the change was prepared (null: no
/// file) and the bytes to write.
/// </summary>
public sealed record StagedFileWrite(string Path, byte[]? Before, byte[] After);

/// <summary>A staged multi-file change failed; every file that could be put back was.</summary>
public sealed class StagedFileCommitException : IOException
{
    public StagedFileCommitException(string message, IReadOnlyList<string> notRestored, Exception? inner)
        : base(message, inner)
    {
        NotRestored = notRestored;
    }

    /// <summary>Files already rewritten that could not be put back (empty when everything was).</summary>
    public IReadOnlyList<string> NotRestored { get; }
}

/// <summary>
/// Writes several store files all or nothing (as far as the filesystem allows): each file is
/// checked to still hold the bytes it was staged from, then each is replaced durably; when one
/// write fails, the files already written get their previous bytes back (or are removed when
/// they did not exist). A file someone else rewrote since staging aborts the whole change before
/// anything is written.
/// </summary>
public static class StagedFileCommit
{
    public static void Commit(IReadOnlyList<StagedFileWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Select(w => w.Path).Distinct(StringComparer.Ordinal).Count() != writes.Count)
            throw new StagedFileCommitException("Nothing was changed: one file was staged twice by two stores.", [], null);

        foreach (var write in writes)
        {
            if (!Unchanged(write))
                throw new StagedFileCommitException(
                    $"{write.Path} changed while the change was being prepared. Nothing was changed; try again.", [], null);
        }

        var written = new List<StagedFileWrite>(writes.Count);
        try
        {
            foreach (var write in writes)
            {
                // Checked again right before each write: a concurrent save in between aborts too.
                if (!Unchanged(write))
                    throw new IOException($"{write.Path} changed while the change was being written.");
                AtomicFile.WriteStoreFile(write.Path, write.After);
                written.Add(write);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var notRestored = new List<string>();
            for (var i = written.Count - 1; i >= 0; i--)
            {
                var done = written[i];
                try
                {
                    if (done.Before is null) AtomicFile.Delete(done.Path);
                    else AtomicFile.WriteStoreFile(done.Path, done.Before);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    notRestored.Add(done.Path);
                }
            }
            var message = notRestored.Count == 0
                ? $"Nothing was changed: {ex.Message}"
                : $"{ex.Message} These files were already rewritten and could not be put back: {string.Join(", ", notRestored)}";
            throw new StagedFileCommitException(message, notRestored, ex);
        }

        foreach (var write in writes) JsonStoreQuarantine.Forget(write.Path);
    }

    private static bool Unchanged(StagedFileWrite write)
    {
        try
        {
            if (write.Before is null) return !File.Exists(write.Path);
            return File.Exists(write.Path) && File.ReadAllBytes(write.Path).AsSpan().SequenceEqual(write.Before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
