namespace CryBar.Scenario;

public static class TerrainMeshBuilder
{
    public static TerrainMesh Build(ScenarioTerrain terrain, ScenarioTextureSet textures)
    {
        int mapX = terrain.MapSizeX;
        int mapZ = terrain.MapSizeZ;
        int vCols = mapX + 1;
        int vRows = mapZ + 1;
        int vertexCount = vCols * vRows;
        int tileCount = mapX * mapZ;

        var tileGroups = terrain.TileGroups;
        var tileSubs = terrain.TileSubs;
        var sliceMap = textures.SliceIndices;

        var slices = new int[tileCount];
        int validTiles = Math.Min(Math.Min(tileGroups.Length, tileSubs.Length), tileCount);
        for (int t = 0; t < validTiles; t++)
            slices[t] = sliceMap.TryGetValue((tileGroups[t], tileSubs[t]), out var s) ? s : -1;
        for (int t = validTiles; t < tileCount; t++)
            slices[t] = -1;

        var verts = new float[vertexCount * TerrainMesh.VertexStrideFloats];
        var heights = terrain.Heights;

        for (int vz = 0; vz < vRows; vz++)
        for (int vx = 0; vx < vCols; vx++)
        {
            int vIdx = vz * vCols + vx;
            int off = vIdx * TerrainMesh.VertexStrideFloats;

            float y = HeightAt(heights, vIdx);
            verts[off + 0] = vx;
            verts[off + 1] = y;
            verts[off + 2] = vz;
            // Per-vertex height-field slope (central difference, edge-clamped). Smooth-interpolating
            // these in the fragment shader gives a continuous normal across tile boundaries -- so
            // hills read as round mounds instead of faceted polygons.
            verts[off + 3] = SlopeAlong(heights, vCols, vRows, vx, vz, dx: 1, dz: 0);
            verts[off + 4] = SlopeAlong(heights, vCols, vRows, vx, vz, dx: 0, dz: 1);

            int sliceA = SliceAt(vx - 1, vz - 1, mapX, mapZ, slices);
            int sliceB = SliceAt(vx,     vz - 1, mapX, mapZ, slices);
            int sliceC = SliceAt(vx - 1, vz,     mapX, mapZ, slices);
            int sliceD = SliceAt(vx,     vz,     mapX, mapZ, slices);

            verts[off + 5] = sliceA;
            verts[off + 6] = sliceB;
            verts[off + 7] = sliceC;
            verts[off + 8] = sliceD;

            float wA = sliceA >= 0 ? 1f : 0f;
            float wB = sliceB >= 0 ? 1f : 0f;
            float wC = sliceC >= 0 ? 1f : 0f;
            float wD = sliceD >= 0 ? 1f : 0f;
            float sum = wA + wB + wC + wD;
            if (sum > 0)
            {
                wA /= sum; wB /= sum; wC /= sum; wD /= sum;
            }
            verts[off + 9]  = wA;
            verts[off + 10] = wB;
            verts[off + 11] = wC;
            // wD is implicit: 1 - (wA + wB + wC)
        }

        // Split each tile along the v00<->v11 diagonal and arrange both triangles
        // to end with v00. Under OpenGL's default GL_LAST_VERTEX_CONVENTION the
        // last vertex is the provoking vertex for flat-qualified varyings, so both
        // halves of a tile see the same vSlices array (= the 4 tiles meeting at
        // v00). This eliminates the diagonal in-tile hatch caused by the two halves
        // sampling different slice neighborhoods.
        var indices = new uint[tileCount * 6];
        int ii = 0;
        for (int tz = 0; tz < mapZ; tz++)
        for (int tx = 0; tx < mapX; tx++)
        {
            uint v00 = (uint)(tz * vCols + tx);
            uint v10 = v00 + 1;
            uint v01 = v00 + (uint)vCols;
            uint v11 = v01 + 1;

            indices[ii++] = v01;
            indices[ii++] = v11;
            indices[ii++] = v00;

            indices[ii++] = v11;
            indices[ii++] = v10;
            indices[ii++] = v00;
        }

        return new TerrainMesh
        {
            MapSizeX = mapX,
            MapSizeZ = mapZ,
            Vertices = verts,
            Indices = indices
        };
    }

    static int SliceAt(int tx, int tz, int mapX, int mapZ, int[] slices)
    {
        if ((uint)tx >= (uint)mapX || (uint)tz >= (uint)mapZ) return -1;
        return slices[tz * mapX + tx];
    }

    static float SlopeAlong(float[] heights, int vCols, int vRows, int vx, int vz, int dx, int dz)
    {
        int x0 = Math.Max(0, vx - dx);
        int z0 = Math.Max(0, vz - dz);
        int x1 = Math.Min(vCols - 1, vx + dx);
        int z1 = Math.Min(vRows - 1, vz + dz);
        int span = (x1 - x0) + (z1 - z0);
        if (span == 0) return 0f;

        float h0 = HeightAt(heights, z0 * vCols + x0);
        float h1 = HeightAt(heights, z1 * vCols + x1);
        return (h1 - h0) / span;
    }

    static float HeightAt(float[] heights, int idx)
    {
        return (uint)idx < (uint)heights.Length ? heights[idx] : 0f;
    }
}
