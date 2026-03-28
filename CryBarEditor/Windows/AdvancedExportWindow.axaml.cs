using Avalonia.Controls;

using CryBar.Bar;
using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CryBarEditor.Windows;

public partial class AdvancedExportWindow : SimpleWindow
{
    bool _doCopy = true;
    bool _doConvert;
    bool _doDecompress;
    bool _doExportMaterials;
    bool _doTmmToGltf;
    bool _doExportAnimations;
    bool _openInEditor;
    string _overrideName = "";
    string _defaultOverrideName = "";
    bool _overrideNameModified;

    readonly ExportOptions _result;
    readonly string _exportRootDirectory;
    readonly IReadOnlyList<ExportFileInfo> _files;
    int _compressedNonConvertibleCount;
    int _compressedConvertibleCount;
    bool _hasTmmFiles;
    bool _hasEditorCommand;

    // Single-file info for rename preview
    string _singleFileSourceExt = "";
    bool _singleFileIsConvertible;

    public bool DoCopy { get => _doCopy; set { _doCopy = value; OnSelfChanged(); OnPropertyChanged(nameof(CanExport)); RefreshOverridePreview(); } }
    public bool DoConvert { get => _doConvert; set { _doConvert = value; OnSelfChanged(); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(ShowDecompressOption)); OnPropertyChanged(nameof(CompressedFileNote)); OnPropertyChanged(nameof(ShowExportMaterialsOption)); OnPropertyChanged(nameof(ShowTmmToGltfOption)); RefreshOverridePreview(); } }
    public bool DoDecompress { get => _doDecompress; set { _doDecompress = value; OnSelfChanged(); } }
    public bool DoExportMaterials { get => _doExportMaterials; set { _doExportMaterials = value; OnSelfChanged(); } }
    public bool DoTmmToGltf { get => _doTmmToGltf; set { _doTmmToGltf = value; OnSelfChanged(); OnPropertyChanged(nameof(ShowExportAnimationsOption)); RefreshOverridePreview(); } }
    public bool DoExportAnimations { get => _doExportAnimations; set { _doExportAnimations = value; OnSelfChanged(); } }
    public bool OpenInEditor { get => _openInEditor; set { _openInEditor = value; OnSelfChanged(); } }
    public string OverrideName
    {
        get => _overrideName;
        set
        {
            if (_overrideName == value) return;
            _overrideName = value;
            _overrideNameModified = _overrideName != _defaultOverrideName;
            OnSelfChanged();
            OnPropertyChanged(nameof(OverrideNameOpacity));
            RefreshOverridePreview();
        }
    }

    public bool CanExport => DoCopy || DoConvert;
    public bool HasEditorCommand => _hasEditorCommand;
    public bool ShowOverrideName => _files.Count == 1;
    public double OverrideNameOpacity => _overrideNameModified ? 1.0 : 0.5;
    public string OverridePreview { get; private set; } = "";

    public string FileSummary { get; }
    public string Recommendation { get; }
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);

    int UnhandledCompressedCount => DoConvert
        ? _compressedNonConvertibleCount
        : _compressedNonConvertibleCount + _compressedConvertibleCount;

    public bool ShowExportMaterialsOption => DoConvert && _hasTmmFiles;
    public bool ShowTmmToGltfOption => DoConvert && _hasTmmFiles;
    public bool ShowExportAnimationsOption => DoConvert && _hasTmmFiles && DoTmmToGltf;
    public bool ShowDecompressOption => UnhandledCompressedCount > 0;
    public string CompressedFileNote => UnhandledCompressedCount > 0
        ? $"{UnhandledCompressedCount} of {_files.Count} file(s) are compressed and won't be auto-decompressed by conversion. " +
          "Enable this to ensure correct output."
        : "";

    public bool IsDirectExport { get; }
    public string? DirectExportPath { get; }

    public string TruncatedDirectExportPath => TruncatePath(DirectExportPath, 50);

    public bool HasOverwriteWarning { get; }
    public string OverwriteWarning { get; }

    public bool HasFilenameConflict { get; }
    public string FilenameConflictWarning { get; }

    public AdvancedExportWindow()
    {
        _result = new ExportOptions();
        _exportRootDirectory = "";
        _files = [];
        FileSummary = "";
        Recommendation = "";
        OverwriteWarning = "";
        FilenameConflictWarning = "";

        DataContext = this;
        InitializeComponent();
    }

    public AdvancedExportWindow(
        IReadOnlyList<ExportFileInfo> files,
        string exportRootDirectory,
        bool isDirectExport,
        string? directExportPath,
        Configuration? savedConfig = null) : this()
    {
        _files = files;
        _exportRootDirectory = exportRootDirectory;
        IsDirectExport = isDirectExport;
        DirectExportPath = directExportPath;
        _result = new ExportOptions
        {
            DirectExport = isDirectExport,
            DirectExportPath = directExportPath
        };

        // Editor command
        _hasEditorCommand = !string.IsNullOrWhiteSpace(savedConfig?.EditorCommand);
        if (savedConfig?.ExportOpenInEditor is bool oie) OpenInEditor = oie && _hasEditorCommand;

        // Override name for single file (base name without source extension)
        if (files.Count == 1)
        {
            var fileName = Path.GetFileName(files[0].RelativePath);
            _singleFileSourceExt = Path.GetExtension(fileName).ToLower();
            _singleFileIsConvertible = ConversionHelper.IsConvertibleExtension(_singleFileSourceExt);

            // For XMB: base name includes inner extension (proto.xml from proto.xml.XMB)
            // For others: base name is filename without extension (texture from texture.ddt)
            _defaultOverrideName = _singleFileSourceExt == ".xmb"
                ? Path.GetFileNameWithoutExtension(fileName) // e.g. proto.xml
                : Path.GetFileNameWithoutExtension(fileName); // e.g. texture
            _overrideName = _defaultOverrideName;
            _overrideNameModified = false;
        }

        // Build file summary by extension
        var extGroups = files
            .GroupBy(f => Path.GetExtension(f.RelativePath).ToLower())
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        FileSummary = $"{files.Count} file(s) selected: {string.Join(", ", extGroups)}";

        // Detect compressed and convertible files
        int convertibleCount = files.Count(f => ConversionHelper.IsConvertibleExtension(Path.GetExtension(f.RelativePath)));

        _compressedConvertibleCount = files.Count(f => f.IsCompressed && ConversionHelper.IsConvertibleExtension(Path.GetExtension(f.RelativePath)));
        _compressedNonConvertibleCount = files.Count(f => f.IsCompressed && !ConversionHelper.IsConvertibleExtension(Path.GetExtension(f.RelativePath)));
        _hasTmmFiles = files.Any(f => Path.GetExtension(f.RelativePath).Equals(".tmm", StringComparison.OrdinalIgnoreCase));

        int compressedCount = _compressedConvertibleCount + _compressedNonConvertibleCount;
        if (compressedCount > 0)
            _doDecompress = true; // default to on

        _result.AnyCompressed = compressedCount > 0;
        _result.AnyConvertible = convertibleCount > 0;

        // Build recommendation
        if (convertibleCount > 0 && convertibleCount == files.Count)
        {
            Recommendation = "All files are convertible - 'Convert' is recommended.";
            DoConvert = true;
            DoCopy = false;
        }
        else if (convertibleCount > 0)
        {
            Recommendation = $"{convertibleCount} file(s) can be converted - 'Convert' is recommended";
            DoConvert = true;
            DoCopy = false;
        }
        else
        {
            Recommendation = "No convertible files - 'Copy' is recommended.";
            DoConvert = false;
            DoCopy = true;
        }

        // Check for overwrites
        if (!isDirectExport && Directory.Exists(exportRootDirectory))
        {
            int overwriteCount = 0;
            foreach (var file in files)
            {
                var targetPath = Path.Combine(exportRootDirectory, file.FullRelativePath);
                if (File.Exists(targetPath))
                    overwriteCount++;
            }

            HasOverwriteWarning = overwriteCount > 0;
            OverwriteWarning = overwriteCount > 0
                ? $"{overwriteCount} file(s) already exist in export directory and will be overwritten."
                : "";
        }
        else
        {
            HasOverwriteWarning = false;
            OverwriteWarning = "";
        }

        // Check for filename conflicts in direct export (flattened, so same name = overwrite)
        if (isDirectExport)
        {
            var conflictCount = files
                .GroupBy(f => Path.GetFileName(f.RelativePath), StringComparer.OrdinalIgnoreCase)
                .Count(g => g.Count() > 1);

            HasFilenameConflict = conflictCount > 0;
            FilenameConflictWarning = conflictCount > 0
                ? $"{conflictCount} filename(s) appear more than once - only the last file will be kept."
                : "";
        }

        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(Recommendation));
        OnPropertyChanged(nameof(HasRecommendation));
        OnPropertyChanged(nameof(ShowDecompressOption));
        OnPropertyChanged(nameof(CompressedFileNote));
        OnPropertyChanged(nameof(IsDirectExport));
        OnPropertyChanged(nameof(DirectExportPath));
        OnPropertyChanged(nameof(TruncatedDirectExportPath));
        OnPropertyChanged(nameof(HasOverwriteWarning));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(HasFilenameConflict));
        OnPropertyChanged(nameof(FilenameConflictWarning));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(HasEditorCommand));
        OnPropertyChanged(nameof(ShowOverrideName));
        OnPropertyChanged(nameof(OverrideNameOpacity));

        // Restore saved export settings
        //if (savedConfig?.ExportDoCopy is bool c) DoCopy = c;
        //if (savedConfig?.ExportDoConvert is bool cv) DoConvert = cv;
        if (savedConfig?.ExportDoDecompress is bool d) DoDecompress = d;
        if (savedConfig?.ExportDoExportMaterials is bool m) DoExportMaterials = m;
        if (savedConfig?.ExportTmmToGltf is bool g) DoTmmToGltf = g;
        if (savedConfig?.ExportAnimations is bool ea) DoExportAnimations = ea;
    }

    /// <summary>
    /// Returns the configured export options, or null if cancelled.
    /// </summary>
    public ExportOptions? GetResult() => _result.Confirmed ? _result : null;

    void RefreshOverridePreview()
    {
        if (!ShowOverrideName)
        {
            OverridePreview = "";
            OnPropertyChanged(nameof(OverridePreview));
            return;
        }

        var name = string.IsNullOrWhiteSpace(OverrideName) ? _defaultOverrideName : OverrideName.Trim();
        var parts = new List<string>();

        if (DoCopy)
        {
            // Copy keeps original extension
            parts.Add(name + _singleFileSourceExt);
        }

        if (DoConvert)
        {
            if (_singleFileIsConvertible)
            {
                if (_singleFileSourceExt == ".xmb")
                    parts.Add(name); // XMB stripped, base name IS the final name
                else
                {
                    var convertedExt = ConversionHelper.GetConvertedExtension(_singleFileSourceExt, DoTmmToGltf);
                    parts.Add(name + (convertedExt ?? _singleFileSourceExt));
                }
            }
            else if (!DoCopy)
            {
                // Not convertible, but Convert is the only mode - still show output (same as copy)
                parts.Add(name + _singleFileSourceExt);
            }
        }

        OverridePreview = parts.Count > 0 ? "Output: " + string.Join(", ", parts) : "";
        OnPropertyChanged(nameof(OverridePreview));
    }

    void ExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result.Copy = DoCopy;
        _result.Convert = DoConvert;
        _result.Decompress = DoDecompress;
        _result.ExportMaterials = DoExportMaterials;
        _result.TmmToGltf = DoTmmToGltf;
        _result.ExportAnimations = DoExportAnimations;
        _result.OpenInEditor = OpenInEditor;
        _result.OverrideBaseName = ShowOverrideName && !string.IsNullOrWhiteSpace(OverrideName)
            && _overrideNameModified ? OverrideName.Trim() : null;
        _result.Confirmed = true;
        Close();
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result.Confirmed = false;
        Close();
    }

    static string TruncatePath(string? path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path ?? "";

        const string ellipsis = "...";
        int side = (maxLength - ellipsis.Length) / 2;
        return string.Concat(path.AsSpan(0, side), ellipsis, path.AsSpan(path.Length - side));
    }
}

/// <summary>
/// Lightweight descriptor for a file to be exported.
/// Works for both Root directory files and BAR archive entries.
/// </summary>
public class ExportFileInfo
{
    /// <summary>Relative path within source (BAR entry path or root-relative path)</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>Full relative path including game root prefix (used for export directory structure)</summary>
    public string FullRelativePath { get; init; } = "";

    /// <summary>Whether the file has the Compressed flag in the BAR archive</summary>
    public bool IsCompressed { get; init; }
}
