using System.Buffers.Binary;
using System.Numerics;

namespace CryBar.TMM;

/// <summary>
/// Decodes TMA animation tracks into translation/rotation/scale arrays.
/// </summary>
public static class TmaDecoder
{
    const float InvSqrt3 = 0.57735026919f; // 1/sqrt(3)

    /// <summary>Decoded animation data for a single track.</summary>
    public sealed class DecodedTrack
    {
        public required string Name { get; init; }
        public required Vector3[] Translations { get; init; }
        public required Quaternion[] Rotations { get; init; }
        public required Vector3[] Scales { get; init; }
    }

    /// <summary>Decodes all tracks. Returns null if not parsed or no tracks.</summary>
    public static DecodedTrack[]? DecodeAllTracks(TmaFile tma)
    {
        if (!tma.Parsed || tma.Tracks == null) return null;

        var result = new DecodedTrack[tma.Tracks.Length];
        for (int i = 0; i < tma.Tracks.Length; i++)
            result[i] = DecodeTrack(tma.Tracks[i]);
        return result;
    }

    /// <summary>Decodes a single track into translation/rotation/scale arrays.</summary>
    public static DecodedTrack DecodeTrack(TmaTrack track)
    {
        return new DecodedTrack
        {
            Name = track.Name,
            Translations = DecodeTranslations(track),
            Rotations = DecodeRotations(track),
            Scales = DecodeScales(track),
        };
    }

    static Vector3[] DecodeTranslations(TmaTrack track)
    {
        return track.TranslationEncoding switch
        {
            TmaEncoding.Constant => DecodeConstantVec3(track.TranslationData, track.KeyframeCount),
            TmaEncoding.Raw => DecodeRawVec3(track.TranslationData, track.KeyframeCount),
            _ => DecodeConstantVec3(track.TranslationData, track.KeyframeCount),
        };
    }

    static Quaternion[] DecodeRotations(TmaTrack track)
    {
        return track.RotationEncoding switch
        {
            TmaEncoding.Constant => DecodeConstantQuat(track.RotationData, track.KeyframeCount),
            TmaEncoding.Raw => DecodeRawQuat(track.RotationData, track.KeyframeCount),
            TmaEncoding.Quat32 => DecodeQuat32(track.RotationData, track.KeyframeCount),
            TmaEncoding.Quat64 => DecodeQuat64(track.RotationData, track.KeyframeCount),
            _ => DecodeConstantQuat(track.RotationData, track.KeyframeCount),
        };
    }

    static Vector3[] DecodeScales(TmaTrack track)
    {
        return track.ScaleEncoding switch
        {
            TmaEncoding.Constant => DecodeConstantVec3(track.ScaleData, track.KeyframeCount),
            TmaEncoding.Raw => DecodeRawVec3(track.ScaleData, track.KeyframeCount),
            _ => DecodeConstantVec3(track.ScaleData, track.KeyframeCount),
        };
    }

