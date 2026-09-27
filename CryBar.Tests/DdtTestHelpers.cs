using System.Buffers.Binary;

using CryBar;

namespace CryBar.Tests;

internal static class DdtTestHelpers
{
    const int Rts4FixedHeader = 20;

    internal static byte[] BuildRts4(int width, int height, byte mipLevels,
        int colorTableSize = 0, int? colorTableBytes = null, int payloadBytes = 16,
        DDTFormat format = DDTFormat.DXT1, Func<int, int, (int Offset, int Length)>? mipEntry = null,
        byte[]? payload = null)
    {
        if (payload != null) payloadBytes = payload.Length;

        int ctBytes = colorTableBytes ?? Math.Max(0, colorTableSize);
        int payloadStart = Rts4FixedHeader + ctBytes + mipLevels * 8;
        mipEntry ??= (_, start) => (start, payloadBytes);

        var data = new byte[payloadStart + payloadBytes];
        data[0] = 0x52; data[1] = 0x54; data[2] = 0x53; data[3] = 0x34;
        data[6] = (byte)format;
        data[7] = mipLevels;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), colorTableSize);

        int offset = Rts4FixedHeader + ctBytes;
        for (int i = 0; i < mipLevels; i++)
        {
            var (mipOffset, mipLength) = mipEntry(i, payloadStart);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), mipOffset); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), mipLength); offset += 4;
        }

        payload?.CopyTo(data, payloadStart);
        return data;
    }
}
