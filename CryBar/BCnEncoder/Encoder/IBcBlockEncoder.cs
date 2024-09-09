using System;

using CryBar.BCnEncoder.Shared;
using CryBar.BCnEncoder.Shared.ImageFiles;

namespace CryBar.BCnEncoder.Encoder
{
    internal interface IBcBlockEncoder<T> where T : unmanaged
    {
        byte[] Encode(T[] blocks, int blockWidth, int blockHeight, CompressionQuality quality, OperationContext context);
        void EncodeBlock(T block, CompressionQuality quality, Span<byte> output);
        GlInternalFormat GetInternalFormat();
        GlFormat GetBaseInternalFormat();
        DxgiFormat GetDxgiFormat();
        int GetBlockSize();
    }


}
