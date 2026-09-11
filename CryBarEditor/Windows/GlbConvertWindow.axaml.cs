using Avalonia.Controls;
using Avalonia.Interactivity;
using CryBar.Export;
using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CryBarEditor.Windows;

public partial class GlbConvertWindow : SimpleWindow
{
    readonly string _glbPath;
    readonly string _glbBaseName;
    readonly string _outputDir;
    GlbModel? _model;
    readonly Dictionary<string, GlbConverter.DdtMaterialParams> _ddtParams = new();

    public string SourcePathDisplay { get; }
    public string OutputDirDisplay { get; }
    public string SummaryDisplay { get; private set; } = "(parsing...)";
    public bool CanConvert { get; private set; }

    bool _busy;
    string _statusLine = "";

    public bool NotBusy => !_busy;
    public bool CanConvertAndNotBusy => CanConvert && !_busy;
    public bool IsConverting => _busy;
    public string StatusLine
    {
        get => _statusLine;
        private set { _statusLine = value; OnPropertyChanged(nameof(StatusLine)); }
    }
    public ObservableCollection<string> Warnings { get; } = new();
    public bool HasWarnings => Warnings.Count > 0;

    public ObservableCollection<PlannedRow> PlannedRows { get; } = new();

    public GlbConvertWindow()
    {
        DataContext = this;
        InitializeComponent();
        _glbPath = "";
        _glbBaseName = "";
        _outputDir = "";
        SourcePathDisplay = "";
        OutputDirDisplay = "";
    }

    public GlbConvertWindow(string glbPath) : this()
    {
        _glbPath = glbPath;
        _glbBaseName = Path.GetFileNameWithoutExtension(glbPath);
        _outputDir = Path.Combine(Path.GetDirectoryName(glbPath) ?? "", _glbBaseName);

        SourcePathDisplay = glbPath;
        OutputDirDisplay = _outputDir;
        OnPropertyChanged(nameof(SourcePathDisplay));
        OnPropertyChanged(nameof(OutputDirDisplay));

        Opened += async (_, _) => await ParseAsync();
    }

