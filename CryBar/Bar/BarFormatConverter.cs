using System;
using System.Xml;
using System.Text;
using System.Buffers.Binary;

using CommunityToolkit.HighPerformance;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBar.Bar;

public static class BarFormatConverter
{
    const int MAX_NODE_DEPTH = 512;
    const int MAX_NAME_LENGTH = 1024;

    // smallest valid encoding of each record
    const int MIN_NAME_SIZE = 6;    // length prefix + 1 char
    const int MIN_ATTRIB_SIZE = 8;  // name index + empty length prefix
    const int MIN_NODE_SIZE = 26;   // XN + 6 int32 fields

    static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        if (offset < 0 || offset > data.Length - 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    /// <summary>Character-count prefixed UTF-16 LE, bounded by remaining data so a corrupt count cannot overflow the byte length.</summary>
    static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string text, int max_chars)
    {
        text = "";

        if (!TryReadInt32(data, ref offset, out var char_count))
            return false;

        if (char_count < 0 || char_count > max_chars || char_count > (data.Length - offset) / 2)
            return false;

        if (char_count == 0)
            return true;

        text = Encoding.Unicode.GetString(data.Slice(offset, char_count * 2));
        offset += char_count * 2;
        return true;
    }

    static bool TryReadCount(ReadOnlySpan<byte> data, ref int offset, int min_item_size, int max_count, out int count)
    {
        if (!TryReadInt32(data, ref offset, out count))
            return false;

        return count >= 0 && count <= max_count && count <= (data.Length - offset) / min_item_size;
    }

    static bool TryReadNames(ReadOnlySpan<byte> data, ref int offset, int count, out List<string> names)
    {
        names = new(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadString(data, ref offset, out var name, MAX_NAME_LENGTH))
                return false;

            try
            {
                XmlConvert.VerifyName(name);
            }
            catch (XmlException) { return false; }

            names.Add(name);
        }

