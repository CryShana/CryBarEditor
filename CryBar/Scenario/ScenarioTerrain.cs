using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CryBar.Scenario;

public sealed class TerrainTextureGroup
{
    public required string Name { get; init; }
    public required string[] Textures { get; init; }
}

public sealed class ScenarioTerrain
{
    public required int MapSizeX { get; init; }
    public required int MapSizeZ { get; init; }
    public required float[] Heights { get; init; }
    public required float[] WaterHeights { get; init; }
    public required float[] UnkHeights { get; init; }
    public required byte[] TileGroups { get; init; }
    public required ushort[] TileSubs { get; init; }
    public required byte[] TilePt { get; init; }
    // 255 = "no water" sentinel; other values index into WaterNames. Authoritative
    // is-this-tile-water flag (WaterHeights alone bleeds into low-lying terrain).
    public required byte[] WaterType { get; init; }

    // Settable: inspector's terrain picker appends new entries from the game-wide
    // list. Append-only; existing entries are never reindexed (orphans are harmless).
    public required TerrainTextureGroup[] TerrainGroups { get; set; }

    // Round-trip metadata. Defaults emit a minimal empty TN section so synthetic
    // fixtures don't need to set them.
    public byte HasT3 { get; init; } = 1;
    public byte HasTm { get; init; }
    public uint T3Magic { get; init; }
    public uint TerrainGroupsMagic { get; init; } = 1;
    public float UnkFloat0 { get; init; }
    public float UnkFloat1 { get; init; }
    public string TileGroupsMarker { get; init; } = "TT";
    public string TileSubsMarker { get; init; } = "TS";
    public string TilePtMarker { get; init; } = "PT";
    public string WaterTypeMarker { get; init; } = "WT";

    // Opaque sections preserved verbatim for byte-exact round-trip. WaterColors
    // and WaterNames include their marker + u32 size header; TmSection is the
    // body only (emitted only when HasTm != 0). Empty = "do not emit".
    public byte[] WaterColorsSection { get; init; } = [];
    public byte[] WaterNamesSection { get; init; } = [];
    public byte[] T3Tail { get; init; } = [];
    public byte[] TmSection { get; init; } = [];
    public byte[] TnTrail { get; init; } = [];

    public static ScenarioTerrain? TryParse(ScenarioFile scenario)
    {
        if (scenario is null || !scenario.Parsed) return null;

        var j1 = scenario.GetJ1();
        if (j1 is null || !j1.Parsed) return null;

        ScenarioSection? tn = null;
        foreach (var sub in j1.Sections!) if (sub.Marker == "TN") { tn = sub; break; }
        if (tn is null) return null;

        return ParseTn(tn.Data.AsSpan());
    }

    internal static ScenarioTerrain? ParseTn(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;
        int off = 0;
        var hasT3 = data[off++];
        if (hasT3 == 0) return null; // hasT3 must be set

        if (!ScenarioFile.TryReadSized(data, ref off, out var t3)) return null;

        // Outer TN tail: optional hasTm flag + optional TM sub-section + opaque trail.
        byte hasTm = 0;
        byte[] tmSection = [];
        byte[] tnTrail = [];
        if (off < data.Length)
        {
            hasTm = data[off++];
        }
        if (hasTm != 0 && off + 6 <= data.Length)
        {
            if (!ScenarioFile.TryReadSized(data, ref off, out var tm)) return null;

            tmSection = tm.ToArray();
        }
        if (off < data.Length)
            tnTrail = data[off..].ToArray();

        return ParseT3(t3, hasT3, hasTm, tmSection, tnTrail);
    }

