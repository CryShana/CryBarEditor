using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using CryBar.BCnEncoder.Shared;

namespace CryBar;

/// <summary>
/// SIMD BC1 (DXT1) decoder. Bit-identical to the vendored Bc1Block.Decode reference,
/// asserted by SimdBc1DecoderParityTests. Lives outside the vendored BCnEncoder folder
/// so the upstream codec stays untouched ("modify sparingly; bug-fix only").
///
/// Strategy: scalar palette construction (only 4 entries per block, must mirror the
/// vendored float-divide rounding exactly), then PSHUFB pulls four output pixels
/// per shot using a precomputed 256-entry shuffle-mask LUT. For full 4x4 blocks the
/// shuffled vectors are written directly into the output buffer at strided offsets,
/// skipping any intermediate scratch.
/// </summary>
internal static class SimdBc1Decoder
{
    /// <summary>True when the current CPU supports the SIMD path. ARM falls through.</summary>
    public static bool IsHardwareAccelerated => Sse2.IsSupported && Ssse3.IsSupported;

    const int BlockBytes = 8;       // BC1 encoded block: c0 + c1 + indices
    const int BlockPixelBytes = 64; // 4x4 RGBA8 decoded

    /// <summary>
    /// Maps an 8-bit chunk of the BC1 colorIndices field (4 packed 2-bit indices,
    /// one per output pixel) to the PSHUFB mask that selects the corresponding
    /// 4 RGBA bytes from the 16-byte palette. Computed once at type init.
    /// </summary>
    static readonly Vector128<byte>[] ShuffleMaskLut = BuildShuffleMaskLut();

    /// <summary>
    /// floor(x / 3) for x in [0, 765] using a 24-bit magic-number divide.
    /// Replaces a float divide-by-3 + truncating cast (~14 cycles -> ~2 cycles).
    /// 0x555556 = ceil(2^24 / 3); range cap of 765 keeps the 32-bit product
    /// (max 0xFF000FFE) below 2^32, and 0xFF000FFE >> 24 = 255 matches floor(765/3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Div3Floor(int x) => (int)((uint)(x * 0x555556) >> 24);

    static Vector128<byte>[] BuildShuffleMaskLut()
    {
        var lut = new Vector128<byte>[256];
        for (int v = 0; v < 256; v++)
        {
            int i0 = (v & 3) << 2;
            int i1 = ((v >> 2) & 3) << 2;
            int i2 = ((v >> 4) & 3) << 2;
            int i3 = ((v >> 6) & 3) << 2;
            lut[v] = Vector128.Create(
                (byte)i0, (byte)(i0 + 1), (byte)(i0 + 2), (byte)(i0 + 3),
                (byte)i1, (byte)(i1 + 1), (byte)(i1 + 2), (byte)(i1 + 3),
                (byte)i2, (byte)(i2 + 1), (byte)(i2 + 2), (byte)(i2 + 3),
                (byte)i3, (byte)(i3 + 1), (byte)(i3 + 2), (byte)(i3 + 3));
        }
        return lut;
    }

