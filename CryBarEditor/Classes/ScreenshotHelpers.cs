using System;
using System.IO;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBarEditor.Classes;

public static class ScreenshotHelpers
{
    public static void FlipRowsInPlace(byte[] rgba, int width, int height)
    {
        int stride = width * 4;
        var tmp = new byte[stride];

        for (int y = 0; y < height / 2; y++)
        {
            var top = rgba.AsSpan(y * stride, stride);
            var bottom = rgba.AsSpan((height - 1 - y) * stride, stride);
            top.CopyTo(tmp);
            bottom.CopyTo(top);
            tmp.CopyTo(bottom);
        }
    }

    // Compression is CPU-bound and runs on the calling thread even with SaveAsync,
    // so hop to the thread pool to keep the UI responsive.
    public static Task EncodeAsync(byte[] rgba, int width, int height, string format, Stream output)
        => Task.Run(() =>
        {
            using var image = Image.WrapMemory<Rgba32>(rgba.AsMemory(), width, height);

            if (format == "png")
                image.Save(output, new PngEncoder());
            else
                image.Save(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 90 });
        });
}
