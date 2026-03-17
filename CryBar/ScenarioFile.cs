using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml;

namespace CryBar;

/// <summary>
/// Parses .mythscn scenario files (Age of Mythology: Retold).
/// After L33t decompression, the binary data is a flat list of tagged sections.
/// Each section: [2-byte ASCII marker] [4-byte uint32 LE size] [size bytes of data].
/// </summary>
public class ScenarioFile
{
    internal const int MaxSectionSize = 50_000_000;
    const int MaxSubSectionSize = 100_000;
    const int MaxSections = 10_000;
    const int MaxStringLength = 10_000;

    [MemberNotNullWhen(true, nameof(Sections))]
    public bool Parsed { get; }
    public uint Version { get; private set; }
    public uint DataSize { get; private set; }

    /// <summary>All top-level sections in file order.</summary>
    public ScenarioSection[]? Sections { get; private set; }

    public ScenarioFile(ReadOnlyMemory<byte> data)
    {
        Parsed = Parse(data.Span);
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) return false;

        // Magic: "BG"
        if (data[0] != 0x42 || data[1] != 0x47) return false;

        DataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));
        Version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(6, 4));

        // Walk sections starting at offset 10
        var sections = new List<ScenarioSection>();
        int offset = 10;

        while (offset + 6 <= data.Length && sections.Count < MaxSections)
        {
            byte b0 = data[offset], b1 = data[offset + 1];
            if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E)
                break;

            var marker = ReadMarker(data, offset);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 2, 4));
            if (size > MaxSectionSize || offset + 6 + size > (uint)data.Length)
                break;

            var sectionData = data.Slice(offset + 6, (int)size).ToArray();
            sections.Add(new ScenarioSection(marker, sectionData));
            offset += 6 + (int)size;
        }

        if (sections.Count == 0) return false;

        Sections = sections.ToArray();
        return true;
    }

    /// <summary>
    /// Finds the first section with the given marker.
    /// </summary>
    public ScenarioSection? FindSection(string marker)
    {
        if (!Parsed) return null;
        foreach (var s in Sections)
            if (s.Marker == marker) return s;
        return null;
    }

    /// <summary>
    /// Finds all sections with the given marker.
    /// </summary>
    public ScenarioSection[] FindSections(string marker)
    {
        if (!Parsed) return [];
        return Sections.Where(s => s.Marker == marker).ToArray();
    }

    /// <summary>
    /// Gets the J1 (main data) section and parses its internal sub-sections.
    /// </summary>
    public ScenarioJ1? GetJ1()
    {
        var j1Section = FindSection("J1");
        if (j1Section == null) return null;
        return new ScenarioJ1(j1Section.Data);
    }

    /// <summary>
    /// Serializes all sections back to binary format.
    /// </summary>
    public byte[] ToBytes()
    {
        if (!Parsed) throw new InvalidOperationException("Cannot serialize unparsed scenario");

        var total = 10 + ScenarioSection.CalculateTotalSize(Sections) + 1;
        var result = new byte[total];
        var span = result.AsSpan();

        span[0] = 0x42; // 'B'
        span[1] = 0x47; // 'G'
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2, 4), (uint)(total - 7));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6, 4), Version);

        var offset = ScenarioSection.WriteSections(Sections, span, 10);
        span[offset] = 0x00;

        return result;
    }

    /// <summary>
    /// Converts the scenario to an editable XML string with human-readable
    /// entities, template tables, and known section decoding.
    /// </summary>
    public string ToXml()
    {
        if (!Parsed) throw new InvalidOperationException("Cannot convert unparsed scenario");

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = false };
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("Scenario");
        writer.WriteAttributeString("version", Version.ToString());

        foreach (var section in Sections)
        {
            if (section.Marker == "J1")
                WriteJ1Xml(writer, section);
            else if (section.Marker == "TR")
                WriteTrXml(writer, section);
            else
                WriteSectionXml(writer, section);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sb.ToString();
    }

    /// <summary>
    /// Creates a ScenarioFile from an XML string produced by ToXml().
    /// </summary>
    public static ScenarioFile FromXml(string xml)
    {
        var readerSettings = new XmlReaderSettings { IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
        reader.MoveToContent();
        var version = uint.Parse(reader.GetAttribute("version")!);

        var sections = new List<ScenarioSection>();
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "J1": sections.Add(ReadJ1Xml(reader)); break;
                    case "TR": sections.Add(ReadTrXml(reader)); break;
                    default: sections.Add(ReadSectionXml(reader)); break;
                }
            }
        }

        var total = 10 + ScenarioSection.CalculateTotalSize(sections) + 1;
        var result = new byte[total];
        var span = result.AsSpan();

        span[0] = 0x42; span[1] = 0x47;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2, 4), (uint)(total - 7));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6, 4), version);

        var offset = ScenarioSection.WriteSections(sections, span, 10);
        span[offset] = 0x00;

        return new ScenarioFile(result);
    }

    #region XML Write Helpers

    static void WriteSectionXml(XmlWriter writer, ScenarioSection section)
    {
        writer.WriteStartElement("S");
        writer.WriteAttributeString("m", section.Marker);

        if (section.Data.Length == 0)
        {
            // Empty section
        }
        else if (section.Data.Length == 4)
        {
            var val = BinaryPrimitives.ReadUInt32LittleEndian(section.Data);
            writer.WriteAttributeString("v", val.ToString());
        }
        else
        {
            writer.WriteBase64(section.Data, 0, section.Data.Length);
        }

        writer.WriteEndElement();
    }

    static void WriteJ1Xml(XmlWriter writer, ScenarioSection section)
    {
        var j1 = new ScenarioJ1(section.Data);

        writer.WriteStartElement("J1");

        if (!j1.Parsed)
        {
            writer.WriteBase64(section.Data, 0, section.Data.Length);
            writer.WriteEndElement();
            return;
        }

        writer.WriteAttributeString("hv", j1.HeaderValue.ToString());

        foreach (var sub in j1.Sections)
        {
            if (sub.Marker is "TM" or "PT")
                WriteTmXml(writer, sub);
            else if (sub.Marker == "Z1")
                WriteZ1Xml(writer, sub);
            else if (sub.Marker == "TN")
                WriteTnXml(writer, sub);
            else
                WriteSectionXml(writer, sub);
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes a TM (template/name table) section as human-readable string entries.
    /// Format: [uint32 type][uint32 count][entries: uint32 byteLen, null-terminated ASCII string]
    /// </summary>
    static void WriteTmXml(XmlWriter writer, ScenarioSection section)
    {
        if (section.Data.Length < 8)
        {
            WriteSectionXml(writer, section);
            return;
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(section.Data);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(section.Data.AsSpan(4));
        var strings = ReadTmStrings(section.Data);

        writer.WriteStartElement(section.Marker);
        writer.WriteAttributeString("type", type.ToString());
        writer.WriteAttributeString("count", count.ToString());

        foreach (var str in strings)
        {
            writer.WriteStartElement("E");
            writer.WriteString(str);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes Z1 (entities) section with decoded positions, types, and player IDs.
    /// </summary>
    static void WriteZ1Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 5)
        {
            WriteSectionXml(writer, section);
            return;
        }

        var entityCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var version = data[4];

        writer.WriteStartElement("Z1");
        writer.WriteAttributeString("count", entityCount.ToString());
        writer.WriteAttributeString("ver", version.ToString());

        int off = 5;
        for (uint ei = 0; ei < entityCount && off + 4 <= data.Length; ei++)
        {
            var entityId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(off));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(off + 2));
            off += 4;

            writer.WriteStartElement("Entity");
            writer.WriteAttributeString("id", entityId.ToString());
            if (flags != 0) writer.WriteAttributeString("flags", $"0x{flags:X4}");

            // Walk sub-sections within this entity (H1, etc.)
            while (off + 6 <= data.Length)
            {
                byte b0 = data[off], b1 = data[off + 1];
                if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E) break;

                var marker = ReadMarker(data, off);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
                if (size > MaxSubSectionSize || off + 6 + size > (uint)data.Length) break;

                var subData = data.Slice(off + 6, (int)size);

                if (marker == "H1" && size >= 86)
                    WriteEntityH1Xml(writer, subData);
                else
                {
                    writer.WriteStartElement("S");
                    writer.WriteAttributeString("m", marker);
                    writer.WriteString(Convert.ToBase64String(subData));
                    writer.WriteEndElement();
                }

                off += 6 + (int)size;
            }

            writer.WriteEndElement(); // Entity
        }

        writer.WriteEndElement(); // Z1
    }

    /// <summary>
    /// Writes an H1 entity section with decoded position, rotation, player, and sub-sections.
    /// H1 layout: [38 bytes header][12 bytes position (3 floats)][36 bytes rotation (9 floats)][sub-sections P1, P2, ...]
    /// Header byte 14 = player ID. Sub-section P1 bytes 0-3 = unit index into TM[0].
    /// </summary>
    static void WriteEntityH1Xml(XmlWriter writer, ReadOnlySpan<byte> h1)
    {
        // Pre-scan P1 to extract protoIndex before writing attributes (XML requires attributes before child elements)
        uint? protoIndex = null;
        int scanOff = 86;
        while (scanOff + 6 <= h1.Length)
        {
            byte sb0 = h1[scanOff], sb1 = h1[scanOff + 1];
            if (sb0 < 0x20 || sb0 > 0x7E || sb1 < 0x20 || sb1 > 0x7E) break;
            var sz = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 2));
            if (sz > MaxSubSectionSize || scanOff + 6 + sz > h1.Length) break;

            if (sb0 == 'P' && sb1 == '1' && sz >= 4)
                protoIndex = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 6));

            scanOff += 6 + (int)sz;
        }

        // Write ALL attributes first (before any child elements)
        var player = h1[14];
        writer.WriteAttributeString("player", player.ToString());

        if (protoIndex.HasValue)
            writer.WriteAttributeString("protoIndex", protoIndex.Value.ToString());

        var px = BitConverter.ToSingle(h1.Slice(38, 4));
        var py = BitConverter.ToSingle(h1.Slice(42, 4));
        var pz = BitConverter.ToSingle(h1.Slice(46, 4));
        writer.WriteAttributeString("x", FormatFloat(px));
        writer.WriteAttributeString("y", FormatFloat(py));
        writer.WriteAttributeString("z", FormatFloat(pz));

        var rotSb = new StringBuilder();
        for (int i = 0; i < 9; i++)
        {
            if (i > 0) rotSb.Append(',');
            rotSb.Append(FormatFloat(BitConverter.ToSingle(h1.Slice(50 + i * 4, 4))));
        }
        writer.WriteAttributeString("rot", rotSb.ToString());

        // Now write child elements
        writer.WriteStartElement("H1Hdr");
        writer.WriteString(Convert.ToBase64String(h1[..38]));
        writer.WriteEndElement();

        // Walk sub-sections inside H1 starting at byte 86 (after rotation matrix)
        int off = 86;
        while (off + 6 <= h1.Length)
        {
            byte b0 = h1[off], b1 = h1[off + 1];
            if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E) break;

            var marker = ReadMarker(h1, off);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(off + 2));
            if (size > MaxSubSectionSize || off + 6 + size > h1.Length) break;

            var innerData = h1.Slice(off + 6, (int)size);

            if (marker == "P1")
            {
                writer.WriteStartElement("P1");
                writer.WriteString(Convert.ToBase64String(innerData));
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement("S");
                writer.WriteAttributeString("m", marker);
                writer.WriteString(Convert.ToBase64String(innerData));
                writer.WriteEndElement();
            }

            off += 6 + (int)size;
        }

        if (off < h1.Length)
        {
            writer.WriteStartElement("H1Trail");
            writer.WriteString(Convert.ToBase64String(h1[off..]));
            writer.WriteEndElement();
        }
    }

    internal static string ReadMarker(ReadOnlySpan<byte> data, int off)
    {
        Span<char> chars = stackalloc char[2];
        chars[0] = (char)data[off];
        chars[1] = (char)data[off + 1];
        return new string(chars);
    }

    static string FormatFloat(float f)
    {
        // Use round-trip format to preserve exact binary value
        return f.ToString("R");
    }

    /// <summary>
    /// Writes a TR (Triggers) section with decoded header, trigger names, and fully decoded groups.
    /// Each trigger's body (conditions/effects) is preserved as base64 for byte-perfect roundtrip.
    /// </summary>
    static void WriteTrXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data;
        if (data.Length < 28 || !CanParseTr(data))
        {
            WriteSectionXml(writer, section);
            return;
        }

        WriteTrXmlInner(writer, section);
    }

    /// <summary>
    /// Pre-validates that the TR section can be fully parsed without errors.
    /// Returns false if any condition/effect parsing would go out of bounds.
    /// </summary>
    static bool CanParseTr(byte[] data)
    {
        try
        {
            var span = data.AsSpan();
            int off = 24; // skip header
            var triggerCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            if (triggerCount > 10000) return false;
            for (uint ti = 0; ti < triggerCount; ti++)
            {
                off += 4 + 12; // magic + 3 unk
                off = SkipString16(span, off); // name
                off += 4 + 5; // unk1 + flags
                off = SkipString16(span, off); // note
                var condCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint ci = 0; ci < condCount; ci++) off = SkipConditionOrEffect(span, off);
                var effectCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint ei = 0; ei < effectCount; ei++) off = SkipConditionOrEffect(span, off);
            }
            var groupCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            if (groupCount > 10000) return false;
            for (uint gi = 0; gi < groupCount; gi++)
            {
                off += 4 + 4; // magic + id
                off = SkipString8(span, off); // name
                var idxCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                off += (int)idxCount * 4;
            }
            return off == data.Length;
        }
        catch { return false; }
    }

    static void WriteTrXmlInner(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data;
        var span = data.AsSpan();
        int off = 0;

        var version = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        var zero1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        var zero2 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        var unk0 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        writer.WriteStartElement("TR");
        writer.WriteAttributeString("version", version.ToString());
        if (zero1 != 0) writer.WriteAttributeString("zero1", zero1.ToString());
        if (zero2 != 0) writer.WriteAttributeString("zero2", zero2.ToString());
        writer.WriteAttributeString("unk", $"{unk0},{unk1},{unk2}");

        var triggerCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        for (uint ti = 0; ti < triggerCount; ti++)
        {
            int triggerStart = off;

            off += 4; // MagicU32<9>
            off += 12; // 3 unknown uint32s
            // String16 name
            var nameCharCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var name = Encoding.Unicode.GetString(span.Slice(off, nameCharCount * 2)); off += nameCharCount * 2;
            off += 4; // s32 unk
            off += 5; // 5 bool bytes (flags)
            // String16 note
            var noteCharCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var note = Encoding.Unicode.GetString(span.Slice(off, noteCharCount * 2)); off += noteCharCount * 2;

            // Skip conditions
            var condCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint ci = 0; ci < condCount; ci++)
                off = SkipConditionOrEffect(span, off);

            // Skip effects
            var effectCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint ei = 0; ei < effectCount; ei++)
                off = SkipConditionOrEffect(span, off);

            writer.WriteStartElement("Trigger");
            writer.WriteAttributeString("name", name);
            if (!string.IsNullOrEmpty(note)) writer.WriteAttributeString("note", note);
            writer.WriteString(Convert.ToBase64String(span.Slice(triggerStart, off - triggerStart)));
            writer.WriteEndElement();
        }

        // Groups
        var groupCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint gi = 0; gi < groupCount; gi++)
        {
            off += 4; // MagicU32<1>
            var groupId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var nameByteLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var groupName = nameByteLen > 1
                ? Encoding.ASCII.GetString(span.Slice(off, nameByteLen - 1))
                : "";
            off += nameByteLen;
            var idxCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            writer.WriteStartElement("Group");
            writer.WriteAttributeString("id", groupId.ToString());
            writer.WriteAttributeString("name", groupName);
            if (idxCount > 0)
            {
                var sb = new StringBuilder();
                for (uint i = 0; i < idxCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)));
                    off += 4;
                }
                writer.WriteAttributeString("indexes", sb.ToString());
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WriteTnXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 2) { WriteSectionXml(writer, section); return; }

        writer.WriteStartElement("TN");
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

        // TT tile group indices: [marker TT][u32 size][SizeList<u8>]
        if (off + 6 > t3.Length) return;
        off += WriteSizeListXml(writer, t3, off, "TileGroups", 1);
        if (off + 6 > t3.Length) return;
        off += WriteSizeListXml(writer, t3, off, "TileSubs", 2);
        if (off + 6 > t3.Length) return;
        off += WriteSizeListXml(writer, t3, off, "TilePT", 1);
        if (off + 6 > t3.Length) return;
        off += WriteSizeListXml(writer, t3, off, "WaterColors", 2);

        // WI water names: [marker WI][u32 size][MagicU32<0>, SizeList<String16>]
        if (off + 6 > t3.Length) return;
        {
            var wiMarker = ReadMarker(t3, off);
            var wiSize = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off + 2));
            var wiData = t3.Slice(off + 6, (int)wiSize);
            off += 6 + (int)wiSize;

            writer.WriteStartElement("WaterNames");
            writer.WriteAttributeString("marker", wiMarker);
            if (wiData.Length >= 8)
            {
                var wiMagic = BinaryPrimitives.ReadUInt32LittleEndian(wiData);
                writer.WriteAttributeString("magic", wiMagic.ToString());
                var nameCount = BinaryPrimitives.ReadUInt32LittleEndian(wiData.Slice(4));
                writer.WriteAttributeString("count", nameCount.ToString());
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
        off += WriteSizeListXml(writer, t3, off, "WaterType", 1);

        // Height arrays
        if (off + 4 > t3.Length) return;
        var heightCount = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        off += 4;

        writer.WriteStartElement("Heights");
        writer.WriteAttributeString("count", heightCount.ToString());
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        writer.WriteStartElement("WaterHeights");
        writer.WriteAttributeString("count", heightCount.ToString());
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        writer.WriteStartElement("UnkHeights");
        writer.WriteAttributeString("count", heightCount.ToString());
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

    static int WriteSizeListXml(XmlWriter writer, ReadOnlySpan<byte> data, int off, string elemName, int elemSize)
    {
        var marker = ReadMarker(data, off);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
        var inner = data.Slice(off + 6, (int)size);
        writer.WriteStartElement(elemName);
        writer.WriteAttributeString("marker", marker);
        if (inner.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(inner);
            writer.WriteAttributeString("count", count.ToString());
            var sb = new StringBuilder();
            for (uint i = 0; i < count && 4 + i * elemSize + elemSize <= (uint)inner.Length; i++)
            {
                if (i > 0) sb.Append(',');
                if (elemSize == 1)
                    sb.Append(inner[4 + (int)i]);
                else
                    sb.Append(BinaryPrimitives.ReadUInt16LittleEndian(inner.Slice(4 + (int)i * 2)));
            }
            writer.WriteString(sb.ToString());
        }
        writer.WriteEndElement();
        return 6 + (int)size;
    }

    static int WriteFloatArrayXml(XmlWriter writer, ReadOnlySpan<byte> data, int off, uint count)
    {
        var sb = new StringBuilder();
        for (uint i = 0; i < count && off + (i + 1) * 4 <= (uint)data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off + (int)i * 4, 4))));
        }
        writer.WriteString(sb.ToString());
        return (int)count * 4;
    }

    static int SkipString8(ReadOnlySpan<byte> span, int off)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off));
        return off + 4 + len;
    }

    static int SkipString16(ReadOnlySpan<byte> span, int off)
    {
        var charCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off));
        return off + 4 + charCount * 2;
    }

    static int SkipXsArgValue(ReadOnlySpan<byte> span, int off, uint valueType)
    {
        switch (valueType)
        {
            case 4: // UnitIdList: SizeList<String16> + bool
                var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint i = 0; i < count; i++)
                    off = SkipString16(span, off);
                return off + 1;
            case 22: // StringId: u32 valueCount + MagicS32<0> + valueCount * String16
                var valCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                off += 4;
                for (uint i = 0; i < valCount; i++)
                    off = SkipString16(span, off);
                return off;
            case 42: // AnimationName: MagicS32<1> + 3 String16
                off += 4;
                off = SkipString16(span, off);
                off = SkipString16(span, off);
                return SkipString16(span, off);
            case 43: // AnimationVariant: MagicS32<1> + 4 String16
                off += 4;
                off = SkipString16(span, off);
                off = SkipString16(span, off);
                off = SkipString16(span, off);
                return SkipString16(span, off);
            case 50: // ProtoAction: MagicS32<1> + 2 String16
                off += 4;
                off = SkipString16(span, off);
                return SkipString16(span, off);
            case 2 or 5 or 8 or 56: // WithFlag: MagicS32<1> + String16 + bool
                off += 4;
                off = SkipString16(span, off);
                return off + 1;
            default: // Common: MagicS32<1> + String16
                off += 4;
                return SkipString16(span, off);
        }
    }

    static int SkipConditionOrEffect(ReadOnlySpan<byte> span, int off)
    {
        off += 4; // MagicU32<6>
        off = SkipString8(span, off); // name
        off = SkipString8(span, off); // type
        var argCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint i = 0; i < argCount; i++)
        {
            off += 4; // key_type
            off = SkipString8(span, off); // key
            off = SkipString8(span, off); // name
            var valueType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            off = SkipXsArgValue(span, off, valueType);
        }
        off = SkipString8(span, off); // command
        var extraCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint i = 0; i < extraCount; i++)
        {
            off = SkipString8(span, off); // ecommand
            off += 1; // bool has_string
            var strCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint j = 0; j < strCount; j++)
                off = SkipString8(span, off);
        }
        return off + 2; // padding
    }

    #endregion

    #region XML Read Helpers

    /// <summary>Reader must be on start element; advances past end element.</summary>
    static ScenarioSection ReadSectionXml(XmlReader reader)
    {
        var marker = reader.GetAttribute("m") ?? reader.Name;
        var valAttr = reader.GetAttribute("v");

        if (!string.IsNullOrEmpty(valAttr))
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, uint.Parse(valAttr));
            reader.Skip();
            return new ScenarioSection(marker, data);
        }

        if (reader.IsEmptyElement) { reader.Read(); return new ScenarioSection(marker, []); }

        var text = reader.ReadElementContentAsString().Trim();
        return text.Length > 0
            ? new ScenarioSection(marker, Convert.FromBase64String(text))
            : new ScenarioSection(marker, []);
    }

    static ScenarioSection ReadJ1Xml(XmlReader reader)
    {
        var hvAttr = reader.GetAttribute("hv");
        if (string.IsNullOrEmpty(hvAttr))
        {
            if (reader.IsEmptyElement) { reader.Read(); return new ScenarioSection("J1", []); }
            var text = reader.ReadElementContentAsString().Trim();
            return new ScenarioSection("J1", text.Length > 0 ? Convert.FromBase64String(text) : []);
        }

        var headerValue = uint.Parse(hvAttr);
        var subSections = new List<ScenarioSection>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "TM": case "PT": subSections.Add(ReadTmXml(reader)); break;
                    case "Z1": subSections.Add(ReadZ1Xml(reader)); break;
                    case "TN": subSections.Add(ReadTnXml(reader)); break;
                    default: subSections.Add(ReadSectionXml(reader)); break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        var result = new byte[4 + ScenarioSection.CalculateTotalSize(subSections)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, headerValue);
        ScenarioSection.WriteSections(subSections, result, 4);

        return new ScenarioSection("J1", result);
    }

    static ScenarioSection ReadTmXml(XmlReader reader)
    {
        var marker = reader.Name;
        var countAttr = reader.GetAttribute("count");
        if (string.IsNullOrEmpty(countAttr))
            return ReadSectionXml(reader);

        var typeAttr = reader.GetAttribute("type");
        var type = string.IsNullOrEmpty(typeAttr) ? 0u : uint.Parse(typeAttr);
        var count = uint.Parse(countAttr);

        var entries = new List<string>();
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name is "E" or "Proto")
                    entries.Add(reader.ReadElementContentAsString());
                else
                    reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        int totalSize = 8;
        foreach (var s in entries)
            totalSize += 4 + Encoding.ASCII.GetByteCount(s) + 1;

        var data = new byte[totalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, type);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), count);

        int off = 8;
        foreach (var s in entries)
        {
            var strBytes = Encoding.ASCII.GetBytes(s);
            var byteLen = strBytes.Length + 1;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off), (uint)byteLen);
            off += 4;
            strBytes.CopyTo(data, off);
            off += strBytes.Length;
            data[off] = 0;
            off++;
        }

        return new ScenarioSection(marker, data);
    }

    static ScenarioSection ReadZ1Xml(XmlReader reader)
    {
        var countAttr = reader.GetAttribute("count");
        if (string.IsNullOrEmpty(countAttr))
            return ReadSectionXml(reader);

        var entityCount = uint.Parse(countAttr);
        var verAttr = reader.GetAttribute("ver");
        var version = string.IsNullOrEmpty(verAttr) ? (byte)1 : byte.Parse(verAttr);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entityCount);
        bw.Write(version);

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Entity")
                {
                    var id = ushort.Parse(reader.GetAttribute("id")!);
                    var flagsStr = reader.GetAttribute("flags");
                    var flags = !string.IsNullOrEmpty(flagsStr)
                        ? ushort.Parse(flagsStr.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber)
                        : (ushort)0;
                    bw.Write(id);
                    bw.Write(flags);

                    var h1Data = RebuildEntityH1(reader);
                    bw.Write((byte)'H'); bw.Write((byte)'1');
                    bw.Write((uint)h1Data.Length);
                    bw.Write(h1Data);
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        return new ScenarioSection("Z1", ms.ToArray());
    }

    /// <summary>Reader is on &lt;Entity&gt; start; advances past end.</summary>
    static byte[] RebuildEntityH1(XmlReader reader)
    {
        var playerAttr = reader.GetAttribute("player");
        var protoIndexAttr = reader.GetAttribute("protoIndex");
        var xAttr = reader.GetAttribute("x");
        var yAttr = reader.GetAttribute("y");
        var zAttr = reader.GetAttribute("z");
        var rotAttr = reader.GetAttribute("rot");

        byte[] header = new byte[38];
        var posBytes = new byte[12];
        var rotBytes = new byte[36];
        byte[]? h1Trail = null;
        uint? protoIndex = !string.IsNullOrEmpty(protoIndexAttr) ? uint.Parse(protoIndexAttr) : null;

        if (!string.IsNullOrEmpty(xAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(0), float.Parse(xAttr));
        if (!string.IsNullOrEmpty(yAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(4), float.Parse(yAttr));
        if (!string.IsNullOrEmpty(zAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(8), float.Parse(zAttr));

        if (!string.IsNullOrEmpty(rotAttr))
        {
            var parts = rotAttr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, 9); i++)
                BitConverter.TryWriteBytes(rotBytes.AsSpan(i * 4), float.Parse(parts[i]));
        }

        var subSections = new List<(string marker, byte[] data)>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "H1Hdr":
                    {
                        var decoded = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        Array.Copy(decoded, header, Math.Min(decoded.Length, 38));
                        break;
                    }
                    case "H1Trail":
                        h1Trail = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    case "P1":
                    {
                        var sectionData = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        if (protoIndex.HasValue && sectionData.Length >= 8)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(sectionData, protoIndex.Value);
                            BinaryPrimitives.WriteUInt32LittleEndian(sectionData.AsSpan(4), protoIndex.Value);
                        }
                        subSections.Add(("P1", sectionData));
                        break;
                    }
                    case "S":
                    {
                        var m = reader.GetAttribute("m")!;
                        var sectionData = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        subSections.Add((m, sectionData));
                        break;
                    }
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        if (!string.IsNullOrEmpty(playerAttr))
            header[14] = byte.Parse(playerAttr);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(posBytes);
        ms.Write(rotBytes);

        foreach (var (marker, sectionData) in subSections)
        {
            ms.WriteByte((byte)marker[0]);
            ms.WriteByte((byte)marker[1]);
            var sizeBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)sectionData.Length);
            ms.Write(sizeBytes);
            ms.Write(sectionData);
        }

        if (h1Trail != null)
            ms.Write(h1Trail);

        return ms.ToArray();
    }

    static ScenarioSection ReadTrXml(XmlReader reader)
    {
        var versionAttr = reader.GetAttribute("version");
        if (string.IsNullOrEmpty(versionAttr))
            return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(versionAttr));
        var zero1Attr = reader.GetAttribute("zero1");
        bw.Write(string.IsNullOrEmpty(zero1Attr) ? 0 : int.Parse(zero1Attr));
        var zero2Attr = reader.GetAttribute("zero2");
        bw.Write(string.IsNullOrEmpty(zero2Attr) ? 0 : int.Parse(zero2Attr));

        var unkParts = reader.GetAttribute("unk")!.Split(',');
        bw.Write(uint.Parse(unkParts[0]));
        bw.Write(uint.Parse(unkParts[1]));
        bw.Write(uint.Parse(unkParts[2]));

        var triggerBlobs = new List<byte[]>();
        var groups = new List<(uint id, string name, string? indexes)>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Trigger")
                    triggerBlobs.Add(Convert.FromBase64String(reader.ReadElementContentAsString().Trim()));
                else if (reader.Name == "Group")
                {
                    groups.Add((
                        uint.Parse(reader.GetAttribute("id")!),
                        reader.GetAttribute("name") ?? "",
                        reader.GetAttribute("indexes")));
                    reader.Skip();
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)triggerBlobs.Count);
        foreach (var blob in triggerBlobs)
            bw.Write(blob);

        bw.Write((uint)groups.Count);
        foreach (var (id, name, indexes) in groups)
        {
            bw.Write((uint)1);
            bw.Write(id);
            var nameBytes = Encoding.ASCII.GetBytes(name);
            bw.Write((uint)(nameBytes.Length + 1));
            bw.Write(nameBytes);
            bw.Write((byte)0);

            if (string.IsNullOrEmpty(indexes))
                bw.Write((uint)0);
            else
            {
                var idxParts = indexes.Split(',');
                bw.Write((uint)idxParts.Length);
                foreach (var idx in idxParts)
                    bw.Write(uint.Parse(idx));
            }
        }

        return new ScenarioSection("TR", ms.ToArray());
    }

    /// <summary>Reader is on &lt;TN&gt; start. T3 children processed sequentially.</summary>
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
                    case "TileGroups" or "TilePT" or "WaterType":
                        ReadSizeListSection(reader, t3Bw!, 1);
                        break;
                    case "TileSubs" or "WaterColors":
                        ReadSizeListSection(reader, t3Bw!, 2);
                        break;
                    case "WaterNames":
                        ReadWaterNames(reader, t3Bw!);
                        break;
                    case "Heights":
                    {
                        var hc = uint.Parse(reader.GetAttribute("count") ?? "0");
                        if (t3Bw != null) t3Bw.Write(hc);
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString();
                        if (t3Bw != null) WriteFloatArrayFromText(t3Bw, text, hc);
                        break;
                    }
                    case "WaterHeights" or "UnkHeights":
                    {
                        var hc = uint.Parse(reader.GetAttribute("count") ?? "0");
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString();
                        if (t3Bw != null) WriteFloatArrayFromText(t3Bw, text, hc);
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

        var ttData = ms.ToArray();
        t3Bw.Write((byte)'T'); t3Bw.Write((byte)'T');
        t3Bw.Write((uint)ttData.Length);
        t3Bw.Write(ttData);
    }

    static void ReadSizeListSection(XmlReader reader, BinaryWriter bw, int elemSize)
    {
        var marker = reader.GetAttribute("marker") ?? "??";
        var countAttr = reader.GetAttribute("count");
        var count = string.IsNullOrEmpty(countAttr) ? 0u : uint.Parse(countAttr);

        bw.Write((byte)marker[0]); bw.Write((byte)marker[1]);
        bw.Write((uint)(4 + count * (uint)elemSize));
        bw.Write(count);

        if (reader.IsEmptyElement) { reader.Read(); return; }

        var text = reader.ReadElementContentAsString().Trim();
        if (text.Length > 0)
            foreach (var p in text.Split(','))
            {
                if (elemSize == 1)
                    bw.Write(byte.Parse(p.Trim()));
                else
                    bw.Write(ushort.Parse(p.Trim()));
            }
    }

    static void ReadWaterNames(XmlReader reader, BinaryWriter t3Bw)
    {
        var marker = reader.GetAttribute("marker") ?? "WI";
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
        var innerData = innerMs.ToArray();

        t3Bw.Write((byte)marker[0]); t3Bw.Write((byte)marker[1]);
        t3Bw.Write((uint)innerData.Length);
        t3Bw.Write(innerData);
    }

    static void WriteString16(BinaryWriter bw, string value)
    {
        bw.Write((uint)value.Length);
        foreach (var c in value)
            bw.Write((ushort)c);
    }

    static void WriteFloatArrayFromText(BinaryWriter bw, string text, uint count)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (uint i = 0; i < count && i < (uint)parts.Length; i++)
            bw.Write(float.Parse(parts[i]));
    }

    #endregion

    #region TM String Table Helpers

    /// <summary>
    /// Reads strings from a TM section's binary data.
    /// Format: [uint32 type][uint32 count][entries: uint32 byteLen, null-terminated ASCII string]
    /// </summary>
    static string[] ReadTmStrings(byte[] data)
    {
        if (data.Length < 8) return [];
        var span = data.AsSpan();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
        var strings = new List<string>((int)Math.Min(count, 10000));

        int off = 8;
        for (uint i = 0; i < count && off + 4 <= data.Length; i++)
        {
            var byteLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off));
            off += 4;
            if (byteLen > MaxStringLength || off + byteLen > data.Length) break;

            var strLen = (int)byteLen > 0 && data[off + (int)byteLen - 1] == 0 ? (int)byteLen - 1 : (int)byteLen;
            strings.Add(Encoding.ASCII.GetString(span.Slice(off, strLen)));
            off += (int)byteLen;
        }

        return strings.ToArray();
    }

    #endregion

    static bool TryReadUTF16(ReadOnlySpan<byte> data, int offset, out string value, out int newOffset)
    {
        value = "";
        newOffset = offset;
        if (offset + 4 > data.Length) return false;
        var charCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        if (charCount < 0 || charCount > MaxStringLength) return false;
        var byteLen = charCount * 2;
        if (offset + 4 + byteLen > data.Length) return false;
        value = Encoding.Unicode.GetString(data.Slice(offset + 4, byteLen));
        newOffset = offset + 4 + byteLen;
        return true;
    }
}

