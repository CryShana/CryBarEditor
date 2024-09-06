using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace CryBar;

public static class BarCompressionUtils
{
    public static bool IsAlz4(this Span<byte> data) => data is [97, 108, 122, 52, ..];
    public static bool IsL33t(this Span<byte> data) => data is [108, 51, 51, 116, ..];
    public static bool IsL66t(this Span<byte> data) => data is [108, 54, 54, 116, ..];

    public static int DecompressAlz4(Span<byte> data, Span<byte> output_data)
    {
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        int size_compressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        // int version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));

        if (size_uncompressed > output_data.Length)
        {
            throw new InvalidDataException("Output buffer is too small for uncompressed data");
        }

        Span<byte> compressed_data = data.Slice(16, size_compressed);
        LZ4Codec.Decode(compressed_data, output_data);
        return size_uncompressed;
    }

    public unsafe static byte[]? DecompressL33tL66t(Span<byte> data)
    {
        var l66 = data.IsL66t();

        int offset = 4;
        int size_uncompressed = l66 ? 
            (int)BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8)) : 
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

        offset += l66 ? 8 : 4;
        offset += 2; // skip deflate spec

        if (size_uncompressed > 500_000_000)
        {
            throw new InvalidDataException("Length is larger than 500MB, may be invalid?");
        }

        if (offset >= data.Length)
        {
            return null;
        }

        var buffer = new byte[size_uncompressed];
        fixed (byte* d = data.Slice(offset))
        {
            using var memory = new UnmanagedMemoryStream(d, data.Length - offset);
            using var deflate = new DeflateStream(memory, CompressionMode.Decompress);
            deflate.ReadExactly(buffer);
        }
        
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

        if (size_uncompressed > output_data.Length)
        {
            throw new InvalidDataException("Output buffer is too small for uncompressed data");
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
}
