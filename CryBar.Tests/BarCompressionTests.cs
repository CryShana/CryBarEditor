using CryBar;
using CryBar.Bar;

namespace CryBar.Tests;

public class BarCompressionTests
{
    #region Alz4 Tests

    [Fact]
    public void Alz4_Roundtrip_SmallData()
    {
        byte[] original = "Hello, World! This is test data for Alz4 compression."u8.ToArray();

        var compressed = BarCompression.CompressAlz4(original);
        var decompressed = BarCompression.DecompressAlz4(compressed.Span);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Alz4_Roundtrip_LargerData()
    {
        // Create data with repeating patterns (compresses well)
        var original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        var compressed = BarCompression.CompressAlz4(original);
        var decompressed = BarCompression.DecompressAlz4(compressed.Span);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Alz4_Roundtrip_WithSpanOverload()
    {
        byte[] original = new byte[512];
        new Random(42).NextBytes(original);

        var compressed = BarCompression.CompressAlz4(original);
        var output = new byte[original.Length];
        int bytesWritten = BarCompression.DecompressAlz4(compressed.Span, output);

        Assert.Equal(original.Length, bytesWritten);
        Assert.Equal(original, output);
    }

    [Fact]
    public void IsAlz4_ReturnsTrueForAlz4Data()
    {
        byte[] original = "Some data to compress"u8.ToArray();
        var compressed = BarCompression.CompressAlz4(original);

        Assert.True(compressed.Span.IsAlz4());
    }

    [Fact]
    public void IsAlz4_ReturnsFalseForNonAlz4Data()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.False(((Span<byte>)data).IsAlz4());
    }

    [Fact]
    public void IsAlz4_ReturnsFalseForL33tData()
    {
        byte[] original = "Some data to compress"u8.ToArray();
        var compressed = BarCompression.CompressL33t(original);

        Assert.False(compressed.Span.IsAlz4());
    }

    [Fact]
    public void Alz4_Header_ContainsCorrectMagicBytes()
    {
        byte[] original = "test"u8.ToArray();
        var compressed = BarCompression.CompressAlz4(original);
        var span = compressed.Span;

        // "alz4" = 97, 108, 122, 52
        Assert.Equal(97, span[0]);
        Assert.Equal(108, span[1]);
        Assert.Equal(122, span[2]);
        Assert.Equal(52, span[3]);
    }

    #endregion

    #region L33t Tests

    [Fact]
    public void L33t_Roundtrip_SmallData()
    {
        byte[] original = "Hello, World! This is test data for L33t compression."u8.ToArray();

        var compressed = BarCompression.CompressL33t(original);
        var decompressed = BarCompression.DecompressL33t(compressed.Span);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void L33t_Roundtrip_LargerData()
    {
        var original = new byte[4096];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);

        var compressed = BarCompression.CompressL33t(original);
        var decompressed = BarCompression.DecompressL33t(compressed.Span);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void L33t_Roundtrip_WithSpanOverload()
    {
        byte[] original = new byte[512];
        new Random(42).NextBytes(original);

        var compressed = BarCompression.CompressL33t(original);
        var output = new byte[original.Length];
        int bytesWritten = BarCompression.DecompressL33t(compressed.Span, output);

        Assert.Equal(original.Length, bytesWritten);
        Assert.Equal(original, output);
    }

    [Fact]
    public void IsL33t_ReturnsTrueForL33tData()
    {
        byte[] original = "Some data to compress"u8.ToArray();
        var compressed = BarCompression.CompressL33t(original);

        Assert.True(compressed.Span.IsL33t());
    }

    [Fact]
    public void IsL33t_ReturnsFalseForNonL33tData()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.False(((Span<byte>)data).IsL33t());
    }

    [Fact]
    public void IsL33t_ReturnsFalseForAlz4Data()
    {
        byte[] original = "Some data to compress"u8.ToArray();
        var compressed = BarCompression.CompressAlz4(original);

        Assert.False(compressed.Span.IsL33t());
    }

    [Fact]
    public void L33t_Header_ContainsCorrectMagicBytes()
    {
        byte[] original = "test"u8.ToArray();
        var compressed = BarCompression.CompressL33t(original);
        var span = compressed.Span;

        // "l33t" = 108, 51, 51, 116
        Assert.Equal(108, span[0]);
        Assert.Equal(51, span[1]);
        Assert.Equal(51, span[2]);
        Assert.Equal(116, span[3]);
    }

    #endregion

    #region EnsureDecompressed Tests

    [Fact]
    public void EnsureDecompressed_UncompressedData_ReturnsOriginal()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Memory<byte> memory = data;

        var result = BarCompression.EnsureDecompressed(memory, out var type);

        Assert.Equal(CompressionType.None, type);
        // Should return the same memory reference for uncompressed data
        Assert.True(result.Span.SequenceEqual(data));
    }

