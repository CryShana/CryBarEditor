using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CryBar.Bar;
using CryBar.Scenario;
using CryBar.Scenario.Editor;
using CryBar.Utilities;
using CryBarEditor.Classes;
using CryBarEditor.Controls;

namespace CryBarEditor;

public partial class MainWindow
{
    ScenarioPreviewData? _scenarioData;
    GlScenarioPreviewControl? _scenarioGl;
    string? _manualTextureBarPath;
    ScenarioFile? _pendingScenario3D;
    CancellationTokenSource? _texturesLoadCts;

    void ShowScenarioPreview(ScenarioFile scenario)
    {
        HideTmmPreview();
        HideScenarioPreview();

        _flatPreview.IsVisible = false;
        _scenarioRoot.IsVisible = true;

        // Reparent the main editor into the XML tab so the scenario view reuses
        // the existing TextMate grammar, folding manager, and large-doc cache.
        if (_txtEditor.Parent is Panel panel)
            panel.Children.Remove(_txtEditor);
        _scenarioXmlHost.Content = _txtEditor;
        _txtEditor.IsVisible = true;
        _ = SetEditorText(".xml", ScenarioFile.StripBinaryForPreview(scenario.ToXml()));

        // Defer 3D build/upload until the user actually opens the 3D View tab.
        _pendingScenario3D = scenario;
        if (_scenarioTabStrip.SelectedIndex == 1) FlushPendingScenario3D();
    }

    void FlushPendingScenario3D()
    {
        var scenario = _pendingScenario3D;
        if (scenario is null) return;
        _pendingScenario3D = null;

        var data = ScenarioPreviewData.TryBuild(scenario);
        if (data is null)
        {
            _scenarioErrorPanel.IsVisible = true;
            _scenarioErrorText.Text = "Could not parse scenario terrain.";
            _scenarioCanvasContainer.IsVisible = false;
            return;
        }
        _scenarioData = data;

        _scenarioGl ??= CreateScenarioGl();
        _scenarioCanvasContainer.Child = _scenarioGl;
        _scenarioCanvasContainer.IsVisible = true;
        _scenarioErrorPanel.IsVisible = false;
        _scenarioInspector.SetCursor(null, null);

        _scenarioInspector.ClearSelectionRequested -= OnInspectorClearSelection;
        _scenarioInspector.ClearSelectionRequested += OnInspectorClearSelection;

        _scenarioGl.SetScenario(data);

        data.Selection.Changed -= OnScenarioSelectionChanged;
        data.Selection.Changed += OnScenarioSelectionChanged;
        _scenarioInspector.UpdateSelection(data);

        data.Editor.Changed -= OnScenarioEditorChanged;
        data.Editor.Changed += OnScenarioEditorChanged;

        _scenarioInspector.ExecuteCommand = cmd => data.Editor.Execute(cmd);
        _scenarioInspector.LoadProtoNamesAsync = async () => await GetOrLoadProtoNamesAsync(data);
        _scenarioInspector.LoadTerrainTypesAsync = async () => await GetOrLoadTerrainTypesAsync(data);
        _scenarioInspector.LoadMajorGodNamesAsync = async () => await GetOrLoadMajorGodNamesAsync(data);
        _scenarioInspector.SetWorld(data);

        _scenarioInspector.BindEditor(data.Editor, sourcePath: ResolveScenarioSourcePath());
        _scenarioInspector.SaveRequested    -= OnToolbarSave;
        _scenarioInspector.SaveAsRequested  -= OnToolbarSaveAs;
        _scenarioInspector.DiscardRequested -= OnToolbarDiscard;
        _scenarioInspector.SaveRequested    += OnToolbarSave;
        _scenarioInspector.SaveAsRequested  += OnToolbarSaveAs;
        _scenarioInspector.DiscardRequested += OnToolbarDiscard;
        _scenarioInspector.SelectGodBarRequested -= SelectGodBarRequested;
        _scenarioInspector.SelectGodBarRequested += SelectGodBarRequested;

        if (_scenarioGl is not null)
        {
            _scenarioGl.GestureCommitted -= OnGestureCommitted;
            _scenarioGl.GestureCommitted += OnGestureCommitted;
            // Texture array realloc wipes slices to placeholder; refill all.
            _scenarioGl.TextureArrayResized -= OnScenarioTextureArrayResized;
            _scenarioGl.TextureArrayResized += OnScenarioTextureArrayResized;
        }

        StartTexturesLoad(data);
    }

