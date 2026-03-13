using System;
using System.Collections.Generic;

namespace CryBarEditor.Classes;

/// <summary>
/// Thread-safe LRU cache for GLB byte arrays, keyed by TMM file name (case-insensitive).
/// </summary>
public class GlbPreviewCache
{
    readonly int _maxItems;
    readonly Dictionary<string, LinkedListNode<(string Key, byte[] Data)>> _map;
    readonly LinkedList<(string Key, byte[] Data)> _list = new();
    readonly object _lock = new();

    public GlbPreviewCache(int maxItems = 10)
    {
        _maxItems = maxItems;
        _map = new Dictionary<string, LinkedListNode<(string Key, byte[] Data)>>(
            maxItems, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string key, out byte[]? data)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                data = node.Value.Data;
                return true;
            }
            data = null;
            return false;
        }
    }

    public void Add(string key, byte[] data)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(key);
            }

            var node = _list.AddFirst((key, data));
            _map[key] = node;

            while (_map.Count > _maxItems)
            {
                var last = _list.Last!;
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
