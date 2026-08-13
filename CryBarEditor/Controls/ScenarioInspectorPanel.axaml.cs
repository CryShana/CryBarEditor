using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CryBar.Scenario;
using CryBar.Scenario.Editor;
using CryBar.Scenario.Editor.Commands;
using CryBarEditor.Classes;
using CryBarEditor.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CryBarEditor.Controls;

public partial class ScenarioInspectorPanel : UserControl
{
    public ScenarioInspectorPanel()
    {
        InitializeComponent();
        RefreshSaveBar();
    }

    public event System.Action? SelectBarRequested;

    ScenarioEditor? _boundEditor;
    string? _boundSourcePath;

    public event System.Action? SaveRequested;
    public event System.Action? SaveAsRequested;
    public event System.Action? DiscardRequested;

    // Wired by MainWindow; null = handlers no-op.
    public System.Action<IScenarioCommand?>? ExecuteCommand;

    // Lazy-loaded full game lists for the pickers. Null = picker falls back to
    // the scenario's own TM table / TerrainGroups.
    public System.Func<System.Threading.Tasks.Task<List<string>?>>? LoadProtoNamesAsync;
    public System.Func<System.Threading.Tasks.Task<TerrainTypesCache?>>? LoadTerrainTypesAsync;

    ScenarioPreviewData? _data;

    // Guard ValueChanged/SelectionChanged from firing while we populate controls.
    bool _suppressWaterChange;
    bool _suppressHeightChange;
    bool _suppressEntityChange;

    static List<PlayerOption>? _playerOptions;
    static readonly int[] _waterTypeOptions = Enumerable.Range(0, 256).ToArray();

    void SelectBarClick(object? sender, RoutedEventArgs e) => SelectBarRequested?.Invoke();

    public void SetCursor(GlScenarioPreviewControl.WorldRayHit? hit, ScenarioPreviewData? data)
    {
        if (hit is null || data is null)
        {
            _cursorInfo.Text = "(hover map)";
            return;
        }
        var h = hit.Value;
        var groupName = "?"; var texName = "?";
        int tIdx = h.TileZ * data.Terrain.MapSizeX + h.TileX;
        if (tIdx >= 0 && tIdx < data.Terrain.TileGroups.Length)
        {
            var g = data.Terrain.TileGroups[tIdx];
            var s = data.Terrain.TileSubs[tIdx];
            if (g < data.Terrain.TerrainGroups.Length)
            {
                var grp = data.Terrain.TerrainGroups[g];
                groupName = grp.Name;
                if (s < grp.Textures.Length) texName = grp.Textures[s];
            }
        }

        _cursorInfo.Text =
            $"Tile: ({h.TileX}, {h.TileZ})\n" +
            $"Vertex: ({h.VertexX}, {h.VertexZ})\n" +
            $"Height: {h.Height:F2}\n" +
            $"Group: {groupName}\n" +
            $"Texture: {texName}";
    }

    public event System.Action? ClearSelectionRequested;

    void ClearSelectionClick(object? sender, RoutedEventArgs e) => ClearSelectionRequested?.Invoke();

    public void UpdateSelection(ScenarioPreviewData? data)
    {
        _data = data;

        if (data is null || data.Selection.Kind == ScenarioSelectionKind.None)
        {
            _selectedSection.IsVisible = false;
            _tileEditPanel.IsVisible = false;
            _entityEditPanel.IsVisible = false;
            return;
        }

        _selectedSection.IsVisible = true;

        if (data.Selection.Kind == ScenarioSelectionKind.Tiles)
            UpdateTileSelection(data);
        else
            UpdateEntitySelection(data);
    }

