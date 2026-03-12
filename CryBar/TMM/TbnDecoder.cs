namespace CryBar.TMM;

/// <summary>
/// Decodes and encodes TBN (tangent/bitangent/normal) quaternions
/// from the packed u16×3 format used in TMM vertex data.
/// </summary>
public static class TbnDecoder
{
    /// <summary>
    /// Maps unsigned 15-bit value (0..32767) to signed float (-1..+1).
    /// </summary>
    public static float U15ToFloat(int v) => (v / 32767.0f) * 2.0f - 1.0f;

    /// <summary>
    /// Maps signed float (-1..+1) to unsigned 15-bit value (0..32767).
    /// </summary>
    public static int FloatToU15(float f) => (int)MathF.Round(((f + 1.0f) * 0.5f) * 32767.0f) & 0x7FFF;

    /// <summary>
    /// Unpacks a TBN quaternion from three u16 values.
    /// MSB of x is the handedness bit (1 = UV-mirrored face).
    /// Returns (x, y, z, w, handedness).
    /// </summary>
    public static (float x, float y, float z, float w, int handedness) QuatFromPacked(ushort u16X, ushort u16Y, ushort u16Z)
    {
        int handedness = (u16X >> 15) & 1;
        float x = U15ToFloat(u16X & 0x7FFF);
        float y = U15ToFloat(u16Y & 0x7FFF);
        float z = U15ToFloat(u16Z & 0x7FFF);

        float wSq = MathF.Max(0.0f, 1.0f - (x * x + y * y + z * z));
        float w = MathF.Sqrt(wSq);
        if (handedness != 0) w = -w;

        float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (mag > 0.0f)
        {
            x /= mag; y /= mag; z /= mag; w /= mag;
        }

        return (x, y, z, w, handedness);
    }

    /// <summary>
    /// Decodes a TBN quaternion into game-space tangent, bitangent and normal vectors.
    /// When handedness=1, bitangent is flipped back to recover the true game-space B.
    /// Vectors are in game coordinate space (Y-up left-handed).
    /// </summary>
    public static (
        (float x, float y, float z) tangent,
        (float x, float y, float z) bitangent,
        (float x, float y, float z) normal
    ) QuatToTbn(float qx, float qy, float qz, float qw, int handedness = 0)
    {
        float xx = qx * qx, yy = qy * qy, zz = qz * qz;
        float xy = qx * qy, xz = qx * qz, yz = qy * qz;
        float wx = qw * qx, wy = qw * qy, wz = qw * qz;

        // Game-space column vectors (Y-up)
        var tg = (x: 1 - 2 * (yy + zz), y: 2 * (xy + wz), z: 2 * (xz - wy));
        var bg = (x: 2 * (xy - wz), y: 1 - 2 * (xx + zz), z: 2 * (yz + wx));
        var ng = (x: 2 * (xz + wy), y: 2 * (yz - wx), z: 1 - 2 * (xx + yy));

        // Recover true B for UV-mirrored faces
        if (handedness != 0)
            bg = (-bg.x, -bg.y, -bg.z);

        return (tg, bg, ng);
    }

    /// <summary>
    /// Decodes packed TBN u16 values directly to a game-space normal vector.
    /// Convenience method combining QuatFromPacked + QuatToTbn.
    /// </summary>
    public static (float x, float y, float z) DecodeNormal(ushort u16X, ushort u16Y, ushort u16Z)
    {
        var (qx, qy, qz, qw, hand) = QuatFromPacked(u16X, u16Y, u16Z);
        var (_, _, normal) = QuatToTbn(qx, qy, qz, qw, hand);
        return normal;
    }
}