    [Fact]
    public void EnsureDecompressed_Alz4Data_DecompressesCorrectly()
    {
        byte[] original = "Test data for EnsureDecompressed with Alz4"u8.ToArray();
        var compressed = BarCompression.CompressAlz4(original);

        var result = BarCompression.EnsureDecompressed(compressed, out var type);

        Assert.Equal(CompressionType.Alz4, type);
        Assert.Equal(original, result.ToArray());
    }

    [Fact]
    public void EnsureDecompressed_L33tData_DecompressesCorrectly()
    {
        byte[] original = "Test data for EnsureDecompressed with L33t"u8.ToArray();
        var compressed = BarCompression.CompressL33t(original);

        var result = BarCompression.EnsureDecompressed(compressed, out var type);

        Assert.Equal(CompressionType.L33t, type);
        Assert.Equal(original, result.ToArray());
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void DecompressAlz4_InvalidData_ThrowsInvalidDataException()
    {
        // Create data with alz4 header but invalid uncompressed size (negative via overflow)
        byte[] badData = new byte[20];
        badData[0] = 97; badData[1] = 108; badData[2] = 122; badData[3] = 52; // alz4
        // Write an invalid (zero) uncompressed size
        badData[4] = 0; badData[5] = 0; badData[6] = 0; badData[7] = 0;

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressAlz4(badData));
    }

    [Fact]
    public void DecompressAlz4_NegativeSize_ThrowsInvalidDataException()
    {
        byte[] badData = new byte[20];
        badData[0] = 97; badData[1] = 108; badData[2] = 122; badData[3] = 52; // alz4
        // Write negative uncompressed size
        badData[4] = 0xFF; badData[5] = 0xFF; badData[6] = 0xFF; badData[7] = 0xFF; // -1

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressAlz4(badData));
    }

    [Fact]
    public void DecompressL33t_InvalidData_ThrowsInvalidDataException()
    {
        byte[] badData = new byte[20];
        badData[0] = 108; badData[1] = 51; badData[2] = 51; badData[3] = 116; // l33t
        // Write zero uncompressed size
        badData[4] = 0; badData[5] = 0; badData[6] = 0; badData[7] = 0;

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressL33t(badData));
    }

    [Fact]
    public void DecompressL33t_NegativeSize_ThrowsInvalidDataException()
    {
        byte[] badData = new byte[20];
        badData[0] = 108; badData[1] = 51; badData[2] = 51; badData[3] = 116; // l33t
        // Write negative uncompressed size
        badData[4] = 0xFF; badData[5] = 0xFF; badData[6] = 0xFF; badData[7] = 0xFF;

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressL33t(badData));
    }

    [Fact]
    public void DecompressAlz4_SpanOverload_TooSmallOutput_ThrowsInvalidDataException()
    {
        byte[] original = new byte[100];
        new Random(42).NextBytes(original);
        var compressed = BarCompression.CompressAlz4(original);

        // Provide output buffer that is too small
        var tooSmall = new byte[10];
        Assert.Throws<InvalidDataException>(() =>
            BarCompression.DecompressAlz4(compressed.Span, tooSmall));
    }

    [Fact]
    public void DecompressL33t_SpanOverload_TooSmallOutput_ThrowsInvalidDataException()
    {
        byte[] original = new byte[100];
        new Random(42).NextBytes(original);
        var compressed = BarCompression.CompressL33t(original);

        var tooSmall = new byte[10];
        Assert.Throws<InvalidDataException>(() =>
            BarCompression.DecompressL33t(compressed.Span, tooSmall));
    }

    #endregion
}
