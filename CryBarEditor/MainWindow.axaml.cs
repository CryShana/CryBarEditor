using CryBar;
using CryBarEditor.Classes;

using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Text;
using System.Buffers;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using AvaloniaEdit.Folding;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

using CommunityToolkit.HighPerformance;
using Configuration = CryBarEditor.Classes.Configuration;
using System.Runtime.InteropServices;

namespace CryBarEditor;

public partial class MainWindow : SimpleWindow
{
    string _bankQuery = "";
    string _entryQuery = "";
    string _filesQuery = "";
    string _rootDirectory = "";
    string _exportRootDirectory = "";
    string _previewedFileName = "";
    string _previewedFileNote = "";
    string _previewedFileData = "";
    string? _latestVersion = null;
    public string _searchExclusionFilter = "";
    int _contextSelectedItemsCount = 0;

    FMODBank? _fmodBank = null;
    BarFile? _barFile = null;
    FileStream? _barStream = null;
    BarFileEntry? _selectedBarEntry = null;
    FMODEvent? _selectedBankEntry = null;
    RootFileEntry? _selectedRootFileEntry = null;
    List<RootFileEntry>? _loadedRootFiles = null;

    double _imageZoomLevel = 1.0;
    FileSystemWatcher? _rootWatcher = null;
    FileSystemWatcher? _exportWatcher = null;

    /// <summary>
    /// This is used to find relative path for Root directory files
    /// </summary>
    const string ROOT_DIRECTORY_NAME = "game";
    const string CONFIG_FILE = "config.json";

    FoldingManager? _foldingManager;
    readonly RegistryOptions _registryOptions;
    readonly TextMate.Installation _textMateInstallation;
    string _previewText = "";
    public string PreviewText => _previewText;

    #region Properties
    public BarFile? BarFile => _barFile;
    public FileStream? BarFileStream => _barStream;
    public event Action<BarFile?, FileStream?>? OnBarFileLoaded;

    public FMODBank? FmodBank => _fmodBank;
    public bool IsBankFileSelected => FmodBank != null;

    public ObservableCollectionExtended<RootFileEntry> RootFileEntries { get; } = new();
    public ObservableCollectionExtended<BarFileEntry> BarEntries { get; } = new();

    public ObservableCollectionExtended<RootFileEntry> SelectedRootFileEntries { get; } = new();
    public ObservableCollectionExtended<BarFileEntry> SelectedBarFileEntries { get; } = new();

    public ObservableCollectionExtended<FMODEvent> BankEntries { get; } = new();
    public ObservableCollectionExtended<FMODEvent> SelectedBankEntries { get; } = new();

    // this is used just to notify override icons to update
    public bool ShowOverridenIcons => true;

    public string LoadedBARFilePathOrRelative => _barStream == null ? "No BAR file loaded" :
        (Directory.Exists(_rootDirectory) && _barStream.Name.StartsWith(_rootDirectory) ?
            Path.GetRelativePath(_rootDirectory, _barStream.Name) : _barStream.Name);

    public string LoadedBankFilePathOrRelative => _fmodBank == null ? "No FMOD bank loaded" :
        (Directory.Exists(_rootDirectory) && _fmodBank.BankPath.StartsWith(_rootDirectory) ?
            Path.GetRelativePath(_rootDirectory, _fmodBank.BankPath) : _fmodBank.BankPath);

