namespace RHI.Platform.Linux.Ux;

public enum LinuxWatchedFileKind
{
    Unsupported = 0,
    Addon,
    Archive,
}

public sealed record LinuxWatchedFileCandidate(string Path, LinuxWatchedFileKind Kind);

/// <summary>
/// Linux-native file watcher for downloaded ReShade/RenoDX/Luma payloads. It never installs into a game or deletes a
/// file itself: it raises a candidate once the file has settled (a download still being written is never raised) and
/// the UI decides what to do with it.
/// </summary>
public sealed class LinuxAddonFileWatcher : IDisposable
{
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(5);
    private FileSystemWatcher? _watcher;

    public string? CurrentPath { get; private set; }
    public bool IsRunning => _watcher?.EnableRaisingEvents == true;
    public event Action<LinuxWatchedFileCandidate>? CandidateDetected;

    public bool Start(string? path)
    {
        Stop();
        var resolved = ResolveWatchPath(path);
        CurrentPath = resolved;
        if (!Directory.Exists(resolved)) return false;

        try
        {
            _watcher = new FileSystemWatcher(resolved)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (_watcher is null) return;
        _watcher.Created -= OnCreated;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
        _watcher = null;
        lock (_recent) _recent.Clear();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs e) => Schedule(e.FullPath);

    private void Schedule(string path)
    {
        var kind = Classify(path);
        if (kind == LinuxWatchedFileKind.Unsupported) return;

        lock (_recent)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in _recent.Where(item => now - item.Value > DedupeWindow).Select(item => item.Key).ToArray())
                _recent.Remove(stale);
            if (_recent.ContainsKey(path)) return;
            _recent[path] = now;
        }

        _ = Task.Run(async () =>
        {
            if (!await WaitUntilSettledAsync(path).ConfigureAwait(false)) return;
            CandidateDetected?.Invoke(new LinuxWatchedFileCandidate(path, kind));
        });
    }

    internal static LinuxWatchedFileKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LinuxWatchedFileKind.Unsupported;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".addon", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            return LinuxWatchedFileKind.Addon;

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            return name.Contains("renodx", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("luma", StringComparison.OrdinalIgnoreCase)
                ? LinuxWatchedFileKind.Archive
                : LinuxWatchedFileKind.Unsupported;
        }

        return LinuxWatchedFileKind.Unsupported;
    }

    internal static string ResolveWatchPath(string? configuredPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var value = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (value == "~") value = home;
            else if (value.StartsWith("~/", StringComparison.Ordinal))
                value = Path.Combine(home, value[2..]);
            return Path.GetFullPath(value);
        }
        return Path.Combine(home, "Downloads");
    }

    /// <summary>How often a new file is looked at while it may still be written.</summary>
    internal static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(250);
    /// <summary>Looks in a row that must see the same size and change time before the file is raised (about 1 s).</summary>
    internal const int StableLooks = 4;
    /// <summary>The longest a file that keeps changing is waited for (a slow download); after it, it is not raised.</summary>
    internal static readonly TimeSpan MaxSettleWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// #216: true once the file has settled — readable, not empty, its size and change time (ctime: a rewrite in place
    /// keeps the size) unchanged for <see cref="StableLooks"/> looks in a row — so a download still being written is never
    /// raised (and never offered or removed); false when it goes away or never settles. The reader still checks the file
    /// did not change while it copied it (<c>DroppedArchiveExtractor.ChangedWhileReadRefusal</c>).
    /// </summary>
    internal static async Task<bool> WaitUntilSettledAsync(string path, TimeSpan? maxWait = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? MaxSettleWait);
        (long Length, DateTime? Changed)? last = null;
        var same = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SettleInterval, cancellationToken).ConfigureAwait(false);
            (long, DateTime?)? now;
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    now = (stream.Length, RHI.Core.Deployment.FileChangeTime.TryGetUtc(path, followLinks: true));
            }
            catch (IOException) { now = null; }
            catch (UnauthorizedAccessException) { now = null; }
            if (now is { Item1: > 0 } && now == last) same++;
            else same = 0;
            last = now;
            if (same >= StableLooks - 1) return true;
        }
        return false;
    }

    public void Dispose() => Stop();
}
