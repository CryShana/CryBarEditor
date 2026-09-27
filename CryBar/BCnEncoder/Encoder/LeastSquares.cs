using System.Numerics;
using CryBar.BCnEncoder.Shared;

namespace CryBar.BCnEncoder.Encoder
{
    /// <summary>
    /// Least squares optimization from https://github.com/knarkowicz/GPURealTimeBC6H
    /// Code is public domain
    /// </summary>
    internal static class LeastSquares
    {

        private static int ComputeIndex4(float texelPos, float endPoint0Pos, float endPoint1Pos)
        {
            var r = (texelPos - endPoint0Pos) / (endPoint1Pos - endPoint0Pos);
            return (int)Math.Clamp(r * 15f /*14.93333f + 0.03333f + 0.5f*/, 0.0f, 15.0f);
        }

        private static int ComputeIndex3(float texelPos, float endPoint0Pos, float endPoint1Pos)
        {
            var r = (texelPos - endPoint0Pos) / (endPoint1Pos - endPoint0Pos);
            return (int)Math.Clamp(r * 6.98182f + 0.00909f + 0.5f, 0.0f, 7.0f);
        }

        private static float F32ToF16(float f32, bool signed)
        {
            return Shared.Half.GetSanitizedSignedBits(f32, signed);
        }

        private static Vector3 F32ToF16(Vector3 f32, bool signed)
        {
            return new Vector3(
                F32ToF16(f32.X, signed),
                F32ToF16(f32.Y, signed),
                F32ToF16(f32.Z, signed)
            );
        }

        private static float F16ToF32(float f16)
        {
            float value = Shared.Half.ToHalf((ushort)MathF.Abs(f16));
            return f16 < 0 ? -value : value;
        }

        private static Vector3 F16ToF32(Vector3 f16)
        {
            return new Vector3(
                F16ToF32(f16.X),
                F16ToF32(f16.Y),
                F16ToF32(f16.Z)
            );
        }

        public static void OptimizeEndpoints1Sub(RawBlock4X4RgbFloat block, ref ColorRgbFloat ep0, ref ColorRgbFloat ep1, bool signed)
        {
            var ep0v = ep0.ToVector3();
            var ep1v = ep1.ToVector3();

            var pixels = block.AsSpan;

            var blockDir = ep1v - ep0v;
            blockDir = blockDir / (blockDir.X + blockDir.Y + blockDir.Z);

            var endPoint0Pos = F32ToF16(Vector3.Dot(ep0v, blockDir), true);
            var endPoint1Pos = F32ToF16(Vector3.Dot(ep1v, blockDir), true);

            var alphaTexelSum = new Vector3();
            var betaTexelSum = new Vector3();
            var alphaBetaSum = 0.0f;
            var alphaSqSum = 0.0f;
            var betaSqSum = 0.0f;

            for (var i = 0; i < 16; i++)
            {
                var texelPos = F32ToF16(Vector3.Dot(pixels[i].ToVector3(), blockDir), true);
                var texelIndex = ComputeIndex4(texelPos, endPoint0Pos, endPoint1Pos);

                var beta = Math.Clamp(texelIndex / 15.0f, 0f, 1f);
                var alpha = 1.0f - beta;

                var texelF16 = F32ToF16(pixels[i].ToVector3(), signed);
                alphaTexelSum += alpha * texelF16;
                betaTexelSum += beta * texelF16;

                alphaBetaSum += alpha * beta;

                alphaSqSum += alpha * alpha;
                betaSqSum += beta * beta;
            }

            var det = alphaSqSum * betaSqSum - alphaBetaSum * alphaBetaSum;

            if (MathF.Abs(det) > 0.00001f)
            {
                var detRcp = 1f / det;
                var ep0f16 = detRcp * (alphaTexelSum * betaSqSum - betaTexelSum * alphaBetaSum);
                var ep1f16 = detRcp * (betaTexelSum * alphaSqSum - alphaTexelSum * alphaBetaSum);
                var maxF16 = new Vector3(Shared.Half.MaxValue.Value);
                var minF16 = signed ? -maxF16 : Vector3.Zero;
                ep0f16 = Vector3.Clamp(ep0f16, minF16, maxF16);
                ep1f16 = Vector3.Clamp(ep1f16, minF16, maxF16);
                ep0 = new ColorRgbFloat(F16ToF32(ep0f16));
                ep1 = new ColorRgbFloat(F16ToF32(ep1f16));
            }
        }

        public static void OptimizeEndpoints2Sub(RawBlock4X4RgbFloat block, ref ColorRgbFloat ep0, ref ColorRgbFloat ep1, int partitionSetId, int subsetIndex, bool signed)
        {
            var ep0v = ep0.ToVector3();
            var ep1v = ep1.ToVector3();

            var pixels = block.AsSpan;

            var blockDir = ep1v - ep0v;
            blockDir = blockDir / (blockDir.X + blockDir.Y + blockDir.Z);

            var endPoint0Pos = F32ToF16(Vector3.Dot(ep0v, blockDir), true);
            var endPoint1Pos = F32ToF16(Vector3.Dot(ep1v, blockDir), true);

            var alphaTexelSum = new Vector3();
            var betaTexelSum = new Vector3();
            var alphaBetaSum = 0.0f;
            var alphaSqSum = 0.0f;
            var betaSqSum = 0.0f;

            for (var i = 0; i < 16; i++)
            {
                if (Bc6Block.Subsets2PartitionTable[partitionSetId][i] == subsetIndex)
                {
                    var texelPos = F32ToF16(Vector3.Dot(pixels[i].ToVector3(), blockDir), true);
                    var texelIndex = ComputeIndex3(texelPos, endPoint0Pos, endPoint1Pos);

                    var beta = Math.Clamp(texelIndex / 7.0f, 0f, 1f);
                    var alpha = 1.0f - beta;

                    var texelF16 = F32ToF16(pixels[i].ToVector3(), signed);
                    alphaTexelSum += alpha * texelF16;
                    betaTexelSum += beta * texelF16;

                    alphaBetaSum += alpha * beta;

                    alphaSqSum += alpha * alpha;
                    betaSqSum += beta * beta;
                }
            }

            var det = alphaSqSum * betaSqSum - alphaBetaSum * alphaBetaSum;

            if (MathF.Abs(det) > 0.00001f)
            {
                var detRcp = 1f / det;
                var ep0f16 = detRcp * (alphaTexelSum * betaSqSum - betaTexelSum * alphaBetaSum);
                var ep1f16 = detRcp * (betaTexelSum * alphaSqSum - alphaTexelSum * alphaBetaSum);
                var maxF16 = new Vector3(Shared.Half.MaxValue.Value);
                var minF16 = signed ? -maxF16 : Vector3.Zero;
                ep0f16 = Vector3.Clamp(ep0f16, minF16, maxF16);
                ep1f16 = Vector3.Clamp(ep1f16, minF16, maxF16);
                ep0 = new ColorRgbFloat(F16ToF32(ep0f16));
                ep1 = new ColorRgbFloat(F16ToF32(ep1f16));
            }
        }
    }
}