    void OnScenarioTextureArrayResized()
    {
        if (_scenarioData is { } data)
            StartTexturesLoad(data);
    }

    // Single in-flight load: the open-scenario load otherwise overlaps with reloads
    // fired by EnsureSlot-grew-array events. Both walk data.TextureSet.Names while
    // the UI thread mutates it (List<T> is not thread-safe) and both queue uploads
    // to the same slot indices.
    void StartTexturesLoad(ScenarioPreviewData data)
    {
        var prev = _texturesLoadCts;
        _texturesLoadCts = CancellationTokenSource.CreateLinkedTokenSource(data.Cancellation.Token);
        prev?.Cancel();
        prev?.Dispose();
        _ = LoadScenarioTexturesAsync(data, _texturesLoadCts.Token);
    }

    void OnScenarioSelectionChanged()
    {
        var d = _scenarioData;
        if (d is null) return;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(d, _scenarioData)) _scenarioInspector.UpdateSelection(d); });
    }

    void OnScenarioEditorChanged()
    {
        var d = _scenarioData;
        if (d is null) return;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(d, _scenarioData)) OnEditorChanged(d); });
    }

    void OnEditorChanged(ScenarioPreviewData data)
    {
        if (!ReferenceEquals(data, _scenarioData)) return;

        // Discard() sets LastChange = null -> rebuild everything.
        var hint = data.Editor.LastChange?.Hint;
        var effective = hint ?? (RenderHint.TerrainTexture | RenderHint.TerrainGeometry
                                | RenderHint.TerrainWater   | RenderHint.EntityList
                                | RenderHint.Players);

        // Selection may reference dead ids; prune before renderer rebuilds.
        // Also invalidate the id->index cache: DeleteEntities shifts indices.
        if ((effective & RenderHint.EntityList) != 0)
        {
            data.InvalidateEntityIndex();
            var liveIds = data.EntityIdToIndex;
            List<uint>? toRemove = null;
            foreach (var id in data.Selection.Entities)
            {
                if (!liveIds.ContainsKey(id))
                {
                    toRemove ??= new List<uint>();
                    toRemove.Add(id);
                }
            }
            if (toRemove is not null)
                foreach (var id in toRemove) data.Selection.RemoveEntity(id);
        }

        _scenarioGl?.OnDataMutated(effective);
        _scenarioInspector.UpdateSelection(data);
    }

    // Routed through editor.Execute so gestures are undoable. Null cmd is a
    // no-op inside Execute itself.
    void OnGestureCommitted(IScenarioCommand? cmd) => _scenarioData?.Editor.Execute(cmd);

    // Loose-file path of the previewed scenario, or null for BAR entries / no selection.
    // Used as a Save As start-folder fallback.
    string? ResolveScenarioSourcePath()
    {
        if (_currentlyPreviewedItem is RootFileEntry rfe && Directory.Exists(_rootDirectory))
            return Path.Combine(_rootDirectory, rfe.RelativePath);
        return null;
    }

    async void OnToolbarSave()
    {
        if (_scenarioData is null) return;
        var ed = _scenarioData.Editor;
        if (ed.SavePath is null) { await OnToolbarSaveAsAsync(); return; }
        await DoSaveTo(_scenarioData, ed.SavePath);
    }

    async void OnToolbarSaveAs() => await OnToolbarSaveAsAsync();

    async Task OnToolbarSaveAsAsync()
    {
        if (_scenarioData is null) return;
        var ed = _scenarioData.Editor;
        var sourcePath = ResolveScenarioSourcePath();

        // Folder priority: explicit per-session memory, then editor.SavePath
        // dir (re-saving an already-saved file), then the source loose-file dir.
        string? startDir = _lastConfiguration?.ScenarioLastSaveDirectory;
        if (string.IsNullOrEmpty(startDir) && ed.SavePath is not null)
            startDir = Path.GetDirectoryName(ed.SavePath);
        if (string.IsNullOrEmpty(startDir) && sourcePath is not null)
            startDir = Path.GetDirectoryName(sourcePath);

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrEmpty(startDir))
            startFolder = await StorageProvider.TryGetFolderFromPathAsync(startDir);

        var suggestedName = Path.GetFileName(ed.SavePath ?? sourcePath ?? "untitled.mythscn");
        if (string.IsNullOrEmpty(suggestedName)) suggestedName = "untitled.mythscn";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save scenario as",
            SuggestedFileName = suggestedName,
            FileTypeChoices =
            [
                new FilePickerFileType("AoM Scenario") { Patterns = ["*.mythscn"] },
            ],
            SuggestedStartLocation = startFolder,
        });
        if (file is null) return;

        var path = file.Path.LocalPath;
        await DoSaveTo(_scenarioData, path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _lastConfiguration ??= new Configuration();
            _lastConfiguration.ScenarioLastSaveDirectory = dir;
            SaveConfiguration();
        }
    }

    async Task DoSaveTo(ScenarioPreviewData data, string path)
    {
        try
        {
            // Offload sync compression off the UI thread.
            await Task.Run(() =>
            {
                data.Scenario.FlushParsedViews(data.Terrain, data.Entities, data.ProtoTable, data.Players);
                var bytes = data.Scenario.ToBytes();
                var compressed = BarCompression.CompressL33t(bytes);
                using var f = File.Create(path);
                f.Write(compressed.Span);
            });
            data.Editor.MarkSaved(path);
        }
        catch (Exception ex)
        {
            await ShowError("Save failed:\n" + ex.Message);
        }
    }

    async void OnToolbarDiscard()
    {
        if (_scenarioData is null) return;
        var ed = _scenarioData.Editor;
        if (!ed.IsDirty) return;
        var ok = await Confirm("Discard changes?", $"Discard {ed.UndoCount} unsaved change(s)?");
        if (!ok) return;
        ed.Discard();
    }

    // Scoped to the scenario root so Ctrl+S/Z don't steal keys from other text fields.
    void ScenarioRoot_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_scenarioData is null) return;
        var ed = _scenarioData.Editor;
        var ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.S when shift:
                _ = OnToolbarSaveAsAsync();
                e.Handled = true;
                break;
            case Key.S:
                OnToolbarSave();
                e.Handled = true;
                break;
            case Key.Z when shift:
                ed.Redo();
                e.Handled = true;
                break;
            case Key.Z:
                ed.Undo();
                e.Handled = true;
                break;
            case Key.Y:
                ed.Redo();
                e.Handled = true;
                break;
        }
    }

    void OnInspectorClearSelection() => _scenarioData?.Selection.Clear();

    // Shift+click box-selects everything between the last single-click anchor
    // and this hit. Entity selection uses the screen rectangle between the two
    // discs (camera-aligned, matches what the user sees). Y tolerance still
    // filters above/below-water altitude bands.
    const float BoxSelectHeightTolerance = 15f;
    bool TryShiftBoxSelect(PickHit hit)
    {
        if (_scenarioData is null) return false;
        var sel = _scenarioData.Selection;

        if (sel.LastClickedEntity is uint anchorId && hit.EntityId is uint hitId && _scenarioGl is not null)
        {
            var ids = _scenarioGl.PickEntitiesInScreenRectBetween(anchorId, hitId, BoxSelectHeightTolerance);
            sel.AddEntities(ids);
            return true;
        }

        // Box-select tiles when both anchor and hit are tiles.
        if (sel.LastClickedTile is int anchorTile && hit.TileIdx is int hitTile && hit.EntityId is null)
        {
            int mapX = _scenarioData.Terrain.MapSizeX;
            int ax = anchorTile % mapX, az = anchorTile / mapX;
            int bx = hitTile % mapX,    bz = hitTile / mapX;
            int minTx = Math.Min(ax, bx), maxTx = Math.Max(ax, bx);
            int minTz = Math.Min(az, bz), maxTz = Math.Max(az, bz);

            var idxs = new List<int>((maxTx - minTx + 1) * (maxTz - minTz + 1));
            for (int z = minTz; z <= maxTz; z++)
                for (int x = minTx; x <= maxTx; x++)
                    idxs.Add(z * mapX + x);
            sel.AddTiles(idxs);
            return true;
        }

        return false;
    }

    GlScenarioPreviewControl CreateScenarioGl()
    {
        var gl = new GlScenarioPreviewControl();
        gl.CursorHit += hit =>
            Dispatcher.UIThread.Post(() => _scenarioInspector.SetCursor(hit, _scenarioData));
        gl.LeftClicked += (hit, ctrl, shift) =>
        {
            if (_scenarioData is null) return;
            if (shift && TryShiftBoxSelect(hit)) return;
            ScenarioSelectionInput.OnLeftClick(_scenarioData.Selection, hit, ctrl);
        };
        gl.RightClicked += (hit, ctrl) =>
        {
            if (_scenarioData is null) return;
            ScenarioSelectionInput.OnRightClick(_scenarioData.Selection, hit, ctrl);
        };
        gl.ErrorChanged += msg =>
            Dispatcher.UIThread.Post(() =>
            {
                _scenarioErrorText.Text = msg ?? "";
                _scenarioErrorPanel.IsVisible = msg is not null;
            });
        gl.ShowEntities = _showScenarioEntitiesCheckbox.IsChecked == true;
        gl.ShowWater = _showScenarioWaterCheckbox.IsChecked == true;
        return gl;
    }

    void ScenarioTab_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ToggleTabPanels(_scenarioTabStrip, _scenarioXmlHost, _scenario3dPanel);
        if (_scenarioTabStrip?.SelectedIndex == 1 && _pendingScenario3D is not null)
            FlushPendingScenario3D();
    }

    void ShowScenarioEntities_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scenarioGl != null)
            _scenarioGl.ShowEntities = _showScenarioEntitiesCheckbox.IsChecked == true;
        SaveConfiguration();
    }

    void ShowScenarioWater_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scenarioGl != null)
            _scenarioGl.ShowWater = _showScenarioWaterCheckbox.IsChecked == true;
        SaveConfiguration();
    }

    void UpdateScenarioProgress(ScenarioTextureLoader.LoadProgress p)
    {
        _scenarioProgressText.Text =
            $"Resolving {p.Resolved}/{p.Total} - Decoding {p.Decoded}/{p.Total} - Uploading {p.Uploaded}/{p.Total}";
        _scenarioProgressBar.Value = (p.Resolved + p.Decoded + p.Uploaded) / (3.0 * Math.Max(1, p.Total));
    }

    async Task LoadScenarioTexturesAsync(ScenarioPreviewData data, CancellationToken ct)
    {
        if (_fileIndex is null || _scenarioGl is null) return;

        Dispatcher.UIThread.Post(() => _scenarioProgressOverlay.IsVisible = true);

        // User-picked BAR opens eagerly for immediate failure feedback; the
        // auto-probed game BAR opens lazily on the first index miss so the
        // common indexed-root case never pays the TOC parse.
        var userBarPath = _manualTextureBarPath;
        var manualBarPath = userBarPath ?? GameInstall.TryFindFile("art", "ArtTerrainTextures.bar");
        ManualTextureBar? manualBar = null;
        Lazy<Task<ManualTextureBar?>>? lazyBar = null;
        if (userBarPath is not null)
        {
            manualBar = await Task.Run(() => ManualTextureBar.TryOpen(userBarPath), ct);
            if (manualBar is null)
            {
                Dispatcher.UIThread.Post(() =>
                    _scenarioInspector.SetManualBarStatus(userBarPath, loadFailed: true));
                _manualTextureBarPath = null;
                manualBarPath = null;
            }
        }
        else if (manualBarPath is not null)
        {
            var probedPath = manualBarPath;
            lazyBar = new(() => Task.Run(() => ManualTextureBar.TryOpen(probedPath)));
        }

        try
        {
            ScenarioTextureLoader.NameResolver.ResolveFromManualBarAsync? barResolver =
                manualBar is not null ? manualBar.ResolveTextureAsync
                : lazyBar is not null ? async (name, innerCt) =>
                    await lazyBar.Value is { } bar ? await bar.ResolveTextureAsync(name, innerCt) : null
                : null;

            var resolver = new ScenarioTextureLoader.NameResolver(
                _fileIndex,
                ReadFromIndexEntryPooledAsync,
                barResolver);

            await ScenarioTextureLoader.LoadAllAsync(
                data,
                resolver,
                (sliceIdx, rgba) => _scenarioGl.UploadSliceAsync(sliceIdx, rgba),
                p => Dispatcher.UIThread.Post(() => UpdateScenarioProgress(p)),
                ct);

            Dispatcher.UIThread.Post(() =>
            {
                _scenarioInspector.UpdateAfterLoad(data);

                bool usedBar = data.TextureSources.Contains(TextureSource.ManualBar);

                // Auto-probed path is only worth showing when it actually supplied textures.
                if (manualBarPath is not null && (usedBar || _manualTextureBarPath is not null))
                    _scenarioInspector.SetManualBarStatus(manualBarPath, loadFailed: false);
            });
        }
        catch (OperationCanceledException) { /* expected on scenario change */ }
        finally
        {
            manualBar?.Dispose();
            if (lazyBar is { IsValueCreated: true })
                (await lazyBar.Value)?.Dispose();
            Dispatcher.UIThread.Post(() => _scenarioProgressOverlay.IsVisible = false);
        }
    }

    // Shared "find XMB in index -> decompress -> XML text" step used by the
    // proto, terrain-type, and god-name caches.
    async ValueTask<string?> LoadXmbXmlFromIndexAsync(string fileName)
    {
        if (_fileIndex is null) return null;

        var entries = _fileIndex.Find(fileName);
        if (entries.Count == 0) return null;

        try
        {
            using var raw = await ReadFromIndexEntryPooledAsync(entries[0]);
            if (raw == null) return null;

            using var decompressed = BarCompression.EnsureDecompressedPooled(raw, out _);
            return ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
        }
        catch
        {
            return null;
        }
    }

    // Major god names in major_gods.xml civ order (P2 god id is a 1-based index
    // into this list). Resolved from the FileIndex when the root covers the game
    // dir, otherwise from Data.bar (auto-probed or user-selected).
    async ValueTask<List<string>?> GetOrLoadMajorGodNamesAsync(ScenarioPreviewData data)
    {
        if (data.MajorGodNamesCache is not null) return data.MajorGodNamesCache;
        if (data.MajorGodNamesUnavailable) return null;

        List<string>? names = null;
        if (await LoadXmbXmlFromIndexAsync("major_gods.xml.XMB") is { } xmlText)
        {
            var parsed = ParseMajorGodNamesFromXml(xmlText);
            if (parsed.Count > 0) names = parsed;
        }

        if (names is null && GameInstall.TryFindFile("data", "Data.bar") is { } dataBar)
            names = await LoadGodNamesFromBarAsync(dataBar);

        if (names is null)
        {
            data.MajorGodNamesUnavailable = true;
            return null;
        }

        data.MajorGodNamesCache = names;
        return names;
    }

    static async Task<List<string>?> LoadGodNamesFromBarAsync(string barPath)
    {
        var xmlText = await Task.Run(() => LoadMajorGodsXmlFromBarAsync(barPath));
        if (xmlText is null) return null;

        var names = ParseMajorGodNamesFromXml(xmlText);
        return names.Count > 0 ? names : null;
    }

    static async Task<string?> LoadMajorGodsXmlFromBarAsync(string barPath)
    {
        try
        {
            var stream = File.OpenRead(barPath);
            var bar = new BarFile(stream);
            if (!bar.Load(out _)) { stream.Dispose(); return null; }

            using var cached = new CachedBarFile(bar, stream);
            var entry = bar.Entries?.FirstOrDefault(e =>
                e.RelativePath.EndsWith("major_gods.xml.XMB", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var raw = await cached.ReadEntryRawPooledAsync(entry);
            if (raw is null) return null;

            using var decompressed = BarCompression.EnsureDecompressedPooled(raw, out _);
            return ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
        }
        catch
        {
            return null;
        }
    }

    // <civs><civ><name>Zeus</name>... - god id is the 1-based civ position.
    static List<string> ParseMajorGodNamesFromXml(string xmlText)
    {
        var names = new List<string>();
        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlText));
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.Name != "civ") continue;

            string? name = null;
            using (var civ = reader.ReadSubtree())
            {
                while (civ.Read())
                {
                    if (civ.NodeType == System.Xml.XmlNodeType.Element && civ.Name == "name")
                    {
                        name = civ.ReadElementContentAsString();
                        break;
                    }
                }
            }
            names.Add(name ?? "");
        }
        return names;
    }

    async Task<string?> PickBarFileAsync(string title)
    {
        var picker = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("BAR archive") { Patterns = ["*.bar"] },
            ],
        });
        if (picker.Count == 0) return null;
        var local = picker[0].Path.LocalPath;
        return string.IsNullOrEmpty(local) ? null : local;
    }

    async void SelectGodBarRequested()
    {
        if (_scenarioData is not { } data) return;

        var local = await PickBarFileAsync("Select Data.bar (contains major_gods.xml)");
        if (local is null) return;

        if (await LoadGodNamesFromBarAsync(local) is { } names)
        {
            data.MajorGodNamesCache = names;
            data.MajorGodNamesUnavailable = false;
        }
        _scenarioInspector.SetWorld(data);
    }

    async void SelectManualTextureBarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var local = await PickBarFileAsync("Select fallback ArtTerrainTextures.bar");
        if (local is null) return;

        _manualTextureBarPath = local;

        if (_scenarioData is { } existing)
            StartTexturesLoad(existing);
        else if (_pendingScenario3D is not null && _scenarioTabStrip.SelectedIndex == 1)
            FlushPendingScenario3D();
    }

    // Lazy game-wide proto-name list. Null on failure -> caller falls back to ProtoTable.
    async ValueTask<List<string>?> GetOrLoadProtoNamesAsync(ScenarioPreviewData data)
    {
        if (data.ProtoNamesCache is not null) return data.ProtoNamesCache;

        if (await LoadXmbXmlFromIndexAsync("proto.xml.XMB") is not { } xmlText) return null;

        var names = ParseProtoNamesFromXml(xmlText);
        data.ProtoNamesCache = names;
        return names;
    }

    // XmlReader (not XDocument) for AOT-friendliness. Alphabetical for browse-ability.
    static List<string> ParseProtoNamesFromXml(string xmlText)
    {
        var names = new List<string>();
        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlText));
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
            if (reader.Name != "unit") continue;
            var name = reader.GetAttribute("name");
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // Lazy game-wide (group, texture) list. Null on failure -> caller falls back
    // to BuildScenarioFallbackCache.
    async ValueTask<TerrainTypesCache?> GetOrLoadTerrainTypesAsync(ScenarioPreviewData data)
    {
        if (data.TerrainTypesCache is not null) return data.TerrainTypesCache;

        if (await LoadXmbXmlFromIndexAsync("terrain_types.xml.XMB") is not { } xmlText) return null;

        var cache = ParseTerrainTypesFromXml(xmlText);
        data.TerrainTypesCache = cache;
        return cache;
    }

    // <terraintypes><type name=...><uiclass><subtype>TEX</subtype>...
    // De-dup per group (same tex appears under multiple ui categories). XmlReader for AOT.
    static TerrainTypesCache ParseTerrainTypesFromXml(string xmlText)
    {
        var byGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlText));

        string? curGroup = null;
        while (reader.Read())
        {
            if (reader.NodeType == System.Xml.XmlNodeType.Element)
            {
                if (reader.Name == "type")
                {
                    curGroup = reader.GetAttribute("name");
                    if (!string.IsNullOrEmpty(curGroup) && !byGroup.ContainsKey(curGroup))
                        byGroup[curGroup] = new HashSet<string>(StringComparer.Ordinal);
                }
                else if (reader.Name == "subtype" && !string.IsNullOrEmpty(curGroup))
                {
                    // ReadElementContentAsString moves the reader past the end tag,
                    // so we don't manually track depth.
                    var tex = reader.ReadElementContentAsString();
                    if (!string.IsNullOrEmpty(tex))
                        byGroup[curGroup].Add(tex);
                }
            }
            else if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Name == "type")
            {
                curGroup = null;
            }
        }

        var groupNames = new List<string>(byGroup.Keys);
        groupNames.Sort(StringComparer.OrdinalIgnoreCase);

        var all = new List<(string Group, string Texture)>();
        foreach (var g in groupNames)
        {
            var list = new List<string>(byGroup[g]);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) all.Add((g, t));
        }

        return new TerrainTypesCache { All = all };
    }

    void HideScenarioPreview()
    {
        _pendingScenario3D = null;

        if (_scenarioData is { } prev)
        {
            prev.Selection.Changed -= OnScenarioSelectionChanged;
            prev.Editor.Changed -= OnScenarioEditorChanged;
        }

        // Detach inspector before the editor goes away.
        _scenarioInspector.BindEditor(null, sourcePath: null);
        _scenarioInspector.ExecuteCommand = null;
        _scenarioInspector.LoadProtoNamesAsync = null;
        _scenarioInspector.LoadTerrainTypesAsync = null;
        _scenarioInspector.LoadMajorGodNamesAsync = null;
        _scenarioInspector.SetWorld(null);
        if (_scenarioGl is not null)
        {
            _scenarioGl.GestureCommitted -= OnGestureCommitted;
            _scenarioGl.TextureArrayResized -= OnScenarioTextureArrayResized;
        }

        _texturesLoadCts?.Cancel();
        _texturesLoadCts?.Dispose();
        _texturesLoadCts = null;

        // Dispose() cancels the data's CTS, which propagates to the in-flight load.
        _scenarioData?.Dispose();
        _scenarioData = null;

        if (_scenarioRoot.IsVisible)
        {
            _scenarioRoot.IsVisible = false;
            _flatPreview.IsVisible = true;

            // Move the editor back to the flat preview panel so other dispatchers can use it.
            if (ReferenceEquals(_scenarioXmlHost.Content, _txtEditor))
            {
                _scenarioXmlHost.Content = null;
                if (!_flatPreview.Children.Contains(_txtEditor))
                    _flatPreview.Children.Add(_txtEditor);
            }
        }
        _scenarioProgressOverlay.IsVisible = false;
        _scenarioErrorPanel.IsVisible = false;

        if (_scenarioGl is not null)
            _scenarioGl.SetScenario(null);
    }
}
