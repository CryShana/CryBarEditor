using CommunityToolkit.HighPerformance;

using CryBar.BCnEncoder.Shared;
using CryBar.BCnEncoder.Encoder;
using CryBar.BCnEncoder.Decoder;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

using System.Text;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Diagnostics.CodeAnalysis;
using CryBar.Utilities;

namespace CryBar;

public enum DDTVersion
{
    RTS3 = 0,
    RTS4 = 1
};

public enum DDTUsage : byte
{
    None = 0,
    AlphaTest = 1,
    LowDetail = 2,
    Bump = 4,
    Cube = 8
}

public enum DDTAlpha : byte
{
    None = 0,
    Player = 1,
    Transparent = 4,
    Blend = 8
}

public enum DDTFormat : byte
{
    None = 0,
    Bgra = 1,
    DXT1 = 4,
    DXT1Alpha = 5,
    Grey = 7,
    DXT3 = 8,
    DXT5 = 9
    // others... I know there is [3]
}

public class DDTImage
{
    /// Master switch for SIMD paths (bilinear resample, BC1 decode). Set false
    /// to force the scalar reference if a regression is suspected.
    public static bool UseSimd { get; set; } = true;

    static bool IsBc1Format(DDTFormat f) => f == DDTFormat.DXT1 || f == DDTFormat.DXT1Alpha;

    public DDTVersion Version { get; private set; }
    public bool HeaderParsed { get; private set; }

    public DDTUsage UsageFlag { get; private set; }
    public DDTAlpha AlphaFlag { get; private set; }
    public DDTFormat FormatFlag { get; private set; }
    public byte MipmapLevels { get; private set; }
    public ReadOnlyMemory<byte>? ColorTable { get; private set; }

    public ushort BaseWidth { get; private set; }
    public ushort BaseHeight { get; private set; }

    public (int, int, ushort, ushort)[]? MipmapOffsets { get; private set; }

    readonly ReadOnlyMemory<byte> _data;

    /// Pass copyData=false only when the caller guarantees `data`'s backing
    /// storage outlives this DDTImage and any ReadOnlyMemory slices it returns
    /// (ColorTable, ReadMipmap). Skipping the copy matters for short-lived
    /// terrain-tile decodes where the memcpy dominates wall time.
    public DDTImage(ReadOnlyMemory<byte> data, bool copyData = true)
    {
        if (copyData)
        {
            var copy = new byte[data.Length];
            data.CopyTo(copy);
            _data = copy;
        }
        else
        {
            _data = data;
        }
    }

    [MemberNotNullWhen(true, nameof(MipmapOffsets))]
    public bool ParseHeader()
    {
        var data = _data.Span;
        if (data.Length < 16) return false;

        var rts4 = data is [0x52, 0x54, 0x53, 0x34, ..];
        var rts3 = data is [0x52, 0x54, 0x53, 0x33, ..];
        if (!rts4 && !rts3) return false;

        var offset = 4;

        // image info
        var usage = data[offset++];
        var alpha = data[offset++];
        var format = data[offset++];
        var mipmap_levels = data[offset++];
        if (mipmap_levels == 0) return false;

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        if (width <= 0 || width > ushort.MaxValue || height <= 0 || height > ushort.MaxValue) return false;

        // color table (RTS4 only):
        ReadOnlyMemory<byte>? color_table = null;
        if (rts4)
        {
            if (data.Length - offset < 4) return false;
            int color_table_size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            if (color_table_size < 0 || color_table_size > data.Length - offset) return false;
            color_table = _data.Slice(offset, color_table_size); offset += color_table_size;
        }

        // read mipmaps
        int images_per_level = 1; // (usage & 8) == 8 ? 6 : 1; // there's more images when usage is 8 = [Cube] - I HAVE NOT ENCOUNTERED THIS YET, let's assume 1 for now
        var mipmap_image_count = mipmap_levels * images_per_level;
        if ((long)mipmap_image_count * 8 > data.Length - offset) return false;

        var mipmap_offsets = new (int, int, ushort, ushort)[mipmap_image_count];
        for (int i = 0; i < mipmap_image_count; i++)
        {
            var level = i / images_per_level;
            var image_width = MipDimension(width, level);
            var image_height = MipDimension(height, level);
            var image_offset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            var image_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
            if (image_offset < 0 || image_length < 0 || image_offset > data.Length - image_length) return false;
            mipmap_offsets[i] = (image_offset, image_length, image_width, image_height);
        }

        Version = rts4 ? DDTVersion.RTS4 : DDTVersion.RTS3;
        UsageFlag = (DDTUsage)usage;
        AlphaFlag = (DDTAlpha)alpha;
        FormatFlag = (DDTFormat)format;
        MipmapLevels = mipmap_levels;
        BaseWidth = (ushort)width;
        BaseHeight = (ushort)height;
        ColorTable = color_table;
        MipmapOffsets = mipmap_offsets;
        HeaderParsed = true;
        return true;
    }

