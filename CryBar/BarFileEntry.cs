using System.Buffers.Binary;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using K4os.Compression.LZ4;

namespace CryBar;

public class BarFileEntry
{
    public long ContentOffset { get; set; }
    public int SizeUncompressed { get; set; }
    public int SizeInArchive { get; set; }
    public string? RelativePath { get; set; }
    public bool IsCompressed { get; set; }

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
    /// Parse XMB data
    /// </summary>
    /// <param name="xmb_data">XMB data (must be already decompressed)</param>
    public static string? ParseXMB(Span<byte> xmb_data)
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
            // XR not found
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

        var item_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (item_count <= 0 || item_count > BarFile.MAX_ENTRY_COUNT)
        {
            // too many items, value probably invalid
            return null;
        }

        List<string> items = new(item_count);
        for (int i = 0; i < item_count; i++)
        {
            var item_name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2;
            offset += 4;

            if (item_name_length > BarFile.MAX_TEXT_LENGTH)
            {
                // invalid item name length
                return null;
            }

            var item_name = Encoding.Unicode.GetString(xmb_data.Slice(offset, item_name_length));
            offset += item_name_length;

            items.Add(item_name);
        }

        var attrib_count = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4));
        offset += 4;

        if (attrib_count <= 0 || attrib_count > BarFile.MAX_ENTRY_COUNT)
        {
            // too many attributes, value probably invalid
            return null;
        }

        List<string> attributes = new(item_count);
        for (int i = 0; i < attrib_count; i++)
        {
            var attrib_name_length = BinaryPrimitives.ReadInt32LittleEndian(xmb_data.Slice(offset, 4)) * 2;
            offset += 4;

            if (attrib_name_length > BarFile.MAX_TEXT_LENGTH)
            {
                // invalid attribute name length
                return null;
            }

            var attrib_name = Encoding.Unicode.GetString(xmb_data.Slice(offset, attrib_name_length));
            offset += attrib_name_length;

            attributes.Add(attrib_name);
        }

        return "";
    }

    public override string ToString() => $"{RelativePath ?? "Unset path"} ({SizeInArchive} bytes)";
}