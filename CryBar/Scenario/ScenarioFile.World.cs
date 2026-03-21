using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
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

    // ── W4: World Settings ──
    static void WriteW4Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        // 7 u32 + vec3 + int + vec3 + vec3 + int + float + 36 ints + 35 suffix = 28+12+4+12+12+4+4+144+35 = 255
        if (data.Length < 255) { WriteSectionXml(writer, section); return; }

        int off = 0;
        writer.WriteStartElement("W4");
        var sb = new StringBuilder();
        for (int i = 0; i < 7; i++) { if (i > 0) sb.Append(','); sb.Append(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off))); off += 4; }
        writer.WriteAttributeString("unk1", sb.ToString());

        writer.WriteAttributeString("v3a", $"{FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 4, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 8, 4)))}");
        off += 12;
        writer.WriteAttributeString("unk4", BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)).ToString()); off += 4;
        writer.WriteAttributeString("v3b", $"{FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 4, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 8, 4)))}");
        off += 12;
        writer.WriteAttributeString("v3c", $"{FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 4, 4)))},{FormatFloat(BitConverter.ToSingle(data.Slice(off + 8, 4)))}");
        off += 12;
        writer.WriteAttributeString("unk7", BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)).ToString()); off += 4;
        writer.WriteAttributeString("unk8", FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))); off += 4;

        var ints36 = new StringBuilder();
        for (int i = 0; i < 36; i++) { if (i > 0) ints36.Append(','); ints36.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off))); off += 4; }
        writer.WriteAttributeString("unk9", ints36.ToString());

        writer.WriteAttributeString("suffix", Convert.ToBase64String(data.Slice(off, data.Length - off)));
        writer.WriteEndElement();
    }

    // ── W5: Settings ──
    static void WriteW5Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        // magic(4) + float(4) + 2 bools(2) + int(4) + bool(1) + 4 floats(16) + 3 ints(12) = 43
        if (data.Length < 43) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var magic = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var f1 = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
        byte b1 = data[off++], b2 = data[off++];
        var i1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        byte b3 = data[off++];

        var floats = new StringBuilder();
        for (int i = 0; i < 4; i++) { if (i > 0) floats.Append(','); floats.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))); off += 4; }
        var ints = new StringBuilder();
        for (int i = 0; i < 3; i++) { if (i > 0) ints.Append(','); ints.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off))); off += 4; }

        writer.WriteStartElement("W5");
        if (magic != 0) writer.WriteAttributeString("magic", magic.ToString());
        writer.WriteAttributeString("f1", FormatFloat(f1));
        writer.WriteAttributeString("b1", b1.ToString());
        writer.WriteAttributeString("b2", b2.ToString());
        writer.WriteAttributeString("i1", i1.ToString());
        writer.WriteAttributeString("b3", b3.ToString());
        writer.WriteAttributeString("floats", floats.ToString());
        writer.WriteAttributeString("ints", ints.ToString());
        if (off < data.Length)
            writer.WriteAttributeString("tail", Convert.ToBase64String(data[off..]));
        writer.WriteEndElement();
    }

    // ── W6: Settings ──
    static void WriteW6Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 14) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var magicA = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var magicB = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        byte b1 = data[off++];
        var f1 = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
        if (!TryReadUTF16(data, off, out var str, out off)) { WriteSectionXml(writer, section); return; }
        byte b2 = data[off++];

        writer.WriteStartElement("W6");
        if (magicA != -1) writer.WriteAttributeString("magicA", magicA.ToString());
        if (magicB != 0) writer.WriteAttributeString("magicB", magicB.ToString());
        writer.WriteAttributeString("b1", b1.ToString());
        writer.WriteAttributeString("f1", FormatFloat(f1));
        if (!string.IsNullOrEmpty(str)) writer.WriteAttributeString("str", str);
        writer.WriteAttributeString("b2", b2.ToString());
        if (off < data.Length)
            writer.WriteAttributeString("tail", Convert.ToBase64String(data[off..]));
        writer.WriteEndElement();
    }

    // ── W8: Constants ──
    static void WriteW8Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        // float(4) + short(2) + byte(1) + int(4) = 11
        if (data.Length < 11) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var f1 = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
        var s1 = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(off)); off += 2;
        byte pad = data[off++];
        var i1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        writer.WriteStartElement("W8");
        if (f1 != 1.0f) writer.WriteAttributeString("f1", FormatFloat(f1));
        if (s1 != 1) writer.WriteAttributeString("s1", s1.ToString());
        if (pad != 0) writer.WriteAttributeString("pad", pad.ToString());
        if (i1 != 3) writer.WriteAttributeString("i1", i1.ToString());
        if (off < data.Length)
            writer.WriteAttributeString("tail", Convert.ToBase64String(data[off..]));
        writer.WriteEndElement();
    }

    // ── CM: Top-level Camera/Config ──
    static void WriteCmXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        // magic(4) + 18 floats(72) + 6 ints(24) + 14 floats(56) + 10 ints(40) = 196
        if (data.Length < 196) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var magic = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        var floats1 = new StringBuilder();
        for (int i = 0; i < 18; i++) { if (i > 0) floats1.Append(','); floats1.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))); off += 4; }
        var ints1 = new StringBuilder();
        for (int i = 0; i < 6; i++) { if (i > 0) ints1.Append(','); ints1.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off))); off += 4; }
        var floats2 = new StringBuilder();
        for (int i = 0; i < 14; i++) { if (i > 0) floats2.Append(','); floats2.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off, 4)))); off += 4; }
        var ints2 = new StringBuilder();
        for (int i = 0; i < 10; i++) { if (i > 0) ints2.Append(','); ints2.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off))); off += 4; }

        writer.WriteStartElement("CameraConfig");
        if (magic != 9) writer.WriteAttributeString("magic", magic.ToString());
        writer.WriteAttributeString("f1", floats1.ToString());
        writer.WriteAttributeString("i1", ints1.ToString());
        writer.WriteAttributeString("f2", floats2.ToString());
        writer.WriteAttributeString("i2", ints2.ToString());
        if (off < data.Length)
            writer.WriteAttributeString("tail", Convert.ToBase64String(data[off..]));
        writer.WriteEndElement();
    }

    // ── W9: World + AM + ES ──
    static void WriteW9Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 20) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var magicA = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        // AM sub-section
        if (off + 6 > data.Length || data[off] != 'A' || data[off + 1] != 'M') { WriteSectionXml(writer, section); return; }
        off += 2;
        var amLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        var amData = data.Slice(off, (int)amLen);
        off += (int)amLen;

        // Middle bytes (26 pad + unk2 byte + magicC int + 2 pad)
        if (off + 33 > data.Length) { WriteSectionXml(writer, section); return; }
        var midBytes = data.Slice(off, 33); // preserve all 33 bytes exactly
        off += 33;

        // ES sub-section
        if (off + 6 > data.Length || data[off] != 'E' || data[off + 1] != 'S') { WriteSectionXml(writer, section); return; }
        off += 2;
        var esLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        int esEnd = off + (int)esLen;

        // Trailing 8 bytes after ES
        byte[]? trail8 = null;
        if (esEnd + 8 <= data.Length) trail8 = data.Slice(esEnd, 8).ToArray();

        writer.WriteStartElement("W9");
        if (magicA != -1) writer.WriteAttributeString("magicA", magicA.ToString());
        writer.WriteAttributeString("am", Convert.ToBase64String(amData));
        writer.WriteAttributeString("mid", Convert.ToBase64String(midBytes));
        writer.WriteAttributeString("es", Convert.ToBase64String(data.Slice(off, (int)esLen)));
        if (trail8 != null) writer.WriteAttributeString("trail", Convert.ToBase64String(trail8));
        writer.WriteEndElement();
    }

    // ── W1: Teams ──
    static void WriteW1Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 8) { WriteSectionXml(writer, section); return; }

        int off = 0;
        // SizeList<u32>
        var listCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        var listSb = new StringBuilder();
        for (uint i = 0; i < listCount; i++) { if (i > 0) listSb.Append(','); listSb.Append(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off))); off += 4; }
        var unk1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        writer.WriteStartElement("Teams");
        if (listSb.Length > 0) writer.WriteAttributeString("list", listSb.ToString());
        writer.WriteAttributeString("unk1", unk1.ToString());

        // Teams come in two groups separated by zero bytes
        // Read group 1: while next byte == 1, read team
        while (off < data.Length && data[off] == 1)
        {
            off++; // skip 0x01 prefix
            off = WriteTeamXml(writer, data, off);
        }

        // Separator: count zero bytes
        int sepStart = off;
        while (off < data.Length && data[off] == 0) off++;
        int sepCount = off - sepStart;
        writer.WriteStartElement("Sep");
        writer.WriteAttributeString("n", sepCount.ToString());
        writer.WriteEndElement();

        // Read group 2
        while (off < data.Length && data[off] == 1)
        {
            off++; // skip 0x01 prefix
            off = WriteTeamXml(writer, data, off);
        }

        if (off < data.Length)
        {
            writer.WriteStartElement("W1Tail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    static int WriteTeamXml(XmlWriter writer, ReadOnlySpan<byte> data, int off)
    {
        // TE sub-section: marker(2) + size(4) + [version(4) + mybTeamId(4) + name(String16) + magic1(4) + teamId(4) + unk2(float) + magic2(4)]
        if (off + 6 > data.Length) return data.Length;
        off += 2; // "TE" marker
        var teLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        int teEnd = off + (int)teLen;

        var version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var mybTeamId = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        if (!TryReadUTF16(data, off, out var name, out off)) return teEnd;
        var magic1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var teamId = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unkFloat = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
        var magic2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        writer.WriteStartElement("Team");
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("version", version.ToString());
        writer.WriteAttributeString("mybId", mybTeamId.ToString());
        writer.WriteAttributeString("id", teamId.ToString());
        if (unkFloat != 0) writer.WriteAttributeString("unk", FormatFloat(unkFloat));
        if (magic1 != 1) writer.WriteAttributeString("magic1", magic1.ToString());
        if (magic2 != -1) writer.WriteAttributeString("magic2", magic2.ToString());
        if (off < teEnd)
            writer.WriteString(Convert.ToBase64String(data.Slice(off, teEnd - off)));
        writer.WriteEndElement();
        return teEnd;
    }

    // ── W7: Fake Players + Objectives ──
    static void WriteW7Xml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 10) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var magic1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        byte pad0 = data[off++];

        // Pre-scan past FP entries to read sep and cm (attributes must precede children)
        int scanOff = off;
        for (int li = 0; li < 8 && scanOff + 4 <= data.Length; li++)
        {
            scanOff += 4; // listMagic
            for (int si = 0; si < 4; si++) { TryReadUTF16(data, scanOff, out _, out scanOff); }
            if (scanOff + 12 <= data.Length) scanOff += 12;
        }
        byte sep = scanOff < data.Length ? data[scanOff++] : (byte)1;
        int cmVal = 0;
        bool hasCm = false;
        if (scanOff + 6 <= data.Length && data[scanOff] == 'c' && data[scanOff + 1] == 'm')
        {
            scanOff += 2;
            scanOff += 4; // cmLen
            cmVal = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(scanOff));
            hasCm = true;
        }

        writer.WriteStartElement("W7");
        if (magic1 != 1) writer.WriteAttributeString("magic", magic1.ToString());
        if (pad0 != 0) writer.WriteAttributeString("pad0", pad0.ToString());
        if (sep != 1) writer.WriteAttributeString("sep", sep.ToString());
        if (hasCm) writer.WriteAttributeString("cm", cmVal.ToString());

        // 8 W7List entries: magic(4) + 4*String16 + 12 pad bytes
        for (int li = 0; li < 8; li++)
        {
            if (off + 4 > data.Length) break;
            var listMagic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;

            // Pre-scan to read pad before writing V children
            int fpScan = off;
            for (int si = 0; si < 4; si++) TryReadUTF16(data, fpScan, out _, out fpScan);
            byte[]? padBytes = null;
            if (fpScan + 12 <= data.Length)
            {
                var pb = data.Slice(fpScan, 12);
                bool allZero = true;
                for (int i = 0; i < 12; i++) if (pb[i] != 0) { allZero = false; break; }
                if (!allZero) padBytes = pb.ToArray();
            }

            writer.WriteStartElement("FP");
            if (listMagic != 3) writer.WriteAttributeString("magic", listMagic.ToString());
            if (padBytes != null) writer.WriteAttributeString("pad", Convert.ToBase64String(padBytes));

            for (int si = 0; si < 4; si++)
            {
                TryReadUTF16(data, off, out var s, out off);
                writer.WriteStartElement("V");
                if (!string.IsNullOrEmpty(s)) writer.WriteString(s);
                writer.WriteEndElement();
            }
            if (off + 12 <= data.Length) off += 12;
            writer.WriteEndElement();
        }

        // Skip sep + cm (already pre-scanned)
        off++; // sep byte
        if (hasCm) off += 2 + 4 + 4; // "cm" + len + value

        // OM objectives sub-section
        if (off + 6 <= data.Length && data[off] == 'O' && data[off + 1] == 'M')
        {
            off += 2;
            var omLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int omEnd = off + (int)omLen;
            off = WriteOmXml(writer, data, off, omEnd);
            off = omEnd;
        }

        if (off < data.Length)
        {
            writer.WriteStartElement("W7Tail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    static int WriteOmXml(XmlWriter writer, ReadOnlySpan<byte> data, int off, int end)
    {
        if (off + 12 > end) return off;
        var magic = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unk1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unk2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;

        // Pre-scan past chapters + objectives to read trailing StringIdPairs (attributes must precede children)
        int scanOff = off;
        if (scanOff + 4 <= end)
        {
            var cc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4;
            for (uint i = 0; i < cc && scanOff < end; i++)
            {
                scanOff += 4; // idx
                TryReadUTF16(data, scanOff, out _, out scanOff); // name
                TryReadUTF16(data, scanOff, out _, out scanOff); // strId
                TryReadUTF16(data, scanOff, out _, out scanOff); // text
                var lc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4;
                scanOff += (int)lc * 4;
            }
        }
        if (scanOff + 4 <= end)
        {
            var oc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4;
            for (uint i = 0; i < oc && scanOff + 4 < end; i++)
            {
                scanOff += 4; // idx
                for (int s = 0; s < 7; s++) TryReadUTF16(data, scanOff, out _, out scanOff);
                scanOff += 4 + 4 + 20 + 4 + 8; // unk2 + magicA + pad20 + unk4 + pad8
            }
        }
        // Now scanOff points to trailing StringIdPairs
        TryReadUTF16(data, scanOff, out var goalStrId, out scanOff);
        TryReadUTF16(data, scanOff, out var goalText, out scanOff);
        TryReadUTF16(data, scanOff, out var titleStrId, out scanOff);
        TryReadUTF16(data, scanOff, out var titleText, out scanOff);
        TryReadUTF16(data, scanOff, out var spot1StrId, out scanOff);
        TryReadUTF16(data, scanOff, out var spot1Text, out scanOff);
        TryReadUTF16(data, scanOff, out var spotImg, out scanOff);
        TryReadUTF16(data, scanOff, out var spot2StrId, out scanOff);
        TryReadUTF16(data, scanOff, out var spot2Text, out scanOff);

        writer.WriteStartElement("Objectives");
        if (magic != 15) writer.WriteAttributeString("magic", magic.ToString());
        writer.WriteAttributeString("unk1", unk1.ToString());
        writer.WriteAttributeString("unk2", unk2.ToString());
        if (!string.IsNullOrEmpty(goalStrId)) writer.WriteAttributeString("goalStrId", goalStrId);
        if (!string.IsNullOrEmpty(goalText)) writer.WriteAttributeString("goal", goalText);
        if (!string.IsNullOrEmpty(titleStrId)) writer.WriteAttributeString("omTitleStrId", titleStrId);
        if (!string.IsNullOrEmpty(titleText)) writer.WriteAttributeString("omTitle", titleText);
        if (!string.IsNullOrEmpty(spot1StrId)) writer.WriteAttributeString("spot1StrId", spot1StrId);
        if (!string.IsNullOrEmpty(spot1Text)) writer.WriteAttributeString("spot1", spot1Text);
        if (!string.IsNullOrEmpty(spotImg)) writer.WriteAttributeString("spotImg", spotImg);
        if (!string.IsNullOrEmpty(spot2StrId)) writer.WriteAttributeString("spot2StrId", spot2StrId);
        if (!string.IsNullOrEmpty(spot2Text)) writer.WriteAttributeString("spot2", spot2Text);

        // Chapters
        if (off + 4 > end) { writer.WriteEndElement(); return off; }
        var chapCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        for (uint i = 0; i < chapCount && off < end; i++)
        {
            var idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            TryReadUTF16(data, off, out var cName, out off);
            TryReadUTF16(data, off, out var cStrId, out off);
            TryReadUTF16(data, off, out var cText, out off);
            var cListCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            var cListSb = new StringBuilder();
            for (uint j = 0; j < cListCount; j++) { if (j > 0) cListSb.Append(','); cListSb.Append(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off))); off += 4; }

            writer.WriteStartElement("Chapter");
            writer.WriteAttributeString("idx", idx.ToString());
            if (!string.IsNullOrEmpty(cName)) writer.WriteAttributeString("name", cName);
            if (!string.IsNullOrEmpty(cStrId)) writer.WriteAttributeString("strId", cStrId);
            if (!string.IsNullOrEmpty(cText)) writer.WriteAttributeString("text", cText);
            if (cListSb.Length > 0) writer.WriteAttributeString("list", cListSb.ToString());
            writer.WriteEndElement();
        }

        // Objectives
        if (off + 4 > end) { writer.WriteEndElement(); return off; }
        var objCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        for (uint i = 0; i < objCount && off + 4 < end; i++)
        {
            var idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            TryReadUTF16(data, off, out var oName, out off);
            TryReadUTF16(data, off, out var oUnk1StrId, out off);
            TryReadUTF16(data, off, out var oUnk1Text, out off);
            TryReadUTF16(data, off, out var oTitleStrId, out off);
            TryReadUTF16(data, off, out var oTitleText, out off);
            TryReadUTF16(data, off, out var oHintStrId, out off);
            TryReadUTF16(data, off, out var oHintText, out off);
            var oUnk2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var oMagicA = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            off += 20; // Pad0<20>
            var oUnk4 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            off += 8; // Pad0<8>

            writer.WriteStartElement("Obj");
            writer.WriteAttributeString("idx", idx.ToString());
            if (!string.IsNullOrEmpty(oName)) writer.WriteAttributeString("name", oName);
            if (!string.IsNullOrEmpty(oTitleStrId)) writer.WriteAttributeString("titleStrId", oTitleStrId);
            if (!string.IsNullOrEmpty(oTitleText)) writer.WriteAttributeString("title", oTitleText);
            if (!string.IsNullOrEmpty(oHintStrId)) writer.WriteAttributeString("hintStrId", oHintStrId);
            if (!string.IsNullOrEmpty(oHintText)) writer.WriteAttributeString("hint", oHintText);
            if (!string.IsNullOrEmpty(oUnk1StrId)) writer.WriteAttributeString("unk1StrId", oUnk1StrId);
            if (!string.IsNullOrEmpty(oUnk1Text)) writer.WriteAttributeString("unk1Text", oUnk1Text);
            if (oUnk2 != 0) writer.WriteAttributeString("unk2", oUnk2.ToString());
            if (oMagicA != 0x01000000) writer.WriteAttributeString("magicA", oMagicA.ToString());
            if (oUnk4 != 0) writer.WriteAttributeString("unk4", oUnk4.ToString());
            writer.WriteEndElement();
        }

        // Skip past trailing strings (already pre-scanned)
        for (int s = 0; s < 9; s++) TryReadUTF16(data, off, out _, out off);

        writer.WriteEndElement();
        return off;
    }

    // ── CT: Camera Tracks ──
    static void WriteCtXml(XmlWriter writer, ScenarioSection section)
    {
        var data = section.Data.AsSpan();
        if (data.Length < 21) { WriteSectionXml(writer, section); return; }

        int off = 0;
        var m1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var m2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unk3 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unk4 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        var unk5 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
        byte pad = data[off++];

        // Pre-scan to find sc2 (statesCount2) at the end - attributes must precede children
        int scanOff = off;
        int sc2 = -1;
        uint scanStateCount = 0;
        if (scanOff + 4 <= data.Length)
        {
            var tc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4;
            for (uint t = 0; t < tc && scanOff + 12 <= data.Length; t++)
            {
                scanOff += 8; // unk2 + bcCount
                var bcCnt = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(scanOff - 4));
                scanOff += 4; // unk5
                var sLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(scanOff)); scanOff += 4 + sLen; // String8
                for (int b = 0; b < bcCnt && scanOff + 6 <= data.Length; b++)
                {
                    scanOff += 2; var bsz = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4 + (int)bsz;
                }
                scanOff += 4; // Magic(0)
            }
            if (scanOff + 4 <= data.Length)
            {
                scanStateCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4;
                for (uint s = 0; s < scanStateCount && scanOff + 6 <= data.Length; s++)
                {
                    scanOff += 2; var ssz = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(scanOff)); scanOff += 4 + (int)ssz;
                }
                if (scanOff + 4 <= data.Length)
                    sc2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(scanOff));
            }
        }

        writer.WriteStartElement("CameraTracks");
        if (m1 != 3) writer.WriteAttributeString("m1", m1.ToString());
        if (m2 != 3) writer.WriteAttributeString("m2", m2.ToString());
        writer.WriteAttributeString("unk3", unk3.ToString());
        writer.WriteAttributeString("unk4", unk4.ToString());
        writer.WriteAttributeString("unk5", unk5.ToString());
        if (pad != 0) writer.WriteAttributeString("pad", pad.ToString());
        if (sc2 >= 0 && sc2 != (int)scanStateCount) writer.WriteAttributeString("sc2", sc2.ToString());

        // Tracks: SizeList<CameraTrack>
        if (off + 4 > data.Length) { writer.WriteEndElement(); return; }
        var trackCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        for (uint t = 0; t < trackCount && off + 12 <= data.Length; t++)
        {
            var tUnk2 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var bcCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var tUnk5 = BitConverter.ToSingle(data.Slice(off, 4)); off += 4;
            var tName = ReadString8(data, ref off);

            writer.WriteStartElement("Track");
            writer.WriteAttributeString("name", tName);
            writer.WriteAttributeString("unk2", tUnk2.ToString());
            writer.WriteAttributeString("unk5", FormatFloat(tUnk5));

            for (int bi = 0; bi < bcCount && off + 6 <= data.Length; bi++)
            {
                off += 2; // "BC" marker
                var bcLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
                int bcEnd = off + (int)bcLen;

                var bcUnk1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
                var floatsSb = new StringBuilder();
                for (int fi = 0; fi < 13 && off + 4 <= bcEnd; fi++)
                {
                    if (fi > 0) floatsSb.Append(',');
                    floatsSb.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off, 4))));
                    off += 4;
                }

                writer.WriteStartElement("BC");
                writer.WriteAttributeString("unk1", bcUnk1.ToString());
                writer.WriteAttributeString("data", floatsSb.ToString());
                writer.WriteEndElement();
                off = bcEnd;
            }

            // Magic(0) at end
            if (off + 4 <= data.Length) off += 4;
            writer.WriteEndElement(); // Track
        }

        // Camera states: SizeList<CameraState>
        if (off + 4 > data.Length) { writer.WriteEndElement(); return; }
        var stateCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        for (uint s = 0; s < stateCount && off + 6 <= data.Length; s++)
        {
            off += 2; // "CS" marker
            var csLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
            int csEnd = off + (int)csLen;

            var csMagic = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            var stateIdx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)); off += 4;
            TryReadUTF16(data, off, out var csName, out off);
            var csFloats = new StringBuilder();
            for (int fi = 0; fi < 12 && off + 4 <= csEnd; fi++)
            {
                if (fi > 0) csFloats.Append(',');
                csFloats.Append(FormatFloat(BitConverter.ToSingle(data.Slice(off, 4))));
                off += 4;
            }

            writer.WriteStartElement("CS");
            writer.WriteAttributeString("idx", stateIdx.ToString());
            if (!string.IsNullOrEmpty(csName)) writer.WriteAttributeString("name", csName);
            if (csMagic != 0) writer.WriteAttributeString("magic", csMagic.ToString());
            writer.WriteAttributeString("data", csFloats.ToString());
            writer.WriteEndElement();
            off = csEnd;
        }

        // statesCount2 (already written as attribute via pre-scan)
        if (off + 4 <= data.Length) off += 4;

        if (off < data.Length)
        {
            writer.WriteStartElement("CtTail");
            writer.WriteString(Convert.ToBase64String(data[off..]));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
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

    static ScenarioSection ReadW4Xml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var unk1Attr = reader.GetAttribute("unk1");
        if (unk1Attr == null) return ReadSectionXml(reader);
        foreach (var v in unk1Attr.Split(',')) bw.Write(uint.Parse(v));

        foreach (var v in (reader.GetAttribute("v3a") ?? "0,0,0").Split(',')) bw.Write(float.Parse(v));
        bw.Write(int.Parse(reader.GetAttribute("unk4") ?? "0"));
        foreach (var v in (reader.GetAttribute("v3b") ?? "0,0,0").Split(',')) bw.Write(float.Parse(v));
        foreach (var v in (reader.GetAttribute("v3c") ?? "0,0,0").Split(',')) bw.Write(float.Parse(v));
        bw.Write(int.Parse(reader.GetAttribute("unk7") ?? "0"));
        bw.Write(float.Parse(reader.GetAttribute("unk8") ?? "0"));
        foreach (var v in (reader.GetAttribute("unk9") ?? "").Split(',')) bw.Write(int.Parse(v));

        var suffixAttr = reader.GetAttribute("suffix");
        if (suffixAttr != null) bw.Write(Convert.FromBase64String(suffixAttr));

        reader.Skip();
        return new ScenarioSection("W4", ms.ToArray());
    }

    // ── ReadW5 ──
    static ScenarioSection ReadW5Xml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(reader.GetAttribute("magic") ?? "0"));
        bw.Write(float.Parse(reader.GetAttribute("f1") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("b1") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("b2") ?? "0"));
        bw.Write(int.Parse(reader.GetAttribute("i1") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("b3") ?? "0"));
        foreach (var v in (reader.GetAttribute("floats") ?? "0,0,0,0").Split(',')) bw.Write(float.Parse(v));
        foreach (var v in (reader.GetAttribute("ints") ?? "0,0,0").Split(',')) bw.Write(int.Parse(v));

        var tailAttr = reader.GetAttribute("tail");
        if (tailAttr != null) bw.Write(Convert.FromBase64String(tailAttr));

        reader.Skip();
        return new ScenarioSection("W5", ms.ToArray());
    }

    // ── ReadW6 ──
    static ScenarioSection ReadW6Xml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(reader.GetAttribute("magicA") ?? "-1"));
        bw.Write(int.Parse(reader.GetAttribute("magicB") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("b1") ?? "0"));
        bw.Write(float.Parse(reader.GetAttribute("f1") ?? "0"));
        WriteString16(bw, reader.GetAttribute("str") ?? "");
        bw.Write(byte.Parse(reader.GetAttribute("b2") ?? "0"));

        var tailAttr = reader.GetAttribute("tail");
        if (tailAttr != null) bw.Write(Convert.FromBase64String(tailAttr));

        reader.Skip();
        return new ScenarioSection("W6", ms.ToArray());
    }

    // ── ReadW8 ──
    static ScenarioSection ReadW8Xml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(float.Parse(reader.GetAttribute("f1") ?? "1"));
        bw.Write(short.Parse(reader.GetAttribute("s1") ?? "1"));
        bw.Write(byte.Parse(reader.GetAttribute("pad") ?? "0"));
        bw.Write(int.Parse(reader.GetAttribute("i1") ?? "3"));

        var tailAttr = reader.GetAttribute("tail");
        if (tailAttr != null) bw.Write(Convert.FromBase64String(tailAttr));

        reader.Skip();
        return new ScenarioSection("W8", ms.ToArray());
    }

    // ── ReadCm ──
    static ScenarioSection ReadCmXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(reader.GetAttribute("magic") ?? "9"));
        foreach (var v in (reader.GetAttribute("f1") ?? "").Split(',')) bw.Write(float.Parse(v));
        foreach (var v in (reader.GetAttribute("i1") ?? "").Split(',')) bw.Write(int.Parse(v));
        foreach (var v in (reader.GetAttribute("f2") ?? "").Split(',')) bw.Write(float.Parse(v));
        foreach (var v in (reader.GetAttribute("i2") ?? "").Split(',')) bw.Write(int.Parse(v));

        var tailAttr = reader.GetAttribute("tail");
        if (tailAttr != null) bw.Write(Convert.FromBase64String(tailAttr));

        reader.Skip();
        return new ScenarioSection("CM", ms.ToArray());
    }

    // ── ReadW9 ──
    static ScenarioSection ReadW9Xml(XmlReader reader)
    {
        var magicAAttr = reader.GetAttribute("magicA");
        if (magicAAttr == null && reader.GetAttribute("am") == null) return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(magicAAttr ?? "-1"));

        var amData = Convert.FromBase64String(reader.GetAttribute("am") ?? "");
        WriteSubSection(bw, "AM", amData);

        bw.Write(Convert.FromBase64String(reader.GetAttribute("mid") ?? ""));
        bw.Write((byte)'E'); bw.Write((byte)'S');
        var esData = Convert.FromBase64String(reader.GetAttribute("es") ?? "");
        bw.Write((uint)esData.Length);
        bw.Write(esData);

        var trailAttr = reader.GetAttribute("trail");
        if (trailAttr != null) bw.Write(Convert.FromBase64String(trailAttr));

        reader.Skip();
        return new ScenarioSection("W9", ms.ToArray());
    }

    // ── ReadW1 (Teams) ──
    static ScenarioSection ReadW1Xml(XmlReader reader)
    {
        var listAttr = reader.GetAttribute("list");
        var unk1Attr = reader.GetAttribute("unk1");
        if (unk1Attr == null) return ReadSectionXml(reader);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // SizeList<u32>
        if (string.IsNullOrEmpty(listAttr))
            bw.Write(0u);
        else
        {
            var parts = listAttr.Split(',');
            bw.Write((uint)parts.Length);
            foreach (var v in parts) bw.Write(uint.Parse(v));
        }
        bw.Write(int.Parse(unk1Attr));

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "Team":
                        bw.Write((byte)1); // prefix
                        ReadTeamXml(reader, bw);
                        break;
                    case "Sep":
                    {
                        var n = int.Parse(reader.GetAttribute("n") ?? "8");
                        for (int i = 0; i < n; i++) bw.Write((byte)0);
                        reader.Skip();
                        break;
                    }
                    case "W1Tail":
                        bw.Write(Convert.FromBase64String(reader.ReadElementContentAsString().Trim()));
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        return new ScenarioSection("W1", ms.ToArray());
    }

    static void ReadTeamXml(XmlReader reader, BinaryWriter parentBw)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(reader.GetAttribute("version") ?? "16"));
        bw.Write(int.Parse(reader.GetAttribute("mybId") ?? "0"));
        WriteString16(bw, reader.GetAttribute("name") ?? "");
        bw.Write(int.Parse(reader.GetAttribute("magic1") ?? "1"));
        bw.Write(int.Parse(reader.GetAttribute("id") ?? "0"));
        bw.Write(float.Parse(reader.GetAttribute("unk") ?? "0"));
        bw.Write(int.Parse(reader.GetAttribute("magic2") ?? "-1"));

        if (!reader.IsEmptyElement)
        {
            var text = reader.ReadElementContentAsString().Trim();
            if (text.Length > 0) bw.Write(Convert.FromBase64String(text));
        }
        else reader.Read();

        WriteSubSection(parentBw, "TE", ms.ToArray());
    }

    // ── ReadW7 (Fake Players + Objectives) ──
    static ScenarioSection ReadW7Xml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(uint.Parse(reader.GetAttribute("magic") ?? "1"));
        bw.Write(byte.Parse(reader.GetAttribute("pad0") ?? "0"));
        var sepVal = int.Parse(reader.GetAttribute("sep") ?? "1");
        var cmVal = reader.GetAttribute("cm");

        var fpList = new List<(uint magic, List<string> vals, byte[]? pad)>();
        var chapters = new List<(int idx, string name, string strId, string text, string list)>();
        var objectives = new List<(int idx, string name, string titleStrId, string title, string hintStrId, string hint, string unk1StrId, string unk1Text, int unk2, uint magicA, int unk4)>();
        int omMagic = 15, omUnk1 = 0, omUnk2 = 0;
        string goalStrId = "", goalText = "", omTitleStrId = "", omTitle = "";
        string spot1StrId = "", spot1Text = "", spotImg = "", spot2StrId = "", spot2Text = "";
        byte[]? w7Tail = null;

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "FP":
                    {
                        var fpMagic = uint.Parse(reader.GetAttribute("magic") ?? "3");
                        var padAttr = reader.GetAttribute("pad");
                        byte[]? padData = padAttr != null ? Convert.FromBase64String(padAttr) : null;
                        var vals = new List<string>();
                        if (!reader.IsEmptyElement)
                        {
                            reader.Read();
                            while (reader.NodeType != XmlNodeType.EndElement)
                            {
                                if (reader.NodeType == XmlNodeType.Element && reader.Name == "V")
                                    vals.Add(reader.ReadElementContentAsString());
                                else reader.Read();
                            }
                            reader.ReadEndElement();
                        }
                        else reader.Read();
                        fpList.Add((fpMagic, vals, padData));
                        break;
                    }
                    case "Objectives":
                    {
                        omMagic = int.Parse(reader.GetAttribute("magic") ?? "15");
                        omUnk1 = int.Parse(reader.GetAttribute("unk1") ?? "0");
                        omUnk2 = int.Parse(reader.GetAttribute("unk2") ?? "0");
                        goalStrId = reader.GetAttribute("goalStrId") ?? "";
                        goalText = reader.GetAttribute("goal") ?? "";
                        omTitleStrId = reader.GetAttribute("omTitleStrId") ?? "";
                        omTitle = reader.GetAttribute("omTitle") ?? "";
                        spot1StrId = reader.GetAttribute("spot1StrId") ?? "";
                        spot1Text = reader.GetAttribute("spot1") ?? "";
                        spotImg = reader.GetAttribute("spotImg") ?? "";
                        spot2StrId = reader.GetAttribute("spot2StrId") ?? "";
                        spot2Text = reader.GetAttribute("spot2") ?? "";
                        if (!reader.IsEmptyElement)
                        {
                            reader.Read();
                            while (reader.NodeType != XmlNodeType.EndElement)
                            {
                                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                                if (reader.Name == "Chapter")
                                {
                                    chapters.Add((
                                        int.Parse(reader.GetAttribute("idx") ?? "0"),
                                        reader.GetAttribute("name") ?? "",
                                        reader.GetAttribute("strId") ?? "",
                                        reader.GetAttribute("text") ?? "",
                                        reader.GetAttribute("list") ?? ""));
                                    reader.Skip();
                                }
                                else if (reader.Name == "Obj")
                                {
                                    objectives.Add((
                                        int.Parse(reader.GetAttribute("idx") ?? "0"),
                                        reader.GetAttribute("name") ?? "",
                                        reader.GetAttribute("titleStrId") ?? "",
                                        reader.GetAttribute("title") ?? "",
                                        reader.GetAttribute("hintStrId") ?? "",
                                        reader.GetAttribute("hint") ?? "",
                                        reader.GetAttribute("unk1StrId") ?? "",
                                        reader.GetAttribute("unk1Text") ?? "",
                                        int.Parse(reader.GetAttribute("unk2") ?? "0"),
                                        uint.Parse(reader.GetAttribute("magicA") ?? "16777216"),
                                        int.Parse(reader.GetAttribute("unk4") ?? "0")));
                                    reader.Skip();
                                }
                                else reader.Skip();
                            }
                            reader.ReadEndElement();
                        }
                        else reader.Read();
                        break;
                    }
                    case "W7Tail":
                        w7Tail = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        // Write 8 FP entries
        foreach (var (fpMagic, vals, padData) in fpList)
        {
            bw.Write(fpMagic);
            for (int i = 0; i < 4; i++)
                WriteString16(bw, i < vals.Count ? vals[i] : "");
            if (padData != null) bw.Write(padData);
            else for (int i = 0; i < 12; i++) bw.Write((byte)0);
        }

        bw.Write((byte)sepVal);

        // cm sub-section
        if (cmVal != null)
        {
            using var cmMs = new MemoryStream();
            using var cmBw = new BinaryWriter(cmMs);
            cmBw.Write(int.Parse(cmVal));
            WriteSubSection(bw, "cm", cmMs.ToArray());
        }

        // OM sub-section
        using var omMs = new MemoryStream();
        using var omBw = new BinaryWriter(omMs);
        omBw.Write(omMagic);
        omBw.Write(omUnk1);
        omBw.Write(omUnk2);

        omBw.Write((uint)chapters.Count);
        foreach (var (idx, name, strId, text, list) in chapters)
        {
            omBw.Write(idx);
            WriteString16(omBw, name);
            WriteString16(omBw, strId);
            WriteString16(omBw, text);
            if (string.IsNullOrEmpty(list)) omBw.Write(0u);
            else { var parts = list.Split(','); omBw.Write((uint)parts.Length); foreach (var v in parts) omBw.Write(int.Parse(v)); }
        }

        omBw.Write((uint)objectives.Count);
        foreach (var (idx, name, titleStrId, title, hintStrId, hint, unk1StrId, unk1Text, unk2, magicA, unk4) in objectives)
        {
            omBw.Write(idx);
            WriteString16(omBw, name);
            WriteString16(omBw, unk1StrId);
            WriteString16(omBw, unk1Text);
            WriteString16(omBw, titleStrId);
            WriteString16(omBw, title);
            WriteString16(omBw, hintStrId);
            WriteString16(omBw, hint);
            omBw.Write(unk2);
            omBw.Write(magicA);
            for (int i = 0; i < 20; i++) omBw.Write((byte)0);
            omBw.Write(unk4);
            for (int i = 0; i < 8; i++) omBw.Write((byte)0);
        }

        WriteString16(omBw, goalStrId);
        WriteString16(omBw, goalText);
        WriteString16(omBw, omTitleStrId);
        WriteString16(omBw, omTitle);
        WriteString16(omBw, spot1StrId);
        WriteString16(omBw, spot1Text);
        WriteString16(omBw, spotImg);
        WriteString16(omBw, spot2StrId);
        WriteString16(omBw, spot2Text);

        WriteSubSection(bw, "OM", omMs.ToArray());

        if (w7Tail != null) bw.Write(w7Tail);

        return new ScenarioSection("W7", ms.ToArray());
    }

    // ── ReadCt (Camera Tracks) ──
    static ScenarioSection ReadCtXml(XmlReader reader)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(int.Parse(reader.GetAttribute("m1") ?? "3"));
        bw.Write(int.Parse(reader.GetAttribute("m2") ?? "3"));
        bw.Write(int.Parse(reader.GetAttribute("unk3") ?? "0"));
        bw.Write(int.Parse(reader.GetAttribute("unk4") ?? "0"));
        bw.Write(int.Parse(reader.GetAttribute("unk5") ?? "0"));
        bw.Write(byte.Parse(reader.GetAttribute("pad") ?? "0"));

        var sc2Attr = reader.GetAttribute("sc2");

        var tracks = new List<byte[]>();
        var states = new List<byte[]>();
        byte[]? ctTail = null;

        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.Name)
                {
                    case "Track":
                    {
                        using var tMs = new MemoryStream();
                        using var tBw = new BinaryWriter(tMs);
                        tBw.Write(int.Parse(reader.GetAttribute("unk2") ?? "0"));
                        var bcList = new List<byte[]>();
                        tBw.Write(0); // placeholder for bcCount
                        tBw.Write(float.Parse(reader.GetAttribute("unk5") ?? "0"));
                        WriteString8(tBw, reader.GetAttribute("name") ?? "");

                        if (!reader.IsEmptyElement)
                        {
                            reader.Read();
                            while (reader.NodeType != XmlNodeType.EndElement)
                            {
                                if (reader.NodeType == XmlNodeType.Element && reader.Name == "BC")
                                {
                                    using var bcMs = new MemoryStream();
                                    using var bcBw = new BinaryWriter(bcMs);
                                    bcBw.Write(int.Parse(reader.GetAttribute("unk1") ?? "0"));
                                    foreach (var v in (reader.GetAttribute("data") ?? "").Split(','))
                                        bcBw.Write(float.Parse(v));
                                    var bcData = bcMs.ToArray();
                                    // Write BC sub-section to track stream
                                    WriteSubSection(tBw, "BC", bcData);
                                    bcList.Add(bcData);
                                    reader.Skip();
                                }
                                else reader.Skip();
                            }
                            reader.ReadEndElement();
                        }
                        else reader.Read();

                        tBw.Write(0); // Magic(0) at end

                        // Patch bcCount
                        var trackData = tMs.ToArray();
                        BinaryPrimitives.WriteInt32LittleEndian(trackData.AsSpan(4), bcList.Count);
                        tracks.Add(trackData);
                        break;
                    }
                    case "CS":
                    {
                        using var csMs = new MemoryStream();
                        using var csBw = new BinaryWriter(csMs);
                        csBw.Write(int.Parse(reader.GetAttribute("magic") ?? "0"));
                        csBw.Write(int.Parse(reader.GetAttribute("idx") ?? "0"));
                        WriteString16(csBw, reader.GetAttribute("name") ?? "");
                        foreach (var v in (reader.GetAttribute("data") ?? "").Split(','))
                            csBw.Write(float.Parse(v));
                        states.Add(csMs.ToArray());
                        reader.Skip();
                        break;
                    }
                    case "CtTail":
                        ctTail = Convert.FromBase64String(reader.ReadElementContentAsString().Trim());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        else reader.Read();

        bw.Write((uint)tracks.Count);
        foreach (var t in tracks) bw.Write(t);

        bw.Write((uint)states.Count);
        foreach (var s in states) WriteSubSection(bw, "CS", s);

        // statesCount2
        bw.Write(sc2Attr != null ? int.Parse(sc2Attr) : states.Count);

        if (ctTail != null) bw.Write(ctTail);

        return new ScenarioSection("CT", ms.ToArray());
    }
}
