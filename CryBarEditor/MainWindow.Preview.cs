using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using CryBar;
using CryBar.Bar;
using CryBar.Export;
using CryBar.Scenario;
using CryBar.Sound;
using CryBar.TMM;
using CryBar.Utilities;
using CryBarEditor.Classes;
using CryBarEditor.Controls;
using CryBarEditor.Windows;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    CancellationTokenSource? _previewCsc;

    // Capacity 2 keeps GPU memory bounded across rapid TMM switches while still hiding
    // decode latency for the most recently viewed model and one neighbor. The eviction
    // callback queues the actual GL handle deletion onto the render thread.
    LruCache<PreviewTextureSet>? _textureCache;
    LruCache<PreviewTextureSet> EnsureTextureCache() => _textureCache ??= new LruCache<PreviewTextureSet>(2,
        set => _glPreview?.QueueGlAction(gl => set.DisposeGl(h => gl.DeleteTexture(h))));

    readonly Dictionary<string, bool> _textureAvailability = new();
    CancellationTokenSource? _textureLoadCts;
    string? _currentTmmFileName;
    bool _useTextured3D;
    double _screenshotScaleFactor = 1;
    bool _screenshotTransparent = true;
    string _screenshotFormat = "webp";

    CancellationToken RestartTextureLoadCts()
    {
        var old = _textureLoadCts;
        _textureLoadCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        return _textureLoadCts.Token;
    }

    // The resolver is stateless apart from a single-entry parse cache it owns.
    // One instance per editor session avoids re-allocating per probe + per load.
    TmmMaterialResolver? _materialResolver;
    TmmMaterialResolver? GetMaterialResolver() =>
        _fileIndex == null ? null
        : _materialResolver ??= new TmmMaterialResolver(_fileIndex, ReadFromIndexEntryPooledAsync);

    #region Preview dispatchers
    public async Task Preview(RootFileEntry? entry)
    {
        if (entry == null || !Directory.Exists(_rootDirectory))
            return;

        _currentlyPreviewedItem = entry;
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
        _previewCsc?.Dispose();
        _previewCsc = new();
        PreviewedFileData = $"File Size: {new FileInfo(path).Length}";
        await Preview(entry, F_GetFullRelativePathRoot, F_ReadSizeRoot, F_ReadRoot, _previewCsc.Token);
    }

    public async Task Preview(BarFileEntry? entry)
    {
        if (entry == null || _barStream == null)
            return;

        _currentlyPreviewedItem = entry;
        _previewCsc?.Cancel();
        _previewCsc?.Dispose();
        _previewCsc = new();

        PreviewedFileData = $"BAR Offset: {entry.ContentOffset},   BAR Size: {entry.SizeInArchive},   Actual Size: {entry.SizeUncompressed},   Compressed: {(entry.IsCompressed ? "true" : "false")}";
        await Preview(entry, F_GetFullRelativePathBAR, F_ReadSizeBAR, F_ReadBAR, _previewCsc.Token);
    }

    // Re-click on an already-selected item doesn't fire the SelectedItem setter, so
    // when the user previewed something in the other panel since, a re-click would
    // be a no-op. Re-fire Preview when the clicked entry isn't what's rendered.
    void RootListBox_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ExtractTappedEntry<RootFileEntry>(e) is { } entry)
            _ = Preview(entry);
    }

    void BarListBox_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ExtractTappedEntry<BarFileEntry>(e) is { } entry)
            _ = Preview(entry);
    }

    T? ExtractTappedEntry<T>(Avalonia.Input.TappedEventArgs e) where T : class
    {
        var entry = (e.Source as Avalonia.Controls.Control)
            ?.FindAncestorOfType<Avalonia.Controls.ListBoxItem>(includeSelf: true)
            ?.DataContext as T;
        return entry != null && !ReferenceEquals(entry, _currentlyPreviewedItem) ? entry : null;
    }

    public async Task Preview(IBankItem? item)
    {
        if (item == null || _fmodBank == null)
            return;

        if (item is FMODSubsound s)
        {
            PreviewSubsound(s);
            return;
        }

        if (item is FMODEvent ev)
            await PreviewEvent(ev);
    }

    void PreviewSubsound(FMODSubsound s)
    {
        HideTmmPreview();

        PreviewedFileName = $"FMOD sound: \"{s.Name}\"";
        PreviewedFileNote = "";
        PreviewedFileData = $"Length: {s.LengthMs}ms";

        // Resolved file path (from soundmanifest / soundsets). When a name maps to several paths,
        // ResolvedPath is the one picked by matching the audio's duration.
        var pathLine = string.IsNullOrEmpty(s.FullRelativePath)
            ? "File path: (no path found)"
            : $"File path: {s.FullRelativePath}";

        _ = SetImagePreview(null);
        _ = SetEditorText(".txt",
        $"""
        Name:    {s.Name}
        Length:  {s.LengthMs}ms
        Type:    FSB5 subsound

        {pathLine}
        """);
    }

    async Task PreviewEvent(FMODEvent e)
    {
        HideTmmPreview();

        PreviewedFileName = $"FMOD event: \"${e.Path}\"";
        PreviewedFileNote = "";
        PreviewedFileData = $"Length: {e.LengthMs}ms";

        await SetImagePreview(null);

        // Resolve the event's soundset once, then reuse it for both the text and the subsound count.
        SoundsetResolution? resolution = null;
        try { resolution = _fileIndex != null ? await ResolveFmodEventSoundsAsync(e) : null; }
        catch { /* best-effort preview */ }

        var soundInfo = BuildSoundsetPreviewText(e, resolution);

        // How many of this event's soundset variants are backed by actual subsounds in this bank
        // (these export deterministically; the rest fall back to rendering).
        var matchedSubs = resolution != null ? MatchEventSubsounds(resolution) : [];
        var subsoundNote = matchedSubs.Count > 0
            ? $"\nResolved subsounds: {matchedSubs.Count} variant(s) found in this bank (exported directly)"
            : "";

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
        {soundInfo}{subsoundNote}
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
        const int LOADING_INDICATOR_THRESHOLD = 500_000; // 500 KB - skip "Loading..." for small files to avoid flicker

        var relative_path = get_rel_path(entry);
        var ext = Path.GetExtension(relative_path).ToLower();
        var text = "";

        PreviewedFileName = Path.GetFileName(relative_path);
        PreviewedFileNote = "";
        ShowExperimentalWarning = false;

        // Cancel any in-progress background document build from a previous preview
        _docLoadCts?.Cancel();
        _docLoadCts?.Dispose();
        _docLoadCts = null;

        // Uninstall folding before replacing document - it holds references to the old document
        if (_foldingManager != null)
        {
            _foldingManager.Clear();
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        var data_size = get_read_size(entry);

        // Only show loading indicator for larger files - small files load fast
        // enough that the "Loading..." flash causes more flicker than it helps
        if (data_size > LOADING_INDICATOR_THRESHOLD)
        {
            HideTmmPreview();
            _txtEditor.Document = new TextDocument("Loading...");
            _textMateInstallation.SetGrammar(null);
        }

        // Mark document as not ready - SearchWindow awaits this before highlighting
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
                else if (ext == ".dds")
                {
                    HideTmmPreview();

                    var summary = ConversionHelper.GetDdsSummary(data.Memory);
                    if (summary == null)
                    {
                        PreviewedFileNote = "(Failed to parse DDS)";
                        await SetImagePreview(null);
                        return;
                    }

                    using var image = await ConversionHelper.DecodeDdsToImage(data.Memory, token);
                    if (image == null)
                    {
                        PreviewedFileNote = "(Unsupported DDS variant)";
                        await SetImagePreview(null);
                        return;
                    }

                    PreviewedFileNote = $"[{summary.Value.FormatName} {summary.Value.Width}x{summary.Value.Height}, {summary.Value.Mips} Mips]";
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
                    var tmmRelativeDir = Path.GetDirectoryName(relative_path);
                    // Copy: LoadTmm3DPreview is fire-and-forget but data's PooledBuffer is disposed on return
                    _ = LoadTmm3DPreview(tmmFileName, data.Memory.ToArray(), tmmRelativeDir, token);
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
                    var dataRelativeDir = Path.GetDirectoryName(relative_path);
                    TmmFile? companionTmm = null;

                    using (var tmmRawData = await ResolveCompanionDataAsync(tmmBaseName, dataRelativeDir))
                    {
                        if (tmmRawData != null)
                        {
                            var tmm = new TmmFile(tmmRawData.Memory);
                            if (tmm.Parsed) companionTmm = tmm;
                        }
                    }

                    if (companionTmm != null)
                    {
                        var dataFile = new TmmDataFile(data.Memory, companionTmm);

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
                else if (ext == ".mythscn")
                {
                    HideTmmPreview();
                    await SetImagePreview(null);

                    var mem = data.Memory;
                    var scenario = await Task.Run(() => new ScenarioFile(mem));
                    if (scenario.Parsed)
                    {
                        ShowScenarioPreview(scenario);
                        PreviewedFileNote = "(AoM Scenario, 3D + XML)";
                        ShowExperimentalWarning = true;
                        return;
                    }

                    HideScenarioPreview();
                    text = "Failed to parse scenario file";
                    ext = ".txt";
                    PreviewedFileNote = "";
                }
                else if (ext == ".trg")
                {
                    HideTmmPreview();
                    await SetImagePreview(null);

                    var mem = data.Memory;
                    var (trgText, trgExt, trgNote) = await Task.Run(() =>
                    {
                        var trg = new TriggerFile(mem);
                        if (trg.Parsed)
                            return (ScenarioFile.StripBinaryForPreview(trg.ToXml()), ".xml", "(AoM Trigger Export, converted to XML)");
                        return ("Failed to parse trigger file", ".txt", "");
                    });
                    PreviewedFileNote = trgNote;
                    ShowExperimentalWarning = trgExt == ".xml";
                    text = trgText;
                    ext = trgExt;
                }
                else if (ext == ".zip")
                {
                    HideTmmPreview();
                    await SetImagePreview(null);

                    try
                    {
                        var mem = data.Memory;
                        text = await Task.Run(() =>
                        {
                            using var zipStream = new MemoryStream(mem.Span.ToArray());
                            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                            return BuildZipHierarchyText(archive);
                        });
                        ext = ".txt";
                    }
                    catch (InvalidDataException)
                    {
                        text = "Preview failed: not a valid ZIP archive";
                        ext = ".txt";
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
                    var isBinary = !unicode && DetectIfBinary(data.Span);
                    PreviewedFileNote = isBinary ? "[Binary]" : (unicode ? "[Unicode]" : "[UTF-8]");

                    // set text
                    text = unicode ?
                        Encoding.Unicode.GetString(data.Span) :
                        Encoding.UTF8.GetString(data.Span);

                    // skip XML sniffing for binary content - PE/binary files often contain embedded
                    // XML manifests that would otherwise trigger XML grammar and spike RAM usage
                    if (!isBinary && ext is not ".xml" && GetXMLTagRegex().IsMatch(text))
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
            var oldEmpty = _previewImage;
            _previewImage = null;
            oldEmpty?.Dispose();
            return;
        }

        Bitmap? newBitmap = null;
        try
        {
            using var stream = new MemoryStream();
            await image.SaveAsPngAsync(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
            }, token);

            if (token.IsCancellationRequested) return;

            stream.Seek(0, SeekOrigin.Begin);
            newBitmap = new Bitmap(stream);
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            newBitmap.Dispose();
            return;
        }

        // Swap on the UI thread with no await between Source assignment and Dispose,
        // so the compositor never sees Source pointing to a disposed Bitmap.
        var oldBitmap = _previewImage;
        _previewImage = newBitmap;
        _imgPreview.Source = newBitmap;
        _txtEditor.IsVisible = false;
        _imgPreview.IsVisible = true;
        _imageZoomLevel = 1.0;
        RefreshImageScale();
        oldBitmap?.Dispose();
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
            InstallFolding(ext);
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

                InstallFolding(ext);
                ScrollEditorToTop();
            }
            finally
            {
                tcs.TrySetResult();
            }
            return;
        }

        // Small document - build synchronously, no snippet needed
        _docReadyTask = Task.CompletedTask;
        var doc = new TextDocument(text);
        _txtEditor.Document = doc;
        if (cacheKey != null) _docCache.Add(cacheKey, doc);
        _textMateInstallation.SetGrammar(scope);
        InstallFolding(ext);
        ScrollEditorToTop();
    }

    // The document shifts slightly after assignment, so we delay before scrolling.
    // Version counter prevents stale scroll-to-top from overriding SearchWindow's scroll-to-match.
    internal int _scrollVersion;
    void ScrollEditorToTop()
    {
        var version = ++_scrollVersion;
        Task.Delay(50).ContinueWith(_ => Dispatcher.Post(() =>
        {
            if (_scrollVersion == version)
                _txtEditor.ScrollTo(0, 0);
        }));
    }

    void InstallFolding(string ext)
    {
        if (ext is not ".xml") return;

        _foldingManager = FoldingManager.Install(_txtEditor.TextArea);
        var strategy = new XmlFoldingStrategy();
        strategy.UpdateFoldings(_foldingManager, _txtEditor.Document);
    }
    #endregion

    #region TMM 3D Preview
    void ShowTmmPreview(string metadataText)
    {
        // Structured preview panels are mutually exclusive.
        if (_scenarioRoot is not null && _scenarioRoot.IsVisible)
            HideScenarioPreview();

        if (!_tmmRoot.IsVisible)
            _tmmTabStrip.SelectedIndex = _tmmSelectedTabIndex;
        _flatPreview.IsVisible = false;
        _tmmRoot.IsVisible = true;
        _tmmMetadataEditor.Document = new TextDocument(metadataText);
    }

    void TmmTab_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ToggleTabPanels(_tmmTabStrip, _tmmMetadataEditor, _tmm3dPanel);
        if (_tmmTabStrip?.SelectedIndex != 1) return;
        if (_pendingMeshData != null) FlushPendingMesh();
        TryKickTextureLoadForCurrent();
    }

    void TryKickTextureLoadForCurrent()
    {
        if (!_useTextured3D || _currentTmmFileName == null) return;
        if (!_textureAvailability.TryGetValue(_currentTmmFileName, out var has) || !has) return;
        _ = EnsureTexturesLoadedAsync(_currentTmmFileName, RestartTextureLoadCts());
    }

    // Avalonia raises SelectionChanged during XAML EndInit before named fields
    // are populated, hence the null guards.
    static void ToggleTabPanels(TabStrip? strip, Control? first, Control? second)
    {
        if (strip is null || first is null || second is null) return;
        bool firstSelected = strip.SelectedIndex == 0;
        first.IsVisible = firstSelected;
        second.IsVisible = !firstSelected;
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
        if (_tmmRoot.IsVisible)
            _tmmSelectedTabIndex = _tmmTabStrip.SelectedIndex;
        _tmmRoot.IsVisible = false;
        _flatPreview.IsVisible = true;
        _meshConversionCts?.Cancel();

        if (_scenarioRoot is not null && _scenarioRoot.IsVisible)
            HideScenarioPreview();
    }

    void Update3DStatus(string text)
    {
        Dispatcher.Post(() => _3dStatusText.Text = text);
    }

    async Task LoadTmm3DPreview(string tmmFileName, Memory<byte> tmmData,
        string? preferredRelativeDir, CancellationToken token)
    {
        _currentTmmFileName = tmmFileName;

        var oldCts = _meshConversionCts;
        _meshConversionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = _meshConversionCts.Token;
        oldCts?.Cancel();
        oldCts?.Dispose();

        Update3DStatus("Loading...");

        if (!_meshCache.TryGet(tmmFileName, out var meshData))
        {
            using var companionData = await ResolveCompanionDataAsync(tmmFileName + ".data", preferredRelativeDir);
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
        if (_tmmTabStrip.SelectedIndex == 1)
            FlushPendingMesh();
        else
            Update3DStatus(""); // ready, will load when tab is selected

        // Texture work has its own CTS lifetime, independent of the mesh-conversion CTS.
        _ = ProbeTextureAvailabilityAsync(tmmFileName, RestartTextureLoadCts());
    }

    async Task ProbeTextureAvailabilityAsync(string tmmFileName, CancellationToken token)
    {
        var resolver = GetMaterialResolver();
        if (resolver == null) { UpdateTexturedToggleVisibility(false); return; }

        if (!_textureAvailability.TryGetValue(tmmFileName, out var has))
        {
            has = await resolver.HasAtLeastBaseColorAsync(tmmFileName, token);
            if (token.IsCancellationRequested) return;
            _textureAvailability[tmmFileName] = has;
        }

        UpdateTexturedToggleVisibility(has);

        // Auto-load only when 3D tab is active; otherwise the mesh isn't in place
        // and TmmTab_SelectionChanged will kick the load on tab activation.
        if (has && _useTextured3D && _tmmTabStrip?.SelectedIndex == 1)
            await EnsureTexturesLoadedAsync(tmmFileName, token);
    }

    void UpdateTexturedToggleVisibility(bool available)
    {
        _texturedToggle.IsVisible = available;
        if (!available)
        {
            // Don't clear _useTextured3D - preserve preference for the next textured-capable TMM.
            if (_glPreview != null) _glPreview.UseTexturedMode = false;
        }
        else
        {
            _texturedToggle.IsChecked = _useTextured3D;
            if (_glPreview != null) _glPreview.UseTexturedMode = _useTextured3D;
        }
    }

    async Task EnsureTexturesLoadedAsync(string tmmFileName, CancellationToken token)
    {
        var resolver = GetMaterialResolver();
        if (resolver == null) return;

        var cache = EnsureTextureCache();
        if (cache.TryGet(tmmFileName, out var existing) && existing != null)
        {
            _glPreview?.SetActiveTextures(existing);
            return;
        }

        var resolved = await resolver.ResolveAsync(tmmFileName, token);
        if (resolved == null || token.IsCancellationRequested) return;

        var meshData = _glPreview?.GetMeshData();
        if (meshData == null) return;

        // .material XML lists submaterials in arbitrary order; mesh groups address materials by
        // TMM material-index order. Align the parsed list to TMM order via name lookup so
        // matBaseImages[i] is the texture for the TMM material at index i (what DrawGroupMaterialIndices points into).
        var matByName = new Dictionary<string, CryBar.Export.MaterialInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in resolved.Value.Materials) matByName[m.Name] = m;

        var tmmMatNames = meshData.MaterialNames;
        var matCount = tmmMatNames.Length;
        var matBaseImages = new Image<Rgba32>?[matCount];
        var matNormalImages = new Image<Rgba32>?[matCount];
        // Skip same-role duplicates (e.g. BaseColor + Diffuse) - parallel writes would race and leak the loser.
        var baseQueued = new bool[matCount];
        var normalQueued = new bool[matCount];

        // Decode all DDTs in parallel - each DDTImage is independent and the BCn decode is CPU-bound.
        var decodeTasks = new List<Task>();
        for (int i = 0; i < matCount; i++)
        {
            int matIndex = i;
            if (!matByName.TryGetValue(tmmMatNames[i], out var mat)) continue;
            foreach (var (texName, texPath) in mat.Textures)
            {
                if (!resolved.Value.Textures.TryGetValue(texPath, out var info)) continue;
                bool isBase = MaterialExporter.IsBaseColorRole(texName);
                bool isNormal = !isBase && MaterialExporter.IsNormalRole(texName);
                if (!isBase && !isNormal) continue;
                if (isBase)
                {
                    if (baseQueued[matIndex]) continue;
                    baseQueued[matIndex] = true;
                }
                else
                {
                    if (normalQueued[matIndex]) continue;
                    normalQueued[matIndex] = true;
                }

                var ddtBytes = info.DdtData;
                decodeTasks.Add(Task.Run(async () =>
                {
                    var ddt = new DDTImage(ddtBytes);
                    if (!ddt.ParseHeader()) return;
                    var img = await ddt.DecodeMipmapToImage(0, token);
                    if (img == null) return;
                    if (isBase) matBaseImages[matIndex] = img;
                    else matNormalImages[matIndex] = img;
                }, token));
            }
        }
        try { await Task.WhenAll(decodeTasks); }
        catch (OperationCanceledException) { /* fall through to dispose */ }

        if (token.IsCancellationRequested)
        {
            for (int i = 0; i < matCount; i++)
            {
                matBaseImages[i]?.Dispose();
                matNormalImages[i]?.Dispose();
            }
            return;
        }

        // Schedule GPU upload on the next render frame.
        _glPreview!.QueueGlAction((gl) =>
        {
            // Drop the upload if the user has switched TMMs (or cancelled) since we queued.
            if (token.IsCancellationRequested || _currentTmmFileName != tmmFileName)
            {
                for (int i = 0; i < matCount; i++)
                {
                    matBaseImages[i]?.Dispose();
                    matNormalImages[i]?.Dispose();
                }
                return;
            }

            var owned = new List<int>();
            var perMaterialByIndex = new (int? Base, int? Normal)[matCount];

            for (int i = 0; i < matCount; i++)
            {
                int? bh = null, nh = null;
                if (matBaseImages[i] != null)
                {
                    bh = UploadImageRgba(gl, matBaseImages[i]!);
                    if (bh.HasValue) owned.Add(bh.Value);
                }
                if (matNormalImages[i] != null)
                {
                    nh = UploadImageRgba(gl, matNormalImages[i]!);
                    if (nh.HasValue) owned.Add(nh.Value);
                }
                perMaterialByIndex[i] = (bh, nh);
                matBaseImages[i]?.Dispose();
                matNormalImages[i]?.Dispose();
            }

            var bindings = new Dictionary<int, (int? BaseColor, int? Normal)>();
            for (int g = 0; g < meshData.DrawGroups.Length; g++)
            {
                uint matIdx = meshData.DrawGroupMaterialIndices.Length > g
                    ? meshData.DrawGroupMaterialIndices[g] : 0u;
                if (matIdx >= matCount) continue;
                var pair = perMaterialByIndex[(int)matIdx];
                if (pair.Base.HasValue || pair.Normal.HasValue)
                    bindings[g] = (pair.Base, pair.Normal);
            }

            var set = new PreviewTextureSet { OwnedHandles = owned, MeshGroupBindings = bindings };
            cache.Add(tmmFileName, set);
            _glPreview.SetActiveTextures(set);
        });
    }

    int? UploadImageRgba(Avalonia.OpenGL.GlInterface gl, Image<Rgba32> img)
    {
        var group = img.GetPixelMemoryGroup();
        if (group.Count == 0) return null;

        if (group.Count == 1)
        {
            var byteSpan = MemoryMarshal.AsBytes(group[0].Span);
            return _glPreview!.UploadTexture(gl, img.Width, img.Height, byteSpan);
        }

        // ImageSharp splits buffers above ~22 MB into multiple chunks (large building textures hit this).
        // Allocate the texture once and stream chunks via glTexSubImage2D - no host-side contiguous copy
        // (which for 8K RGBA is a 256 MB LOH allocation that ArrayPool can't actually pool).
        int tex = _glPreview!.CreateEmptyTexture(gl, img.Width, img.Height);
        int yOffset = 0;
        foreach (var chunk in group)
        {
            int chunkRows = chunk.Length / img.Width; // ImageSharp splits on row boundaries
            if (chunkRows == 0) continue;
            var byteSpan = MemoryMarshal.AsBytes(chunk.Span);
            _glPreview.UploadTextureRows(gl, yOffset, img.Width, chunkRows, byteSpan);
            yOffset += chunkRows;
        }
        _glPreview.FinalizeTexture(gl);
        return tex;
    }

    void TexturedToggle_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _useTextured3D = _texturedToggle.IsChecked == true;
        if (_glPreview != null) _glPreview.UseTexturedMode = _useTextured3D;
        TryKickTextureLoadForCurrent();
        SaveConfiguration();
    }

    PreviewMeshData? _pendingMeshData;

    void EnsureGlPreviewInitialized()
    {
        if (_glPreview != null) return;
        _glPreview = new GlPreviewControl();
        _glPreview.GizmoLabelsProjected += OnGizmoLabelsProjected;
        _glPreview.MarkersProjected += OnMarkersProjected;
        // Texture cache holds GL handles tied to the dying context; drop it so the
        // next render rebuilds fresh against the new one. Also cancel any in-flight
        // load so its queued upload doesn't write into the wrong context.
        _glPreview.GlContextLost += () =>
        {
            _textureCache = null;
            _textureLoadCts?.Cancel();
        };
        _glPreview.ShowMarkers = _showMarkersCheckbox.IsChecked == true;
        _glPreview.ShowGroundGrid = _showGroundGridCheckbox.IsChecked == true;
        // Sync textured pref restored from config; otherwise the new control's
        // default (false) silently overrides the user's saved preference.
        _glPreview.UseTexturedMode = _useTextured3D;
        _3dViewContainer.Child = _glPreview;
    }

    List<TextBlock>? _markerLabelPool;

    void EnsureMarkerLabelPool() => _markerLabelPool ??= new List<TextBlock>();

    void ShowMarkers_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool enabled = _showMarkersCheckbox.IsChecked == true;
        if (_glPreview != null) _glPreview.ShowMarkers = enabled;

        SaveConfiguration();

        if (!enabled && _markerLabelPool != null)
        {
            foreach (var tb in _markerLabelPool) tb.IsVisible = false;
        }
    }

    void ShowGroundGrid_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool enabled = _showGroundGridCheckbox.IsChecked == true;
        if (_glPreview != null) _glPreview.ShowGroundGrid = enabled;
        SaveConfiguration();
    }

    void OnMarkersProjected(IReadOnlyList<Controls.GlPreviewControl.MarkerLabel> labels)
    {
        EnsureMarkerLabelPool();
        var pool = _markerLabelPool!;
        int needed = labels.Count * 2; // outline + foreground per label

        while (pool.Count < needed)
        {
            var outline = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.Black,
                IsHitTestVisible = false
            };
            var fg = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.White,
                IsHitTestVisible = false
            };
            _3dLabelCanvas.Children.Add(outline);
            _3dLabelCanvas.Children.Add(fg);
            pool.Add(outline);
            pool.Add(fg);
        }

        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var outline = pool[i * 2];
            var fg = pool[i * 2 + 1];
            if (label.Visible)
            {
                outline.Text = label.Name;
                fg.Text = label.Name;
                Canvas.SetLeft(outline, label.X + 1);
                Canvas.SetTop(outline, label.Y + 1);
                Canvas.SetLeft(fg, label.X);
                Canvas.SetTop(fg, label.Y);
                outline.Opacity = label.Occluded ? 0.5 : 1.0;
                fg.Opacity = label.Occluded ? 0.7 : 1.0;
                outline.IsVisible = true;
                fg.IsVisible = true;
            }
            else
            {
                outline.IsVisible = false;
                fg.IsVisible = false;
            }
        }

        // Hide any leftover pool entries beyond the current count.
        for (int i = labels.Count * 2; i < pool.Count; i++)
            pool[i].IsVisible = false;
    }

    Border[]? _gizmoLabelBorders;

    static IBrush GetGizmoLabelBrush(int axis, bool hovered)
    {
        // Match the gizmo axis palette; positive ends get the saturated colors.
        (byte r, byte g, byte b) c = axis switch
        {
            0 => (255, 51, 51),    // +X
            2 => (51, 255, 51),    // +Y
            4 => (77, 128, 255),   // +Z
            _ => (200, 200, 200)
        };
        if (hovered)
        {
            c.r = (byte)Math.Min(255, c.r + 40);
            c.g = (byte)Math.Min(255, c.g + 40);
            c.b = (byte)Math.Min(255, c.b + 40);
        }
        return new SolidColorBrush(Avalonia.Media.Color.FromRgb(c.r, c.g, c.b));
    }

    void OnGizmoLabelsProjected(IReadOnlyList<Controls.GlPreviewControl.GizmoLabel> labels)
    {
        if (_gizmoLabelBorders == null)
        {
            _gizmoLabelBorders = new Border[labels.Count];
            for (int i = 0; i < labels.Count; i++)
            {
                var tb = new TextBlock
                {
                    Text = labels[i].Letter,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                var border = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Child = tb,
                    IsHitTestVisible = false
                };
                _3dLabelCanvas.Children.Add(border);
                _gizmoLabelBorders[i] = border;
            }
        }

        for (int i = 0; i < labels.Count && i < _gizmoLabelBorders.Length; i++)
        {
            var l = labels[i];
            var b = _gizmoLabelBorders[i];
            b.Background = GetGizmoLabelBrush(l.Axis, l.Hovered);
            Canvas.SetLeft(b, l.X - b.Width * 0.5);
            Canvas.SetTop(b, l.Y - b.Height * 0.5);
            b.IsVisible = true;
        }
    }

    void LoadMeshIntoScene(PreviewMeshData meshData)
    {
        try
        {
            EnsureGlPreviewInitialized();
            _glPreview!.LoadMesh(meshData);
            // Clear any previously bound textures so the new mesh doesn't briefly render
            // with the old TMM's textures while EnsureTexturesLoadedAsync is in flight.
            // Cache hits in EnsureTexturesLoadedAsync will re-bind immediately.
            _glPreview.SetActiveTextures(null);
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

    async void Screenshot_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_glPreview == null || _glPreview.GetMeshData() == null)
            return;

        var scaling = GetTopLevel(_glPreview)?.RenderScaling ?? 1.0;
        int baseW = (int)(_glPreview.Bounds.Width * scaling);
        int baseH = (int)(_glPreview.Bounds.Height * scaling);
        if (baseW <= 0 || baseH <= 0)
            return;

        var suggestedBase = Path.GetFileNameWithoutExtension(_currentTmmFileName ?? "model");
        var dialog = new CaptureScreenshotWindow(baseW, baseH,
            _screenshotScaleFactor, _screenshotTransparent, _screenshotFormat,
            suggestedBase, async (options, file) =>
            {
                int w = (int)(baseW * options.Factor);
                int h = (int)(baseH * options.Factor);
                var capture = await _glPreview.CaptureScreenshotAsync(w, h, options.Transparent);

                await using var stream = await file.OpenWriteAsync();
                await ScreenshotHelpers.EncodeAsync(capture.Rgba, capture.Width, capture.Height, options.Format, stream);
            });
        await dialog.ShowDialog(this);

        var result = dialog.GetResult();
        if (result != null)
        {
            _screenshotScaleFactor = result.Factor;
            _screenshotTransparent = result.Transparent;
            _screenshotFormat = result.Format;
            SaveConfiguration();

            Update3DStatus("Screenshot saved");
            await Task.Delay(2000);
            if (_3dStatusText.Text == "Screenshot saved")
                Update3DStatus("");
        }
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
        if (_fmodBank == null)
            return;

        BankEntries.AddItems(FilterBankItems(EnumerateBankItems(_fmodBank)));
    }

    static IEnumerable<IBankItem> EnumerateBankItems(FMODBank bank)
    {
        foreach (var e in bank.Events) yield return e;
        foreach (var s in bank.Subsounds) yield return s;
    }

    IEnumerable<BarFileEntry> FilterBAR(IEnumerable<BarFileEntry> entries)
    {
        var q = EntryQuery;
        foreach (var e in entries)
        {
            if (!GlobMatcher.IsMatch(e.RelativePath, q))
                continue;

            if (_filterOnlyOverriddenBarEntries)
            {
                var fullRel = GetBARFullRelativePath(e);
                if (!IsFileOverriden(fullRel) && !IsFileAdditiveModded(fullRel))
                    continue;
            }

            yield return e;
        }
    }

    IEnumerable<RootFileEntry> FilterFile(IEnumerable<RootFileEntry> entries)
    {
        var q = FilesQuery;
        foreach (var e in entries)
        {
            if (!GlobMatcher.IsMatch(e.RelativePath, q))
                continue;

            if (_filterOnlyOverriddenFiles)
            {
                var fullRel = GetRootFullRelativePath(e);
                if (!IsFileOverriden(fullRel) && !IsFileAdditiveModded(fullRel))
                    continue;
            }

            yield return e;
        }
    }

    IEnumerable<IBankItem> FilterBankItems(IEnumerable<IBankItem> items)
    {
        var q = BankQuery;
        foreach (var i in items)
        {
            if (!GlobMatcher.IsMatch(i.DisplayName, q))
                continue;

            yield return i;
        }
    }
    #endregion

    #region ZIP hierarchy
    static string BuildZipHierarchyText(ZipArchive archive)
    {
        // Build tree from entry paths
        var root = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0, dirCount = 0;

        foreach (var entry in archive.Entries)
        {
            var fullName = entry.FullName.Replace('\\', '/');
            var parts = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                bool isLast = i == parts.Length - 1;
                bool isDir = isLast && fullName.EndsWith('/');

                if (isLast && !isDir)
                {
                    // File leaf - store size
                    current[parts[i]] = entry.Length;
                    fileCount++;
                }
                else
                {
                    // Directory node
                    var dirKey = parts[i] + "/";
                    if (!current.TryGetValue(dirKey, out var child) || child is not SortedDictionary<string, object?>)
                    {
                        var newDir = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        current[dirKey] = newDir;
                        dirCount++;
                        current = newDir;
                    }
                    else
                    {
                        current = (SortedDictionary<string, object?>)child;
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Archive: {fileCount + dirCount} entries ({dirCount} folders, {fileCount} files)");
        sb.AppendLine();
        RenderTree(sb, root, "");
        return sb.ToString();
    }

    static void RenderTree(StringBuilder sb, SortedDictionary<string, object?> node, string prefix)
    {
        // Sort: directories first, then files, both alphabetically
        var entries = node.OrderBy(kv => kv.Value is SortedDictionary<string, object?> ? 0 : 1)
                         .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                         .ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            var (name, value) = entries[i];
            bool isLast = i == entries.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var childPrefix = isLast ? "    " : "│   ";

            if (value is SortedDictionary<string, object?> children)
            {
                sb.AppendLine($"{prefix}{connector}{name}");
                RenderTree(sb, children, prefix + childPrefix);
            }
            else
            {
                var size = (long)(value ?? 0);
                sb.AppendLine($"{prefix}{connector}{name} ({size:N0} bytes)");
            }
        }
    }
    #endregion
}