    static ushort MipDimension(int base_size, int level) =>
        level >= 16 ? (ushort)1 : (ushort)Math.Max(1, base_size >> level);

    public ReadOnlyMemory<byte> ReadMipmap(int index, out ushort width, out ushort height)
    {
        if (!HeaderParsed) throw new Exception("Header not yet parsed!");
        if (index >= MipmapOffsets!.Length) throw new IndexOutOfRangeException("Mipmap index out of range");

        var (offset, length, m_width, m_height) = MipmapOffsets[index];
        var image_data = _data.Slice(offset, length);

        width = m_width;
        height = m_height;
        return image_data;
    }

    /// Zero-alloc BC1/BC1Alpha decode into a caller-supplied buffer. Returns
    /// false (without writing) for non-BC1 formats or when SIMD is unavailable;
    /// callers should fall back to DecodeMipmap. Also returns false when the mip
    /// payload is too short for its dimensions. width/height are populated
    /// regardless so the caller can size a pooled scratch buffer.
    public bool TryDecodeMipmapInto(int mipmap_index, Span<byte> output, out int width, out int height)
    {
        var mipmap_data = ReadMipmap(mipmap_index, out var w, out var h);
        width = w;
        height = h;

        if (!UseSimd || !SimdBc1Decoder.IsHardwareAccelerated) return false;
        if (!IsBc1Format(FormatFlag)) return false;
        if (mipmap_data.Length < RequiredMipBytes(FormatFlag, w, h)) return false;

        SimdBc1Decoder.DecodeImage(mipmap_data.Span, output, w, h, FormatFlag == DDTFormat.DXT1Alpha);
        return true;
    }

    public Task<Memory2D<ColorRgba32>?> DecodeMipmap(int mipmap_index = 0, CancellationToken token = default)
    {
        // Pre-check at entry instead of forwarding to BcDecoder: its per-block
        // ThrowIfCancellationRequested raises a first-chance OCE on the worker
        // that paused the debugger even when caught downstream.
        if (token.IsCancellationRequested)
            return Task.FromResult<Memory2D<ColorRgba32>?>(null);

        var mipmap_data = ReadMipmap(mipmap_index, out var width, out var height);

        var required = RequiredMipBytes(FormatFlag, width, height);
        if (mipmap_data.Length < required)
            return Task.FromResult<Memory2D<ColorRgba32>?>(null);

        mipmap_data = mipmap_data.Slice(0, (int)required);

        // NOTE:
        // - RTS3 files are rare (ex: "cloudshadows.ddt" in "ArtEffects.bar")
        // - Most RST4 DDT files use format 4 = DXT1
        // - When Alpha = 4, format is usually either 1,8 or 9 (Bgra,DXT3,DXT5)
        // - When Alpha = 1, format is usually 1 (Bgra)
        // - When Alpha = 0 and Usage = 4, format is usually 3

        if (UseSimd && SimdBc1Decoder.IsHardwareAccelerated && IsBc1Format(FormatFlag))
        {
            var pixels = SimdBc1Decoder.DecodeImage(mipmap_data.Span, width, height, FormatFlag == DDTFormat.DXT1Alpha);
            return Task.FromResult<Memory2D<ColorRgba32>?>(pixels.AsMemory().AsMemory2D(height, width));
        }

        return DecodeMipmapVendored(mipmap_data, width, height, token);
    }

    static long RequiredMipBytes(DDTFormat format, int width, int height)
    {
        long blocks = (long)((width + 3) >> 2) * ((height + 3) >> 2);
        return format switch
        {
            DDTFormat.DXT1 or DDTFormat.DXT1Alpha => blocks * 8,
            DDTFormat.DXT3 or DDTFormat.DXT5 => blocks * 16,
            DDTFormat.Grey => (long)width * height,
            _ => (long)width * height * 4,
        };
    }

