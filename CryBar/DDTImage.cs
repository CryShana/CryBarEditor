using CommunityToolkit.HighPerformance;

using CryBar.BCnEncoder.Decoder;
using CryBar.BCnEncoder.Shared;

using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace CryBar;

public enum DDTVersion
{
    RTS3,
    RTS4
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
    public DDTVersion Version { get; private set; }
    public bool HeaderParsed { get; private set; }

    public DDTUsage UsageFlag { get; private set; }
    public DDTAlpha AlphaFlag { get; private set; }
    public DDTFormat FormatFlag { get; private set; }
    public byte MipmapLevels { get; private set; }

    public ushort BaseWidth { get; private set; }
    public ushort BaseHeight { get; private set; }

    public (int, int, ushort, ushort)[]? MipmapOffsets { get; private set; }

    readonly ReadOnlyMemory<byte> _data;

    public DDTImage(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    [MemberNotNullWhen(true, nameof(MipmapOffsets))]
    public bool ParseHeader()
    {
        var data_span = _data.Span;

        var rts4 = data_span is [0x52, 0x54, 0x53, 0x34, ..];
        var rts3 = data_span is [0x52, 0x54, 0x53, 0x33, ..];

        if (rts4) Version = DDTVersion.RTS4;
        else if (rts3) Version = DDTVersion.RTS3;
        else return false;

        var offset = 4;

        // image info
        var usage = data_span[offset++];
        var alpha = data_span[offset++];
        var format = data_span[offset++]; 
        var mipmap_levels = data_span[offset++]; 

        var width = (ushort)BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;
        var height = (ushort)BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;

        UsageFlag = (DDTUsage)usage;
        AlphaFlag = (DDTAlpha)alpha;
        FormatFlag = (DDTFormat)format;
        MipmapLevels = mipmap_levels;
        BaseWidth = width;
        BaseHeight = height;

        // color table (RTS4 only):
        if (rts4)
        {
            int color_table_size = BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;
            var color_table = data_span.Slice(offset, color_table_size); offset += color_table_size;
        }

        // read mipmaps
        int images_per_level = (usage & 8) == 8 ? 6 : 1; // there's more images when usage is 8 = [Cube]
        var mipmap_image_count = mipmap_levels * images_per_level;
        var mipmap_offsets = new (int, int, ushort, ushort)[mipmap_image_count];
        for (int i = 0; i < mipmap_image_count; i++)
        {
            var level = i / images_per_level;
            var image_width = (ushort)Math.Max(1, width >> level);
            var image_height = (ushort)Math.Max(1, height >> level);
            var image_offset = BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;
            var image_length = BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;
            mipmap_offsets[i] = (image_offset, image_length, image_width, image_height);
        }
        MipmapOffsets = mipmap_offsets;
        HeaderParsed = true;
        return true;
    }

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

    public async Task<Memory2D<ColorRgba32>?> DecodeMipmap(int mipmap_index = 0, CancellationToken token = default)
    {
        var mipmap_data = ReadMipmap(mipmap_index, out var width, out var height);

        // NOTE:
        // - RTS3 files are rare (ex: "cloudshadows.ddt" in "ArtEffects.bar")
        // - Most RST4 DDT files use format 4 = DXT1
        // - When Alpha = 4, format is usually either 1,8 or 9 (Bgra,DXT3,DXT5)
        // - When Alpha = 1, format is usually 1 (Bgra)
        // - When Alpha = 0 and Usage = 4, format is usually 3

        try
        {
            var decoder = new BcDecoder();
            Memory2D<ColorRgba32> decoded_pixels;
            switch (FormatFlag)
            {
                case DDTFormat.DXT1:
                    // DXT1 - CompressionFormat.Bc1
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1, token);
                    break;
                case DDTFormat.DXT1Alpha:
                    // DXT1 with Transparency - CompressionFormat.Bc1WithAlpha
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1WithAlpha, token);
                    break;
                case DDTFormat.Grey:
                    // Grey - CompressionFormat.R
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.R, token);
                    break;
                case DDTFormat.DXT3:
                    // DXT3 - CompressionFormat.Bc2
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc2, token);
                    break;
                case DDTFormat.DXT5:
                    // DXT5 - CompressionFormat.Bc3
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc3, token);
                    break;
                default:
                    // CompressionFormat.Bgra
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bgra, token);
                    break;
            }

            return decoded_pixels;
        }
        catch (OperationCanceledException) { return null; }
        catch
        {
            throw;
        }
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
        for (var y = 0; y < colors.Height; y++)
        {
            var yPixels = output.Frames.RootFrame.PixelBuffer.DangerousGetRowSpan(y);
            var yColors = colors.Span.GetRowSpan(y);

            MemoryMarshal.Cast<ColorRgba32, Rgba32>(yColors).CopyTo(yPixels);
        }
        return output;
    }
    
    public static Memory<byte> EncodeImageToDDT(Image<Rgba32> image, 
        DDTVersion version, DDTUsage usage, DDTAlpha alpha, DDTFormat format)
    {
        // TODO: when RTS4, we need to make a color table (byte array sizes 64, 116, etc...)
        // need to figure out how they are constructed

        // TODO: image is the base image, we need to create mipmaps until sizes allow (smallest dimension is 4 pixels)

        throw new NotImplementedException();
    }
}
