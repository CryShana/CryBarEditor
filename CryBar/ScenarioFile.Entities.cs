using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar;

public partial class ScenarioFile
{
    /// <summary>TM/PT section labels by index (from AomrLib StringDb).</summary>
    static readonly string[] TmLabels = ["Unit/Protounit names (referenced by Entity protoIndex)", "Logical names", "Tech names", "Ability names"];

    static void WriteTmXml(XmlWriter writer, ScenarioSection section, int tmIndex)
    {
        if (section.Data.Length < 8)
        {
            WriteSectionXml(writer, section);
            return;
        }

        if (tmIndex >= 0 && tmIndex < TmLabels.Length)
            writer.WriteComment($" {TmLabels[tmIndex]} ");

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

        writer.WriteComment(" protoIndex references the first PT/TM table (Unit/Protounit names) ");
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

                if (marker == "H1" && size >= 82)
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
    /// Computes entity field offsets from the EN sub-section inside H1.
    /// H1 layout: [EN marker 2B][EN size 4B][unitIdCopy 4B][magic 4B][playerId 4B][Unk2 len+data][Position 12B][Matrix 36B][optional bool]][P1...][P2...][P3...][2x ConstP1]
    /// EN size varies: 76 (Unk2=12, old format) or 80 (Unk2=16, new format), plus optional +1 for boolean.
    /// Old format (j1Version &lt;= 412): P1/P2 are inline (no markers). New format: P1/P2 have "P1"/"P2" section markers.
    /// </summary>
    static bool GetEntityOffsets(ReadOnlySpan<byte> h1, out int posOff, out int rotOff, out int enEnd)
    {
        posOff = rotOff = enEnd = 0;
        if (h1.Length < 22) return false;

        // EN section: h1[0..1] = 'EN' marker, h1[2..5] = size
        var enSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(2));
        enEnd = 6 + enSize;
        if (enEnd > h1.Length || enSize < 52) return false; // minimum: 4+4+4+4+0+12+36 = 64, but Unk2 len adds 4

        var unk2Len = (int)BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(18));
        if (unk2Len < 0 || unk2Len > 1000) return false;

        posOff = 22 + unk2Len;
        rotOff = posOff + 12;

