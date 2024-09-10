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

public class DDTImage
{
    public DDTVersion Version { get; private set; }
    public bool HeaderParsed { get; private set; }

    public byte UsageFlag { get; private set; }
    public byte AlphaFlag { get; private set; }
    public byte FormatFlag { get; private set; }
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
        var usage = data_span[offset++];         // 0, 8 = Cube
        var alpha = data_span[offset++];         // 0 = none, 1 = Player, 4 = transparent
        var format = data_span[offset++];        // 1 = Bgra, 5 = Dxt1Alpha, 4 = Dxt1, 7 = Grey, 8 = Dxt3, 9 = Dxt5
        var mipmap_levels = data_span[offset++];      // 10,7,8

        var width = (ushort)BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;
        var height = (ushort)BinaryPrimitives.ReadInt32LittleEndian(data_span.Slice(offset, 4)); offset += 4;

        UsageFlag = usage;
        AlphaFlag = alpha;
        FormatFlag = format;
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

    public async Task<Memory<byte>?> ConvertMipmapToTGA(int mipmap_index = 0, CancellationToken token = default)
    {
        var mipmap_data = ReadMipmap(mipmap_index, out var width, out var height);

        // NOTE:
        // - RTS3 files are rare (ex: "cloudshadows.ddt" in "ArtEffects.bar")
        // - Most RST4 DDT files use format 4 = DXT1
        // - When Alpha = 4, format is usually either 1,8 or 9 (Bgra,DXT3,DXT5)
        // - When Alpha = 1, format is usually 1 (Bgra)
        // - When Alpha = 0 and Usage = 4, format is usually 3 (???)
        // - Most images, even large ones 2048x2048, load within 0.25 seconds (lag spike usually caused by displaying it in UI)

        try
        {
            var decoder = new BcDecoder();
            Memory2D<ColorRgba32> decoded_pixels;
            switch (FormatFlag)
            {
                case 4:
                    // DXT1 - CompressionFormat.Bc1
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1, token);
                    break;
                case 5:
                    // DXT1 with Transparency - CompressionFormat.Bc1WithAlpha
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc1WithAlpha, token);
                    break;
                case 7:
                    // Grey - CompressionFormat.R
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.R, token);
                    break;
                case 8:
                    // DXT3 - CompressionFormat.Bc2
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc2, token);
                    break;
                case 9:
                    // DXT5 - CompressionFormat.Bc3
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bc3, token);
                    break;
                default: // usually 1
                         // CompressionFormat.Bgra
                    decoded_pixels = await decoder.DecodeRaw2DAsync(mipmap_data, width, height, CompressionFormat.Bgra, token);
                    break;
            }

            using var memory = new MemoryStream();
            using (var image = PixelsToImage(decoded_pixels))
                await image.SaveAsTgaAsync(memory, token);

            return memory.GetBuffer().AsMemory(0, (int)memory.Position);
        }
        catch (OperationCanceledException) { return null; }
        catch
        {
            throw;
        }
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
}
