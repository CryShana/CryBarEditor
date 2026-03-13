using Avalonia.Controls;

using CryBar;
using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CryBarEditor;

public partial class AdvancedExportWindow : SimpleWindow
{
    bool _doCopy = true;
    bool _doConvert;
    bool _doDecompress;
    bool _doExportMaterials;
    bool _doTmmToGltf;

    readonly ExportOptions _result;
    readonly string _exportRootDirectory;
    readonly IReadOnlyList<ExportFileInfo> _files;
    int _compressedNonConvertibleCount;
    int _compressedConvertibleCount;
    bool _hasTmmFiles;

    public bool DoCopy { get => _doCopy; set { _doCopy = value; OnSelfChanged(); OnPropertyChanged(nameof(CanExport)); } }
    public bool DoConvert { get => _doConvert; set { _doConvert = value; OnSelfChanged(); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(ShowDecompressOption)); OnPropertyChanged(nameof(CompressedFileNote)); OnPropertyChanged(nameof(ShowExportMaterialsOption)); OnPropertyChanged(nameof(ShowTmmToGltfOption)); } }
    public bool DoDecompress { get => _doDecompress; set { _doDecompress = value; OnSelfChanged(); } }
    public bool DoExportMaterials { get => _doExportMaterials; set { _doExportMaterials = value; OnSelfChanged(); } }
    public bool DoTmmToGltf { get => _doTmmToGltf; set { _doTmmToGltf = value; OnSelfChanged(); } }

    public bool CanExport => DoCopy || DoConvert;

    public string FileSummary { get; }
    public string Recommendation { get; }
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);

    int UnhandledCompressedCount => DoConvert
        ? _compressedNonConvertibleCount
        : _compressedNonConvertibleCount + _compressedConvertibleCount;

    public bool ShowExportMaterialsOption => DoConvert && _hasTmmFiles;
    public bool ShowTmmToGltfOption => DoConvert && _hasTmmFiles;
    public bool ShowDecompressOption => UnhandledCompressedCount > 0;
    public string CompressedFileNote => UnhandledCompressedCount > 0
        ? $"{UnhandledCompressedCount} of {_files.Count} file(s) are compressed and won't be auto-decompressed by conversion. " +
          "Enable this to ensure correct output."
        : "";

    public bool IsDirectExport { get; }
    public string? DirectExportPath { get; }

    public bool HasOverwriteWarning { get; }
    public string OverwriteWarning { get; }

    public AdvancedExportWindow()
    {
        _result = new ExportOptions();
        _exportRootDirectory = "";
        _files = [];
        FileSummary = "";
        Recommendation = "";
        OverwriteWarning = "";

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

        // Build file summary by extension
        var extGroups = files
            .GroupBy(f => Path.GetExtension(f.RelativePath).ToLower())
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        FileSummary = $"{files.Count} file(s) selected: {string.Join(", ", extGroups)}";

        // Detect compressed and convertible files
        int convertibleCount = files.Count(f => ConversionHelper.IsConvertibleExtension(
            Path.GetExtension(f.RelativePath)));

        _compressedConvertibleCount = files.Count(f => f.IsCompressed &&
            ConversionHelper.IsConvertibleExtension(Path.GetExtension(f.RelativePath)));
        _compressedNonConvertibleCount = files.Count(f => f.IsCompressed &&
            !ConversionHelper.IsConvertibleExtension(Path.GetExtension(f.RelativePath)));
        _hasTmmFiles = files.Any(f => Path.GetExtension(f.RelativePath)
            .Equals(".tmm", StringComparison.OrdinalIgnoreCase));

        int compressedCount = _compressedConvertibleCount + _compressedNonConvertibleCount;
        if (compressedCount > 0)
            _doDecompress = true; // default to on

        _result.AnyCompressed = compressedCount > 0;
        _result.AnyConvertible = convertibleCount > 0;

        // Build recommendation
        if (convertibleCount > 0 && convertibleCount == files.Count)
            Recommendation = "All selected files are convertible - 'Convert' is recommended.";
        else if (convertibleCount > 0)
            Recommendation = $"{convertibleCount} file(s) can be converted. Use 'Copy + Convert' to get both versions.";
        else
            Recommendation = "No convertible files in selection - 'Copy' is the best option.";

        // Set sensible defaults (by default only one is selected)
        DoConvert = convertibleCount > 0;
        DoCopy = !DoConvert;

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

        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(Recommendation));
        OnPropertyChanged(nameof(HasRecommendation));
        OnPropertyChanged(nameof(ShowDecompressOption));
        OnPropertyChanged(nameof(CompressedFileNote));
        OnPropertyChanged(nameof(IsDirectExport));
        OnPropertyChanged(nameof(DirectExportPath));
        OnPropertyChanged(nameof(HasOverwriteWarning));
        OnPropertyChanged(nameof(OverwriteWarning));
        OnPropertyChanged(nameof(CanExport));

        // Restore saved export settings
        if (savedConfig?.ExportDoCopy is bool c) DoCopy = c;
        if (savedConfig?.ExportDoConvert is bool cv) DoConvert = cv;
        if (savedConfig?.ExportDoDecompress is bool d) DoDecompress = d;
        if (savedConfig?.ExportDoExportMaterials is bool m) DoExportMaterials = m;
        if (savedConfig?.ExportTmmToGltf is bool g) DoTmmToGltf = g;
    }

    /// <summary>
    /// Returns the configured export options, or null if cancelled.
    /// </summary>
    public ExportOptions? GetResult() => _result.Confirmed ? _result : null;

    void ExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result.Copy = DoCopy;
        _result.Convert = DoConvert;
        _result.Decompress = DoDecompress;
        _result.ExportMaterials = DoExportMaterials;
        _result.TmmToGltf = DoTmmToGltf;
        _result.Confirmed = true;
        Close();
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result.Confirmed = false;
        Close();
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
