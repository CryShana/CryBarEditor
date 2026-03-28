using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using CryBar.Bar;
using CryBar.Utilities;

namespace CryBarEditor.Classes;

/// <summary>
/// Holds a parsed BarFile with its open FileStream and a fast entry lookup dictionary.
/// Disposed when evicted from cache.
/// </summary>
sealed class CachedBarFile : IDisposable
{
    public BarFile Bar { get; }
    FileStream Stream { get; }

    readonly Dictionary<string, BarFileEntry> _entryLookup;
    readonly SemaphoreSlim _streamLock = new(1, 1);

    public CachedBarFile(BarFile bar, FileStream stream)
    {
        Bar = bar;
        Stream = stream;

        _entryLookup = new Dictionary<string, BarFileEntry>(
            bar.Entries?.Count ?? 0, StringComparer.OrdinalIgnoreCase);

        if (bar.Entries != null)
        {
            foreach (var entry in bar.Entries)
                _entryLookup.TryAdd(entry.RelativePath, entry);
        }
    }

    public BarFileEntry? FindEntry(string relativePath)
    {
        _entryLookup.TryGetValue(relativePath, out var entry);
        return entry;
    }

    /// <summary>
    /// Thread-safe read of a BAR entry's raw data. Serializes access to the shared FileStream.
    /// </summary>
    public async ValueTask<PooledBuffer?> ReadEntryRawPooledAsync(BarFileEntry entry)
    {
        await _streamLock.WaitAsync();
        try
        {
            return await entry.ReadDataRawPooledAsync(Stream);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    public void Dispose()
    {
        _streamLock.Dispose();
        Stream.Dispose();
    }
}
