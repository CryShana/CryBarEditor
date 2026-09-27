using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
    public sealed class TrScript
    {
        public string Name { get; set; } = "";
        public bool HasBody { get; set; }
        public List<string> BodyLines { get; set; } = new();
    }

    public sealed class TrFormat
    {
        public int Version;
        public bool IsScenario;
        public int BodyStart;
        public int ArgTrailerSize;
        public List<TrScript>? Scripts;
        public uint Unk0, Unk1, Unk2;
    }

    /// <summary>
    /// Converts a TR ScenarioSection to standalone Triggers XML string.
    /// </summary>
    public static string SectionToTriggersXml(ScenarioSection section)
    {
        if (!CanParseTr(section.Data, out TrFormat format))
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
        WriteTrXmlInner(writer, section, format);
        writer.WriteEndDocument();
        writer.Flush();
        return sb.ToString();
    }

    static void WriteTrXml(XmlWriter writer, ScenarioSection section)
    {
        if (section.Data.Length < 20 || !CanParseTr(section.Data, out TrFormat format))
        {
            WriteSectionXml(writer, section);
            return;
        }

        WriteTrXmlInner(writer, section, format);
    }

    /// <summary>
    /// Validates TR section data structure and populates a <see cref="TrFormat"/>
    /// describing the layout. Auto-detects:
    /// - Scenario format (24-byte minimum header) with optional embedded XS script list
    /// - Standalone .trg format (16-byte header, no script list)
    /// </summary>
    internal static bool CanParseTr(byte[] data, out TrFormat format)
    {
        // Scenario formats: try with and without embedded script list, both versions
        if (TryParseScenarioHeader(data, out format) && TryValidateTrBody(data, format))
            return true;
        // Standalone .trg
        if (TryParseStandaloneHeader(data, out format) && TryValidateTrBody(data, format))
            return true;
        format = null!;
        return false;
    }

    static bool TryParseScenarioHeader(byte[] data, out TrFormat format)
    {
        format = null!;
        var span = data.AsSpan();
        if (span.Length < 24) return false;

        int off = 0;
        var version = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        if (version < 0 || version > 100) return false;

        var scriptCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        if (scriptCount > 1000) return false;

        List<TrScript>? scripts = null;
        // Legacy zero u32 separator only follows the script list when the last script has no body.
        bool lastScriptHasBody = false;
        if (scriptCount > 0)
        {
            scripts = new List<TrScript>((int)scriptCount);
            for (uint i = 0; i < scriptCount; i++)
            {
                if (off + 4 > span.Length) return false;
                if (!TryReadUTF16(span, off, out var name, out off)) return false;
                if (off + 1 > span.Length) return false;
                byte hasBody = span[off]; off += 1;
                if (hasBody > 1) return false;
                var script = new TrScript { Name = name, HasBody = hasBody == 1 };
                if (hasBody == 1)
                {
                    if (off + 4 > span.Length) return false;
                    var lineCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                    if (lineCount > 100_000) return false;
                    for (uint li = 0; li < lineCount; li++)
                    {
                        if (!TryReadUTF16(span, off, out var line, out off)) return false;
                        script.BodyLines.Add(line);
                    }
                }
                scripts.Add(script);
                lastScriptHasBody = hasBody == 1;
            }
            if (!lastScriptHasBody)
            {
                if (off + 4 > span.Length) return false;
                off += 4;
            }
        }
        else
        {
            if (off + 4 > span.Length) return false;
            off += 4;
        }

        if (off + 12 + 4 > span.Length) return false;
        var unk0 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        format = new TrFormat
        {
            Version = version,
            IsScenario = true,
            BodyStart = off,
            ArgTrailerSize = version >= 12 ? 1 : 0,
            Scripts = scripts,
            Unk0 = unk0,
            Unk1 = unk1,
            Unk2 = unk2,
        };
        return true;
    }

    static bool TryParseStandaloneHeader(byte[] data, out TrFormat format)
    {
        format = null!;
        var span = data.AsSpan();
        if (span.Length < 16 + 4) return false;

        int off = 0;
        var version = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
        if (version < 0 || version > 100) return false;
        var unk0 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        var unk2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        format = new TrFormat
        {
            Version = version,
            IsScenario = false,
            BodyStart = 16,
            ArgTrailerSize = version >= 12 ? 1 : 0,
            Scripts = null,
            Unk0 = unk0,
            Unk1 = unk1,
            Unk2 = unk2,
        };
        return true;
    }

    static bool TryValidateTrBody(byte[] data, TrFormat format)
    {
        try
        {
            var span = data.AsSpan();
            int off = format.BodyStart;
            int trailer = format.ArgTrailerSize;
            int trVersion = format.Version;
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
                for (uint ci = 0; ci < condCount; ci++) off = SkipConditionOrEffect(span, off, trailer, trVersion);
                var effectCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint ei = 0; ei < effectCount; ei++) off = SkipConditionOrEffect(span, off, trailer, trVersion);
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

    static void WriteTrXmlInner(XmlWriter writer, ScenarioSection section, TrFormat format)
    {
        var data = section.Data;
        var span = data.AsSpan();
        int off = format.BodyStart;
        int argTrailer = format.ArgTrailerSize;

        writer.WriteComment("""

            CryBar Trigger XML - Known Value Types
            vt: 0=string/number, 3=bool, 4=unitIdList, 5=location, 6=player,
                10=tech, 11=status/techstatus, 12=godpower, 13=protounit, 22=stringId
            kt: 10 (standard)

        """);
        writer.WriteStartElement("Triggers");
        writer.WriteAttributeString("version", format.Version.ToString());
        if (!format.IsScenario) writer.WriteAttributeString("standalone", "1");
        writer.WriteAttributeString("unk", $"{format.Unk0},{format.Unk1},{format.Unk2}");

        if (format.Scripts != null && format.Scripts.Count > 0)
        {
            foreach (var script in format.Scripts)
            {
                writer.WriteStartElement("Script");
                writer.WriteAttributeString("name", script.Name);
                if (script.HasBody)
                {
                    writer.WriteAttributeString("hasBody", "1");
                    foreach (var line in script.BodyLines)
                    {
                        writer.WriteStartElement("Line");
                        writer.WriteString(line);
                        writer.WriteEndElement();
                    }
                }
                writer.WriteEndElement();
            }
        }

        var triggerCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

        for (uint ti = 0; ti < triggerCount; ti++)
        {
            off += 4; // MagicU32<9>
            var triggerId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var groupId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            var priority = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;

            var name = ReadString16Checked(span, ref off);
            var unkS32 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
            byte fLoop = span[off], fActive = span[off + 1], fRunImm = span[off + 2];
            byte flag3 = span[off + 3], flag4 = span[off + 4];
            off += 5;
            var note = ReadString16Checked(span, ref off);

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
                off = WriteCondOrEffectXml(writer, span, off, "Cond", argTrailer, format.Version);

            // Effects
            var effectCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            for (uint ei = 0; ei < effectCount; ei++)
                off = WriteCondOrEffectXml(writer, span, off, "Effect", argTrailer, format.Version);

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

    static int WriteCondOrEffectXml(XmlWriter writer, ReadOnlySpan<byte> span, int off, string elemName, int argTrailer, int trVersion)
    {
        off += 4; // MagicU32<6>
        var ceName = ReadString8(span, ref off);
        var ceType = ReadString8(span, ref off);

        // XmlWriter requires all attributes before any child elements, so pre-walk
        // args/cmd/extras to read the per-element trail bytes that need to land in attributes.
        var argCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
        int argsStart = off;
        byte[]? argTrails = null;
        bool anyArgTrail = false;
        for (uint i = 0; i < argCount; i++)
        {
            off += 4;
            off = SkipString8(span, off);
            off = SkipString8(span, off);
            var vt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
            off = SkipXsArgValue(span, off, vt, trVersion);
            if (argTrailer > 0)
            {
                byte t = span[off++];
                if (t != 0)
                {
                    argTrails ??= new byte[argCount];
                    argTrails[i] = t;
                    anyArgTrail = true;
                }
            }
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

        writer.WriteStartElement(elemName);
        writer.WriteAttributeString("name", ceName);
        if (ceType != ceName) writer.WriteAttributeString("type", ceType);
        writer.WriteAttributeString("cmd", cmd);
        if (trail0 != 0 || trail1 != 0)
            writer.WriteAttributeString("trail", $"{trail0},{trail1}");
        if (anyArgTrail)
            writer.WriteAttributeString("argTrails", string.Join(",", argTrails!));

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

            off = WriteXsArgValueXml(writer, span, off, valueType, trVersion);
            writer.WriteEndElement();
            off += argTrailer;
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

    static int WriteXsArgValueXml(XmlWriter writer, ReadOnlySpan<byte> span, int off, uint valueType, int trVersion)
    {
        switch (valueType)
        {
            case 4: // UnitIdList: count * String16 [+ v12 proto list] + bool
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                var values = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    values[i] = ReadString16Checked(span, ref off);
                }
                (uint id, uint magic, string name)[]? protos = null;
                if (trVersion >= 12)
                {
                    var count2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                    protos = new (uint, uint, string)[count2];
                    for (uint i = 0; i < count2; i++)
                    {
                        var pid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                        var pmagic = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                        var pname = ReadString16Checked(span, ref off);
                        protos[i] = (pid, pmagic, pname);
                    }
                }
                writer.WriteAttributeString("flag", span[off].ToString());
                off += 1;
                foreach (var v in values)
                {
                    writer.WriteStartElement("V");
                    writer.WriteString(v);
                    writer.WriteEndElement();
                }
                if (protos != null)
                {
                    foreach (var (pid, pmagic, pname) in protos)
                    {
                        writer.WriteStartElement("Proto");
                        writer.WriteAttributeString("id", pid.ToString());
                        if (pmagic != 2) writer.WriteAttributeString("magic", pmagic.ToString());
                        writer.WriteString(pname);
                        writer.WriteEndElement();
                    }
                }
                return off;
            }
            case 7: // Sound/MultiString: count + count * String16
            {
                var sCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint i = 0; i < sCount; i++)
                {
                    var s = ReadString16Checked(span, ref off);
                    writer.WriteStartElement("V");
                    writer.WriteString(s);
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
                    var v = ReadString16Checked(span, ref off);
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
                    var v = ReadString16Checked(span, ref off);
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
                var v = ReadString16Checked(span, ref off);
                writer.WriteAttributeString("flag", span[off].ToString());
                off += 1;
                writer.WriteString(v);
                return off;
            }
            default: // Common: magic + String16
            {
                var magic = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(off)); off += 4;
                if (magic != 1) writer.WriteAttributeString("magic", magic.ToString());
                var v = ReadString16Checked(span, ref off);
                writer.WriteString(v);
                return off;
            }
        }
    }

    static string ReadString16Checked(ReadOnlySpan<byte> span, ref int off)
    {
        if (!TryReadUTF16(span, off, out var value, out var newOff))
            throw new InvalidDataException($"Invalid UTF-16 string at offset {off}");

        off = newOff;
        return value;
    }

    static int SkipXsArgValue(ReadOnlySpan<byte> span, int off, uint valueType, int trVersion)
    {
        switch (valueType)
        {
            case 4: // UnitIdList: count*String16 [+ v12 proto list] + bool
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint i = 0; i < count; i++)
                    off = SkipString16(span, off);
                if (trVersion >= 12)
                {
                    var count2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                    for (uint i = 0; i < count2; i++)
                    {
                        off += 8; // unit id u32 + magic u32
                        off = SkipString16(span, off);
                    }
                }
                return off + 1;
            }
            case 7: // Sound/MultiString: count + count * String16
            {
                var sCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off)); off += 4;
                for (uint i = 0; i < sCount; i++)
                    off = SkipString16(span, off);
                return off;
            }
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

    static int SkipConditionOrEffect(ReadOnlySpan<byte> span, int off, int argTrailer, int trVersion)
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
            off = SkipXsArgValue(span, off, valueType, trVersion);
            off += argTrailer;
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
    /// Reads TR XML back to binary. <paramref name="hasScenarioPrefix"/> when true
    /// emits the scenario-style header (version + scriptCount + scripts/zero2 +
    /// unk0..unk2 + triggerCount); when false emits the standalone .trg style
    /// header (version + unk0..unk2 + triggerCount), used by .trg files.
    /// </summary>
    internal static ScenarioSection ReadTrXml(XmlReader reader, bool hasScenarioPrefix = true)
    {
        var versionAttr = reader.GetAttribute("version");
        if (string.IsNullOrEmpty(versionAttr))
            return ReadSectionXml(reader);

        int version = int.Parse(versionAttr);
        // Capture attributes before descending into children - once we
        // ReadEndElement on Triggers, attributes are no longer accessible.
        var unkAttr = reader.GetAttribute("unk") ?? "0,0,0";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(version);

        // Collect children up front so we know whether scripts are present
        // before we have to write the scriptCount placeholder.
        var scripts = new List<(string name, bool hasBody, List<string> lines)>();
        var triggers = new List<byte[]>();
        var groups = new List<(uint id, string name, string? indexes)>();

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name == "Script")
                {
                    var sName = reader.GetAttribute("name") ?? "";
                    var sHas = reader.GetAttribute("hasBody") == "1";
                    var sLines = new List<string>();
                    if (!reader.IsEmptyElement)
                    {
                        reader.Read();
                        while (reader.NodeType != XmlNodeType.EndElement)
                        {
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "Line")
                                sLines.Add(reader.ReadElementContentAsString());
                            else reader.Read();
                        }
                        reader.ReadEndElement();
                    }
                    else reader.Read();
                    scripts.Add((sName, sHas, sLines));
                }
                else if (reader.Name == "Trigger")
                    triggers.Add(ReadTriggerXml(reader, version));
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

        if (hasScenarioPrefix)
        {
            if (scripts.Count > 0)
            {
                bw.Write((uint)scripts.Count);
                for (int i = 0; i < scripts.Count; i++)
                {
                    var (name, hasBody, lines) = scripts[i];
                    WriteString16(bw, name);
                    bw.Write(hasBody ? (byte)1 : (byte)0);
                    if (hasBody)
                    {
                        bw.Write((uint)lines.Count);
                        foreach (var l in lines) WriteString16(bw, l);
                    }
                }
                // Legacy zero-separator follows only when the last script has no body
                var (_, lastHasBody, _) = scripts[^1];
                if (!lastHasBody) bw.Write(0u);
            }
            else
            {
                bw.Write(0u); // scriptCount
                bw.Write(0u); // legacy zero2
            }
        }

        var unkParts = unkAttr.Split(',');
        bw.Write(uint.Parse(unkParts[0]));
        bw.Write(uint.Parse(unkParts[1]));
        bw.Write(uint.Parse(unkParts[2]));

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

    static byte[] ReadTriggerXml(XmlReader reader, int trVersion)
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
                    conds.Add(ReadCondOrEffectXml(reader, trVersion));
                else if (reader.Name == "Effect")
                    effects.Add(ReadCondOrEffectXml(reader, trVersion));
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

    static byte[] ReadCondOrEffectXml(XmlReader reader, int trVersion)
    {
        int argTrailer = trVersion >= 12 ? 1 : 0;

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
        var argTrailsAttr = reader.GetAttribute("argTrails");
        byte[]? argTrails = argTrailsAttr != null
            ? argTrailsAttr.Split(',').Select(byte.Parse).ToArray()
            : null;

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
                    args.Add(ReadXsArgXml(reader, trVersion));
                else if (reader.Name == "Extra")
                    extras.Add(ReadExtraXml(reader));
                else reader.Skip();
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            bw.Write(args[i]);
            if (argTrailer > 0)
            {
                byte t = argTrails != null && i < argTrails.Length ? argTrails[i] : (byte)0;
                bw.Write(t);
            }
        }

        WriteString8(bw, cmd);

        bw.Write((uint)extras.Count);
        foreach (var e in extras) bw.Write(e);

        bw.Write(trail0);
        bw.Write(trail1);
        return ms.ToArray();
    }

    static byte[] ReadXsArgXml(XmlReader reader, int trVersion)
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
            case 4: // UnitIdList: count * String16 [+ v12 proto list] + bool
            {
                var values = new List<string>();
                var protos = new List<(uint id, uint magic, string name)>();
                if (!reader.IsEmptyElement)
                {
                    reader.Read();
                    while (reader.NodeType != XmlNodeType.EndElement)
                    {
                        if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                        if (reader.Name == "V")
                            values.Add(reader.ReadElementContentAsString());
                        else if (reader.Name == "Proto")
                        {
                            var pid = uint.Parse(reader.GetAttribute("id") ?? "0");
                            var pmagic = uint.Parse(reader.GetAttribute("magic") ?? "2");
                            var pname = reader.ReadElementContentAsString();
                            protos.Add((pid, pmagic, pname));
                        }
                        else reader.Skip();
                    }
                    reader.ReadEndElement();
                }
                else reader.Read();
                bw.Write((uint)values.Count);
                foreach (var v in values) WriteString16(bw, v);
                if (trVersion >= 12)
                {
                    bw.Write((uint)protos.Count);
                    foreach (var (pid, pmagic, pname) in protos)
                    {
                        bw.Write(pid);
                        bw.Write(pmagic);
                        WriteString16(bw, pname);
                    }
                }
                bw.Write(byte.Parse(flagAttr ?? "0"));
                break;
            }
            case 7: // Sound/MultiString: count + count * String16
            {
                var values = ReadVChildren(reader);
                bw.Write((uint)values.Count);
                foreach (var v in values) WriteString16(bw, v);
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
