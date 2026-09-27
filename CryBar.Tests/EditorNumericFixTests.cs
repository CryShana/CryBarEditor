using System;
using System.Buffers.Binary;
using System.IO;
using CryBar.TMM;
using CryBarEditor.Classes;

namespace CryBar.Tests;

public class EditorNumericFixTests
{
    static byte[] BuildWav(short channels, short[] samples, int? dataSizeOverride = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(44100);
        w.Write(44100 * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);

        w.Write("data"u8);
        w.Write(dataSizeOverride ?? dataBytes);
        foreach (var s in samples) w.Write(s);

        w.Flush();
        return ms.ToArray();
    }

    static short[] ReadSamples(byte[] wav)
    {
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4));
        var samples = new short[dataSize / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2));
        return samples;
    }

    static string WriteTempWav(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"crybar_test_{Guid.NewGuid()}.wav");
        File.WriteAllBytes(path, wav);
        return path;
    }

    [Fact]
    public void TrimSilence_MinValueSample_DoesNotThrowAndTrims()
    {
        var path = WriteTempWav(BuildWav(1, [0, 0, short.MinValue, 1000, 0, 0]));
        try
        {
            FMODEvent.TrimSilence(path);

            Assert.Equal(new short[] { short.MinValue, 1000 }, ReadSamples(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrimSilence_MinValueInSecondChannel_KeepsFrame()
    {
        var path = WriteTempWav(BuildWav(2, [0, 0, 0, short.MinValue, 0, 0]));
        try
        {
            FMODEvent.TrimSilence(path);

            Assert.Equal(new short[] { 0, short.MinValue }, ReadSamples(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-2)]
    public void TrimSilence_NonPositiveChannels_LeavesFileUnchanged(short channels)
    {
        var original = BuildWav(channels, [0, 0, 500, 0, 0]);
        var path = WriteTempWav(original);
        try
        {
            FMODEvent.TrimSilence(path);

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrimSilence_OversizedDataChunk_ClampsToFileLength()
    {
        var path = WriteTempWav(BuildWav(1, [0, 500, 0], dataSizeOverride: int.MaxValue));
        try
        {
            FMODEvent.TrimSilence(path);

            Assert.Equal(new short[] { 500 }, ReadSamples(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OrbitCamera_ZoomHugeDelta_StaysAboveMinAndFinite()
    {
        var cam = new OrbitCamera { Distance = 5f };

        cam.Zoom(1e6f);

        Assert.True(float.IsFinite(cam.Distance));
        Assert.True(cam.Distance > OrbitCamera.MinDistance);
    }

    [Fact]
    public void OrbitCamera_RepeatedZoomOut_IsCappedBelowFarPlane()
    {
        var cam = new OrbitCamera { Distance = 5f };

        for (int i = 0; i < 1000; i++) cam.Zoom(-1e6f);

        Assert.Equal(OrbitCamera.MaxDistance, cam.Distance);
        Assert.True(cam.Distance < OrbitCamera.FarPlane);
    }

    [Fact]
    public void OrbitCamera_ZoomNonFiniteDelta_LeavesDistance()
    {
        var cam = new OrbitCamera { Distance = 5f };

        cam.Zoom(float.NaN);
        cam.Zoom(float.PositiveInfinity);

        Assert.Equal(5f, cam.Distance);
    }

    [Fact]
    public void OrbitCamera_NormalZoom_UnchangedBehavior()
    {
        var cam = new OrbitCamera { Distance = 10f };

        cam.Zoom(1f);

        Assert.Equal(9f, cam.Distance, 4);
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(-3f)]
    public void OrbitCamera_FitToSphereBadRadius_UsesDefaultDistance(float radius)
    {
        var cam = new OrbitCamera();

        cam.FitToSphere(0f, 0f, 0f, radius);

        Assert.Equal(5f, cam.Distance);
    }

    [Fact]
    public void OrbitCamera_FitToSphereNonFiniteCenter_TargetStaysFinite()
    {
        var cam = new OrbitCamera();

        cam.FitToSphere(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2f);

        Assert.Equal(0f, cam.TargetX);
        Assert.Equal(0f, cam.TargetY);
        Assert.Equal(0f, cam.TargetZ);
        Assert.True(float.IsFinite(cam.GetViewMatrix().M11));
    }

    [Fact]
    public void OrbitCamera_FitToSphereHugeRadius_ClampedToMax()
    {
        var cam = new OrbitCamera();

        cam.FitToSphere(0f, 0f, 0f, 1e30f);

        Assert.Equal(OrbitCamera.MaxDistance, cam.Distance);
    }

    const int MeshGroupSize = 24;
    const string MatName = "mat";
    const string SubName = "sub";

    static (byte[] Tmm, byte[] Data) BuildModel(uint numVerts, uint numTris, uint numGroups)
    {
        uint vertByteLen = numVerts * (uint)TmmVertex.SizeInBytes;
        var tmm = TmmTestHelpers.CreateSyntheticTmm(
            numMeshGroups: numGroups,
            materials: [MatName],
            submodels: [SubName],
            numVertices: numVerts, numTriangleVerts: numTris,
            verticesStart: 0, verticesByteLength: vertByteLen,
            trianglesStart: vertByteLen, trianglesByteLength: numTris * 2);
        var data = TmmTestHelpers.CreateSyntheticData(numVerts, numTris, hasSkinning: false);
        return (tmm, data);
    }

    // Mesh groups are followed only by the material and submodel strings (no bones in these models).
    static void PatchGroupField(byte[] tmm, int fieldIndex, uint value)
    {
        int tail = (4 + MatName.Length * 2) + (4 + SubName.Length * 2);
        int groupOffset = tmm.Length - tail - MeshGroupSize;
        BinaryPrimitives.WriteUInt32LittleEndian(tmm.AsSpan(groupOffset + fieldIndex * 4, 4), value);
    }

    [Fact]
    public void MeshDataBuilder_EmptyMesh_HasFiniteBounds()
    {
        var (tmm, data) = BuildModel(0, 0, 0);

        var mesh = MeshDataBuilder.BuildFromTmm(tmm, data);

        Assert.NotNull(mesh);
        Assert.Empty(mesh!.Vertices);
        Assert.Equal(1f, mesh.Radius);
        Assert.Equal(0f, mesh.CenterX);
        Assert.Equal(0f, mesh.CenterY);
        Assert.Equal(0f, mesh.CenterZ);
    }

    [Fact]
    public void MeshDataBuilder_IndexStartOutOfRange_GroupDrawsNothing()
    {
        var (tmm, data) = BuildModel(3, 3, 1);
        PatchGroupField(tmm, 1, 300);
        Assert.Equal(300u, new TmmFile(tmm).MeshGroups![0].IndexStart);

        var mesh = MeshDataBuilder.BuildFromTmm(tmm, data);

        Assert.NotNull(mesh);
        Assert.Single(mesh!.DrawGroups);
        Assert.Equal(0, mesh.DrawGroups[0].Count);
    }

    [Fact]
    public void MeshDataBuilder_IndexCountOverflow_ClipsToAvailableIndices()
    {
        var (tmm, data) = BuildModel(3, 3, 1);
        PatchGroupField(tmm, 3, uint.MaxValue);
        Assert.Equal(uint.MaxValue, new TmmFile(tmm).MeshGroups![0].IndexCount);

        var mesh = MeshDataBuilder.BuildFromTmm(tmm, data);

        Assert.NotNull(mesh);
        Assert.Equal((0, 3), mesh!.DrawGroups[0]);
    }

    [Fact]
    public void MeshDataBuilder_VertexStartOutOfRange_IndicesStayInBounds()
    {
        var (tmm, data) = BuildModel(3, 3, 1);
        PatchGroupField(tmm, 0, 1000);
        Assert.Equal(1000u, new TmmFile(tmm).MeshGroups![0].VertexStart);

        var mesh = MeshDataBuilder.BuildFromTmm(tmm, data);

        Assert.NotNull(mesh);
        int vertexCount = mesh!.Vertices.Length / PreviewMeshData.VertexStrideFloats;
        Assert.All(mesh.Indices, i => Assert.True(i < vertexCount));
    }

    [Fact]
    public void MeshDataBuilder_ValidModel_KeepsGroupAndWinding()
    {
        var (tmm, data) = BuildModel(3, 3, 1);

        var mesh = MeshDataBuilder.BuildFromTmm(tmm, data);

        Assert.NotNull(mesh);
        Assert.Equal((0, 3), mesh!.DrawGroups[0]);
        Assert.Equal(new uint[] { 0, 2, 1 }, mesh.Indices);
    }
}
