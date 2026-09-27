using System.Numerics;
using System.Runtime.InteropServices;
using CryBar.Export;
using CryBar.TMM;
using static CryBar.Tests.TmmTestHelpers;

namespace CryBar.Tests;

public class GlbReaderRobustnessTests
{
    // Adds a 3-vertex triangle; shortAttr (if set) gets only 2 elements to simulate a malformed export.
    static string AddTriangle(GlbTestBuilder b, string? shortAttr = null, bool skinned = false,
        float[]? normals = null, uint[]? indices = null)
    {
        int Count(string attr) => attr == shortAttr ? 2 : 3;

        int pos = b.Floats("VEC3", [1, 0, 0,  0, 1, 0,  0, 0, 1]);
        int nrm = b.Floats("VEC3", (normals ?? new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1 })[..(Count("NORMAL") * 3)]);
        int tan = b.Floats("VEC4", (new float[] { 1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1 })[..(Count("TANGENT") * 4)]);
        int uv = b.Floats("VEC2", (new float[] { 0, 0,  1, 0,  0, 1 })[..(Count("TEXCOORD_0") * 2)]);
        int idx = b.UInts(indices ?? new uint[] { 0, 1, 2 });

        var attrs = $"\"POSITION\":{pos},\"NORMAL\":{nrm},\"TANGENT\":{tan},\"TEXCOORD_0\":{uv}";
        if (skinned || shortAttr is "JOINTS_0" or "WEIGHTS_0")
        {
            int j = b.JointBytes((new byte[] { 0, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0 })[..(Count("JOINTS_0") * 4)]);
            int w = b.Floats("VEC4", (new float[] { 1, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0 })[..(Count("WEIGHTS_0") * 4)]);
            attrs += $",\"JOINTS_0\":{j},\"WEIGHTS_0\":{w}";
        }

