using System;
using System.Xml;
using System.Text;
using System.Buffers.Binary;

using CommunityToolkit.HighPerformance;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBar;

public static class BarFormatConverter
{
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
        var data_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)); offset += 4;

        if (data_length < 0 || data_length > xmb_data.Length - 6)
            return false;

        xmb_data = xmb_data.Slice(0, 6 + data_length);
        if (xmb_data.Length < offset + 2 || xmb_data.Slice(offset, 2) is not [88, 82])
            return false;
        offset += 2;

        var id1 = BinaryPrimitives.ReadUInt32LittleEndian(xmb_data.Slice(offset, 4)); offset += 4;
        if (id1 != 4)
            return false;

        var version = BinaryPrimitives.ReadUInt32LittleEndian(xmb_data.Slice(offset, 4)); offset += 4;
        if (version != 8)
            return false;

        var element_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)); offset += 4;
        if (element_count <= 0 || element_count > BarFile.MAX_ENTRY_COUNT)
            return false;

        elements = new(element_count);
        for (int i = 0; i < element_count; i++)
        {
            var name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2; offset += 4;
            if (name_length <= 0 || name_length > BarFile.MAX_TEXT_LENGTH)
                return false;

            var name = Encoding.Unicode.GetString(xmb_data.Slice(offset, name_length)); offset += name_length;
            elements.Add(name);
        }

        var attrib_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)); offset += 4;
        if (attrib_count < 0 || attrib_count > BarFile.MAX_ENTRY_COUNT)
            return false;

        attributes = new(attrib_count);
        for (int i = 0; i < attrib_count; i++)
        {
            var name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2; offset += 4;
            if (name_length <= 0 || name_length > BarFile.MAX_TEXT_LENGTH)
                return false;

            var name = Encoding.Unicode.GetString(xmb_data.Slice(offset, name_length)); offset += name_length;
            attributes.Add(name);
        }

        nodeData = xmb_data.Slice(offset);
        return true;
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

        static XmlElement? GetNextNode(XmlDocument doc, ReadOnlySpan<byte> data, ref int offset, List<string> elements, List<string> attributes)
        {
            // node is marked by XN header
            if (data is not [88, 78, ..])
                return null;

            offset += 2;
            //var node_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            var text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2; offset += 4;
            if (text_length < 0 || text_length > BarFile.MAX_TEXT_LENGTH)
                return null;

            var text = text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, text_length)); offset += text_length;
            var element_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            if (element_idx < 0 || element_idx >= elements.Count)
                return null;

            var node = doc.CreateElement(elements[element_idx]);
            node.InnerText = text;

            //int line_number = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            // ATTRIBUTES
            int attrib_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            for (int i = 0; i < attrib_count; i++)
            {
                int attrib_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
                if (attrib_idx < 0 || attrib_idx >= attributes.Count)
                    return null;

                var attrib = doc.CreateAttribute(attributes[attrib_idx]);
                var attrib_text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2; offset += 4;
                if (attrib_text_length < 0 || attrib_text_length > BarFile.MAX_TEXT_LENGTH)
                    return null;

                var attrib_text = attrib_text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, attrib_text_length)); offset += attrib_text_length;
                attrib.InnerText = attrib_text;
                node.Attributes.Append(attrib);
            }

            // CHILD NODES
            int child_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            for (int i = 0; i < child_count; i++)
            {
                var child = GetNextNode(doc, data, ref offset, elements, attributes);
                if (child == null)
                    return null;

                node.AppendChild(child);
            }

            return node;
        }

        return document;
    }

    /// <summary>
    /// Reads binary XMB and writes directly to XmlWriter, producing formatted XML.
    /// Single pass: binary → formatted XML string. No intermediate XmlDocument.
    /// </summary>
    public static string? XMBtoFormattedXmlString(ReadOnlySpan<byte> xmb_data)
    {
        if (!TryParseXmbHeader(xmb_data, out var elements, out var attributes, out var nodeData))
            return null;

        var sb = new StringBuilder(nodeData.Length * 3);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = true
        };

        using (var writer = XmlWriter.Create(sb, settings))
        {
            var node_offset = 0;
            if (!WriteNextNode(writer, nodeData, ref node_offset, elements, attributes))
                return null;
        }

        return sb.ToString();

        static bool WriteNextNode(XmlWriter writer, ReadOnlySpan<byte> data, ref int offset, List<string> elements, List<string> attributes)
        {
            if (offset + 2 > data.Length || data[offset] != 88 || data[offset + 1] != 78)
                return false;

            offset += 2;
            // node_length (skip)
            offset += 4;

            var text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2; offset += 4;
            if (text_length < 0 || text_length > BarFile.MAX_TEXT_LENGTH)
                return false;

            var text = text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, text_length)); offset += text_length;
            var element_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            if (element_idx < 0 || element_idx >= elements.Count)
                return false;

            writer.WriteStartElement(elements[element_idx]);

            // line number (skip)
            offset += 4;

            // ATTRIBUTES
            int attrib_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            for (int i = 0; i < attrib_count; i++)
            {
                int attrib_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
                if (attrib_idx < 0 || attrib_idx >= attributes.Count)
                    return false;

                var attrib_text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2; offset += 4;
                if (attrib_text_length < 0 || attrib_text_length > BarFile.MAX_TEXT_LENGTH)
                    return false;

                var attrib_text = attrib_text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, attrib_text_length)); offset += attrib_text_length;
                writer.WriteAttributeString(attributes[attrib_idx], attrib_text);
            }

            // INNER TEXT (after attributes, before children)
            if (text.Length > 0)
                writer.WriteString(text);

            // CHILD NODES
            int child_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            for (int i = 0; i < child_count; i++)
            {
                if (!WriteNextNode(writer, data, ref offset, elements, attributes))
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
        var attributes = new List<string>();
        FindNames(xml.DocumentElement, elements, attributes);
        
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
        WriteNode(xml.FirstChild, writer, elements, attributes);
        
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

        static void FindNames(XmlNode node, List<string> elements, List<string> attributes)
        {
            // handle element
            if (!elements.Contains(node.Name))
                elements.Add(node.Name);

            // handle attributes
            if (node.Attributes != null)
                foreach (XmlAttribute? attr in node.Attributes)
                    if (attr != null && !attributes.Contains(attr.Name))
                        attributes.Add(attr.Name);

            // handle children
            foreach (XmlNode? child in node.ChildNodes)
                if (child?.NodeType == XmlNodeType.Element)
                    FindNames(child, elements, attributes);
        }

        static void WriteNode(XmlNode node, BinaryWriter writer, List<string> elements, List<string> attributes)
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
            writer.Write(elements.IndexOf(node.Name));

            // line num (original files don't use this, so we leave 0)
            writer.Write(0);

            // node attributes
            var attribute_count = node.Attributes?.Count ?? 0;
            writer.Write(attribute_count);
            for (int i = 0; i < attribute_count; i++)
            {
                var attribute = node.Attributes![i];
                writer.Write(attributes.IndexOf(attribute.Name));
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
                    WriteNode(child, writer, elements, attributes);
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