/// <summary>
/// A single section in a scenario file: 2-byte marker + raw data.
/// </summary>
public class ScenarioSection
{
    public string Marker { get; set; }
    public byte[] Data { get; set; }

    public ScenarioSection(string marker, byte[] data)
    {
        Marker = marker;
        Data = data;
    }

    /// <summary>
    /// Calculates total byte size needed for a list of sections (marker + size + data per section).
    /// </summary>
    internal static long CalculateTotalSize(IEnumerable<ScenarioSection> sections)
    {
        long total = 0;
        foreach (var s in sections)
            total += 6 + s.Data.Length;
        return total;
    }

    /// <summary>
    /// Writes a list of sections into a span at the given offset. Returns the new offset.
    /// </summary>
    internal static int WriteSections(IEnumerable<ScenarioSection> sections, Span<byte> span, int offset)
    {
        foreach (var s in sections)
        {
            span[offset] = (byte)s.Marker[0];
            span[offset + 1] = (byte)s.Marker[1];
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 2, 4), (uint)s.Data.Length);
            s.Data.AsSpan().CopyTo(span.Slice(offset + 6));
            offset += 6 + s.Data.Length;
        }
        return offset;
    }
}

/// <summary>
/// Parsed J1 (main data) section containing sub-sections.
/// </summary>
public class ScenarioJ1
{
    const int MaxSections = 50_000;

