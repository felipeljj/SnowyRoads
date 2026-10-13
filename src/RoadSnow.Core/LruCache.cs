using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

// For derived managed data only. Eviction never owns or destroys a Unity resource.
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _recent = new();
    public int Count => _entries.Count;

    public LruCache(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }
        _recent.Remove(node);
        _recent.AddLast(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            node.Value = (key, value);
            _recent.Remove(node);
            _recent.AddLast(node);
            return;
        }
        if (_entries.Count == _capacity)
        {
            var oldest = _recent.First!;
            _entries.Remove(oldest.Value.Key);
            _recent.RemoveFirst();
            // Reuse the node instead of allocating on every eviction.
            oldest.Value = (key, value);
            _recent.AddLast(oldest);
            _entries.Add(key, oldest);
        }
        else
            _entries.Add(key, _recent.AddLast((key, value)));
    }

    public void Remove(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node))
            return;
        _entries.Remove(key);
        _recent.Remove(node);
    }

    public void Clear()
    {
        _entries.Clear();
        _recent.Clear();
    }
}
