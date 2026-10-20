namespace GitManager.Services;

internal sealed class BoundedAsyncCache<T>(int capacity, long maximumWeight, Func<T, long> weight)
{
    private sealed class Entry
    {
        public Task<T> Task { get; set; } = null!;
        public required LinkedListNode<string> Node { get; init; }
        public long Weight { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private long _totalWeight;

    public Task<T> GetAsync(string key, Func<Task<T>> read)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                _order.Remove(cached.Node);
                _order.AddLast(cached.Node);
                return cached.Task;
            }
            var entry = new Entry { Node = _order.AddLast(key) };
            _entries.Add(key, entry);
            entry.Task = Task.Run(async () =>
            {
                try
                {
                    var value = await read().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                        {
                            entry.Weight = Math.Max(0, weight(value));
                            _totalWeight += entry.Weight;
                            Trim();
                        }
                    }
                    return value;
                }
                catch
                {
                    lock (_gate)
                        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Remove(key, entry);
                    throw;
                }
            });
            Trim();
            return entry.Task;
        }
    }

    private void Trim()
    {
        while ((_entries.Count > capacity || _totalWeight > maximumWeight) && _order.First is { } first)
            Remove(first.Value, _entries[first.Value]);
    }

    private void Remove(string key, Entry entry)
    {
        _entries.Remove(key);
        _order.Remove(entry.Node);
        _totalWeight -= entry.Weight;
    }
}