    void UpdateTileSelection(ScenarioPreviewData data)
    {
        _tileEditPanel.IsVisible = true;
        _entityEditPanel.IsVisible = false;

        var sel = data.Selection;
        int count = sel.Tiles.Count;
        _selectedHeader.Text = count == 1 ? "Selected tile" : $"Selected {count} tiles";

        var terrain = data.Terrain;
        int rowStride = terrain.MapSizeX + 1;

        // Aggregate over selected tiles. Field is MIXED if any value differs.
        float? avgHeight = null; bool heightMixed = false;
        (byte g, ushort s)? sharedTex = null; bool textureMixed = false;
        string? sharedTexName = null;
        byte? waterType = null; bool waterMixed = false;

        var lines = new System.Text.StringBuilder();
        foreach (int idx in sel.Tiles)
        {
            int tx = idx % terrain.MapSizeX;
            int tz = idx / terrain.MapSizeX;

            float h00 = terrain.Heights[tz       * rowStride + tx    ];
            float h10 = terrain.Heights[tz       * rowStride + tx + 1];
            float h11 = terrain.Heights[(tz + 1) * rowStride + tx + 1];
            float h01 = terrain.Heights[(tz + 1) * rowStride + tx    ];
            float avgH = (h00 + h10 + h11 + h01) * 0.25f;

            byte g = terrain.TileGroups[idx];
            ushort s = terrain.TileSubs[idx];
            string tName = "?";
            if (g < terrain.TerrainGroups.Length && s < terrain.TerrainGroups[g].Textures.Length)
                tName = terrain.TerrainGroups[g].Textures[s];

            byte wt = terrain.WaterType[idx];

            if (avgHeight is null) avgHeight = avgH;
            else if (!heightMixed && System.Math.Abs(avgH - avgHeight.Value) > 1e-3f) heightMixed = true;

            if (sharedTex is null) { sharedTex = (g, s); sharedTexName = tName; }
            else if (!textureMixed && (sharedTex.Value.g != g || sharedTex.Value.s != s)) textureMixed = true;

            if (waterType is null) waterType = wt;
            else if (!waterMixed && waterType != wt) waterMixed = true;

            if (count > 1) lines.AppendLine($"({tx}, {tz}) {tName}");
        }

        // TextBlock (not raw string) so underscores render literally; Button.Content as
        // string is fed through AccessText which interprets _x as a mnemonic prefix.
        _tileTextureBtn.Content = new TextBlock { Text = textureMixed ? "MIXED" : (sharedTexName ?? "?") };

        // Water types are byte 0..255 (255 = no water). Show plain ints in the combo.
        if (_tileWaterCombo.ItemsSource is null)
            _tileWaterCombo.ItemsSource = _waterTypeOptions;
        _suppressWaterChange = true;
        _tileWaterCombo.SelectedIndex = waterMixed ? -1 : (waterType ?? -1);
        _suppressWaterChange = false;

        _suppressHeightChange = true;
        _tileHeightNum.Value = heightMixed ? null : (avgHeight is null ? null : (decimal?)avgHeight.Value);
        _suppressHeightChange = false;

        _selectedListButton.IsVisible = count > 1;
        if (count > 1) _selectedListText.Text = lines.ToString().TrimEnd();
    }

    void UpdateEntitySelection(ScenarioPreviewData data)
    {
        _tileEditPanel.IsVisible = false;
        _entityEditPanel.IsVisible = true;

        var sel = data.Selection;
        int count = sel.Entities.Count;
        _selectedHeader.Text = count == 1 ? "Selected entity" : $"Selected {count} entities";

        var idToIdx = data.EntityIdToIndex;

        string? proto = null; bool protoMixed = false;
        byte? player = null; bool playerMixed = false;
        Vector3? position = null; bool positionMixed = false;
        float? yaw = null; bool yawMixed = false;

        var lines = new System.Text.StringBuilder();
        foreach (uint id in sel.Entities)
        {
            if (!idToIdx.TryGetValue(id, out int idx)) continue;
            var m = data.Entities[idx];

            if (proto is null) proto = m.ProtoName;
            else if (!protoMixed && proto != m.ProtoName) protoMixed = true;

            if (player is null) player = m.PlayerId;
            else if (!playerMixed && player != m.PlayerId) playerMixed = true;

            if (position is null) position = m.Position;
            else if (!positionMixed && Vector3.DistanceSquared(position.Value, m.Position) > 1e-4f)
                positionMixed = true;

            float ey = m.Rotation.ExtractYawDegrees();
            if (yaw is null) yaw = ey;
            else if (!yawMixed && System.Math.Abs(((ey - yaw.Value + 540f) % 360f) - 180f) > 0.1f)
                yawMixed = true;

            if (count > 1) lines.AppendLine($"[{id}] {m.ProtoName}");
        }

        // Populate proto button caption. TextBlock (not raw string) so underscores in
        // proto names render literally; raw string Content goes through AccessText.
        _entityProtoBtn.Content = new TextBlock { Text = protoMixed ? "MIXED" : (proto ?? "(none)") };

        // Player ComboBox: build options once, then select.
        if (_entityPlayerCombo.ItemsSource is null)
        {
            _playerOptions ??= BuildPlayerOptions();
            _entityPlayerCombo.ItemsSource = _playerOptions;
        }
        _suppressEntityChange = true;
        if (playerMixed || player is null)
            _entityPlayerCombo.SelectedIndex = -1;
        else
            _entityPlayerCombo.SelectedIndex = player.Value < _playerOptions!.Count ? player.Value : -1;
        _suppressEntityChange = false;

        // Position fields. MIXED -> blank.
        _suppressEntityChange = true;
        if (positionMixed || position is null)
        {
            _entityPosX.Value = null;
            _entityPosY.Value = null;
            _entityPosZ.Value = null;
        }
        else
        {
            _entityPosX.Value = (decimal)position.Value.X;
            _entityPosY.Value = (decimal)position.Value.Y;
            _entityPosZ.Value = (decimal)position.Value.Z;
        }

        if (yawMixed || yaw is null)
            _entityYaw.Value = null;
        else
            _entityYaw.Value = (decimal)yaw.Value;
        _suppressEntityChange = false;

        _selectedListButton.IsVisible = count > 1;
        if (count > 1) _selectedListText.Text = lines.ToString().TrimEnd();
    }

