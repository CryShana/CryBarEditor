using CryBar.TMM;

using static CryBar.Tests.TmmTestHelpers;

namespace CryBar.Tests;

public class TmmDataFileTests
{
    [Fact]
    public void Parse_EmptyData_ReturnsFalse()
    {
        var tmm = CreateSyntheticTmmFile(1, 3, false);
        var dataFile = new TmmDataFile(ReadOnlyMemory<byte>.Empty, tmm);
        Assert.False(dataFile.Parsed);
    }

    [Fact]
    public void Parse_SingleVertexNoSkinning()
    {
        var tmm = CreateSyntheticTmmFile(1, 3, false);
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 3, hasSkinning: false);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.True(dataFile.Parsed);
        Assert.NotNull(dataFile.Vertices);
        Assert.Single(dataFile.Vertices);
        Assert.NotNull(dataFile.Indices);
        Assert.Equal(3, dataFile.Indices.Length);
        Assert.Null(dataFile.SkinWeights);
    }

    [Fact]
    public void Parse_WithSkinning()
    {
        var tmm = CreateSyntheticTmmFile(2, 3, true);
        var data = CreateSyntheticData(numVertices: 2, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.True(dataFile.Parsed);
        Assert.NotNull(dataFile.SkinWeights);
        Assert.Equal(2, dataFile.SkinWeights.Length);
        Assert.Equal(200, dataFile.SkinWeights[0].Weight0);
        Assert.Equal(55, dataFile.SkinWeights[0].Weight1);
    }

    [Fact]
    public void Parse_VertexPositions_F16()
    {
        var tmm = CreateSyntheticTmmFile(1, 0, false);
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.True(dataFile.Parsed);

        var v = dataFile.Vertices![0];
        // We wrote Half(1.5f) for PosX
        Assert.Equal((Half)1.5f, v.PosX);
        Assert.Equal((Half)2.5f, v.PosY);
        Assert.Equal((Half)(-0.5f), v.PosZ);
    }

    [Fact]
    public void Parse_Heights()
    {
        var tmm = CreateSyntheticTmmFile(1, 0, false, includeHeights: true);
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false, includeHeights: true);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.True(dataFile.Parsed);
        Assert.NotNull(dataFile.Heights);
        Assert.Single(dataFile.Heights);
    }

    [Fact]
    public void Parse_MismatchedCounts_ReturnsFalse()
    {
        // Claim 10 vertices but provide data for only 1
        var tmm = CreateSyntheticTmmFile(10, 0, false);
        var data = CreateSyntheticData(numVertices: 1, numTriangleVerts: 0, hasSkinning: false);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.False(dataFile.Parsed);
    }

    [Fact]
    public void GetSummary_Parsed()
    {
        var tmm = CreateSyntheticTmmFile(2, 3, true);
        var data = CreateSyntheticData(numVertices: 2, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(data, tmm);
        Assert.True(dataFile.Parsed);

        var summary = dataFile.GetSummary();
        Assert.Contains("Vertices: 2", summary);
        Assert.Contains("1 triangles", summary);
        Assert.Contains("Skin weights", summary);
    }

}