        // Validate: rotation must fit within EN section
        return rotOff + 36 <= enEnd;
    }

    /// <summary>
    /// Writes an H1 entity section with decoded position, rotation, player, and sub-sections.
    /// Handles both old format (inline P1, EN size ~76) and new format (named P1 section, EN size ~80).
    /// </summary>
    static void WriteEntityH1Xml(XmlWriter writer, ReadOnlySpan<byte> h1)
    {
        if (!GetEntityOffsets(h1, out int posOff, out int rotOff, out int enEnd))
        {
            // Fallback: write as raw base64
            writer.WriteStartElement("S");
            writer.WriteAttributeString("m", "H1");
            writer.WriteString(Convert.ToBase64String(h1));
            writer.WriteEndElement();
            return;
        }

        // Extract protoIndex: check for named P1 section or inline P1 after EN
        uint? protoIndex = null;
        int scanOff = enEnd;

        // Try named P1 sections first
        while (scanOff + 6 <= h1.Length)
        {
            byte sb0 = h1[scanOff], sb1 = h1[scanOff + 1];
            if (sb0 < 0x20 || sb0 > 0x7E || sb1 < 0x20 || sb1 > 0x7E) break;
            var sz = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 2));
            if (sz > MaxSubSectionSize || scanOff + 6 + sz > h1.Length) break;

            if (sb0 == 'P' && sb1 == '1' && sz >= 4)
            {
                protoIndex = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(scanOff + 6));
                break;
            }

            scanOff += 6 + (int)sz;
        }

        // Fallback: inline P1 (old format) — first uint32 after EN end is NameIndex
        if (!protoIndex.HasValue && enEnd + 4 <= h1.Length)
        {
            // Check that the bytes at enEnd are NOT an ASCII section marker (confirming inline format)
            byte fb0 = h1[enEnd], fb1 = h1[enEnd + 1];
            if (fb0 < 0x20 || fb0 > 0x7E || fb1 < 0x20 || fb1 > 0x7E)
                protoIndex = BinaryPrimitives.ReadUInt32LittleEndian(h1.Slice(enEnd));
        }

        // Write ALL attributes first (before any child elements)
        var player = h1[14];
        writer.WriteAttributeString("player", player.ToString());

        if (protoIndex.HasValue)
            writer.WriteAttributeString("protoIndex", protoIndex.Value.ToString());

        var px = BitConverter.ToSingle(h1.Slice(posOff, 4));
        var py = BitConverter.ToSingle(h1.Slice(posOff + 4, 4));
        var pz = BitConverter.ToSingle(h1.Slice(posOff + 8, 4));
        writer.WriteAttributeString("x", FormatFloat(px));
        writer.WriteAttributeString("y", FormatFloat(py));
        writer.WriteAttributeString("z", FormatFloat(pz));

        var rotSb = new StringBuilder();
        for (int i = 0; i < 9; i++)
        {
            if (i > 0) rotSb.Append(',');
            rotSb.Append(FormatFloat(BitConverter.ToSingle(h1.Slice(rotOff + i * 4, 4))));
        }
        writer.WriteAttributeString("rot", rotSb.ToString());

        // H1Hdr = full EN section (variable size, contains all pre-P1 data including position/rotation)
        writer.WriteStartElement("H1Hdr");
        writer.WriteString(Convert.ToBase64String(h1[..enEnd]));
        writer.WriteEndElement();

        // Walk named sub-sections after EN (new format has P1, P2 here)
        int off = enEnd;
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

        // H1Trail: everything after named sub-sections (P3 data, ConstP1s, or inline P1/P2/P3/ConstP1s for old format)
        if (off < h1.Length)
        {
            var trail = h1[off..];
            string? note = null;

            // Try to extract note from trail structure
            // Old format trail: P1 inline + P2 inline + P3(magic*4 + pad20 + hasNote + [note] + magic + pad18) + 2x ConstP1
            // New format trail: P3(magic*4 + pad20 + hasNote + [note] + magic + pad18) + 2x ConstP1
            // Note extraction: scan for hasNote byte in the P3 area
            int noteSearchOff = off == enEnd ? -1 : 0; // for new format, P3 starts at trail beginning
            if (noteSearchOff == 0 && trail.Length >= 51)
            {
                int tOff = 16 + 20; // skip P3: 4 magic values (16 bytes) + Pad0<20>
                byte hasNote = trail[tOff++];
                if (hasNote != 0 && TryReadUTF16(trail, tOff, out var noteStr, out _))
                    note = noteStr;
            }

            writer.WriteStartElement("H1Trail");
            if (!string.IsNullOrEmpty(note)) writer.WriteAttributeString("note", note);
            writer.WriteString(Convert.ToBase64String(trail));
            writer.WriteEndElement();
        }
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

        byte[] header = [];
        byte[]? h1Trail = null;
        uint? protoIndex = !string.IsNullOrEmpty(protoIndexAttr) ? uint.Parse(protoIndexAttr) : null;

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
                        header = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
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

        // Patch player, position, rotation into the header at correct offsets
        if (header.Length >= 22 && GetEntityOffsets(header, out int posOff, out int rotOff, out int enEnd))
        {
            if (!string.IsNullOrEmpty(playerAttr))
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), int.Parse(playerAttr));

            if (!string.IsNullOrEmpty(xAttr)) BitConverter.TryWriteBytes(header.AsSpan(posOff), float.Parse(xAttr));
            if (!string.IsNullOrEmpty(yAttr)) BitConverter.TryWriteBytes(header.AsSpan(posOff + 4), float.Parse(yAttr));
            if (!string.IsNullOrEmpty(zAttr)) BitConverter.TryWriteBytes(header.AsSpan(posOff + 8), float.Parse(zAttr));

            if (!string.IsNullOrEmpty(rotAttr))
            {
                var parts = rotAttr.Split(',');
                for (int i = 0; i < Math.Min(parts.Length, 9); i++)
                    BitConverter.TryWriteBytes(header.AsSpan(rotOff + i * 4), float.Parse(parts[i]));
            }

            // Patch protoIndex into inline P1 within trail (old format: no named P1 sub-sections)
            if (protoIndex.HasValue && subSections.All(s => s.marker != "P1") && h1Trail != null && h1Trail.Length >= 8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h1Trail.AsSpan(0), protoIndex.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(h1Trail.AsSpan(4), protoIndex.Value);
            }
        }
        else
        {
            // Legacy fallback: old 38-byte header format (for XMLs exported before this change)
            if (header.Length == 0) header = new byte[38];
            if (header.Length <= 38)
            {
                if (!string.IsNullOrEmpty(playerAttr) && header.Length > 14)
                    header[14] = byte.Parse(playerAttr);
            }

            // Write position/rotation as separate blocks after header (legacy layout)
            using var legacyMs = new MemoryStream();
            legacyMs.Write(header);

            var posBytes = new byte[12];
            var rotBytes = new byte[36];
            if (!string.IsNullOrEmpty(xAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(0), float.Parse(xAttr));
            if (!string.IsNullOrEmpty(yAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(4), float.Parse(yAttr));
            if (!string.IsNullOrEmpty(zAttr)) BitConverter.TryWriteBytes(posBytes.AsSpan(8), float.Parse(zAttr));
            if (!string.IsNullOrEmpty(rotAttr))
            {
                var parts = rotAttr.Split(',');
                for (int i = 0; i < Math.Min(parts.Length, 9); i++)
                    BitConverter.TryWriteBytes(rotBytes.AsSpan(i * 4), float.Parse(parts[i]));
            }
            legacyMs.Write(posBytes);
            legacyMs.Write(rotBytes);

            WriteSubSectionsAndTrail(legacyMs, subSections, h1Trail);
            return legacyMs.ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(header);

        WriteSubSectionsAndTrail(ms, subSections, h1Trail);
        return ms.ToArray();
    }

    static void WriteSubSectionsAndTrail(MemoryStream ms, List<(string marker, byte[] data)> subSections, byte[]? trail)
    {
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        foreach (var (marker, data) in subSections)
            WriteSubSection(bw, marker, data);
        if (trail != null)
            ms.Write(trail);
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

    static List<string> ReadTmStrings(byte[] data)
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

        return strings;
    }
}