    async Task ParseAsync()
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(_glbPath);
            // Parse + inspect are synchronous and CPU-bound; offload so the window stays responsive.
            var (model, summary, inspection) = await Task.Run(() =>
            {
                var m = GlbReader.Parse(bytes.AsMemory());
                return (m, BuildSummary(m), GlbConverter.Inspect(m, _glbBaseName));
            });
            _model = model;
            SummaryDisplay = summary;
            PopulatePlannedRowsFrom(inspection);
            CanConvert = true;
        }
        catch (Exception ex)
        {
            SummaryDisplay = $"Parse failed: {ex.Message}";
            CanConvert = false;
        }
        OnPropertyChanged(nameof(SummaryDisplay));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(CanConvertAndNotBusy));
    }

    void PopulatePlannedRowsFrom(GlbConverter.InspectionResult inspection)
    {
        PlannedRows.Clear();
        foreach (var p in inspection.PlannedFiles)
        {
            PlannedRows.Add(new PlannedRow
            {
                Name = p.Name,
                NeedsClick = p.NeedsDdtParams,
                DdtMaterialName = p.DdtMaterialName,
                StatusText = p.NeedsDdtParams ? "[!] missing params (click)" : "",
                Enabled = !p.NeedsDdtParams,
            });
        }
        AutoLinkFbximports();
    }

    /// <summary>
    /// Conservative match: same directory as the GLB, and exactly "<anim_name>.fbximport"
    /// (case-insensitive, no fuzzy matching). Misses are silent - users can manually link.
    /// </summary>
    void AutoLinkFbximports()
    {
        var dir = Path.GetDirectoryName(_glbPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        foreach (var row in PlannedRows)
        {
            if (!row.IsTmaRow) continue;
            var animName = Path.GetFileNameWithoutExtension(row.Name);
            var candidate = Path.Combine(dir, animName + ".fbximport");
            if (File.Exists(candidate))
            {
                row.LinkedFbximportPath = candidate;
                row.FbximportStatus = "auto: " + Path.GetFileName(candidate);
            }
        }
    }

    async void LinkFbximportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PlannedRow row) return;

        var picker = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"Link fbximport for {row.Name}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("fbximport") { Patterns = ["*.fbximport"] },
            ],
        });
        if (picker.Count == 0) return;

        var file = picker[0];
        var localPath = file.Path.LocalPath;
        if (string.IsNullOrEmpty(localPath)) return;

        row.LinkedFbximportPath = localPath;
        row.FbximportStatus = "linked: " + Path.GetFileName(localPath);
    }

    void ClearFbximportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PlannedRow row) return;
        row.LinkedFbximportPath = null;
        row.FbximportStatus = "";
    }

    async void MissingParamsClick(object? sender, RoutedEventArgs e)
    {
        if (_model == null) return;
        if (sender is not Button btn || btn.Tag is not PlannedRow row) return;
        if (row.DdtMaterialName == null) return;

        var png = FindPngBytes(_model, row.DdtMaterialName);
        if (png == null) return;

        var dialog = new DDTCreateDialogue(row.DdtMaterialName, png);
        await dialog.ShowDialog(this);
        if (dialog.PickedResult == null) return;

        _ddtParams[row.DdtMaterialName] = dialog.PickedResult.Params;
        MarkRowResolved(row);

        if (dialog.PickedResult.ApplyToAll)
        {
            foreach (var other in PlannedRows)
            {
                if (!other.NeedsClick) continue;
                if (other.DdtMaterialName == null) continue;
                if (_ddtParams.ContainsKey(other.DdtMaterialName)) continue;
                _ddtParams[other.DdtMaterialName] = dialog.PickedResult.Params;
                MarkRowResolved(other);
            }
        }
    }

    static void MarkRowResolved(PlannedRow row)
    {
        row.StatusText = "ready";
        row.Enabled = true;
    }

    static byte[]? FindPngBytes(GlbModel model, string ddtName)
    {
        foreach (var mat in model.Materials)
        {
            if (mat.Name == ddtName && mat.BaseColorPng is { Length: > 0 })
                return mat.BaseColorPng;
            if (ddtName == $"{mat.Name}_normal" && mat.NormalMapPng is { Length: > 0 })
                return mat.NormalMapPng;
        }
        return null;
    }

    static string BuildSummary(GlbModel model)
    {
        int vertCount = 0, triCount = 0;
        foreach (var p in model.Mesh.Primitives)
        {
            vertCount += p.Positions.Length / 3;
            triCount  += p.Indices.Length / 3;
        }
        var lines = new List<string>
        {
            $"Mesh:        {vertCount} vertices, {triCount} triangles, {model.Mesh.Primitives.Length} mesh groups",
            $"Skeleton:    {model.Bones?.Length ?? 0} bones",
            $"Attachments: {model.Attachments?.Length ?? 0}",
            $"Animations:  {model.Animations?.Length ?? 0}",
            $"Materials:   {model.Materials.Length}",
            $"CryBar metadata: {(model.Extras != null ? "yes" : "no (defaults will be used)")}",
        };
        return string.Join("\n", lines);
    }

    async void ConvertClick(object? sender, RoutedEventArgs e)
    {
        if (_model == null) return;

        _busy = true;
        StatusLine = "Starting...";
        Warnings.Clear();
        OnPropertyChanged(nameof(NotBusy));
        OnPropertyChanged(nameof(CanConvertAndNotBusy));
        OnPropertyChanged(nameof(IsConverting));
        OnPropertyChanged(nameof(HasWarnings));

        // Progress<T>'s callback fires on the captured SynchronizationContext (the
        // UI thread here, since we construct it before going async). Safe to write
        // bound properties directly.
        var progress = new Progress<string>(msg => StatusLine = msg);

        try
        {
            // Read linked fbximport files into a name -> bytes map.
            // Map keys are the same animation names that appear as `<name>.tma` rows.
            Dictionary<string, byte[]>? fbxByAnim = null;
            foreach (var row in PlannedRows)
            {
                if (!row.IsTmaRow || string.IsNullOrEmpty(row.LinkedFbximportPath)) continue;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(row.LinkedFbximportPath);
                    fbxByAnim ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    fbxByAnim[Path.GetFileNameWithoutExtension(row.Name)] = bytes;
                }
                catch (Exception ex)
                {
                    Warnings.Add($"Failed to read {row.LinkedFbximportPath}: {ex.Message}");
                }
            }

            var result = await GlbConverter.ConvertAsync(_model, _glbBaseName, _ddtParams, progress,
                fbximportByAnimName: fbxByAnim);

            var enabledNames = new HashSet<string>(
                PlannedRows.Where(r => r.Enabled).Select(r => r.Name),
                StringComparer.Ordinal);
            var written = await GlbConverter.WriteFilesAsync(
                result.Files.Where(f => enabledNames.Contains(f.Name)).ToList(),
                _outputDir,
                progress);

            foreach (var w in result.Warnings) Warnings.Add(w);
            StatusLine = $"Wrote {written} files to {_outputDir}";
        }
        catch (Exception ex)
        {
            StatusLine = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(NotBusy));
            OnPropertyChanged(nameof(CanConvertAndNotBusy));
            OnPropertyChanged(nameof(IsConverting));
            OnPropertyChanged(nameof(HasWarnings));
        }
    }

    void CancelClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed class PlannedRow : INotifyPropertyChanged
{
    bool _enabled = true;
    string _statusText = "";
    string? _linkedFbximportPath;
    string _fbximportStatus = "";

    public required string Name { get; init; }
    public required bool NeedsClick { get; init; }
    public string? DdtMaterialName { get; init; }

    public bool IsTmaRow => Name.EndsWith(".tma", StringComparison.Ordinal);

    public string ExtensionColor => Name switch
    {
        _ when Name.EndsWith(".tmm.data", StringComparison.Ordinal) => "#90A4AE",
        _ when Name.EndsWith(".tmm",      StringComparison.Ordinal) => "#64B5F6",
        _ when Name.EndsWith(".tma",      StringComparison.Ordinal) => "#81C784",
        _ when Name.EndsWith(".ddt",      StringComparison.Ordinal) => "#FFB74D",
        _                                                            => "#d9d9d9",
    };

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public string? LinkedFbximportPath
    {
        get => _linkedFbximportPath;
        set
        {
            if (_linkedFbximportPath == value) return;
            _linkedFbximportPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLinkedFbximport));
        }
    }

    public bool HasLinkedFbximport => !string.IsNullOrEmpty(_linkedFbximportPath);

    public string FbximportStatus
    {
        get => _fbximportStatus;
        set { if (_fbximportStatus == value) return; _fbximportStatus = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
