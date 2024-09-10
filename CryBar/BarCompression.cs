using CryBar.Classes;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using K4os.Compression.LZ4;

namespace CryBar;

public static class BarCompression
{
    public static bool IsAlz4(this Span<byte> data) => data is [97, 108, 122, 52, ..];
    public static bool IsL33t(this Span<byte> data) => data is [108, 51, 51, 116, ..];

    #region ALZ4
    public static byte[]? DecompressAlz4(Span<byte> data)
    {
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Length is invalid: " + size_uncompressed);
        }

        var buffer = new byte[size_uncompressed];
        DecompressAlz4(data, buffer);
        return buffer;
    }

    public static int DecompressAlz4(Span<byte> data, Span<byte> output_data)
    {
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        int size_compressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        int version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));

        if (size_uncompressed > output_data.Length || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        Span<byte> compressed_data = data.Slice(16, size_compressed);
        LZ4Codec.Decode(compressed_data, output_data);
        return size_uncompressed;
    }

    public static Memory<byte> CompressAlz4(Span<byte> data)
    {
        const int HEADER_SIZE = 4 + 4 + 4 + 4;

        var compressed = new byte[HEADER_SIZE + data.Length];
        var c = LZ4Codec.Encode(data, compressed.AsSpan(HEADER_SIZE), LZ4Level.L11_OPT);
        if (c < 0)
        {
            // buffer was too small
            throw new Exception("Buffer for compression was too small, this should not occur unless compressed size was bigger than uncompressed");
        }

        var cspan = compressed.AsSpan();

        // header
        cspan[0] = 97;
        cspan[1] = 108;
        cspan[2] = 122;
        cspan[3] = 52;

        // size uncompressed
        BinaryPrimitives.WriteInt32LittleEndian(cspan.Slice(4), data.Length);

        // size compressed
        BinaryPrimitives.WriteInt32LittleEndian(cspan.Slice(8), c);

        // version
        BinaryPrimitives.WriteInt32LittleEndian(cspan.Slice(12), 1);

        return compressed.AsMemory(0, HEADER_SIZE + c);
    }
    #endregion

    #region L33T
    // NOTE: L33t compression seems to be used by .mythscn files only (that I found so far)
    // NOTE: Current L33t decompression/compression implementation not supported by AOMR (need to investigate)

    public static byte[]? DecompressL33t(Span<byte> data)
    {
        int offset = 4;
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        var buffer = new byte[size_uncompressed];
        DecompressL33t(data, buffer);
        return buffer;
    }
    public unsafe static int DecompressL33t(Span<byte> data, Span<byte> output_data)
    {
        // TODO: there is a problem in how I decompress L33t files!!! AOMR doesn't seem to recognize when I compress it again 

        int offset = 4;
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        if (size_uncompressed > output_data.Length || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        offset += 2; // skip deflate spec

        if (offset >= data.Length)
        {
            return -1;
        }

        fixed (byte* d = data.Slice(offset))
        {
            using var memory = new UnmanagedMemoryStream(d, data.Length - offset);
            using var deflate = new DeflateStream(memory, CompressionMode.Decompress);
            deflate.ReadExactly(output_data.Slice(0, size_uncompressed));
        }

        return size_uncompressed;
    }  
    public static Memory<byte> CompressL33t(Span<byte> data)
    {
        const int HEADER_SIZE = 4 + 4 + 2;

        var compressed = new byte[HEADER_SIZE + data.Length];
        var cspan = compressed.AsSpan();
        var offset = 0;

        // header
        cspan[offset++] = 108;
        cspan[offset++] = 51;
        cspan[offset++] = 51;
        cspan[offset++] = 116;

        // size uncompressed
        BinaryPrimitives.WriteInt32LittleEndian(cspan.Slice(offset), data.Length); offset += 4;
        
        // deflate spec
        cspan[offset++] = 120;
        cspan[offset++] = 156;

        var memory = new ActualMemoryStream(compressed.AsMemory(offset));
        using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal))
            deflate.Write(data);

        return compressed.AsMemory(0, offset + (int)memory.Position);  
    }
    #endregion

    public static Memory<byte> EnsureDecompressed(Memory<byte> buffer, out CompressionType type)
    {
        var data = buffer.Span;
        var l33 = data.IsL33t();
        if (l33)
        {
            var ddata = DecompressL33t(data);
            if (ddata != null)
            {
                type = CompressionType.L33t;
                return ddata;
            }
        }
        else if (data.IsAlz4())
        {
            var ddata = DecompressAlz4(data);
            if (ddata != null)
            {
                type = CompressionType.Alz4;
                return ddata;
            }
        }

        type = CompressionType.None;
        return buffer;
    }
}

public enum CompressionType
{
    None,
    L33t,
    Alz4
}
