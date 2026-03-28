using System;
using System.Collections.Generic;
using System.Threading;

namespace CryBarEditor.Classes;

/// <summary>
/// Thread-safe LRU cache keyed by string (case-insensitive).
/// </summary>
public class LruCache<TValue>
{
    readonly int _maxItems;
    readonly Action<TValue>? _onEvict;
    readonly Dictionary<string, LinkedListNode<(string Key, TValue Value)>> _map;
    readonly LinkedList<(string Key, TValue Value)> _list = new();
    readonly Lock _lock = new();

    public LruCache(int maxItems, Action<TValue>? onEvict = null)
    {
        _maxItems = maxItems;
        _onEvict = onEvict;
        _map = new Dictionary<string, LinkedListNode<(string Key, TValue Value)>>(
            maxItems, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default;
            return false;
        }
    }

    public void Add(string key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(key);
                _onEvict?.Invoke(existing.Value.Value);
            }

            var node = _list.AddFirst((key, value));
            _map[key] = node;

            while (_map.Count > _maxItems)
            {
                var last = _list.Last!;
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
                _onEvict?.Invoke(last.Value.Value);
            }
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _map.Remove(key);
                _onEvict?.Invoke(node.Value.Value);
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_onEvict != null)
            {
                foreach (var node in _list)
                    _onEvict(node.Value);
            }
            _map.Clear();
            _list.Clear();
        }
    }
}
