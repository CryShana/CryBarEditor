using System.Buffers.Binary;

using CryBar;
using CryBar.Bar;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using static CryBar.Tests.DdtTestHelpers;

namespace CryBar.Tests;

public class DDTImageValidationTests
{
    static bool Parse(byte[] data) => new DDTImage(data).ParseHeader();

    [Fact]
    public void ParseHeader_ValidSyntheticHeader_ReturnsTrue()
    {
        Assert.True(Parse(BuildRts4(64, 64, 3, colorTableSize: 8)));
    }

    [Fact]
    public void ParseHeader_EveryTruncation_ReturnsFalse()
    {
        var data = BuildRts4(64, 64, 3, colorTableSize: 8);

        for (int len = 0; len < data.Length; len++)
        {
            var ddt = new DDTImage(data.AsMemory(0, len));
            Assert.False(ddt.ParseHeader(), $"length {len}");
            Assert.False(ddt.HeaderParsed);
        }
    }

    [Fact]
    public void ParseHeader_Rts3WithoutMipTable_ReturnsFalse()
    {
        var data = new byte[16];
        data[0] = 0x52; data[1] = 0x54; data[2] = 0x53; data[3] = 0x33;
        data[7] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 4);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 4);

        Assert.False(Parse(data));
    }

    [Fact]
    public void ParseHeader_NegativeColorTableSize_ReturnsFalse()
    {
        Assert.False(Parse(BuildRts4(16, 16, 1, colorTableSize: -1)));
        Assert.False(Parse(BuildRts4(16, 16, 1, colorTableSize: int.MinValue)));
    }

    [Fact]
    public void ParseHeader_ColorTablePastEnd_ReturnsFalse()
    {
        Assert.False(Parse(BuildRts4(16, 16, 1, colorTableSize: 10_000, colorTableBytes: 0)));
        Assert.False(Parse(BuildRts4(16, 16, 1, colorTableSize: int.MaxValue, colorTableBytes: 0)));
    }

    [Fact]
    public void ParseHeader_ZeroMipmapLevels_ReturnsFalse()
    {
        Assert.False(Parse(BuildRts4(16, 16, 0)));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    [InlineData(-1, 16)]
    [InlineData(16, -5)]
    [InlineData(65536, 16)]
    [InlineData(16, 65536)]
    [InlineData(int.MaxValue, 16)]
    public void ParseHeader_InvalidDimensions_ReturnsFalse(int width, int height)
    {
        Assert.False(Parse(BuildRts4(width, height, 1)));
    }

    [Fact]
    public void ParseHeader_MaxDimensions_Accepted()
    {
        var ddt = new DDTImage(BuildRts4(65535, 65535, 1));

        Assert.True(ddt.ParseHeader());
        Assert.Equal(65535, ddt.BaseWidth);
        Assert.Equal(65535, ddt.BaseHeight);
    }

    [Theory]
    [InlineData(int.MaxValue, 16)]
    [InlineData(-1, 16)]
    [InlineData(0, -1)]
    [InlineData(int.MaxValue - 4, 16)]
    public void ParseHeader_MipOutOfRange_ReturnsFalse(int mipOffset, int mipLength)
    {
        var data = BuildRts4(16, 16, 2, mipEntry: (i, start) => i == 1 ? (mipOffset, mipLength) : (start, 16));
        Assert.False(Parse(data));
    }

    [Fact]
    public void ParseHeader_MipJustPastEnd_ReturnsFalse()
    {
        var data = BuildRts4(16, 16, 1, mipEntry: (_, start) => (start + 1, 16));
        Assert.False(Parse(data));
    }

    [Fact]
    public void ParseHeader_ManyLevels_DeepLevelsClampToOne()
    {
        var ddt = new DDTImage(BuildRts4(65535, 32768, 40, payloadBytes: 0));

        Assert.True(ddt.ParseHeader());
        Assert.Equal(40, ddt.MipmapOffsets.Length);
        Assert.Equal(255, ddt.MipmapOffsets[8].Item3);
        Assert.Equal(1, ddt.MipmapOffsets[15].Item3);
        Assert.Equal(1, ddt.MipmapOffsets[15].Item4);
        for (int level = 16; level < 40; level++)
        {
            Assert.Equal(1, ddt.MipmapOffsets[level].Item3);
            Assert.Equal(1, ddt.MipmapOffsets[level].Item4);
        }
    }

    [Theory]
    [InlineData(DDTFormat.DXT1)]
    [InlineData(DDTFormat.DXT1Alpha)]
    [InlineData(DDTFormat.DXT5)]
    [InlineData(DDTFormat.Grey)]
    [InlineData(DDTFormat.Bgra)]
    public async Task ConvertDdt_ShortMipPayload_ReturnsNull(DDTFormat format)
    {
        var data = BuildRts4(64, 64, 1, payloadBytes: 8, format: format);

        Assert.Null(await ConversionHelper.ConvertDdtToPngBytes(data));
        Assert.Null(await ConversionHelper.ConvertDdtToTgaBytes(data));
    }

    [Fact]
    public async Task ConvertDdt_CorruptHeader_ReturnsNull()
    {
        var data = BuildRts4(64, 64, 1, colorTableSize: 10_000, colorTableBytes: 0);

        Assert.Null(await ConversionHelper.ConvertDdtToPngBytes(data));
        Assert.Null(await ConversionHelper.ConvertDdtToTgaBytes(data));
    }

    [Fact]
    public async Task EncodeImageToDDT_WidthOver65535_Throws()
    {
        using var image = new Image<Rgba32>(65536, 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DDTImage.EncodeImageToDDT(image, DDTVersion.RTS4, DDTUsage.None, DDTAlpha.None, DDTFormat.Bgra));
    }

    [Fact]
    public void SimdBc1DecodeImage_OverflowingDimensions_Throws()
    {
        var blocks = new byte[8];
        var output = new byte[64];

        Assert.Throws<ArgumentException>(() => SimdBc1Decoder.DecodeImage(blocks, output, 65535, 65535, false));
        Assert.Throws<ArgumentException>(() => SimdBc1Decoder.DecodeImage(blocks, output, 65535, 65535, true));
        Assert.Throws<ArgumentException>(() => SimdBc1Decoder.DecodeImage(blocks, 65535, 65535, false));
    }

    [Fact]
    public void TryDecodeMipmapInto_HugeDimensions_DoesNotWritePastBuffer()
    {
        var ddt = new DDTImage(BuildRts4(65535, 65535, 1, payloadBytes: 8));
        Assert.True(ddt.ParseHeader());

        var buffer = new byte[128];
        var output = buffer.AsMemory(0, 64);

        try
        {
            Assert.False(ddt.TryDecodeMipmapInto(0, output.Span, out _, out _));
        }
        catch (ArgumentException) { }

        Assert.All(buffer.AsSpan(64).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Resample_UndersizedDestination_Throws()
    {
        var src = new byte[4 * 4 * 4];
        var dst = new byte[2 * 2 * 4 - 1];

        Assert.Throws<ArgumentException>(() => DDTImage.ResampleBilinearRgba8Scalar(src, 4, 4, dst, 2, 2));
        Assert.Throws<ArgumentException>(() => DDTImage.ResampleBilinearRgba8Simd(src, 4, 4, dst, 2, 2));
        Assert.Throws<ArgumentException>(() => DDTImage.ResampleBilinearRgba8(src, 4, 4, dst, 2, 2));
    }

    [Fact]
    public void Resample_UndersizedSource_Throws()
    {
        var src = new byte[4 * 4 * 4 - 1];
        var dst = new byte[2 * 2 * 4];

        Assert.Throws<ArgumentException>(() => DDTImage.ResampleBilinearRgba8Scalar(src, 4, 4, dst, 2, 2));
        Assert.Throws<ArgumentException>(() => DDTImage.ResampleBilinearRgba8Simd(src, 4, 4, dst, 2, 2));
    }

    [Fact]
    public void Resample_HalfwayBlend_RoundsToNearest_ScalarMatchesSimd()
    {
        byte[] src = [0, 0, 0, 0, 1, 3, 255, 254];
        var scalar = new byte[4];
        var simd = new byte[4];

        DDTImage.ResampleBilinearRgba8Scalar(src, 2, 1, scalar, 1, 1);
        DDTImage.ResampleBilinearRgba8Simd(src, 2, 1, simd, 1, 1);

        Assert.Equal(new byte[] { 1, 2, 128, 127 }, scalar);
        Assert.Equal(scalar, simd);
    }

    static async Task<byte[]> EncodeGreyAndDecode(Rgba32 color)
    {
        using var image = new Image<Rgba32>(4, 4, color);
        var encoded = await DDTImage.EncodeImageToDDT(image, DDTVersion.RTS4, DDTUsage.None, DDTAlpha.None, DDTFormat.Grey);

        var ddt = new DDTImage(encoded);
        Assert.True(ddt.ParseHeader());
        Assert.Equal(DDTFormat.Grey, ddt.FormatFlag);

        var pixels = await ddt.DecodeMipmap(0);
        Assert.NotNull(pixels);
        var p = pixels.Value.Span[0, 0];
        return [p.r, p.g, p.b];
    }

    [Fact]
    public async Task EncodeGrey_PureRed_StoresLuminance()
    {
        var rgb = await EncodeGreyAndDecode(new Rgba32(255, 0, 0, 255));

        Assert.InRange(rgb[0], 75, 77);
        Assert.Equal(rgb[0], rgb[1]);
        Assert.Equal(rgb[0], rgb[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(200)]
    [InlineData(254)]
    [InlineData(255)]
    public async Task EncodeGrey_GreyInput_RoundTripsExactly(byte v)
    {
        var rgb = await EncodeGreyAndDecode(new Rgba32(v, v, v, 255));

        Assert.Equal(new[] { v, v, v }, rgb);
    }
}
