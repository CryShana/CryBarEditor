using System.Numerics;

using CryBar.BCnEncoder.Encoder;
using CryBar.BCnEncoder.Encoder.Bptc;
using CryBar.BCnEncoder.Shared;

namespace CryBar.Tests;

public class BCnEncoderFixTests
{
    [Fact]
    public void Create565A_MaxAlphaComesFromAlphaChannel()
    {
        var colors = new ColorRgba32[16];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = new ColorRgba32(10, 10, 10, (byte)(i * 16));

        RgbBoundingBox.Create565A(colors, out _, out _, out var minAlpha, out var maxAlpha);

        Assert.InRange(maxAlpha, (byte)230, (byte)240);
        Assert.InRange(minAlpha, (byte)0, (byte)10);
    }

    [Theory]
    [InlineData(0.4f, 0)]
    [InlineData(0.5f, 1)]
    [InlineData(199.6f, 200)]
    [InlineData(254.49f, 254)]
    [InlineData(254.5f, 255)]
    [InlineData(-3f, 0)]
    [InlineData(-0.7f, 0)]
    [InlineData(300f, 255)]
    public void ClampToByte_Float_RoundsAndClamps(float input, int expected)
    {
        Assert.Equal((byte)expected, ByteHelper.ClampToByte(input));
    }

    [Fact]
    public void ClampToByte_Float_RoundTripsNormalizedBytes()
    {
        for (var v = 0; v < 256; v++)
            Assert.Equal((byte)v, ByteHelper.ClampToByte(v / 255f * 255));
    }

    [Fact]
    public void MipChain_UniformColor_KeepsExactValuesAtAllLevels()
    {
        var color = new ColorRgba32(200, 100, 50, 254);
        var pixels = new ColorRgba32[16 * 16];
        Array.Fill(pixels, color);

        var numMipMaps = 0;
        var chain = MipMapper.GenerateMipChain((ReadOnlyMemory<ColorRgba32>)pixels, 16, 16, ref numMipMaps);

        Assert.Equal(5, numMipMaps);
        for (var level = 1; level < numMipMaps; level++)
        {
            var span = chain[level].Span;
            for (var y = 0; y < span.Height; y++)
            for (var x = 0; x < span.Width; x++)
                Assert.Equal(color, span[y, x]);
        }
    }

