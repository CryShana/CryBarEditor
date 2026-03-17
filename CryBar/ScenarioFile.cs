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

            var marker = $"{(char)b0}{(char)b1}";
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
    /// Generates a human-readable summary of the parsed scenario.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(Scenario not parsed)";

        var sb = new StringBuilder();
        sb.AppendLine($"AoM Scenario (format version {Version})");

        // FH details
        var fh = FindSection("FH");
        if (fh != null)
        {
            var fhSpan = fh.Data.AsSpan();
            if (TryReadUTF16(fhSpan, 0, out var versionStr, out _))
                sb.AppendLine($"Build: {versionStr}");
        }

        // SH details
        var sh = FindSection("SH");
        if (sh != null && sh.Data.Length >= 20)
        {
            var shSpan = sh.Data.AsSpan();
            var w = BinaryPrimitives.ReadUInt32LittleEndian(shSpan.Slice(12, 4));
            var h = BinaryPrimitives.ReadUInt32LittleEndian(shSpan.Slice(16, 4));
            sb.AppendLine($"Map size: {w}x{h}");
        }

        // J1 summary
        var j1 = GetJ1();
        if (j1 is { Parsed: true })
        {
            var rn = j1.FindSection("RN");
            if (rn != null)
            {
                var rnSpan = rn.Data.AsSpan();
                if (TryReadUTF16(rnSpan, 0, out var name, out var off))
                {
                    TryReadUTF16(rnSpan, off, out var type, out _);
                    sb.AppendLine($"Name: \"{name}\", Type: \"{type}\"");
                }
            }

            // Player info
            var pl = j1.FindSection("PL");
            if (pl != null && pl.Data.Length >= 4)
            {
                var playerCount = BinaryPrimitives.ReadInt32LittleEndian(pl.Data.AsSpan().Slice(0, 4));
                sb.AppendLine($"\nPlayers: {playerCount}");
            }

            // Entity count from Z1
            var z1 = j1.FindSection("Z1");
            if (z1 != null && z1.Data.Length >= 4)
            {
                var entityCount = BinaryPrimitives.ReadUInt32LittleEndian(z1.Data.AsSpan().Slice(0, 4));
                sb.AppendLine($"\nEntities: {entityCount}");
                sb.AppendLine($"Entity data size: {z1.Data.Length:N0} bytes");
            }

            // Triggers
            var tr = FindSection("TR");
            if (tr != null)
            {
                sb.AppendLine($"\nTrigger data: {tr.Data.Length:N0} bytes");
                if (tr.Data.Length > 100)
                    sb.AppendLine("  (Contains triggers/scripts)");
            }

            // Terrain
            var tn = j1.FindSection("TN");
            if (tn != null)
                sb.AppendLine($"\nTerrain data: {tn.Data.Length:N0} bytes");

            // Template counts
            var tmSections = j1.FindSections("TM");
            if (tmSections.Length > 0)
            {
                sb.AppendLine($"\nTemplate tables: {tmSections.Length}");
                for (int i = 0; i < tmSections.Length; i++)
                    sb.AppendLine($"  TM[{i}]: {tmSections[i].Data.Length:N0} bytes");
            }

            // W7 objectives
            var w7 = j1.FindSection("W7");
            if (w7 != null && w7.Data.Length > 20)
                sb.AppendLine($"\nObjectives data: {w7.Data.Length:N0} bytes");

            // Section overview
            sb.AppendLine($"\nJ1 sub-sections: {j1.Sections.Count}");
            var sectionCounts = new Dictionary<string, int>();
            foreach (var s in j1.Sections)
            {
                sectionCounts.TryGetValue(s.Marker, out var c);
                sectionCounts[s.Marker] = c + 1;
            }
            foreach (var kvp in sectionCounts.Where(k => k.Value > 1 || k.Key.Length == 2))
            {
                if (kvp.Value > 1)
                    sb.AppendLine($"  \"{kvp.Key}\" x{kvp.Value}");
            }
        }

        sb.AppendLine($"\nTop-level sections: {Sections.Length}");
        foreach (var s in Sections)
            sb.AppendLine($"  \"{s.Marker}\" — {s.Data.Length:N0} bytes");

        return sb.ToString();
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
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var root = doc.DocumentElement ?? throw new InvalidOperationException("No root element");
        var version = uint.Parse(root.GetAttribute("version"));

        var sections = new List<ScenarioSection>();
        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is not XmlElement elem) continue;
            sections.Add(elem.Name == "J1" ? ReadJ1Xml(elem) : ReadSectionXml(elem));
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

        // Collect TM tables for entity name resolution
        var tmTables = new List<string[]>();
        foreach (var sub in j1.Sections)
        {
            if (sub.Marker == "TM")
                tmTables.Add(ReadTmStrings(sub.Data));
        }

        foreach (var sub in j1.Sections)
        {
            if (sub.Marker == "TM")
                WriteTmXml(writer, sub);
            else if (sub.Marker == "Z1")
                WriteZ1Xml(writer, sub, tmTables.Count > 0 ? tmTables[0] : null);
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

        writer.WriteStartElement("TM");
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
    static void WriteZ1Xml(XmlWriter writer, ScenarioSection section, string[]? protoNames)
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

                var marker = $"{(char)b0}{(char)b1}";
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 2));
                if (size > MaxSubSectionSize || off + 6 + size > (uint)data.Length) break;

                var subData = data.Slice(off + 6, (int)size);

                if (marker == "H1" && size >= 86)
                    WriteEntityH1Xml(writer, subData, protoNames);
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
    /// Header byte 14 = player ID. Sub-section P1 bytes 0-3 = protounit index into TM[0].
    /// </summary>
    static void WriteEntityH1Xml(XmlWriter writer, ReadOnlySpan<byte> h1, string[]? protoNames)
    {
        // Pre-scan P1 to extract type info before writing any attributes
        uint? typeIndex = null;
        string? typeName = null;
        int scanOff = 86;
        while (scanOff + 6 <= h1.Length)
        {
            byte sb0 = h1[scanOff], sb1 = h1[scanOff + 1];
            if (sb0 < 0x20 || sb0 > 0x7E || sb1 < 0x20 || sb1 > 0x7E) break;
            var sz = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 2));
            if (sz > MaxSubSectionSize || scanOff + 6 + sz > h1.Length) break;

            if (sb0 == 'P' && sb1 == '1' && sz >= 4)
            {
                typeIndex = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 6));
                if (protoNames != null && typeIndex.Value < protoNames.Length)
                    typeName = protoNames[typeIndex.Value];
            }
            scanOff += 6 + (int)sz;
        }

        // Write ALL attributes first (before any child elements)
        var player = h1[14];
        writer.WriteAttributeString("player", player.ToString());

        if (typeName != null)
            writer.WriteAttributeString("type", typeName);
        if (typeIndex.HasValue)
            writer.WriteAttributeString("typeIndex", typeIndex.Value.ToString());

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

            var marker = $"{(char)b0}{(char)b1}";
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

    static string FormatFloat(float f)
    {
        // Use round-trip format to preserve exact binary value
        return f.ToString("R");
    }

    #endregion

    #region XML Read Helpers

    static ScenarioSection ReadSectionXml(XmlElement elem)
    {
        var marker = elem.GetAttribute("m");
        if (string.IsNullOrEmpty(marker))
            marker = elem.Name; // fallback

        var valAttr = elem.GetAttribute("v");
        if (!string.IsNullOrEmpty(valAttr))
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, uint.Parse(valAttr));
            return new ScenarioSection(marker, data);
        }

        var text = elem.InnerText.Trim();
        if (text.Length > 0)
            return new ScenarioSection(marker, Convert.FromBase64String(text));

        return new ScenarioSection(marker, []);
    }

    static ScenarioSection ReadJ1Xml(XmlElement elem)
    {
        var hvAttr = elem.GetAttribute("hv");
        if (string.IsNullOrEmpty(hvAttr))
        {
            var text = elem.InnerText.Trim();
            return new ScenarioSection("J1", text.Length > 0 ? Convert.FromBase64String(text) : []);
        }

        var headerValue = uint.Parse(hvAttr);
        var subSections = new List<ScenarioSection>();
        foreach (XmlNode child in elem.ChildNodes)
        {
            if (child is not XmlElement subElem) continue;

            if (subElem.Name == "TM")
                subSections.Add(ReadTmXml(subElem));
            else if (subElem.Name == "Z1")
                subSections.Add(ReadZ1Xml(subElem));
            else
                subSections.Add(ReadSectionXml(subElem));
        }

        var result = new byte[4 + ScenarioSection.CalculateTotalSize(subSections)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, headerValue);
        ScenarioSection.WriteSections(subSections, result, 4);

        return new ScenarioSection("J1", result);
    }

    /// <summary>
    /// Reads a TM section from XML back to binary.
    /// </summary>
    static ScenarioSection ReadTmXml(XmlElement elem)
    {
        var countAttr = elem.GetAttribute("count");
        if (string.IsNullOrEmpty(countAttr))
            return ReadSectionXml(elem);

        var typeAttr = elem.GetAttribute("type");
        var type = string.IsNullOrEmpty(typeAttr) ? 0u : uint.Parse(typeAttr);
        var count = uint.Parse(countAttr);

        // Collect string entries
        var entries = new List<string>();
        foreach (XmlNode child in elem.ChildNodes)
        {
            if (child is XmlElement e && e.Name == "E")
                entries.Add(e.InnerText);
        }

        // Build binary: [uint32 type][uint32 count][entries: uint32 byteLen, ASCII + null]
        int totalSize = 8;
        foreach (var s in entries)
            totalSize += 4 + Encoding.ASCII.GetByteCount(s) + 1; // +1 for null terminator

        var data = new byte[totalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, type);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), count);

        int off = 8;
        foreach (var s in entries)
        {
            var strBytes = Encoding.ASCII.GetBytes(s);
            var byteLen = strBytes.Length + 1; // include null terminator
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off), (uint)byteLen);
            off += 4;
            strBytes.CopyTo(data, off);
            off += strBytes.Length;
            data[off] = 0; // null terminator
            off++;
        }

        return new ScenarioSection("TM", data);
    }

    /// <summary>
    /// Reads a Z1 section from XML back to binary.
    /// </summary>
    static ScenarioSection ReadZ1Xml(XmlElement elem)
    {
        var countAttr = elem.GetAttribute("count");
        if (string.IsNullOrEmpty(countAttr))
            return ReadSectionXml(elem);

        var entityCount = uint.Parse(countAttr);
        var verAttr = elem.GetAttribute("ver");
        var version = string.IsNullOrEmpty(verAttr) ? (byte)1 : byte.Parse(verAttr);

        // Build TM[0] name-to-index map from sibling TM elements
        Dictionary<string, uint>? nameToIndex = null;
        if (elem.ParentNode is XmlElement parent)
        {
            foreach (XmlNode sibling in parent.ChildNodes)
            {
                if (sibling is XmlElement tmElem && tmElem.Name == "TM")
                {
                    // Use only the first TM table (protounits)
                    nameToIndex = new Dictionary<string, uint>();
                    uint idx = 0;
                    foreach (XmlNode child in tmElem.ChildNodes)
                    {
                        if (child is XmlElement e && e.Name == "E")
                            nameToIndex[e.InnerText] = idx++;
                    }
                    break;
                }
            }
        }

        // Collect entity data
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entityCount);
        bw.Write(version);

        foreach (XmlNode child in elem.ChildNodes)
        {
            if (child is not XmlElement entityElem || entityElem.Name != "Entity") continue;

            var id = ushort.Parse(entityElem.GetAttribute("id"));
            var flagsStr = entityElem.GetAttribute("flags");
            var flags = !string.IsNullOrEmpty(flagsStr)
                ? ushort.Parse(flagsStr.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber)
                : (ushort)0;

            bw.Write(id);
            bw.Write(flags);

            // Rebuild H1 from decoded attributes + stored data
            var h1Data = RebuildEntityH1(entityElem, nameToIndex);
            bw.Write((byte)'H');
            bw.Write((byte)'1');
            bw.Write((uint)h1Data.Length);
            bw.Write(h1Data);
        }

        return new ScenarioSection("Z1", ms.ToArray());
    }

    /// <summary>
    /// Rebuilds the H1 binary data from XML entity attributes and child elements.
    /// </summary>
    static byte[] RebuildEntityH1(XmlElement entityElem, Dictionary<string, uint>? nameToIndex)
    {
        // Read H1 header from base64
        byte[] header = new byte[38];
        var hdrElem = entityElem.SelectSingleNode("H1Hdr") as XmlElement;
        if (hdrElem != null)
        {
            var decoded = Convert.FromBase64String(hdrElem.InnerText.Trim());
            Array.Copy(decoded, header, Math.Min(decoded.Length, 38));
        }

        // Patch player ID at byte 14
        var playerAttr = entityElem.GetAttribute("player");
        if (!string.IsNullOrEmpty(playerAttr))
            header[14] = byte.Parse(playerAttr);

        // Position (3 floats at bytes 38-49)
        var posBytes = new byte[12];
        WriteFloatAttr(entityElem, "x", posBytes, 0);
        WriteFloatAttr(entityElem, "y", posBytes, 4);
        WriteFloatAttr(entityElem, "z", posBytes, 8);

        // Rotation (9 floats at bytes 50-85)
        var rotBytes = new byte[36];
        var rotAttr = entityElem.GetAttribute("rot");
        if (!string.IsNullOrEmpty(rotAttr))
        {
            var parts = rotAttr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, 9); i++)
                BitConverter.TryWriteBytes(rotBytes.AsSpan(i * 4), float.Parse(parts[i]));
        }

        // Collect sub-sections (P1, P2, etc.)
        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(posBytes);
        ms.Write(rotBytes);

        // Resolve type by name if changed
        var typeAttr = entityElem.GetAttribute("type");
        var typeIndexAttr = entityElem.GetAttribute("typeIndex");
        uint? resolvedTypeIndex = null;
        if (!string.IsNullOrEmpty(typeAttr) && nameToIndex != null && nameToIndex.TryGetValue(typeAttr, out var idx))
            resolvedTypeIndex = idx;
        else if (!string.IsNullOrEmpty(typeIndexAttr))
            resolvedTypeIndex = uint.Parse(typeIndexAttr);

        foreach (XmlNode child in entityElem.ChildNodes)
        {
            if (child is not XmlElement e) continue;
            if (e.Name == "H1Hdr" || e.Name == "H1Trail") continue;

            string marker;
            byte[] sectionData;

            if (e.Name == "P1")
            {
                marker = "P1";
                sectionData = Convert.FromBase64String(e.InnerText.Trim());
                // Patch type index if resolved
                if (resolvedTypeIndex.HasValue && sectionData.Length >= 8)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(sectionData, resolvedTypeIndex.Value);
                    BinaryPrimitives.WriteUInt32LittleEndian(sectionData.AsSpan(4), resolvedTypeIndex.Value);
                }
            }
            else if (e.Name == "S")
            {
                marker = e.GetAttribute("m");
                sectionData = Convert.FromBase64String(e.InnerText.Trim());
            }
            else continue;

            ms.WriteByte((byte)marker[0]);
            ms.WriteByte((byte)marker[1]);
            var sizeBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)sectionData.Length);
            ms.Write(sizeBytes);
            ms.Write(sectionData);
        }

        // Trailing data
        var trailElem = entityElem.SelectSingleNode("H1Trail") as XmlElement;
        if (trailElem != null)
            ms.Write(Convert.FromBase64String(trailElem.InnerText.Trim()));

        return ms.ToArray();
    }

    static void WriteFloatAttr(XmlElement elem, string attr, byte[] target, int offset)
    {
        var val = elem.GetAttribute(attr);
        if (!string.IsNullOrEmpty(val))
            BitConverter.TryWriteBytes(target.AsSpan(offset), float.Parse(val));
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

            var marker = $"{(char)b0}{(char)b1}";
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
