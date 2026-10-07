namespace RHI.Core.Deployment;

/// <summary>
/// Tells RHI processes apart for start-up work that must not race a running deployment (#23).
/// Every process registers once and holds an exclusive lock on its own
/// <c>&lt;pid&gt;-&lt;id&gt;.instance</c> file for its whole lifetime (flock, released by the kernel when it
/// dies). Start-up work runs under <see cref="AcquireStartupLock"/> and checks
/// <see cref="OtherInstanceRunning"/> inside it:
/// <list type="bullet">
/// <item>a process registers before it waits for the start-up lock, and checks for others only after
/// taking it, so of two processes starting together either the second one sees the first or it waits
/// until the first one's start-up work (the sweep) is finished;</item>
/// <item>while any other instance lives, its deployments may be running, so the sweep is skipped.</item>
/// </list>
/// </summary>
public sealed class DeploymentInstanceGate : IDisposable
{
    private const string InstanceExtension = ".instance";
    private const string StartupLockName = "startup.lock";

    private readonly FileStream _instanceLock;

    private DeploymentInstanceGate(string directory, string instancePath, FileStream instanceLock)
    {
        Directory = directory;
        InstancePath = instancePath;
        _instanceLock = instanceLock;
    }

    public string Directory { get; }

    public string InstancePath { get; }

    /// <summary>Registers this process (holds its instance lock until <see cref="Dispose"/>).</summary>
    public static DeploymentInstanceGate Register(string directory)
    {
        AtomicFile.CreateDirectoryDurable(directory);
        for (var attempt = 0; ; attempt++)
        {
            var path = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}{InstanceExtension}");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 5)
            {
                continue; // another process probed the new file between create and lock: take a new name
            }
            // A probe that found the file unlocked may have deleted it before the lock was taken; a lock
            // on an unlinked file protects nothing, so start again.
            if (File.Exists(path)) return new DeploymentInstanceGate(directory, path, stream);
            stream.Dispose();
            if (attempt >= 5) throw new IOException($"Could not register this RHI instance in {directory}.");
        }
    }

    /// <summary>
    /// True when another live RHI process is registered. Instance files whose owner died (unlocked)
    /// are removed on the way.
    /// </summary>
    public bool OtherInstanceRunning()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + InstanceExtension))
        {
            if (string.Equals(path, InstancePath, StringComparison.Ordinal)) continue;
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                File.Delete(path); // its owner is gone
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException)
            {
                return true; // locked: that process is alive
            }
            catch (UnauthorizedAccessException)
            {
                return true; // cannot tell: assume alive
            }
        }
        return false;
    }

    /// <summary>
    /// Takes the start-up lock, waiting up to <paramref name="timeout"/> for another process's start-up
    /// work to finish. Returns null when it could not be taken in time; the caller must then skip the
    /// work the lock protects.
    /// </summary>
    public IDisposable? AcquireStartupLock(TimeSpan timeout)
    {
        var path = Path.Combine(Directory, StartupLockName);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        _instanceLock.Dispose();
        FileCleanup.TryDelete(InstancePath);
    }
}
