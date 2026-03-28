using System.Buffers.Binary;
using System.Numerics;

namespace CryBar.TMM;

/// <summary>
/// Decodes TMA animation tracks into translation/rotation/scale arrays.
/// </summary>
public static class TmaDecoder
{
    const float InvSqrt2 = 0.70710678118f; // 1/sqrt(2)
    const float Quat64MaxMagnitude = 524287f; // 2^19 - 1
    const float Quat32MaxMagnitude = 511f;    // 2^9 - 1

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

    /// <summary>Quat32 "smallest three": 2-bit index + 3×10-bit signed components (4 bytes/kf).</summary>
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

    /// <summary>Quat64 "smallest three": 4-bit index + 3×20-bit signed components (8 bytes/kf).</summary>
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
        // Layout: [2-bit index][10-bit C2][10-bit C1][10-bit C0]
        // Each 10-bit: bit 9 = sign, bits 8-0 = magnitude
        // Extraction order: low→high = C0, C1, C2
        // Index: 0=X, 1=Y, 2=Z, 3=W is reconstructed
        int idx = (int)(packed >> 30) & 3;

        float[] q = new float[4];
        uint bits = packed;
        for (int comp = 3; comp >= 0; comp--)
        {
            if (comp == idx) continue;
            int magnitude = (int)(bits & 0x1FF);  // 9-bit magnitude
            bool negative = ((bits >> 9) & 1) != 0;
            float val = (magnitude / Quat32MaxMagnitude) * InvSqrt2;
            if (negative) val = -val;
            q[comp] = val;
            bits >>= 10;
        }
        q[idx] = MathF.Sqrt(MathF.Max(0, 1.0f - q[0] * q[0] - q[1] * q[1] - q[2] * q[2] - q[3] * q[3]));

        return new Quaternion(q[0], q[1], q[2], q[3]);
    }

    static Quaternion DecodeSmallestThree64(ulong packed)
    {
        // Layout: [4-bit index][20-bit C2][20-bit C1][20-bit C0]
        // Each 20-bit: bit 19 = sign, bits 18-0 = magnitude (max 524287)
        // Extraction order: low→high = C0, C1, C2
        // Index: 0=X, 1=Y, 2=Z, 3=W is reconstructed
        int idx = (int)(packed >> 60) & 0xF;
        if (idx > 3) idx = 3; // safety clamp

        float[] q = new float[4];
        ulong bits = packed;
        for (int comp = 3; comp >= 0; comp--)
        {
            if (comp == idx) continue;
            int magnitude = (int)(bits & 0x7FFFF);  // 19-bit magnitude
            bool negative = ((bits >> 19) & 1) != 0;
            float val = (magnitude / Quat64MaxMagnitude) * InvSqrt2;
            if (negative) val = -val;
            q[comp] = val;
            bits >>= 20;
        }
        q[idx] = MathF.Sqrt(MathF.Max(0, 1.0f - q[0] * q[0] - q[1] * q[1] - q[2] * q[2] - q[3] * q[3]));

        return new Quaternion(q[0], q[1], q[2], q[3]);
    }
}
