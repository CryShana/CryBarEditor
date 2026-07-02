using System.IO;
using System.Threading.Tasks;
using CryBarEditor.Classes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CryBar.Tests;

public class ScreenshotHelpersTests
{
    static byte[] MakeImage(int width, int height)
    {
        // pixel (x, y) = (x, y, x + y, 255 - y) so every row/column is distinguishable
        var data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            data[i + 0] = (byte)x;
            data[i + 1] = (byte)y;
            data[i + 2] = (byte)(x + y);
            data[i + 3] = (byte)(255 - y);
        }
        return data;
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(1, 1)]
    public void FlipRowsInPlace_ReversesRowOrder(int width, int height)
    {
        var original = MakeImage(width, height);
        var flipped = (byte[])original.Clone();

        ScreenshotHelpers.FlipRowsInPlace(flipped, width, height);

        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            var expectedRow = original.AsSpan((height - 1 - y) * stride, stride);
            var actualRow = flipped.AsSpan(y * stride, stride);
            Assert.True(expectedRow.SequenceEqual(actualRow), $"row {y} mismatch");
        }
    }

    [Fact]
    public async Task EncodeAsync_Png_RoundTripsPixelsExactly()
    {
        const int w = 4, h = 3;
        var rgba = MakeImage(w, h);
        using var ms = new MemoryStream();

        await ScreenshotHelpers.EncodeAsync(rgba, w, h, "png", ms);

        ms.Position = 0;
        using var decoded = Image.Load<Rgba32>(ms);
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            var p = decoded[x, y];
            Assert.Equal(rgba[i + 0], p.R);
            Assert.Equal(rgba[i + 1], p.G);
            Assert.Equal(rgba[i + 2], p.B);
            Assert.Equal(rgba[i + 3], p.A);
        }
    }

    [Fact]
    public async Task EncodeAsync_Webp_PreservesDimensionsAndAlpha()
    {
        const int w = 16, h = 16;
        // left half opaque red, right half fully transparent
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (x < w / 2) { rgba[i] = 255; rgba[i + 3] = 255; }
        }
        using var ms = new MemoryStream();

        await ScreenshotHelpers.EncodeAsync(rgba, w, h, "webp", ms);

        ms.Position = 0;
        using var decoded = Image.Load<Rgba32>(ms);
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
        Assert.True(decoded[2, 8].A > 245, "opaque region lost alpha");
        Assert.True(decoded[13, 8].A < 10, "transparent region gained alpha");
    }
}
