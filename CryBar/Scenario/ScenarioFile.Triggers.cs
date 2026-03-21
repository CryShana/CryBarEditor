using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
    /// <summary>
    /// Converts a TR ScenarioSection to standalone Triggers XML string.
    /// </summary>
    public static string SectionToTriggersXml(ScenarioSection section)
    {
        if (!CanParseTr(section.Data, out var headerSize))
            throw new InvalidOperationException("Invalid TR section data");

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.Entitize
        };
        var sb = new StringBuilder(Math.Max(1024, section.Data.Length * 2));
        using var writer = XmlWriter.Create(sb, settings);
        writer.WriteStartDocument();
        WriteTrXmlInner(writer, section, headerSize);
        writer.WriteEndDocument();
        writer.Flush();
        return sb.ToString();
    }

    static void WriteTrXml(XmlWriter writer, ScenarioSection section)
    {
        if (section.Data.Length < 20 || !CanParseTr(section.Data, out var headerSize))
        {
            WriteSectionXml(writer, section);
            return;
        }

        WriteTrXmlInner(writer, section, headerSize);
    }

    /// <summary>
    /// Validates TR section data structure. Auto-detects header size:
    /// - Scenario format (24 bytes): version + zero1 + zero2 + unk0 + unk1 + unk2
    /// - Standalone .trg format (16 bytes): version + unk0 + unk1 + unk2
    /// The zero1/zero2 fields are scenario-context fields omitted in standalone trigger exports.
    /// </summary>
    internal static bool CanParseTr(byte[] data, out int headerSize)
    {
        // Try scenario format first (24-byte header), then standalone (16-byte)
        if (TryValidateTrBody(data, 24)) { headerSize = 24; return true; }
        if (TryValidateTrBody(data, 16)) { headerSize = 16; return true; }
        headerSize = 0;
        return false;
    }

    static bool TryValidateTrBody(byte[] data, int headerBytes)
    {
        try
        {
            var span = data.AsSpan();
            int off = headerBytes;
            if (off + 4 > span.Length) return false;
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

    /// <summary>
    /// Writes TR section to XML using the pre-detected header size.
    /// headerSize 24 = scenario format (with zero1/zero2), 16 = standalone .trg format.
    /// </summary>
    static void WriteTrXmlInner(XmlWriter writer, ScenarioSection section, int headerSize)
    {
        var data = section.Data;
        var span = data.AsSpan();
        int off = 0;

        var version = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        int zero1 = 0, zero2 = 0;
        if (headerSize == 24)
        {
            zero1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            zero2 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        }
        var unk0 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        writer.WriteComment("""

            CryBar Trigger XML - Known Value Types
            vt: 0=string/number, 3=bool, 4=unitIdList, 5=location, 6=player,
                10=tech, 11=status/techstatus, 12=godpower, 13=protounit, 22=stringId
            kt: 10 (standard)

        """);
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
            var groupName = ReadString8(span, ref off);
            var indexes = ReadUInt32ListCsv(span, ref off);

            writer.WriteStartElement("Group");
            writer.WriteAttributeString("id", groupIdVal.ToString());
            writer.WriteAttributeString("name", groupName);
            if (indexes.Length > 0)
                writer.WriteAttributeString("indexes", indexes);
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

    // ── TR Read ──

    /// <summary>
    /// Reads TR XML back to binary. When includePadding is true (default, scenario format),
    /// writes the 24-byte header with zero1/zero2. When false (.trg format), writes the
    /// 16-byte header without them.
    /// </summary>
    internal static ScenarioSection ReadTrXml(XmlReader reader, bool includePadding = true)
    {
        var versionAttr = reader.GetAttribute("version");
        if (string.IsNullOrEmpty(versionAttr))
            return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(versionAttr));
        if (includePadding)
        {
            var zero1Attr = reader.GetAttribute("zero1");
            bw.Write(string.IsNullOrEmpty(zero1Attr) ? 0 : int.Parse(zero1Attr));
            var zero2Attr = reader.GetAttribute("zero2");
            bw.Write(string.IsNullOrEmpty(zero2Attr) ? 0 : int.Parse(zero2Attr));
        }

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
            WriteString8(bw, name);

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
}
