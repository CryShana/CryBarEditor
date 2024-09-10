using CryBar;
using CryBarEditor.Classes;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Reflection;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;

using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tga;
using CommunityToolkit.HighPerformance;
using Configuration = CryBarEditor.Classes.Configuration;

namespace CryBarEditor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    string _entryQuery = "";
    string _filesQuery = "";
    string _rootDirectory = "";
    string _exportRootDirectory = "";
    string _previewedFileName = "";
    string _previewedFileNote = "";
    string _previewedFileData = "";
    BarFile? _barFile = null;
    FileStream? _barStream = null;
    FileEntry? _selectedFileEntry = null;
    List<FileEntry>? _loadedFiles = null;
    FileSystemWatcher? _watcher = null;
    BarFileEntry? _selectedBarEntry = null;


    /// <summary>
    /// This is used to find relative path for Root directory files
    /// </summary>
    const string ROOT_DIRECTORY_NAME = "game";
    const string CONFIG_FILE = "config.json";

    readonly RegistryOptions _registryOptions;
    readonly TextMate.Installation _textMateInstallation;

    #region Properties
    public BarFile? BarFile => _barFile;
    public FileStream? BarFileStream => _barStream;

    public ObservableCollectionExtended<FileEntry> FileEntries { get; } = new();
    public ObservableCollectionExtended<BarFileEntry> Entries { get; } = new();

    public ObservableCollectionExtended<FileEntry> SelectedFileEntries { get; } = new();
    public ObservableCollectionExtended<BarFileEntry> SelectedBarFileEntries { get; } = new();

    public string LoadedBARFilePathOrRelative => _barStream == null ? "No BAR file loaded" :
        (Directory.Exists(_rootDirectory) && _barStream.Name.StartsWith(_rootDirectory) ?
            Path.GetRelativePath(_rootDirectory, _barStream.Name) : _barStream.Name);

    public string ExportRootDirectory
    {
        get => string.IsNullOrEmpty(_exportRootDirectory) ? "No export Root directory selected" : _exportRootDirectory; set
        {
            _exportRootDirectory = value;
            OnPropertyChanged(nameof(ExportRootDirectory));
            OnPropertyChanged(nameof(CanExport));
        }
    }
    public bool CanExport => !string.IsNullOrEmpty(_exportRootDirectory);
    public string RootDirectory
    {
        get => string.IsNullOrEmpty(_rootDirectory) ? "No Root directory loaded" : _rootDirectory; set
        {
            _rootDirectory = value;
            _rootRelevantPathCached = null;
            OnPropertyChanged(nameof(RootDirectory));
            OnPropertyChanged(nameof(RootFileRootPath));
        }
    }

    public string EntryQuery { get => _entryQuery; set { _entryQuery = value; OnPropertyChanged(nameof(EntryQuery)); RefreshBAREntries(); } }
    public string FilesQuery { get => _filesQuery; set { _filesQuery = value; OnPropertyChanged(nameof(FilesQuery)); RefreshFileEntries(); } }
    public string BarFileRootPath => _barFile == null ? "-" : _barFile.RootPath!;
    public string RootFileRootPath => string.IsNullOrEmpty(_rootDirectory) ? "-" : GetRootRelevantPath();
    public string PreviewedFileName { get => string.IsNullOrEmpty(_previewedFileName) ? "No file selected" : _previewedFileName; set { _previewedFileName = value; OnPropertyChanged(nameof(PreviewedFileName)); } }
    public string PreviewedFileNote { get => _previewedFileNote; set { _previewedFileNote = value; OnPropertyChanged(nameof(PreviewedFileNote)); } }
    public string PreviewedFileData { get => _previewedFileData; set { _previewedFileData = value; OnPropertyChanged(nameof(PreviewedFileData)); } }


    public FileEntry? SelectedFileEntry
    {
        get => _selectedFileEntry; set
        {
            if (value == _selectedFileEntry)
                return;

            _selectedFileEntry = value;
            OnPropertyChanged(nameof(SelectedFileEntry));

            // ensure BAR file is not already loaded
            if (value != null && _barStream?.Name == Path.Combine(_rootDirectory, value.RelativePath))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                _ = Preview(value);
            });
        }
    }

    public BarFileEntry? SelectedBarEntry
    {
        get => _selectedBarEntry; set
        {
            if (value == _selectedBarEntry)
                return;

            _selectedBarEntry = value;
            OnPropertyChanged(nameof(SelectedBarEntry));
            _ = Preview(value);
        }
    }
    #endregion

    // export functions
    readonly Func<FileEntry, string> F_GetFullRelativePathRoot;
    readonly Func<BarFileEntry, string> F_GetFullRelativePathBAR;
    readonly Action<FileEntry, FileStream> F_CopyRoot;
    readonly Action<BarFileEntry, FileStream> F_CopyBAR;
    readonly Func<FileEntry, Memory<byte>> F_ReadRoot;
    readonly Func<BarFileEntry, Memory<byte>> F_ReadBAR;

    public MainWindow()
    {
        InitializeComponent();

        TryRestorePreviousConfiguration();

        // set up editor
        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = textEditor.InstallTextMate(_registryOptions);

        // prepare functions (to handle different types of entries - one from Root dir - others from BAR archives)
        F_GetFullRelativePathRoot = f => GetRootFullRelativePath(f);
        F_GetFullRelativePathBAR = f => GetBARFullRelativePath(f);
        F_CopyRoot = (f, stream) =>
        {
            using var from = File.OpenRead(Path.Combine(_rootDirectory, f.RelativePath));
            from.CopyTo(stream);
        };
        F_CopyBAR = (f, stream) => f.CopyData(_barStream!, stream);
        F_ReadRoot = f => File.ReadAllBytes(Path.Combine(_rootDirectory, f.RelativePath));
        F_ReadBAR = f => f.ReadDataRaw(_barStream!);
    }

    #region Button events
    async void LoadBAR_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Open BAR file", [new("BAR file") { Patterns = ["*.bar"] }]);
        if (file == null) return;
        LoadBAR(file);
    }

    async void LoadDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Root directory",
            AllowMultiple = false
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

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select export Root directory",
            AllowMultiple = false
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (folders.Count == 0)
            return;

        if (ExportRootDirectory == RootDirectory)
        {
            // TODO: show error
            return;
        }

        ExportRootDirectory = folders[0].Path.LocalPath;
        SaveConfiguration();
    }
    #endregion

    #region File Watcher events
    void RootDir_Deleted(object sender, FileSystemEventArgs e)
    {
        if (_loadedFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var removed_file = e.FullPath;
        if (!removed_file.StartsWith(_rootDirectory))
            return;

        var relative_path = Path.GetRelativePath(_rootDirectory, removed_file);

        FileEntry? entry_removed = null;
        for (int i = 0; i < _loadedFiles.Count; i++)
        {
            var f = _loadedFiles[i];
            if (f.RelativePath == relative_path)
            {
                entry_removed = f;
                _loadedFiles.RemoveAt(i);
                break;
            }
        }

        if (entry_removed != null)
        {
            if (SelectedFileEntry == entry_removed)
                SelectedFileEntry = null;

            RefreshFileEntries();
        }
    }

    void RootDir_Created(object sender, FileSystemEventArgs e)
    {
        if (_loadedFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var removed_file = e.FullPath;
        if (!removed_file.StartsWith(_rootDirectory))
            return;

        var prev_file = SelectedFileEntry;

        // reload files
        LoadFilesFromRoot();

        // re-select prev file
        if (prev_file != null)
        {
            foreach (var f in _loadedFiles)
            {
                if (f.RelativePath == prev_file.RelativePath)
                {
                    SelectedFileEntry = f;
                    break;
                }
            }
        }
    }

    void RootDir_Renamed(object sender, RenamedEventArgs e)
    {
        if (_loadedFiles == null || !Directory.Exists(_rootDirectory))
            return;

        var renamed_file = e.OldFullPath;
        if (!renamed_file.StartsWith(_rootDirectory) ||
            !e.FullPath.StartsWith(_rootDirectory))
            return;

        var new_entry = new FileEntry(_rootDirectory, e.FullPath);
        var old_relative_path = Path.GetRelativePath(_rootDirectory, renamed_file);

        for (int i = 0; i < _loadedFiles.Count; i++)
        {
            var f = _loadedFiles[i];
            if (f.RelativePath == old_relative_path)
            {
                _loadedFiles[i] = new_entry;
                if (f == SelectedFileEntry)
                {
                    SelectedFileEntry = new_entry;
                }
                break;
            }
        }

        RefreshFileEntries();
    }

    #endregion

    #region Loading files
    public void LoadDir(string dir, bool update_config = true)
    {
        try
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException("Directory not found");

            if (_watcher != null)
            {
                _watcher.Renamed -= RootDir_Renamed;
                _watcher.Created -= RootDir_Created;
                _watcher.Deleted -= RootDir_Deleted;
                _watcher.Dispose();
                _watcher = null;
            }

            RootDirectory = dir;
            LoadFilesFromRoot();

            _watcher = new FileSystemWatcher(RootDirectory);
            _watcher.IncludeSubdirectories = true;
            _watcher.EnableRaisingEvents = true;
            _watcher.Renamed += RootDir_Renamed;
            _watcher.Created += RootDir_Created;
            _watcher.Deleted += RootDir_Deleted;

            if (update_config) SaveConfiguration();
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    void LoadFilesFromRoot()
    {
        _loadedFiles = Directory.GetFiles(_rootDirectory, "*.*", SearchOption.AllDirectories)
                .Select(x => new FileEntry(_rootDirectory, x))
                .ToList();

        SelectedFileEntry = null;

        RefreshFileEntries();
        OnPropertyChanged(nameof(LoadedBARFilePathOrRelative));

        // in case BAR file is loaded from before, if it's within root dir, select it here
        if (_barFile != null && _barStream?.Name.StartsWith(_rootDirectory) == true)
        {
            var relative_path = Path.GetRelativePath(_rootDirectory, _barStream.Name);
            foreach (var file in _loadedFiles)
            {
                if (file.RelativePath == relative_path)
                {
                    SelectedFileEntry = file;
                    break;
                }
            }
        }
    }

    public void LoadBAR(string bar_file)
    {
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

            OnPropertyChanged(nameof(LoadedBARFilePathOrRelative));
            OnPropertyChanged(nameof(BarFileRootPath));

            // if BAR file is contained within root dir, select it there for convenience
            if (Directory.Exists(_rootDirectory) && bar_file.StartsWith(_rootDirectory))
            {
                var relative_path = Path.GetRelativePath(_rootDirectory, bar_file);
                foreach (var f in FileEntries)
                {
                    if (f.RelativePath == relative_path)
                    {
                        SelectedFileEntry = f;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _barStream = null;

            // TODO: show error
        }
    }

    CancellationTokenSource? _previewCsc;

    public async Task Preview(FileEntry? entry)
    {
        if (entry == null || !Directory.Exists(_rootDirectory))
            return;

        var path = Path.Combine(_rootDirectory, entry.RelativePath);
        if (entry.Extension == ".BAR")
        {
            LoadBAR(path);
            return;
        }

        _previewCsc?.Cancel();
        _previewCsc = new();
        PreviewedFileData = $"File Size: {new FileInfo(path).Length}";
        await Preview(entry, F_GetFullRelativePathRoot, F_ReadRoot, _previewCsc.Token);
    }

    public async Task Preview(BarFileEntry? entry)
    {
        if (entry == null || _barStream == null)
            return;

        _previewCsc?.Cancel();
        _previewCsc = new();

        PreviewedFileData = $"BAR Offset: {entry.ContentOffset},   BAR Size: {entry.SizeInArchive},   Actual Size: {entry.SizeUncompressed},   Compressed: {(entry.IsCompressed ? "true" : "false")}";
        await Preview(entry, F_GetFullRelativePathBAR, F_ReadBAR, _previewCsc.Token);
    }

    public async Task Preview<T>(T entry, Func<T, string> get_rel_path,
        Func<T, Memory<byte>> read, CancellationToken token = default)
    {
        var relative_path = get_rel_path(entry);
        var ext = Path.GetExtension(relative_path).ToLower();

        var text = "";
        PreviewedFileName = Path.GetFileName(relative_path);

        var data = BarCompression.EnsureDecompressed(read(entry), out var type);
        PreviewedFileNote = type switch
        {
            CompressionType.L33t => "(Decompressed L33t)",
            CompressionType.Alz4 => "(Decompressed Alz4)",
            _ => ""
        };

        if (IsImage(ext))
        {
            using (var image = SixLabors.ImageSharp.Image.Load(data.Span))
            {
                await SetImagePreview(image, token);
                PreviewedFileNote = $"[{image.Width}x{image.Height}]";
            }

            return;
        }


        if (ext == ".xmb")
        {
            var xml = BarFormatConverter.XMBtoXML(data.Span);
            if (xml != null)
            {
                PreviewedFileNote = "(Converted to XML)";
                text = BarFormatConverter.FormatXML(xml);
                ext = ".xml";
            }
            else
            {
                text = "Failed to parse XMB document";
                ext = ".txt";
            }
        }
        else if (ext == ".ddt")
        {
            var ddt = new DDTImage(data);
            if (!ddt.ParseHeader())
            {
                PreviewedFileNote = "(Failed to parse DDT)";
                return;
            }

            using var image = await BarFormatConverter.ParseDDT(ddt, max_resolution: 1024, token: token);
            if (image == null)
            {
                PreviewedFileNote = "(Failed to parse DDT)";
                return;
            }

            var preview_note = $"(Converted to PNG) [{ddt.Version} {ddt.MipmapOffsets[0].Item3}x{ddt.MipmapOffsets[0].Item4}, {ddt.MipmapOffsets.Length} Mips] ";
            if (image.Width < ddt.BaseWidth || image.Height < ddt.BaseHeight)
                preview_note += $"- Downscaled to {image.Width}x{image.Height}";

            PreviewedFileNote = preview_note;
            await SetImagePreview(image, token);
            return;
        }
        else
        {
            text = Encoding.UTF8.GetString(data.Span);
        }

        await SetImagePreview(null);
        SetTextEditorLanguage(ext);

        textEditor.Text = text;
        textEditor.ScrollTo(0, 0);
    }

    public async Task Export<T>(IList<T> files, bool should_convert,
        Func<T, string> getFullRelPath,
        Action<T, FileStream> copy,
        Func<T, Memory<byte>> read)
    {
        // TODO: show progress bar somewhere

        List<string> failed = new();
        foreach (var f in files)
        {
            var relative_path = getFullRelPath(f);

            try
            {
                // FINALIZE RELATIVE PATH
                var ext = Path.GetExtension(relative_path).ToLower();
                if (should_convert)
                {
                    if (ext == ".xmb")
                    {
                        relative_path = relative_path[..^4];    // remove .XMB extension
                    }
                    else if (ext == ".ddt")
                    {
                        relative_path = relative_path[..^4] + ".tga";    // change .DDT to .TGA
                    }
                }

                // DETERMINE EXPORT PATH
                var exported_path = Path.Combine(_exportRootDirectory, relative_path);

                // CREATE MISSING DIRECTORIES
                var dirs = Path.GetDirectoryName(exported_path);
                if (dirs != null) Directory.CreateDirectory(dirs);

                // CREATE FILE
                using var file = File.Create(exported_path);

                // EXPORT DATA
                if (should_convert)
                {
                    // optionally decompress first
                    var data = BarCompression.EnsureDecompressed(read(f), out _);

                    if (ext == ".xmb")
                    {
                        var xml = BarFormatConverter.XMBtoXML(data.Span)!;
                        var xml_text = BarFormatConverter.FormatXML(xml);
                        var xml_bytes = Encoding.UTF8.GetBytes(xml_text);
                        file.Write(xml_bytes);
                        continue;
                    }
                    else if (ext == ".ddt")
                    {
                        var ddt = new DDTImage(data);
                        var image = await BarFormatConverter.ParseDDT(ddt);
                        if (image == null) throw new InvalidDataException("Failed to convert DDT file");

                        using var memory = new MemoryStream();
                        await image.SaveAsTgaAsync(memory, new TgaEncoder
                        {
                            BitsPerPixel = TgaBitsPerPixel.Pixel32
                        });
                        image.Dispose();

                        file.Write(memory.GetBuffer().AsSpan(0, (int)memory.Position));
                        continue;
                    }
                }

                copy(f, file);
            }
            catch (Exception ex)
            {
                // TODO: handle error and show it somewhere
                failed.Add(relative_path);
            }
        }

        // TODO: maybe show faileda attempts
    }
    #endregion

    #region UI functions
    Bitmap? _previewImage = null;
    public async Task SetImagePreview(SixLabors.ImageSharp.Image? image, CancellationToken token = default)
    {
        if (image == null)
        {
            textEditor.IsVisible = true;
            previewImage.IsVisible = false;

            previewImage.Source = null;
            return;
        }

        if (_previewImage != null)
        {
            _previewImage.Dispose();
            _previewImage = null;
        }

        try
        {
            using (var stream = new MemoryStream())
            {
                await image.SaveAsPngAsync(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder
                {
                    CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
                }, token);

                if (token.IsCancellationRequested || _previewImage != null) return;

                stream.Seek(0, SeekOrigin.Begin);
                _previewImage = new Bitmap(stream);
            }
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            // note error or just ignore it?
            return;
        }

        textEditor.IsVisible = false;
        previewImage.IsVisible = true;
        previewImage.Source = _previewImage;
    }

    public void SetTextEditorLanguage(string extension)
    {
        if (string.Equals(extension, ".xs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".con", StringComparison.OrdinalIgnoreCase))
            extension = ".cpp";

        if (string.Equals(extension, ".composite", StringComparison.OrdinalIgnoreCase))
            extension = ".json";

        var lang = _registryOptions.GetLanguageByExtension(extension);
        if (lang == null)
        {
            _textMateInstallation.SetGrammar("");
            return;
        }

        var scope = _registryOptions.GetScopeByLanguageId(lang.Id);
        _textMateInstallation.SetGrammar(scope);
    }

    public void RefreshFileEntries()
    {
        FileEntries.Clear();
        if (_loadedFiles == null)
            return;

        FileEntries.AddItems(FilterFile(_loadedFiles));
    }

    public void RefreshBAREntries()
    {
        Entries.Clear();
        if (_barFile?.Entries == null)
            return;

        Entries.AddItems(FilterBAR(_barFile.Entries));
    }

    public void RefreshPreview()
    {

    }

    IEnumerable<BarFileEntry> FilterBAR(IEnumerable<BarFileEntry> entries)
    {
        var q = EntryQuery;
        foreach (var e in entries)
        {
            // filter by query
            if (q.Length > 0 && !e.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return e;
        }
    }

    IEnumerable<FileEntry> FilterFile(IEnumerable<FileEntry> entries)
    {
        var q = FilesQuery;
        foreach (var e in entries)
        {
            // filter by query
            if (q.Length > 0 && !e.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return e;
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    #endregion

    #region ContextMenu events
    void MenuItem_CopyFileName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
        {
            // TODO: show error
            return;
        }

        if (list.ItemsSource == Entries)
        {
            // BAR entry list
            var entry = SelectedBarEntry;
            if (entry != null)
            {
                Clipboard?.SetTextAsync(entry.Name);
            }
        }
        else
        {
            // file entry list
            var entry = SelectedFileEntry;
            if (entry != null)
            {
                Clipboard?.SetTextAsync(entry.Name);
            }
        }
    }

    void MenuItem_CopyFilePath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
        {
            // TODO: show error
            return;
        }

        if (list.ItemsSource == Entries)
        {
            // BAR entry list
            var entry = SelectedBarEntry;
            if (entry != null && _barFile != null)
            {
                // we must consider BAR root path to get the correct relative path
                Clipboard?.SetTextAsync(GetBARFullRelativePath(entry));
            }
        }
        else
        {
            // file entry list
            var entry = SelectedFileEntry;
            if (entry != null)
            {
                Clipboard?.SetTextAsync(GetRootFullRelativePath(entry));
            }
        }
    }

    async void MenuItem_ExportSelectedRaw(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        item.IsEnabled = false;

        try
        {
            if (list.ItemsSource == Entries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    async void MenuItem_ExportSelectedConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        item.IsEnabled = false;

        try
        {
            if (list.ItemsSource == Entries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedFileEntries.ToArray();
                await Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    async void MenuItem_ExportSelectedRawConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        item.IsEnabled = false;

        try
        {
            if (list.ItemsSource == Entries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
                await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
                await Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }
    #endregion

    #region Helpers
    public bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".tga" or ".gif" or ".webp" or ".avif" or ".jpx" or ".bmp";

    public string GetBARFullRelativePath(BarFileEntry entry)
    {
        if (_barFile == null)
            return entry.RelativePath;

        return Path.Combine(_barFile.RootPath ?? "", entry.RelativePath);
    }

    string? _rootRelevantPathCached = null;
    string GetRootRelevantPath()
    {
        if (_rootRelevantPathCached != null)
            return _rootRelevantPathCached;

        // root directory could be "\game" or "\game\art" etc... let's find if there is a parent "game" directory anywhere in the chain
        // then we cache this value for later use

        var relevant_path = "";
        var dirs = _rootDirectory.Split('\\');
        foreach (var dir in dirs)
        {
            if (dir == ROOT_DIRECTORY_NAME)
            {
                relevant_path += ROOT_DIRECTORY_NAME + "\\";
                continue;
            }

            if (relevant_path.Length > 0)
            {
                relevant_path += dir + "\\";
            }
        }
        _rootRelevantPathCached = relevant_path;
        return relevant_path;
    }

    public string GetRootFullRelativePath(FileEntry entry)
    {
        if (!Directory.Exists(_rootDirectory))
            return entry.RelativePath;

        return Path.Combine(GetRootRelevantPath(), entry.RelativePath);
    }

    async Task<string?> PickFile(object? sender, string title, IReadOnlyList<FilePickerFileType>? filter = null)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filter
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (files.Count == 0)
            return null;

        return files[0].Path.LocalPath;
    }

    /// <summary>
    /// Picks a new file name for output that doesn't exist yet. If extension null, original extension is used.
    /// Suffix is optionally added before the extension. 
    /// If new filename exists, a counter is added after suffix (unless overwrite is enabled)
    /// </summary>
    string PickOutFile(string file, string suffix = "", string? new_extension = null, bool overwrite = false)
    {
        var ext = new_extension ?? Path.GetExtension(file);
        var dir = Path.GetDirectoryName(file);
        var name = Path.GetFileNameWithoutExtension(file);

        var counter = 0;
        var new_file = "";
        do
        {
            if (counter == 0)
            {
                new_file = Path.Combine(dir ?? "", $"{name}{suffix}{ext}");
                if (overwrite) break;
            }
            else
            {
                new_file = Path.Combine(dir ?? "", $"{name}{suffix}_{counter}{ext}");
            }
            counter++;
        } while (File.Exists(new_file));
        return new_file;
    }
    #endregion

    #region Menu events
    async void MenuItem_ConvertXMLtoXMB(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert XML to XMB");
        if (file == null) return;

        // when converting to XMB, the original extension is retained!
        // so "something.xml" becomes "something.xml.XMB"

        var out_file = PickOutFile(file, suffix: Path.GetExtension(file), new_extension: ".XMB", overwrite: true);

        try
        {
            var xml_text = File.ReadAllText(file);
            var xml = new XmlDocument();
            xml.LoadXml(xml_text);

            var data = BarFormatConverter.XMLtoXMB(xml, CompressionType.Alz4);
            using (var f = File.Create(out_file)) f.Write(data.Span);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    async void MenuItem_ConvertXMBtoXML(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert XMB to XML");
        if (file == null) return;

        var ext = Path.GetExtension(file).ToLower();

        // when converting XMB to XML, the .XMB extension is removed! But this only happens if it was there in the first place!
        var out_file = PickOutFile(file, suffix: (ext == ".xmb" ? "" : ext), new_extension: (ext == ".xmb" ? "" : ".xml"), overwrite: true);
        try
        {
            var xmb_data = File.ReadAllBytes(file);
            var xml_decompressed = BarCompression.EnsureDecompressed(xmb_data, out _);
            var xml = BarFormatConverter.XMBtoXML(xml_decompressed.Span);
            if (xml == null) throw new Exception("Failed to parse XMB file");

            var formatted_xml = BarFormatConverter.FormatXML(xml);
            File.WriteAllText(out_file, formatted_xml);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    async void MenuItem_CompressAlz4(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Compress using Alz4");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_alz4");
        try
        {
            var data = File.ReadAllBytes(file);
            var compressed = BarCompression.CompressAlz4(data);
            using (var f = File.Create(out_file)) f.Write(compressed.Span);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    async void MenuItem_CompressL33t(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Compress using L33t");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_l33t");
        try
        {
            var data = File.ReadAllBytes(file);
            var compressed = BarCompression.CompressL33t(data);
            using (var f = File.Create(out_file)) f.Write(compressed.Span);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    async void MenuItem_Decompress(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Decompress (Alz4, L33t L66t)");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_decompressed");
        try
        {
            var data = File.ReadAllBytes(file);
            var decompressed = BarCompression.EnsureDecompressed(data, out var type);
            using (var f = File.Create(out_file)) f.Write(decompressed.Span);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    async void MenuItem_XStoRM(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Pick XS script to make RM friendly", [new("XS script") { Patterns = ["*.xs"] }]);
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_RMFriendly");
        try
        {
            var class_name = XStoRM.GetSafeClassNameRgx().Replace(Path.GetFileNameWithoutExtension(file), "");
            XStoRM.Convert(file, out_file, class_name);

            // TODO: show success message
        }
        catch (Exception ex)
        {
            // TODO: show error
        }
    }

    SearchWindow? _activeWindow;
    void MenuItem_Search(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_activeWindow != null)
        {
            _activeWindow.Focus();
            return;
        }

        if (_barFile == null && _barStream == null && string.IsNullOrEmpty(_rootDirectory) &&
            !Directory.Exists(_rootDirectory))
        {
            // TODO: show error
            return;
        }

        _activeWindow = new SearchWindow(this);
        _activeWindow.Closed += (a, b) =>
        {
            _activeWindow = null;
        };

        _activeWindow.Show(this);
    }
    #endregion

    void TryRestorePreviousConfiguration()
    {
        var exe_dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var config_path = Path.Combine(exe_dir ?? "", CONFIG_FILE);
        if (!File.Exists(config_path))
            return;

        try
        {
            var config = JsonSerializer.Deserialize(File.ReadAllText(config_path), CryBarJsonContext.Default.Configuration);
            if (config == null)
                return;

            if (Directory.Exists(config.RootDirectory))
                LoadDir(config.RootDirectory, false);

            if (Directory.Exists(config.ExportRootDirectory))
                ExportRootDirectory = config.ExportRootDirectory;
        }
        catch
        {

        }
    }

    void SaveConfiguration()
    {
        var exe_dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var config_path = Path.Combine(exe_dir ?? "", CONFIG_FILE);

        try
        {
            File.WriteAllText(config_path, JsonSerializer.Serialize(new Configuration
            {
                RootDirectory = _rootDirectory,
                ExportRootDirectory = _exportRootDirectory
            }, CryBarJsonContext.Default.Configuration));
        }
        catch
        {

        }
    }
}