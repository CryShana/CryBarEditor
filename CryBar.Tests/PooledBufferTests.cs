using CryBar;
using CryBar.Bar;
using CryBar.Utilities;

namespace CryBar.Tests;

public class PooledBufferTests
{
    #region Construction & Basic Access

    [Fact]
    public void Constructor_AllocatesCorrectSize()
    {
        using var buffer = new PooledBuffer(100);

        Assert.Equal(100, buffer.Length);
        Assert.Equal(100, buffer.Span.Length);
        Assert.Equal(100, buffer.Memory.Length);
    }

    [Fact]
    public void Constructor_ZeroSize_Works()
    {
        using var buffer = new PooledBuffer(0);

        Assert.Equal(0, buffer.Length);
        Assert.Equal(0, buffer.Span.Length);
    }

    [Fact]
    public void Span_CanReadAndWrite()
    {
        using var buffer = new PooledBuffer(4);
        buffer.Span[0] = 0xAA;
        buffer.Span[1] = 0xBB;
        buffer.Span[2] = 0xCC;
        buffer.Span[3] = 0xDD;

        Assert.Equal(0xAA, buffer.Span[0]);
        Assert.Equal(0xDD, buffer.Span[3]);
    }

    [Fact]
    public void Memory_CanReadAndWrite()
    {
        using var buffer = new PooledBuffer(4);
        var mem = buffer.Memory;
        mem.Span[0] = 42;

        Assert.Equal(42, buffer.Memory.Span[0]);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_PreventsFurtherAccess()
    {
        var buffer = new PooledBuffer(10);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = buffer.Span);
        Assert.Throws<ObjectDisposedException>(() => _ = buffer.Memory);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var buffer = new PooledBuffer(10);
        buffer.Dispose();
        buffer.Dispose(); // should not throw
    }

    #endregion

    #region MoveFrom

    [Fact]
    public void MoveFrom_TransfersOwnership()
    {
        var original = new PooledBuffer(16);
        original.Span[0] = 0xFF;

        var moved = PooledBuffer.MoveFrom(original);

        // Moved buffer has same content
        Assert.Equal(0xFF, moved.Span[0]);
        Assert.Equal(16, moved.Length);

        // Original can still be accessed (it points to same array)
        // but disposing original won't return to pool
        original.Dispose();

        // Moved buffer is still valid after original dispose
        Assert.Equal(0xFF, moved.Span[0]);

        moved.Dispose();
    }

    [Fact]
    public void MoveFrom_OriginalDisposeDoesNotReturnToPool()
    {
        var original = new PooledBuffer(16);
        var moved = PooledBuffer.MoveFrom(original);

        // Disposing original should NOT return the buffer to the pool
        // (only the moved buffer should)
        original.Dispose();

        // The moved buffer should still be usable
        moved.Span[0] = 42;
        Assert.Equal(42, moved.Span[0]);

        moved.Dispose();
    }

    [Fact]
    public void MoveFrom_DoubleMove_Throws()
    {
        var original = new PooledBuffer(16);
        var moved = PooledBuffer.MoveFrom(original);

        Assert.Throws<InvalidOperationException>(() => PooledBuffer.MoveFrom(original));

        moved.Dispose();
        original.Dispose();
    }

    #endregion

    #region FromFile

    [Fact]
    public async Task FromFile_ReadsCorrectContent()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            await File.WriteAllBytesAsync(tempPath, expected);

            using var buffer = await PooledBuffer.FromFile(tempPath);

