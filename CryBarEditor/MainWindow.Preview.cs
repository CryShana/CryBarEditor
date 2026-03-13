using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using CryBar;
using CryBar.TMM;
using CryBarEditor.Classes;
using CryBarEditor.Controls;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    CancellationTokenSource? _previewCsc;

    #region Preview dispatchers
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
    #endregion

    #region Preview core
    public async Task Preview<T>(T entry, Func<T, string> get_rel_path,
        Func<T, long> get_read_size, Func<T, Memory<byte>> read, CancellationToken token = default)
    {
        const int MAX_DATA_SIZE = 1_500_000_000;    // 1.5 GB
        const int MAX_DATA_TEXT_SIZE = 100_000_000; // 100 MB

        HideTmmPreview();

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
                var xmlText = ConversionHelper.ConvertXmbToXmlText(data.Span);
                if (xmlText != null)
                {
                    PreviewedFileNote = "(Converted to XML)";
                    text = xmlText;
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
                var tmm = new TmmFile(data);
                if (!tmm.Parse())
                {
                    PreviewedFileNote = "(Failed to parse TMM)";
                    return;
                }

                PreviewedFileNote = $"TMM v{tmm.Version} - {tmm.NumBones} bones, {tmm.NumMaterials} mats";
                ShowTmmPreview(tmm.GetSummary(relative_path));

                var tmmFileName = Path.GetFileName(relative_path);
                _ = LoadTmm3DPreview(tmmFileName, data, token);
                return;
            }
            else if (ext == ".tma")
            {
                var tma = new TmaFile(data);
                if (!tma.Parse())
                {
                    PreviewedFileNote = "(Failed to parse TMA)";
                    return;
                }

                ext = ".txt";
                text = tma.GetSummary();
                PreviewedFileNote = $"TMA v{tma.Version} - {tma.NumBones} bones, {tma.NumTracks} tracks";
            }
            else if (relative_path.EndsWith(".tmm.data", StringComparison.OrdinalIgnoreCase))
            {
                // Try to find the companion .tmm file
                // TMM.DATA files are in ArtModelCacheModelData*.bar but TMMs are in ArtModelCacheMeta.bar
                var tmmBaseName = Path.GetFileName(relative_path[..^5]); // e.g. "petrobolos.tmm"
                TmmFile? companionTmm = null;

                // First: check current BAR
                if (_barFile?.Entries != null && _barStream != null)
                {
                    var tmmEntry = _barFile.Entries.FirstOrDefault(
                        e => e.Name.Equals(tmmBaseName, StringComparison.OrdinalIgnoreCase));
                    if (tmmEntry != null)
                    {
                        var tmmRawData = BarCompression.EnsureDecompressed(
                            tmmEntry.ReadDataRaw(_barStream), out _);
                        companionTmm = new TmmFile(tmmRawData);
                        if (!companionTmm.Parse()) companionTmm = null;
                    }
                }

                // Second: search all .bar files in the same directory
                if (companionTmm == null && _barStream != null)
                {
                    companionTmm = FindCompanionInSiblingBars<TmmFile>(
                        _barStream.Name, tmmBaseName,
                        (entry, stream) =>
                        {
                            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(stream), out _);
                            var tmm = new TmmFile(raw);
                            return tmm.Parse() ? tmm : null;
                        });
                }

                if (companionTmm != null)
                {
                    var dataFile = new TmmDataFile(data,
                        companionTmm.NumVertices, companionTmm.NumTriangleVerts,
                        companionTmm.NumBones > 0);

                    if (dataFile.Parse())
                    {
                        ext = ".txt";
                        text = dataFile.GetSummary();
                        PreviewedFileNote = "TMM Data";
                    }
                    else
                    {
                        PreviewedFileNote = "(Failed to parse TMM data)";
                        return;
                    }
                }
                else
                {
                    ext = ".txt";
                    text = $"TMM Data file ({data_size:N0} bytes)\nCompanion .tmm not found in BAR - cannot decode without vertex/index counts.";
                    PreviewedFileNote = "TMM Data (no companion)";
                }
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
    #endregion

    #region UI display functions
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
    #endregion

    #region TMM 3D Preview
    void ShowTmmPreview(string metadataText)
    {
        _flatPreview.IsVisible = false;
        _tmmTabControl.IsVisible = true;
        _tmmTabControl.SelectedIndex = _tmmSelectedTabIndex;
        _tmmTabControl.SelectionChanged -= TmmTabControl_SelectionChanged;
        _tmmTabControl.SelectionChanged += TmmTabControl_SelectionChanged;
        _tmmMetadataEditor.Document = new TextDocument(metadataText);
    }

    void TmmTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tmmTabControl.SelectedIndex == 1)
        {
            // Flush pending mesh on first switch to 3D tab
            if (_pendingMeshData != null)
                FlushPendingMesh();

            // Force GL control to re-render by detaching and reattaching
            if (_glPreview != null)
            {
                _3dViewContainer.Child = null;
                _3dViewContainer.Child = _glPreview;
            }
        }
    }

    void FlushPendingMesh()
    {
        if (_pendingMeshData == null) return;
        var mesh = _pendingMeshData;
        _pendingMeshData = null;
        LoadMeshIntoScene(mesh);
    }

    void HideTmmPreview()
    {
        if (_tmmTabControl.IsVisible)
            _tmmSelectedTabIndex = _tmmTabControl.SelectedIndex;
        _tmmTabControl.IsVisible = false;
        _flatPreview.IsVisible = true;
        _meshConversionCts?.Cancel();
    }

    void Update3DStatus(string text)
    {
        Dispatcher.UIThread.Post(() => _3dStatusText.Text = text);
    }

    async Task LoadTmm3DPreview(string tmmFileName, Memory<byte> tmmData, CancellationToken token)
    {
        var oldCts = _meshConversionCts;
        _meshConversionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = _meshConversionCts.Token;
        oldCts?.Cancel();
        oldCts?.Dispose();

        Update3DStatus("Loading...");

        if (!_meshCache.TryGet(tmmFileName, out var meshData))
        {
            var companionData = ResolveCompanionData(tmmFileName + ".data");
            if (companionData == null) { Update3DStatus("No .tmm.data found"); return; }
            if (ct.IsCancellationRequested) return;

            meshData = await Task.Run(() =>
                MeshDataBuilder.BuildFromTmm(tmmData, companionData.Value), ct);

            if (meshData == null) { Update3DStatus("Conversion failed"); return; }
            if (ct.IsCancellationRequested) return;

            _meshCache.Add(tmmFileName, meshData);
        }

        if (ct.IsCancellationRequested) return;

        // Store pending mesh; only initialize GL and upload when 3D tab is visible
        _pendingMeshData = meshData;
        if (_tmmTabControl.SelectedIndex == 1)
            FlushPendingMesh();
        else
            Update3DStatus(""); // ready, will load when tab is selected
    }

    PreviewMeshData? _pendingMeshData;

    void EnsureGlPreviewInitialized()
    {
        if (_glPreview != null) return;
        _glPreview = new GlPreviewControl();
        _3dViewContainer.Child = _glPreview;
    }

    void LoadMeshIntoScene(PreviewMeshData meshData)
    {
        try
        {
            EnsureGlPreviewInitialized();
            _glPreview!.LoadMesh(meshData);
            Update3DStatus("");
        }
        catch (Exception ex)
        {
            Update3DStatus($"Error: {ex.Message}");
        }
    }

    void FitCameraToScene() => _glPreview?.ResetCamera();

    void ResetCamera_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FitCameraToScene();
    }
    #endregion

    #region Refresh and Filter
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
}