    public string ExportRootDirectory
    {
        get => string.IsNullOrEmpty(_exportRootDirectory) ? "No export Root directory selected" : _exportRootDirectory; set
        {
            _exportRootDirectory = value;
            OnSelfChanged();
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanExportAndIsDDT));
            ResetExportWatcher();
        }
    }
    public bool CanExport => !string.IsNullOrEmpty(_exportRootDirectory);
    public bool CanExportAndIsDDT => !string.IsNullOrEmpty(_exportRootDirectory) && SelectedIsDDT;
    public string RootDirectory
    {
        get => string.IsNullOrEmpty(_rootDirectory) ? "No Root directory loaded" : _rootDirectory; set
        {
            _rootDirectory = value;
            _rootRelevantPathCached = null;
            OnSelfChanged();
            OnPropertyChanged(nameof(RootFileRootPath));
        }
    }

    public string BankQuery { get => _bankQuery; set { _bankQuery = value; OnSelfChanged(); RefreshBankEntries(); } }
    public string EntryQuery { get => _entryQuery; set { _entryQuery = value; OnSelfChanged(); RefreshBAREntries(); } }
    public string FilesQuery { get => _filesQuery; set { _filesQuery = value; OnSelfChanged(); RefreshFileEntries(); } }
    public string BarFileRootPath => _barFile == null ? "-" : _barFile.RootPath!;
    public string RootFileRootPath => string.IsNullOrEmpty(_rootDirectory) ? "-" : GetRootRelevantPath();
    public string PreviewedFileName { get => string.IsNullOrEmpty(_previewedFileName) ? "No file selected" : _previewedFileName; set { _previewedFileName = value; OnPropertyChanged(nameof(PreviewedFileName)); } }
    public string PreviewedFileNote { get => _previewedFileNote; set { _previewedFileNote = value; OnSelfChanged(); } }
    public string PreviewedFileData { get => _previewedFileData; set { _previewedFileData = value; OnSelfChanged(); } }
    public bool SelectedIsDDT =>
        Path.GetExtension(SelectedRootFileEntry?.RelativePath ?? "").ToLower() == ".ddt" ||
        Path.GetExtension(SelectedBarEntry?.RelativePath ?? "").ToLower() == ".ddt";
    public bool SelectedCanHaveAdditiveMod
        => AdditiveModding.IsSupportedFor(SelectedBarEntry?.RelativePath ?? SelectedRootFileEntry?.RelativePath, out _);
    public int ContextSelectedItemsCount { get => _contextSelectedItemsCount; set { _contextSelectedItemsCount = value; OnSelfChanged(); } }

    public RootFileEntry? SelectedRootFileEntry
    {
        get => _selectedRootFileEntry; set
        {
            if (value == _selectedRootFileEntry)
                return;

            _selectedRootFileEntry = value;
            OnSelfChanged();
            RefreshSelectedProperties();

            // ensure BAR file is not already loaded
            if (value != null && _barStream?.Name == Path.Combine(_rootDirectory, value.RelativePath))
                return;

            // sometimes it can be called outside UI thread, so we use Dispatcher to be safe
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
            OnSelfChanged();
            RefreshSelectedProperties();

            _ = Preview(value);
        }
    }

    public FMODEvent? SelectedBankEntry
    {
        get => _selectedBankEntry; set
        {
            if (value == _selectedBankEntry)
                return;

            _selectedBankEntry = value;
            OnSelfChanged();
            RefreshSelectedProperties();

            _ = Preview(value);
        }
    }
    void RefreshSelectedProperties()
    {
        OnPropertyChanged(nameof(SelectedIsDDT));
        OnPropertyChanged(nameof(CanExportAndIsDDT));
        OnPropertyChanged(nameof(SelectedCanHaveAdditiveMod));
    }
    #endregion

    // export functions
    readonly Func<RootFileEntry, string> F_GetFullRelativePathRoot;
    readonly Func<BarFileEntry, string> F_GetFullRelativePathBAR;
    readonly Action<RootFileEntry, FileStream> F_CopyRoot;
    readonly Action<BarFileEntry, FileStream> F_CopyBAR;
    readonly Func<RootFileEntry, Memory<byte>> F_ReadRoot;
    readonly Func<BarFileEntry, Memory<byte>> F_ReadBAR;
    readonly Func<RootFileEntry, long> F_ReadSizeRoot;
    readonly Func<BarFileEntry, long> F_ReadSizeBAR;

    public MainWindow()
    {
        // Needed to work when AOT compiled
        NativeLibrary.SetDllImportResolver(typeof(FMOD.Studio.STUDIO_VERSION).Assembly,
            (library_name, assembly, search_path) =>
            {
                if (library_name.Contains("fmod"))
                {
                    var libPath = Path.Combine(AppContext.BaseDirectory, "lib", $"{Path.GetFileName(library_name)}.dll");
                    return NativeLibrary.Load(libPath);
                }
                return IntPtr.Zero;
            });

        InitializeComponent();

        TryRestorePreviousConfiguration();

        // set up editor
        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = _txtEditor.InstallTextMate(_registryOptions);

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
        F_ReadSizeRoot = f => new FileInfo(Path.Combine(_rootDirectory, f.RelativePath)).Length;
        F_ReadSizeBAR = f => f.SizeUncompressed;

        // events
        PointerWheelChanged += ScrollChanged;

        // append version to window title 
        var v = GetCurrentVersion();
        Title = $"{Title} {v.Major}.{v.Minor}.{v.Build}";

        // check for newer version
        _ = TryGetLatestVersion().ContinueWith(s =>
        {
            if (!s.IsCompletedSuccessfully || s.Result.version == null) return;
            _latestVersion = s.Result.version;

            if (IsVersionNewer(s.Result.version))
            {
                // only show if we haven't shown yet for this version
                var last_version_checked = _lastConfiguration?.LastVersionCheck;
                var should_show = last_version_checked == null || s.Result.version != last_version_checked;
                if (should_show)
                {
                    SaveConfiguration();

                    Dispatcher.UIThread.Post(async () =>
                    {
                        var prompt = new Prompt(PromptType.Information, "Version " + s.Result.version, "New version is available for download:\n\n" + s.Result.link + "");
                        await prompt.ShowDialog(this);
                    });
                }
            }
        });
    }

    #region Version
    [GeneratedRegex(@"href=""(?<link>[^""]+tag/(?<version>\d+\.\d+\.\d+))""")]
    public static partial Regex ReleasesVersionRgx();
    public async Task<(string? link, string? version)> TryGetLatestVersion()
    {
        const string ReleasesLink = @"https://github.com/CryShana/CryBarEditor/releases";
        try
        {
            var response = await new HttpClient().GetAsync(ReleasesLink);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var matches = ReleasesVersionRgx().Matches(content);

            // first version is valid
            foreach (Match match in matches)
            {
                var link = "https://github.com" + match.Groups["link"].Value;
                var version = match.Groups["version"].Value;
                return (link, version);
            }
        }
        catch { }

        return default;
    }

    public Version GetCurrentVersion() => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)")]
    public static partial Regex VersionRgx();
    public bool IsVersionNewer(string version)
    {
        var match = VersionRgx().Match(version);
        if (!match.Success) return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var build = int.Parse(match.Groups["build"].Value);

        var v = GetCurrentVersion();

        return major > v.Major ||
            (major == v.Major && minor > v.Minor) ||
            (major == v.Major && minor == v.Minor && build > v.Build);
    }
    #endregion

    #region UI events
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

    void ScrollChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        // is going to be -1 or 1
        var delta = e.Delta.Y;
        if (delta == 0)
            return;

        // just to make sure, don't want any surprises
        if (delta < 0) delta = -1;
        else if (delta > 0) delta = 1;

        // HANDLE IMAGE ZOOMING IN
        if (!_imgPreview.IsVisible || _imgPreview.Source == null)
            return;

        // only zoom in if pointer within preview grid
        var position = e.GetPosition(_gridPreview);
        if (position.X < 0 || position.Y < 0 ||
            position.X > _gridPreview.Bounds.Width ||
            position.Y > _gridPreview.Bounds.Height)
            return;

        // it has to be exponential for percieved zoom step to be kinda consistent
        var zoom_speed = 0.1;
        var zoom_step = Math.Pow(1 + zoom_speed, _imageZoomLevel) - 1;
        var zoom_factor_change = delta * zoom_step;
        _imageZoomLevel = Math.Clamp(_imageZoomLevel + zoom_factor_change, 0.1, 10.0);

        // scale image
        RefreshImageScale();
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

            if (_rootWatcher != null)
            {
                _rootWatcher.Renamed -= RootDir_Renamed;
                _rootWatcher.Created -= RootDir_Created;
                _rootWatcher.Deleted -= RootDir_Deleted;
                _rootWatcher.Dispose();
                _rootWatcher = null;
            }

            RootDirectory = dir;
            LoadFilesFromRoot();

            _rootWatcher = new FileSystemWatcher(RootDirectory);
            _rootWatcher.IncludeSubdirectories = true;
            _rootWatcher.EnableRaisingEvents = true;
            _rootWatcher.Renamed += RootDir_Renamed;
            _rootWatcher.Created += RootDir_Created;
            _rootWatcher.Deleted += RootDir_Deleted;

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

    CancellationTokenSource? _previewCsc;

    public async Task Preview(RootFileEntry? entry)
    {
        if (entry == null || !Directory.Exists(_rootDirectory))
            return;

        var path = Path.Combine(_rootDirectory, entry.RelativePath);
        if (entry.Extension == ".BAR")
        {
            LoadBAR(path);
            return;
        }

        if (entry.Extension == ".BANK")
        {
            LoadFMODBank(path);
            return;
        }

        _previewCsc?.Cancel();
        _previewCsc = new();
        PreviewedFileData = $"File Size: {new FileInfo(path).Length}";
        await Preview(entry, F_GetFullRelativePathRoot, F_ReadSizeRoot, F_ReadRoot, _previewCsc.Token);
    }

    public async Task Preview(BarFileEntry? entry)
    {
        if (entry == null || _barStream == null)
            return;

        _previewCsc?.Cancel();
        _previewCsc = new();

        PreviewedFileData = $"BAR Offset: {entry.ContentOffset},   BAR Size: {entry.SizeInArchive},   Actual Size: {entry.SizeUncompressed},   Compressed: {(entry.IsCompressed ? "true" : "false")}";
        await Preview(entry, F_GetFullRelativePathBAR, F_ReadSizeBAR, F_ReadBAR, _previewCsc.Token);
    }

    public async Task Preview(FMODEvent? e)
    {
        if (e == null || _fmodBank == null)
            return;

        PreviewedFileName = $"FMOD event: \"${e.Path}\"";
        PreviewedFileNote = "";
        PreviewedFileData = $"Length: {e.LengthMs}ms";

        await SetImagePreview(null);
        SetEditorText(".txt",
        $"""
        Id:         {e.Id}
        Path:       {e.Path}
        Length:     {e.LengthMs}ms
        Is3D:       {e.Is3D} (Distance: {e.MinDistance} - {e.MaxDistance})
        IsOneshot:  {e.IsOneshot}
        IsSnapshot: {e.IsSnapshot}
        Doppler:    {e.IsDopplerEnabled}

        Parameters: 
        - {string.Join("\n- ", e.Parameters)}
        """);
    }

    public async Task Preview<T>(T entry, Func<T, string> get_rel_path,
        Func<T, long> get_read_size, Func<T, Memory<byte>> read, CancellationToken token = default)
    {
        const int MAX_DATA_SIZE = 1_500_000_000;    // 1.5 GB
        const int MAX_DATA_TEXT_SIZE = 100_000_000; // 100 MB

        var relative_path = get_rel_path(entry);
        var ext = Path.GetExtension(relative_path).ToLower();
        var text = "";

        PreviewedFileName = Path.GetFileName(relative_path);

        try
        {
            var data_size = get_read_size(entry);
            if (data_size > MAX_DATA_SIZE)
            {
                await SetImagePreview(null);
                SetEditorText(".txt", "Data too big to be loaded for preview");
                return;
            }

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

                var preview_note = $"[{ddt.Version} {ddt.MipmapOffsets[0].Item3}x{ddt.MipmapOffsets[0].Item4}, {ddt.MipmapOffsets.Length} Mips, " +
                    $"Usage: {(int)ddt.UsageFlag}, Format: {(int)ddt.FormatFlag}, Alpha: {(int)ddt.AlphaFlag}] ";

                if (image.Width < ddt.BaseWidth || image.Height < ddt.BaseHeight)
                    preview_note += $"- Downscaled to {image.Width}x{image.Height}";

                PreviewedFileNote = preview_note;
                await SetImagePreview(image, token);
                return;
            }
            else if (ext == ".tmm")
            {
                var tmm = new TmmModel(data);
                if (!tmm.ParseHeader())
                {
                    PreviewedFileNote = "(Failed to parse TMM)";
                    return;
                }

                if (tmm.ModelNames.Length == 0)
                {
                    PreviewedFileNote = "(TMM contains 0 models)";
                    return;
                }

                // TODO: show
                tmm.ParseModel(0);

                PreviewedFileNote = "(Preview not yet supported)";
            }
            else
            {
                if (data_size > MAX_DATA_TEXT_SIZE)
                {
                    // to large for text file
                    await SetImagePreview(null);
                    SetEditorText(".txt", "Data too big to preview as text");
                    return;
                }

                var unicode = DetectIfUnicode(data.Span);
                PreviewedFileNote = unicode ? "[Unicode]" : "[UTF-8]";

                // set text
                text = unicode ?
                    Encoding.Unicode.GetString(data.Span) :
                    Encoding.UTF8.GetString(data.Span);

                var xml_tags = GetXMLTagRegex().Count(text);
                if (xml_tags > 0) ext = ".xml";

                //if (ext == ".simjson")
                //   ext = ".json";
            }
        }
        catch (UnknownImageFormatException)
        {
            ext = ".txt";
            text = "Preview failed: Unrecognized image format";
        }
        catch (Exception ex)
        {
            ext = ".txt";
            text = "Preview failed: " + ex.Message;
        }

        await SetImagePreview(null);
        SetEditorText(ext, text);
    }

    public async Task Export<T>(IList<T> files, bool should_convert,
        Func<T, string> getFullRelPath,
        Action<T, FileStream> copy,
        Func<T, Memory<byte>> read)
    {
        var progress = new Progress<string?>();
        IProgress<string?> p = progress;

        _ = ShowProgress($"Exporting {files.Count} files", progress);

        p.Report("Starting export...");

        var sw = Stopwatch.StartNew();

        List<string> failed = new();
        foreach (var f in files)
        {
            var relative_path = getFullRelPath(f);
            p.Report($"Exporting '{Path.GetFileName(relative_path)}'");

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
            catch
            {
                // TODO: handle error and show it somewhere
                failed.Add(relative_path);
            }
        }

        sw.Stop();

        if (failed.Count > 0)
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds with {failed.Count} failed exports:\n"
                + string.Join(",", failed.Select(x => Path.GetFileName(x))));
        }
        else
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds");
        }

        p.Report(null);
    }
    #endregion

    #region UI functions
    void RefreshImageScale()
    {
        _imgPreview.RenderTransformOrigin = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Absolute);
        _imgPreview.RenderTransform = new ScaleTransform(_imageZoomLevel, _imageZoomLevel);
    }

    Bitmap? _previewImage = null;
    public async Task SetImagePreview(SixLabors.ImageSharp.Image? image, CancellationToken token = default)
    {
        if (image == null)
        {
            _txtEditor.IsVisible = true;
            _imgPreview.IsVisible = false;
            _imgPreview.Source = null;
            return;
        }

        if (_previewImage != null)
        {
            _previewImage.Dispose();
            _previewImage = null;
        }

        _imageZoomLevel = 1.0;
        RefreshImageScale();

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

        _txtEditor.IsVisible = false;
        _imgPreview.IsVisible = true;
        _imgPreview.Source = _previewImage;
    }

    public void SetEditorText(string extension, string text)
    {
        // ANY CLEANUP
        if (_foldingManager != null)
        {
            _foldingManager.Clear();
            FoldingManager.Uninstall(_foldingManager);
        }

        // PREPARE EXTENSION
        var ext = extension.ToLower();
        if (ext is ".xs" or ".con")
            ext = ".cpp";

        if (ext is ".composite")
            ext = ".json";

        if (ext is ".xaml")
            ext = ".xml";

        // SET GRAMMAR + TEXT
        var lang = _registryOptions.GetLanguageByExtension(ext);
        var scope = lang == null ? null : _registryOptions.GetScopeByLanguageId(lang.Id);

        _previewText = text;
        _txtEditor.Document = new TextDocument(text);
        _textMateInstallation.SetGrammar(scope);

        // HANDLE FOLDING
        if (ext is ".xml")
        {
            _foldingManager = FoldingManager.Install(_txtEditor.TextArea);
            var strategy = new XmlFoldingStrategy();
            strategy.UpdateFoldings(_foldingManager, _txtEditor.Document);
        }

        // SCROLL TO TOP (must be with a delay, because the document gets moved a bit)
        Task.Delay(50).ContinueWith((t) => Dispatcher.UIThread.Post(() => _txtEditor.ScrollTo(0, 0)));
    }

    public void RefreshFileEntries()
    {
        RootFileEntries.Clear();
        if (_loadedRootFiles == null)
            return;

        RootFileEntries.AddItems(FilterFile(_loadedRootFiles));
    }

    public void RefreshBAREntries()
    {
        BarEntries.Clear();
        if (_barFile?.Entries == null)
            return;

        BarEntries.AddItems(FilterBAR(_barFile.Entries));
    }

    public void RefreshBankEntries()
    {
        // TODO: filter
        BankEntries.Clear();
        if (_fmodBank?.Events == null)
            return;

        BankEntries.AddItems(FilterBankEvents(_fmodBank.Events));
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

    IEnumerable<RootFileEntry> FilterFile(IEnumerable<RootFileEntry> entries)
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

    IEnumerable<FMODEvent> FilterBankEvents(IEnumerable<FMODEvent> events)
    {
        var q = BankQuery;
        foreach (var e in events)
        {
            // filter by query
            if (q.Length > 0 && !e.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return e;
        }
    }
    #endregion

    #region ContextMenu events
    void MenuItem_CopyFileName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null) return;

        if (list.ItemsSource == BarEntries)
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
            var entry = SelectedRootFileEntry;
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
        if (list == null) return;

        if (list.ItemsSource == BarEntries)
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
            var entry = SelectedRootFileEntry;
            if (entry != null)
            {
                Clipboard?.SetTextAsync(GetRootFullRelativePath(entry));
            }
        }
    }

    void MenuItem_ExportSelectedOpenDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var item = (MenuItem)sender!;
        var list = item.Parent?.Parent?.Parent as ListBox;
        if (list == null)
            return;

        string relative_path_full;
        if (list.ItemsSource == BarEntries && SelectedBarEntry != null)
        {
            relative_path_full = GetBARFullRelativePath(SelectedBarEntry);
        }
        else if (list.ItemsSource == RootFileEntries && SelectedRootFileEntry != null)
        {
            relative_path_full = GetRootFullRelativePath(SelectedRootFileEntry);
        }
        else
        {
            return;
        }

        var export_path = Path.Combine(_exportRootDirectory, relative_path_full);
        var export_dir = Path.GetDirectoryName(export_path);
        if (!string.IsNullOrEmpty(export_dir))
            Directory.CreateDirectory(export_dir);

        // TODO: only for windows, maybe make methods for other platforms? But will people even use it elsewhere?

        var process_info = new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = $"explorer.exe",
            Arguments = $"\"{export_dir}\""
        };

        Process.Start(process_info);
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
            if (list.ItemsSource == BarEntries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedRootFileEntries.ToArray();
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
            if (list.ItemsSource == BarEntries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedRootFileEntries.ToArray();
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
            if (list.ItemsSource == BarEntries)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
                await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedRootFileEntries.ToArray();
                await Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
                await Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    async void MenuItem_ReplaceImageAndExportDDT(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            string relative_path_full = "";
            string title = "";
            Memory<byte> data;
            if (list.ItemsSource == BarEntries)
            {
                if (SelectedBarEntry == null || _barStream == null)
                    return;

                relative_path_full = GetBARFullRelativePath(SelectedBarEntry);
                data = SelectedBarEntry.ReadDataDecompressed(_barStream);
                title = $"Pick image to replace {Path.GetFileName(SelectedBarEntry.RelativePath)}";
            }
            else
            {
                if (SelectedRootFileEntry == null || !Directory.Exists(_rootDirectory))
                    return;

                relative_path_full = GetRootFullRelativePath(SelectedRootFileEntry);
                data = BarCompression.EnsureDecompressed(File.ReadAllBytes(Path.Combine(_rootDirectory, SelectedRootFileEntry.RelativePath)), out _);
                title = $"Pick image to replace {Path.GetFileName(SelectedRootFileEntry.RelativePath)}";
            }

            var ddt = new DDTImage(data);
            if (!ddt.ParseHeader())
            {
                _ = ShowError("Failed to parse DDT header");
                return;
            }

            var file = await PickFile(sender, title, [new("Image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.webp"] }]);
            if (file == null) return;

            var progress = new Progress<string?>();
            IProgress<string?> p = progress;
            _ = ShowProgress($"Exporting with new image", progress);

            try
            {
                var sw = Stopwatch.StartNew();

                p.Report("Loading target image");
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(file);

                p.Report("Encoding image into DDT");
                var modified_ddt_data = await DDTImage.EncodeImageToDDT(image, ddt.Version, ddt.UsageFlag, ddt.AlphaFlag, ddt.FormatFlag, ddt.MipmapLevels, ddt.ColorTable);

                // CHECK: should I consider decompression of the original and apply it?

                // export it
                p.Report("Exporting final DDT");
                var output_path = Path.Combine(_exportRootDirectory, relative_path_full);
                var dir = Path.GetDirectoryName(output_path);
                if (dir != null) Directory.CreateDirectory(dir);

                using (var out_file = File.Create(output_path))
                    out_file.Write(modified_ddt_data.Span);

                sw.Stop();
                p.Report($"Exported in {sw.Elapsed.TotalSeconds:0.00} seconds");
            }
            catch (Exception ex)
            {
                p.Report("Failed to export: " + ex.Message);
            }
            finally
            {
                p.Report(null);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    void MenuItem_CreateNewAdditiveMod(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            string relative_path_full = "";
            if (list.ItemsSource == BarEntries)
            {
                if (SelectedBarEntry == null)
                    return;

                relative_path_full = GetBARFullRelativePath(SelectedBarEntry);
            }
            else
            {
                if (SelectedRootFileEntry == null)
                    return;

                relative_path_full = GetRootFullRelativePath(SelectedRootFileEntry);
            }

            if (!AdditiveModding.IsSupportedFor(relative_path_full, out var format))
                return;

            var output_dir = Path.Combine(_exportRootDirectory, Path.GetDirectoryName(relative_path_full) ?? "");
            Directory.CreateDirectory(output_dir);

            var output_path = Path.Combine(output_dir, format.FileName);
            File.WriteAllText(output_path, format.Content);

            _ = ShowSuccess("Additive mod created, new file:\n" + Path.GetFileName(output_path));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to create additive mod:\n" + ex.Message);
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    CancellationTokenSource? bank_play_csc = null;
    void BankItem_Play(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null || FmodBank == null)
            return;

        bank_play_csc?.Cancel();
        bank_play_csc = new();
        _ = SelectedBankEntry.Play(bank_play_csc.Token);
    }
    #endregion

    #region Helpers
    public bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".tga" or ".gif" or ".webp" or ".avif" or ".jpx" or ".bmp";

    public bool IsFileOverriden(string relative_path_full)
    {
        if (string.IsNullOrEmpty(relative_path_full))
            return false;

        if (!CanExport)
            return false;

        var exported_path = Path.Combine(_exportRootDirectory, relative_path_full);
        if (File.Exists(exported_path))
            return true;

        // also check files that work without the XMB extension
        if (exported_path.EndsWith(".xmb", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(exported_path[..^4]))
            return true;

        return false;
    }

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
        var dirs = _rootDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries);
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

    public string GetRootFullRelativePath(RootFileEntry entry)
    {
        if (!Directory.Exists(_rootDirectory))
            return entry.RelativePath;

        return Path.Combine(GetRootRelevantPath(), entry.RelativePath);
    }

    async Task<string?> PickFile(object? sender, string title, IReadOnlyList<FilePickerFileType>? filter = null, string? suggested_folder = null)
    {
        var btn = sender as Button;
        if (btn != null)
            btn.IsEnabled = false;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filter,
            SuggestedStartLocation = suggested_folder == null ? null : await StorageProvider.TryGetFolderFromPathAsync(suggested_folder)
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

    public static bool DetectIfUnicode(ReadOnlySpan<byte> data)
    {
        var length = Math.Min(data.Length, 1000);

        // detect if unicode
        int empty_pair = 0;
        int nonempty_pair = 0;
        for (int i = 0; i < length - 1; i++)
        {
            byte b1 = data[i];
            byte b2 = data[i + 1];
            if ((b1 > 0 && b2 == 0) || (b2 > 0 && b1 == 0))
            {
                empty_pair++;
            }
            else
            {
                nonempty_pair++;
            }
        }
        var unicode = empty_pair > (length / 2) && empty_pair > nonempty_pair;
        return unicode;
    }

    [GeneratedRegex(@"<\w+[^>]+>[^<]+</\w+\>")]
    public static partial Regex GetXMLTagRegex();
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

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert to XMB:\n" + ex.Message);
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

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert to XML:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertDDTtoTGA(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert DDT to TGA", [new("DDT Image") { Patterns = ["*.ddt"] }]);
        if (file == null) return;

        var ext = Path.GetExtension(file).ToLower();

        var out_file = PickOutFile(file, new_extension: ".tga", overwrite: true);
        try
        {
            var ddt_data = BarCompression.EnsureDecompressed(File.ReadAllBytes(file), out _);

            var ddt = new DDTImage(ddt_data);
            var image = await BarFormatConverter.ParseDDT(ddt);
            if (image == null) throw new InvalidDataException("Failed to convert DDT file");

            using (var stream = File.Create(out_file))
            {
                await image.SaveAsTgaAsync(stream, new TgaEncoder
                {
                    BitsPerPixel = TgaBitsPerPixel.Pixel32
                });
                image.Dispose();
            }

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert to TGA:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertToDDT(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var in_file = await PickFile(sender, "Create DDT from image", [new("Image file") {
            Patterns = ["*.tga", "*.png", "*.jpg", "*.jpeg", "*.webm", "*.bmp"] }]);

        if (in_file == null) return;

        var out_file = PickOutFile(in_file, new_extension: ".ddt", overwrite: true);
        try
        {
            var dialogue = new DDTCreateDialogue(in_file, out_file);
            await dialogue.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _ = ShowError(ex.Message);
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

            _ = ShowSuccess("Compression completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to compress using Alz4:\n" + ex.Message);
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

            _ = ShowSuccess("Compression completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to compress using L33t:\n" + ex.Message);
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

            _ = ShowSuccess("Decompression completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to decompress:\n" + ex.Message);
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

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert script:\n" + ex.Message);
        }
    }

    SearchWindow? _activeSearchWindow;
    void MenuItem_Search(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_activeSearchWindow != null)
        {
            _activeSearchWindow.Focus();
            return;
        }

        if (_barFile == null && _barStream == null && string.IsNullOrEmpty(_rootDirectory) &&
            !Directory.Exists(_rootDirectory))
        {
            _ = ShowError("No BAR archive or Root directory loaded");
            return;
        }

        _activeSearchWindow = new SearchWindow(this);
        _activeSearchWindow.Closed += (a, b) =>
        {
            _activeSearchWindow = null;
        };

        _activeSearchWindow.Show(this);
    }
    #endregion

    #region Configuration
    Configuration? _lastConfiguration;
    void TryRestorePreviousConfiguration()
    {
        var config_path = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE);
        if (!File.Exists(config_path))
            return;

        try
        {
            var config = JsonSerializer.Deserialize(File.ReadAllText(config_path), CryBarJsonContext.Default.Configuration);
            if (config == null)
                return;

            _lastConfiguration = config;

            if (Directory.Exists(config.RootDirectory))
                LoadDir(config.RootDirectory, false);

            if (Directory.Exists(config.ExportRootDirectory) && config.ExportRootDirectory != _rootDirectory)
                ExportRootDirectory = config.ExportRootDirectory;

            if (File.Exists(config.BarFile))
                LoadBAR(config.BarFile, false);

            _searchExclusionFilter = config.SearchExclusionFilter ?? "";
        }
        catch
        {

        }
    }

    public void SaveConfiguration()
    {
        var config_path = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE);

        try
        {
            _lastConfiguration = new Configuration
            {
                RootDirectory = _rootDirectory,
                ExportRootDirectory = _exportRootDirectory,
                BarFile = _barStream?.Name,
                LastVersionCheck = _latestVersion,
                SearchExclusionFilter = _searchExclusionFilter
            };

            File.WriteAllText(config_path, JsonSerializer.Serialize(_lastConfiguration, CryBarJsonContext.Default.Configuration));
        }
        catch
        {

        }
    }

    #endregion

    #region Prompt functions
    async Task ShowError(string text)
    {
        var prompt = new Prompt(PromptType.Error, "Error", text);
        await prompt.ShowDialog(this);
    }

    async Task ShowSuccess(string text)
    {
        var prompt = new Prompt(PromptType.Success, "Success", text);
        await prompt.ShowDialog(this);
    }

    async Task ShowProgress(string title, Progress<string?> progress)
    {
        var prompt = new Prompt(PromptType.Progress, title, progress_reporter: progress);
        await prompt.ShowDialog(this);
    }
    #endregion

    void ResetExportWatcher()
    {
        if (_exportWatcher != null)
        {
            _exportWatcher.Renamed -= ExportDir_Renamed;
            _exportWatcher.Created -= ExportDir_Created;
            _exportWatcher.Deleted -= ExportDir_Deleted;
            _exportWatcher.Dispose();
            _exportWatcher = null;
        }

        if (string.IsNullOrEmpty(_exportRootDirectory))
            return;

        if (!Directory.Exists(_exportRootDirectory))
            return;

        _exportWatcher = new FileSystemWatcher(ExportRootDirectory);
        _exportWatcher.IncludeSubdirectories = true;
        _exportWatcher.EnableRaisingEvents = true;
        _exportWatcher.Renamed += ExportDir_Renamed;
        _exportWatcher.Created += ExportDir_Created;
        _exportWatcher.Deleted += ExportDir_Deleted;
    }

    void ContextMenu_Opened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var listbox = (ListBox)((ContextMenu)sender!).Parent!.Parent!;
        ContextSelectedItemsCount = listbox.SelectedItems!.Count;
    }
}