    async Task<Memory2D<ColorRgba32>?> DecodeMipmapVendored(ReadOnlyMemory<byte> mipmap_data, ushort width, ushort height, CancellationToken token)
    {
        // Token is intentionally NOT forwarded to BcDecoder.DecodeRaw2DAsync -
        // see DecodeMipmap entry for the OCE-on-debugger rationale. Non-BC1
        // formats here are rare; the few ms of wasted work on cancel is fine.
        try
        {
            if (token.IsCancellationRequested) return null;

            var decoder = new BcDecoder();
            Memory2D<ColorRgba32> decoded_pixels;
            switch (FormatFlag)
            {
                case DDTFormat.DXT1:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1);
                    break;
                case DDTFormat.DXT1Alpha:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1WithAlpha);
                    break;
                case DDTFormat.Grey:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.R);
                    break;
                case DDTFormat.DXT3:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc2);
                    break;
                case DDTFormat.DXT5:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc3);
                    break;
                default:
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bgra);
                    break;
            }

            return token.IsCancellationRequested ? null : decoded_pixels;
        }
        catch (OperationCanceledException) { return null; }
    }
    public async Task<Image<Rgba32>?> DecodeMipmapToImage(int mipmap_index = 0, CancellationToken token = default)
    {
        var data = await DecodeMipmap(mipmap_index, token);
        if (!data.HasValue) return null;
        return PixelsToImage(data.Value);
    }
    
    public static Image<Rgba32> PixelsToImage(Memory2D<ColorRgba32> colors)
    {
        var output = new Image<Rgba32>(colors.Width, colors.Height);

        var destGroup = output.GetPixelMemoryGroup();
        if (colors.TryGetMemory(out var srcMemory) && destGroup.Count == 1)
        {
            MemoryMarshal.Cast<ColorRgba32, Rgba32>(srcMemory.Span).CopyTo(destGroup[0].Span);
        }
        else
        {
            for (var y = 0; y < colors.Height; y++)
            {
                var yPixels = output.Frames.RootFrame.PixelBuffer.DangerousGetRowSpan(y);
                var yColors = colors.Span.GetRowSpan(y);
                MemoryMarshal.Cast<ColorRgba32, Rgba32>(yColors).CopyTo(yPixels);
            }
        }
        return output;
    }

    public static Memory2D<ColorRgba32> ImageToPixels(Image<Rgba32> inputImage)
    {
        // Rgba32 and ColorRgba32 are layout-identical - reinterpret the image's pixel buffer directly
        var pixelGroup = inputImage.GetPixelMemoryGroup();
        if (pixelGroup.Count == 1)
        {
            var pixels = pixelGroup[0];
            return pixels.Cast<Rgba32, ColorRgba32>().AsMemory2D(inputImage.Height, inputImage.Width);
        }

        // multi-chunk fallback
        var buffer = new ColorRgba32[inputImage.Width * inputImage.Height];
        var memory2D = buffer.AsMemory().AsMemory2D(inputImage.Height, inputImage.Width);
        var dstSpan = memory2D.Span;
        var pixelBuffer = inputImage.Frames.RootFrame.PixelBuffer;
        for (var y = 0; y < inputImage.Height; y++)
        {
            var srcRow = pixelBuffer.DangerousGetRowSpan(y);
            var dstRow = dstSpan.GetRowSpan(y);
            MemoryMarshal.Cast<Rgba32, ColorRgba32>(srcRow).CopyTo(dstRow);
        }
        return memory2D;
    }

    public static async Task<Memory<byte>> EncodeImageToDDT(Image<Rgba32> image, 
        DDTVersion version, DDTUsage usage, DDTAlpha alpha, DDTFormat format,
        byte minmap_levels = 0, ReadOnlyMemory<byte>? color_table = null,
        CancellationToken token = default)
    {
        int base_width = image.Width;
        int base_height = image.Height;
        if (base_width > ushort.MaxValue || base_height > ushort.MaxValue)
            throw new ArgumentException($"DDT dimensions are limited to {ushort.MaxValue}x{ushort.MaxValue}, got {base_width}x{base_height}", nameof(image));

        byte max_levels = GetMaxMinmapLevels(base_width, base_height);
        byte mipmap_levels = minmap_levels == 0 ? max_levels : Math.Min(max_levels, minmap_levels);
        var images_per_level = 1; // check above note... this could be different based on usage, but am not handling it here as I've not encountered it in any AOM file
        var mipmap_count = mipmap_levels * images_per_level;

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = format switch
        {
            DDTFormat.DXT1 => CompressionFormat.Bc1,
            DDTFormat.DXT1Alpha => CompressionFormat.Bc1WithAlpha,
            DDTFormat.Grey => CompressionFormat.R,
            DDTFormat.DXT3 => CompressionFormat.Bc2,
            DDTFormat.DXT5 => CompressionFormat.Bc3,
            _=> CompressionFormat.Bgra,
        };
        encoder.OutputOptions.MaxMipMapLevel = mipmap_levels;
        encoder.InputOptions.LuminanceAsRed = format == DDTFormat.Grey;

        byte[][] mipmaps = await encoder.EncodeToRawBytesAsync(ImageToPixels(image), token);

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.UTF8, true);

        switch (version)
        {
            case DDTVersion.RTS4:
                writer.Write((byte)0x52);
                writer.Write((byte)0x54);
                writer.Write((byte)0x53);
                writer.Write((byte)0x34);
                break;

            case DDTVersion.RTS3:
                writer.Write((byte)0x52);
                writer.Write((byte)0x54);
                writer.Write((byte)0x53);
                writer.Write((byte)0x33);
                break;

            default:
                throw new NotSupportedException("Unsupported DDT version provided");
        }

        writer.Write((byte)usage);
        writer.Write((byte)alpha);
        writer.Write((byte)format);
        writer.Write(mipmap_levels);
        writer.Write(base_width);
        writer.Write(base_height);

        if (version == DDTVersion.RTS4)
        {
            // TODO: how is this color table constructed? for now we just copy it from existing DDT image

            // color table
            int color_table_size = color_table.HasValue ? color_table.Value.Length : 0;
            writer.Write(color_table_size);

            if (color_table_size > 0)
            {
                writer.Write(color_table!.Value.Span);
            }
        }

        // write mipmap offsets/length
        int mipmap_header_offset = (int)memory.Position;
        int mipmap_data_offset = mipmap_header_offset + (mipmap_count * 8);
        for (int i = 0; i < mipmap_count; i++)
        {
            int mipmap_size = mipmaps[i].Length;
            writer.Write(mipmap_data_offset);
            writer.Write(mipmap_size);

            mipmap_data_offset += mipmap_size;
        }

        // write mipmap data
        for (int i = 0; i < mipmap_count; i++)
        {
            var mipmap_data = mipmaps[i];
            writer.Write(mipmap_data);
        }

        return memory.GetBuffer().AsMemory(0, (int)memory.Position);
    }

    /// <summary>
    /// Calculates the expected and max. amount of minmap levels based on resolution
    /// (This will not always match the actual levels in a DDT file, it could be less, but never more)
    /// </summary>
    public static byte GetMaxMinmapLevels(int width, int height)
    {
        // always take the smallest dimension
        int size = Math.Min(width, height);
        if (size <= 0) return 0;

        byte levels = 1; // always at least the base level
        while (size > 4)
        {
            levels++;
            size >>= 1;
        }

        return levels;
    }

    /// Fast path for terrain tile textures: decodes the closest mip to
    /// targetSize and resamples to exact targetSize x targetSize RGBA8.
    /// Allocating overload; prefer the buffer overload below for hot paths.
    public static async Task<byte[]?> DecodeBaseColorOnlyAsync(
        ReadOnlyMemory<byte> ddtBytes,
        int targetSize,
        CancellationToken ct = default)
    {
        var dst = new byte[targetSize * targetSize * 4];
        if (!await DecodeBaseColorOnlyIntoAsync(ddtBytes, targetSize, dst, ct))
            return null;
        return dst;
    }

    /// Zero-alloc terrain-tile fast path. Writes targetSize*targetSize*4 bytes
    /// of RGBA8 into dst. BC1/BC1Alpha sources are fully zero-alloc; non-BC1
    /// falls through to the vendored decoder which allocates internally.
    public static async Task<bool> DecodeBaseColorOnlyIntoAsync(
        ReadOnlyMemory<byte> ddtBytes,
        int targetSize,
        Memory<byte> dst,
        CancellationToken ct = default)
    {
        int dstByteCount = targetSize * targetSize * 4;
        if (dst.Length < dstByteCount)
            throw new ArgumentException($"Destination too small: have {dst.Length}, need {dstByteCount}", nameof(dst));

        // No-copy: ddtBytes outlives this method via the await chain (the caller
        // owns the buffer and keeps it alive until DecodeBaseColorOnlyIntoAsync returns).
        var img = new DDTImage(ddtBytes, copyData: false);
        if (!img.ParseHeader()) return false;
        if (img.MipmapOffsets!.Length == 0) return false;

        // Pick smallest mip with width >= targetSize, otherwise largest available
        int chosen = 0;
        ushort chosenW = 0;
        for (int i = 0; i < img.MipmapOffsets.Length; i++)
        {
            var (_, _, w, _) = img.MipmapOffsets[i];
            if (w >= targetSize)
            {
                if (chosenW == 0 || w < chosenW) { chosen = i; chosenW = w; }
            }
        }
        if (chosenW == 0)
        {
            for (int i = 0; i < img.MipmapOffsets.Length; i++)
            {
                var (_, _, w, _) = img.MipmapOffsets[i];
                if (w > chosenW) { chosen = i; chosenW = w; }
            }
        }

        var (_, _, mipW, mipH) = img.MipmapOffsets[chosen];
        if ((long)mipW * mipH * 4 > int.MaxValue) return false;

        if (UseSimd && SimdBc1Decoder.IsHardwareAccelerated && IsBc1Format(img.FormatFlag))
        {
            if (mipW == targetSize && mipH == targetSize)
                return img.TryDecodeMipmapInto(chosen, dst.Span.Slice(0, dstByteCount), out _, out _);

            using var scratch = new PooledBuffer(mipW * mipH * 4);
            if (!img.TryDecodeMipmapInto(chosen, scratch.Span, out _, out _)) return false;
            ResampleBilinearRgba8(scratch.Span.Slice(0, mipW * mipH * 4), mipW, mipH,
                dst.Span.Slice(0, dstByteCount), targetSize, targetSize);
            return true;
        }

        var pixels = await img.DecodeMipmap(chosen, ct);
        if (!pixels.HasValue) return false;

        var p = pixels.Value;
        var dstSpan = dst.Span;
        if (p.Width == targetSize && p.Height == targetSize)
        {
            if (p.TryGetMemory(out var contiguous))
                MemoryMarshal.AsBytes(contiguous.Span).CopyTo(dstSpan);
            else
                for (int y = 0; y < p.Height; y++)
                    MemoryMarshal.AsBytes(p.Span.GetRowSpan(y)).CopyTo(dstSpan.Slice(y * p.Width * 4));
            return true;
        }

        ResampleBilinearRgba8(p, dst.Span, targetSize, targetSize);
        return true;
    }

    internal static void ResampleBilinearRgba8(Memory2D<ColorRgba32> src, Span<byte> dst, int dstW, int dstH)
    {
        if (src.TryGetMemory(out var contiguous))
        {
            ResampleBilinearRgba8(MemoryMarshal.AsBytes(contiguous.Span), src.Width, src.Height, dst, dstW, dstH);
            return;
        }
        // Strided fallback (rare): copy rows into a contiguous pooled buffer
        // and re-enter the byte-span path. Avoids carrying a separate strided
        // codepath that the SIMD/scalar parity tests don't cover.
        using var pooled = CopyMemory2DToContiguous(src, out int byteCount);
        ResampleBilinearRgba8(pooled.Span.Slice(0, byteCount), src.Width, src.Height, dst, dstW, dstH);
    }

    internal static void ResampleBilinearRgba8(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        if (UseSimd && Sse2.IsSupported && Vector128.IsHardwareAccelerated)
            ResampleBilinearRgba8Simd(src, srcW, srcH, dst, dstW, dstH);
        else
            ResampleBilinearRgba8Scalar(src, srcW, srcH, dst, dstW, dstH);
    }

    internal static void ResampleBilinearRgba8Scalar(Memory2D<ColorRgba32> src, byte[] dst, int dstW, int dstH)
    {
        if (src.TryGetMemory(out var contiguous))
        {
            ResampleBilinearRgba8Scalar(MemoryMarshal.AsBytes(contiguous.Span), src.Width, src.Height, dst, dstW, dstH);
            return;
        }
        using var pooled = CopyMemory2DToContiguous(src, out int byteCount);
        ResampleBilinearRgba8Scalar(pooled.Span.Slice(0, byteCount), src.Width, src.Height, dst, dstW, dstH);
    }

    static void ValidateResampleArgs(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0)
            throw new ArgumentException($"Invalid source size {srcW}x{srcH}", nameof(src));
        if (dstW <= 0 || dstH <= 0)
            throw new ArgumentException($"Invalid destination size {dstW}x{dstH}", nameof(dst));

        long src_bytes = (long)srcW * srcH * 4;
        if (src.Length < src_bytes)
            throw new ArgumentException($"Source too small: have {src.Length}, need {src_bytes} for {srcW}x{srcH}", nameof(src));

        long dst_bytes = (long)dstW * dstH * 4;
        if (dst.Length < dst_bytes)
            throw new ArgumentException($"Destination too small: have {dst.Length}, need {dst_bytes} for {dstW}x{dstH}", nameof(dst));
    }

    internal static void ResampleBilinearRgba8Scalar(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        ValidateResampleArgs(src, srcW, srcH, dst, dstW, dstH);

        int strideBytes = srcW * 4;
        float scaleX = (float)srcW / dstW;
        float scaleY = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float fy = (y + 0.5f) * scaleY - 0.5f;
            int y0 = (int)MathF.Floor(fy); int y1 = y0 + 1;
            float wy = fy - y0;
            if (y0 < 0) y0 = 0; if (y1 >= srcH) y1 = srcH - 1;

            var row0 = src.Slice(y0 * strideBytes, strideBytes);
            var row1 = src.Slice(y1 * strideBytes, strideBytes);
            int dstRow = y * dstW * 4;
            float iwy = 1 - wy;

            for (int x = 0; x < dstW; x++)
            {
                float fx = (x + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(fx); int x1 = x0 + 1;
                float wx = fx - x0;
                if (x0 < 0) x0 = 0; if (x1 >= srcW) x1 = srcW - 1;

                int p0 = x0 * 4, p1 = x1 * 4, dp = dstRow + x * 4;
                float iwx = 1 - wx;

                float r = (row0[p0]     * iwx + row0[p1]     * wx) * iwy + (row1[p0]     * iwx + row1[p1]     * wx) * wy;
                float g = (row0[p0 + 1] * iwx + row0[p1 + 1] * wx) * iwy + (row1[p0 + 1] * iwx + row1[p1 + 1] * wx) * wy;
                float b = (row0[p0 + 2] * iwx + row0[p1 + 2] * wx) * iwy + (row1[p0 + 2] * iwx + row1[p1 + 2] * wx) * wy;
                float a = (row0[p0 + 3] * iwx + row0[p1 + 3] * wx) * iwy + (row1[p0 + 3] * iwx + row1[p1 + 3] * wx) * wy;

                dst[dp]     = ByteHelper.ClampToByte(r);
                dst[dp + 1] = ByteHelper.ClampToByte(g);
                dst[dp + 2] = ByteHelper.ClampToByte(b);
                dst[dp + 3] = ByteHelper.ClampToByte(a);
            }
        }
    }

    /// Copy a strided Memory2D source to a contiguous pooled byte buffer.
    /// The caller is responsible for disposing the returned PooledBuffer.
    static PooledBuffer CopyMemory2DToContiguous(Memory2D<ColorRgba32> src, out int byteCount)
    {
        int srcW = src.Width;
        int srcH = src.Height;
        byteCount = srcW * srcH * 4;
        int strideBytes = srcW * 4;
        var pooled = new PooledBuffer(byteCount);
        var dstBytes = pooled.Span.Slice(0, byteCount);
        var srcSpan = src.Span;
        for (int y = 0; y < srcH; y++)
            MemoryMarshal.AsBytes(srcSpan.GetRowSpan(y)).CopyTo(dstBytes.Slice(y * strideBytes));
        return pooled;
    }

    internal static void ResampleBilinearRgba8Simd(Memory2D<ColorRgba32> src, byte[] dst, int dstW, int dstH)
    {
        if (src.TryGetMemory(out var contiguous))
        {
            ResampleBilinearRgba8Simd(MemoryMarshal.AsBytes(contiguous.Span), src.Width, src.Height, dst, dstW, dstH);
            return;
        }
        using var pooled = CopyMemory2DToContiguous(src, out int byteCount);
        ResampleBilinearRgba8Simd(pooled.Span.Slice(0, byteCount), src.Width, src.Height, dst, dstW, dstH);
    }

    /// SSE2 bilinear. Float ops follow the scalar evaluation order
    /// (top = c00*iwx + c01*wx; bot = c10*iwx + c11*wx; out = top*iwy + bot*wy)
    /// to stay bit-identical to the scalar path; clamp via PackSignedSaturate
    /// / PackUnsignedSaturate is identity for in-range values.
    internal static void ResampleBilinearRgba8Simd(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        ValidateResampleArgs(src, srcW, srcH, dst, dstW, dstH);

        int strideBytes = srcW * 4;
        float scaleX = (float)srcW / dstW;
        float scaleY = (float)srcH / dstH;

        var vMax = Vector128.Create(255f);
        var vZero = Vector128<float>.Zero;
        var vHalf = Vector128.Create(0.5f);

        for (int y = 0; y < dstH; y++)
        {
            float fy = (y + 0.5f) * scaleY - 0.5f;
            int y0 = (int)MathF.Floor(fy); int y1 = y0 + 1;
            float wy = fy - y0;
            if (y0 < 0) y0 = 0; if (y1 >= srcH) y1 = srcH - 1;

            var row0 = src.Slice(y0 * strideBytes, strideBytes);
            var row1 = src.Slice(y1 * strideBytes, strideBytes);
            int dstRow = y * dstW * 4;
            float iwy = 1 - wy;
            var vWy = Vector128.Create(wy);
            var vIwy = Vector128.Create(iwy);

            for (int x = 0; x < dstW; x++)
            {
                float fx = (x + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(fx); int x1 = x0 + 1;
                float wx = fx - x0;
                if (x0 < 0) x0 = 0; if (x1 >= srcW) x1 = srcW - 1;

                int p0 = x0 * 4, p1 = x1 * 4, dp = dstRow + x * 4;
                var vWx = Vector128.Create(wx);
                var vIwx = Vector128.Create(1 - wx);

                var c00 = LoadPixelToFloat(row0, p0);
                var c01 = LoadPixelToFloat(row0, p1);
                var c10 = LoadPixelToFloat(row1, p0);
                var c11 = LoadPixelToFloat(row1, p1);

                var top = Vector128.Add(Vector128.Multiply(c00, vIwx), Vector128.Multiply(c01, vWx));
                var bot = Vector128.Add(Vector128.Multiply(c10, vIwx), Vector128.Multiply(c11, vWx));
                var blended = Vector128.Add(Vector128.Multiply(top, vIwy), Vector128.Multiply(bot, vWy));

                var clamped = Vector128.Min(Vector128.Max(blended, vZero), vMax);
                var asInt32 = Sse2.ConvertToVector128Int32WithTruncation(Vector128.Add(clamped, vHalf));
                var asInt16 = Sse2.PackSignedSaturate(asInt32, asInt32);
                var asByte = Sse2.PackUnsignedSaturate(asInt16, asInt16);
                uint packed = asByte.AsUInt32().GetElement(0);
                Unsafe.WriteUnaligned(ref dst[dp], packed);
            }
        }
    }

    /// Loads 4 packed RGBA bytes and zero-extends to one float per channel.
    /// Byte-to-float is lossless for [0,255] so the SSE4.1 and SSE2 paths
    /// produce identical floats.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> LoadPixelToFloat(ReadOnlySpan<byte> row, int offset)
    {
        uint packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in row[offset]));
        Vector128<int> asInt32;
        if (Sse41.IsSupported)
        {
            asInt32 = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte());
        }
        else
        {
            asInt32 = Vector128.Create(
                (int)(packed & 0xFF),
                (int)((packed >> 8) & 0xFF),
                (int)((packed >> 16) & 0xFF),
                (int)((packed >> 24) & 0xFF));
        }
        return Sse2.ConvertToVector128Single(asInt32);
    }
}
