using CryBar.BCnEncoder.Decoder;
using CryBar.BCnEncoder.Encoder;
using CryBar.BCnEncoder.Encoder.Bptc;
using CryBar.BCnEncoder.Shared;

namespace CryBar.Tests;

public class Bc6EncoderFixTests
{
    const float HalfMax = 65504f;

    static readonly float[] DarkValues = [-0f, -1e-7f, -1e-3f, 0f, -0f, -5e-8f, -0f, -1e-5f];

    [Fact]
    public void ClampToHalf_ClampsEachChannelIndependently()
    {
        var a = new ColorRgbFloat(-1e6f, 1e6f, 1e6f);
        a.ClampToHalf();
        Assert.Equal(-HalfMax, a.r);
        Assert.Equal(HalfMax, a.g);
        Assert.Equal(HalfMax, a.b);

        var b = new ColorRgbFloat(1e6f, -1e6f, 1e6f);
        b.ClampToHalf();
        Assert.Equal(HalfMax, b.r);
        Assert.Equal(-HalfMax, b.g);
        Assert.Equal(HalfMax, b.b);

        var c = new ColorRgbFloat(1e6f, 1e6f, -1e6f);
        c.ClampToHalf();
        Assert.Equal(HalfMax, c.r);
        Assert.Equal(HalfMax, c.g);
        Assert.Equal(-HalfMax, c.b);
    }

