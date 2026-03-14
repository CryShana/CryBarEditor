using System;
using System.IO;

namespace CryBarEditor.Classes;

/// <summary>
/// Manages FileSystemWatcher lifecycle - setup, event binding, and disposal.
/// </summary>
public class FileWatcherHelper : IDisposable
{
    FileSystemWatcher? _watcher;

    public event FileSystemEventHandler? Created;
    public event FileSystemEventHandler? Deleted;
    public event FileSystemEventHandler? Changed;
    public event RenamedEventHandler? Renamed;

    public void Watch(string directory)
    {
        Dispose();

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Created += (s, e) => Created?.Invoke(s, e);
        _watcher.Deleted += (s, e) => Deleted?.Invoke(s, e);
        _watcher.Changed += (s, e) => Changed?.Invoke(s, e);
        _watcher.Renamed += (s, e) => Renamed?.Invoke(s, e);
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
