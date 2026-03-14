using System.Buffers.Binary;

namespace CryBar.TMM;

/// <summary>
/// Parses .tmm.data files containing vertex, index, skinning, and height buffers.
/// Requires section counts from the companion TmmFile.
/// </summary>
public class TmmDataFile
{
    public bool Parsed { get; }

    public TmmVertex[]? Vertices { get; private set; }
    public ushort[]? Indices { get; private set; }
    public TmmSkinWeight[]? SkinWeights { get; private set; }
    public Half[]? Heights { get; private set; }

    readonly uint _numVertices;
    readonly uint _numTriangleVerts;
    readonly bool _hasSkinning;

    /// <param name="data">Raw .tmm.data file bytes.</param>
    /// <param name="numVertices">Total vertex count from TmmFile.</param>
    /// <param name="numTriangleVerts">Total triangle index count from TmmFile.</param>
    /// <param name="hasSkinning">True if the model has bones (skinning buffer present).</param>
    public TmmDataFile(ReadOnlyMemory<byte> data, uint numVertices, uint numTriangleVerts, bool hasSkinning)
    {
        _numVertices = numVertices;
        _numTriangleVerts = numTriangleVerts;
        _hasSkinning = hasSkinning;
        Parsed = Parse(data.Span);
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        // Vertex buffer: 16 bytes per vertex
        var vertexByteSize = (int)_numVertices * TmmVertex.SizeInBytes;
        if (offset + vertexByteSize > data.Length) return false;

        var vertices = new TmmVertex[_numVertices];
        for (int i = 0; i < _numVertices; i++)
        {
            vertices[i] = new TmmVertex
            {
                PosX = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset, 2)),
                PosY = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 2, 2)),
                PosZ = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 4, 2)),
                U = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 6, 2)),
                V = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 8, 2)),
                TbnX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 10, 2)),
                TbnY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 12, 2)),
                TbnZ = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 14, 2))
            };
            offset += TmmVertex.SizeInBytes;
        }
        Vertices = vertices;

        // Index buffer: 2 bytes per index (u16)
        var indexByteSize = (int)_numTriangleVerts * 2;
        if (offset + indexByteSize > data.Length) return false;

        var indices = new ushort[_numTriangleVerts];
        for (int i = 0; i < _numTriangleVerts; i++)
        {
            indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;
        }
        Indices = indices;

        // Skinning buffer: 8 bytes per vertex (only present if model has bones)
        if (_hasSkinning)
        {
            var skinByteSize = (int)_numVertices * TmmSkinWeight.SizeInBytes;
            if (offset + skinByteSize > data.Length) return false;

            var skinWeights = new TmmSkinWeight[_numVertices];
            for (int i = 0; i < _numVertices; i++)
            {
                skinWeights[i] = new TmmSkinWeight
                {
                    Weight0 = data[offset],
                    Weight1 = data[offset + 1],
                    Weight2 = data[offset + 2],
                    Weight3 = data[offset + 3],
                    BoneIndex0 = data[offset + 4],
                    BoneIndex1 = data[offset + 5],
                    BoneIndex2 = data[offset + 6],
                    BoneIndex3 = data[offset + 7]
                };
                offset += TmmSkinWeight.SizeInBytes;
            }
            SkinWeights = skinWeights;
        }

        // Height buffer: 2 bytes per vertex (f16)
        var heightByteSize = (int)_numVertices * 2;
        if (offset + heightByteSize <= data.Length)
        {
            var heights = new Half[_numVertices];
            for (int i = 0; i < _numVertices; i++)
            {
                heights[i] = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset, 2));
                offset += 2;
            }
            Heights = heights;
        }

        return true;
    }

    /// <summary>
    /// Generates a human-readable summary of the data file contents.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(TMM data not parsed)";

        var lines = new List<string>
        {
            $"TMM Data File",
            $"Vertices: {Vertices?.Length ?? 0}",
            $"Indices: {Indices?.Length ?? 0} ({(Indices?.Length ?? 0) / 3} triangles)"
        };

        if (SkinWeights != null)
            lines.Add($"Skin weights: {SkinWeights.Length} entries");

        if (Heights != null)
            lines.Add($"Height values: {Heights.Length} entries");

        return string.Join("\n", lines);
    }
}
