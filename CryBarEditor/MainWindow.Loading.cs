using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CryBar;
using CryBar.Classes;
using CryBarEditor.Classes;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    #region UI Load events
    async void LoadBAR_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var suggested_folder = _lastConfiguration?.BarFile == null ? null : Path.GetDirectoryName(_lastConfiguration.BarFile);
        var file = await PickFile(sender, "Open BAR file", [new("BAR file") { Patterns = ["*.bar"] }], suggested_folder);
        if (file == null) return;
        LoadBAR(file);
    }

    async void LoadBank_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var suggested_folder = _lastConfiguration?.RootDirectory;
        var file = await PickFile(sender, "Open FMOD Bank file", [new("Bank file") { Patterns = ["*.bank"] }], suggested_folder);
        if (file == null) return;
        LoadFMODBank(file);
    }

    async void LoadDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var suggested_folder = _lastConfiguration?.RootDirectory == null ? null : Path.GetDirectoryName(_lastConfiguration.RootDirectory);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Root directory",
            AllowMultiple = false,
            SuggestedStartLocation = suggested_folder == null ? null : await StorageProvider.TryGetFolderFromPathAsync(suggested_folder)
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (folders.Count == 0)
            return;

        LoadDir(folders[0].Path.LocalPath);
    }

    async void SelectExportRootDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var suggested_folder = _lastConfiguration?.ExportRootDirectory == null ? null : Path.GetDirectoryName(_lastConfiguration.ExportRootDirectory);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select export Root directory",
            AllowMultiple = false,
            SuggestedStartLocation = suggested_folder == null ? null : await StorageProvider.TryGetFolderFromPathAsync(suggested_folder)
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (folders.Count == 0)
            return;

        var dir = folders[0].Path.LocalPath;
        if (dir == _rootDirectory)
        {
            _ = ShowError("Root directory is the same as export directory");
            return;
        }

        ExportRootDirectory = folders[0].Path.LocalPath;
        SaveConfiguration();
    }
    #endregion

    #region File Watcher events
    void RootDir_Deleted(object sender, FileSystemEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var removed_path = e.FullPath;
        if (!removed_path.StartsWith(_rootDirectory))
            return;

        // TODO: check if it was a directory that was removed...

        var relative_path = Path.GetRelativePath(_rootDirectory, removed_path);

        RootFileEntry? entry_removed = null;
        for (int i = 0; i < _loadedRootFiles.Count; i++)
        {
            var f = _loadedRootFiles[i];
            if (f.RelativePath == relative_path)
            {
                entry_removed = f;
                _loadedRootFiles.RemoveAt(i);
                break;
            }
        }

        if (entry_removed != null)
        {
            if (SelectedRootFileEntry == entry_removed)
                SelectedRootFileEntry = null;

            RefreshFileEntries();
        }
    }

    void RootDir_Created(object sender, FileSystemEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var created_path = e.FullPath;
        if (!created_path.StartsWith(_rootDirectory))
            return;

        if (Directory.Exists(created_path))
            return;

        var prev_file = SelectedRootFileEntry;

        // reload files
        LoadFilesFromRoot();
        RebuildFileIndex();

        // re-select prev file
        if (prev_file != null)
        {
            foreach (var f in _loadedRootFiles)
            {
                if (f.RelativePath == prev_file.RelativePath)
                {
                    SelectedRootFileEntry = f;
                    break;
                }
            }
        }
    }

    void RootDir_Renamed(object sender, RenamedEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var renamed_path = e.OldFullPath;
        if (!renamed_path.StartsWith(_rootDirectory) ||
            !e.FullPath.StartsWith(_rootDirectory))
            return;

        if (Directory.Exists(e.FullPath))
        {
            // TODO: handle all moved files under this directory...
            return;
        }

        var new_entry = new RootFileEntry(_rootDirectory, e.FullPath);
        var old_relative_path = Path.GetRelativePath(_rootDirectory, renamed_path);

        for (int i = 0; i < _loadedRootFiles.Count; i++)
        {
            var f = _loadedRootFiles[i];
            if (f.RelativePath == old_relative_path)
            {
                _loadedRootFiles[i] = new_entry;
                if (f == SelectedRootFileEntry)
                {
                    SelectedRootFileEntry = new_entry;
                }
                break;
            }
        }

        RefreshFileEntries();
    }

    void RootDir_Changed(object sender, FileSystemEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var changed_path = e.FullPath;
        if (!changed_path.StartsWith(_rootDirectory))
            return;

        var relative_path = Path.GetRelativePath(_rootDirectory, changed_path);
        _docCache.Remove(relative_path);
    }

    void ExportDir_Deleted(object sender, FileSystemEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var removed_path = e.FullPath;
        if (!removed_path.StartsWith(_exportRootDirectory))
            return;

        OnPropertyChanged(nameof(ShowOverridenIcons));
    }

    void ExportDir_Created(object sender, FileSystemEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var created_path = e.FullPath;
        if (!created_path.StartsWith(_exportRootDirectory))
            return;

        OnPropertyChanged(nameof(ShowOverridenIcons));
    }

    void ExportDir_Renamed(object sender, RenamedEventArgs e)
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var renamed_path = e.OldFullPath;
        if (!renamed_path.StartsWith(_exportRootDirectory) ||
            !e.FullPath.StartsWith(_exportRootDirectory))
            return;

        OnPropertyChanged(nameof(ShowOverridenIcons));
    }
    #endregion

    #region Loading files
    public void LoadDir(string dir, bool update_config = true)
    {
        try
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException("Directory not found");

            if (_exportRootDirectory == dir)
                throw new InvalidOperationException("Root directory is the same as export directory");

            _docCache.Clear();
            RootDirectory = dir;
            LoadFilesFromRoot();
            RebuildFileIndex();

            _rootWatcher.Watch(dir);

            if (update_config) SaveConfiguration();
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to load root directory:\n" + ex.Message);
        }
    }

    void LoadFilesFromRoot()
    {
        _loadedRootFiles = Directory.GetFiles(_rootDirectory, "*.*", SearchOption.AllDirectories)
                .Select(x => new RootFileEntry(_rootDirectory, x))
                .ToList();

        SelectedRootFileEntry = null;

        RefreshFileEntries();
        OnPropertyChanged(nameof(LoadedBARFilePathOrRelative));

        // in case BAR file is loaded from before, if it's within root dir, select it here
        if (_barFile != null && _barStream?.Name.StartsWith(_rootDirectory) == true)
        {
            var relative_path = Path.GetRelativePath(_rootDirectory, _barStream.Name);
            foreach (var file in _loadedRootFiles)
            {
                if (file.RelativePath == relative_path)
                {
                    SelectedRootFileEntry = file;
                    break;
                }
            }
        }
    }

    void RebuildFileIndex()
    {
        if (_loadedRootFiles == null || !Directory.Exists(_rootDirectory)) return;

        var index = new FileIndex();
        var rootRelevantPath = GetRootRelevantPath();

        // Add root files (non-.bar)
        foreach (var rootFile in _loadedRootFiles)
        {
            if (rootFile.Extension == ".BAR") continue;
            var fullRelPath = Path.Combine(rootRelevantPath, rootFile.RelativePath);
            index.Add(new FileIndexEntry
            {
                FullRelativePath = fullRelPath,
                FileName = rootFile.Name,
                Source = FileIndexSource.RootFile,
            });
        }

        // Scan BAR files in parallel; FileIndex.Add is thread-safe
        var barFiles = _loadedRootFiles
            .Where(f => f.Extension == ".BAR")
            .Select(f => Path.Combine(_rootDirectory, f.RelativePath))
            .ToList();

        Parallel.ForEach(barFiles, barFilePath =>
        {
            try
            {
                using var stream = File.OpenRead(barFilePath);
                var bar = new BarFile(stream);
                if (!bar.Load(out _)) return;

                var barRootPath = bar.RootPath;
                var entries = bar.Entries;
                if (entries == null) return;

                foreach (var entry in entries)
                {
                    var fullRelPath = string.IsNullOrEmpty(barRootPath)
                        ? entry.RelativePath
                        : Path.Combine(barRootPath, entry.RelativePath);

                    index.Add(new FileIndexEntry
                    {
                        FullRelativePath = fullRelPath,
                        FileName = entry.Name,
                        Source = FileIndexSource.BarEntry,
                        BarFilePath = barFilePath,
                        BarRootPath = barRootPath,
                        EntryRelativePath = entry.RelativePath,
                    });
                }
            }
            catch { /* skip unreadable BAR files */ }
        });

        _fileIndex = index;
        ClearSoundCaches();
    }

    async ValueTask<PooledBuffer?> ReadFromIndexEntryPooledAsync(FileIndexEntry entry)
    {
        if (entry.Source == FileIndexSource.RootFile)
        {
            var rootRelevantPath = GetRootRelevantPath();
            var relPath = entry.FullRelativePath;

            if (relPath.StartsWith(rootRelevantPath, StringComparison.OrdinalIgnoreCase))
                relPath = relPath[rootRelevantPath.Length..];

            var diskPath = Path.Combine(_rootDirectory, relPath);
            if (!File.Exists(diskPath)) 
                return null;

            return await PooledBuffer.FromFile(diskPath);
        }

        if (entry.BarFilePath == null || entry.EntryRelativePath == null) 
            return null;

        try
        {
            using var stream = File.OpenRead(entry.BarFilePath);
            var bar = new BarFile(stream);
            if (!bar.Load(out _)) return null;

            var barEntry = bar.Entries?.FirstOrDefault(e =>
                e.RelativePath.Equals(entry.EntryRelativePath, StringComparison.OrdinalIgnoreCase));
            if (barEntry == null) return null;

            using var dataRaw = await barEntry.ReadDataRawPooledAsync(stream);
            return BarCompression.EnsureDecompressedPooled(dataRaw, out _);
        }
        catch
        {
            return null;
        }
    }

    public void LoadFMODBank(string fmod_bank_file)
    {
        if (_barStream != null)
        {
            _barStream?.Dispose();
            _barStream = null;
            _barFile = null;
            RefreshBAREntries();
            OnBarFileLoaded?.Invoke(null, null);

            OnPropertyChanged(nameof(LoadedBARFilePathOrRelative));
            OnPropertyChanged(nameof(BarFileRootPath));
            OnPropertyChanged(nameof(BarFile));
        }

        try
        {
            bank_play_csc?.Cancel();
            _fmodBank?.Dispose();
            _fmodBank = FMODBank.LoadBank(fmod_bank_file);
            SelectedBankEntries.Clear();
            SelectedBankEntry = null;
            OnPropertyChanged(nameof(FmodBank));
            OnPropertyChanged(nameof(IsBankFileSelected));
            OnPropertyChanged(nameof(LoadedBankFilePathOrRelative));
            RefreshBankEntries();
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to load FMOD bank:\n{ex.Message} (Dir: {Environment.CurrentDirectory})");
        }
    }

    public void LoadBAR(string bar_file, bool update_config = true)
    {
        // deselect FMOD bank if selected
        if (_fmodBank != null)
        {
            _fmodBank?.Dispose();
            _fmodBank = null;
            SelectedBankEntries.Clear();
            SelectedBankEntry = null;
            OnPropertyChanged(nameof(FmodBank));
            OnPropertyChanged(nameof(IsBankFileSelected));
        }

        _docCache.Clear();
        _barStream?.Dispose();
        var stream = File.OpenRead(bar_file);

        try
        {
            var file = new BarFile(stream);
            if (!file.Load(out var error))
            {
                throw new Exception("Failed to load BAR file: " + error.ToString());
            }

            _barStream = stream;
            _barFile = file;
            RefreshBAREntries();
            OnBarFileLoaded?.Invoke(file, stream);

            OnPropertyChanged(nameof(LoadedBARFilePathOrRelative));
            OnPropertyChanged(nameof(BarFileRootPath));
            OnPropertyChanged(nameof(BarFile));

            // if BAR file is contained within root dir, select it there for convenience
            if (Directory.Exists(_rootDirectory) && bar_file.StartsWith(_rootDirectory))
            {
                var relative_path = Path.GetRelativePath(_rootDirectory, bar_file);
                foreach (var f in RootFileEntries)
                {
                    if (f.RelativePath == relative_path)
                    {
                        SelectedRootFileEntry = f;
                        break;
                    }
                }
            }

            if (update_config)
            {
                SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _barStream = null;
            _ = ShowError("Failed to load BAR archive:\n" + ex.Message);
        }
    }
    #endregion

    #region Path Helpers
    public string GetBARFullRelativePath(BarFileEntry entry)
    {
        if (_barFile == null)
            return entry.RelativePath;

        return Path.Combine(_barFile.RootPath ?? "", entry.RelativePath);
    }

    /// <summary>
    /// Searches all .bar files in the same directory as the given BAR file for an entry matching the given name.
    /// Skips the current BAR file. Returns the result of the factory function, or default if not found.
    /// </summary>
    static async ValueTask<T?> FindCompanionInSiblingBars<T>(string currentBarPath, string entryName,
        Func<BarFileEntry, FileStream, ValueTask<T?>> factory)
    {
        var barDir = Path.GetDirectoryName(currentBarPath);
        if (barDir == null) return default;

        try
        {
            foreach (var siblingBarPath in Directory.GetFiles(barDir, "*.bar"))
            {
                if (siblingBarPath.Equals(currentBarPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var sibStream = File.OpenRead(siblingBarPath);
                    var sibBar = new BarFile(sibStream);
                    if (!sibBar.Load(out _)) continue;

                    var entry = sibBar.Entries?.FirstOrDefault(e =>
                        e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        var result = await factory(entry, sibStream);
                        if (result != null) return result;
                    }
                }
                catch { /* skip unreadable BAR files */ }
            }
        }
        catch { /* ignore directory enumeration errors */ }

        return default;
    }

    string? _rootRelevantPathCached = null;
    string GetRootRelevantPath()
    {
        if (_rootRelevantPathCached != null)
            return _rootRelevantPathCached;

        // root directory could be "\game" or "\game\art" etc... let's find if there is a parent "game" directory anywhere in the chain
        // then we cache this value for later use

        var relevant_path = "";
        var dirs = _rootDirectory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        {
            if (dir == ROOT_DIRECTORY_NAME)
            {
                relevant_path += ROOT_DIRECTORY_NAME + Path.DirectorySeparatorChar;
                continue;
            }

            if (relevant_path.Length > 0)
            {
                relevant_path += dir + Path.DirectorySeparatorChar;
            }
        }
        _rootRelevantPathCached = relevant_path;
        return relevant_path;
    }

    public string GetRootFullRelativePath(RootFileEntry entry)
    {
        if (!Directory.Exists(_rootDirectory))
            return entry.RelativePath;

        return Path.Combine(GetRootRelevantPath(), entry.RelativePath);
    }
    #endregion
}
