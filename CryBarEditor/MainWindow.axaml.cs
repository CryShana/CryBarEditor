using CryBar;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

using System;
using System.Xml;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Collections.Generic;

using CryBarEditor.Classes;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

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

    readonly RegistryOptions _registryOptions;
    readonly TextMate.Installation _textMateInstallation;

    public ObservableCollectionExtended<FileEntry> FileEntries { get; } = new();
    public ObservableCollectionExtended<BarFileEntry> Entries { get; } = new();

    public string LoadedBARFilePathOrRelative => _barStream == null ? "No BAR file loaded" :
        (Directory.Exists(_rootDirectory) && _barStream.Name.StartsWith(_rootDirectory) ?
            Path.GetRelativePath(_rootDirectory, _barStream.Name) : _barStream.Name);

    public string ExportRootDirectory { get => string.IsNullOrEmpty(_exportRootDirectory) ? "No export Root directory selected" : _exportRootDirectory; set { _exportRootDirectory = value; OnPropertyChanged(nameof(ExportRootDirectory)); } }
    public string RootDirectory { get => string.IsNullOrEmpty(_rootDirectory) ? "No Root directory loaded" : _rootDirectory; set { _rootDirectory = value; OnPropertyChanged(nameof(RootDirectory)); } }
    public string EntryQuery { get => _entryQuery; set { _entryQuery = value; OnPropertyChanged(nameof(EntryQuery)); RefreshBAREntries(); } }
    public string FilesQuery { get => _filesQuery; set { _filesQuery = value; OnPropertyChanged(nameof(FilesQuery)); RefreshFileEntries(); } }
    public string BarFileRootPath => _barFile == null ? "-" : _barFile.RootPath;
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

    public MainWindow()
    {
        InitializeComponent();

        // set up editor
        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = textEditor.InstallTextMate(_registryOptions);
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
        {
            // TODO: only clear BAR entries or preview if prev. file was unselected
            return;
        }

        var path = Path.Combine(_rootDirectory, entry.RelativePath);

        // BAR files have to first be opened in separate panel to view their contained files
        if (entry.Extension == ".BAR")
        {
            LoadBAR(path);
            return;
        }

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

        PreviewedFileName = Path.GetFileName(path);
        textEditor.ScrollTo(0, 0);
    }

    public void LoadBarFileEntry(BarFileEntry? entry)
    {
        if (entry == null || _barStream == null)
        {
            return;
        }

        var text = "";
        var ext = Path.GetExtension(entry.RelativePath).ToLower();

        if (entry.IsXMB)
        {
            // NOTE: both these methods execute under 50ms even for larger files like "proto.xml.XMB", if you notice any lag it's because of UI updates
            // most likely on AvaloniaEdit side or incorrect Visual Tree layout that is destroying virtualization, idk yet
            var data = entry.ReadDataDecompressed(_barStream);
            var xml = BarFileEntry.ConvertXMBtoXML(data.Span);
            if (xml != null)
            {
                var sb = new StringWriter();

                var settings = new XmlWriterSettings
                {
                    Indent = true
                };

                using (var writer = XmlWriter.Create(sb, settings))
                {
                    xml.Save(writer);
                }

                text = sb.ToString();
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

        SetTextEditorLanguage(ext);

        textEditor.Text = text;
        textEditor.ScrollTo(0, 0);
        PreviewedFileName = entry.Name;
    }
    #endregion

    #region UI functions
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
}