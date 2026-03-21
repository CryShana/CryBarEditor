using CryBar.Bar;
using CryBar.Dependencies;
using CryBar.Indexing;
using CryBar.Sound;
using CryBar.Utilities;
using CryBarEditor.Classes;
using CryBarEditor.Windows;

using System;
using System.IO;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    DependenciesWindow? _dependenciesWindow;
    SoundsetIndex? _soundsetIndex;
    string? _cachedStringTableContent;

    internal SoundsetIndex? SoundsetIndex => _soundsetIndex;

    void MenuItem_ShowDependencies(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        if (IsContextFromBAR(list))
        {
            if (SelectedBarEntry == null || _barStream == null) return;
            _ = ShowDependenciesAsync(SelectedBarEntry);
        }
        else
        {
            if (SelectedRootFileEntry == null) return;
            _ = ShowDependenciesAsync(SelectedRootFileEntry);
        }
    }

    async void MenuItem_ShowDependenciesFmod(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null || _fmodBank == null) return;

        // Resolve FMOD event to its soundset file and read its content
        var resolution = await ResolveFmodEventSoundsAsync(SelectedBankEntry);
        if (resolution == null)
        {
            _ = ShowError("Could not resolve soundset for this event.\nMake sure a root directory with Sound.bar is loaded.");
            return;
        }

        // Read the soundset file content
        var sourceEntry = _fileIndex?.Find(resolution.SourceFile);
        if (sourceEntry == null || sourceEntry.Count == 0)
        {
            _ = ShowError($"Could not find soundset file: {resolution.SourceFile}");
            return;
        }

        try
        {
            using var data = await ReadFromIndexEntryPooledAsync(sourceEntry[0]);
            if (data == null)
            {
                _ = ShowError("Could not read soundset file content.");
                return;
            }

            await EnsureSoundsetIndexAsync();
            var fileIndex = _fileIndex;
            var soundsetIndex = _soundsetIndex;
            var lang = _stringTableLanguage;
            var filterName = resolution.SoundsetName;
            var result = await Task.Run(() => DependencyFinder.FindDependenciesForFileAsync(
                resolution.SourceFile, data, fileIndex, soundsetIndex,
                lang, ReadFromIndexEntryPooledAsync,
                filterEntityName: filterName));
            ShowDependenciesForResult(result, displayName: resolution.SoundsetName);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to analyze dependencies:\n{ex.Message}");
        }
    }

    async Task ShowDependenciesAsync(BarFileEntry entry)
    {
        if (_barStream == null) return;

        var entryPath = GetBARFullRelativePath(entry);
        var window = EnsureDependenciesWindow(entryPath);
        if (window == null) return; // duplicate request

        try
        {
            using var rawData = await entry.ReadDataRawPooledAsync(_barStream);

            await EnsureSoundsetIndexAsync();
            var fileIndex = _fileIndex;
            var soundsetIndex = _soundsetIndex;
            var lang = _stringTableLanguage;
            var result = await Task.Run(() => DependencyFinder.FindDependenciesForFileAsync(
                entryPath, rawData, fileIndex, soundsetIndex,
                lang, ReadFromIndexEntryPooledAsync));
            window.LoadDependenciesFromResult(result, fileIndex: _fileIndex);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read file for dependency analysis:\n{ex.Message}");
        }
        finally
        {
            window.IsLoading = false;
        }
    }

    async Task ShowDependenciesAsync(RootFileEntry entry)
    {
        if (!Directory.Exists(_rootDirectory)) return;

        var entryPath = GetRootFullRelativePath(entry);
        var window = EnsureDependenciesWindow(entryPath);
        if (window == null) return; // duplicate request

        try
        {
            var path = Path.Combine(_rootDirectory, entry.RelativePath);
            using var rawData = await PooledBuffer.FromFile(path);

            await EnsureSoundsetIndexAsync();
            var fileIndex = _fileIndex;
            var soundsetIndex = _soundsetIndex;
            var lang = _stringTableLanguage;
            var result = await Task.Run(() => DependencyFinder.FindDependenciesForFileAsync(
                entryPath, rawData, fileIndex, soundsetIndex,
                lang, ReadFromIndexEntryPooledAsync));
            window.LoadDependenciesFromResult(result, fileIndex: _fileIndex);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read file for dependency analysis:\n{ex.Message}");
        }
        finally
        {
            window.IsLoading = false;
        }
    }

    void ShowDependenciesForResult(DependencyResult result, string? displayName = null)
    {
        var window = EnsureDependenciesWindow(result.EntryPath, displayName);
        if (window == null) return;
        window.LoadDependenciesFromResult(result, displayName, _fileIndex);
        window.IsLoading = false;
    }

    /// <summary>
    /// Opens or reuses the DependenciesWindow, setting it to loading state immediately.
    /// Returns null if the window is already loading the same path (duplicate request).
    /// </summary>
    DependenciesWindow? EnsureDependenciesWindow(string entryPath, string? displayName = null)
    {
        if (_dependenciesWindow != null)
        {
            if (_dependenciesWindow.IsLoading && _dependenciesWindow.CurrentEntryPath == entryPath)
                return null; // already loading this file

            _dependenciesWindow.StartLoading(entryPath, displayName);
            _dependenciesWindow.Focus();
            return _dependenciesWindow;
        }

        _dependenciesWindow = new DependenciesWindow(this);
        _dependenciesWindow.Closed += (_, _) => _dependenciesWindow = null;
        _dependenciesWindow.Show(this);
        _dependenciesWindow.StartLoading(entryPath, displayName);
        return _dependenciesWindow;
    }

    async Task EnsureSoundsetIndexAsync()
    {
        if (_soundsetIndex == null && _fileIndex != null)
            await RebuildSoundsetIndexAsync();
    }

    /// <summary>
    /// Builds the SoundsetIndex from all soundset files in the file index.
    /// Loads and parses soundset files that aren't already cached.
    /// </summary>
    async Task RebuildSoundsetIndexAsync()
    {
        if (_fileIndex == null)
        {
            _soundsetIndex = null;
            return;
        }

        var index = new SoundsetIndex();
        var soundsetFileNames = FindSoundsetFileNames();

        foreach (var fileName in soundsetFileNames)
        {
            var fileEntries = _fileIndex.Find(fileName);
            if (fileEntries.Count == 0) continue;

            // Try cache first, otherwise load and parse
            if (!_cachedSoundsetFiles.TryGetValue(fileName, out var definitions))
            {
                using var data = await ReadFromIndexEntryPooledAsync(fileEntries[0]);
                if (data == null) continue;

                using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
                var xmlText = ConversionHelper.GetTextContent(decompressed.Span, fileName);

                try
                {
                    definitions = SoundsetParser.ParseSoundsetXml(xmlText);
                    _cachedSoundsetFiles[fileName] = definitions;
                }
                catch { continue; }
            }

            var culture = SoundsetIndex.ExtractCulture(fileName) ?? "unknown";

            FileIndexEntry? bankFile = null;
            var bankName = culture + ".bank";
            var bankEntries = _fileIndex.Find(bankName);
            if (bankEntries.Count > 0)
                bankFile = bankEntries[0];

            index.AddFromParsedFile(definitions, culture, fileEntries[0], bankFile);
        }

        _soundsetIndex = index;
    }
}
