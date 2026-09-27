using CryBar.Scenario;

namespace CryBar.Tests;

public class TerrainMeshBuilderTests
{
    [Fact]
    public void Build_VertexCountMatchesGrid()
    {
        var terrain = MakeFlat(2, 2);
        var set = ScenarioTextureSet.Build(terrain);

        var mesh = TerrainMeshBuilder.Build(terrain, set);

        Assert.Equal(9 * TerrainMesh.VertexStrideFloats, mesh.Vertices.Length);
        Assert.Equal(24, mesh.Indices.Length);
        Assert.Equal(2, mesh.MapSizeX);
        Assert.Equal(2, mesh.MapSizeZ);
    }

    [Fact]
    public void Build_VertexPositionsMatchHeights()
    {
        var terrain = MakeFlat(2, 2);
        terrain.Heights[1 * 3 + 1] = 5.0f;

        var mesh = TerrainMeshBuilder.Build(terrain, ScenarioTextureSet.Build(terrain));

        Assert.Equal(5.0f, mesh.Vertices[4 * TerrainMesh.VertexStrideFloats + 1]);
        Assert.Equal(0.0f, mesh.Vertices[0 * TerrainMesh.VertexStrideFloats + 1]);
    }

    [Fact]
    public void Build_BlendWeightsSumToOne()
    {
        var terrain = MakeFlat(2, 2);
        var mesh = TerrainMeshBuilder.Build(terrain, ScenarioTextureSet.Build(terrain));

        int vIdx = 1 * 3 + 1;
        int baseOff = vIdx * TerrainMesh.VertexStrideFloats;
        float wA = mesh.Vertices[baseOff + 9];
        float wB = mesh.Vertices[baseOff + 10];
        float wC = mesh.Vertices[baseOff + 11];
        float wD = 1f - (wA + wB + wC);

        Assert.True(wA >= 0); Assert.True(wB >= 0); Assert.True(wC >= 0); Assert.True(wD >= 0);
        Assert.Equal(1.0f, wA + wB + wC + wD, precision: 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public void Build_HeightsShorterThanGrid_DoesNotThrow(int heightCount)
    {
        var terrain = MakeFlat(2, 2, heightCount);

        var mesh = TerrainMeshBuilder.Build(terrain, ScenarioTextureSet.Build(terrain));

        Assert.Equal(9 * TerrainMesh.VertexStrideFloats, mesh.Vertices.Length);
        Assert.Equal(0f, mesh.Vertices[8 * TerrainMesh.VertexStrideFloats + 1]);
    }

    static ScenarioTerrain MakeFlat(int mapX, int mapZ, int? heightCount = null)
    {
        int vCount = (mapX + 1) * (mapZ + 1);
        int tCount = mapX * mapZ;
        return new ScenarioTerrain
        {
            MapSizeX = mapX, MapSizeZ = mapZ,
            Heights = new float[heightCount ?? vCount],
            WaterHeights = new float[vCount],
            UnkHeights = new float[vCount],
            TileGroups = new byte[tCount],
            TileSubs = new ushort[tCount],
            TilePt = new byte[tCount],
            WaterType = new byte[tCount],
            TerrainGroups = [new TerrainTextureGroup { Name = "G0", Textures = ["a"] }]
        };
    }
}
