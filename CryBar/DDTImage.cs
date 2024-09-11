using CommunityToolkit.HighPerformance;

using CryBar.BCnEncoder.Shared;
using CryBar.BCnEncoder.Encoder;
using CryBar.BCnEncoder.Decoder;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

using System.Text;
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
    public ReadOnlyMemory<byte>? ColorTable { get; private set; }

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
            var color_table = _data.Slice(offset, color_table_size); offset += color_table_size;
            ColorTable = color_table;
        }

        // read mipmaps
        int images_per_level = 1; // (usage & 8) == 8 ? 6 : 1; // there's more images when usage is 8 = [Cube] - I HAVE NOT ENCOUNTERED THIS YET, let's assume 1 for now
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

    public static Memory2D<ColorRgba32> ImageToPixels(Image<Rgba32> inputImage)
    {
        var pixels = inputImage.GetPixelMemoryGroup()[0];
        var colors = new ColorRgba32[inputImage.Width * inputImage.Height];
        for (var y = 0; y < inputImage.Height; y++)
        {
            var yPixels = inputImage.Frames.RootFrame.PixelBuffer.DangerousGetRowSpan(y);
            var yColors = colors.AsSpan(y * inputImage.Width, inputImage.Width);

            MemoryMarshal.Cast<Rgba32, ColorRgba32>(yPixels).CopyTo(yColors);
        }
        var memory = colors.AsMemory().AsMemory2D(inputImage.Height, inputImage.Width);
        return memory;
    }

    public static async Task<Memory<byte>> EncodeImageToDDT(Image<Rgba32> image, 
        DDTVersion version, DDTUsage usage, DDTAlpha alpha, DDTFormat format,
        byte minmap_levels = 0, ReadOnlyMemory<byte>? color_table = null,
        CancellationToken token = default)
    {
        int base_width = image.Width;
        int base_height = image.Height;

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

        byte[][] mipmaps = await encoder.EncodeToRawBytesAsync(ImageToPixels(image), token);

        var memory = new MemoryStream();
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

        byte levels = 0; 
        int new_size = size;
        for (int i = 0; new_size > 4 ; i++)
        {
            levels++;
            new_size = size >> i;
        }

        return levels;
    }
}
