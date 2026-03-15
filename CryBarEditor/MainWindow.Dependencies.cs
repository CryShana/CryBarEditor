using CryBar;
using CryBar.Classes;
using CryBarEditor.Classes;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    DependenciesWindow? _dependenciesWindow;
    SoundsetIndex? _soundsetIndex;

    void MenuItem_ShowDependencies(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        if (IsContextFromBAR(list))
        {
            if (SelectedBarEntry == null || _barStream == null) return;
            _ = ShowDependenciesForBarEntry(SelectedBarEntry);
        }
        else
        {
            if (SelectedRootFileEntry == null) return;
            if (SelectedRootFileEntry.RelativePath.EndsWith(".bank", StringComparison.OrdinalIgnoreCase))
                _ = ShowDependenciesForBankFile(SelectedRootFileEntry);
            else
                _ = ShowDependenciesForRootFile(SelectedRootFileEntry);
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

        using var data = await ReadFromIndexEntryPooledAsync(sourceEntry[0]);
        if (data == null)
        {
            _ = ShowError("Could not read soundset file content.");
            return;
        }

        using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
        var content = GetTextContent(decompressed.Span, resolution.SourceFile);
        ShowDependenciesFor(content, resolution.SourceFile, filterEntityName: resolution.SoundsetName);
    }

    async Task ShowDependenciesForBarEntry(BarFileEntry entry)
    {
        if (_barStream == null) return;

        try
        {
            using var rawData = await entry.ReadDataRawPooledAsync(_barStream);
            using var data = BarCompression.EnsureDecompressedPooled(rawData, out _);
            var entryPath = GetBARFullRelativePath(entry);

            if (entry.RelativePath.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase))
            {
                ShowDependenciesForResult(DependencyFinder.FindDependenciesForTmm(entryPath, _fileIndex));
                return;
            }

            var content = GetTextContent(data.Span, entry.RelativePath);
            ShowDependenciesFor(content, entryPath);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read file for dependency analysis:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Handles "Show dependencies" for a .bank root file by finding and reading
    /// the associated soundset file (e.g. greek.bank → soundsets_greek.soundset.XMB).
    /// </summary>
    async Task ShowDependenciesForBankFile(RootFileEntry entry)
    {
        // Extract culture from bank filename: "greek.bank" → "greek"
        var bankName = Path.GetFileNameWithoutExtension(entry.RelativePath);
        var soundsetFileName = $"soundsets_{bankName}.soundset.XMB";

        var soundsetEntries = _fileIndex?.Find(soundsetFileName);
        if (soundsetEntries == null || soundsetEntries.Count == 0)
        {
            _ = ShowError($"Could not find associated soundset file: {soundsetFileName}");
            return;
        }

        try
        {
            using var data = await ReadFromIndexEntryPooledAsync(soundsetEntries[0]);
            if (data == null)
            {
                _ = ShowError($"Could not read soundset file: {soundsetFileName}");
                return;
            }

            using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
            var content = GetTextContent(decompressed.Span, soundsetFileName);
            ShowDependenciesFor(content, soundsetFileName);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read soundset file for dependency analysis:\n{ex.Message}");
        }
    }

    async Task ShowDependenciesForRootFile(RootFileEntry entry)
    {
        if (!Directory.Exists(_rootDirectory)) return;

        try
        {
            var path = Path.Combine(_rootDirectory, entry.RelativePath);
            using var rawData = await PooledBuffer.FromFile(path);
            using var data = BarCompression.EnsureDecompressedPooled(rawData, out _);
            var entryPath = GetRootFullRelativePath(entry);

            if (entry.RelativePath.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase))
            {
                ShowDependenciesForResult(DependencyFinder.FindDependenciesForTmm(entryPath, _fileIndex));
                return;
            }

            var content = GetTextContent(data.Span, entry.RelativePath);
            ShowDependenciesFor(content, entryPath);
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read file for dependency analysis:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Converts decompressed file data to text, handling XMB-to-XML conversion when needed.
    /// </summary>
    static string GetTextContent(ReadOnlySpan<byte> data, string filePath)
    {
        if (Path.GetExtension(filePath).Equals(".xmb", StringComparison.OrdinalIgnoreCase))
            return ConversionHelper.ConvertXmbToXmlText(data) ?? Encoding.UTF8.GetString(data);
        return Encoding.UTF8.GetString(data);
    }

    void ShowDependenciesForResult(DependencyResult result)
    {
        if (_dependenciesWindow != null)
        {
            _dependenciesWindow.LoadDependenciesFromResult(result);
            _dependenciesWindow.Focus();
            return;
        }

        _dependenciesWindow = new DependenciesWindow(this);
        _dependenciesWindow.Closed += (_, _) => _dependenciesWindow = null;
        _dependenciesWindow.Show(this);
        _dependenciesWindow.LoadDependenciesFromResult(result);
    }

    async Task EnsureSoundsetIndexAsync()
    {
        if (_soundsetIndex == null && _fileIndex != null)
            await RebuildSoundsetIndexAsync();
    }

    async void ShowDependenciesFor(string content, string entryPath, string? filterEntityName = null)
    {
        await EnsureSoundsetIndexAsync();
        if (_dependenciesWindow != null)
        {
            _dependenciesWindow.LoadDependencies(content, entryPath, _fileIndex, _soundsetIndex, _stringTableLanguage, filterEntityName);
            _dependenciesWindow.Focus();
            return;
        }

        _dependenciesWindow = new DependenciesWindow(this);
        _dependenciesWindow.Closed += (_, _) =>
        {
            _dependenciesWindow = null;
        };

        _dependenciesWindow.Show(this);
        _dependenciesWindow.LoadDependencies(content, entryPath, _fileIndex, _soundsetIndex, _stringTableLanguage, filterEntityName);
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
            // Try cache first, otherwise load and parse
            if (!_cachedSoundsetFiles.TryGetValue(fileName, out var definitions))
            {
                var fileEntries = _fileIndex.Find(fileName);
                if (fileEntries.Count == 0) continue;

                using var data = await ReadFromIndexEntryPooledAsync(fileEntries[0]);
                if (data == null) continue;

                using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
                var xmlText = GetTextContent(decompressed.Span, fileName);

                try
                {
                    definitions = SoundsetParser.ParseSoundsetXml(xmlText);
                    _cachedSoundsetFiles[fileName] = definitions;
                }
                catch { continue; }
            }

            var culture = SoundsetIndex.ExtractCulture(fileName) ?? "unknown";
            var entries = _fileIndex.Find(fileName);
            if (entries.Count == 0) continue;

            FileIndexEntry? bankFile = null;
            var bankName = culture + ".bank";
            var bankEntries = _fileIndex.Find(bankName);
            if (bankEntries.Count > 0)
                bankFile = bankEntries[0];

            index.AddFromParsedFile(definitions, culture, entries[0], bankFile);
        }

        _soundsetIndex = index;
    }
}
