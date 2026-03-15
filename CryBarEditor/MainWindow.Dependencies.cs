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
        ShowDependenciesFor(content, resolution.SourceFile);
    }

    async Task ShowDependenciesForBarEntry(BarFileEntry entry)
    {
        if (_barStream == null) return;

        try
        {
            using var rawData = await entry.ReadDataRawPooledAsync(_barStream);
            using var data = BarCompression.EnsureDecompressedPooled(rawData, out _);
            var content = GetTextContent(data.Span, entry.RelativePath);
            ShowDependenciesFor(content, GetBARFullRelativePath(entry));
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to read file for dependency analysis:\n{ex.Message}");
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
            var content = GetTextContent(data.Span, entry.RelativePath);
            ShowDependenciesFor(content, GetRootFullRelativePath(entry));
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

    void ShowDependenciesFor(string content, string entryPath)
    {
        if (_dependenciesWindow != null)
        {
            _dependenciesWindow.LoadDependencies(content, entryPath, _fileIndex, _soundsetIndex, _stringTableLanguage);
            _dependenciesWindow.Focus();
            return;
        }

        _dependenciesWindow = new DependenciesWindow(this);
        _dependenciesWindow.Closed += (_, _) =>
        {
            _dependenciesWindow = null;
        };

        _dependenciesWindow.Show(this);
        _dependenciesWindow.LoadDependencies(content, entryPath, _fileIndex, _soundsetIndex, _stringTableLanguage);
    }

    /// <summary>
    /// Builds the SoundsetIndex from all soundset files in the file index.
    /// Called after RebuildFileIndex().
    /// </summary>
    void RebuildSoundsetIndex()
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
            // Reuse cached parsed files from MainWindow.Sound.cs
            if (!_cachedSoundsetFiles.TryGetValue(fileName, out var definitions))
                continue;

            var culture = SoundsetIndex.ExtractCulture(fileName) ?? "unknown";
            var fileEntries = _fileIndex.Find(fileName);
            if (fileEntries.Count == 0) continue;

            var soundsetFileEntry = fileEntries[0];

            // Try to find the associated bank file
            FileIndexEntry? bankFile = null;
            var bankName = culture + ".bank";
            var bankEntries = _fileIndex.Find(bankName);
            if (bankEntries.Count > 0)
                bankFile = bankEntries[0];

            index.AddFromParsedFile(definitions, culture, soundsetFileEntry, bankFile);
        }

        _soundsetIndex = index;
    }
}
