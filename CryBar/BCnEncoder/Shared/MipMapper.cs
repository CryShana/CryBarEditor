using System;
using System.Threading.Tasks;

using CommunityToolkit.HighPerformance;

namespace CryBar.BCnEncoder.Shared
{
    internal static class MipMapper
    {
        /// <summary>
        /// Generate a chain of <paramref name="numMipMaps"/> elements.
        /// </summary>
        /// <param name="input">The original image to scale down.</param>
        /// <param name="width">The original image width.</param>
        /// <param name="height">The original image height.</param>
        /// <param name="numMipMaps">The number of mipmaps to generate.</param>
        /// <returns>Will generate as many mipmaps as possible until a mipmap of 1x1 is reached for <paramref name="numMipMaps"/> 0 or smaller.</returns>
        public static ReadOnlyMemory2D<ColorRgba32>[] GenerateMipChain(ReadOnlyMemory<ColorRgba32> input, int width, int height, ref int numMipMaps)
        {
            return GenerateMipChain(input.AsMemory2D(height, width), ref numMipMaps);
        }

        /// <summary>
        /// Generate a chain of <paramref name="numMipMaps"/> elements.
        /// </summary>
        /// <param name="pixels">The original image to scale down.</param>
        /// <param name="numMipMaps">The number of mipmaps to generate.</param>
        /// <returns>Will generate as many mipmaps as possible until a mipmap of 1x1 is reached for <paramref name="numMipMaps"/> 0 or smaller.</returns>
        public static ReadOnlyMemory2D<ColorRgba32>[] GenerateMipChain(ReadOnlyMemory2D<ColorRgba32> pixels, ref int numMipMaps)
        {
            var width = pixels.Width;
            var height = pixels.Height;
            var mipChainLength = CalculateMipChainLength(width, height, numMipMaps);

            var result = new ReadOnlyMemory2D<ColorRgba32>[mipChainLength];
            result[0] = pixels;

            // If only one mipmap was requested, return original image only
            if (numMipMaps == 1)
            {
                return result;
            }

            // If number of mipmaps is "marked as boundless", do as many mipmaps as it takes to reach a size of 1x1
            if (numMipMaps <= 0)
            {
                numMipMaps = int.MaxValue;
            }

            // Generate mipmaps
            for (var mipLevel = 1; mipLevel < numMipMaps; mipLevel++)
            {
                var mipWidth = Math.Max(1, width >> mipLevel);
                var mipHeight = Math.Max(1, height >> mipLevel);

                var newMip = ResizeToHalf(result[mipLevel - 1]);
                result[mipLevel] = newMip;

                // Stop generating if last generated mipmap was of size 1x1
                if (mipWidth == 1 && mipHeight == 1)
                {
                    numMipMaps = mipLevel + 1;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Generate a chain of <paramref name="numMipMaps"/> elements.
        /// </summary>
        /// <param name="input">The original image to scale down.</param>
        /// <param name="width">The original image width.</param>
        /// <param name="height">The original image height.</param>
        /// <param name="numMipMaps">The number of mipmaps to generate.</param>
        /// <returns>Will generate as many mipmaps as possible until a mipmap of 1x1 is reached for <paramref name="numMipMaps"/> 0 or smaller.</returns>
        public static ReadOnlyMemory2D<ColorRgbFloat>[] GenerateMipChain(ReadOnlyMemory<ColorRgbFloat> input, int width, int height, ref int numMipMaps)
        {
            return GenerateMipChain(input.AsMemory2D(height, width), ref numMipMaps);
        }

        /// <summary>
        /// Generate a chain of <paramref name="numMipMaps"/> elements.
        /// </summary>
        /// <param name="pixels">The original image to scale down.</param>
        /// <param name="numMipMaps">The number of mipmaps to generate.</param>
        /// <returns>Will generate as many mipmaps as possible until a mipmap of 1x1 is reached for <paramref name="numMipMaps"/> 0 or smaller.</returns>
        public static ReadOnlyMemory2D<ColorRgbFloat>[] GenerateMipChain(ReadOnlyMemory2D<ColorRgbFloat> pixels, ref int numMipMaps)
        {
            var width = pixels.Width;
            var height = pixels.Height;
            var mipChainLength = CalculateMipChainLength(width, height, numMipMaps);

            var result = new ReadOnlyMemory2D<ColorRgbFloat>[mipChainLength];
            result[0] = pixels;

            // If only one mipmap was requested, return original image only
            if (numMipMaps == 1)
            {
                return result;
            }

            // If number of mipmaps is "marked as boundless", do as many mipmaps as it takes to reach a size of 1x1
            if (numMipMaps <= 0)
            {
                numMipMaps = int.MaxValue;
            }

            // Generate mipmaps
            for (var mipLevel = 1; mipLevel < numMipMaps; mipLevel++)
            {
                var mipWidth = Math.Max(1, width >> mipLevel);
                var mipHeight = Math.Max(1, height >> mipLevel);

                var newMip = ResizeToHalf(result[mipLevel - 1].Span);
                result[mipLevel] = newMip;

                // Stop generating if last generated mipmap was of size 1x1
                if (mipWidth == 1 && mipHeight == 1)
                {
                    numMipMaps = mipLevel + 1;
                    break;
                }
            }

            return result;
        }

        public static int CalculateMipChainLength(int width, int height, int maxNumMipMaps)
        {
            if (maxNumMipMaps == 1)
            {
                return 1;
            }

            if (maxNumMipMaps <= 0)
            {
                maxNumMipMaps = int.MaxValue;
            }

            var output = 0;
            for (var mipLevel = 1; mipLevel <= maxNumMipMaps; mipLevel++)
            {
                var mipWidth = Math.Max(1, width >> mipLevel);
                var mipHeight = Math.Max(1, height >> mipLevel);

                if (mipLevel == maxNumMipMaps)
                {
                    return maxNumMipMaps;
                }

                if (mipWidth == 1 && mipHeight == 1)
                {
                    output = mipLevel + 1;
                    break;
                }
            }

            return output;
        }

        public static void CalculateMipLevelSize(int width, int height, int mipIdx, out int mipWidth, out int mipHeight)
        {
            mipWidth = Math.Max(1, width >> mipIdx);
            mipHeight = Math.Max(1, height >> mipIdx);
        }

        private static ColorRgba32[,] ResizeToHalf(ReadOnlyMemory2D<ColorRgba32> pixelsMemory)
        {
            var oldWidth = pixelsMemory.Width;
            var oldHeight = pixelsMemory.Height;
            var newWidth = Math.Max(1, oldWidth >> 1);
            var newHeight = Math.Max(1, oldHeight >> 1);

            var result = new ColorRgba32[newHeight, newWidth];
            var maxX = oldWidth - 1;
            var maxY = oldHeight - 1;

            void ProcessRow(int y2)
            {
                var pixelsRgba = pixelsMemory.Span;
                var srcY0 = Math.Min(maxY, y2 * 2);
                var srcY1 = Math.Min(maxY, y2 * 2 + 1);
                for (var x2 = 0; x2 < newWidth; x2++)
                {
                    var srcX0 = Math.Min(maxX, x2 * 2);
                    var srcX1 = Math.Min(maxX, x2 * 2 + 1);

                    var ul = pixelsRgba[srcY0, srcX0];
                    var ur = pixelsRgba[srcY0, srcX1];
                    var ll = pixelsRgba[srcY1, srcX0];
                    var lr = pixelsRgba[srcY1, srcX1];

                    result[y2, x2] = new ColorRgba32(
                        (byte)((ul.r + ur.r + ll.r + lr.r + 2) >> 2),
                        (byte)((ul.g + ur.g + ll.g + lr.g + 2) >> 2),
                        (byte)((ul.b + ur.b + ll.b + lr.b + 2) >> 2),
                        (byte)((ul.a + ur.a + ll.a + lr.a + 2) >> 2));
                }
            }

            // Parallel.For startup outweighs the work for tiny mips
            if (newHeight * newWidth < 4096)
            {
                for (var y2 = 0; y2 < newHeight; y2++) ProcessRow(y2);
            }
            else
            {
                Parallel.For(0, newHeight, ProcessRow);
            }

            return result;
        }

        private static ColorRgbFloat[,] ResizeToHalf(ReadOnlySpan2D<ColorRgbFloat> pixelsRgba)
        {

            var oldWidth = pixelsRgba.Width;
            var oldHeight = pixelsRgba.Height;
            var newWidth = Math.Max(1, oldWidth >> 1);
            var newHeight = Math.Max(1, oldHeight >> 1);

            var result = new ColorRgbFloat[newHeight, newWidth];

            int ClampW(int x) => Math.Max(0, Math.Min(oldWidth - 1, x));
            int ClampH(int y) => Math.Max(0, Math.Min(oldHeight - 1, y));

            for (var y2 = 0; y2 < newHeight; y2++)
            {
                for (var x2 = 0; x2 < newWidth; x2++)
                {
                    var ul = pixelsRgba[ClampH(y2 * 2), ClampW(x2 * 2)];
                    var ur = pixelsRgba[ClampH(y2 * 2), ClampW(x2 * 2 + 1)];
                    var ll = pixelsRgba[ClampH(y2 * 2 + 1), ClampW(x2 * 2)];
                    var lr = pixelsRgba[ClampH(y2 * 2 + 1), ClampW(x2 * 2 + 1)];

                    result[y2, x2] = (ul + ur + ll + lr) / 4;
                }
            }

            return result;
        }
    }
}