        return true;
    }

    static bool TryParseXmbHeader(
        ReadOnlySpan<byte> xmb_data,
        out List<string> elements,
        out List<string> attributes,
        out ReadOnlySpan<byte> nodeData)
    {
        elements = default!;
        attributes = default!;
        nodeData = default;

        if (xmb_data is not [88, 49, ..])
            return false;

        var offset = 2;
        if (!TryReadInt32(xmb_data, ref offset, out var data_length) ||
            data_length < 0 || data_length > xmb_data.Length - 6)
            return false;

        xmb_data = xmb_data.Slice(0, 6 + data_length);
        if (xmb_data.Length < offset + 2 || xmb_data.Slice(offset, 2) is not [88, 82])
            return false;
        offset += 2;

        if (!TryReadInt32(xmb_data, ref offset, out var id1) || id1 != 4)
            return false;

        if (!TryReadInt32(xmb_data, ref offset, out var version) || version != 8)
            return false;

        if (!TryReadCount(xmb_data, ref offset, MIN_NAME_SIZE, BarFile.MAX_ENTRY_COUNT, out var element_count) ||
            element_count == 0 ||
            !TryReadNames(xmb_data, ref offset, element_count, out elements))
            return false;

        if (!TryReadCount(xmb_data, ref offset, MIN_NAME_SIZE, BarFile.MAX_ENTRY_COUNT, out var attrib_count) ||
            !TryReadNames(xmb_data, ref offset, attrib_count, out attributes))
            return false;

        nodeData = xmb_data.Slice(offset);
        return true;
    }

    static bool TryReadNodeHeader(ReadOnlySpan<byte> data, ref int offset,
        List<string> elements, List<string> attributes,
        out string text, out int element_idx, out int attrib_count)
    {
        text = "";
        element_idx = 0;
        attrib_count = 0;

        if (offset + 2 > data.Length || data[offset] != 88 || data[offset + 1] != 78)
            return false;

        offset += 2;

        // node length (unused)
        if (!TryReadInt32(data, ref offset, out _))
            return false;

        if (!TryReadString(data, ref offset, out text, int.MaxValue))
            return false;

        if (!TryReadInt32(data, ref offset, out element_idx) ||
            element_idx < 0 || element_idx >= elements.Count)
            return false;

        // line number (unused)
        if (!TryReadInt32(data, ref offset, out _))
            return false;

        // a well-formed element cannot repeat an attribute name
        return TryReadCount(data, ref offset, MIN_ATTRIB_SIZE, attributes.Count, out attrib_count);
    }

    public static XmlDocument? XMBtoXML(ReadOnlySpan<byte> xmb_data)
    {
        if (!TryParseXmbHeader(xmb_data, out var elements, out var attributes, out var nodeData))
            return null;

        var document = new XmlDocument();

        var node_offset = 0;
        var root = GetNextNode(document, nodeData, ref node_offset, elements, attributes);
        if (root == null)
            return null;

        document.AppendChild(root);
        return document;

        static XmlElement? GetNextNode(XmlDocument doc, ReadOnlySpan<byte> data, ref int offset, List<string> elements, List<string> attributes, int depth = 0)
        {
            if (depth > MAX_NODE_DEPTH)
                return null;

            if (!TryReadNodeHeader(data, ref offset, elements, attributes, out var text, out var element_idx, out var attrib_count))
                return null;

            var node = doc.CreateElement(elements[element_idx]);

            // assigned even when empty: the text node it creates is what makes empty
            // elements serialize the same way XMBtoFormattedXmlString writes them
            node.InnerText = text;

            for (int i = 0; i < attrib_count; i++)
            {
                if (!TryReadInt32(data, ref offset, out var attrib_idx) ||
                    attrib_idx < 0 || attrib_idx >= attributes.Count)
                    return null;

                if (!TryReadString(data, ref offset, out var attrib_text, int.MaxValue))
                    return null;

                var attrib = doc.CreateAttribute(attributes[attrib_idx]);
                attrib.InnerText = attrib_text;
                node.Attributes.Append(attrib);
            }

            if (!TryReadCount(data, ref offset, MIN_NODE_SIZE, int.MaxValue, out var child_count))
                return null;

            for (int i = 0; i < child_count; i++)
            {
                var child = GetNextNode(doc, data, ref offset, elements, attributes, depth + 1);
                if (child == null)
                    return null;

                node.AppendChild(child);
            }

            return node;
        }
    }

    /// <summary>
    /// Converts binary XMB data to a formatted XML string.
    /// </summary>
    public static string? XMBtoFormattedXmlString(ReadOnlySpan<byte> xmb_data)
    {
        if (!TryParseXmbHeader(xmb_data, out var elements, out var attributes, out var nodeData))
            return null;

        var sb = new StringBuilder(Math.Min(nodeData.Length, 1024 * 1024));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "	",
            OmitXmlDeclaration = true
        };

        using (var writer = XmlWriter.Create(sb, settings))
        {
            var node_offset = 0;

            // corrupt values may hold characters XML cannot represent
            try
            {
                if (!WriteNextNode(writer, nodeData, ref node_offset, elements, attributes))
                    return null;
            }
            catch (ArgumentException) { return null; }
        }

        return sb.ToString();

        static bool WriteNextNode(XmlWriter writer, ReadOnlySpan<byte> data, ref int offset, List<string> elements, List<string> attributes, int depth = 0)
        {
            if (depth > MAX_NODE_DEPTH)
                return false;

            if (!TryReadNodeHeader(data, ref offset, elements, attributes, out var text, out var element_idx, out var attrib_count))
                return false;

            writer.WriteStartElement(elements[element_idx]);

            // read all attributes first, letting later duplicates overwrite earlier ones
            // (matches XmlDocument.Attributes.Append behavior for malformed XMB data)
            var attribs = attrib_count == 0 ? [] : new (int idx, string text)[attrib_count];
            int attrib_write_count = 0;

            for (int i = 0; i < attrib_count; i++)
            {
                if (!TryReadInt32(data, ref offset, out var attrib_idx) ||
                    attrib_idx < 0 || attrib_idx >= attributes.Count)
                    return false;

                if (!TryReadString(data, ref offset, out var attrib_text, int.MaxValue))
                    return false;

                bool found = false;
                for (int j = 0; j < attrib_write_count; j++)
                {
                    if (attribs[j].idx == attrib_idx) { attribs[j] = (attrib_idx, attrib_text); found = true; break; }
                }
                if (!found) attribs[attrib_write_count++] = (attrib_idx, attrib_text);
            }

            for (int i = 0; i < attrib_write_count; i++)
                writer.WriteAttributeString(attributes[attribs[i].idx], attribs[i].text);

            // must follow the attributes and precede the children
            if (text.Length > 0)
                writer.WriteString(text);

            if (!TryReadCount(data, ref offset, MIN_NODE_SIZE, int.MaxValue, out var child_count))
                return false;

            for (int i = 0; i < child_count; i++)
            {
                if (!WriteNextNode(writer, data, ref offset, elements, attributes, depth + 1))
                    return false;
            }

            writer.WriteFullEndElement();
            return true;
        }
    }

    public static string FormatXML(XmlDocument xml)
    {
        var sb = new StringBuilder();
        var rsettings = new XmlReaderSettings
        {
            IgnoreWhitespace = true
        };

        var wsettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = true
        };

        // for some reason I gotta read it first while ignoring whitespaces, to get proper formatting when writing it again... is there a better way?
        using (var reader = XmlReader.Create(new StringReader(xml.InnerXml), rsettings))
        using (var writer = XmlWriter.Create(sb, wsettings))
        {
            writer.WriteNode(reader, true);
        }

        return sb.ToString();
    }

    public static Memory<byte> XMLtoXMB(XmlDocument xml, CompressionType compression = CompressionType.Alz4)
    {
        if (xml.DocumentElement == null || xml.FirstChild == null)
            throw new Exception("Invalid XML file, no root element found");

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        // X1
        writer.Write((byte)88);
        writer.Write((byte)49);

        // Data length (int32)
        writer.Write(0);

        // XR = root node
        writer.Write((byte)88);
        writer.Write((byte)82);

        // id1
        writer.Write(4);

        // version
        writer.Write(8);

        // get all elements and attributes (sorted by order of appearance)
        var elements = new List<string>();
        var elementIndex = new Dictionary<string, int>();
        var attributes = new List<string>();
        var attributeIndex = new Dictionary<string, int>();
        FindNames(xml.DocumentElement, elements, elementIndex, attributes, attributeIndex);

        // elements
        writer.Write(elements.Count);
        for (int i = 0; i < elements.Count; ++i)
        {
            writer.Write(elements[i].Length);
            writer.Write(Encoding.Unicode.GetBytes(elements[i]));
        }

        // attributes
        writer.Write(attributes.Count);
        for (int i = 0; i < attributes.Count; ++i)
        {
            writer.Write(attributes[i].Length);
            writer.Write(Encoding.Unicode.GetBytes(attributes[i]));
        }

        // write all nodes
        WriteNode(xml.FirstChild, writer, elementIndex, attributeIndex);
        
        // fill out the data length
        int data_length = (int)(memory.Position - (2 + 4)); // (XR + data length) size is subtracted
        writer.BaseStream.Seek(2, SeekOrigin.Begin);
        writer.Write(data_length);

        var underlying_memory = memory.GetBuffer().AsMemory(0, (int)memory.Length);

        switch (compression)
        {
            default:
                return memory.ToArray(); // make a copy because stream will be disposed after this

            case CompressionType.Alz4:
                return BarCompression.CompressAlz4(underlying_memory.Span);

            case CompressionType.L33t:
                return BarCompression.CompressL33t(underlying_memory.Span);
        }

        static void FindNames(XmlNode node, List<string> elements, Dictionary<string, int> elementIndex, List<string> attributes, Dictionary<string, int> attributeIndex)
        {
            // handle element
            if (!elementIndex.ContainsKey(node.Name))
            {
                elementIndex[node.Name] = elements.Count;
                elements.Add(node.Name);
            }

            // handle attributes
            if (node.Attributes != null)
                foreach (XmlAttribute? attr in node.Attributes)
                    if (attr != null && !attributeIndex.ContainsKey(attr.Name))
                    {
                        attributeIndex[attr.Name] = attributes.Count;
                        attributes.Add(attr.Name);
                    }

            // handle children
            foreach (XmlNode? child in node.ChildNodes)
                if (child?.NodeType == XmlNodeType.Element)
                    FindNames(child, elements, elementIndex, attributes, attributeIndex);
        }

        static void WriteNode(XmlNode node, BinaryWriter writer, Dictionary<string, int> elementIndex, Dictionary<string, int> attributeIndex)
        {
            // XN
            writer.Write((byte)88);
            writer.Write((byte)78);

            // node length (will fill in later)
            writer.Write(0);
            var node_start_offset = writer.BaseStream.Position;

            // inner text
            if (node.HasChildNodes &&
                node.FirstChild?.NodeType == XmlNodeType.Text &&
                node.FirstChild.Value?.Length > 0)
            {
                var text = node.FirstChild.Value;
                writer.Write(text.Length);
                writer.Write(Encoding.Unicode.GetBytes(text));
            }
            else
            {
                writer.Write(0);
            }

            // name id
            writer.Write(elementIndex[node.Name]);

            // line num (original files don't use this, so we leave 0)
            writer.Write(0);

            // node attributes
            var attribute_count = node.Attributes?.Count ?? 0;
            writer.Write(attribute_count);
            for (int i = 0; i < attribute_count; i++)
            {
                var attribute = node.Attributes![i];
                writer.Write(attributeIndex[attribute.Name]);
                writer.Write(attribute.InnerText.Length);
                writer.Write(Encoding.Unicode.GetBytes(attribute.InnerText));
            }

            // node children
            int element_count = 0;
            int child_count = node.ChildNodes.Count;
            for (int i = 0; i < child_count; i++)
                if (node.ChildNodes[i]?.NodeType == XmlNodeType.Element)
                    element_count++;

            writer.Write(element_count);
            for (int i = 0; i < child_count; i++)
            {
                var child = node.ChildNodes[i];
                if (child?.NodeType == XmlNodeType.Element)
                {
                    WriteNode(child, writer, elementIndex, attributeIndex);
                }
            }

            // fill in node-length from before
            var node_end_offset = writer.BaseStream.Position;
            int node_length = (int)(node_end_offset - node_start_offset);
            writer.BaseStream.Seek(node_start_offset - 4, SeekOrigin.Begin);
            writer.Write(node_length);

            // continue from before
            writer.BaseStream.Seek(node_end_offset, SeekOrigin.Begin);
        }
    }

    public static async Task<Image<Rgba32>?> ParseDDT(DDTImage ddt, 
        int mipmap_index = 0, int max_resolution = -1,
        CancellationToken token = default)
    {
        if (!ddt.HeaderParsed && !ddt.ParseHeader()) return null;
        if (max_resolution > 0)
        {
            // find mipmap that is closest to max_resolution
            for (int i = 0; i < ddt.MipmapOffsets!.Length; i++)
            {
                var mipmap = ddt.MipmapOffsets[i];
                if (mipmap.Item3 > max_resolution ||
                    mipmap.Item4 > max_resolution) 
                    continue;

                mipmap_index = i;
                break;
            }
        }

        return await ddt.DecodeMipmapToImage(mipmap_index, token);
    }
}
