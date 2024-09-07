using CryBar;
using CryBarEditor.Classes;

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Collections.Generic;

using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using SixLabors.ImageSharp.Formats.Png;

namespace CryBarEditor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    string _entryQuery = "";
    string _filesQuery = "";
    string _rootDirectory = "";
    string _exportRootDirectory = "";
    string _previewedFileName = "";
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

    readonly RegistryOptions _registryOptions;
    readonly TextMate.Installation _textMateInstallation;

    #region Properties
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
    public string BarFileRootPath => _barFile == null ? "-" : _barFile.RootPath;
    public string RootFileRootPath => string.IsNullOrEmpty(_rootDirectory) ? "-" : GetRootRelevantPath();
    public string PreviewedFileName { get => string.IsNullOrEmpty(_previewedFileName) ? "No file selected" : _previewedFileName; set { _previewedFileName = value; OnPropertyChanged(nameof(PreviewedFileName)); } }

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

            LoadFileEntry(value);
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
            LoadBarFileEntry(value);
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

        // set up editor
        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = textEditor.InstallTextMate(_registryOptions);

        // prepare functions for exporting
        F_GetFullRelativePathRoot = f => GetRootFullRelativePath(f);
        F_GetFullRelativePathBAR = f => GetBARFullRelativePath(f);
        F_CopyRoot = (f, stream) =>
        {
            using var from = File.OpenRead(Path.Combine(_rootDirectory, f.RelativePath));
            from.CopyTo(stream);
        };
        F_CopyBAR = (f, stream) =>
        {
            // any BAR compressed data should automatically be decompressed, this only happens for BAR
            // because external files are expected to be decompressed already with .XMB extension (and similar)
            if (f.IsCompressed)
            {
                var data = f.ReadDataDecompressed(_barStream!);
                stream.Write(data.Span);
                return;
            }
            
            f.CopyData(_barStream!, stream);
        };
        F_ReadRoot = f => File.ReadAllBytes(Path.Combine(_rootDirectory, f.RelativePath));
        F_ReadBAR = f => f.ReadDataDecompressed(_barStream!);
    }

    #region Button events
    async void LoadBAR_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open BAR file",
            AllowMultiple = false,
            FileTypeFilter = [new("BAR file") { Patterns = ["*.bar"] }]
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (files.Count == 0)
            return;

        LoadBAR(files[0].Path.LocalPath);
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
    public void LoadDir(string dir)
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
            if (!file.Load())
            {
                throw new Exception("Failed to load BAR file, possibly invalid of unsupported format");
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

    public void LoadFileEntry(FileEntry? entry)
    {
        if (entry == null || !Directory.Exists(_rootDirectory))
            return;

        var path = Path.Combine(_rootDirectory, entry.RelativePath);
        PreviewedFileName = Path.GetFileName(path);

        // BAR files have to first be opened in separate panel to view their contained files
        if (entry.Extension == ".BAR")
        {
            LoadBAR(path);
            return;
        }

        if (IsImage(entry.Extension.ToLower()))
        {
            var data = File.ReadAllBytes(path);
            SetImagePreview(data);
            return;
        }

        SetImagePreview(null);
        SetTextEditorLanguage(entry.Extension);

        // other files we can try directly previewing
        var size = new FileInfo(path).Length;
        if (size < 300_000)
        {
            var text = File.ReadAllText(path);
            textEditor.Text = text;
        }
        else
        {
            textEditor.Text = "File too large to display in text editor";
        }

        textEditor.ScrollTo(0, 0);
    }

    public void LoadBarFileEntry(BarFileEntry? entry)
    {
        if (entry == null || _barStream == null)
            return;

        var text = "";
        var ext = Path.GetExtension(entry.RelativePath).ToLower();
        PreviewedFileName = entry.Name;

        if (entry.IsXMB)
        {
            // NOTE: both these methods execute under 50ms even for larger files like "proto.xml.XMB", if you notice any lag it's because of UI updates
            // most likely on AvaloniaEdit side or incorrect Visual Tree layout that is destroying virtualization, idk yet
            var data = entry.ReadDataDecompressed(_barStream);
            var xml = BarFileEntry.ConvertXMBtoXML(data.Span);
            if (xml != null)
            {
                text = FormatXML(xml);
                ext = ".xml";
            }
            else
            {
                text = "Failed to parse XMB document";
                ext = ".txt";
            }
        }
        else if (entry.IsText)
        {
            var data = entry.ReadDataDecompressed(_barStream);
            text = Encoding.UTF8.GetString(data.Span);
        }
        else if (IsImage(ext))
        {
            var data = entry.ReadDataRaw(_barStream);
            SetImagePreview(data);
            return;
        }

        SetImagePreview(null);
        SetTextEditorLanguage(ext);

        textEditor.Text = text;
        textEditor.ScrollTo(0, 0);
    }
    #endregion

    #region UI functions
    Bitmap? _previewImage = null;
    public void SetImagePreview(Memory<byte>? data)
    {
        if (data == null)
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


        using (var image = SixLabors.ImageSharp.Image.Load(data.Value.Span))
        using (var stream = new MemoryStream())
        {
            image.Save(stream, new PngEncoder { TransparentColorMode = PngTransparentColorMode.Preserve });
            stream.Seek(0, SeekOrigin.Begin);

            _previewImage = new Bitmap(stream);
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

    void MenuItem_ExportSelectedRaw(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        if (list.ItemsSource == Entries)
        {
            var to_export = SelectedBarFileEntries.ToArray();
            Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
        }
        else
        {
            var to_export = SelectedFileEntries.ToArray();
            Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
        }
    }

    void MenuItem_ExportSelectedConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        if (list.ItemsSource == Entries)
        {
            var to_export = SelectedBarFileEntries.ToArray();
            Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
        }
        else
        {
            var to_export = SelectedFileEntries.ToArray();
            Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
        }
    }

    void MenuItem_ExportSelectedRawConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        if (list.ItemsSource == Entries)
        {
            var to_export = SelectedBarFileEntries.ToArray();
            Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
        }
        else
        {
            var to_export = SelectedFileEntries.ToArray();
            Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
        }
    }

    void Export<T>(IList<T> files, bool should_convert,
        Func<T, string> getFullRelativePath,
        Action<T, FileStream> copy,
        Func<T, Memory<byte>> read_decompressed)
    {
        List<string> failed = new();
        foreach (var f in files)
        {
            var relative_path = getFullRelativePath(f);

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
                    if (ext == ".xmb")
                    {
                        var data = read_decompressed(f);
                        var xml = BarFileEntry.ConvertXMBtoXML(data.Span)!;
                        var xml_text = FormatXML(xml);
                        var xml_bytes = Encoding.UTF8.GetBytes(xml_text);
                        file.Write(xml_bytes);
                        continue;
                    }
                    else if (ext == ".ddt")
                    {
                        // TODO: convert to TGA
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

    }
    #endregion

    #region Helpers

    public string FormatXML(XmlDocument xml)
    {
        var sb = new StringBuilder();
        var rsettings = new XmlReaderSettings
        {
            IgnoreWhitespace = true
        };

        var wsettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = true
        };

        // for some reason I gotta read it first while ignoring whitespaces, to get proper formatting when writing it again... is there a better way?
        using (var reader = XmlReader.Create(new StringReader(xml.InnerXml), rsettings))
        using (var writer = XmlWriter.Create(sb, wsettings))
        {
            writer.WriteNode(reader, true);
        }

        return sb.ToString();
    }

    public bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".tga" or ".gif" or ".webp" or ".avif" or ".jpx" or ".bmp";

    public string GetBARFullRelativePath(BarFileEntry entry)
    {
        if (_barFile == null)
            return entry.RelativePath;

        return Path.Combine(_barFile.RootPath, entry.RelativePath);
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
    #endregion
}