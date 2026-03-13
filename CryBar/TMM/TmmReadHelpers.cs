using System.Buffers.Binary;
using System.Text;

namespace CryBar.TMM;

/// <summary>Shared low-level binary read helpers used by TmmFile and TmaFile.</summary>
internal static class TmmReadHelpers
{
    internal const int DefaultMaxNameLength = 5000;

    internal static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    internal static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    internal static bool TryReadFloat(ReadOnlySpan<byte> data, ref int offset, out float value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    internal static bool TryReadUTF16String(ReadOnlySpan<byte> data, ref int offset, out string value,
        int maxLength = DefaultMaxNameLength)
    {
        value = "";
        if (offset + 4 > data.Length) return false;
        var charCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        if (charCount < 0 || charCount > maxLength) return false;
        var byteLength = charCount * 2;
        if (offset + byteLength > data.Length) return false;
        value = Encoding.Unicode.GetString(data.Slice(offset, byteLength));
        offset += byteLength;
        return true;
    }

    internal static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
            offset += 4;
        }
        return result;
    }
}