    [Fact]
    public void MipChain_AlphaMix255And254_RoundsTo255()
    {
        var pixels = new ColorRgba32[4 * 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new ColorRgba32(0, 0, 0, (byte)(((i % 4) + (i / 4)) % 2 == 0 ? 255 : 254));

        var numMipMaps = 0;
        var chain = MipMapper.GenerateMipChain((ReadOnlyMemory<ColorRgba32>)pixels, 4, 4, ref numMipMaps);

        Assert.Equal(3, numMipMaps);
        for (var level = 1; level < numMipMaps; level++)
        {
            var span = chain[level].Span;
            for (var y = 0; y < span.Height; y++)
            for (var x = 0; x < span.Width; x++)
                Assert.Equal(255, span[y, x].a);
        }
    }

    [Theory]
    [InlineData(254, 255)]
    [InlineData(255, 255)]
    [InlineData(8, 0)]
    [InlineData(9, 17)]
    [InlineData(25, 17)]
    [InlineData(26, 34)]
    [InlineData(0, 0)]
    public void Bc2SetAlpha_RoundsToNearestStep(int alpha, int expected)
    {
        var block = new Bc2AlphaBlock();
        block.SetAlpha(5, (byte)alpha);

        Assert.Equal((byte)expected, block.GetAlpha(5));
    }

    [Fact]
    public void Bc2SetAlpha_ErrorWithinHalfStep()
    {
        for (var alpha = 0; alpha < 256; alpha++)
        {
            var block = new Bc2AlphaBlock();
            block.SetAlpha(0, (byte)alpha);

            Assert.InRange(Math.Abs(block.GetAlpha(0) - alpha), 0, 8);
        }
    }

    [Fact]
    public void InterpolateFourthAtc_DoesNotWrapNegative()
    {
        var c0 = new ColorRgb24(10, 10, 10);
        var c1 = new ColorRgb24(200, 200, 200);

        var result = Interpolation.InterpolateFourthAtc(c0, c1, 1);

        Assert.Equal(0, result.r);
        Assert.Equal(0, result.g);
        Assert.Equal(0, result.b);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bc7InitialEndpoints_ClampPcaExtremesOutsideCube(bool extremesAboveOne)
    {
        var block = new RawBlock4X4Rgba32();
        var pixels = block.AsSpan;
        var corner = extremesAboveOne ? (byte)255 : (byte)0;
        for (var i = 0; i < 16; i++)
        {
            pixels[i] = i switch
            {
                < 6 => new ColorRgba32(255, 0, 0, 255),
                < 12 => new ColorRgba32(0, 255, 0, 255),
                _ => new ColorRgba32(corner, corner, 0, 255)
            };
        }

        PcaVectors.CreateWithAlpha(pixels, out var mean, out var pa);
        var minD = 0f;
        var maxD = 0f;
        foreach (var p in pixels)
        {
            var d = Vector4.Dot(new Vector4(p.r / 255f, p.g / 255f, p.b / 255f, p.a / 255f) - mean, pa);
            minD = Math.Min(minD, d);
            maxD = Math.Max(maxD, d);
        }

        var rawMin = mean + pa * minD;
        var rawMax = mean + pa * maxD;
        Assert.True(IsOutsideUnitCube(rawMin) || IsOutsideUnitCube(rawMax));

        PcaVectors.GetExtremePointsWithAlpha(pixels, mean, pa, out var min, out var max);
        Assert.Equal(Vector4.Clamp(rawMin, Vector4.Zero, Vector4.One), min);
        Assert.Equal(Vector4.Clamp(rawMax, Vector4.Zero, Vector4.One), max);

        Bc7EncodingHelpers.GetInitialUnscaledEndpoints(block, out var ep0, out var ep1);

        AssertClampedEndpoint(rawMin, ep0);
        AssertClampedEndpoint(rawMax, ep1);
    }

    [Fact]
    public void Bc7InitialSubsetEndpoints_UseOnlySubsetPixels()
    {
        var partitionTable = Bc7Block.Subsets2PartitionTable[0];
        var block = new RawBlock4X4Rgba32();
        var pixels = block.AsSpan;
        var toggle = false;
        for (var i = 0; i < 16; i++)
        {
            if (partitionTable[i] == 0)
            {
                var v = toggle ? (byte)150 : (byte)100;
                toggle = !toggle;
                pixels[i] = new ColorRgba32(v, v, v, 255);
            }
            else
            {
                pixels[i] = new ColorRgba32(0, 0, 0, 255);
            }
        }

        Bc7EncodingHelpers.GetInitialUnscaledEndpointsForSubset(block, out var ep0, out var ep1, partitionTable, 0);

        foreach (var ep in new[] { ep0, ep1 })
        {
            Assert.InRange(ep.r, (byte)95, (byte)155);
            Assert.InRange(ep.g, (byte)95, (byte)155);
            Assert.InRange(ep.b, (byte)95, (byte)155);
        }
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 0, 1)]
    [InlineData(0, 2, 0)]
    public void Bc7GetSharedPBit_CombinesVotesOfBothEndpoints(int low0, int low1, int expected)
    {
        var ep0 = new ColorRgba32((byte)(0x40 | low0), (byte)(0x80 | low0), (byte)(0xC0 | low0), 255);
        var ep1 = new ColorRgba32((byte)(0x10 | low1), (byte)(0x20 | low1), (byte)(0x30 | low1), 255);

        Assert.Equal((byte)expected, Bc7EncodingHelpers.GetSharedPBit(ep0, ep1, Bc7BlockType.Type1));
    }

    [Fact]
    public void Bc7Mode1_UniformSubsets_DecodeCloseToSource()
    {
        var block = new RawBlock4X4Rgba32();
        var pixels = block.AsSpan;
        for (var i = 0; i < 16; i++)
            pixels[i] = (i % 4) < 2 ? new ColorRgba32(201, 91, 13, 255) : new ColorRgba32(37, 180, 222, 255);

        var encoded = Bc7Mode1Encoder.EncodeBlock(block, 2, 0);
        var decoded = encoded.Decode().AsSpan;

        for (var i = 0; i < 16; i++)
        {
            Assert.InRange(Math.Abs(decoded[i].r - pixels[i].r), 0, 4);
            Assert.InRange(Math.Abs(decoded[i].g - pixels[i].g), 0, 4);
            Assert.InRange(Math.Abs(decoded[i].b - pixels[i].b), 0, 4);
        }
    }

    [Fact]
    public void YCbCrLuminance_GreyMapsBackExactly()
    {
        var pixels = new ColorRgba32[256];
        for (var v = 0; v < 256; v++)
            pixels[v] = new ColorRgba32((byte)v, (byte)v, (byte)v, 255);

        var raw = new RawLuminanceEncoder(true).Encode(pixels);

        for (var v = 0; v < 256; v++)
        {
            Assert.Equal((byte)v, raw[v]);
            Assert.Equal((byte)v, ComponentHelper.ColorToComponent(pixels[v], ColorComponent.Luminance));
        }
    }

    [Theory]
    [InlineData(CompressionQuality.Fast)]
    [InlineData(CompressionQuality.Balanced)]
    [InlineData(CompressionQuality.BestQuality)]
    public void Bc1Alpha_AlphaAboveCutoff_EncodesFourColorMode(CompressionQuality quality)
    {
        var block = new RawBlock4X4Rgba32();
        var pixels = block.AsSpan;
        for (var i = 0; i < 16; i++)
            pixels[i] = (i % 2 == 0) ? new ColorRgba32(255, 0, 0, 200) : new ColorRgba32(0, 0, 255, 128);

        var encoded = new Bc1AlphaBlockEncoder().EncodeBlock(block, quality);

        Assert.True(encoded.color0.data > encoded.color1.data);

        var decoded = encoded.Decode(true).AsSpan;
        for (var i = 0; i < 16; i++)
            Assert.Equal(255, decoded[i].a);
    }

    [Fact]
    public void Bc1Alpha_AlphaBelowCutoff_StillEncodesTransparent()
    {
        var block = new RawBlock4X4Rgba32();
        var pixels = block.AsSpan;
        for (var i = 0; i < 16; i++)
            pixels[i] = (i % 2 == 0) ? new ColorRgba32(255, 0, 0, 255) : new ColorRgba32(0, 0, 0, 127);

        var encoded = new Bc1AlphaBlockEncoder().EncodeBlock(block, CompressionQuality.Balanced);

        Assert.True(encoded.color0.data <= encoded.color1.data);

        var decoded = encoded.Decode(true).AsSpan;
        for (var i = 0; i < 16; i++)
            Assert.Equal(i % 2 == 0 ? 255 : 0, decoded[i].a);
    }

    static bool IsOutsideUnitCube(Vector4 v) =>
        v.X < 0 || v.X > 1 || v.Y < 0 || v.Y > 1 || v.Z < 0 || v.Z > 1 || v.W < 0 || v.W > 1;

    static void AssertClampedEndpoint(Vector4 expected, ColorRgba32 actual)
    {
        AssertClampedComponent(expected.X, actual.r);
        AssertClampedComponent(expected.Y, actual.g);
        AssertClampedComponent(expected.Z, actual.b);
        AssertClampedComponent(expected.W, actual.a);
    }

    static void AssertClampedComponent(float expected, byte actual)
    {
        var scaled = expected * 255;
        if (scaled <= 0)
            Assert.Equal(0, actual);
        else if (scaled >= 255)
            Assert.Equal(255, actual);
        else
            Assert.InRange(Math.Abs(actual - scaled), 0f, 1f);
    }
}