    [MemberNotNullWhen(true, nameof(Sections))]
    public bool Parsed { get; }
    public uint HeaderValue { get; private set; }
    public List<ScenarioSection>? Sections { get; private set; }

    public ScenarioJ1(byte[] data)
    {
        Parsed = Parse(data.AsSpan());
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;

        HeaderValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));

        var sections = new List<ScenarioSection>();
        int offset = 4;

        while (offset + 6 <= data.Length && sections.Count < MaxSections)
        {
            byte b0 = data[offset], b1 = data[offset + 1];
            if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E)
                break;

            var marker = ScenarioFile.ReadMarker(data, offset);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 2, 4));
            if (size > ScenarioFile.MaxSectionSize || offset + 6 + size > (uint)data.Length)
                break;

            var sectionData = data.Slice(offset + 6, (int)size).ToArray();
            sections.Add(new ScenarioSection(marker, sectionData));
            offset += 6 + (int)size;
        }

        if (sections.Count == 0) return false;

        Sections = sections;
        return true;
    }

    public ScenarioSection? FindSection(string marker)
    {
        if (!Parsed) return null;
        foreach (var s in Sections)
            if (s.Marker == marker) return s;
        return null;
    }

    public ScenarioSection[] FindSections(string marker)
    {
        if (!Parsed) return [];
        return Sections.Where(s => s.Marker == marker).ToArray();
    }

    /// <summary>
    /// Serializes J1 back to binary: header_value + all sub-sections.
    /// </summary>
    public byte[] ToBytes()
    {
        if (!Parsed) throw new InvalidOperationException("Cannot serialize unparsed J1");

        var result = new byte[4 + ScenarioSection.CalculateTotalSize(Sections)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, HeaderValue);
        ScenarioSection.WriteSections(Sections, result, 4);

        return result;
    }
}
