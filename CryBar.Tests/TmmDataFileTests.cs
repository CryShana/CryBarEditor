using System.Buffers.Binary;

using CryBar.TMM;

namespace CryBar.Tests;

public class TmmDataFileTests
{
    [Fact]
    public void Parse_EmptyData_ReturnsFalse()
    {
        var dataFile = new TmmDataFile(ReadOnlyMemory<byte>.Empty, 1, 3, false);
        Assert.False(dataFile.Parse());
    }

    [Fact]
    public void Parse_SingleVertexNoSkinning()
    {
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 3, hasSkinning: false);
        var dataFile = new TmmDataFile(data, 1, 3, false);
        Assert.True(dataFile.Parse());
        Assert.NotNull(dataFile.Vertices);
        Assert.Single(dataFile.Vertices);
        Assert.NotNull(dataFile.Indices);
        Assert.Equal(3, dataFile.Indices.Length);
        Assert.Null(dataFile.SkinWeights);
    }

    [Fact]
    public void Parse_WithSkinning()
    {
        var data = CreateSyntheticData(numVertices: 2, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(data, 2, 3, true);
        Assert.True(dataFile.Parse());
        Assert.NotNull(dataFile.SkinWeights);
        Assert.Equal(2, dataFile.SkinWeights.Length);
        Assert.Equal(200, dataFile.SkinWeights[0].Weight0);
        Assert.Equal(55, dataFile.SkinWeights[0].Weight1);
    }

    [Fact]
    public void Parse_VertexPositions_F16()
    {
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false);
        var dataFile = new TmmDataFile(data, 1, 0, false);
        Assert.True(dataFile.Parse());

        var v = dataFile.Vertices![0];
        // We wrote Half(1.5f) for PosX
        Assert.Equal((Half)1.5f, v.PosX);
        Assert.Equal((Half)2.5f, v.PosY);
        Assert.Equal((Half)(-0.5f), v.PosZ);
    }

    [Fact]
    public void Parse_Heights()
    {
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false, includeHeights: true);
        var dataFile = new TmmDataFile(data, 1, 0, false);
        Assert.True(dataFile.Parse());
        Assert.NotNull(dataFile.Heights);
        Assert.Single(dataFile.Heights);
    }

    [Fact]
    public void Parse_MismatchedCounts_ReturnsFalse()
    {
        // Claim 10 vertices but provide data for only 1
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false);
        var dataFile = new TmmDataFile(data, 10, 0, false);
        Assert.False(dataFile.Parse());
    }

    [Fact]
    public void GetSummary_Parsed()
    {
        var data = CreateSyntheticData(numVertices: 2, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(data, 2, 3, true);
        Assert.True(dataFile.Parse());

        var summary = dataFile.GetSummary();
        Assert.Contains("Vertices: 2", summary);
        Assert.Contains("1 triangles", summary);
        Assert.Contains("Skin weights", summary);
    }

    #region Helper Methods

    static byte[] CreateSyntheticData(uint numVertices, uint numTriangleVerts, bool hasSkinning, bool includeHeights = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Vertex buffer: 16 bytes per vertex
        for (int i = 0; i < numVertices; i++)
        {
            w.Write((Half)1.5f);   // PosX
            w.Write((Half)2.5f);   // PosY
            w.Write((Half)(-0.5f)); // PosZ
            w.Write((Half)0.5f);   // U
            w.Write((Half)0.25f);  // V
            w.Write((ushort)16384); // TbnX
            w.Write((ushort)16384); // TbnY
            w.Write((ushort)16384); // TbnZ
        }

        // Index buffer: 2 bytes per index
        for (int i = 0; i < numTriangleVerts; i++)
            w.Write((ushort)(i % numVertices));

        // Skinning buffer
        if (hasSkinning)
        {
            for (int i = 0; i < numVertices; i++)
            {
                w.Write((byte)200); // weight0
                w.Write((byte)55);  // weight1
                w.Write((byte)0);   // weight2
                w.Write((byte)0);   // weight3
                w.Write((byte)0);   // boneIndex0
                w.Write((byte)1);   // boneIndex1
                w.Write((byte)0);   // boneIndex2
                w.Write((byte)0);   // boneIndex3
            }
        }

        // Height buffer
        if (includeHeights)
        {
            for (int i = 0; i < numVertices; i++)
                w.Write((Half)1.0f);
        }

        return ms.ToArray();
    }

    #endregion
}