    static List<PlayerOption> BuildPlayerOptions()
    {
        var list = new List<PlayerOption>(PlayerColors.Count);
        for (byte i = 0; i < PlayerColors.Count; i++)
        {
            var c = PlayerColors.GetRgb(i);
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)System.Math.Clamp(c.R * 255f, 0f, 255f),
                (byte)System.Math.Clamp(c.G * 255f, 0f, 255f),
                (byte)System.Math.Clamp(c.B * 255f, 0f, 255f)));
            list.Add(new PlayerOption
            {
                PlayerId = i,
                Label = $"Player {i}",
                Brush = brush,
            });
        }
        return list;
    }

    // ----- Tile edit handlers -----

    async void OnTileTextureClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var data = _data;
            if (data is null) return;
            var sel = data.Selection;
            if (sel.Kind != ScenarioSelectionKind.Tiles || sel.Tiles.Count == 0) return;

            var terrain = data.Terrain;

            // Try game-wide list (cached, then lazy-loaded). Fall back to a synthetic
            // cache built from scenario's own TerrainGroups when XMB isn't reachable.
            var cache = data.TerrainTypesCache;
            if (cache is null && LoadTerrainTypesAsync is not null)
                cache = await LoadTerrainTypesAsync();
            // Re-check data after the await; user may have navigated away.
            if (!ReferenceEquals(data, _data)) return;
            cache ??= BuildScenarioFallbackCache(terrain);
            if (cache.All.Count == 0) return;

            var items = new List<PickerItem>(cache.All.Count);
            foreach (var (group, tex) in cache.All)
                items.Add(new PickerItem { Group = group, Display = tex });

            int firstTileIdx = sel.Tiles.First();
            byte curG = terrain.TileGroups[firstTileIdx];
            ushort curS = terrain.TileSubs[firstTileIdx];
            string? curGroupName = curG < terrain.TerrainGroups.Length
                ? terrain.TerrainGroups[curG].Name : null;
            string? curTexName = curG < terrain.TerrainGroups.Length
                && curS < terrain.TerrainGroups[curG].Textures.Length
                ? terrain.TerrainGroups[curG].Textures[curS] : null;
            int preselect = -1;
            if (curGroupName is not null && curTexName is not null)
            {
                for (int i = 0; i < cache.All.Count; i++)
                {
                    if (cache.All[i].Group == curGroupName && cache.All[i].Texture == curTexName)
                    {
                        preselect = i; break;
                    }
                }
            }

            var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
            if (owner is null) return;

            var picker = new PickerWindow("Pick tile texture", items, preselect >= 0 ? preselect : null);
            await picker.ShowDialog(owner);
            if (!ReferenceEquals(data, _data)) return;

            if (picker.PickedIndex is not int idx) return;
            var (newGroupName, newTexName) = cache.All[idx];

            var (newG, newS) = ResolveOrAppendTerrain(terrain, newGroupName, newTexName);

            // For newly-appended pairs the GL renderer detects the slice-count growth
            // on next frame, fires TextureArrayResized, and the host re-runs the load.
            data.TextureSet.EnsureSlot(newG, newS, newTexName, out var addedSliceIndex);
            if (addedSliceIndex is not null)
                data.EnsureSlotCapacity(data.TextureSet.Names.Count);

            var tileList = sel.Tiles.ToArray();
            var cmd = SetTileTextures.Create(terrain, tileList, newG, newS);
            ExecuteCommand?.Invoke(cmd);
        }
        catch (System.Exception ex)
        {
            ShowInspectorError("Tile texture pick failed:\n" + ex.Message);
        }
    }

    // Append-only: never reindex existing entries (existing tiles keep their (g, s)).
    static (byte g, ushort s) ResolveOrAppendTerrain(ScenarioTerrain terrain, string group, string texture)
    {
        for (int gi = 0; gi < terrain.TerrainGroups.Length; gi++)
        {
            var grp = terrain.TerrainGroups[gi];
            if (grp.Name != group) continue;
            for (int si = 0; si < grp.Textures.Length; si++)
                if (grp.Textures[si] == texture) return ((byte)gi, (ushort)si);

            // TerrainTextureGroup is init-only; replace slot with extended array.
            var newTexs = new string[grp.Textures.Length + 1];
            System.Array.Copy(grp.Textures, newTexs, grp.Textures.Length);
            newTexs[grp.Textures.Length] = texture;
            terrain.TerrainGroups[gi] = new TerrainTextureGroup { Name = grp.Name, Textures = newTexs };
            return ((byte)gi, (ushort)(newTexs.Length - 1));
        }

        // Group index is stored as a byte in the binary format.
        if (terrain.TerrainGroups.Length >= 256)
            throw new System.InvalidOperationException("Cannot add terrain group: scenario already has 256 groups (binary format max).");

        var newGroup = new TerrainTextureGroup { Name = group, Textures = new[] { texture } };
        var newGroups = new TerrainTextureGroup[terrain.TerrainGroups.Length + 1];
        System.Array.Copy(terrain.TerrainGroups, newGroups, terrain.TerrainGroups.Length);
        newGroups[^1] = newGroup;
        terrain.TerrainGroups = newGroups;
        return ((byte)(newGroups.Length - 1), 0);
    }

    // Fallback when terrain_types.xml.XMB isn't reachable. Preserves scenario-file order.
    static TerrainTypesCache BuildScenarioFallbackCache(ScenarioTerrain terrain)
    {
        var all = new List<(string Group, string Texture)>();
        foreach (var grp in terrain.TerrainGroups)
            foreach (var t in grp.Textures) all.Add((grp.Name, t));
        return new TerrainTypesCache { All = all };
    }

    void OnTileWaterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWaterChange) return;
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Tiles || sel.Tiles.Count == 0) return;

        int newIdx = _tileWaterCombo.SelectedIndex;
        if (newIdx < 0 || newIdx > 255) return;

        var tileList = sel.Tiles.ToArray();
        var cmd = SetTileWaterTypes.Create(_data.Terrain, tileList, (byte)newIdx);
        ExecuteCommand?.Invoke(cmd);
    }

    void OnTileHeightChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressHeightChange) return;
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Tiles || sel.Tiles.Count == 0) return;

        if (_tileHeightNum.Value is not decimal newDec) return;
        float newH = (float)newDec;

        ApplyHeightAbsolute(_data, sel, newH);
    }

    void OnTileHeightIncrement(object? sender, RoutedEventArgs e) => ApplyHeightDelta(+1f);
    void OnTileHeightDecrement(object? sender, RoutedEventArgs e) => ApplyHeightDelta(-1f);

    void ApplyHeightDelta(float delta)
    {
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Tiles || sel.Tiles.Count == 0) return;

        var terrain = _data.Terrain;
        var verts = VertexHeightHelpers.UniqueCornerVertices(sel.Tiles, terrain.MapSizeX);
        var vertList = verts.ToArray();
        var newH = new float[vertList.Length];
        for (int i = 0; i < vertList.Length; i++)
            newH[i] = terrain.Heights[vertList[i]] + delta;

        var cmd = SetVertexHeights.Create(terrain, vertList, newH);
        ExecuteCommand?.Invoke(cmd);
    }

    void ApplyHeightAbsolute(ScenarioPreviewData data, ScenarioSelection sel, float newHeight)
    {
        var terrain = data.Terrain;
        var verts = VertexHeightHelpers.UniqueCornerVertices(sel.Tiles, terrain.MapSizeX);
        var vertList = verts.ToArray();
        var newH = new float[vertList.Length];
        for (int i = 0; i < vertList.Length; i++) newH[i] = newHeight;

        var cmd = SetVertexHeights.Create(terrain, vertList, newH);
        ExecuteCommand?.Invoke(cmd);
    }

    public void UpdateAfterLoad(ScenarioPreviewData? data)
    {
        if (data is null)
        {
            _textureSummary.Text = "(no scenario)";
            _warningsRow.IsVisible = false;
            _selectBarButton.IsVisible = false;
            _manualBarPathText.IsVisible = false;
            return;
        }

        int total = data.TextureSources.Count;
        var counts = new int[3];
        for (int i = 0; i < total; i++)
            counts[(int)data.TextureSources[i]]++;
        int fromBar = counts[(int)TextureSource.ManualBar];
        int missing = counts[(int)TextureSource.Placeholder];

        _textureSummary.Text = (fromBar, missing) switch
        {
            (0, 0)         => $"{total} textures",
            (var b, 0)     => $"{total} textures ({b} from BAR)",
            (0, var m)     => $"{total} textures ({m} missing)",
            (var b, var m) => $"{total} textures ({b} from BAR, {m} missing)",
        };

        if (missing > 0)
        {
            var names = data.TextureSet.Names;
            var missingList = new System.Collections.Generic.List<string>(missing);
            for (int i = 0; i < total; i++)
                if (data.TextureSources[i] == TextureSource.Placeholder)
                    missingList.Add(names[i]);
            _missingNamesText.Text = string.Join('\n', missingList);

            _warningsText.Text = $"! {missing} of {total} textures missing";
            _warningsRow.IsVisible = true;
            _selectBarButton.IsVisible = true;
        }
        else
        {
            _warningsRow.IsVisible = false;
            _selectBarButton.IsVisible = false;
        }
    }

    // ----- Entity edit handlers -----

    async void OnEntityProtoClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var data = _data;
            if (data is null) return;
            var sel = data.Selection;
            if (sel.Kind != ScenarioSelectionKind.Entities || sel.Entities.Count == 0) return;

            var protoNames = data.ProtoNamesCache;
            if (protoNames is null && LoadProtoNamesAsync is not null)
                protoNames = await LoadProtoNamesAsync();
            if (!ReferenceEquals(data, _data)) return;
            protoNames ??= data.ProtoTable;
            if (protoNames.Count == 0) return;

            string? curName = null;
            var idToIdx = data.EntityIdToIndex;
            foreach (uint id in sel.Entities)
            {
                if (idToIdx.TryGetValue(id, out int idx)) { curName = data.Entities[idx].ProtoName; break; }
            }
            int preselect = curName is not null ? protoNames.IndexOf(curName) : -1;

            var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
            if (owner is null) return;

            var picker = new PickerWindow("Pick proto", protoNames, preselect >= 0 ? preselect : null);
            await picker.ShowDialog(owner);
            if (!ReferenceEquals(data, _data)) return;

            if (picker.PickedItem is null) return;
            var newProtoName = picker.PickedItem;

            var ids = sel.Entities.ToArray();
            var cmd = SetEntityProtos.Create(data.Entities, ids, newProtoName, data.ProtoTable);
            ExecuteCommand?.Invoke(cmd);
        }
        catch (System.Exception ex)
        {
            ShowInspectorError("Entity proto pick failed:\n" + ex.Message);
        }
    }

    void OnEntityPlayerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEntityChange) return;
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Entities || sel.Entities.Count == 0) return;

        int sIdx = _entityPlayerCombo.SelectedIndex;
        if (sIdx < 0 || _playerOptions is null || sIdx >= _playerOptions.Count) return;

        byte newPlayer = _playerOptions[sIdx].PlayerId;
        var ids = sel.Entities.ToArray();
        var cmd = SetEntityPlayers.Create(_data.Entities, ids, newPlayer);
        ExecuteCommand?.Invoke(cmd);
    }

    void OnEntityPosChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressEntityChange) return;
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Entities || sel.Entities.Count == 0) return;

        if (_entityPosX.Value is not decimal dx) return;
        if (_entityPosY.Value is not decimal dy) return;
        if (_entityPosZ.Value is not decimal dz) return;

        var pos = new Vector3((float)dx, (float)dy, (float)dz);
        var ids = sel.Entities.ToArray();
        var newPos = new Vector3[ids.Length];
        for (int i = 0; i < ids.Length; i++) newPos[i] = pos;

        var cmd = SetEntityPositions.Create(_data.Entities, ids, newPos);
        ExecuteCommand?.Invoke(cmd);
    }

    void OnEntityYawChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressEntityChange) return;
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Entities || sel.Entities.Count == 0) return;

        if (_entityYaw.Value is not decimal dyaw) return;

        var rot = Matrix3x3.FromYawDegrees((float)dyaw);
        var ids = sel.Entities.ToArray();
        var newRot = new Matrix3x3[ids.Length];
        for (int i = 0; i < ids.Length; i++) newRot[i] = rot;

        var cmd = SetEntityRotations.Create(_data.Entities, ids, newRot);
        ExecuteCommand?.Invoke(cmd);
    }

    void OnEntityDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        var sel = _data.Selection;
        if (sel.Kind != ScenarioSelectionKind.Entities || sel.Entities.Count == 0) return;

        var ids = sel.Entities.ToArray();
        var cmd = DeleteEntities.Create(_data.Entities, ids);
        ExecuteCommand?.Invoke(cmd);
    }

    // ----- Save bar (formerly ScenarioToolbar) -----

    // Pass null to detach. sourcePath shows when editor.SavePath is null (never saved).
    public void BindEditor(ScenarioEditor? editor, string? sourcePath)
    {
        if (_boundEditor is not null)
        {
            _boundEditor.Changed -= RefreshSaveBar;
            _boundEditor.Changed -= RefreshWorldOnEditorChange;
        }
        _boundEditor = editor;
        _boundSourcePath = sourcePath;
        if (_boundEditor is not null)
        {
            _boundEditor.Changed += RefreshSaveBar;
            _boundEditor.Changed += RefreshWorldOnEditorChange;
        }
        RefreshSaveBar();
    }

    void RefreshSaveBar()
    {
        var hasEditor = _boundEditor is not null;
        var dirty = hasEditor && _boundEditor!.IsDirty;
        // Save stays hidden until Save As has chosen a target; prevents accidental
        // overwrite of the source mythscn while browsing game files.
        var hasSavePath = hasEditor && _boundEditor!.SavePath is not null;

        _saveBarRow.IsVisible = hasEditor;
        _dirtyIndicator.IsVisible = dirty;
        _saveBtn.IsVisible = hasSavePath;
        _saveBtn.IsEnabled = hasSavePath && dirty;
        _saveAsBtn.IsEnabled = hasEditor;
        _discardBtn.IsEnabled = dirty;
        _undoBtn.IsEnabled = hasEditor && _boundEditor!.CanUndo;
        _redoBtn.IsEnabled = hasEditor && _boundEditor!.CanRedo;

        if (!hasEditor)
        {
            _savePathLabel.IsVisible = false;
            return;
        }

        var path = _boundEditor!.SavePath;
        if (string.IsNullOrEmpty(path))
        {
            _savePathLabel.Text = "Save location not set";
            ToolTip.SetTip(_savePathLabel, null);
        }
        else
        {
            _savePathLabel.Text = TrimPathStart(path);
            ToolTip.SetTip(_savePathLabel, path);
        }
        _savePathLabel.IsVisible = true;
    }

    // Trim from the START so the filename always shows; "..." marks elided prefix.
    // Avalonia's TextTrimming only trims at the END, so we do this in code.
    static string TrimPathStart(string path, int max = 40)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
        return "..." + path[^(max - 3)..];
    }

    async void ShowInspectorError(string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
        if (owner is null) return;
        var dlg = new Prompt(PromptType.Error, "Error", message);
        await dlg.ShowDialog(owner);
    }

    void OnSaveClick(object? sender, RoutedEventArgs e) => SaveRequested?.Invoke();
    void OnSaveAsClick(object? sender, RoutedEventArgs e) => SaveAsRequested?.Invoke();
    void OnDiscardClick(object? sender, RoutedEventArgs e) => DiscardRequested?.Invoke();
    void OnUndoClick(object? sender, RoutedEventArgs e) => _boundEditor?.Undo();
    void OnRedoClick(object? sender, RoutedEventArgs e) => _boundEditor?.Redo();

    public void SetManualBarStatus(string? path, bool loadFailed)
    {
        if (path is null)
        {
            _manualBarPathText.IsVisible = false;
            return;
        }
        _manualBarPathText.Text = loadFailed
            ? $"Failed to load: {System.IO.Path.GetFileName(path)}"
            : $"Fallback: {System.IO.Path.GetFileName(path)}";
        _manualBarPathText.Foreground = loadFailed
            ? Avalonia.Media.Brushes.Salmon
            : Avalonia.Media.Brushes.Gray;
        _manualBarPathText.IsVisible = true;
    }
}

public sealed class PlayerOption
{
    public required byte PlayerId { get; init; }
    public required string Label { get; init; }
    public required IBrush Brush { get; init; }
}

