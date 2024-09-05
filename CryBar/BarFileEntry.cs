using System.Xml;
using System.Text;
using System.Buffers.Binary;
using K4os.Compression.LZ4;
using CommunityToolkit.HighPerformance.Buffers;

namespace CryBar;

public class BarFileEntry
{
    public long ContentOffset { get; set; }
    public int SizeUncompressed { get; set; }
    public int SizeCompressed { get; set; }
    public int SizeInArchive { get; set; }
    public string RelativePath { get; set; }
    public bool IsCompressed { get; set; }

    public BarFileEntry(string relative_path)
    {
        RelativePath = relative_path;
    }

    /// <summary>
    /// Is file a XMB file? This needs to be converted to be viewed.
    /// </summary>
    public bool IsXMB => RelativePath.EndsWith(".XMB", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Is file a special DDT image file? This has to be converted to be viewed.
    /// </summary>
    public bool IsDDT => RelativePath.EndsWith(".DDT", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Is file a special DATA file? This has to be converted to be viewed.
    /// </summary>
    public bool IsData => RelativePath.EndsWith(".DATA", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Is file a regular image file?
    /// </summary>
    public bool IsImage =>
        RelativePath.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".DDT", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".TGA", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".JPEG", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".WEBM", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".AVIF", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".GIF", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".JPX", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".BMP", StringComparison.OrdinalIgnoreCase);

    public bool IsFont => RelativePath.EndsWith(".TTF", StringComparison.OrdinalIgnoreCase);
    public bool IsCache => RelativePath.EndsWith(".CACHE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Is file a human readable text file? (There could be other extensions too)
    /// </summary>
    public bool IsText =>
        RelativePath.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".XS", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".XAML", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies file data (not header) from [from] BAR stream to [to] stream.
    /// This will seek to content offset on [from] stream
    /// but won't seek at all for [to] stream, so make sure
    /// you set the position correctly for the destination
    /// </summary>
    /// <param name="from">Input stream containing BAR file content</param>
    /// <param name="to">Output stream to which data will be copied (only data)</param>
    public void CopyData(Stream from, Stream to)
    {
        from.Seek(ContentOffset, SeekOrigin.Begin);

        // copy [SizeInArchive] amount of bytes to [to] stream
        using var buffer = SpanOwner<byte>.Allocate(81920);
        var span = buffer.Span;
        var size = SizeInArchive;

        var copied_bytes = 0;
        do
        {
            var r = from.Read(span);
            if (r <= 0)
            {
                throw new Exception("Failed to read more data while copying");
            }

            to.Write(span.Slice(0, r));
            copied_bytes += r;
        } while (copied_bytes < size);
    }

    /// <summary>
    /// Reads file content from BAR stream and allocates new array for the data.
    /// <br />
    /// This data is raw and may be compressed
    /// </summary>
    public byte[] ReadDataRaw(Stream stream)
    {
        var buffer = new byte[SizeInArchive];
        ReadDataRaw(stream, buffer);
        return buffer;
    }

    /// <summary>
    /// Reads file content from BAR stream and outputs it to given Span.
    /// Make sure the span is large enough to accomodate [SizeInArchive] bytes.
    /// <br />
    /// This data is raw and may be compressed
    /// </summary>
    public void ReadDataRaw(Stream stream, Span<byte> read_data)
    {
        stream.Seek(ContentOffset, SeekOrigin.Begin);
        stream.ReadExactly(read_data.Slice(0, SizeInArchive));
    }

    public Memory<byte> ReadDataDecompressed(Stream stream)
    {
        var buffer = new byte[SizeUncompressed];
        var r = ReadDataDecompressed(stream, buffer);
        if (r == -1) return Memory<byte>.Empty;

        return buffer.AsMemory(0, r);
    }

    /// <summary>
    /// Reads file content from BAR stream and outputs it to given Span.
    /// Make sure the span is large enough to accomodate AT LEAST [SizeUncompressed] bytes.
    /// <br />
    /// This data will be decompressed before returning.
    /// </summary>
    /// <returns>Numer of bytes read. Returns -1 if data failed to be decompressed.</returns>
    public int ReadDataDecompressed(Stream stream, Span<byte> read_data)
    {
        stream.Seek(ContentOffset, SeekOrigin.Begin);
        if (!IsCompressed)
        {
            // read directly into output
            stream.ReadExactly(read_data);
            return SizeInArchive;
        }

        using var raw_data = SpanOwner<byte>.Allocate(SizeInArchive);
        var raw = raw_data.Span;
        stream.ReadExactly(raw);

        if (raw is [97, 108, 122, 52, ..])
        {
            // ALZ4 signature
            int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(4, 4));
            int size_compressed = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(8, 4));
            int version = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(12, 4));

            if (size_uncompressed > read_data.Length)
            {
                throw new InvalidDataException("Output buffer is too small for uncompressed data");
            }

            var output_data = read_data.Slice(0, size_uncompressed);

            Span<byte> compressed_data = raw.Slice(16, size_compressed);
            LZ4Codec.Decode(compressed_data, output_data);
            return size_uncompressed;
        }
        else if (raw is [108, 51, 51, 116, ..])
        {
            // L33T signature

            // TODO
            // -> int32     length
            // -> 2 byte    deflate spec
            // skips to 10 ?

            // and then for both THIS and L66T use DEFLATE STREAM to decompress
        }
        else if (raw is [108, 54, 54, 116, ..])
        {
            // L66T signature

            // TODO
            // -> int64     length
            // -> 2 byte    deflate spec
            // skips to 14 ?
        }

        return -1;
    }

    /// <summary>
    /// Convert XMB to XML data format
    /// </summary>
    /// <param name="xmb_data">XMB data (must be already decompressed)</param>
    public static XmlDocument? ConvertXMBtoXML(Span<byte> xmb_data)
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

    public override string ToString() => $"{RelativePath ?? "Unset path"} ({SizeInArchive} bytes)";
}