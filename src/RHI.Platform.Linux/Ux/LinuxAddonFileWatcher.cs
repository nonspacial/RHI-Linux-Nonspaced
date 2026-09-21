namespace RHI.Platform.Linux.Ux;

public enum LinuxWatchedFileKind
{
    Unsupported = 0,
    Addon,
    Archive,
}

public sealed record LinuxWatchedFileCandidate(string Path, LinuxWatchedFileKind Kind);

/// <summary>
/// Linux-native, non-destructive file watcher for downloaded ReShade/RenoDX payloads.
/// It never installs into a game and never deletes the source file. The UI decides what to do
/// with a detected candidate.
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
            if (!await WaitUntilReadableAsync(path).ConfigureAwait(false)) return;
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

    private static async Task<bool> WaitUntilReadableAsync(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return stream.Length > 0;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    public void Dispose() => Stop();
}
