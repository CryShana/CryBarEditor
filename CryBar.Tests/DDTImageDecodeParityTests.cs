using System.Buffers.Binary;

using CryBar;

using static CryBar.Tests.DdtTestHelpers;

namespace CryBar.Tests;

/// <summary>
/// End-to-end parity for DDTImage.DecodeMipmap: the SIMD path (UseSimd=true) must
/// produce byte-identical pixels to the vendored path (UseSimd=false) for DXT1
/// and DXT1Alpha mipmaps. Block-level parity is asserted by SimdBc1DecoderParityTests;
/// this test additionally exercises the untile loop and the DecodeMipmap dispatch.
/// </summary>
public class DDTImageDecodeParityTests
{
    static byte[] MakeBc1BlockBytes(ushort c0, ushort c1, uint indices)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), indices);
        return b;
    }

    static byte[] HandCraftedBc1Mipmap(int blocksWidth, int blocksHeight, int seed)
    {
        // Generate one BC1 block per tile with deterministic pseudo-random content.
        // Indices and endpoints are chosen so that we hit both palette modes (c0>c1 and c0<=c1).
        var rng = new Random(seed);
        var data = new byte[blocksWidth * blocksHeight * 8];
        int offset = 0;
        for (int b = 0; b < blocksWidth * blocksHeight; b++)
        {
            ushort c0 = (ushort)rng.Next(0x10000);
            ushort c1 = (ushort)rng.Next(0x10000);
            uint indices = (uint)rng.Next() ^ ((uint)rng.Next() << 1);
            var block = MakeBc1BlockBytes(c0, c1, indices);
            Array.Copy(block, 0, data, offset, 8);
            offset += 8;
        }
        return data;
    }

    static async Task AssertSimdMatchesVendored(byte[] ddtBytes)
    {
        var prevUseSimd = DDTImage.UseSimd;
        try
        {
            DDTImage.UseSimd = true;
            var simdDdt = new DDTImage(ddtBytes);
            Assert.True(simdDdt.ParseHeader());
            var simdResult = await simdDdt.DecodeMipmap(0);
            Assert.NotNull(simdResult);

            DDTImage.UseSimd = false;
            var vendoredDdt = new DDTImage(ddtBytes);
            Assert.True(vendoredDdt.ParseHeader());
            var vendoredResult = await vendoredDdt.DecodeMipmap(0);
            Assert.NotNull(vendoredResult);

            Assert.Equal(vendoredResult!.Value.Width, simdResult!.Value.Width);
            Assert.Equal(vendoredResult.Value.Height, simdResult.Value.Height);

            // Compare row-by-row (Memory2D is not guaranteed contiguous in general, but here it is).
            int h = simdResult.Value.Height;
            for (int y = 0; y < h; y++)
            {
                var simdRow = simdResult.Value.Span.GetRowSpan(y);
                var vendRow = vendoredResult.Value.Span.GetRowSpan(y);
                for (int x = 0; x < simdRow.Length; x++)
                {
                    if (!simdRow[x].Equals(vendRow[x]))
                        Assert.Fail($"Mismatch at ({x}, {y}): SIMD={simdRow[x]} Vendored={vendRow[x]}");
                }
            }
        }
        finally
        {
            DDTImage.UseSimd = prevUseSimd;
        }
    }

    [Theory]
    [InlineData(8, 8, DDTFormat.DXT1, 0xA1)]
    [InlineData(8, 8, DDTFormat.DXT1Alpha, 0xB2)]
    [InlineData(64, 64, DDTFormat.DXT1, 0xC3)]
    [InlineData(64, 64, DDTFormat.DXT1Alpha, 0xD4)]
    [InlineData(256, 128, DDTFormat.DXT1, 0xE5)]
    [InlineData(128, 256, DDTFormat.DXT1Alpha, 0xF6)]
    public async Task DecodeMipmap_SimdMatchesVendored(int width, int height, DDTFormat format, int seed)
    {
        int blocksWidth = (width + 3) >> 2;
        int blocksHeight = (height + 3) >> 2;
        var mipmapBytes = HandCraftedBc1Mipmap(blocksWidth, blocksHeight, seed);
        var ddtBytes = BuildRts4(width, height, 1, format: format, payload: mipmapBytes);
        await AssertSimdMatchesVendored(ddtBytes);
    }

    [Theory]
    [InlineData(64, 64, DDTFormat.DXT1, 0x11)]
    [InlineData(64, 64, DDTFormat.DXT1Alpha, 0x22)]
    [InlineData(256, 128, DDTFormat.DXT1, 0x33)]
    [InlineData(128, 256, DDTFormat.DXT1Alpha, 0x44)]
    public async Task TryDecodeMipmapInto_MatchesAllocatingDecodeMipmap(int width, int height, DDTFormat format, int seed)
    {
        int blocksWidth = (width + 3) >> 2;
        int blocksHeight = (height + 3) >> 2;
        var mipmapBytes = HandCraftedBc1Mipmap(blocksWidth, blocksHeight, seed);
        var ddtBytes = BuildRts4(width, height, 1, format: format, payload: mipmapBytes);

        var prev = DDTImage.UseSimd;
        try
        {
            DDTImage.UseSimd = true;
            var ddt = new DDTImage(ddtBytes);
            Assert.True(ddt.ParseHeader());

            // Reference: allocating path.
            var allocResult = await ddt.DecodeMipmap(0);
            Assert.NotNull(allocResult);

            // Zero-alloc path: caller-supplied buffer.
            var ddt2 = new DDTImage(ddtBytes);
            Assert.True(ddt2.ParseHeader());
            var buffer = new byte[width * height * 4];
            bool ok = ddt2.TryDecodeMipmapInto(0, buffer, out int w, out int h);
            Assert.True(ok);
            Assert.Equal(width, w);
            Assert.Equal(height, h);

            // Compare row-by-row to allocating path.
            int strideBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                var allocRow = allocResult!.Value.Span.GetRowSpan(y);
                var bufRow = buffer.AsSpan(y * strideBytes, strideBytes);
                var allocRowBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(allocRow);
                Assert.True(allocRowBytes.SequenceEqual(bufRow), $"Row {y} differs");
            }
        }
        finally
        {
            DDTImage.UseSimd = prev;
        }
    }
}
