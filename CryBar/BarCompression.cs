using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace CryBar;

public static class BarCompression
{
    public static bool IsAlz4(this Span<byte> data) => data is [97, 108, 122, 52, ..];
    public static bool IsL33t(this Span<byte> data) => data is [108, 51, 51, 116, ..];
    public static bool IsL66t(this Span<byte> data) => data is [108, 54, 54, 116, ..];

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
        // int version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));

        if (size_uncompressed > output_data.Length || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        Span<byte> compressed_data = data.Slice(16, size_compressed);
        LZ4Codec.Decode(compressed_data, output_data);
        return size_uncompressed;
    }
#endregion

#region L33T / L66T
    public unsafe static byte[]? DecompressL33tL66t(Span<byte> data)
    {
        var l66 = data.IsL66t();

        int offset = 4;
        int size_uncompressed = l66 ? 
            (int)BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8)) : 
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        var buffer = new byte[size_uncompressed];
        DecompressL33tL66t(data, buffer);
        return buffer;
    }
    
    public unsafe static int DecompressL33tL66t(Span<byte> data, Span<byte> output_data)
    {
        var l66 = data.IsL66t();

        int offset = 4;
        int size_uncompressed = l66 ? 
            (int)BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8)) : 
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

        offset += l66 ? 8 : 4;
        offset += 2; // skip deflate spec

        if (size_uncompressed > output_data.Length || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);  
        }

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
#endregion
}
