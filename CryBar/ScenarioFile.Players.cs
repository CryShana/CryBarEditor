using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace CryBar;

public partial class ScenarioFile
{
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
}