    [Fact]
    public void ClampToPositive_NegativeZeroAndNaN_BecomePositiveZero()
    {
        var c = new ColorRgbFloat(-0f, float.NaN, -3f);
        c.ClampToPositive();

        Assert.Equal(0, BitConverter.SingleToInt32Bits(c.r));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(c.g));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(c.b));
    }

    [Theory]
    [InlineData(-0f)]
    [InlineData(-1e-7f)]
    [InlineData(-1e-3f)]
    [InlineData(-1f)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void PreQuantize_Unsigned_NegativeOrNaN_IsZero(float value)
    {
        Assert.Equal(0, Bc6EncodingHelpers.PreQuantize(value, false));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-7f)]
    [InlineData(1f)]
    [InlineData(HalfMax)]
    [InlineData(1e9f)]
    [InlineData(float.PositiveInfinity)]
    public void PreQuantize_Unsigned_StaysInRange(float value)
    {
        var q = Bc6EncodingHelpers.PreQuantize(value, false);
        Assert.InRange(q, 0, 0xFFFF);
    }

    [Fact]
    public void PreQuantize_Signed_NegativeZeroIsZero()
    {
        Assert.Equal(0, Bc6EncodingHelpers.PreQuantize(-0f, true));
        Assert.Equal(0, Bc6EncodingHelpers.PreQuantize(float.NaN, true));
    }

    [Theory]
    [InlineData(-1e-7f)]
    [InlineData(-1e-3f)]
    [InlineData(-1f)]
    [InlineData(-HalfMax)]
    [InlineData(float.NegativeInfinity)]
    public void PreQuantize_Signed_NegativeStaysNegativeAndInRange(float value)
    {
        var q = Bc6EncodingHelpers.PreQuantize(value, true);
        Assert.InRange(q, -0x7FFF, 0);
        Assert.Equal(-Bc6EncodingHelpers.PreQuantize(-value, true), q);
    }

    [Theory]
    [InlineData(1e-7f)]
    [InlineData(1f)]
    [InlineData(HalfMax)]
    [InlineData(float.PositiveInfinity)]
    public void PreQuantize_Signed_PositiveStaysInRange(float value)
    {
        var q = Bc6EncodingHelpers.PreQuantize(value, true);
        Assert.InRange(q, 0, 0x7FFF);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(16)]
    public void Quantize_Unsigned_NeverExceedsBitRange(int endpointBits)
    {
        var max = (1 << endpointBits) - 1;
        int[] components = [-5, 0, 1, 15, 31, 0x7FFF, 0xFFFE, 0xFFFF, 0x10000, 70000];

        foreach (var component in components)
        {
            Assert.InRange(Bc6EncodingHelpers.Quantize(component, endpointBits, false), 0, max);
        }

        var inf = Bc6EncodingHelpers.PreQuantize(float.PositiveInfinity, false);
        var halfMax = Bc6EncodingHelpers.PreQuantize(65504f, false);
        Assert.Equal(Bc6EncodingHelpers.Quantize(halfMax, endpointBits, false), Bc6EncodingHelpers.Quantize(inf, endpointBits, false));
        Assert.True(Bc6EncodingHelpers.Quantize(inf, endpointBits, false) >= max - 2);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(16)]
    public void Quantize_Signed_NeverExceedsBitRange(int endpointBits)
    {
        var max = endpointBits >= 16 ? 0x7FFF : (1 << endpointBits - 1) - 1;
        int[] components = [-40000, -0x8000, -0x7FFF, -0x7FFE, -1, 0, 1, 0x7FFE, 0x7FFF, 0x8000, 40000];

        foreach (var component in components)
        {
            var q = Bc6EncodingHelpers.Quantize(component, endpointBits, true);
            Assert.InRange(q, -max, max);
            if (component > 0) Assert.True(q >= 0);
            if (component < 0) Assert.True(q <= 0);
        }
    }

    [Fact]
    public void ColorRgbe_NegativeAndNaN_ComponentsBecomeZero()
    {
        var allNegative = new ColorRgbe(new ColorRgbFloat(-1f, -2f, -3f));
        Assert.Equal(new ColorRgbe(0, 0, 0, 0), allNegative);

        var allNaN = new ColorRgbe(new ColorRgbFloat(float.NaN, float.NaN, float.NaN));
        Assert.Equal(new ColorRgbe(0, 0, 0, 0), allNaN);

        var mixed = new ColorRgbe(new ColorRgbFloat(-1f, float.NaN, 0.5f));
        Assert.Equal(0, mixed.r);
        Assert.Equal(0, mixed.g);

        var decoded = mixed.ToColorRgbFloat();
        Assert.InRange(decoded.b, 0.49f, 0.51f);
        Assert.InRange(decoded.r, 0f, 0.01f);
        Assert.InRange(decoded.g, 0f, 0.01f);
    }

    [Fact]
    public void ColorRgbe_HugeValues_Saturate()
    {
        var huge = new ColorRgbe(new ColorRgbFloat(float.MaxValue, float.PositiveInfinity, 1f));

        Assert.Equal(255, huge.e);
        Assert.Equal(255, huge.r);
        Assert.Equal(255, huge.g);

        var decoded = huge.ToColorRgbFloat();
        Assert.True(decoded.r > 1e38f);
        Assert.True(decoded.g > 1e38f);
    }

    [Fact]
    public void ColorRgbe_NormalValues_RoundTrip()
    {
        var decoded = new ColorRgbe(new ColorRgbFloat(1f, 0.5f, 0.25f)).ToColorRgbFloat();

        Assert.InRange(decoded.r, 0.99f, 1.01f);
        Assert.InRange(decoded.g, 0.49f, 0.51f);
        Assert.InRange(decoded.b, 0.24f, 0.26f);
    }

    [Fact]
    public void LeastSquares_Unsigned_NegativeZeroPixels_DoNotDragEndpointsToMax()
    {
        var block = new RawBlock4X4RgbFloat();
        for (var i = 0; i < 16; i++)
        {
            block[i] = i < 8 ? new ColorRgbFloat(-0f, -0f, -0f) : new ColorRgbFloat(1f, 1f, 1f);
        }

        var ep0 = new ColorRgbFloat(0f, 0f, 0f);
        var ep1 = new ColorRgbFloat(1f, 1f, 1f);
        LeastSquares.OptimizeEndpoints1Sub(block, ref ep0, ref ep1, false);

        Assert.InRange(ep0.r, 0f, 0.01f);
        Assert.InRange(ep1.r, 0.99f, 1.01f);
    }

    [Fact]
    public void LeastSquares_Signed_KeepsNegativeEndpoints()
    {
        var block = new RawBlock4X4RgbFloat();
        for (var i = 0; i < 16; i++)
        {
            block[i] = i < 8 ? new ColorRgbFloat(-1f, -1f, -1f) : new ColorRgbFloat(1f, 1f, 1f);
        }

        var ep0 = new ColorRgbFloat(-1f, -1f, -1f);
        var ep1 = new ColorRgbFloat(1f, 1f, 1f);
        LeastSquares.OptimizeEndpoints1Sub(block, ref ep0, ref ep1, true);

        Assert.InRange(ep0.r, -1.01f, -0.99f);
        Assert.InRange(ep1.r, 0.99f, 1.01f);
    }

    public static TheoryData<CompressionFormat, CompressionQuality> FormatsAndQualities()
    {
        var data = new TheoryData<CompressionFormat, CompressionQuality>();
        foreach (var format in new[] { CompressionFormat.Bc6U, CompressionFormat.Bc6S })
        {
            foreach (var quality in new[] { CompressionQuality.Fast, CompressionQuality.Balanced, CompressionQuality.BestQuality })
            {
                data.Add(format, quality);
            }
        }
        return data;
    }

    static ColorRgbFloat[] EncodeDecode(ColorRgbFloat[] pixels, CompressionFormat format, CompressionQuality quality)
    {
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.Quality = quality;
        encoder.OutputOptions.GenerateMipMaps = false;

        var encoded = encoder.EncodeBlockHdr(new ReadOnlySpan<ColorRgbFloat>(pixels));
        return new BcDecoder().DecodeRawHdr(encoded, 4, 4, format);
    }

    static void AssertDark(ColorRgbFloat c, CompressionFormat format, float tolerance = 0.01f)
    {
        var min = format == CompressionFormat.Bc6U ? 0f : -tolerance;
        Assert.InRange(c.r, min, tolerance);
        Assert.InRange(c.g, min, tolerance);
        Assert.InRange(c.b, min, tolerance);
    }

    [Theory]
    [MemberData(nameof(FormatsAndQualities))]
    public void EncodeDecode_DarkBlockWithNegativeZero_StaysDark(CompressionFormat format, CompressionQuality quality)
    {
        var pixels = new ColorRgbFloat[16];
        for (var i = 0; i < 16; i++)
        {
            var v = DarkValues[i % DarkValues.Length];
            pixels[i] = new ColorRgbFloat(v, v, v);
        }

        var decoded = EncodeDecode(pixels, format, quality);

        foreach (var c in decoded)
        {
            AssertDark(c, format);
        }
    }

    [Theory]
    [MemberData(nameof(FormatsAndQualities))]
    public void EncodeDecode_MixedBlockWithNegativeZero_DarkPixelsStayDark(CompressionFormat format, CompressionQuality quality)
    {
        var pixels = new ColorRgbFloat[16];
        for (var i = 0; i < 16; i++)
        {
            if (i < 8)
            {
                var v = DarkValues[i];
                pixels[i] = new ColorRgbFloat(v, v, v);
            }
            else
            {
                pixels[i] = new ColorRgbFloat(1f, 1f, 1f);
            }
        }

        var decoded = EncodeDecode(pixels, format, quality);

        for (var i = 0; i < 16; i++)
        {
            if (i < 8)
            {
                AssertDark(decoded[i], format, 0.05f);
            }
            else
            {
                Assert.InRange(decoded[i].r, 0.9f, 1.1f);
                Assert.InRange(decoded[i].g, 0.9f, 1.1f);
                Assert.InRange(decoded[i].b, 0.9f, 1.1f);
            }
        }
    }
}
