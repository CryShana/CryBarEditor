using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using CryBar.Scenario.Writers;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
    static void WriteTnXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (!IsTnWellFormed(data)) { WriteSectionXml(writer, section); return; }

        writer.WriteStartElement("Terrain");
        int off = 0;

        byte hasT3 = data[off++];
        writer.WriteAttributeString("hasT3", hasT3.ToString());

        if (hasT3 != 0 && TryReadSized(data, ref off, out var t3))
        {
            // Write T3 magic as attribute on TN (must come before child elements)
            if (t3.Length >= 4)
                writer.WriteAttributeString("t3Magic", BinaryPrimitives.ReadUInt32LittleEndian(t3).ToString());
            byte hasTm2 = off < data.Length ? data[off] : (byte)0;
            writer.WriteAttributeString("hasTm", hasTm2.ToString());
            WriteTnT3Xml(writer, t3);
        }

        byte hasTm = 0;
        if (off < data.Length)
        {
            hasTm = data[off++];
            // hasTm already written above as attribute
        }

        if (hasTm != 0 && TryReadSized(data, ref off, out var tmData))
        {
            writer.WriteStartElement("TnTM");
            // Decode lighting preset: unk1(4) + unk2(4) + String16(preset)
            if (tmData.Length >= 12)
            {
                int tmOff = 8; // skip unk1, unk2
                var charCount = BinaryPrimitives.ReadInt32LittleEndian(tmData.Slice(tmOff));
                if (charCount > 0 && charCount < 1000 && tmOff + 4 + charCount * 2 <= tmData.Length)
                {
                    var preset = Encoding.Unicode.GetString(tmData.Slice(tmOff + 4, charCount * 2));
                    writer.WriteAttributeString("lightingPreset", preset);
                }
            }
            writer.WriteString(Convert.ToBase64String(tmData));
            writer.WriteEndElement();
        }

        if (off < data.Length)
        {
            writer.WriteStartElement("TnTrail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    // Malformed TN falls back to a raw section instead of throwing mid-XML.
    static bool IsTnWellFormed(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        if (data[0] != 0) return ScenarioTerrain.ParseTn(data) is not null;

        int off = 2;
        return data[1] == 0 || off + 6 > data.Length || TryReadSized(data, ref off, out _);
    }

    static void WriteTnT3Xml(XmlWriter writer, ReadOnlySpan<byte> t3)
    {
        int off = 0;
        if (off + 4 > t3.Length) return;
        // t3Magic already written as attribute on parent <TN>
        off += 4;

        // TT terrain groups sub-section
        if (!TryReadSized(t3, ref off, out var ttGroups)) return;
        WriteTnTerrainGroupsXml(writer, ttGroups);

        // File stores (gameZ, gameX); XML attributes use game-axis names so x/z
        // line up with ScenarioTerrain.MapSizeX / MapSizeZ downstream.
        if (off + 8 > t3.Length) return;
        var mapSizeZ = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        var mapSizeX = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 4));
        off += 8;
        writer.WriteStartElement("MapSize");
        writer.WriteAttributeString("x", mapSizeX.ToString());
        writer.WriteAttributeString("z", mapSizeZ.ToString());
        writer.WriteEndElement();

        // 2 unknown floats
        if (off + 8 > t3.Length) return;
        writer.WriteStartElement("UnkFloats");
        writer.WriteAttributeString("f0", FormatFloat(BitConverter.ToSingle(t3.Slice(off, 4))));
        writer.WriteAttributeString("f1", FormatFloat(BitConverter.ToSingle(t3.Slice(off + 4, 4))));
        writer.WriteEndElement();
        off += 8;

        if (off + 6 > t3.Length) return;
        writer.WriteComment("TileGroups (base64: u32 count + u8[])");
        off += WriteSizeListXml(writer, t3, off);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TileSubs (base64: u32 count + u16le[])");
        off += WriteSizeListXml(writer, t3, off);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TilePT (base64: u32 count + u8[])");
        off += WriteSizeListXml(writer, t3, off);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("WaterColors (base64: u32 count + u16le[])");
        off += WriteSizeListXml(writer, t3, off);

        // WI water names: [marker WI][u32 size][MagicU32<0>, SizeList<String16>]
        if (off + 6 > t3.Length) return;
        {
            var wiMarker = ReadMarker(t3, off);
            if (!TryReadSized(t3, ref off, out var wiData)) return;

            writer.WriteComment("WaterNames");
            writer.WriteStartElement(wiMarker);
            if (wiData.Length >= 8)
            {
                var wiMagic = BinaryPrimitives.ReadUInt32LittleEndian(wiData);
                writer.WriteAttributeString("magic", wiMagic.ToString());
                var nameCount = BinaryPrimitives.ReadUInt32LittleEndian(wiData.Slice(4));
                int wiOff = 8;
                for (uint i = 0; i < nameCount; i++)
                {
                    if (!TryReadUTF16(wiData, wiOff, out var name, out wiOff)) break;
                    writer.WriteStartElement("Water");
                    writer.WriteString(name);
                    writer.WriteEndElement();
                }
            }
            writer.WriteEndElement();
        }

        // WT water type
        if (off + 6 > t3.Length) return;
        writer.WriteComment("WaterType (base64: u32 count + u8[])");
        off += WriteSizeListXml(writer, t3, off);

        // Height arrays
        if (off + 4 > t3.Length) return;
        var heightCount = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        off += 4;

        writer.WriteComment($"Heights (base64: {heightCount} x float32le)");
        writer.WriteStartElement("Heights");
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        writer.WriteComment($"WaterHeights (base64: {heightCount} x float32le)");
        writer.WriteStartElement("WaterHeights");
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        writer.WriteComment($"UnkHeights (base64: {heightCount} x float32le)");
        writer.WriteStartElement("UnkHeights");
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        // Remaining opaque data (CM, UM, EmbeddedImage)
        if (off < t3.Length)
        {
            var tail = t3[off..];
            writer.WriteStartElement("T3Tail");
            // Scan for embedded minimap image: magic=1(4) + width(4) + height(4) + magic=6(4) + pixelCount(4)
            for (int i = 0; i < tail.Length - 20; i++)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(i)) != 1) continue;
                if (BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(i + 12)) != 6) continue;
                var w = BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(i + 4));
                var h = BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(i + 8));
                var pxBytes = BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(i + 16));
                if (w > 0 && w <= 2048 && h > 0 && h <= 2048 && pxBytes == w * h * 4)
                {
                    writer.WriteAttributeString("minimapWidth", w.ToString());
                    writer.WriteAttributeString("minimapHeight", h.ToString());
                    break;
                }
            }
            writer.WriteString(Convert.ToBase64String(tail));
            writer.WriteEndElement();
        }
    }

    static void WriteTnTerrainGroupsXml(XmlWriter writer, ReadOnlySpan<byte> ttData)
    {
        writer.WriteStartElement("TerrainGroups");
        if (ttData.Length < 8) { writer.WriteEndElement(); return; }

        var ttMagic = BinaryPrimitives.ReadUInt32LittleEndian(ttData);
        writer.WriteAttributeString("magic", ttMagic.ToString());
        var groupCount = BinaryPrimitives.ReadUInt32LittleEndian(ttData.Slice(4));
        int gOff = 8;

        for (uint g = 0; g < groupCount; g++)
        {
            if (!TryReadUTF16(ttData, gOff, out var groupName, out gOff)) break;
            writer.WriteStartElement("Group");
            writer.WriteAttributeString("name", groupName);
            if (gOff + 4 > ttData.Length) { writer.WriteEndElement(); break; }
            var texCount = BinaryPrimitives.ReadUInt32LittleEndian(ttData.Slice(gOff));
            gOff += 4;
            for (uint t = 0; t < texCount; t++)
            {
                if (!TryReadUTF16(ttData, gOff, out var texName, out gOff)) break;
                writer.WriteStartElement("Tex");
                writer.WriteString(texName);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    static int WriteSizeListXml(XmlWriter writer, ReadOnlySpan<byte> data, int off)
    {
        var marker = ReadMarker(data, off);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
        var inner = data.Slice(off + 6, (int)size);
        writer.WriteStartElement(marker);
        if (inner.Length > 0)
            writer.WriteString(Convert.ToBase64String(inner));
        writer.WriteEndElement();
        return 6 + (int)size;
    }

    static int WriteFloatArrayXml(XmlWriter writer, ReadOnlySpan<byte> data, int off, uint count)
    {
        var byteCount = (int)Math.Min((long)count * 4, Math.Max(0, data.Length - off));
        if (byteCount > 0)
            writer.WriteString(Convert.ToBase64String(data.Slice(off, byteCount)));
        return byteCount;
    }

    static ScenarioSection ReadTnXml(XmlReader reader)
    {
        var hasT3Attr = reader.GetAttribute("hasT3");
        if (string.IsNullOrEmpty(hasT3Attr))
        {
            reader.Skip();
            return new ScenarioSection("TN", []);
        }

        var t3MagicAttr = reader.GetAttribute("t3Magic");
        var hasTmAttr = reader.GetAttribute("hasTm");

        byte hasT3 = byte.Parse(hasT3Attr);
        byte hasTm = string.IsNullOrEmpty(hasTmAttr) ? (byte)0 : byte.Parse(hasTmAttr);
        uint t3Magic = string.IsNullOrEmpty(t3MagicAttr) ? 0u : uint.Parse(t3MagicAttr);

        // Defaults populated from XML; any element absent leaves the default.
        TerrainTextureGroup[] terrainGroups = [];
        uint terrainGroupsMagic = 1u;
        int mapX = 0, mapZ = 0;
        float unkF0 = 0f, unkF1 = 0f;
        byte[] tileGroups = [];
        ushort[] tileSubs = [];
        byte[] tilePt = [];
        byte[] waterType = [];
        string tileGroupsMarker = "TT";
        string tileSubsMarker = "TS";
        string tilePtMarker = "PT";
        string waterTypeMarker = "WT";
        byte[] waterColorsSection = [];
        byte[] waterNamesSection = [];
        byte[] t3Tail = [];
        byte[] tmSection = [];
        byte[] tnTrail = [];
        // Heights count is shared. We track each array separately and later resize so
        // all three have matching lengths -- the writer emits a single u32 count.
        float[] heights = [];
        float[] waterHeights = [];
        float[] unkHeights = [];

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "TerrainGroups":
                        (terrainGroupsMagic, terrainGroups) = ReadTerrainGroupsXml(reader);
                        break;
                    case "MapSize":
                        // File order is (gameZ, gameX); XML uses game-axis names.
                        mapZ = int.Parse(reader.GetAttribute("z") ?? "0");
                        mapX = int.Parse(reader.GetAttribute("x") ?? "0");
                        reader.Skip();
                        break;
                    case "UnkFloats":
                        unkF0 = float.Parse(reader.GetAttribute("f0") ?? "0");
                        unkF1 = float.Parse(reader.GetAttribute("f1") ?? "0");
                        reader.Skip();
                        break;
                    case "TT" or "PT" or "WT":
                    {
                        var marker = reader.Name;
                        var bytes = ReadSizeListBytes(reader, elemSize: 1);
                        if (marker == "TT") { tileGroupsMarker = marker; tileGroups = bytes; }
                        else if (marker == "PT") { tilePtMarker = marker; tilePt = bytes; }
                        else { waterTypeMarker = marker; waterType = bytes; }
                        break;
                    }
                    case "TS" or "PS":
                    {
                        var marker = reader.Name;
                        if (marker == "TS")
                        {
                            tileSubsMarker = marker;
                            tileSubs = ReadSizeListUshorts(reader);
                        }
                        else
                        {
                            // PS = WaterColors (cosmetic). Capture the raw section bytes
                            // (marker + size header + body) so it round-trips opaquely.
                            waterColorsSection = ReadFullSizeListSectionBytes(reader, elemSize: 2);
                        }
                        break;
                    }
                    case "WI":
                        waterNamesSection = ReadWaterNamesSectionBytes(reader);
                        break;
                    case "Heights":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        heights = ReadFloatArrayFromXml(reader.ReadElementContentAsString().Trim());
                        break;
                    }
                    case "WaterHeights":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        waterHeights = ReadFloatArrayFromXml(reader.ReadElementContentAsString().Trim());
                        break;
                    }
                    case "UnkHeights":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        unkHeights = ReadFloatArrayFromXml(reader.ReadElementContentAsString().Trim());
                        break;
                    }
                    case "T3Tail":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) t3Tail = Convert.FromBase64String(text);
                        break;
                    }
                    case "TnTM":
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        tmSection = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    case "TnTrail":
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        tnTrail = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        // The writer emits a single u32 height count. Align the three arrays to the max
        // observed length (trailing zeros) so byte-equivalent output is produced regardless
        // of which arrays the XML happened to populate.
        var heightCount = Math.Max(heights.Length, Math.Max(waterHeights.Length, unkHeights.Length));
        if (heights.Length != heightCount) Array.Resize(ref heights, heightCount);
        if (waterHeights.Length != heightCount) Array.Resize(ref waterHeights, heightCount);
        if (unkHeights.Length != heightCount) Array.Resize(ref unkHeights, heightCount);

        var terrain = new ScenarioTerrain
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
            TerrainGroups = terrainGroups,
            HasT3 = hasT3,
            HasTm = hasTm,
            T3Magic = t3Magic,
            TerrainGroupsMagic = terrainGroupsMagic,
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

        return new ScenarioSection("TN", TnWriter.Write(terrain));
    }

    static (uint magic, TerrainTextureGroup[] groups) ReadTerrainGroupsXml(XmlReader reader)
    {
        var magicAttr = reader.GetAttribute("magic");
        var magic = string.IsNullOrEmpty(magicAttr) ? 1u : uint.Parse(magicAttr);

        var groups = new List<TerrainTextureGroup>();
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Group")
                {
                    var name = reader.GetAttribute("name") ?? "";
                    var textures = new List<string>();
                    if (!reader.IsEmptyElement)
                    {
                        reader.Read();
                        while (reader.NodeType != XmlNodeType.EndElement)
                        {
                            if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                            if (reader.Name == "Tex")
                                textures.Add(reader.ReadElementContentAsString());
                            else
                                reader.Skip();
                        }
                        reader.ReadEndElement();
                    }
                    else reader.Read();
                    groups.Add(new TerrainTextureGroup { Name = name, Textures = textures.ToArray() });
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        return (magic, groups.ToArray());
    }

    /// <summary>
    /// Reads a size-list element's payload as a byte array. Accepts both base64 (new
    /// format: raw inner bytes containing u32 count + u8[]) and legacy CSV. The
    /// returned array is the elements only (no count prefix).
    /// </summary>
    static byte[] ReadSizeListBytes(XmlReader reader, int elemSize)
    {
        if (reader.IsEmptyElement) { reader.Read(); return []; }
        var text = reader.ReadElementContentAsString().Trim();
        if (text.Length == 0) return [];

        if (IsBase64Content(text))
        {
            var bytes = Convert.FromBase64String(text);
            // Strip the leading u32 count, return element bytes only.
            if (bytes.Length < 4) return [];
            return bytes[4..];
        }

        // Legacy CSV format.
        var parts = text.Split(',');
        var result = new byte[parts.Length * elemSize];
        if (elemSize == 1)
        {
            for (int i = 0; i < parts.Length; i++)
                result[i] = byte.Parse(parts[i].Trim());
        }
        else
        {
            var span = result.AsSpan();
            for (int i = 0; i < parts.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2), ushort.Parse(parts[i].Trim()));
        }
        return result;
    }

    static ushort[] ReadSizeListUshorts(XmlReader reader)
    {
        var bytes = ReadSizeListBytes(reader, elemSize: 2);
        if (bytes.Length == 0) return [];
        var result = new ushort[bytes.Length / 2];
        MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(0, result.Length * 2)).CopyTo(result);
        return result;
    }

    /// <summary>
    /// Reads a size-list XML element and returns the full sub-section bytes
    /// (marker[2] + u32 size + body). Used for cosmetic sub-sections we preserve
    /// opaquely (e.g. WaterColors "PS").
    /// </summary>
    static byte[] ReadFullSizeListSectionBytes(XmlReader reader, int elemSize)
    {
        var marker = reader.Name;

        if (reader.IsEmptyElement)
        {
            reader.Read();
            // Empty list: section body is just a u32 count of 0.
            var empty = new byte[10];
            empty[0] = (byte)marker[0]; empty[1] = (byte)marker[1];
            BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(2, 4), 4u);
            // u32 count at [6..10] is already zero-initialized.
            return empty;
        }

        var text = reader.ReadElementContentAsString().Trim();
        if (text.Length == 0)
        {
            var empty = new byte[10];
            empty[0] = (byte)marker[0]; empty[1] = (byte)marker[1];
            BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(2, 4), 4u);
            return empty;
        }

        byte[] body;
        if (IsBase64Content(text))
        {
            // New format: base64 of raw inner bytes (count + elements).
            body = Convert.FromBase64String(text);
        }
        else
        {
            // Legacy CSV: rebuild count + elements blob.
            var parts = text.Split(',');
            body = new byte[4 + parts.Length * elemSize];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), (uint)parts.Length);
            if (elemSize == 1)
            {
                for (int i = 0; i < parts.Length; i++)
                    body[4 + i] = byte.Parse(parts[i].Trim());
            }
            else
            {
                var span = body.AsSpan(4);
                for (int i = 0; i < parts.Length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2), ushort.Parse(parts[i].Trim()));
            }
        }

        var section = new byte[6 + body.Length];
        section[0] = (byte)marker[0];
        section[1] = (byte)marker[1];
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(2, 4), (uint)body.Length);
        Buffer.BlockCopy(body, 0, section, 6, body.Length);
        return section;
    }

    /// <summary>
    /// Reads a WI-style XML element (magic + per-name UTF-16 entries) and returns the
    /// full sub-section bytes (marker[2] + u32 size + body). The body is u32 magic +
    /// u32 nameCount + nameCount * String16.
    /// </summary>
    static byte[] ReadWaterNamesSectionBytes(XmlReader reader)
    {
        var marker = reader.Name;
        var magicAttr = reader.GetAttribute("magic");
        var magic = string.IsNullOrEmpty(magicAttr) ? 0u : uint.Parse(magicAttr);

        using var innerMs = new MemoryStream();
        using var innerBw = new BinaryWriter(innerMs);
        innerBw.Write(magic);

        var names = new List<string>();
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Water")
                    names.Add(reader.ReadElementContentAsString());
                else
                    reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        innerBw.Write((uint)names.Count);
        foreach (var name in names)
            WriteString16(innerBw, name);

        var body = innerMs.ToArray();
        var section = new byte[6 + body.Length];
        section[0] = (byte)marker[0];
        section[1] = (byte)marker[1];
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(2, 4), (uint)body.Length);
        Buffer.BlockCopy(body, 0, section, 6, body.Length);
        return section;
    }

    /// <summary>
    /// Decodes a Heights/WaterHeights/UnkHeights XML payload into a float array.
    /// Accepts both base64 (raw float bytes; or, for Heights only, a u32 count
    /// followed by raw floats) and legacy space-separated text.
    /// </summary>
    static float[] ReadFloatArrayFromXml(string text)
    {
        if (text.Length == 0) return [];

        if (IsBase64Content(text))
        {
            var bytes = Convert.FromBase64String(text);
            // Heights section originally encoded a u32 count followed by floats; the
            // count length (4 bytes) is included in the base64. WaterHeights/UnkHeights
            // contain only floats with no count prefix. Distinguish by length: if the
            // byte length is divisible by 4 and has an apparent count of (n-1)/1 floats
            // matching, treat as count-prefixed; otherwise treat as raw floats.
            // Simplest heuristic: if (bytes.Length - 4) >= 0 and divisible by 4 AND the
            // u32 at offset 0 equals (bytes.Length - 4) / 4, then count-prefixed.
            if (bytes.Length >= 4 && bytes.Length % 4 == 0)
            {
                var maybeCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                if ((long)maybeCount * 4 == bytes.Length - 4)
                {
                    var floats = new float[maybeCount];
                    if (maybeCount > 0)
                        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(4)).Slice(0, (int)maybeCount).CopyTo(floats);
                    return floats;
                }
                var raw = new float[bytes.Length / 4];
                MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).CopyTo(raw);
                return raw;
            }
            return [];
        }

        // Legacy space-separated floats.
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = float.Parse(parts[i]);
        return result;
    }
}
