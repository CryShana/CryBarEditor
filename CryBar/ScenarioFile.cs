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

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = false, NewLineHandling = NewLineHandling.Entitize };
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
            else if (section.Marker == "PL")
                WritePlXml(writer, section);
            else if (section.Marker == "FH")
                WriteFhXml(writer, section);
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
                    case "World": sections.Add(ReadJ1Xml(reader)); break;
                    case "Triggers": sections.Add(ReadTrXml(reader)); break;
                    case "Players": sections.Add(ReadPlXml(reader)); break;
                    case "FileHeader": sections.Add(ReadFhXml(reader)); break;
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

        writer.WriteStartElement("World");

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
            else if (sub.Marker == "PL")
                WritePlXml(writer, sub);
            else if (sub.Marker == "FH")
                WriteFhXml(writer, sub);
            else if (sub.Marker == "RN")
                WriteRnXml(writer, sub);
            else if (sub.Marker == "RM")
                WriteRmXml(writer, sub);
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
        var strings = ReadTmStrings(section.Data);

        writer.WriteStartElement(section.Marker);
        writer.WriteAttributeString("type", type.ToString());

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

        writer.WriteStartElement("Entities");
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
            var trail = h1[off..];
            // Trailing structure: 4 magic values (16 bytes) + Pad0<20> + has_note(1) + [optional note] + MagicS32<-1>(4) + Pad0<18> + 2x fake P1 (12)
            if (trail.Length >= 51)
            {
                int tOff = 16 + 20; // skip 4 magic values + Pad0<20>
                byte hasNote = trail[tOff++];
                string? note = null;
                if (hasNote != 0 && TryReadUTF16(trail, tOff, out var noteStr, out tOff))
                    note = noteStr;

                writer.WriteStartElement("H1Trail");
                if (!string.IsNullOrEmpty(note)) writer.WriteAttributeString("note", note);
                writer.WriteString(Convert.ToBase64String(trail));
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement("H1Trail");
                writer.WriteString(Convert.ToBase64String(trail));
                writer.WriteEndElement();
            }
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
        if (section.Data.Length < 28 || !CanParseTr(section.Data))
        {
            WriteSectionXml(writer, section);
            return;
        }

        WriteTrXmlInner(writer, section);
    }

    static bool CanParseTr(byte[] data)
    {
        try
        {
            var span = data.AsSpan();
            int off = 24; // skip 6 header u32s
            var triggerCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            if (triggerCount > 10000) return false;
            for (uint ti = 0; ti < triggerCount; ti++)
            {
                off += 16; // magic + triggerId + groupId + priority
                off = SkipString16(span, off); // name
                off += 9; // unkS32 + 5 flag bytes
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
                off += 8; // magic + id
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

        writer.WriteStartElement("Triggers");
        writer.WriteAttributeString("version", version.ToString());
        if (zero1 != 0) writer.WriteAttributeString("zero1", zero1.ToString());
        if (zero2 != 0) writer.WriteAttributeString("zero2", zero2.ToString());
        writer.WriteAttributeString("unk", $"{unk0},{unk1},{unk2}");

        var triggerCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        for (uint ti = 0; ti < triggerCount; ti++)
        {
            off += 4; // MagicU32<9>
            var triggerId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var groupId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var priority = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            if (!TryReadUTF16(span, off, out var name, out off)) break;
            var unkS32 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            byte fLoop = span[off], fActive = span[off + 1], fRunImm = span[off + 2];
            byte flag3 = span[off + 3], flag4 = span[off + 4];
            off += 5;
            if (!TryReadUTF16(span, off, out var note, out off)) break;

            writer.WriteStartElement("Trigger");
            writer.WriteAttributeString("name", name);
            writer.WriteAttributeString("id", triggerId.ToString());
            writer.WriteAttributeString("group", groupId.ToString());
            writer.WriteAttributeString("priority", priority.ToString());
            writer.WriteAttributeString("unk", unkS32.ToString());
            writer.WriteAttributeString("loop", fLoop.ToString());
            writer.WriteAttributeString("active", fActive.ToString());
            writer.WriteAttributeString("runImm", fRunImm.ToString());
            if (flag3 != 0) writer.WriteAttributeString("flag3", flag3.ToString());
            if (flag4 != 0) writer.WriteAttributeString("flag4", flag4.ToString());
            if (!string.IsNullOrEmpty(note)) writer.WriteAttributeString("note", note);

            // Conditions
            var condCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint ci = 0; ci < condCount; ci++)
                off = WriteCondOrEffectXml(writer, span, off, "Cond");

            // Effects
            var effectCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint ei = 0; ei < effectCount; ei++)
                off = WriteCondOrEffectXml(writer, span, off, "Effect");

            writer.WriteEndElement();
        }

        // Groups
        var groupCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint gi = 0; gi < groupCount; gi++)
        {
            off += 4; // MagicU32<1>
            var groupIdVal = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var nameByteLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var groupName = nameByteLen > 1
                ? Encoding.ASCII.GetString(span.Slice(off, nameByteLen - 1))
                : "";
            off += nameByteLen;
            var idxCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            writer.WriteStartElement("Group");
            writer.WriteAttributeString("id", groupIdVal.ToString());
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

    static int WriteCondOrEffectXml(XmlWriter writer, ReadOnlySpan<byte> span, int off, string elemName)
    {
        off += 4; // MagicU32<6>
        var ceName = ReadString8(span, ref off);
        var ceType = ReadString8(span, ref off);

        // Pre-scan past args, cmd, extras to read trail bytes (all attributes must precede children)
        var argCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        int argsStart = off;
        for (uint i = 0; i < argCount; i++)
        {
            off += 4;
            off = SkipString8(span, off);
            off = SkipString8(span, off);
            var vt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            off = SkipXsArgValue(span, off, vt);
        }
        var cmd = ReadString8(span, ref off);
        int extrasStart = off;
        var extraCountScan = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint i = 0; i < extraCountScan; i++)
        {
            off = SkipString8(span, off);
            off += 1;
            var sc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint j = 0; j < sc; j++) off = SkipString8(span, off);
        }
        byte trail0 = span[off], trail1 = span[off + 1];
        int endOff = off + 2;

        // Write element with all attributes first
        writer.WriteStartElement(elemName);
        writer.WriteAttributeString("name", ceName);
        if (ceType != ceName) writer.WriteAttributeString("type", ceType);
        writer.WriteAttributeString("cmd", cmd);
        if (trail0 != 0 || trail1 != 0)
            writer.WriteAttributeString("trail", $"{trail0},{trail1}");

        // Write arg children
        off = argsStart;
        for (uint i = 0; i < argCount; i++)
        {
            var keyType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var key = ReadString8(span, ref off);
            var argName = ReadString8(span, ref off);
            var valueType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            writer.WriteStartElement("Arg");
            writer.WriteAttributeString("key", key);
            if (argName != key) writer.WriteAttributeString("name", argName);
            writer.WriteAttributeString("kt", keyType.ToString());
            writer.WriteAttributeString("vt", valueType.ToString());

            off = WriteXsArgValueXml(writer, span, off, valueType);
            writer.WriteEndElement();
        }

        // Write extra children
        off = extrasStart;
        var extraCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        for (uint i = 0; i < extraCount; i++)
        {
            var ecmd = ReadString8(span, ref off);
            byte hasStr = span[off++];
            var strCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            writer.WriteStartElement("Extra");
            if (hasStr != 0) writer.WriteAttributeString("has", hasStr.ToString());
            if (strCount > 0)
            {
                writer.WriteAttributeString("cmd", ecmd);
                for (uint j = 0; j < strCount; j++)
                {
                    var s = ReadString8(span, ref off);
                    writer.WriteStartElement("S");
                    writer.WriteString(s);
                    writer.WriteEndElement();
                }
            }
            else
            {
                writer.WriteString(ecmd);
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        return endOff;
    }

    static int WriteXsArgValueXml(XmlWriter writer, ReadOnlySpan<byte> span, int off, uint valueType)
    {
        switch (valueType)
        {
            case 4: // UnitIdList: count * String16 + bool
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                var values = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    TryReadUTF16(span, off, out values[i], out off);
                }
                writer.WriteAttributeString("flag", span[off].ToString());
                off += 1;
                foreach (var v in values)
                {
                    writer.WriteStartElement("V");
                    writer.WriteString(v);
                    writer.WriteEndElement();
                }
                return off;
            }
            case 22: // StringId: valCount + magic + valCount * String16
            {
                var valCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
                if (magic != 0) writer.WriteAttributeString("magic", magic.ToString());
                for (uint i = 0; i < valCount; i++)
                {
                    TryReadUTF16(span, off, out var v, out off);
                    writer.WriteStartElement("V");
                    writer.WriteString(v);
                    writer.WriteEndElement();
                }
                return off;
            }
            case 42 or 43 or 50: // AnimationName(3)/AnimationVariant(4)/ProtoAction(2): magic + N * String16
            {
                int n = valueType == 43 ? 4 : valueType == 42 ? 3 : 2;
                var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
                if (magic != 1) writer.WriteAttributeString("magic", magic.ToString());
                for (int i = 0; i < n; i++)
                {
                    TryReadUTF16(span, off, out var v, out off);
                    writer.WriteStartElement("V");
                    writer.WriteString(v);
                    writer.WriteEndElement();
                }
                return off;
            }
            case 2 or 5 or 8 or 56: // WithFlag: magic + String16 + bool
            {
                var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
                if (magic != 1) writer.WriteAttributeString("magic", magic.ToString());
                TryReadUTF16(span, off, out var v, out off);
                writer.WriteAttributeString("flag", span[off].ToString());
                off += 1;
                writer.WriteString(v);
                return off;
            }
            default: // Common: magic + String16
            {
                var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
                if (magic != 1) writer.WriteAttributeString("magic", magic.ToString());
                TryReadUTF16(span, off, out var v, out off);
                writer.WriteString(v);
                return off;
            }
        }
    }

    static string ReadString8(ReadOnlySpan<byte> span, ref int off)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off));
        off += 4;
        var str = len > 1 ? Encoding.Latin1.GetString(span.Slice(off, len - 1)) : "";
        off += len;
        return str;
    }

    static string ReadInt32ListCsv(ReadOnlySpan<byte> data, ref int off)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        if (count == 0) return "";
        var sb = new StringBuilder();
        for (uint i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)));
            off += 4;
        }
        return sb.ToString();
    }

    static string ReadUInt32ListCsv(ReadOnlySpan<byte> data, ref int off)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        if (count == 0) return "";
        var sb = new StringBuilder();
        for (uint i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)));
            off += 4;
        }
        return sb.ToString();
    }

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
        writer.WriteComment("TileGroups");
        off += WriteSizeListXml(writer, t3, off, 1);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TileSubs");
        off += WriteSizeListXml(writer, t3, off, 2);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("TilePT");
        off += WriteSizeListXml(writer, t3, off, 1);
        if (off + 6 > t3.Length) return;
        writer.WriteComment("WaterColors");
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
        writer.WriteComment("WaterType");
        off += WriteSizeListXml(writer, t3, off, 1);

        // Height arrays
        if (off + 4 > t3.Length) return;
        var heightCount = BinaryPrimitives.ReadUInt32LittleEndian(t3.Slice(off));
        off += 4;

        writer.WriteStartElement("Heights");
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

        writer.WriteStartElement("WaterHeights");
        off += WriteFloatArrayXml(writer, t3, off, heightCount);
        writer.WriteEndElement();

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
        if (inner.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(inner);
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
            case 42 or 43 or 50: // AnimationName(3)/AnimationVariant(4)/ProtoAction(2)
            {
                int n = valueType == 43 ? 4 : valueType == 42 ? 3 : 2;
                off += 4; // magic
                for (int i = 0; i < n; i++) off = SkipString16(span, off);
                return off;
            }
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

    static void WriteFhXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 8) { WriteSectionXml(writer, section); return; }

        int off = 0;
        writer.WriteStartElement("FileHeader");

        if (!TryReadUTF16(data, off, out var generator, out off)) { writer.WriteEndElement(); return; }
        var magic0 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        if (!TryReadUTF16(data, off, out var name, out off)) { writer.WriteEndElement(); return; }
        if (!TryReadUTF16(data, off, out var category, out off)) { writer.WriteEndElement(); return; }
        var z1a = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        var z1b = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        byte pad = data[off++];
        var unk2 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        if (!TryReadUTF16(data, off, out var uuid, out off)) { writer.WriteEndElement(); return; }

        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("category", category);
        writer.WriteAttributeString("uuid", uuid);
        writer.WriteAttributeString("generator", generator);
        if (magic0 != 0) writer.WriteAttributeString("magic0", magic0.ToString());
        writer.WriteAttributeString("unk1", unk1.ToString());
        if (z1a != 0) writer.WriteAttributeString("z1a", z1a.ToString());
        if (z1b != 0) writer.WriteAttributeString("z1b", z1b.ToString());
        if (pad != 0) writer.WriteAttributeString("pad", pad.ToString());
        writer.WriteAttributeString("unk2", unk2.ToString());

        if (off < data.Length)
        {
            writer.WriteStartElement("FhTail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WritePlXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 8) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        var playerCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;

        writer.WriteStartElement("Players");
        writer.WriteAttributeString("unk1", unk1.ToString());

        for (uint p = 0; p < playerCount; p++)
        {
            // BP header: 0x01 'B' 'P' + u32 length
            if (off + 7 > data.Length) break;
            off += 3; // magic \x01BP
            var bpLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int bpEnd = off + (int)bpLen;

            var bpVersion = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

            writer.WriteStartElement("Player");
            writer.WriteAttributeString("version", bpVersion.ToString());

            // P1
            off += 2; // "P1" marker
            var p1Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p1End = off + (int)p1Len;
            var playerId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            byte p1Pad = data[off++];
            TryReadUTF16(data, off, out var unkStrId, out off);
            TryReadUTF16(data, off, out var playerName, out off);
            TryReadUTF16(data, off, out var nameStrId, out off);
            byte p1Unk4 = data[off++];
            TryReadUTF16(data, off, out var unkStrId2, out off);
            var endPlayerId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;

            writer.WriteStartElement("P1");
            writer.WriteAttributeString("id", playerId.ToString());
            if (p1Pad != 0) writer.WriteAttributeString("pad", p1Pad.ToString());
            if (p1Unk4 != 0) writer.WriteAttributeString("unk4", p1Unk4.ToString());
            if (endPlayerId != playerId) writer.WriteAttributeString("endId", endPlayerId.ToString());
            writer.WriteAttributeString("name", playerName);
            if (!string.IsNullOrEmpty(unkStrId)) writer.WriteAttributeString("strId", unkStrId);
            if (!string.IsNullOrEmpty(nameStrId)) writer.WriteAttributeString("nameStrId", nameStrId);
            if (!string.IsNullOrEmpty(unkStrId2)) writer.WriteAttributeString("strId2", unkStrId2);
            if (off < p1End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p1End - off)));
            }
            off = p1End;
            writer.WriteEndElement();

            // P2
            off += 2; // "P2" marker
            var p2Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p2End = off + (int)p2Len;
            var p2Magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var godId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            // 8 bytes of 0xFF padding
            off += 8;

            writer.WriteStartElement("P2");
            writer.WriteAttributeString("god", godId.ToString());
            if (p2Magic != 1) writer.WriteAttributeString("magic", p2Magic.ToString());
            if (off < p2End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p2End - off)));
            }
            off = p2End;
            writer.WriteEndElement();

            // P3
            off += 2; // "P3" marker
            var p3Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p3End = off + (int)p3Len;
            var startAge = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var p3Unk2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var classGod = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var heroicGod = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var mythicGod = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var maxAge = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var popLimit = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var initPopCap = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

            writer.WriteStartElement("P3");
            writer.WriteAttributeString("startAge", startAge.ToString());
            writer.WriteAttributeString("unk2", p3Unk2.ToString());
            writer.WriteAttributeString("classGod", classGod.ToString());
            writer.WriteAttributeString("heroicGod", heroicGod.ToString());
            writer.WriteAttributeString("mythicGod", mythicGod.ToString());
            writer.WriteAttributeString("maxAge", maxAge.ToString());
            writer.WriteAttributeString("popLimit", popLimit.ToString());
            writer.WriteAttributeString("initPopCap", initPopCap.ToString());
            if (off < p3End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p3End - off)));
            }
            off = p3End;
            writer.WriteEndElement();

            // P4 (always empty)
            off += 2; // "P4" marker
            var p4Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            writer.WriteStartElement("P4");
            if (p4Len > 0)
                writer.WriteString(Convert.ToBase64String(data.Slice(off, (int)p4Len)));
            writer.WriteEndElement();
            off += (int)p4Len;

            // P5
            off += 2; // "P5" marker
            var p5Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p5End = off + (int)p5Len;
            byte p5b0 = data[off], p5b1 = data[off + 1], p5b2 = data[off + 2]; off += 3;
            TryReadUTF16(data, off, out var aiPath, out off);
            TryReadUTF16(data, off, out var aiPersonality, out off);
            byte p5Pad1 = data[off++];
            byte p5Unk1 = data[off++];
            var color = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var colorDupe = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;

            writer.WriteStartElement("P5");
            if (!string.IsNullOrEmpty(aiPath)) writer.WriteAttributeString("ai", aiPath);
            if (!string.IsNullOrEmpty(aiPersonality)) writer.WriteAttributeString("ai2", aiPersonality);
            writer.WriteAttributeString("color", color.ToString());
            if (colorDupe != 0) writer.WriteAttributeString("colorDupe", colorDupe.ToString());
            if (p5Unk1 != 0) writer.WriteAttributeString("unk1", p5Unk1.ToString());
            if (p5Pad1 != 0) writer.WriteAttributeString("pad1", p5Pad1.ToString());
            if (p5b0 != 1 || p5b1 != 1 || p5b2 != 1)
                writer.WriteAttributeString("pad3", $"{p5b0},{p5b1},{p5b2}");
            if (off < p5End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p5End - off)));
            }
            off = p5End;
            writer.WriteEndElement();

            // P6
            off += 2; // "P6" marker
            var p6Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p6End = off + (int)p6Len;
            var p6Unk1Str = ReadInt32ListCsv(data, ref off);
            var p6Unk2Str = ReadInt32ListCsv(data, ref off);
            // ResourceBlock
            var resMagic = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var gold = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            var wood = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            var food = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            var favor = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            var total = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            // Pad0<12> + Pad0<5>
            off += 17;

            writer.WriteStartElement("P6");
            writer.WriteAttributeString("gold", FormatFloat(gold));
            writer.WriteAttributeString("wood", FormatFloat(wood));
            writer.WriteAttributeString("food", FormatFloat(food));
            writer.WriteAttributeString("favor", FormatFloat(favor));
            writer.WriteAttributeString("total", FormatFloat(total));
            if (resMagic != 4) writer.WriteAttributeString("resMagic", resMagic.ToString());
            if (p6Unk1Str.Length > 0) writer.WriteAttributeString("list1", p6Unk1Str);
            if (p6Unk2Str.Length > 0) writer.WriteAttributeString("list2", p6Unk2Str);
            if (off < p6End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p6End - off)));
            }
            off = p6End;
            writer.WriteEndElement();

            // P7 (constant values, no section_length — just magic + pad + float)
            off += 2; // "P7" marker
            var p7Magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            off += 5; // Pad0<5>
            var p7Float = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;

            writer.WriteStartElement("P7");
            if (p7Magic != 9) writer.WriteAttributeString("magic", p7Magic.ToString());
            if (p7Float != 1.0f) writer.WriteAttributeString("val", FormatFloat(p7Float));
            writer.WriteEndElement();

            // P8
            off += 2; // "P8" marker
            var p8Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p8End = off + (int)p8Len;
            var forbidUnitsStr = ReadUInt32ListCsv(data, ref off);
            var forbidTechsStr = ReadUInt32ListCsv(data, ref off);
            var p8Unk3 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;

            writer.WriteStartElement("P8");
            if (forbidUnitsStr.Length > 0) writer.WriteAttributeString("forbidUnits", forbidUnitsStr);
            if (forbidTechsStr.Length > 0) writer.WriteAttributeString("forbidTechs", forbidTechsStr);
            if (p8Unk3 != 0) writer.WriteAttributeString("unk3", p8Unk3.ToString());
            if (off < p8End)
            {
                writer.WriteString(Convert.ToBase64String(data.Slice(off, p8End - off)));
            }
            off = p8End;
            writer.WriteEndElement();

            // P9
            off += 2; // "P9" marker
            var p9Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int p9End = off + (int)p9Len;

            writer.WriteStartElement("P9");
            if (p9Len > 0)
                writer.WriteAttributeString("raw", Convert.ToBase64String(data.Slice(off, (int)p9Len)));

            // Extract camera start position for readability (v308/v309 only)
            if (bpVersion < 314 && p9Len >= 7 + 22 + 17 + 20 + 1 + 12)
            {
                int camOff = off + 7; // Pad0<7>
                camOff += 2; // "mm" marker
                var mmLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(camOff)); camOff += 4;
                camOff += (int)mmLen + 17 + 20; // mm content + Pad0<17> + 5 floats
                byte hasCamStart = data[camOff++];
                if (hasCamStart != 0)
                {
                    var camX = BitConverter.ToSingle(data.Slice(camOff, 4));
                    var camY = BitConverter.ToSingle(data.Slice(camOff + 4, 4));
                    var camZ = BitConverter.ToSingle(data.Slice(camOff + 8, 4));
                    writer.WriteAttributeString("camX", FormatFloat(camX));
                    writer.WriteAttributeString("camY", FormatFloat(camY));
                    writer.WriteAttributeString("camZ", FormatFloat(camZ));
                }
            }
            off = p9End;
            writer.WriteEndElement();

            // Trailing sections after P9 (fake P1s, or Pa/Pb/Pc for v314+)
            if (off < bpEnd)
            {
                writer.WriteStartElement("BpTail");
                writer.WriteString(Convert.ToBase64String(data.Slice(off, bpEnd - off)));
                writer.WriteEndElement();
            }
            off = bpEnd;

            writer.WriteEndElement(); // Player
        }

        if (off < data.Length)
        {
            writer.WriteStartElement("PlTail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    static void WriteRnXml(XmlWriter writer, ScenarioSection section)
    {
        var span = section.Data.AsSpan();
        if (!TryReadUTF16(span, 0, out var s1, out var off) ||
            !TryReadUTF16(span, off, out var s2, out _))
        {
            WriteSectionXml(writer, section);
            return;
        }

        writer.WriteStartElement("MapInfo");
        writer.WriteAttributeString("s1", s1);
        writer.WriteAttributeString("s2", s2);
        writer.WriteEndElement();
    }

    static void WriteRmXml(XmlWriter writer, ScenarioSection section)
    {
        var span = section.Data.AsSpan();
        if (span.Length < 4)
        {
            WriteSectionXml(writer, section);
            return;
        }

        var off = 0;
        var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        if (magic != 1 || off + 4 > span.Length)
        {
            WriteSectionXml(writer, section);
            return;
        }

        var rm1Count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var rm1 = new StringBuilder();
        for (uint i = 0; i < rm1Count; i++)
        {
            if (off + 5 > span.Length) { WriteSectionXml(writer, section); return; }
            var val = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var flag = span[off]; off += 1;
            if (i > 0) rm1.Append(',');
            rm1.Append(val).Append(':').Append(flag);
        }

        if (off + 4 > span.Length) { WriteSectionXml(writer, section); return; }
        var rm2Count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var rm2 = new StringBuilder();
        for (uint i = 0; i < rm2Count; i++)
        {
            if (off + 8 > span.Length) { WriteSectionXml(writer, section); return; }
            var a = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            var b = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            if (i > 0) rm2.Append(',');
            rm2.Append(a).Append(':').Append(b);
        }

        writer.WriteStartElement("RandomMap");
        writer.WriteAttributeString("rm1", rm1.ToString());
        writer.WriteAttributeString("rm2", rm2.ToString());
        writer.WriteEndElement();
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
                    case "Entities": subSections.Add(ReadZ1Xml(reader)); break;
                    case "Terrain": subSections.Add(ReadTnXml(reader)); break;
                    case "Players": subSections.Add(ReadPlXml(reader)); break;
                    case "FileHeader": subSections.Add(ReadFhXml(reader)); break;
                    case "MapInfo": subSections.Add(ReadRnXml(reader)); break;
                    case "RandomMap": subSections.Add(ReadRmXml(reader)); break;
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
        var typeAttr = reader.GetAttribute("type");
        if (string.IsNullOrEmpty(typeAttr))
            return ReadSectionXml(reader);

        var type = uint.Parse(typeAttr);

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
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)entries.Count);

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

    static ScenarioSection ReadRnXml(XmlReader reader)
    {
        var s1 = reader.GetAttribute("s1") ?? "";
        var s2 = reader.GetAttribute("s2") ?? "";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteString16(bw, s1);
        WriteString16(bw, s2);
        bw.Flush();

        reader.Skip();
        return new ScenarioSection("RN", ms.ToArray());
    }

    static ScenarioSection ReadRmXml(XmlReader reader)
    {
        var rm1Attr = reader.GetAttribute("rm1") ?? "";
        var rm2Attr = reader.GetAttribute("rm2") ?? "";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1); // magic

        var rm1Entries = rm1Attr.Length > 0 ? rm1Attr.Split(',') : [];
        bw.Write((uint)rm1Entries.Length);
        foreach (var entry in rm1Entries)
        {
            var parts = entry.Split(':');
            bw.Write(int.Parse(parts[0]));
            bw.Write(byte.Parse(parts[1]));
        }

        var rm2Entries = rm2Attr.Length > 0 ? rm2Attr.Split(',') : [];
        bw.Write((uint)rm2Entries.Length);
        foreach (var entry in rm2Entries)
        {
            var parts = entry.Split(':');
            bw.Write(int.Parse(parts[0]));
            bw.Write(int.Parse(parts[1]));
        }

        bw.Flush();
        reader.Skip();
        return new ScenarioSection("RM", ms.ToArray());
    }

    static ScenarioSection ReadZ1Xml(XmlReader reader)
    {
        var verAttr = reader.GetAttribute("ver");
        if (string.IsNullOrEmpty(verAttr))
            return ReadSectionXml(reader);

        var version = byte.Parse(verAttr);

        var entities = new List<byte[]>();
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Entity")
                {
                    using var ems = new MemoryStream();
                    using var ebw = new BinaryWriter(ems);
                    var id = ushort.Parse(reader.GetAttribute("id")!);
                    var flagsStr = reader.GetAttribute("flags");
                    var flags = !string.IsNullOrEmpty(flagsStr)
                        ? ushort.Parse(flagsStr.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber)
                        : (ushort)0;
                    ebw.Write(id);
                    ebw.Write(flags);

                    var h1Data = RebuildEntityH1(reader);
                    ebw.Write((byte)'H'); ebw.Write((byte)'1');
                    ebw.Write((uint)h1Data.Length);
                    ebw.Write(h1Data);
                    entities.Add(ems.ToArray());
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)entities.Count);
        bw.Write(version);
        foreach (var e in entities) bw.Write(e);

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

        var triggers = new List<byte[]>();
        var groups = new List<(uint id, string name, string? indexes)>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Trigger")
                    triggers.Add(ReadTriggerXml(reader));
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

        bw.Write((uint)triggers.Count);
        foreach (var blob in triggers)
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

    static byte[] ReadTriggerXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var idAttr = reader.GetAttribute("id");
        if (idAttr == null)
        {
            // Legacy base64 format
            var blob = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
            return blob;
        }

        bw.Write(9u); // magic
        bw.Write(uint.Parse(idAttr));
        bw.Write(uint.Parse(reader.GetAttribute("group") ?? "0"));
        bw.Write(uint.Parse(reader.GetAttribute("priority") ?? "0"));
        WriteString16(bw, reader.GetAttribute("name") ?? "");
        bw.Write(int.Parse(reader.GetAttribute("unk") ?? "-1"));

        bw.Write(byte.Parse(reader.GetAttribute("loop") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("active") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("runImm") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("flag3") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("flag4") ?? "0"));

        WriteString16(bw, reader.GetAttribute("note") ?? "");

        var conds = new List<byte[]>();
        var effects = new List<byte[]>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Cond")
                    conds.Add(ReadCondOrEffectXml(reader));
                else if (reader.Name == "Effect")
                    effects.Add(ReadCondOrEffectXml(reader));
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)conds.Count);
        foreach (var c in conds) bw.Write(c);
        bw.Write((uint)effects.Count);
        foreach (var e in effects) bw.Write(e);

        return ms.ToArray();
    }

    static byte[] ReadCondOrEffectXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var ceName = reader.GetAttribute("name") ?? "";
        var ceType = reader.GetAttribute("type") ?? ceName;
        var cmd = reader.GetAttribute("cmd") ?? "";
        var trailAttr = reader.GetAttribute("trail");
        byte trail0 = 0, trail1 = 0;
        if (trailAttr != null)
        {
            var tp = trailAttr.Split(',');
            trail0 = byte.Parse(tp[0]);
            trail1 = byte.Parse(tp[1]);
        }

        bw.Write(6u); // magic
        WriteString8(bw, ceName);
        WriteString8(bw, ceType);

        var args = new List<byte[]>();
        var extras = new List<byte[]>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Arg")
                    args.Add(ReadXsArgXml(reader));
                else if (reader.Name == "Extra")
                    extras.Add(ReadExtraXml(reader));
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)args.Count);
        foreach (var a in args) bw.Write(a);

        WriteString8(bw, cmd);

        bw.Write((uint)extras.Count);
        foreach (var e in extras) bw.Write(e);

        bw.Write(trail0);
        bw.Write(trail1);
        return ms.ToArray();
    }

    static byte[] ReadXsArgXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var key = reader.GetAttribute("key") ?? "";
        var argName = reader.GetAttribute("name") ?? key;
        var keyType = uint.Parse(reader.GetAttribute("kt") ?? "0");
        var valueType = uint.Parse(reader.GetAttribute("vt") ?? "0");
        var magicAttr = reader.GetAttribute("magic");
        var flagAttr = reader.GetAttribute("flag");

        bw.Write(keyType);
        WriteString8(bw, key);
        WriteString8(bw, argName);
        bw.Write(valueType);

        switch (valueType)
        {
            case 4: // UnitIdList: count * String16 + bool
            {
                var values = ReadVChildren(reader);
                bw.Write((uint)values.Count);
                foreach (var v in values) WriteString16(bw, v);
                bw.Write(byte.Parse(flagAttr ?? "0"));
                break;
            }
            case 22: // StringId: valCount + magic + valCount * String16
            {
                var values = ReadVChildren(reader);
                bw.Write((uint)values.Count);
                bw.Write(int.Parse(magicAttr ?? "0"));
                foreach (var v in values) WriteString16(bw, v);
                break;
            }
            case 42 or 43 or 50: // AnimationName/AnimationVariant/ProtoAction: magic + N * String16
            {
                bw.Write(int.Parse(magicAttr ?? "1"));
                if (!reader.IsEmptyElement)
                {
                    reader.Read();
                    while (reader.NodeType != XmlNodeType.EndElement)
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "V")
                        {
                            var v = reader.ReadElementContentAsString();
                            WriteString16(bw, v);
                        }
                        else reader.Read();
                    }
                    reader.ReadEndElement();
                }
                else reader.Read();
                break;
            }
            case 2 or 5 or 8 or 56: // WithFlag: magic + String16 + bool
            {
                bw.Write(int.Parse(magicAttr ?? "1"));
                var text = reader.ReadElementContentAsString();
                WriteString16(bw, text);
                bw.Write(byte.Parse(flagAttr ?? "0"));
                break;
            }
            default: // Common: magic + String16
            {
                bw.Write(int.Parse(magicAttr ?? "1"));
                var text = reader.ReadElementContentAsString();
                WriteString16(bw, text);
                break;
            }
        }

        return ms.ToArray();
    }

    static byte[] ReadExtraXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var hasAttr = reader.GetAttribute("has");
        byte hasStr = byte.Parse(hasAttr ?? "0");
        var cmdAttr = reader.GetAttribute("cmd");

        if (cmdAttr != null)
        {
            // Has child S elements
            WriteString8(bw, cmdAttr);
            bw.Write(hasStr);
            var strings = new List<string>();
            if (!reader.IsEmptyElement)
            {
                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "S")
                        strings.Add(reader.ReadElementContentAsString());
                    else reader.Read();
                }
                reader.ReadEndElement();
            }
            else reader.Read();
            bw.Write((uint)strings.Count);
            foreach (var s in strings) WriteString8(bw, s);
        }
        else
        {
            // Simple text content
            var text = reader.ReadElementContentAsString();
            WriteString8(bw, text);
            bw.Write(hasStr);
            bw.Write(0u);
        }

        return ms.ToArray();
    }

    static void WriteString8(BinaryWriter bw, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        bw.Write(bytes.Length + 1);
        bw.Write(bytes);
        bw.Write((byte)0);
    }

    static void WriteCsvInt32List(BinaryWriter bw, string? csv)
    {
        if (string.IsNullOrEmpty(csv)) { bw.Write(0u); return; }
        var parts = csv.Split(',');
        bw.Write((uint)parts.Length);
        foreach (var v in parts) bw.Write(int.Parse(v));
    }

    static void WriteCsvUInt32List(BinaryWriter bw, string? csv)
    {
        if (string.IsNullOrEmpty(csv)) { bw.Write(0u); return; }
        var parts = csv.Split(',');
        bw.Write((uint)parts.Length);
        foreach (var v in parts) bw.Write(uint.Parse(v));
    }

    static void WriteSubSection(BinaryWriter bw, string marker, byte[] data)
    {
        bw.Write((byte)marker[0]);
        bw.Write((byte)marker[1]);
        bw.Write((uint)data.Length);
        bw.Write(data);
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
                        var text = reader.ReadElementContentAsString();
                        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var hc = (uint)parts.Length;
                        if (t3Bw != null) { t3Bw.Write(hc); WriteFloatArrayFromText(t3Bw, text, hc); }
                        break;
                    }
                    case "WaterHeights" or "UnkHeights":
                    {
                        if (reader.IsEmptyElement) { reader.Read(); break; }
                        var text = reader.ReadElementContentAsString();
                        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (t3Bw != null) WriteFloatArrayFromText(t3Bw, text, (uint)parts.Length);
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

    static List<string> ReadVChildren(XmlReader reader)
    {
        var values = new List<string>();
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "V")
                    values.Add(reader.ReadElementContentAsString());
                else reader.Read();
            }
            reader.ReadEndElement();
        }
        else reader.Read();
        return values;
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

    static ScenarioSection ReadFhXml(XmlReader reader)
    {
        var nameAttr = reader.GetAttribute("name");
        if (nameAttr == null) return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        WriteString16(bw, reader.GetAttribute("generator") ?? "");
        bw.Write(uint.Parse(reader.GetAttribute("magic0") ?? "0"));
        bw.Write(uint.Parse(reader.GetAttribute("unk1") ?? "0"));
        WriteString16(bw, nameAttr);
        WriteString16(bw, reader.GetAttribute("category") ?? "");
        bw.Write(uint.Parse(reader.GetAttribute("z1a") ?? "0"));
        bw.Write(uint.Parse(reader.GetAttribute("z1b") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("pad") ?? "0"));
        bw.Write(uint.Parse(reader.GetAttribute("unk2") ?? "0"));
        WriteString16(bw, reader.GetAttribute("uuid") ?? "");

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "FhTail")
                    bw.Write(Convert.FromBase64String(reader.ReadElementContentAsString().Trim()));
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        return new ScenarioSection("FH", ms.ToArray());
    }

    static ScenarioSection ReadPlXml(XmlReader reader)
    {
        var unk1Attr = reader.GetAttribute("unk1");
        if (unk1Attr == null) return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(uint.Parse(unk1Attr));

        var players = new List<byte[]>();

        void FlushPlayers()
        {
            bw.Write((uint)players.Count);
            foreach (var p in players) { bw.Write((byte)0x01); bw.Write((byte)'B'); bw.Write((byte)'P'); bw.Write((uint)p.Length); bw.Write(p); }
            players.Clear();
        }

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Player")
                    players.Add(ReadPlayerBpXml(reader));
                else if (reader.Name == "PlTail")
                {
                    FlushPlayers();
                    bw.Write(Convert.FromBase64String(reader.ReadElementContentAsString().Trim()));
                }
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        if (players.Count > 0)
            FlushPlayers();

        return new ScenarioSection("PL", ms.ToArray());
    }

    static byte[] ReadPlayerBpXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var bpVersion = int.Parse(reader.GetAttribute("version") ?? "308");
        bw.Write(bpVersion);

        if (reader.IsEmptyElement) { reader.Read(); return ms.ToArray(); }

        reader.Read();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
            switch (reader.Name)
            {
                case "P1":
                {
                    var id = uint.Parse(reader.GetAttribute("id") ?? "0");
                    byte pad = byte.Parse(reader.GetAttribute("pad") ?? "0");
                    byte unk4 = byte.Parse(reader.GetAttribute("unk4") ?? "0");
                    var endId = uint.Parse(reader.GetAttribute("endId") ?? id.ToString());
                    var name = reader.GetAttribute("name") ?? "";
                    var strId = reader.GetAttribute("strId") ?? "";
                    var nameStrId = reader.GetAttribute("nameStrId") ?? "";
                    var strId2 = reader.GetAttribute("strId2") ?? "";

                    using var p1Ms = new MemoryStream();
                    using var p1Bw = new BinaryWriter(p1Ms);
                    p1Bw.Write(id);
                    p1Bw.Write(pad);
                    WriteString16(p1Bw, strId);
                    WriteString16(p1Bw, name);
                    WriteString16(p1Bw, nameStrId);
                    p1Bw.Write(unk4);
                    WriteString16(p1Bw, strId2);
                    p1Bw.Write(endId);

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p1Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P1", p1Ms.ToArray());
                    break;
                }
                case "P2":
                {
                    var god = uint.Parse(reader.GetAttribute("god") ?? "0");
                    var magic = uint.Parse(reader.GetAttribute("magic") ?? "1");

                    using var p2Ms = new MemoryStream();
                    using var p2Bw = new BinaryWriter(p2Ms);
                    p2Bw.Write(magic);
                    p2Bw.Write(god);
                    for (int i = 0; i < 8; i++) p2Bw.Write((byte)0xFF);

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p2Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P2", p2Ms.ToArray());
                    break;
                }
                case "P3":
                {
                    using var p3Ms = new MemoryStream();
                    using var p3Bw = new BinaryWriter(p3Ms);
                    p3Bw.Write(uint.Parse(reader.GetAttribute("startAge") ?? "4294967295"));
                    p3Bw.Write(int.Parse(reader.GetAttribute("unk2") ?? "-1"));
                    p3Bw.Write(uint.Parse(reader.GetAttribute("classGod") ?? "4294967295"));
                    p3Bw.Write(uint.Parse(reader.GetAttribute("heroicGod") ?? "4294967295"));
                    p3Bw.Write(uint.Parse(reader.GetAttribute("mythicGod") ?? "4294967295"));
                    p3Bw.Write(uint.Parse(reader.GetAttribute("maxAge") ?? "4294967295"));
                    p3Bw.Write(int.Parse(reader.GetAttribute("popLimit") ?? "-1"));
                    p3Bw.Write(int.Parse(reader.GetAttribute("initPopCap") ?? "-1"));

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p3Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P3", p3Ms.ToArray());
                    break;
                }
                case "P4":
                {
                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        WriteSubSection(bw, "P4", text.Length > 0 ? Convert.FromBase64String(text) : []);
                    }
                    else { WriteSubSection(bw, "P4", []); reader.Read(); }
                    break;
                }
                case "P5":
                {
                    var pad3Attr = reader.GetAttribute("pad3");
                    byte pb0 = 1, pb1 = 1, pb2 = 1;
                    if (pad3Attr != null)
                    {
                        var pp = pad3Attr.Split(',');
                        pb0 = byte.Parse(pp[0]); pb1 = byte.Parse(pp[1]); pb2 = byte.Parse(pp[2]);
                    }

                    using var p5Ms = new MemoryStream();
                    using var p5Bw = new BinaryWriter(p5Ms);
                    p5Bw.Write(pb0); p5Bw.Write(pb1); p5Bw.Write(pb2);
                    WriteString16(p5Bw, reader.GetAttribute("ai") ?? "");
                    WriteString16(p5Bw, reader.GetAttribute("ai2") ?? "");
                    p5Bw.Write(byte.Parse(reader.GetAttribute("pad1") ?? "0"));
                    p5Bw.Write(byte.Parse(reader.GetAttribute("unk1") ?? "0"));
                    p5Bw.Write(uint.Parse(reader.GetAttribute("color") ?? "0"));
                    p5Bw.Write(uint.Parse(reader.GetAttribute("colorDupe") ?? "0"));

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p5Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P5", p5Ms.ToArray());
                    break;
                }
                case "P6":
                {
                    using var p6Ms = new MemoryStream();
                    using var p6Bw = new BinaryWriter(p6Ms);

                    WriteCsvInt32List(p6Bw, reader.GetAttribute("list1"));
                    WriteCsvInt32List(p6Bw, reader.GetAttribute("list2"));
                    p6Bw.Write(int.Parse(reader.GetAttribute("resMagic") ?? "4"));
                    p6Bw.Write(float.Parse(reader.GetAttribute("gold") ?? "0"));
                    p6Bw.Write(float.Parse(reader.GetAttribute("wood") ?? "0"));
                    p6Bw.Write(float.Parse(reader.GetAttribute("food") ?? "0"));
                    p6Bw.Write(float.Parse(reader.GetAttribute("favor") ?? "0"));
                    p6Bw.Write(float.Parse(reader.GetAttribute("total") ?? "0"));
                    for (int i = 0; i < 17; i++) p6Bw.Write((byte)0);

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p6Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P6", p6Ms.ToArray());
                    break;
                }
                case "P7":
                {
                    var magic = uint.Parse(reader.GetAttribute("magic") ?? "9");
                    var val = float.Parse(reader.GetAttribute("val") ?? "1");
                    bw.Write((byte)'P'); bw.Write((byte)'7');
                    bw.Write(magic);
                    for (int i = 0; i < 5; i++) bw.Write((byte)0);
                    bw.Write(val);
                    reader.Skip();
                    break;
                }
                case "P8":
                {
                    using var p8Ms = new MemoryStream();
                    using var p8Bw = new BinaryWriter(p8Ms);

                    WriteCsvUInt32List(p8Bw, reader.GetAttribute("forbidUnits"));
                    WriteCsvUInt32List(p8Bw, reader.GetAttribute("forbidTechs"));
                    p8Bw.Write(uint.Parse(reader.GetAttribute("unk3") ?? "0"));

                    if (!reader.IsEmptyElement)
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) p8Bw.Write(Convert.FromBase64String(text));
                    }
                    else reader.Read();

                    WriteSubSection(bw, "P8", p8Ms.ToArray());
                    break;
                }
                case "P9":
                {
                    var rawAttr = reader.GetAttribute("raw");
                    if (rawAttr != null)
                        WriteSubSection(bw, "P9", Convert.FromBase64String(rawAttr));
                    reader.Skip();
                    break;
                }
                case "BpTail":
                {
                    bw.Write(Convert.FromBase64String(reader.ReadElementContentAsString().Trim()));
                    break;
                }
                default:
                    reader.Skip();
                    break;
            }
        }
        reader.ReadEndElement();

        return ms.ToArray();
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
