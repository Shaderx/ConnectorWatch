using System.Collections;
using System.Collections.Generic;

namespace ConnectorWatch.Gui;

// Storage limits are enforced on insertion, independently of file-read success.
public sealed class BoundedBuffer<T> : IReadOnlyList<T>
{
    readonly int limit;
    T[] items;
    int head;
    public int Count { get; private set; }
    public long Revision { get; private set; }
    public long Evictions { get; private set; }
    public int Capacity => items.Length;
    public BoundedBuffer(int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        this.limit = limit; items = new T[Math.Min(256, limit)];
    }
    public T this[int index] => index >= 0 && index < Count ? items[(head + index) % items.Length] : throw new ArgumentOutOfRangeException(nameof(index));
    public void Add(T value)
    {
        if (Count == limit) { items[head] = value; head = (head + 1) % items.Length; Evictions++; }
        else
        {
            if (Count == items.Length)
            {
                var larger = new T[Math.Min(limit, items.Length * 2)];
                for (int i = 0; i < Count; i++) larger[i] = this[i];
                items = larger; head = 0;
            }
            items[(head + Count++) % items.Length] = value;
        }
        Revision++;
    }
    public void AddRange(IEnumerable<T> values) { foreach (var value in values) Add(value); }
    public void Clear()
    {
        if (Count == 0) return;
        Array.Clear(items); head = Count = 0; Revision++;
    }
    public int RemoveAll(Predicate<T> predicate)
    {
        int kept = 0, oldCount = Count;
        for (int i = 0; i < oldCount; i++) { var value = this[i]; if (!predicate(value)) items[(head + kept++) % items.Length] = value; }
        for (int i = kept; i < oldCount; i++) items[(head + i) % items.Length] = default!;
        Count = kept; if (kept != oldCount) Revision++;
        return oldCount - kept;
    }
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class BoundedIdSet : IEnumerable<string>
{
    readonly int limit;
    readonly Queue<string> order = new();
    readonly HashSet<string> ids = new(StringComparer.Ordinal);
    public int Count => ids.Count;
    public long Revision { get; private set; }
    public BoundedIdSet(int limit) { if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit)); this.limit = limit; }
    public bool Contains(string id) => ids.Contains(id);
    public bool Add(string id)
    {
        if (id == null || id.Length > 256 || !ids.Add(id)) return false;
        order.Enqueue(id);
        if (order.Count > limit) ids.Remove(order.Dequeue());
        Revision++; return true;
    }
    public IEnumerator<string> GetEnumerator() => order.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
