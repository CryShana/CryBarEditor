using System;
using System.Xml;
using System.Text;
using System.Buffers.Binary;

namespace CryBar;

public static class BarFormatConverter
{
    public static XmlDocument? XMBtoXML(Span<byte> xmb_data)
    {
        if (xmb_data is not [88, 49, ..])
        {
            // X1 not found
            return null;
        }

        var offset = 2;
        var data_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (data_length < 0 || data_length > xmb_data.Length - 6)
        {
            // invalid data length
            return null;
        }

        // let's limit ourselves to just this data
        xmb_data = xmb_data.Slice(0, 6 + data_length);

        if (xmb_data.Slice(offset, 2) is not [88, 82])
        {
            // XR not found (marks the root node)
            return null;
        }
        offset += 2;

        var id1 = BinaryPrimitives.ReadUInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (id1 != 4)
        {
            // invalid id
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (version != 8)
        {
            // unsupported version
            return null;
        }

        var element_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (element_count <= 0 || element_count > BarFile.MAX_ENTRY_COUNT)
        {
            // too many items, value probably invalid
            return null;
        }

        // READ ELEMENTS
        List<string> elements = new(element_count);
        for (int i = 0; i < element_count; i++)
        {
            var name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2;
            offset += 4;

            if (name_length <= 0 || name_length > BarFile.MAX_TEXT_LENGTH)
            {
                // invalid item name length
                return null;
            }

            var name = Encoding.Unicode.GetString(xmb_data.Slice(offset, name_length));
            offset += name_length;
            elements.Add(name);
        }

        var attrib_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (attrib_count < 0 || attrib_count > BarFile.MAX_ENTRY_COUNT)
        {
            // too many attributes, value probably invalid
            return null;
        }

        List<string> attributes = new(element_count);
        for (int i = 0; i < attrib_count; i++)
        {
            var name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2;
            offset += 4;

            if (name_length <= 0 || name_length > BarFile.MAX_TEXT_LENGTH)
            {
                // invalid attribute name length
                return null;
            }

            var name = Encoding.Unicode.GetString(xmb_data.Slice(offset, name_length));
            offset += name_length;
            attributes.Add(name);
        }

        // PROCESS NODES
        var document = new XmlDocument();

        var node_offset = 0;
        var root = GetNextNode(document, xmb_data.Slice(offset), ref node_offset, elements, attributes);
        if (root == null)
        {
            // no root node found
            return null;
        }

        document.AppendChild(root);

        static XmlElement? GetNextNode(XmlDocument doc, Span<byte> data, ref int offset, List<string> elements, List<string> attributes)
        {
            // node is marked by XN header
            if (data is not [88, 78, ..])
            {
                // XN not found, invalid XMB format
                return null;
            }

            offset += 2;
            //var node_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            var text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2;
            offset += 4;

            if (text_length < 0 || text_length > BarFile.MAX_TEXT_LENGTH)
            {
                return null;
            }

            var text = text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, text_length));
            offset += text_length;

            var element_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            if (element_idx < 0 || element_idx >= elements.Count)
            {
                return null;
            }

            var node = doc.CreateElement(elements[element_idx]);
            node.InnerText = text;

            //int line_number = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            // ATTRIBUTES
            int attrib_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            for (int i = 0; i < attrib_count; i++)
            {
                int attrib_idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;

                if (attrib_idx < 0 || attrib_idx >= attributes.Count)
                {
                    return null;
                }

                var attrib = doc.CreateAttribute(attributes[attrib_idx]);

                var attrib_text_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2;
                offset += 4;

                if (attrib_text_length < 0 || attrib_text_length > BarFile.MAX_TEXT_LENGTH)
                {
                    return null;
                }

                var attrib_text = attrib_text_length == 0 ? "" : Encoding.Unicode.GetString(data.Slice(offset, attrib_text_length));
                offset += attrib_text_length;

                attrib.InnerText = attrib_text;
                node.Attributes.Append(attrib);
            }

            // CHILD NODES
            int child_count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;

            for (int i = 0; i < child_count; i++)
            {
                var child = GetNextNode(doc, data, ref offset, elements, attributes);
                if (child == null)
                {
                    return null;
                }

                node.AppendChild(child);
            }

            return node;
        }

        return document;
    }

    public static Memory<byte> XMLtoXMB(XmlDocument xml)
    {
        throw new NotImplementedException();
    }

    public static Memory<byte> DDTtoTGA(Span<byte> ddt_data)
    { 
        throw new NotImplementedException();
    }

    public static Memory<byte> TGAtoDDT(Span<byte> tga_data)
    {
        throw new NotImplementedException();
    }
}
