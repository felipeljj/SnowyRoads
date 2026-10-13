using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

// Retains one sequence and detects exact changes while its next version is built.
// End must be called before reading Items: it also detects removed trailing items.
public sealed class ExactSequence<T>
    where T : struct, IEquatable<T>
{
    public readonly List<T> Items = new();
    private int _count;
    private bool _changed;
    public int Count => _count;

    public void Begin()
    {
        _count = 0;
        _changed = false;
    }

    public void Add(T value)
    {
        if (_count == Items.Count)
        {
            Items.Add(value);
            _changed = true;
        }
        else if (!Items[_count].Equals(value))
        {
            Items[_count] = value;
            _changed = true;
        }
        _count++;
    }

    // Only for a caller that has verified the inputs and retained offset exactly.
    public void Retain(int count)
    {
        if (count < 0 || count > Items.Count - _count)
            throw new ArgumentOutOfRangeException(nameof(count));
        _count += count;
    }

    public bool End()
    {
        if (_count < Items.Count)
        {
            Items.RemoveRange(_count, Items.Count - _count);
            _changed = true;
        }
        return _changed;
    }
}
