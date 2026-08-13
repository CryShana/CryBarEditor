using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CryBar.Scenario;
using CryBar.Scenario.Editor;

namespace CryBarEditor.Classes;

public enum TextureSource : byte
{
    Placeholder = 0,
    Index = 1,
    ManualBar = 2,
}

public sealed class ScenarioPreviewData : IDisposable
{
    public required ScenarioFile Scenario { get; init; }
    public required ScenarioTerrain Terrain { get; init; }
    public required ScenarioTextureSet TextureSet { get; init; }
    // Mutable so the renderer can rebuild from live terrain on command hints.
    public required TerrainMesh TerrainMesh { get; set; }
    public WaterMesh? WaterMesh { get; set; }
    public required List<ScenarioEntity> Entities { get; init; }

    bool[] _sliceReady = [];
    public IReadOnlyList<bool> SliceReady => _sliceReady;
    internal void MarkSliceReady(int index)
    {
        if ((uint)index < (uint)_sliceReady.Length)
            _sliceReady[index] = true;
    }

    TextureSource[] _textureSources = [];
    public IReadOnlyList<TextureSource> TextureSources => _textureSources;
    internal void SetTextureSource(int sliceIndex, TextureSource source)
    {
        if ((uint)sliceIndex < (uint)_textureSources.Length)
            _textureSources[sliceIndex] = source;
    }

    // Grow per-slice tracking arrays after EnsureSlot extends TextureSet.Names.
    public void EnsureSlotCapacity(int capacity)
    {
        if (capacity <= _sliceReady.Length) return;
        var newReady = new bool[capacity];
        Array.Copy(_sliceReady, newReady, _sliceReady.Length);
        _sliceReady = newReady;

        var newSources = new TextureSource[capacity];
        Array.Copy(_textureSources, newSources, _textureSources.Length);
        _textureSources = newSources;
    }

    public CancellationTokenSource Cancellation { get; } = new();
    public ScenarioSelection Selection { get; } = new();

    // Editor and renderer share the SAME List<ScenarioEntity> -- command mutations
    // are visible to both via in-place edits.
    public ScenarioEditor Editor { get; private set; } = null!;

    // Null when the PL section is missing or has an unexpected layout;
    // the World inspector section hides itself in that case.
    public ScenarioPlayersView? Players { get; private set; }

    // Lazy game-wide proto names (proto.xml.XMB). Null until first picker open;
    // picker falls back to ProtoTable when null.
    public List<string>? ProtoNamesCache { get; set; }

    // Lazy major god names in major_gods.xml civ order (god id is 1-based index).
    // Null until resolved; UI falls back to raw numbers. Unavailable is set on a
    // failed resolve so every World rebuild doesn't re-probe Data.bar.
    public List<string>? MajorGodNamesCache { get; set; }
    public bool MajorGodNamesUnavailable { get; set; }

    // Lazy game-wide terrain (group, texture) list. Null until first picker open;
    // picker falls back to a synthetic cache built from the scenario's TerrainGroups.
    public TerrainTypesCache? TerrainTypesCache { get; set; }

    // Editable scenario TM table (proto names indexed by entity ProtoIndex).
    // SetEntityProtos appends; FlushParsedViews writes back on save.
    public List<string> ProtoTable { get; } = new();

    Dictionary<uint, int>? _entityIdToIndex;
    // Lazy id -> Entities[] index lookup. Invalidate via InvalidateEntityIndex()
    // whenever the Entities list is mutated (DeleteEntities, future AddEntities).
    public IReadOnlyDictionary<uint, int> EntityIdToIndex
    {
        get
        {
            if (_entityIdToIndex is null)
            {
                var d = new Dictionary<uint, int>(Entities.Count);
                for (int i = 0; i < Entities.Count; i++)
                    d[Entities[i].EntityId] = i;
                _entityIdToIndex = d;
            }
            return _entityIdToIndex;
        }
    }

    public void InvalidateEntityIndex() => _entityIdToIndex = null;

    public static ScenarioPreviewData? TryBuild(ScenarioFile scenario)
    {
        if (scenario is null || !scenario.Parsed) return null;

        var terrain = ScenarioTerrain.TryParse(scenario);
        if (terrain is null) return null;

        int expectedVertices = (terrain.MapSizeX + 1) * (terrain.MapSizeZ + 1);
        int expectedTiles = terrain.MapSizeX * terrain.MapSizeZ;
        if (terrain.MapSizeX <= 0 || terrain.MapSizeX > 1024) return null;
        if (terrain.MapSizeZ <= 0 || terrain.MapSizeZ > 1024) return null;
        if (terrain.Heights.Length != expectedVertices) return null;
        if (terrain.TileGroups.Length != expectedTiles) return null;
        if (terrain.TileSubs.Length != expectedTiles) return null;

        var textureSet = ScenarioTextureSet.Build(terrain);
        var mesh = TerrainMeshBuilder.Build(terrain, textureSet);
        var water = WaterMeshBuilder.Build(terrain);
        var entities = ScenarioEntityListBuilder.Build(scenario);
        var entitiesList = entities.ToList();

        var data = new ScenarioPreviewData
        {
            Scenario = scenario,
            Terrain = terrain,
            TextureSet = textureSet,
            TerrainMesh = mesh,
            WaterMesh = water,
            Entities = entitiesList,
        };
        data._sliceReady = new bool[textureSet.Names.Count];
        data._textureSources = new TextureSource[textureSet.Names.Count];

        var j1 = scenario.GetJ1();
        if (j1 is not null && j1.Parsed)
        {
            foreach (var sub in j1.Sections!)
            {
                if (sub.Marker == "TM" || sub.Marker == "PT")
                {
                    data.ProtoTable.AddRange(ScenarioFile.ReadTmStrings(sub.Data));
                    break;
                }
            }
        }

        data.Players = scenario.ParsePlayersView();
        data.Editor = new ScenarioEditor(scenario, terrain, data.Entities);
        return data;
    }

    public void Dispose()
    {
        Cancellation.Cancel();
        Cancellation.Dispose();
    }
}