            Assert.Equal(expected.Length, buffer.Length);
            Assert.True(buffer.Span.SequenceEqual(expected));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task FromFile_EmptyFile_ReturnsEmptyBuffer()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            using var buffer = await PooledBuffer.FromFile(tempPath);
            Assert.Equal(0, buffer.Length);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task FromFile_CancellationToken_Respected()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempPath, new byte[100]);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PooledBuffer.FromFile(tempPath, cts.Token).AsTask());
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    #endregion

    #region EnsureDecompressedPooled

    [Fact]
    public void EnsureDecompressedPooled_UncompressedData_MovesOwnership()
    {
        var original = new PooledBuffer(10);
        original.Span[0] = 42;

        var result = BarCompression.EnsureDecompressedPooled(original, out var type);

        Assert.Equal(CompressionType.None, type);
        Assert.Equal(42, result.Span[0]);
        Assert.Equal(10, result.Length);

        // Original is moved, dispose should be safe
        original.Dispose();
        // Result still accessible
        Assert.Equal(42, result.Span[0]);

        result.Dispose();
    }

    [Fact]
    public void EnsureDecompressedPooled_Alz4Data_DecompressesToNewBuffer()
    {
        byte[] plaintext = "Hello, World! This is test data for pooled decompression."u8.ToArray();
        var compressed = BarCompression.CompressAlz4(plaintext);

        var input = new PooledBuffer(compressed.Length);
        compressed.Span.CopyTo(input.Span);

        using var result = BarCompression.EnsureDecompressedPooled(input, out var type);
        input.Dispose();

        Assert.Equal(CompressionType.Alz4, type);
        Assert.Equal(plaintext.Length, result.Length);
        Assert.True(result.Span.SequenceEqual(plaintext));
    }

    [Fact]
    public void EnsureDecompressedPooled_MemoryOverload_UncompressedReturnsNull()
    {
        byte[] data = [1, 2, 3, 4, 5];

        var result = BarCompression.EnsureDecompressedPooled((Memory<byte>)data, out var type);

        Assert.Null(result);
        Assert.Equal(CompressionType.None, type);
    }

    [Fact]
    public void EnsureDecompressedPooled_MemoryOverload_Alz4ReturnsPooledBuffer()
    {
        byte[] plaintext = "Pooled decompression test data"u8.ToArray();
        var compressed = BarCompression.CompressAlz4(plaintext);

        using var result = BarCompression.EnsureDecompressedPooled(compressed, out var type);

        Assert.NotNull(result);
        Assert.Equal(CompressionType.Alz4, type);
        Assert.True(result.Span.SequenceEqual(plaintext));
    }

    #endregion

    #region ReadDataRawPooledAsync

    [Fact]
    public async Task ReadDataRawPooledAsync_ReadsCorrectBytes()
    {
        var streamData = new byte[100];
        byte[] expectedContent = [10, 20, 30, 40, 50];
        Array.Copy(expectedContent, 0, streamData, 25, expectedContent.Length);

        var ms = new MemoryStream(streamData);

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 25,
            SizeInArchive = 5,
            SizeUncompressed = 5
        };

        using var result = await entry.ReadDataRawPooledAsync(ms);

        Assert.Equal(5, result.Length);
        Assert.True(result.Span.SequenceEqual(expectedContent));
    }

    [Fact]
    public async Task ReadDataRawPooledAsync_MatchesNonPooledVersion()
    {
        var streamData = new byte[200];
        new Random(42).NextBytes(streamData);

        var ms = new MemoryStream(streamData);

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 50,
            SizeInArchive = 75,
            SizeUncompressed = 75
        };

        var nonPooled = entry.ReadDataRaw(ms);
        using var pooled = await entry.ReadDataRawPooledAsync(ms);

        Assert.Equal(nonPooled.Length, pooled.Length);
        Assert.True(pooled.Span.SequenceEqual(nonPooled));
    }

    #endregion

    #region DecompressAlz4Pooled / DecompressL33tPooled

    [Fact]
    public void DecompressAlz4Pooled_MatchesNonPooledVersion()
    {
        byte[] original = new byte[512];
        new Random(42).NextBytes(original);

        var compressed = BarCompression.CompressAlz4(original);

        var nonPooled = BarCompression.DecompressAlz4(compressed.Span);
        using var pooled = BarCompression.DecompressAlz4Pooled(compressed.Span);

        Assert.NotNull(nonPooled);
        Assert.Equal(nonPooled.Length, pooled.Length);
        Assert.True(pooled.Span.SequenceEqual(nonPooled));
    }

    [Fact]
    public void DecompressL33tPooled_InvalidSize_Throws()
    {
        byte[] badData = new byte[20];
        badData[0] = 108; badData[1] = 51; badData[2] = 51; badData[3] = 116; // l33t
        // Zero uncompressed size
        badData[4] = 0; badData[5] = 0; badData[6] = 0; badData[7] = 0;

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressL33tPooled(badData));
    }

    [Fact]
    public void DecompressAlz4Pooled_InvalidSize_Throws()
    {
        byte[] badData = new byte[20];
        badData[0] = 97; badData[1] = 108; badData[2] = 122; badData[3] = 52; // alz4
        // Zero uncompressed size
        badData[4] = 0; badData[5] = 0; badData[6] = 0; badData[7] = 0;

        Assert.Throws<InvalidDataException>(() => BarCompression.DecompressAlz4Pooled(badData));
    }

    #endregion
}
