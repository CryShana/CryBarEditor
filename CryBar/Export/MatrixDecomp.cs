using System.Numerics;

namespace CryBar.Export;

public static class MatrixDecomp
{
    // Converts a glTF column-major flat float[16] into a System.Numerics Matrix4x4.
    // glTF column-major: m[0..3]=col0, m[4..7]=col1, ... System.Numerics uses row-vector convention,
    // so column j of the col-vector matrix becomes row j of the System.Numerics matrix.
    public static Matrix4x4 ColMajorToMatrix(float[] m) => new(
        m[0],  m[1],  m[2],  m[3],
        m[4],  m[5],  m[6],  m[7],
        m[8],  m[9],  m[10], m[11],
        m[12], m[13], m[14], m[15]);

    // Decomposes a column-major 4x4 affine matrix into translation, rotation, scale.
    // Column-major columns become rows in System.Numerics row-vector Matrix4x4.
    public static void Decompose(float[] m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        translation = new Vector3(m[12], m[13], m[14]);

        var col0 = new Vector3(m[0], m[1], m[2]);
        var col1 = new Vector3(m[4], m[5], m[6]);
        var col2 = new Vector3(m[8], m[9], m[10]);
        scale = new Vector3(col0.Length(), col1.Length(), col2.Length());

        if (scale.X > 0) col0 /= scale.X;
        if (scale.Y > 0) col1 /= scale.Y;
        if (scale.Z > 0) col2 /= scale.Z;

        // A reflection can't be a quaternion; fold it into a negative X scale.
        if (Vector3.Dot(Vector3.Cross(col0, col1), col2) < 0)
        {
            scale.X = -scale.X;
            col0 = -col0;
        }

        // System.Numerics uses row-vector convention (v' = v * M),
        // so column-major columns become rows (transpose)
        var rotMatrix = new Matrix4x4(
            col0.X, col0.Y, col0.Z, 0,
            col1.X, col1.Y, col1.Z, 0,
            col2.X, col2.Y, col2.Z, 0,
            0, 0, 0, 1);
        rotation = Quaternion.CreateFromRotationMatrix(rotMatrix);
    }

    public static float[] Compose(Vector3 t, Quaternion r, Vector3 s)
    {
        var matS = Matrix4x4.CreateScale(s);
        var matR = Matrix4x4.CreateFromQuaternion(r);
        var matT = Matrix4x4.CreateTranslation(t);
        return MatrixToColMajor(matS * matR * matT);
    }

    public static float[] MatrixToColMajor(Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    // An out-of-range ParentIndex is treated as a root.
    public static Matrix4x4[] ComputeBoneWorldMatrices(GlbBone[] bones)
    {
        const byte Pending = 0, Visiting = 1, Done = 2;

        var world = new Matrix4x4[bones.Length];
        var state = new byte[bones.Length];
        var chain = new List<int>();

        for (int i = 0; i < bones.Length; i++)
        {
            if (state[i] == Done) continue;

            chain.Clear();
            int cur = i;
            while ((uint)cur < (uint)bones.Length && state[cur] == Pending)
            {
                state[cur] = Visiting;
                chain.Add(cur);
                cur = bones[cur].ParentIndex;
            }

            if ((uint)cur < (uint)bones.Length && state[cur] == Visiting)
                throw new InvalidDataException($"Bone hierarchy contains a cycle through '{bones[cur].Name}'.");

            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int b = chain[k];
                var local = ColMajorToMatrix(bones[b].LocalMatrix);
                int parent = bones[b].ParentIndex;
                world[b] = (uint)parent < (uint)bones.Length ? local * world[parent] : local;
                state[b] = Done;
            }
        }
        return world;
    }
}
