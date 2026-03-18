using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar;

/// <summary>
/// Parses .mythscn scenario files (Age of Mythology: Retold).
/// After L33t decompression, the binary data is a flat list of tagged sections.
/// Each section: [2-byte ASCII marker] [4-byte uint32 LE size] [size bytes of data].
/// </summary>
public partial class ScenarioFile
{
    [GeneratedRegex(@"(?<=>)[A-Za-z0-9+/=\r\n]{60,}(?=<)")]
    private static partial Regex Base64ContentRegex();

    /// <summary>
    /// Replaces long base64 element content with a placeholder for preview display.
    /// </summary>
    public static string StripBinaryForPreview(string xml) =>
        Base64ContentRegex().Replace(xml, "[BINARY DATA]");

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

    public ScenarioJ1? GetJ1()
    {
        var j1Section = FindSection("J1");
        if (j1Section == null) return null;
        return new ScenarioJ1(j1Section.Data);
    }

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

    public string ToXml()
    {
        if (!Parsed) throw new InvalidOperationException("Cannot convert unparsed scenario");

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = false, NewLineHandling = NewLineHandling.Entitize };
        var totalBytes = 0L;
        foreach (var s in Sections) totalBytes += s.Data.Length;
        var sb = new StringBuilder(Math.Max(1024, (int)Math.Min(totalBytes * 2, 50_000_000)));
        using var writer = XmlWriter.Create(sb, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("Scenario");
        writer.WriteAttributeString("version", Version.ToString());

        foreach (var section in Sections)
        {
            switch (section.Marker)
            {
                case "J1": WriteJ1Xml(writer, section); break;
                case "TR": WriteTrXml(writer, section); break;
                case "PL": WritePlXml(writer, section); break;
                case "FH": WriteFhXml(writer, section); break;
                case "CM": WriteCmXml(writer, section); break;
                case "CT": WriteCtXml(writer, section); break;
                default: WriteSectionXml(writer, section); break;
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sb.ToString();
    }

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
                    case "CameraConfig": sections.Add(ReadCmXml(reader)); break;
                    case "CameraTracks": sections.Add(ReadCtXml(reader)); break;
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

    #region Dispatch

    static void WriteSectionXml(XmlWriter writer, ScenarioSection section)
    {
        writer.WriteStartElement("S");
        writer.WriteAttributeString("m", section.Marker);

        if (section.Data.Length == 0) { }
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

        int tmIndex = 0;
        foreach (var sub in j1.Sections)
        {
            switch (sub.Marker)
            {
                case "TM" or "PT": WriteTmXml(writer, sub, tmIndex++); break;
                case "Z1": WriteZ1Xml(writer, sub); break;
                case "TN": WriteTnXml(writer, sub); break;
                case "PL": WritePlXml(writer, sub); break;
                case "FH": WriteFhXml(writer, sub); break;
                case "RN": WriteRnXml(writer, sub); break;
                case "RM": WriteRmXml(writer, sub); break;
                case "W1": WriteW1Xml(writer, sub); break;
                case "W4": WriteW4Xml(writer, sub); break;
                case "W5": WriteW5Xml(writer, sub); break;
                case "W6": WriteW6Xml(writer, sub); break;
                case "W7": WriteW7Xml(writer, sub); break;
                case "W8": WriteW8Xml(writer, sub); break;
                case "W9": WriteW9Xml(writer, sub); break;
                default: WriteSectionXml(writer, sub); break;
            }
        }

        writer.WriteEndElement();
    }

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
                    case "Teams": subSections.Add(ReadW1Xml(reader)); break;
                    case "W4": subSections.Add(ReadW4Xml(reader)); break;
                    case "W5": subSections.Add(ReadW5Xml(reader)); break;
                    case "W6": subSections.Add(ReadW6Xml(reader)); break;
                    case "W7": subSections.Add(ReadW7Xml(reader)); break;
                    case "W8": subSections.Add(ReadW8Xml(reader)); break;
                    case "W9": subSections.Add(ReadW9Xml(reader)); break;
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

    #endregion

    #region Shared Helpers

    internal static string ReadMarker(ReadOnlySpan<byte> data, int off)
    {
        Span<char> chars = stackalloc char[2];
        chars[0] = (char)data[off];
        chars[1] = (char)data[off + 1];
        return new string(chars);
    }

    static string FormatFloat(float f) => f.ToString("R");

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

    static void WriteString8(BinaryWriter bw, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        bw.Write(bytes.Length + 1);
        bw.Write(bytes);
        bw.Write((byte)0);
    }

    static void WriteString16(BinaryWriter bw, string value)
    {
        bw.Write((uint)value.Length);
        foreach (var c in value)
            bw.Write((ushort)c);
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

    static void WriteFloatArray(BinaryWriter bw, string[] parts)
    {
        foreach (var p in parts)
            bw.Write(float.Parse(p));
    }

    static bool IsBase64Content(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c) || c == '+' || c == '/' || c == '=')
                return true;
        }
        return false;
    }

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

    #endregion
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

    internal static long CalculateTotalSize(IEnumerable<ScenarioSection> sections)
    {
        long total = 0;
        foreach (var s in sections)
            total += 6 + s.Data.Length;
        return total;
    }

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

    public byte[] ToBytes()
    {
        if (!Parsed) throw new InvalidOperationException("Cannot serialize unparsed J1");

        var result = new byte[4 + ScenarioSection.CalculateTotalSize(Sections)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, HeaderValue);
        ScenarioSection.WriteSections(Sections, result, 4);

        return result;
    }
}
