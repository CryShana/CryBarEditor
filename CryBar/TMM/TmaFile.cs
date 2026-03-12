using System.Buffers.Binary;
using System.Text;

namespace CryBar.TMM;

/// <summary>
/// Parses .tma animation files (Age of Mythology: Retold).
/// Currently header-only — full body parsing is deferred to a future iteration.
/// </summary>
public class TmaFile
{
    public bool Parsed { get; private set; }

    /// <summary>File signature (expected: "BTMA" = 0x414D5442).</summary>
    public uint Signature { get; private set; }

    /// <summary>Format version.</summary>
    public uint Version { get; private set; }

    /// <summary>Total file size in bytes.</summary>
    public int FileSize { get; private set; }

    /// <summary>Number of bones/channels in the animation.</summary>
    public uint NumBones { get; private set; }

    /// <summary>Number of keyframes.</summary>
    public uint NumKeyframes { get; private set; }

    /// <summary>Animation duration or frame count.</summary>
    public float Duration { get; private set; }

    /// <summary>Raw header bytes for inspection (first 64 bytes or file length, whichever is smaller). Populated on first call to GetSummary().</summary>
    public byte[]? HeaderBytes { get; private set; }

    readonly ReadOnlyMemory<byte> _data;

    public TmaFile(ReadOnlyMemory<byte> data)
    {
        _data = data;
        FileSize = data.Length;
    }

    public bool Parse()
    {
        var data = _data.Span;
        if (data.Length < 16) return false;

        // Require "BTMA" signature (0x42, 0x54, 0x4D, 0x41)
        if (data is not [0x42, 0x54, 0x4D, 0x41, ..]) return false;

        var offset = 0;
        Signature = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        Version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;

        if (offset + 4 <= data.Length)
            NumBones = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;

        if (offset + 4 <= data.Length)
            NumKeyframes = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;

        if (offset + 4 <= data.Length)
            Duration = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;

        Parsed = true;
        return true;
    }

    /// <summary>
    /// Generates a human-readable summary of the TMA file header.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(TMA not parsed)";

        var sb = new StringBuilder();
        sb.AppendLine($"TMA Animation File");
        sb.AppendLine($"Signature: 0x{Signature:X8} ({SignatureString()})");
        sb.AppendLine($"Version: {Version}");
        sb.AppendLine($"File size: {FileSize:N0} bytes");

        if (NumBones > 0) sb.AppendLine($"Bones/Channels: {NumBones}");
        if (NumKeyframes > 0) sb.AppendLine($"Keyframes: {NumKeyframes}");
        if (Duration > 0) sb.AppendLine($"Duration: {Duration:F3}");

        // Lazily populate header bytes for hex dump
        HeaderBytes ??= _data.Span[..Math.Min(64, _data.Length)].ToArray();

        if (HeaderBytes.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Header (hex):");
            for (int i = 0; i < HeaderBytes.Length; i += 16)
            {
                var count = Math.Min(16, HeaderBytes.Length - i);
                sb.Append($"  {i:X4}: ");
                for (int j = 0; j < count; j++)
                    sb.Append($"{HeaderBytes[i + j]:X2} ");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    string SignatureString()
    {
        var bytes = BitConverter.GetBytes(Signature);
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
        return new string(chars);
    }
}
