using System.Numerics;
using CryBar.Export;

namespace CryBar.Tests;

public class MatrixDecompTests
{
    [Fact]
    public void Compose_DecomposeRoundTrip_PreservesValues()
    {
        var t = new Vector3(1, 2, 3);
        var r = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f);
        var s = new Vector3(2, 3, 4);

        var m = MatrixDecomp.Compose(t, r, s);
        MatrixDecomp.Decompose(m, out var t2, out var r2, out var s2);

        Assert.Equal(t.X, t2.X, 1e-5f);
        Assert.Equal(t.Y, t2.Y, 1e-5f);
        Assert.Equal(t.Z, t2.Z, 1e-5f);
        Assert.Equal(s.X, s2.X, 1e-5f);
        Assert.Equal(s.Y, s2.Y, 1e-5f);
        Assert.Equal(s.Z, s2.Z, 1e-5f);
        Assert.Equal(r.X, r2.X, 1e-5f);
        Assert.Equal(r.Y, r2.Y, 1e-5f);
        Assert.Equal(r.Z, r2.Z, 1e-5f);
        Assert.Equal(r.W, r2.W, 1e-5f);
    }

    [Theory]
    [InlineData(-2f, 3f, 4f)]
    [InlineData(2f, -3f, 4f)]
    [InlineData(2f, 3f, -4f)]
    [InlineData(-1f, -1f, -1f)]
    public void Decompose_MirroredMatrix_RecomposesToOriginal(float sx, float sy, float sz)
    {
        var m = MatrixDecomp.Compose(
            new Vector3(0.5f, -1.5f, 2f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f)),
            new Vector3(sx, sy, sz));

        MatrixDecomp.Decompose(m, out var t, out var r, out var s);

        Assert.True(s.X < 0, "reflection must be folded into a negative X scale");
        Assert.True(s.Y > 0 && s.Z > 0);
        Assert.Equal(1f, r.Length(), 1e-4f);

        var back = MatrixDecomp.Compose(t, r, s);
        for (int i = 0; i < 16; i++)
            Assert.Equal(m[i], back[i], 1e-4f);
    }

    [Fact]
    public void ComputeBoneWorldMatrices_ChildBeforeParent_MatchesParentFirstOrder()
    {
        var rootLocal = MatrixDecomp.Compose(new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), Vector3.One);
        var midLocal = MatrixDecomp.Compose(new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), Vector3.One);
        var leafLocal = MatrixDecomp.Compose(new Vector3(0, 0, 3), Quaternion.Identity, new Vector3(2, 2, 2));

        var parentFirst = new[]
        {
            Bone("root", -1, rootLocal),
            Bone("mid", 0, midLocal),
            Bone("leaf", 1, leafLocal),
        };
        var childFirst = new[]
        {
            Bone("leaf", 1, leafLocal),
            Bone("mid", 2, midLocal),
            Bone("root", -1, rootLocal),
        };

        var expected = MatrixDecomp.ComputeBoneWorldMatrices(parentFirst);
        var actual = MatrixDecomp.ComputeBoneWorldMatrices(childFirst);

        AssertMatrixEqual(expected[2], actual[0]);
        AssertMatrixEqual(expected[1], actual[1]);
        AssertMatrixEqual(expected[0], actual[2]);
    }

    [Fact]
    public void ComputeBoneWorldMatrices_Cycle_Throws()
    {
        var id = TmmTestHelpers.Identity16Local();
        var bones = new[] { Bone("a", 1, id), Bone("b", 0, id) };

        Assert.Throws<InvalidDataException>(() => MatrixDecomp.ComputeBoneWorldMatrices(bones));
    }

    static GlbBone Bone(string name, int parent, float[] local) => new()
    {
        Name = name,
        ParentIndex = parent,
        LocalMatrix = local,
        InverseBindMatrix = TmmTestHelpers.Identity16Local(),
    };

    static void AssertMatrixEqual(Matrix4x4 a, Matrix4x4 b)
    {
        var fa = MatrixDecomp.MatrixToColMajor(a);
        var fb = MatrixDecomp.MatrixToColMajor(b);
        for (int i = 0; i < 16; i++)
            Assert.Equal(fa[i], fb[i], 1e-5f);
    }
}
