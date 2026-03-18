using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar;

public partial class ScenarioFile
{
    static void WriteTnXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 2) { WriteSectionXml(writer, section); return; }

        writer.WriteStartElement("Terrain");
        int off = 0;

        byte hasT3 = data[off++];
        writer.WriteAttributeString("hasT3", hasT3.ToString());

        if (hasT3 != 0 && off + 6 <= data.Length)
        {
            var t3Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
            off += 6;
            if (off + (int)t3Size <= data.Length)
            {
                var t3 = data.Slice(off, (int)t3Size);
                // Write T3 magic as attribute on TN (must come before child elements)
                if (t3.Length >= 4)
                    writer.WriteAttributeString("t3Magic", BinaryPrimitives.ReadUInt32LittleEndian(t3).ToString());
                byte hasTm2 = (off + (int)t3Size < data.Length) ? data[off + (int)t3Size] : (byte)0;
                writer.WriteAttributeString("hasTm", hasTm2.ToString());
                WriteTnT3Xml(writer, t3);
                off += (int)t3Size;
            }
        }

        byte hasTm = 0;
        if (off < data.Length)
        {
            hasTm = data[off++];
            // hasTm already written above as attribute
        }

        if (hasTm != 0 && off + 6 <= data.Length)
        {
            var tmSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
            off += 6;
            if (off + (int)tmSize <= data.Length)
            {
                writer.WriteStartElement("TnTM");
                writer.WriteString(Convert.ToBase64String(data.Slice(off, (int)tmSize)));
                writer.WriteEndElement();
                off += (int)tmSize;
            }
        }

        if (off < data.Length)
        {
            writer.WriteStartElement("TnTrail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WriteTnT3Xml(XmlWriter writer, ReadOnlySpan<byte> t3)
    {
        int off = 0;
        if (off + 4 > t3.Length) return;
        // t3Magic already written as attribute on parent <TN>
        off += 4;

        // TT terrain groups sub-section
        if (off + 6 > t3.Length) return;
        var ttGroupSize = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 2));
        off += 6;
        if (off + (int)ttGroupSize <= t3.Length)
        {
            WriteTnTerrainGroupsXml(writer, t3.Slice(off, (int)ttGroupSize));
            off += (int)ttGroupSize;
        }

        // map_size_x, map_size_z
        if (off + 8 > t3.Length) return;
        var mapSizeX = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        var mapSizeZ = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 4));
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
        off += WriteSizeListXml(writer, t3, off, 1);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TileSubs (base64: u32 count + u16le[])");
        off += WriteSizeListXml(writer, t3, off, 2);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TilePT (base64: u32 count + u8[])");
        off += WriteSizeListXml(writer, t3, off, 1);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("WaterColors (base64: u32 count + u16le[])");
        off += WriteSizeListXml(writer, t3, off, 2);

        // WI water names: [marker WI][u32 size][MagicU32<0>, SizeList<String16>]
        if (off + 6 > t3.Length) return;
        {
            var wiMarker = ReadMarker(t3, off);
            var wiSize = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 2));
            var wiData = t3.Slice(off + 6, (int)wiSize);
            off += 6 + (int)wiSize;

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
        off += WriteSizeListXml(writer, t3, off, 1);

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
            writer.WriteStartElement("T3Tail");
            writer.WriteString(Convert.ToBase64String(t3[off..]));
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

    static int WriteSizeListXml(XmlWriter writer, ReadOnlySpan<byte> data, int off, int elemSize)
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
        var byteCount = (int)Math.Min(count * 4, (uint)Math.Max(0, data.Length - off));
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

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte hasT3 = byte.Parse(hasT3Attr);
        bw.Write(hasT3);

        MemoryStream? t3Ms = hasT3 != 0 ? new MemoryStream() : null;
        BinaryWriter? t3Bw = t3Ms != null ? new BinaryWriter(t3Ms) : null;

        if (t3Bw != null)
            t3Bw.Write(string.IsNullOrEmpty(t3MagicAttr) ? 0u : uint.Parse(t3MagicAttr));

        byte[]? tnTmData = null;
        byte[]? tnTrailData = null;

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "TerrainGroups":
                        ReadTerrainGroupsTT(reader, t3Bw!);
                        break;
                    case "MapSize":
                        if (t3Bw != null)
                        {
                            t3Bw.Write(uint.Parse(reader.GetAttribute("x") ?? "0"));
                            t3Bw.Write(uint.Parse(reader.GetAttribute("z") ?? "0"));
                        }
                        reader.Skip();
                        break;
                    case "UnkFloats":
                        if (t3Bw != null)
                        {
                            t3Bw.Write(float.Parse(reader.GetAttribute("f0") ?? "0"));
                            t3Bw.Write(float.Parse(reader.GetAttribute("f1") ?? "0"));
                        }
                        reader.Skip();
                        break;
                    case "TT" or "PT" or "WT":
                        ReadSizeListSection(reader, t3Bw!, 1);
                        break;
                    case "TS" or "PS":
                        ReadSizeListSection(reader, t3Bw!, 2);
                        break;
                    case "WI":
                        ReadWaterNames(reader, t3Bw!);
                        break;
                    case "Heights":
                    {
                        if (reader.IsEmptyElement) { if (t3Bw != null) t3Bw.Write(0u); reader.Read(); break; }
                        var text = reader.ReadElementContentAsString().Trim();
                        if (t3Bw != null)
                        {
                            if (text.Length == 0)
                            {
                                t3Bw.Write(0u);
                            }
                            else if (text.Contains(' '))
                            {
                                // Old format: space-separated floats
                                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                t3Bw.Write((uint)parts.Length);
                                WriteFloatArray(t3Bw, parts);
                            }
                            else
                            {
                                // New format: base64 of raw float bytes
                                var bytes = Convert.FromBase64String(text);
                                t3Bw.Write((uint)(bytes.Length / 4));
                                t3Bw.Write(bytes);
                            }
                        }
                        break;
                    }
                    case "WaterHeights" or "UnkHeights":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString().Trim();
                        if (t3Bw != null && text.Length > 0)
                        {
                            if (text.Contains(' '))
                            {
                                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                WriteFloatArray(t3Bw, parts);
                            }
                            else
                            {
                                t3Bw.Write(Convert.FromBase64String(text));
                            }
                        }
                        break;
                    }
                    case "T3Tail":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString().Trim();
                        if (t3Bw != null && text.Length > 0)
                            t3Bw.Write(Convert.FromBase64String(text));
                        break;
                    }
                    case "TnTM":
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        tnTmData = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    case "TnTrail":
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        tnTrailData = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        if (t3Ms != null)
        {
            var t3Data = t3Ms.ToArray();
            bw.Write((byte)'T'); bw.Write((byte)'3');
            bw.Write((uint)t3Data.Length);
            bw.Write(t3Data);
            t3Bw!.Dispose();
            t3Ms.Dispose();
        }

        byte hasTm = string.IsNullOrEmpty(hasTmAttr) ? (byte)0 : byte.Parse(hasTmAttr);
        bw.Write(hasTm);

        if (hasTm != 0 && tnTmData != null)
        {
            bw.Write((byte)'T'); bw.Write((byte)'M');
            bw.Write((uint)tnTmData.Length);
            bw.Write(tnTmData);
        }

        if (tnTrailData != null)
            bw.Write(tnTrailData);

        return new ScenarioSection("TN", ms.ToArray());
    }

    static void ReadTerrainGroupsTT(XmlReader reader, BinaryWriter t3Bw)
    {
        var magicAttr = reader.GetAttribute("magic");

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(string.IsNullOrEmpty(magicAttr) ? 1u : uint.Parse(magicAttr));

        var groups = new List<(string name, List<string> textures)>();
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
                    groups.Add((name, textures));
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)groups.Count);
        foreach (var (name, textures) in groups)
        {
            WriteString16(bw, name);
            bw.Write((uint)textures.Count);
            foreach (var tex in textures)
                WriteString16(bw, tex);
        }

        WriteSubSection(t3Bw, "TT", ms.ToArray());
    }

    static void ReadSizeListSection(XmlReader reader, BinaryWriter bw, int elemSize)
    {
        var marker = reader.Name;

        bw.Write((byte)marker[0]); bw.Write((byte)marker[1]);

        if (reader.IsEmptyElement)
        {
            bw.Write(4u); // section size = just the count
            bw.Write(0u); // count = 0
            reader.Read();
            return;
        }

        var text = reader.ReadElementContentAsString().Trim();
        if (text.Length == 0)
        {
            bw.Write(4u);
            bw.Write(0u);
            return;
        }

        if (IsBase64Content(text))
        {
            // New format: base64 of raw inner bytes (count + elements)
            var bytes = Convert.FromBase64String(text);
            bw.Write((uint)bytes.Length);
            bw.Write(bytes);
        }
        else
        {
            // Old format: CSV of values
            var parts = text.Split(',');
            var count = (uint)parts.Length;
            bw.Write((uint)(4 + count * (uint)elemSize));
            bw.Write(count);
            foreach (var p in parts)
            {
                if (elemSize == 1)
                    bw.Write(byte.Parse(p.Trim()));
                else
                    bw.Write(ushort.Parse(p.Trim()));
            }
        }
    }

    static void ReadWaterNames(XmlReader reader, BinaryWriter t3Bw)
    {
        var marker = reader.Name;
        var magicAttr = reader.GetAttribute("magic");

        using var innerMs = new MemoryStream();
        using var innerBw = new BinaryWriter(innerMs);
        innerBw.Write(string.IsNullOrEmpty(magicAttr) ? 0u : uint.Parse(magicAttr));

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
        WriteSubSection(t3Bw, marker, innerMs.ToArray());
    }
}
