namespace RHI.Desktop.Views;

/// <summary>
/// w14 final review (achievements): loads by key once and keeps the result (the most recently used
/// <see cref="Capacity"/> of them). A load in flight is shared; one that fails, is cancelled or yields nothing (null) is
/// forgotten, so the next request tries again.
/// </summary>
internal sealed class BoundedTaskCache<TKey, TValue> where TKey : notnull where TValue : class
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, Task<TValue?> Load)>> _entries = new();
    private readonly LinkedList<(TKey Key, Task<TValue?> Load)> _recent = new();

    public BoundedTaskCache(int capacity) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }

    public int Count { get { lock (_gate) return _entries.Count; } }

    public Task<TValue?> GetOrAdd(TKey key, Func<Task<TValue?>> load)
    {
        Task<TValue?> task;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                _recent.Remove(hit);
                _recent.AddFirst(hit);
                return hit.Value.Load;
            }
            task = load();
            _entries[key] = _recent.AddFirst((key, task));
            while (_entries.Count > Capacity && _recent.Last is { } oldest)
            {
                _recent.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
        _ = task.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully && done.Result is not null) return;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Load, done))
                {
                    _recent.Remove(node);
                    _entries.Remove(key);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}

/// <summary>w14 final review (achievements): a list's views kept by the row they show, so a refresh builds only what changed.</summary>
internal static class KeyedViews
{
    /// <summary>The views for <paramref name="rows"/>, reusing those in <paramref name="kept"/> for equal rows; <paramref name="kept"/>
    /// is replaced by the views now shown.</summary>
    public static List<TView> Reuse<TRow, TView>(IEnumerable<TRow> rows, ref Dictionary<TRow, TView> kept, Func<TRow, TView> create) where TRow : notnull
    {
        var next = new Dictionary<TRow, TView>();
        var views = new List<TView>();
        foreach (var row in rows)
        {
            if (next.ContainsKey(row)) continue;
            var view = kept.TryGetValue(row, out var old) ? old : create(row);
            next[row] = view;
            views.Add(view);
        }
        kept = next;
        return views;
    }
}
