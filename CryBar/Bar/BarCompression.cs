using CryBar.Utilities;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using K4os.Compression.LZ4;

namespace CryBar.Bar;

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

    public static PooledBuffer DecompressAlz4Pooled(Span<byte> data)
    {
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Length is invalid: " + size_uncompressed);
        }

        var buffer = new PooledBuffer(size_uncompressed);
        try
        {
            DecompressAlz4(data, buffer.Span);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
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

        // LZ4 can produce output larger than input for small/incompressible data
        var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
        var compressed = new byte[HEADER_SIZE + maxCompressedSize];
        var c = LZ4Codec.Encode(data, compressed.AsSpan(HEADER_SIZE), LZ4Level.L11_OPT);
        if (c < 0)
        {
            throw new Exception("LZ4 compression failed unexpectedly");
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
    // L33t compression is used by .mythscn scenario files.
    //
    // Format: [l33t 4B] [uncompressedSize LE 4B] [zlib header 2B] [deflate data] [CRC32 checksum LE 4B]
    //
    // The last 4 bytes occupy the same position as zlib's Adler32, but the game replaces
    // it with a CRC32 (standard polynomial 0xEDB88320) of the entire file contents with
    // the checksum field zeroed out, stored as a little-endian uint32.
    //
    // The game validates this CRC32 on load - if it doesn't match, the scenario is rejected.
    // Because the trailing bytes are not a valid Adler32, decompression must use raw
    // DeflateStream (skipping the 2-byte zlib header and 4-byte trailer) instead of
    // ZLibStream, which would fail Adler32 validation.

    public static byte[]? DecompressL33t(Memory<byte> data)
    {
        int offset = 4;
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(offset, 4));
        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        var buffer = new byte[size_uncompressed];
        DecompressL33t(data, buffer);
        return buffer;
    }

    public static PooledBuffer DecompressL33tPooled(Memory<byte> data)
    {
        int offset = 4;
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(offset, 4));
        if (size_uncompressed > BarFile.MAX_BUFFER_SIZE || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        var buffer = new PooledBuffer(size_uncompressed);
        try
        {
            DecompressL33t(data, buffer.Span);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        return buffer;
    }

    public static int DecompressL33t(Memory<byte> data, Span<byte> output_data)
    {
        var dataSpan = data.Span;
        int offset = 4;
        int size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(dataSpan.Slice(offset, 4)); offset += 4;
        if (size_uncompressed > output_data.Length || size_uncompressed <= 0)
        {
            throw new InvalidDataException("Size is invalid: " + size_uncompressed);
        }

        // offset = 8, pointing at the zlib stream (header + deflate + checksum)
        if (offset >= data.Length)
        {
            return -1;
        }

        // Skip 2-byte zlib header, use raw DeflateStream to avoid Adler32 validation
        // (L33t format replaces the Adler32 with a CRC32 checksum)
        int deflateOffset = offset + 2; // skip zlib CMF+FLG bytes
        int deflateLen = data.Length - deflateOffset - 4; // exclude trailing 4-byte checksum

        using var memory = new ActualMemoryStream(data.Slice(deflateOffset, deflateLen));
        using var deflate = new DeflateStream(memory, CompressionMode.Decompress);
        deflate.ReadExactly(output_data.Slice(0, size_uncompressed));

        return size_uncompressed;
    }  
    public static Memory<byte> CompressL33t(Span<byte> data)
    {
        const int HEADER_SIZE = 4 + 4;

        // conservative upper bound for zlib output (well above theoretical worst case)
        var maxCompressedSize = data.Length + data.Length / 100 + 64;
        var compressed = new byte[HEADER_SIZE + maxCompressedSize];
        var cspan = compressed.AsSpan();
        var offset = 0;

        // header
        cspan[offset++] = 108;
        cspan[offset++] = 51;
        cspan[offset++] = 51;
        cspan[offset++] = 116;

        // size uncompressed
        BinaryPrimitives.WriteInt32LittleEndian(cspan.Slice(offset), data.Length); offset += 4;

        // ZLibStream writes zlib header + deflate data + adler32 checksum
        var memory = new ActualMemoryStream(compressed.AsMemory(offset));
        using (var zlib = new ZLibStream(memory, CompressionLevel.SmallestSize))
            zlib.Write(data);

        var totalLen = offset + (int)memory.Position;
        var result = compressed.AsMemory(0, totalLen);

        // Replace the zlib Adler32 (last 4 bytes) with CRC32 of the entire file
        // (with checksum field zeroed), stored as little-endian.
        // The game validates this checksum on load and rejects mismatches.
        var resultSpan = result.Span;
        resultSpan[totalLen - 4] = 0;
        resultSpan[totalLen - 3] = 0;
        resultSpan[totalLen - 2] = 0;
        resultSpan[totalLen - 1] = 0;
        var crc = ComputeCrc32(resultSpan);
        BinaryPrimitives.WriteUInt32LittleEndian(resultSpan.Slice(totalLen - 4), crc);

        return result;
    }

    public static uint ComputeCrc32(Span<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
        }
        return ~crc;
    }

    /// <summary>
    /// Verifies the CRC32 checksum stored in the last 4 bytes of an L33t file.
    /// Returns true if the stored checksum matches CRC32(file with checksum field zeroed).
    /// </summary>
    public static bool VerifyL33tChecksum(Span<byte> data)
    {
        if (data.Length < 12) return false;
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(data[^4..]);

        // Temporarily zero the checksum field to compute expected CRC32
        var tail = data[^4..];
        uint b0 = tail[0], b1 = tail[1], b2 = tail[2], b3 = tail[3];
        tail[0] = 0; tail[1] = 0; tail[2] = 0; tail[3] = 0;
        var computed = ComputeCrc32(data);
        tail[0] = (byte)b0; tail[1] = (byte)b1; tail[2] = (byte)b2; tail[3] = (byte)b3;

        return stored == computed;
    }
    #endregion

    public static Memory<byte> EnsureDecompressed(Memory<byte> buffer, out CompressionType type)
    {
        var l33 = buffer.Span.IsL33t();
        if (l33)
        {
            var ddata = DecompressL33t(buffer);
            if (ddata != null)
            {
                type = CompressionType.L33t;
                return ddata;
            }
        }
        else if (buffer.Span.IsAlz4())
        {
            var ddata = DecompressAlz4(buffer.Span);
            if (ddata != null)
            {
                type = CompressionType.Alz4;
                return ddata;
            }
        }

        type = CompressionType.None;
        return buffer;
    }

    public static PooledBuffer EnsureDecompressedPooled(PooledBuffer buffer, out CompressionType type)
    {
        var data = TryEnsureDecompressedPooled(buffer.Memory, out type);
        if (data == null) return PooledBuffer.MoveFrom(buffer);
        buffer.Dispose();
        return data;
    }
    
    public static PooledBuffer? TryEnsureDecompressedPooled(Memory<byte> buffer, out CompressionType type)
    {
        var l33 = buffer.Span.IsL33t();
        if (l33)
        {
            var ddata = DecompressL33tPooled(buffer);
            if (ddata != null)
            {
                type = CompressionType.L33t;
                return ddata;
            }
        }
        else if (buffer.Span.IsAlz4())
        {
            var ddata = DecompressAlz4Pooled(buffer.Span);
            if (ddata != null)
            {
                type = CompressionType.Alz4;
                return ddata;
            }
        }

        type = CompressionType.None;
        return null;
    }
}

public enum CompressionType
{
    None,
    L33t,
    Alz4
}