    /// <summary>
    /// Decodes one BC1 block (8 bytes: c0 LE u16, c1 LE u16, indices LE u32) into
    /// 16 RGBA pixels (64 bytes, row-major). When <paramref name="useAlpha"/> is true
    /// AND c0 &lt;= c1, index 3 yields a transparent black pixel (DXT1 1-bit alpha).
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> outRgba, bool useAlpha)
    {
        if (block.Length < BlockBytes) throw new ArgumentException($"BC1 block must be at least {BlockBytes} bytes", nameof(block));
        if (outRgba.Length < BlockPixelBytes) throw new ArgumentException($"Output must be at least {BlockPixelBytes} bytes", nameof(outRgba));
        DecodeBlockCore(block, ref MemoryMarshal.GetReference(outRgba), 16, useAlpha);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void DecodeBlockCore(ReadOnlySpan<byte> block, ref byte dstRow0, int rowStride, bool useAlpha)
    {
        ushort c0Raw = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1Raw = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(2));
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4));

        // Match ColorRgb565.{R,G,B} bit-replicate semantics exactly.
        int r0 = (c0Raw >> 11) & 0x1F; r0 = (r0 << 3) | (r0 >> 2);
        int g0 = (c0Raw >> 5) & 0x3F;  g0 = (g0 << 2) | (g0 >> 4);
        int b0 = c0Raw & 0x1F;         b0 = (b0 << 3) | (b0 >> 2);

        int r1 = (c1Raw >> 11) & 0x1F; r1 = (r1 << 3) | (r1 >> 2);
        int g1 = (c1Raw >> 5) & 0x3F;  g1 = (g1 << 2) | (g1 >> 4);
        int b1 = c1Raw & 0x1F;         b1 = (b1 << 3) | (b1 >> 2);

        bool hasAlphaOrBlack = c0Raw <= c1Raw;

        // Build 16-byte palette in a stack vector so it can be shuffled directly.
        // p2/p3 reproduce Interpolation.Interpolate's `(int)((..)/(float)den)` semantics
        // via integer magic-number divide. Since channel values are 0..255, the
        // numerators 2*a+b and a+2*b stay in [0, 765] and (a+b) stays in [0, 510],
        // ranges where Div3Floor and (>> 1) are bit-identical to the float path
        // (verified exhaustively by SimdBc1DecoderParityTests' R5/G6 sweeps).
        Span<byte> palette = stackalloc byte[16];
        palette[0] = (byte)r0; palette[1] = (byte)g0; palette[2] = (byte)b0; palette[3] = 255;
        palette[4] = (byte)r1; palette[5] = (byte)g1; palette[6] = (byte)b1; palette[7] = 255;

        if (hasAlphaOrBlack)
        {
            palette[8]  = (byte)((r0 + r1) >> 1);
            palette[9]  = (byte)((g0 + g1) >> 1);
            palette[10] = (byte)((b0 + b1) >> 1);
            palette[11] = 255;

            palette[12] = 0; palette[13] = 0; palette[14] = 0;
            palette[15] = useAlpha ? (byte)0 : (byte)255;
        }
        else
        {
            palette[8]  = (byte)Div3Floor(2 * r0 + r1);
            palette[9]  = (byte)Div3Floor(2 * g0 + g1);
            palette[10] = (byte)Div3Floor(2 * b0 + b1);
            palette[11] = 255;

            palette[12] = (byte)Div3Floor(r0 + 2 * r1);
            palette[13] = (byte)Div3Floor(g0 + 2 * g1);
            palette[14] = (byte)Div3Floor(b0 + 2 * b1);
            palette[15] = 255;
        }

        if (Ssse3.IsSupported)
        {
            var paletteVec = Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(palette));
            var lut = ShuffleMaskLut;

            // Each chunk is one row of the 4x4 block (4 pixels, 16 bytes).
            // With rowStride = pixelWidth*4 we land directly in the output image,
            // with rowStride = 16 we land in a contiguous 64-byte buffer.
            var row0 = Ssse3.Shuffle(paletteVec, lut[(int)(indices & 0xFFu)]);
            var row1 = Ssse3.Shuffle(paletteVec, lut[(int)((indices >> 8) & 0xFFu)]);
            var row2 = Ssse3.Shuffle(paletteVec, lut[(int)((indices >> 16) & 0xFFu)]);
            var row3 = Ssse3.Shuffle(paletteVec, lut[(int)((indices >> 24) & 0xFFu)]);

            Unsafe.WriteUnaligned(ref dstRow0, row0);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow0, rowStride), row1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow0, rowStride * 2), row2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow0, rowStride * 3), row3);
        }
        else
        {
            DecodeBlockScalarStrided(palette, indices, ref dstRow0, rowStride);
        }
    }

    static void DecodeBlockScalarStrided(ReadOnlySpan<byte> palette, uint indices, ref byte dstRow0, int rowStride)
    {
        for (int row = 0; row < 4; row++)
        {
            ref byte rowPtr = ref Unsafe.Add(ref dstRow0, rowStride * row);
            int baseShift = row * 8;
            for (int col = 0; col < 4; col++)
            {
                int idx = (int)((indices >> (baseShift + col * 2)) & 0x3) << 2;
                int dstOff = col * 4;
                Unsafe.Add(ref rowPtr, dstOff)     = palette[idx];
                Unsafe.Add(ref rowPtr, dstOff + 1) = palette[idx + 1];
                Unsafe.Add(ref rowPtr, dstOff + 2) = palette[idx + 2];
                Unsafe.Add(ref rowPtr, dstOff + 3) = palette[idx + 3];
            }
        }
    }

    /// <summary>
    /// Decodes a full BC1 image (block-compressed mipmap) into a caller-supplied
    /// row-major RGBA8 buffer of size pixelWidth * pixelHeight * 4 bytes, untiling
    /// the 4x4 blocks. Handles non-multiple-of-4 dimensions by clipping the last
    /// row/column of blocks. Zero allocations: the caller can rent the destination
    /// from <see cref="System.Buffers.ArrayPool{T}"/> or
    /// <see cref="CryBar.Utilities.PooledBuffer"/> and reuse it across calls.
    /// </summary>
    public static void DecodeImage(ReadOnlySpan<byte> blocks, Span<byte> outputRgba, int pixelWidth, int pixelHeight, bool useAlpha)
    {
        if (pixelWidth < 0 || pixelHeight < 0)
            throw new ArgumentException($"Invalid BC1 image size {pixelWidth}x{pixelHeight}");

        int blocksWidth = (pixelWidth + 3) >> 2;
        int blocksHeight = (pixelHeight + 3) >> 2;
        long requiredBlockBytes = (long)blocksWidth * blocksHeight * BlockBytes;
        long requiredOutputBytes = (long)pixelWidth * pixelHeight * 4;
        if (blocks.Length < requiredBlockBytes)
            throw new ArgumentException($"BC1 input too short: have {blocks.Length}, need {requiredBlockBytes} for {pixelWidth}x{pixelHeight}", nameof(blocks));
        if (outputRgba.Length < requiredOutputBytes)
            throw new ArgumentException($"Output buffer too small: have {outputRgba.Length}, need {requiredOutputBytes} for {pixelWidth}x{pixelHeight}", nameof(outputRgba));

        ref byte outRef = ref MemoryMarshal.GetReference(outputRgba);
        int strideBytes = pixelWidth * 4;

        int fullBlocksY = pixelHeight >> 2;          // blocks whose 4 rows fit
        int fullBlocksX = pixelWidth >> 2;           // blocks whose 4 cols fit
        int trailRows = pixelHeight - (fullBlocksY << 2);
        int trailCols = pixelWidth - (fullBlocksX << 2);

        // Full blocks decoded straight into the destination at strided offsets.
        for (int by = 0; by < fullBlocksY; by++)
        {
            int rowByteBase = (by << 2) * strideBytes;
            int blockRowBase = by * blocksWidth;
            for (int bx = 0; bx < fullBlocksX; bx++)
            {
                int dstByteOff = rowByteBase + (bx << 4);
                DecodeBlockCore(blocks.Slice((blockRowBase + bx) * BlockBytes, BlockBytes),
                    ref Unsafe.Add(ref outRef, dstByteOff),
                    strideBytes,
                    useAlpha);
            }
        }

        // Trailing partial blocks at the right and bottom edges.
        if (trailCols > 0 || trailRows > 0)
        {
            Span<byte> scratch = stackalloc byte[BlockPixelBytes];
            int trailColBytes = trailCols * 4;

            if (trailCols > 0)
            {
                int bx = fullBlocksX;
                int baseX = bx << 2;
                for (int by = 0; by < fullBlocksY; by++)
                {
                    int blockIdx = by * blocksWidth + bx;
                    DecodeBlockCore(blocks.Slice(blockIdx * BlockBytes, BlockBytes),
                        ref MemoryMarshal.GetReference(scratch), 16, useAlpha);
                    int baseY = by << 2;
                    for (int yi = 0; yi < 4; yi++)
                    {
                        int dstOff = (baseY + yi) * strideBytes + baseX * 4;
                        scratch.Slice(yi * 16, trailColBytes).CopyTo(outputRgba.Slice(dstOff, trailColBytes));
                    }
                }
            }

            if (trailRows > 0)
            {
                int by = fullBlocksY;
                int baseY = by << 2;
                for (int bx = 0; bx < blocksWidth; bx++)
                {
                    int blockIdx = by * blocksWidth + bx;
                    DecodeBlockCore(blocks.Slice(blockIdx * BlockBytes, BlockBytes),
                        ref MemoryMarshal.GetReference(scratch), 16, useAlpha);
                    int baseX = bx << 2;
                    int colsHere = Math.Min(4, pixelWidth - baseX);
                    int colBytes = colsHere * 4;
                    for (int yi = 0; yi < trailRows; yi++)
                    {
                        int dstOff = (baseY + yi) * strideBytes + baseX * 4;
                        scratch.Slice(yi * 16, colBytes).CopyTo(outputRgba.Slice(dstOff, colBytes));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Allocating overload: decodes into a freshly-allocated <see cref="ColorRgba32"/>
    /// array. Prefer the <see cref="Span{T}"/> overload with a pooled buffer when
    /// decoding many mipmaps in succession (terrain previews, batch loaders).
    /// </summary>
    public static ColorRgba32[] DecodeImage(ReadOnlySpan<byte> blocks, int pixelWidth, int pixelHeight, bool useAlpha)
    {
        long pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelCount * 4 > int.MaxValue)
            throw new ArgumentException($"Invalid BC1 image size {pixelWidth}x{pixelHeight}");

        var output = new ColorRgba32[pixelCount];
        DecodeImage(blocks, MemoryMarshal.AsBytes(output.AsSpan()), pixelWidth, pixelHeight, useAlpha);
        return output;
    }
}