    static ScenarioTerrain? ParseT3(ReadOnlySpan<byte> t3, byte hasT3, byte hasTm, byte[] tmSection, byte[] tnTrail)
    {
        int off = 0;
        if (off + 4 > t3.Length) return null;
        var t3Magic = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        off += 4;

        // TT terrain groups sub-section
        if (!ScenarioFile.TryReadSized(t3, ref off, out var ttBody)) return null;
        var ttMagic = ttBody.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(ttBody) : 1u;
        var groups = ParseTerrainGroups(ttBody);

        // The two map-size u32s are stored as (gameZ, gameX) -- the file's first
        // dimension is the game's Z (north-south) axis and the second is X. Loading
        // them in the natural order with that labeling means the per-vertex/per-tile
        // arrays that follow are already in the renderer's expected
        // [vz_outer * (mapX+1) + vx_inner] layout, no transpose needed.
        if (off + 8 > t3.Length) return null;
        var mapZ = (int)BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        var mapX = (int)BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 4));
        off += 8;
        if (mapZ < 0 || mapX < 0) return null;

        if (off + 8 > t3.Length) return null;
        var unkF0 = BitConverter.ToSingle(t3.Slice(off, 4));
        var unkF1 = BitConverter.ToSingle(t3.Slice(off + 4, 4));
        off += 8;

        if (!TryReadList<byte>(t3, ref off, out var tileGroupsMarker, out var tileGroups)) return null;
        if (!TryReadList<ushort>(t3, ref off, out var tileSubsMarker, out var tileSubs)) return null;
        if (!TryReadList<byte>(t3, ref off, out var tilePtMarker, out var tilePt)) return null;

        if (!TryReadFullSizeSection(t3, ref off, out var waterColorsSection)) return null;
        if (!TryReadFullSizeSection(t3, ref off, out var waterNamesSection)) return null;

        if (!TryReadList<byte>(t3, ref off, out var waterTypeMarker, out var waterType)) return null;

        if (off + 4 > t3.Length) return null;
        var heightCount = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        off += 4;
        if ((long)heightCount * 3 * sizeof(float) > t3.Length - off) return null;

        var heights = ReadFloats(t3, ref off, (int)heightCount);
        var waterHeights = ReadFloats(t3, ref off, (int)heightCount);
        var unkHeights = ReadFloats(t3, ref off, (int)heightCount);

        var t3Tail = off < t3.Length ? t3.Slice(off).ToArray() : [];

        return new ScenarioTerrain
        {
            MapSizeX = mapX,
            MapSizeZ = mapZ,
            Heights = heights,
            WaterHeights = waterHeights,
            UnkHeights = unkHeights,
            TileGroups = tileGroups,
            TileSubs = tileSubs,
            TilePt = tilePt,
            WaterType = waterType,
            TerrainGroups = groups,
            HasT3 = hasT3,
            HasTm = hasTm,
            T3Magic = t3Magic,
            TerrainGroupsMagic = ttMagic,
            UnkFloat0 = unkF0,
            UnkFloat1 = unkF1,
            TileGroupsMarker = tileGroupsMarker,
            TileSubsMarker = tileSubsMarker,
            TilePtMarker = tilePtMarker,
            WaterTypeMarker = waterTypeMarker,
            WaterColorsSection = waterColorsSection,
            WaterNamesSection = waterNamesSection,
            T3Tail = t3Tail,
            TmSection = tmSection,
            TnTrail = tnTrail,
        };
    }

    static TerrainTextureGroup[] ParseTerrainGroups(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return [];
        int off = 4; // skip ttMagic
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off));
        off += 4;

        var result = new List<TerrainTextureGroup>();
        for (uint g = 0; g < count; g++)
        {
            if (!ScenarioFile.TryReadUTF16(data, off, out var name, out off))
                break;
            if (off + 4 > data.Length)
                break;
            var texCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off));
            off += 4;

            var textures = new List<string>();
            for (uint t = 0; t < texCount; t++)
            {
                if (!ScenarioFile.TryReadUTF16(data, off, out var tex, out off)) break;
                textures.Add(tex);
            }

            result.Add(new TerrainTextureGroup { Name = name, Textures = textures.ToArray() });
        }
        return result.ToArray();
    }

    static unsafe bool TryReadList<T>(ReadOnlySpan<byte> data, ref int off, out string marker, out T[] result) where T : unmanaged
    {
        marker = "";
        result = [];
        int start = off;
        if (!ScenarioFile.TryReadSized(data, ref off, out var body) || body.Length < 4) return false;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if ((long)count * sizeof(T) > body.Length - 4) return false;

        marker = ScenarioFile.ReadMarker(data, start);
        result = new T[count];
        MemoryMarshal.Cast<byte, T>(body.Slice(4, (int)count * sizeof(T))).CopyTo(result);
        return true;
    }

    /// <summary>
    /// Reads a sub-section verbatim including its 2-byte marker and u32 size header.
    /// Returns the full sub-section bytes (marker + size + body) and advances the offset.
    /// Used to round-trip cosmetic sub-sections we don't model semantically.
    /// </summary>
    static bool TryReadFullSizeSection(ReadOnlySpan<byte> data, ref int off, out byte[] bytes)
    {
        bytes = [];
        int start = off;
        if (!ScenarioFile.TryReadSized(data, ref off, out _)) return false;

        bytes = data[start..off].ToArray();
        return true;
    }

    static float[] ReadFloats(ReadOnlySpan<byte> data, ref int off, int count)
    {
        var result = new float[count];
        MemoryMarshal.Cast<byte, float>(data.Slice(off, count * sizeof(float))).CopyTo(result);
        off += count * sizeof(float);
        return result;
    }

}
