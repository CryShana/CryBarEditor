using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using CryBar;
using CryBar.Classes;
using CryBar.TMM;
using CryBarEditor.Classes;
using CryBarEditor.Controls;
using SixLabors.ImageSharp;
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

        HideTmmPreview();

        PreviewedFileName = $"FMOD event: \"${e.Path}\"";
        PreviewedFileNote = "";
        PreviewedFileData = $"Length: {e.LengthMs}ms";

        await SetImagePreview(null);

        var soundInfo = await BuildSoundsetPreviewTextAsync(e);

        _ = SetEditorText(".txt",
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
        {soundInfo}
        """);
    }
    #endregion

    #region Preview core
    public async Task Preview<T>(T entry,
        Func<T, string> get_rel_path,
        Func<T, long> get_read_size,
        Func<T, CancellationToken, ValueTask<PooledBuffer>> read,
        CancellationToken token = default)
    {
        const int MAX_DATA_SIZE = 1_500_000_000;    // 1.5 GB
        const int MAX_DATA_TEXT_SIZE = 100_000_000; // 100 MB
        const int LOADING_INDICATOR_THRESHOLD = 500_000; // 500 KB — skip "Loading..." for small files to avoid flicker

        var relative_path = get_rel_path(entry);
        var ext = Path.GetExtension(relative_path).ToLower();
        var text = "";

        PreviewedFileName = Path.GetFileName(relative_path);
        PreviewedFileNote = "";

        // Cancel any in-progress background document build from a previous preview
        _docLoadCts?.Cancel();
        _docLoadCts?.Dispose();
        _docLoadCts = null;

        // Uninstall folding before replacing document — it holds references to the old document
        if (_foldingManager != null)
        {
            _foldingManager.Clear();
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        var data_size = get_read_size(entry);

        // Only show loading indicator for larger files — small files load fast
        // enough that the "Loading..." flash causes more flicker than it helps
        if (data_size > LOADING_INDICATOR_THRESHOLD)
        {
            HideTmmPreview();
            _txtEditor.Document = new TextDocument("Loading...");
            _textMateInstallation.SetGrammar(null);
        }

        // Mark document as not ready — SearchWindow awaits this before highlighting
        var previewTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _docReadyTask = previewTcs.Task;

        try
        {
            try
            {
                if (data_size > MAX_DATA_SIZE)
                {
                    HideTmmPreview();
                    await SetImagePreview(null);
                    _ = SetEditorText(".txt", "Data too big to be loaded for preview");
                    return;
                }

                using var rawData = await read(entry, token);
                var (decompressedData, type) = await Task.Run(() =>
                {
                    var d = BarCompression.EnsureDecompressedPooled(rawData, out var t);
                    return (d, t);
                });
                using var data = decompressedData;

                PreviewedFileNote = type switch
                {
                    CompressionType.L33t => "(Decompressed L33t)",
                    CompressionType.Alz4 => "(Decompressed Alz4)",
                    _ => ""
                };

                if (IsImage(ext))
                {
                    HideTmmPreview();
                    using (var image = SixLabors.ImageSharp.Image.Load(data.Span))
                    {
                        await SetImagePreview(image, token);
                        PreviewedFileNote = $"[{image.Width}x{image.Height}]";
                    }

                    return;
                }

                if (ext == ".xmb")
                {
                    if (_docCache.TryGet(relative_path, out _))
                    {
                        ext = ".xml";
                        PreviewedFileNote = "(Converted to XML)";
                    }
                    else
                    {
                        var mem = data.Memory;
                        var xmlText = await Task.Run(() => ConversionHelper.ConvertXmbToXmlText(mem.Span));
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
                }
                else if (ext == ".ddt")
                {
                    HideTmmPreview();
                    var ddt = new DDTImage(data.Memory);
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
                    var tmm = new TmmFile(data.Memory);
                    if (!tmm.Parsed)
                    {
                        PreviewedFileNote = "(Failed to parse TMM)";
                        return;
                    }

                    PreviewedFileNote = $"TMM v{tmm.Version} - {tmm.NumBones} bones, {tmm.NumMaterials} mats";
                    ShowTmmPreview(tmm.GetSummary(relative_path));

                    var tmmFileName = Path.GetFileName(relative_path);
                    // Copy: LoadTmm3DPreview is fire-and-forget but data's PooledBuffer is disposed on return
                    _ = LoadTmm3DPreview(tmmFileName, data.Memory.ToArray(), token);
                    return;
                }
                else if (ext == ".tma")
                {
                    var tma = new TmaFile(data.Memory);
                    if (!tma.Parsed)
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
                            using var tmmData = await tmmEntry.ReadDataRawPooledAsync(_barStream);
                            using var tmmRawData = BarCompression.EnsureDecompressedPooled(tmmData, out _);
                            companionTmm = new TmmFile(tmmRawData.Memory);
                            if (!companionTmm.Parsed) companionTmm = null;
                        }
                    }

                    // Second: search all .bar files in the same directory
                    if (companionTmm == null && _barStream != null)
                    {
                        companionTmm = await FindCompanionInSiblingBars<TmmFile>(
                            _barStream.Name, tmmBaseName,
                            async (entry, stream) =>
                            {
                                using var rawData = await entry.ReadDataRawPooledAsync(stream);
                                using var data = BarCompression.EnsureDecompressedPooled(rawData, out _);
                                var tmm = new TmmFile(data.Memory);
                                return tmm.Parsed ? tmm : null;
                            });
                    }

                    if (companionTmm != null)
                    {
                        var dataFile = new TmmDataFile(data.Memory,
                            companionTmm.NumVertices, companionTmm.NumTriangleVerts,
                            companionTmm.NumBones > 0);

                        if (dataFile.Parsed)
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
                        _ = SetEditorText(".txt", "Data too big to preview as text");
                        return;
                    }

                    var unicode = DetectIfUnicode(data.Span);
                    PreviewedFileNote = unicode ? "[Unicode]" : "[UTF-8]";

                    // set text
                    text = unicode ?
                        Encoding.Unicode.GetString(data.Span) :
                        Encoding.UTF8.GetString(data.Span);

                    if (ext is not ".xml" && GetXMLTagRegex().IsMatch(text))
                        ext = ".xml";

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

            if (token.IsCancellationRequested) return;

            HideTmmPreview();
            await SetImagePreview(null);
            await SetEditorText(ext, text, cacheKey: relative_path);
        }
        finally
        {
            previewTcs.TrySetResult();
        }
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

    public async Task SetEditorText(string extension, string text, string? cacheKey = null)
    {
        // Cancel any in-progress async document build from the previous call
        _docLoadCts?.Cancel();
        _docLoadCts?.Dispose();
        _docLoadCts = null;

        // ANY CLEANUP
        if (_foldingManager != null)
        {
            _foldingManager.Clear();
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
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

        // Cache hit: assign immediately (document already built, no async needed)
        if (cacheKey != null && _docCache.TryGet(cacheKey, out var cachedDoc))
        {
            if (text.Length == 0)
                _previewText = cachedDoc!.Text;
            _docReadyTask = Task.CompletedTask;
            _txtEditor.Document = cachedDoc!;
            _textMateInstallation.SetGrammar(scope);
            InstallFolding(ext, cachedDoc!.TextLength);
            ScrollEditorToTop();
            return;
        }

        const int LARGE_TEXT_THRESHOLD = 500_000;
        if (text.Length > LARGE_TEXT_THRESHOLD)
        {
            // Show a placeholder while the full document builds in the background
            _txtEditor.Document = new TextDocument("Loading...");
            _textMateInstallation.SetGrammar(null);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _docReadyTask = tcs.Task;

            var cts = new CancellationTokenSource();
            _docLoadCts = cts;

            // Capture UI thread so the background thread can transfer document ownership
            var uiThread = Thread.CurrentThread;

            try
            {
                var fullDoc = await Task.Run(() =>
                {
                    var doc = new TextDocument(text);
                    doc.SetOwnerThread(uiThread);
                    return doc;
                }, cts.Token);

                if (cts.Token.IsCancellationRequested || _previewText != text) return;

                _txtEditor.Document = fullDoc;
                _textMateInstallation.SetGrammar(scope);
                if (cacheKey != null) _docCache.Add(cacheKey, fullDoc);

                InstallFolding(ext, text.Length);
                ScrollEditorToTop();
            }
            finally
            {
                tcs.TrySetResult();
            }
            return;
        }

        // Small document — build synchronously, no snippet needed
        _docReadyTask = Task.CompletedTask;
        var doc = new TextDocument(text);
        _txtEditor.Document = doc;
        if (cacheKey != null) _docCache.Add(cacheKey, doc);
        _textMateInstallation.SetGrammar(scope);
        InstallFolding(ext, text.Length);
        ScrollEditorToTop();
    }

    // The document shifts slightly after assignment, so we delay before scrolling.
    // Version counter prevents stale scroll-to-top from overriding SearchWindow's scroll-to-match.
    internal int _scrollVersion;
    void ScrollEditorToTop()
    {
        var version = ++_scrollVersion;
        Task.Delay(50).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_scrollVersion == version)
                _txtEditor.ScrollTo(0, 0);
        }));
    }

    void InstallFolding(string ext, int textLength)
    {
        const int FOLDING_MAX_CHARS = 2_000_000; // skip XML folding on huge files — it's also slow
        if (ext is not ".xml" || textLength > FOLDING_MAX_CHARS) return;

        _foldingManager = FoldingManager.Install(_txtEditor.TextArea);
        var strategy = new XmlFoldingStrategy();
        strategy.UpdateFoldings(_foldingManager, _txtEditor.Document);
    }
    #endregion

    #region TMM 3D Preview
    void ShowTmmPreview(string metadataText)
    {
        if (!_tmmTabControl.IsVisible)
            _tmmTabControl.SelectedIndex = _tmmSelectedTabIndex;
        _flatPreview.IsVisible = false;
        _tmmTabControl.IsVisible = true;
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
            using var companionData = await ResolveCompanionDataAsync(tmmFileName + ".data");
            if (companionData == null) { Update3DStatus("No .tmm.data found"); return; }
            if (ct.IsCancellationRequested) return;

            meshData = await Task.Run(() =>
                MeshDataBuilder.BuildFromTmm(tmmData, companionData.Memory), ct);

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
