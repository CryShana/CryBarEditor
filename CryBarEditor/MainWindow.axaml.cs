using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using CryBar;
using CryBarEditor.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CryBarEditor.Controls;
using TextMateSharp.Grammars;
using Configuration = CryBarEditor.Classes.Configuration;

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
    int _contextSelectedItemsCount = 0;

    // SEARCH SETTINGS that are saved
    public string _searchExclusionFilter = "";
    public bool _searchCaseSensitive = true;
    public bool _searchRegex = false;

    // EDITOR SETTINGS
    string _editorCommand = "";

    FMODBank? _fmodBank = null;
    BarFile? _barFile = null;
    FileStream? _barStream = null;
    FileIndex? _fileIndex = null;
    BarFileEntry? _selectedBarEntry = null;
    FMODEvent? _selectedBankEntry = null;
    RootFileEntry? _selectedRootFileEntry = null;
    List<RootFileEntry>? _loadedRootFiles = null;

    double _imageZoomLevel = 1.0;
    readonly FileWatcherHelper _rootWatcher = new();
    readonly FileWatcherHelper _exportWatcher = new();

    // 3D preview
    GlPreviewControl? _glPreview;
    readonly PreviewMeshCache _meshCache = new(maxItems: 10);
    int _tmmSelectedTabIndex = 0;
    CancellationTokenSource? _meshConversionCts;

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
        => AdditiveModding.IsSupportedFor(SelectedBarEntry?.RelativePath ?? SelectedRootFileEntry?.RelativePath, out _) && CanExport;

    public bool CanOpenInEditor
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_editorCommand) || !CanExport)
                return false;

            var relPath = SelectedBarEntry != null ? GetBARFullRelativePath(SelectedBarEntry)
                : SelectedRootFileEntry != null ? GetRootFullRelativePath(SelectedRootFileEntry) : null;
            return relPath != null && IsFileOverriden(relPath);
        }
    }
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
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(SelectedIsDDT));
        OnPropertyChanged(nameof(CanExportAndIsDDT));
        OnPropertyChanged(nameof(SelectedCanHaveAdditiveMod));
        OnPropertyChanged(nameof(CanOpenInEditor));
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

        // file watcher events
        _rootWatcher.Created += RootDir_Created;
        _rootWatcher.Deleted += RootDir_Deleted;
        _rootWatcher.Renamed += RootDir_Renamed;
        _exportWatcher.Created += ExportDir_Created;
        _exportWatcher.Deleted += ExportDir_Deleted;
        _exportWatcher.Renamed += ExportDir_Renamed;

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

    #region Helpers
    public bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".tga" or ".gif" or ".webp" or ".avif" or ".jpx" or ".bmp";

    /// <summary>
    /// Resolves the actual file path for an exported file, handling XMB extension fallback.
    /// Returns null if the file does not exist.
    /// </summary>
    public string? ResolveExportedFilePath(string relative_path_full)
    {
        if (string.IsNullOrEmpty(relative_path_full) || !CanExport)
            return null;

        var exported_path = Path.Combine(_exportRootDirectory, relative_path_full);
        if (File.Exists(exported_path))
            return exported_path;

        // also check files that work without the XMB extension
        if (exported_path.EndsWith(".xmb", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = exported_path[..^4];
            if (File.Exists(trimmed))
                return trimmed;
        }

        return null;
    }

    public bool IsFileOverriden(string relative_path_full)
        => ResolveExportedFilePath(relative_path_full) != null;

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
            _searchCaseSensitive = config.SearchCaseSensitive ?? true;
            _searchRegex = config.SearchUseRegex ?? false;
            _editorCommand = config.EditorCommand ?? "";
        }
        catch
        {

        }
    }

    void SaveExportConfiguration(ExportOptions options)
    {
        _lastConfiguration ??= new Configuration();
        _lastConfiguration.ExportDoCopy = options.Copy;
        _lastConfiguration.ExportDoConvert = options.Convert;
        _lastConfiguration.ExportDoDecompress = options.Decompress;
        _lastConfiguration.ExportDoExportMaterials = options.ExportMaterials;
        _lastConfiguration.ExportTmmToGltf = options.TmmToGltf;
        _lastConfiguration.ExportOpenInEditor = options.OpenInEditor;
        SaveConfiguration();
    }

    public void SaveConfiguration()
    {
        var config_path = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE);

        try
        {
            _lastConfiguration ??= new Configuration();
            _lastConfiguration.RootDirectory = _rootDirectory;
            _lastConfiguration.ExportRootDirectory = _exportRootDirectory;
            _lastConfiguration.BarFile = _barStream?.Name;
            _lastConfiguration.LastVersionCheck = _latestVersion;
            _lastConfiguration.SearchExclusionFilter = _searchExclusionFilter;
            _lastConfiguration.SearchCaseSensitive = _searchCaseSensitive;
            _lastConfiguration.SearchUseRegex = _searchRegex;
            _lastConfiguration.EditorCommand = _editorCommand;

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

    #region Settings & Editor
    async void MenuItem_Settings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new SettingsWindow(_editorCommand);
        await window.ShowDialog(this);

        if (window.Confirmed)
        {
            _editorCommand = window.EditorCommand;
            SaveConfiguration();
            OnPropertyChanged(nameof(CanOpenInEditor));
        }
    }

    const string EDITOR_FILE_PLACEHOLDER = "{file}";

    void LaunchEditorForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(_editorCommand))
            return;

        var cmd = _editorCommand;
        if (cmd.Contains(EDITOR_FILE_PLACEHOLDER))
            cmd = cmd.Replace(EDITOR_FILE_PLACEHOLDER, filePath);
        else
            cmd = $"{cmd} \"{filePath}\"";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.Arguments = $"/c {cmd}";
        }
        else
        {
            psi.Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"";
        }

        System.Diagnostics.Process.Start(psi);
    }

    bool TryLaunchEditorForFile(string filePath)
    {
        try
        {
            LaunchEditorForFile(filePath);
            return true;
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to open editor: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region Directory path clicks
    void RootDirectoryPath_Click(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (Directory.Exists(_rootDirectory))
            OpenDirectoryInExplorer(_rootDirectory);
    }

    void ExportDirectoryPath_Click(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (Directory.Exists(_exportRootDirectory))
            OpenDirectoryInExplorer(_exportRootDirectory);
    }

    static void OpenDirectoryInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
    }
    #endregion

    void ResetExportWatcher()
    {
        _exportWatcher.Watch(_exportRootDirectory);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _meshConversionCts?.Cancel();
        _meshConversionCts?.Dispose();
        _glPreview = null;
        _meshCache.Clear();
        base.OnClosing(e);
    }
}