    /// <summary>Single Vec3 from 16 bytes, repeated for all keyframes.</summary>
    static Vector3[] DecodeConstantVec3(byte[] data, int keyframeCount)
    {
        if (data.Length < 12) return [Vector3.Zero];
        var x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0));
        var y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8));
        var val = new Vector3(x, y, z);
        int count = Math.Max(1, keyframeCount);
        var result = new Vector3[count];
        Array.Fill(result, val);
        return result;
    }

    /// <summary>N keyframes, 12 bytes each (x,y,z floats).</summary>
    static Vector3[] DecodeRawVec3(byte[] data, int keyframeCount)
    {
        int count = Math.Max(1, keyframeCount);
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 12;
            if (off + 12 > data.Length) break;
            result[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off + 8)));
        }
        return result;
    }

    /// <summary>Single quaternion from 16 bytes (XYZW), repeated for all keyframes.</summary>
    static Quaternion[] DecodeConstantQuat(byte[] data, int keyframeCount)
    {
        if (data.Length < 16) return [Quaternion.Identity];
        var x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0));
        var y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8));
        var w = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(12));
        var val = new Quaternion(x, y, z, w);
        int count = Math.Max(1, keyframeCount);
        var result = new Quaternion[count];
        Array.Fill(result, val);
        return result;
    }

    /// <summary>N keyframes, 16 bytes each (XYZW floats).</summary>
    static Quaternion[] DecodeRawQuat(byte[] data, int keyframeCount)
    {
        int count = Math.Max(1, keyframeCount);
        var result = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 16;
            if (off + 16 > data.Length) break;
            result[i] = new Quaternion(
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off + 8)),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(off + 12)));
        }
        return result;
    }

    /// <summary>Quat32 "smallest three": 2-bit index + 10+10+10 signed components (4 bytes/kf).</summary>
    static Quaternion[] DecodeQuat32(byte[] data, int keyframeCount)
    {
        int count = Math.Max(1, keyframeCount);
        var result = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 4;
            if (off + 4 > data.Length) break;
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            result[i] = DecodeSmallestThree32(packed);
        }
        return result;
    }

    /// <summary>Quat64 "smallest three": 2-bit index + 22+20+20 signed components (8 bytes/kf).</summary>
    static Quaternion[] DecodeQuat64(byte[] data, int keyframeCount)
    {
        int count = Math.Max(1, keyframeCount);
        var result = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 8;
            if (off + 8 > data.Length) break;
            ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            result[i] = DecodeSmallestThree64(packed);
        }
        return result;
    }

    static Quaternion DecodeSmallestThree32(uint packed)
    {
        int idx = (int)(packed >> 30) & 3;
        int rawA = (int)((packed >> 20) & 0x3FF);  // 10 bits
        int rawB = (int)((packed >> 10) & 0x3FF);
        int rawC = (int)(packed & 0x3FF);

        // signed two's complement, scaled to [-1/sqrt(3), +1/sqrt(3)]
        float fa = SignedComponent(rawA, 10) * InvSqrt3;
        float fb = SignedComponent(rawB, 10) * InvSqrt3;
        float fc = SignedComponent(rawC, 10) * InvSqrt3;
        float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

        return AssembleQuaternion(idx, fa, fb, fc, fw);
    }

    static Quaternion DecodeSmallestThree64(ulong packed)
    {
        // top 2 bits = index, then 22+20+20 bit components
        int idx = (int)(packed >> 62) & 3;
        int rawA = (int)((packed >> 40) & 0x3FFFFF);  // 22 bits
        int rawB = (int)((packed >> 20) & 0xFFFFF);    // 20 bits
        int rawC = (int)(packed & 0xFFFFF);             // 20 bits

        float fa = SignedComponent(rawA, 22) * InvSqrt3;
        float fb = SignedComponent(rawB, 20) * InvSqrt3;
        float fc = SignedComponent(rawC, 20) * InvSqrt3;
        float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

        return AssembleQuaternion(idx, fa, fb, fc, fw);
    }

    /// <summary>Unsigned N-bit value to signed float via sign-magnitude.
    /// MSB = sign (0=positive, 1=negative), remaining bits = magnitude.</summary>
    static float SignedComponent(int raw, int bits)
    {
        int signBit = 1 << (bits - 1);
        int magnitude = raw & (signBit - 1);
        float value = magnitude / (float)signBit;
        return (raw & signBit) != 0 ? -value : value;
    }

    /// <summary>
    /// Reassembles quaternion from "smallest three" with WXYZ index convention.
    /// Index 0=W is largest (remaining: X,Y,Z), 1=X, 2=Y, 3=Z.
    /// The three stored components (a,b,c) are the remaining ones in WXYZ order.
    /// </summary>
    static Quaternion AssembleQuaternion(int largestIndex, float a, float b, float c, float reconstructed)
    {
        return largestIndex switch
        {
            0 => new Quaternion(a, b, c, reconstructed),       // W largest → a=X, b=Y, c=Z
            1 => new Quaternion(reconstructed, b, c, a),       // X largest → a=W, b=Y, c=Z
            2 => new Quaternion(b, reconstructed, c, a),       // Y largest → a=W, b=X, c=Z
            3 => new Quaternion(b, c, reconstructed, a),       // Z largest → a=W, b=X, c=Y
            _ => Quaternion.Identity,
        };
    }
}
