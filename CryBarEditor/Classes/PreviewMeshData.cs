using System;
using CryBar.TMM;

namespace CryBarEditor.Classes;

/// <summary>
/// CPU-side mesh data decoded from TMM, ready for GPU upload.
/// Vertices are interleaved: pos(3) + normal(3) + uv(2) = 8 floats/vertex, stride 32 bytes.
/// </summary>
public class PreviewMeshData
{
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required (int Offset, int Count)[] DrawGroups { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public float Radius { get; init; }
}

public static class MeshDataBuilder
{
    public static PreviewMeshData? BuildFromTmm(ReadOnlyMemory<byte> tmmBytes, ReadOnlyMemory<byte> tmmDataBytes)
    {
        var tmm = new TmmFile(tmmBytes);
        if (!tmm.Parsed) return null;

        var dataFile = new TmmDataFile(tmmDataBytes, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
        if (!dataFile.Parsed) return null;

        var srcVerts = dataFile.Vertices!;
        var srcIndices = dataFile.Indices!;
        var meshGroups = tmm.MeshGroups!;

        int vertexCount = srcVerts.Length;
        int indexCount = srcIndices.Length;

        // Interleaved: pos(3) + normal(3) + uv(2) = 8 floats per vertex
        var vertices = new float[vertexCount * 8];

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (int i = 0; i < vertexCount; i++)
        {
            var v = srcVerts[i];
            float px = (float)v.PosX;
            float py = (float)v.PosY;
            float pz = -(float)v.PosZ; // negate Z for LH→RH

            var (nx, ny, nz) = TbnDecoder.DecodeNormal(v.TbnX, v.TbnY, v.TbnZ);
            nz = -nz; // negate Z for LH→RH

            float u = (float)v.U;
            float vCoord = (float)v.V; // no flip - TMM top-left origin matches OpenGL

            int off = i * 8;
            vertices[off]     = px;
            vertices[off + 1] = py;
            vertices[off + 2] = pz;
            vertices[off + 3] = nx;
            vertices[off + 4] = ny;
            vertices[off + 5] = nz;
            vertices[off + 6] = u;
            vertices[off + 7] = vCoord;

            if (px < minX) minX = px;
            if (py < minY) minY = py;
            if (pz < minZ) minZ = pz;
            if (px > maxX) maxX = px;
            if (py > maxY) maxY = py;
            if (pz > maxZ) maxZ = pz;
        }

        // Reverse triangle winding: [i0,i1,i2] → [i0,i2,i1] for LH→RH
        // Add VertexStart offset per mesh group to make indices global
        var indices = new uint[indexCount];
        foreach (var mg in meshGroups)
        {
            uint vStart = mg.VertexStart;
            int iStart = (int)mg.IndexStart;
            int iEnd = iStart + (int)mg.IndexCount;
            for (int i = iStart; i + 2 < iEnd; i += 3)
            {
                indices[i]     = srcIndices[i] + vStart;
                indices[i + 1] = srcIndices[i + 2] + vStart;
                indices[i + 2] = srcIndices[i + 1] + vStart;
            }
        }

        // Bounding sphere from AABB
        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        float cz = (minZ + maxZ) * 0.5f;
        float dx = maxX - minX;
        float dy = maxY - minY;
        float dz = maxZ - minZ;
        float radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f;

        // Draw groups from mesh groups
        var drawGroups = new (int Offset, int Count)[meshGroups.Length];
        for (int i = 0; i < meshGroups.Length; i++)
        {
            drawGroups[i] = ((int)meshGroups[i].IndexStart, (int)meshGroups[i].IndexCount);
        }

        return new PreviewMeshData
        {
            Vertices = vertices,
            Indices = indices,
            DrawGroups = drawGroups,
            CenterX = cx,
            CenterY = cy,
            CenterZ = cz,
            Radius = radius
        };
    }
}