        return $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{{attrs}}},\"indices\":{idx}}}]}}]";
    }

    [Theory]
    [InlineData("NORMAL")]
    [InlineData("TANGENT")]
    [InlineData("TEXCOORD_0")]
    [InlineData("JOINTS_0")]
    [InlineData("WEIGHTS_0")]
    public void Parse_AttributeCountMismatch_ThrowsClearError(string attr)
    {
        var b = new GlbTestBuilder();
        var meshes = AddTriangle(b, shortAttr: attr);
        var glb = b.Build($"\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}],{meshes}");

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains(attr, ex.Message);
        Assert.Contains("POSITION", ex.Message);
    }

    [Fact]
    public void Parse_IndexOutOfRange_ThrowsClearError()
    {
        var b = new GlbTestBuilder();
        var meshes = AddTriangle(b, indices: [0, 1, 5]);
        var glb = b.Build($"\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}],{meshes}");

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Parse_InterleavedStridedAccessors_ReadsEachElement()
    {
        float[] positions = [1, 0, 0,  0, 1, 0,  0, 0, 1];
        float[] normals = [0, 0, 1,  0, 1, 0,  1, 0, 0];
        float[] uvs = [0.1f, 0.2f,  0.3f, 0.4f,  0.5f, 0.6f];

        const int Stride = 32; // pos(12) + normal(12) + uv(8)
        var interleaved = new float[3 * Stride / 4];
        for (int v = 0; v < 3; v++)
        {
            int o = v * Stride / 4;
            Array.Copy(positions, v * 3, interleaved, o, 3);
            Array.Copy(normals, v * 3, interleaved, o + 3, 3);
            Array.Copy(uvs, v * 2, interleaved, o + 6, 2);
        }

        var b = new GlbTestBuilder();
        int view = b.View(MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray(), Stride);
        int pos = b.Accessor(view, 5126, 3, "VEC3", byteOffset: 0);
        int nrm = b.Accessor(view, 5126, 3, "VEC3", byteOffset: 12);
        int uv = b.Accessor(view, 5126, 3, "VEC2", byteOffset: 24);
        int tan = b.Floats("VEC4", [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1]);
        int idx = b.UInts(0, 1, 2);
        var glb = b.Build(
            $"\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}]," +
            $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos},\"NORMAL\":{nrm},\"TANGENT\":{tan},\"TEXCOORD_0\":{uv}}},\"indices\":{idx}}}]}}]");

        var prim = GlbReader.Parse(glb).Mesh.Primitives[0];

        Assert.Equal(positions, prim.Positions);
        Assert.Equal(normals, prim.Normals);
        Assert.Equal(uvs, prim.TexCoords);
    }

    [Fact]
    public void Parse_StrideSmallerThanElement_Throws()
    {
        var b = new GlbTestBuilder();
        int view = b.View(new byte[36], stride: 8);
        int pos = b.Accessor(view, 5126, 3, "VEC3");
        int nrm = b.Floats("VEC3", [0, 0, 1,  0, 0, 1,  0, 0, 1]);
        int uv = b.Floats("VEC2", [0, 0,  1, 0,  0, 1]);
        int idx = b.UInts(0, 1, 2);
        var glb = b.Build(
            $"\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}]," +
            $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{pos},\"NORMAL\":{nrm},\"TEXCOORD_0\":{uv}}},\"indices\":{idx}}}]}}]");

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains("byteStride", ex.Message);
    }

    [Fact]
    public void Parse_NegativeDeterminantRootTransform_FlipsWindingAndTangentW()
    {
        var b = new GlbTestBuilder();
        var meshes = AddTriangle(b, normals: [0, 0, 1,  0, 0, 0,  0, 0, 1]);
        var glb = b.Build(
            "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            "\"nodes\":[{\"name\":\"mirror\",\"scale\":[-1,1,1],\"children\":[1]},{\"mesh\":0}]," + meshes);

        var prim = GlbReader.Parse(glb).Mesh.Primitives[0];

        Assert.Equal(new uint[] { 0, 2, 1 }, prim.Indices);
        Assert.Equal(-1f, prim.Positions[0]);
        for (int v = 0; v < 3; v++)
            Assert.Equal(-1f, prim.Tangents[v * 4 + 3]);

        for (int v = 0; v < 3; v++)
        {
            var n = new Vector3(prim.Normals[v * 3], prim.Normals[v * 3 + 1], prim.Normals[v * 3 + 2]);
            Assert.True(float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z), $"normal {v} is not finite");
            Assert.Equal(1f, n.Length(), 1e-5f);
        }
    }

    [Fact]
    public void Parse_PositiveDeterminantRootTransform_KeepsWindingAndTangentW()
    {
        var b = new GlbTestBuilder();
        var meshes = AddTriangle(b);
        var glb = b.Build(
            "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            "\"nodes\":[{\"name\":\"scaled\",\"scale\":[2,2,2],\"children\":[1]},{\"mesh\":0}]," + meshes);

        var prim = GlbReader.Parse(glb).Mesh.Primitives[0];

        Assert.Equal(new uint[] { 0, 1, 2 }, prim.Indices);
        for (int v = 0; v < 3; v++)
            Assert.Equal(1f, prim.Tangents[v * 4 + 3]);
    }

    [Fact]
    public void Parse_BoneWithoutChannels_KeepsRestPoseThroughTmaWriter()
    {
        var restRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var animRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4);

        var b = new GlbTestBuilder();
        var meshes = AddTriangle(b, skinned: true);
        int times = b.Floats("SCALAR", [0f, 1f]);
        int rots = b.Floats("VEC4", [0, 0, 0, 1,  animRot.X, animRot.Y, animRot.Z, animRot.W]);
        var glb = b.Build(
            "\"scene\":0,\"scenes\":[{\"nodes\":[0,1]}]," +
            "\"nodes\":[" +
                "{\"mesh\":0,\"skin\":0}," +
                "{\"name\":\"root\",\"translation\":[0,1,0],\"children\":[2]}," +
                $"{{\"name\":\"child\",\"translation\":[0,0,2],\"rotation\":[{F(restRot.X)},{F(restRot.Y)},{F(restRot.Z)},{F(restRot.W)}],\"scale\":[1,2,1]}}" +
            "]," +
            "\"skins\":[{\"joints\":[1,2]}]," +
            $"\"animations\":[{{\"name\":\"nod\",\"samplers\":[{{\"input\":{times},\"output\":{rots},\"interpolation\":\"LINEAR\"}}]," +
            "\"channels\":[{\"sampler\":0,\"target\":{\"node\":1,\"path\":\"rotation\"}}]}]," + meshes);

        var model = GlbReader.Parse(glb);
        var anim = Assert.Single(model.Animations!);
        var root = anim.Tracks[0];
        var child = anim.Tracks[1];

        foreach (var t in root.Translations) AssertVec(new Vector3(0, 1, 0), t, 1e-5f);
        foreach (var t in child.Translations) AssertVec(new Vector3(0, 0, 2), t, 1e-5f);
        foreach (var r in child.Rotations) Assert.True(MathF.Abs(Quaternion.Dot(restRot, r)) > 0.99999f);
        Assert.Empty(child.Scales);

        var (tmaBytes, _) = TmaWriter.Write(anim, model.Bones!, null);
        var tma = new TmaFile(tmaBytes);
        Assert.True(tma.Parsed);

        var childTrack = tma.Tracks!.Single(t => t.Name == "child");
        Assert.Equal(TmaEncoding.Constant, childTrack.TranslationEncoding);
        Assert.Equal(TmaEncoding.Constant, childTrack.RotationEncoding);
        Assert.Equal(TmaEncoding.Constant, childTrack.ScaleEncoding);

        var decoded = TmaDecoder.DecodeTrack(childTrack);
        AssertVec(Vector3.Zero, decoded.Translations[0], 1e-4f);
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Identity, decoded.Rotations[0])) > 0.9999f);
        AssertVec(Vector3.One, decoded.Scales[0], 1e-4f);

        var rootDecoded = TmaDecoder.DecodeTrack(tma.Tracks!.Single(t => t.Name == "root"));
        foreach (var t in rootDecoded.Translations) AssertVec(Vector3.Zero, t, 1e-4f);
    }

    [Fact]
    public void ScaleChannel_TmaToGlbToTma_PreservesScale()
    {
        var tmm = CreateSyntheticTmmFile(numVertices: 3, numTriangleVerts: 3, hasSkinning: true,
            numMeshGroups: 1, materials: ["m"], submodels: ["default"], numBones: 2);
        var dataFile = new TmmDataFile(CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true), tmm);

        const int Frames = 5;
        var tracks = SyntheticTracksUniform(boneCount: 2, frameCount: Frames, duration: 1f);
        var expected = new Vector3[Frames];
        for (int f = 0; f < Frames; f++)
        {
            expected[f] = new Vector3(1 + 0.1f * f, 1f, 2 - 0.2f * f);
            tracks[1].Scales[f] = expected[f];
        }

        var anim = new GlbExporter.GlbAnimation { Name = "grow", Tracks = tracks, Duration = 1f, FrameCount = Frames };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, animations: [anim])!;

        var model = GlbReader.Parse(glb);
        var (tmaBytes, _) = TmaWriter.Write(model.Animations![0], model.Bones!, null);
        var tma = new TmaFile(tmaBytes);
        Assert.True(tma.Parsed);

        var animated = tma.Tracks!.Single(t => t.Name == "bone_1");
        Assert.Equal(TmaEncoding.Raw, animated.ScaleEncoding);
        var decoded = TmaDecoder.DecodeTrack(animated);
        Assert.Equal(Frames, decoded.Scales.Length);
        for (int f = 0; f < Frames; f++)
            AssertVec(expected[f], decoded.Scales[f], 1e-4f);

        var still = tma.Tracks!.Single(t => t.Name == "bone_0");
        Assert.Equal(TmaEncoding.Constant, still.ScaleEncoding);
        AssertVec(Vector3.One, TmaDecoder.DecodeTrack(still).Scales[0], 1e-5f);
    }

    static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    static void AssertVec(Vector3 expected, Vector3 actual, float tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }
}
